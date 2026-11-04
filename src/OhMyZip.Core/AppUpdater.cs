using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OhMyZip.Core;

public enum UpdateCheckStatus { UpToDate, Available, Unavailable }

/// <summary>GitHub Releases 의 한 릴리스. PackageUrl·ChecksumUrl 은 자산의 browser_download_url.</summary>
public sealed record UpdateRelease(Version Version, string Tag, string Notes, Uri PackageUrl, Uri ChecksumUrl, Uri PageUrl);

/// <summary>업데이트 확인 결과. Release 는 Available 일 때만 있다. Message 는 그대로 보여 줄 수 있는 문장.</summary>
public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateRelease? Release, string Message);

/// <summary>
/// GitHub Releases 에서 최신판을 확인·내려받기·검증하고 설치 폴더의 파일을 바꾼다.
/// 설치는 기존 파일을 "&lt;이름&gt;.ohmyzip-old" 로 이름만 바꾼 뒤 새 파일을 복사하므로 실행 중인 exe/dll 도 바꿀 수 있다.
/// 남은 옛 파일은 다음 시작 때 <see cref="CleanupLeftovers"/> 가 지운다.
/// </summary>
public sealed class AppUpdater
{
    public const string Repository = "woori-doweb/ohmyzip";

    public static readonly Uri LatestReleaseApi = new($"https://api.github.com/repos/{Repository}/releases/latest");

    internal const string MainExecutable = "ohmyZip.exe";
    internal const string LeftoverSuffix = ".ohmyzip-old";
    private const int MaxChecksumBytes = 4096;

    private static readonly Regex TagPattern = new(@"^v([0-9]{1,9})\.([0-9]{1,9})\.([0-9]{1,9})$", RegexOptions.CultureInvariant);
    private static readonly Regex ChecksumPattern = new(@"^([0-9a-fA-F]{64}) [ *]([^\r\n]+)$", RegexOptions.CultureInvariant);

    private readonly HttpClient http;
    private readonly string installRoot;
    private readonly string workRoot;

    /// <summary>browser_download_url → 자산의 API url. 인증된 HttpClient 는 비공개 저장소 자산을 API url 로만 받을 수 있다.</summary>
    private readonly ConcurrentDictionary<Uri, Uri> assetApiUrls = new();

    private string userAgentVersion = (typeof(AppUpdater).Assembly.GetName().Version ?? new Version(0, 0, 0)).ToString(3);

    public AppUpdater(HttpClient http, string installDir, string workDir)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrEmpty(installDir);
        ArgumentException.ThrowIfNullOrEmpty(workDir);
        this.http = http;
        installRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDir));
        workRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workDir));
    }

    /// <summary>최신 릴리스를 확인한다. 네트워크·서버·형식 문제는 예외가 아니라 Unavailable 로 돌려준다(ct 취소만 예외).</summary>
    public async Task<UpdateCheckResult> CheckAsync(Version current, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(current);
        Version installed = ThreePart(current);
        userAgentVersion = installed.ToString(3);

        string json;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            AddUserAgent(request);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return Unavailable("아직 받을 수 있는 업데이트가 없습니다. 나중에 다시 확인하세요.");
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                return Unavailable("업데이트 서버가 지금은 요청을 받지 않습니다. 잠시 뒤 다시 확인하세요.");
            if (!response.IsSuccessStatusCode)
                return Unavailable($"업데이트 정보를 받지 못했습니다(HTTP {(int)response.StatusCode}). 잠시 뒤 다시 확인하세요.");
            json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return Unavailable("업데이트 서버에 연결하지 못했습니다. 인터넷 연결을 확인한 뒤 다시 확인하세요.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Unavailable("업데이트 서버의 응답이 너무 늦습니다. 인터넷 연결을 확인한 뒤 다시 확인하세요.");
        }
        catch (IOException)
        {
            return Unavailable("업데이트 정보를 받는 중에 연결이 끊겼습니다. 인터넷 연결을 확인한 뒤 다시 확인하세요.");
        }

        UpdateRelease? release;
        string? problem;
        try
        {
            (release, problem) = ParseRelease(json);
        }
        catch (JsonException)
        {
            (release, problem) = (null, "업데이트 정보를 읽지 못했습니다. 잠시 뒤 다시 확인하세요.");
        }
        if (release is null)
            return Unavailable(problem!);

        return release.Version > installed
            ? new UpdateCheckResult(UpdateCheckStatus.Available, release, $"새 버전을 받을 수 있습니다: {release.Version.ToString(3)} (지금 {installed.ToString(3)}).")
            : new UpdateCheckResult(UpdateCheckStatus.UpToDate, null, $"최신 버전을 쓰고 있습니다({installed.ToString(3)}).");
    }

    /// <summary>
    /// 설치 파일을 내려받아 SHA-256 을 대조하고 안전하게 푼 뒤, 최상위 폴더를 벗긴 준비 폴더(workDir\staged-버전)를 돌려준다.
    /// 받기·풀기는 고유한 임시 폴더에서 하고 검증이 끝난 뒤에만 준비 폴더로 옮긴다. 실패하면 임시 폴더를 지우고 IOException.
    /// </summary>
    public async Task<string> DownloadAsync(UpdateRelease release, IProgress<(long Done, long? Total)>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(release);
        string version = ThreePart(release.Version).ToString(3);
        string assetName = PackageName(version);
        string staged = Path.Join(workRoot, "staged-" + version);
        if (File.Exists(Path.Join(staged, MainExecutable)))
            return staged;

        string temp = Path.Join(workRoot, $"download-{version}-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(temp);
            string expected = await ReadChecksumAsync(release.ChecksumUrl, assetName, ct).ConfigureAwait(false);
            string package = Path.Join(temp, assetName);
            string actual = await DownloadFileAsync(release.PackageUrl, package, progress, ct).ConfigureAwait(false);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new UpdateFailure("내려받은 업데이트 파일이 손상되었습니다(SHA-256 불일치). 다시 받아 보세요.");

            string files = Path.Join(temp, "files");
            ExtractPackage(package, files, ct);

            if (Directory.Exists(staged) && !File.Exists(Path.Join(staged, MainExecutable)))
                TryDeleteDirectory(staged);
            try
            {
                Directory.Move(files, staged);
            }
            catch (IOException) when (File.Exists(Path.Join(staged, MainExecutable)))
            {
                // 다른 창이 같은 버전을 먼저 준비했다. 그 결과도 같은 검증을 거쳤으므로 그대로 쓴다.
            }
            return staged;
        }
        catch (Exception ex) when (ex is not (UpdateFailure or OperationCanceledException))
        {
            throw new UpdateFailure("업데이트를 내려받지 못했습니다. 인터넷 연결과 디스크 공간을 확인한 뒤 다시 시도하세요.", ex);
        }
        finally
        {
            TryDeleteDirectory(temp);
        }
    }

    /// <summary>
    /// 준비 폴더의 파일(하위 폴더 포함)로 설치 폴더의 파일을 바꾼다. 기존 파일은 "&lt;이름&gt;.ohmyzip-old" 로 남긴다.
    /// 중간에 실패하면 이미 바꾼 파일과 새로 만든 폴더를 모두 되돌리고 IOException.
    /// </summary>
    public void Apply(string stagedDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(stagedDir);
        string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagedDir));
        if (!File.Exists(Path.Join(source, MainExecutable)))
            throw new UpdateFailure("설치할 업데이트 파일이 없습니다. 업데이트를 다시 받아 주세요.");
        if (!Directory.Exists(installRoot))
            throw new UpdateFailure("ohmyZip 설치 폴더를 찾지 못했습니다. ohmyZip 을 다시 설치한 뒤 업데이트하세요.");

        string[] files = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        var replaced = new List<(string Target, string? Backup)>();
        var createdDirectories = new List<string>();
        try
        {
            foreach (string file in files)
            {
                string relative = Path.GetRelativePath(source, file);
                string target = SafePaths.ResolveUnder(installRoot, relative)
                    ?? throw new UpdateFailure($"업데이트 파일 '{relative}' 가 설치 폴더 밖을 가리킵니다. 업데이트를 다시 받아 주세요.");
                if (Directory.Exists(target))
                    throw new UpdateFailure($"설치 폴더의 '{relative}' 가 폴더라서 파일로 바꿀 수 없습니다. 그 폴더를 옮긴 뒤 다시 시도하세요.");

                CreateMissingDirectories(Path.GetDirectoryName(target)!, createdDirectories);
                string? backup = null;
                if (File.Exists(target))
                {
                    backup = FreeLeftoverPath(target);
                    File.Move(target, backup);
                }
                replaced.Add((target, backup));
                File.Copy(file, target);
            }
        }
        catch (Exception ex)
        {
            bool restored = Rollback(replaced, createdDirectories);
            if (!restored)
                throw new UpdateFailure("업데이트를 설치하다 멈췄고 일부 파일을 되돌리지 못했습니다. ohmyZip 을 새로 내려받아 설치 폴더에 다시 풀어 주세요.", ex);
            if (ex is UpdateFailure)
                throw new UpdateFailure($"업데이트를 설치하지 못해 원래 파일로 되돌렸습니다. {ex.Message}", ex);
            if (ex is UnauthorizedAccessException)
                throw new UpdateFailure("설치 폴더에 쓸 권한이 없어 업데이트를 설치하지 못했습니다(원래 파일로 되돌림). 쓰기 권한이 있는 폴더로 ohmyZip 을 옮기거나 관리자 권한으로 실행한 뒤 다시 시도하세요.", ex);
            throw new UpdateFailure("업데이트를 설치하지 못해 원래 파일로 되돌렸습니다. 다른 프로그램이 ohmyZip 파일을 쓰고 있지 않은지 확인한 뒤 다시 시도하세요.", ex);
        }
    }

    /// <summary>설치 폴더 아래(하위 폴더 포함) 지난 업데이트의 "*.ohmyzip-old*" 만 지운다. 지울 수 없는 것은 조용히 넘어간다.</summary>
    public static void CleanupLeftovers(string installDir)
    {
        try
        {
            if (string.IsNullOrEmpty(installDir) || !Directory.Exists(installDir))
                return;
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                MatchType = MatchType.Simple,
                MatchCasing = MatchCasing.CaseInsensitive,
            };
            foreach (string file in Directory.EnumerateFiles(installDir, "*" + LeftoverSuffix + "*", options))
            {
                if (IsLeftoverName(Path.GetFileName(file)))
                    TryDeleteFile(file);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>"이름.ohmyzip-old" 또는 "이름.ohmyzip-old-접미사" 인지.</summary>
    internal static bool IsLeftoverName(string name)
    {
        int i = name.LastIndexOf(LeftoverSuffix, StringComparison.OrdinalIgnoreCase);
        if (i <= 0)
            return false;
        int end = i + LeftoverSuffix.Length;
        return end == name.Length || name[end] == '-';
    }

    internal static string PackageName(string version) => $"ohmyZip-{version}-win-x64.zip";

    private static Version ThreePart(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    private static UpdateCheckResult Unavailable(string message) => new(UpdateCheckStatus.Unavailable, null, message);

    private void AddUserAgent(HttpRequestMessage request) =>
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ohmyZip", userAgentVersion));

    /// <summary>릴리스 JSON 을 읽는다. 쓸 수 없으면 (null, 문구).</summary>
    private (UpdateRelease? Release, string? Problem) ParseRelease(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return (null, "업데이트 정보를 읽지 못했습니다. 잠시 뒤 다시 확인하세요.");
        if (IsTrue(root, "draft") || IsTrue(root, "prerelease"))
            return (null, "아직 받을 수 있는 업데이트가 없습니다. 나중에 다시 확인하세요.");

        string? tag = StringProperty(root, "tag_name");
        Match m = TagPattern.Match(tag ?? "");
        if (tag is null || !m.Success)
            return (null, "최신 릴리스의 버전 표시를 읽지 못했습니다. 잠시 뒤 다시 확인하세요.");
        var version = new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
        string versionText = version.ToString(3);

        string packageName = PackageName(versionText);
        string checksumName = packageName + ".sha256";
        (Uri Browser, Uri? Api)? package = null;
        (Uri Browser, Uri? Api)? checksum = null;
        if (root.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object)
                    continue;
                string? state = StringProperty(asset, "state");
                if (state is not null && state != "uploaded")
                    continue;
                string? name = StringProperty(asset, "name");
                Uri? browser = HttpsUri(StringProperty(asset, "browser_download_url"));
                if (name is null || browser is null)
                    continue;
                Uri? api = HttpsUri(StringProperty(asset, "url"));
                if (name == packageName)
                    package = (browser, api);
                else if (name == checksumName)
                    checksum = (browser, api);
            }
        }
        if (package is null || checksum is null)
            return (null, $"최신 릴리스 {tag} 에 설치 파일이 아직 없습니다. 잠시 뒤 다시 확인하세요.");

        foreach (var (browser, api) in new[] { package.Value, checksum.Value })
        {
            if (api is not null)
                assetApiUrls[browser] = api;
        }

        Uri page = HttpsUri(StringProperty(root, "html_url")) ?? new Uri($"https://github.com/{Repository}/releases/tag/{tag}");
        string notes = StringProperty(root, "body") ?? "";
        return (new UpdateRelease(version, tag, notes, package.Value.Browser, checksum.Value.Browser, page), null);
    }

    private static string? StringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsTrue(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    private static Uri? HttpsUri(string? text) =>
        Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null;

    /// <summary>자산 하나를 요청한다. 인증된 HttpClient 이고 API url 을 알면 API url 로 원본 바이트를 받는다.</summary>
    private async Task<HttpResponseMessage> GetAssetAsync(Uri browserUrl, CancellationToken ct)
    {
        Uri? apiUrl = null;
        bool useApi = http.DefaultRequestHeaders.Authorization is not null && assetApiUrls.TryGetValue(browserUrl, out apiUrl);
        using var request = new HttpRequestMessage(HttpMethod.Get, useApi ? apiUrl : browserUrl);
        AddUserAgent(request);
        if (useApi)
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateFailure("업데이트 서버에 연결하지 못했습니다. 인터넷 연결을 확인한 뒤 다시 시도하세요.", ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new UpdateFailure("업데이트 서버의 응답이 너무 늦습니다. 인터넷 연결을 확인한 뒤 다시 시도하세요.", ex);
        }
        if (!response.IsSuccessStatusCode)
        {
            int code = (int)response.StatusCode;
            response.Dispose();
            throw new UpdateFailure($"업데이트 파일을 받지 못했습니다(HTTP {code}). 잠시 뒤 다시 시도하세요.");
        }
        return response;
    }

    /// <summary>"&lt;hex&gt; *&lt;자산 이름&gt;" 한 줄을 읽어 해시를 돌려준다. 파일 이름이 자산 이름과 다르면 IOException.</summary>
    private async Task<string> ReadChecksumAsync(Uri url, string assetName, CancellationToken ct)
    {
        using HttpResponseMessage response = await GetAssetAsync(url, ct).ConfigureAwait(false);
        await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        byte[] buffer = new byte[MaxChecksumBytes + 1];
        int length = 0;
        int read;
        while (length < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(length), ct).ConfigureAwait(false)) > 0)
            length += read;
        if (length > MaxChecksumBytes)
            throw new UpdateFailure("업데이트 검증 정보의 형식이 맞지 않습니다. 잠시 뒤 다시 시도하세요.");

        string text = Encoding.UTF8.GetString(buffer, 0, length).Trim('\uFEFF', ' ', '\t', '\r', '\n');
        Match m = ChecksumPattern.Match(text);
        if (!m.Success)
            throw new UpdateFailure("업데이트 검증 정보의 형식이 맞지 않습니다. 잠시 뒤 다시 시도하세요.");
        if (!string.Equals(m.Groups[2].Value, assetName, StringComparison.Ordinal))
            throw new UpdateFailure("업데이트 검증 정보가 설치 파일과 맞지 않습니다. 잠시 뒤 다시 시도하세요.");
        return m.Groups[1].Value.ToLowerInvariant();
    }

    /// <summary>자산을 파일로 받으며 SHA-256(소문자 hex)을 계산한다.</summary>
    private async Task<string> DownloadFileAsync(Uri url, string path, IProgress<(long Done, long? Total)>? progress, CancellationToken ct)
    {
        using HttpResponseMessage response = await GetAssetAsync(url, ct).ConfigureAwait(false);
        long? total = response.Content.Headers.ContentLength;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using Stream input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        byte[] buffer = new byte[81920];
        long done = 0;
        progress?.Report((done, total));
        int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;
            progress?.Report((done, total));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>
    /// 압축 파일을 검사한 뒤 최상위 폴더를 벗겨 target 에 푼다. 항목은 모두 안전한 상대 경로이고,
    /// 최상위 폴더가 정확히 하나이며 그 바로 아래에 ohmyZip.exe 가 있어야 한다.
    /// </summary>
    private static void ExtractPackage(string package, string target, CancellationToken ct)
    {
        try
        {
            using ZipArchive zip = ZipFile.OpenRead(package);
            string? top = null;
            bool hasExecutable = false;
            var plan = new List<(ZipArchiveEntry Entry, string Relative, bool IsDirectory)>();
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (!SafePaths.TryGetSafeRelative(entry.FullName, out string relative, out _))
                    throw new UpdateFailure("업데이트 파일에 허용되지 않는 경로가 들어 있습니다. 업데이트를 다시 받아 보세요.");
                bool isDirectory = SafePaths.EndsWithSeparator(entry.FullName);
                int slash = relative.IndexOf('/');
                string first = slash < 0 ? relative : relative[..slash];
                string rest = slash < 0 ? "" : relative[(slash + 1)..];
                if (!isDirectory && rest.Length == 0)
                    throw new UpdateFailure("업데이트 파일의 구성이 올바르지 않습니다. 업데이트를 다시 받아 보세요.");
                if (top is null)
                    top = first;
                else if (!string.Equals(top, first, StringComparison.Ordinal))
                    throw new UpdateFailure("업데이트 파일의 구성이 올바르지 않습니다. 업데이트를 다시 받아 보세요.");
                if (rest.Length == 0)
                    continue;
                if (!isDirectory && string.Equals(rest, MainExecutable, StringComparison.OrdinalIgnoreCase))
                    hasExecutable = true;
                plan.Add((entry, rest, isDirectory));
            }
            if (!hasExecutable)
                throw new UpdateFailure("업데이트 파일에 ohmyZip.exe 가 없습니다. 업데이트를 다시 받아 보세요.");

            Directory.CreateDirectory(target);
            foreach (var (entry, relative, isDirectory) in plan)
            {
                ct.ThrowIfCancellationRequested();
                string path = SafePaths.ResolveUnder(target, relative)
                    ?? throw new UpdateFailure("업데이트 파일에 허용되지 않는 경로가 들어 있습니다. 업데이트를 다시 받아 보세요.");
                if (isDirectory)
                {
                    Directory.CreateDirectory(path);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using Stream input = entry.Open();
                using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                input.CopyTo(output);
            }
        }
        catch (InvalidDataException ex)
        {
            throw new UpdateFailure("내려받은 업데이트 파일을 풀 수 없습니다. 업데이트를 다시 받아 보세요.", ex);
        }
    }

    /// <summary>옛 파일을 옮길 이름. 지난 업데이트의 잔여가 있으면 먼저 지우고, 못 지우면 고유 접미사를 붙인다.</summary>
    private static string FreeLeftoverPath(string target)
    {
        string old = target + LeftoverSuffix;
        if (File.Exists(old))
            TryDeleteFile(old);
        if (!File.Exists(old) && !Directory.Exists(old))
            return old;
        return $"{old}-{Guid.NewGuid():N}";
    }

    private static void CreateMissingDirectories(string directory, List<string> created)
    {
        var missing = new Stack<string>();
        for (string? d = directory; !string.IsNullOrEmpty(d) && !Directory.Exists(d); d = Path.GetDirectoryName(d))
            missing.Push(d);
        while (missing.Count > 0)
        {
            string d = missing.Pop();
            Directory.CreateDirectory(d);
            created.Add(d);
        }
    }

    /// <summary>바꾼 순서의 역순으로 새 파일을 지우고 옛 파일을 제자리로 돌린 뒤, 새로 만든 폴더를 지운다. 모두 되돌렸으면 true.</summary>
    private static bool Rollback(List<(string Target, string? Backup)> replaced, List<string> createdDirectories)
    {
        bool ok = true;
        for (int i = replaced.Count - 1; i >= 0; i--)
        {
            var (target, backup) = replaced[i];
            try
            {
                if (File.Exists(target))
                {
                    File.SetAttributes(target, FileAttributes.Normal);
                    File.Delete(target);
                }
                if (backup is not null)
                    File.Move(backup, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ok = false;
            }
        }
        for (int i = createdDirectories.Count - 1; i >= 0; i--)
        {
            try
            {
                Directory.Delete(createdDirectories[i]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ok = false;
            }
        }
        return ok;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>이 클래스가 내는 실패. Message 는 그대로 보여 줄 수 있는 문장(무엇이 안 되는지 + 어떻게 하면 되는지).</summary>
    private sealed class UpdateFailure : IOException
    {
        public UpdateFailure(string message)
            : base(message)
        {
        }

        public UpdateFailure(string message, Exception inner)
            : base(message, inner)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
