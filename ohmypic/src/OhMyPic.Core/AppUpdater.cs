using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OhMyPic.Core;

public enum UpdateCheckStatus { UpToDate, Available, Unavailable }

/// <summary>Why a check, download or install did not succeed. The app turns each code into a sentence.</summary>
public enum UpdateError
{
    None,
    /// <summary>No ohMypic release has been published yet (or the release list is missing).</summary>
    NoRelease,
    /// <summary>GitHub refused the request (rate limit, 403/429).</summary>
    ServerBusy,
    /// <summary>Unexpected HTTP status. Detail = the status code.</summary>
    HttpError,
    ConnectFailed,
    Timeout,
    ConnectionLost,
    /// <summary>The release list is not the expected JSON.</summary>
    InfoUnreadable,
    /// <summary>The newest release lacks the package or its .sha256 file. Detail = tag.</summary>
    PackageMissing,
    /// <summary>An address is not https or not on the GitHub host allow-list. Detail = host.</summary>
    UnsafeUrl,
    /// <summary>A response or the package exceeds its size limit.</summary>
    TooLarge,
    ChecksumUnreadable,
    ChecksumWrongFile,
    ChecksumMismatch,
    /// <summary>A package entry is absolute, has "..", a drive, a device name or another unsafe path.</summary>
    PackageUnsafePath,
    /// <summary>The package is not exactly one top folder with files below it.</summary>
    PackageBadLayout,
    PackageNoExecutable,
    PackageUnreadable,
    DownloadFailed,
    StagedMissing,
    InstallFolderMissing,
    /// <summary>Detail = relative path.</summary>
    FileOutsideInstall,
    /// <summary>Detail = relative path.</summary>
    TargetIsFolder,
    AccessDenied,
    /// <summary>Install failed and every file was put back. Detail = system message.</summary>
    InstallFailed,
    /// <summary>Install failed and some files could not be put back. Detail = system message.</summary>
    RollbackIncomplete,
}

/// <summary>One ohMypic release. PackageUrl/ChecksumUrl are the assets' browser_download_url.</summary>
public sealed record UpdateRelease(Version Version, string Tag, string Notes, Uri PackageUrl, Uri ChecksumUrl, Uri PageUrl, long? PackageSize = null);

/// <summary>Result of a check. Release is set only when Available; Error/Detail only when Unavailable.</summary>
public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateRelease? Release, UpdateError Error = UpdateError.None, string? Detail = null);

/// <summary>Typed failure of a download or install. The message is technical English; the app shows text for <see cref="Code"/>.</summary>
public sealed class UpdateException : IOException
{
    public UpdateException(UpdateError code, string? detail = null, Exception? inner = null)
        : base(detail is null ? $"Update failed: {code}." : $"Update failed: {code} ({detail}).", inner)
    {
        Code = code;
        Detail = detail;
    }

    public UpdateError Code { get; }

    public string? Detail { get; }
}

/// <summary>
/// Checks GitHub Releases for the newest ohMypic release, downloads and verifies it, and swaps the files of the install
/// folder. ohMypic shares the repository with another app: its releases are prereleases tagged "ohmypic-vX.Y.Z", so the
/// release list is scanned instead of the "latest" release. Installing renames every replaced file to
/// "&lt;name&gt;.ohmypic-old" and copies the new file in its place, which works for the running exe and dlls too;
/// <see cref="CleanupLeftovers"/> removes the old files on the next start.
/// </summary>
public sealed class AppUpdater
{
    public const string Repository = "woori-doweb/ohmyzip";
    public const string MainExecutable = "ohMypic.exe";
    public const string MainAssembly = "ohMypic.dll";
    public const string LeftoverSuffix = ".ohmypic-old";

    public static readonly Uri ReleasesApi = new($"https://api.github.com/repos/{Repository}/releases?per_page=50");
    public static readonly Uri ReleasesPage = new($"https://github.com/{Repository}/releases");

    internal const long MaxReleaseListBytes = 8L * 1024 * 1024;
    internal const int MaxChecksumBytes = 4096;
    internal const long MaxPackageBytes = 512L * 1024 * 1024;
    internal const long MaxExtractedBytes = 1024L * 1024 * 1024;
    internal const int MaxPackageEntries = 10_000;

    private const string StagedPrefix = "staged-";
    private const string DownloadPrefix = "download-";

    /// <summary>Hosts the app may ask for release data and assets.</summary>
    private static readonly string[] AllowedHosts = ["github.com", "api.github.com", "objects.githubusercontent.com"];

    /// <summary>Hosts a download may end on after redirects: the allow-list plus GitHub's release asset CDN.</summary>
    private static readonly string[] AllowedFinalHosts = [.. AllowedHosts, "release-assets.githubusercontent.com"];

    private static readonly Regex TagPattern = new(@"^ohmypic-v([0-9]{1,9})\.([0-9]{1,9})\.([0-9]{1,9})$", RegexOptions.CultureInvariant);
    private static readonly Regex LeadingVersionPattern = new(@"^([0-9]{1,9})\.([0-9]{1,9})\.([0-9]{1,9})(?![0-9])", RegexOptions.CultureInvariant);
    private static readonly Regex ChecksumPattern = new(@"^([0-9a-fA-F]{64}) [ *]([^\r\n]+)$", RegexOptions.CultureInvariant);

    private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly HttpClient http;
    private readonly string installRoot;
    private readonly string workRoot;
    private readonly Uri releasesApi;
    private string userAgentVersion = VersionOf(Assembly.GetEntryAssembly() ?? typeof(AppUpdater).Assembly).ToString(3);

    /// <param name="http">Client used for every request (tests pass one with a fake handler).</param>
    /// <param name="installDir">Folder whose files are replaced (the app folder).</param>
    /// <param name="workDir">Download and staging folder (%LOCALAPPDATA%\ohMypic\updates in the app).</param>
    /// <param name="releasesApi">Release list address; must be https on an allowed GitHub host. Default <see cref="ReleasesApi"/>.</param>
    public AppUpdater(HttpClient http, string installDir, string workDir, Uri? releasesApi = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrEmpty(installDir);
        ArgumentException.ThrowIfNullOrEmpty(workDir);
        this.http = http;
        installRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDir));
        workRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workDir));
        this.releasesApi = releasesApi ?? ReleasesApi;
        if (!IsAllowed(this.releasesApi, AllowedHosts))
            throw new ArgumentException("The release list address must be https on an allowed GitHub host.", nameof(releasesApi));
    }

    /// <summary>
    /// Finds the newest ohMypic release. Network, server and format problems come back as Unavailable, never as
    /// exceptions (only cancellation of <paramref name="ct"/> throws).
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(Version current, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(current);
        Version installed = ThreePart(current);
        userAgentVersion = installed.ToString(3);

        string json;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, releasesApi);
            AddUserAgent(request);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            Uri final = FinalUri(response, releasesApi);
            if (!IsAllowed(final, AllowedFinalHosts))
                return Unavailable(UpdateError.UnsafeUrl, final.Host);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return Unavailable(UpdateError.NoRelease);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                return Unavailable(UpdateError.ServerBusy);
            if (!response.IsSuccessStatusCode)
                return Unavailable(UpdateError.HttpError, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
            if (response.Content.Headers.ContentLength > MaxReleaseListBytes)
                return Unavailable(UpdateError.TooLarge);
            byte[]? body = await ReadLimitedAsync(response, MaxReleaseListBytes, ct).ConfigureAwait(false);
            if (body is null)
                return Unavailable(UpdateError.TooLarge);
            json = Encoding.UTF8.GetString(body);
        }
        catch (HttpRequestException)
        {
            return Unavailable(UpdateError.ConnectFailed);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Unavailable(UpdateError.Timeout);
        }
        catch (IOException)
        {
            return Unavailable(UpdateError.ConnectionLost);
        }

        ReleaseInfo? latest;
        try
        {
            latest = SelectLatest(json);
        }
        catch (JsonException)
        {
            return Unavailable(UpdateError.InfoUnreadable);
        }
        if (latest is null)
            return Unavailable(UpdateError.NoRelease);
        if (latest.Version <= installed)
            return new UpdateCheckResult(UpdateCheckStatus.UpToDate, null);
        return ToRelease(latest);
    }

    /// <summary>
    /// Downloads the package and its .sha256, compares the hash, extracts safely and returns the staging folder
    /// (workDir\staged-X.Y.Z, top folder removed). Work happens in a unique temp folder that is always deleted; the
    /// staging folder appears only after every check passed. Failures throw <see cref="UpdateException"/>.
    /// </summary>
    public async Task<string> DownloadAsync(UpdateRelease release, IProgress<(long Done, long? Total)>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(release);
        string version = ThreePart(release.Version).ToString(3);
        string assetName = PackageName(version);
        string staged = Path.Join(workRoot, StagedPrefix + version);
        if (File.Exists(Path.Join(staged, MainExecutable)))
            return staged;

        string temp = Path.Join(workRoot, $"{DownloadPrefix}{version}-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(temp);
            string expected = await ReadChecksumAsync(release.ChecksumUrl, assetName, ct).ConfigureAwait(false);
            string package = Path.Join(temp, assetName);
            string actual = await DownloadFileAsync(release.PackageUrl, package, progress, ct).ConfigureAwait(false);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new UpdateException(UpdateError.ChecksumMismatch);

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
                // Another window staged the same version first; it passed the same checks, so use it.
            }
            return staged;
        }
        catch (Exception ex) when (ex is not (UpdateException or OperationCanceledException))
        {
            throw new UpdateException(UpdateError.DownloadFailed, ex.Message, ex);
        }
        finally
        {
            TryDeleteDirectory(temp);
        }
    }

    /// <summary>
    /// Replaces the install folder's files with the staged files (subfolders included). Each existing file is kept as
    /// "&lt;name&gt;.ohmypic-old". When anything fails, every replaced file and created folder is put back and an
    /// <see cref="UpdateException"/> is thrown.
    /// </summary>
    public void Apply(string stagedDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(stagedDir);
        string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagedDir));
        if (!File.Exists(Path.Join(source, MainExecutable)))
            throw new UpdateException(UpdateError.StagedMissing);
        if (!Directory.Exists(installRoot))
            throw new UpdateException(UpdateError.InstallFolderMissing);

        string[] files = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        var replaced = new List<(string Target, string? Backup)>();
        var createdDirectories = new List<string>();
        try
        {
            foreach (string file in files)
            {
                string relative = Path.GetRelativePath(source, file);
                string target = ResolveUnder(installRoot, relative)
                    ?? throw new UpdateException(UpdateError.FileOutsideInstall, relative);
                if (Directory.Exists(target))
                    throw new UpdateException(UpdateError.TargetIsFolder, relative);

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
            if (!Rollback(replaced, createdDirectories))
                throw new UpdateException(UpdateError.RollbackIncomplete, ex.Message, ex);
            if (ex is UpdateException)
                throw;
            if (ex is UnauthorizedAccessException)
                throw new UpdateException(UpdateError.AccessDenied, ex.Message, ex);
            throw new UpdateException(UpdateError.InstallFailed, ex.Message, ex);
        }
    }

    /// <summary>Deletes only "*.ohmypic-old*" leftovers below the install folder. Files still in use are skipped quietly.</summary>
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

    /// <summary>
    /// Deletes staging folders that are not newer than <paramref name="installed"/> and download folders older than a
    /// day (a younger one may belong to another running window). Problems are skipped quietly.
    /// </summary>
    public static void CleanupWorkFolder(string workDir, Version installed)
    {
        ArgumentNullException.ThrowIfNull(installed);
        try
        {
            if (string.IsNullOrEmpty(workDir) || !Directory.Exists(workDir))
                return;
            Version current = ThreePart(installed);
            DateTime cutoff = DateTime.UtcNow - TimeSpan.FromDays(1);
            foreach (string dir in Directory.EnumerateDirectories(workDir))
            {
                string name = Path.GetFileName(dir);
                bool stale = name.StartsWith(StagedPrefix, StringComparison.Ordinal)
                    ? ParseReleaseVersion(name[StagedPrefix.Length..]) is not { } v || v <= current
                    : name.StartsWith(DownloadPrefix, StringComparison.Ordinal) && Directory.GetLastWriteTimeUtc(dir) < cutoff;
                if (stale)
                    TryDeleteDirectory(dir);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>"1.2.3", "1.2.3-dev", "1.2.3+abc" → 1.2.3. Null when the text does not start with three numbers.</summary>
    public static Version? ParseReleaseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        Match m = LeadingVersionPattern.Match(text.Trim());
        return m.Success ? ToVersion(m) : null;
    }

    /// <summary>Release version of an assembly: its informational version, else its assembly version (three parts).</summary>
    public static Version VersionOf(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return ParseReleaseVersion(informational) ?? ThreePart(assembly.GetName().Version ?? new Version(0, 0, 0));
    }

    /// <summary>Version of the app files currently in <paramref name="installDir"/> (product version of ohMypic.dll); null when unreadable.</summary>
    public static Version? InstalledVersion(string installDir)
    {
        try
        {
            string path = Path.Join(installDir, MainAssembly);
            if (!File.Exists(path))
                return null;
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
            return ParseReleaseVersion(info.ProductVersion) ?? ParseReleaseVersion(info.FileVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>"name.ohmypic-old" or "name.ohmypic-old-suffix".</summary>
    internal static bool IsLeftoverName(string name)
    {
        int i = name.LastIndexOf(LeftoverSuffix, StringComparison.OrdinalIgnoreCase);
        if (i <= 0)
            return false;
        int end = i + LeftoverSuffix.Length;
        return end == name.Length || name[end] == '-';
    }

    internal static string PackageName(string version) => $"ohMypic-{version}-win-x64.zip";

    private static Version ThreePart(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    private static Version ToVersion(Match m) =>
        new(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));

    private static UpdateCheckResult Unavailable(UpdateError error, string? detail = null) =>
        new(UpdateCheckStatus.Unavailable, null, error, detail);

    private void AddUserAgent(HttpRequestMessage request) =>
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ohMypic", userAgentVersion));

    // ───────────── release list ─────────────

    private sealed record AssetInfo(string Name, string? Url, string? State, long? Size);

    private sealed record ReleaseInfo(Version Version, string Tag, string Notes, string? PageUrl, IReadOnlyList<AssetInfo> Assets);

    /// <summary>
    /// Newest non-draft release tagged exactly "ohmypic-vX.Y.Z" (prereleases accepted; other apps' tags and malformed
    /// tags ignored). Null when there is none. Throws JsonException when the text is not a JSON array.
    /// </summary>
    private static ReleaseInfo? SelectLatest(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array)
            throw new JsonException("The release list is not a JSON array.");

        ReleaseInfo? best = null;
        foreach (JsonElement release in root.EnumerateArray())
        {
            if (release.ValueKind != JsonValueKind.Object || IsTrue(release, "draft"))
                continue;
            string? tag = StringProperty(release, "tag_name");
            Match m = TagPattern.Match(tag ?? "");
            if (tag is null || !m.Success)
                continue;
            Version version = ToVersion(m);
            if (best is not null && version <= best.Version)
                continue;

            var assets = new List<AssetInfo>();
            if (release.TryGetProperty("assets", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement asset in list.EnumerateArray())
                {
                    if (asset.ValueKind != JsonValueKind.Object || StringProperty(asset, "name") is not { } name)
                        continue;
                    long? size = asset.TryGetProperty("size", out JsonElement s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out long n) ? n : null;
                    assets.Add(new AssetInfo(name, StringProperty(asset, "browser_download_url"), StringProperty(asset, "state"), size));
                }
            }
            best = new ReleaseInfo(version, tag, StringProperty(release, "body") ?? "", StringProperty(release, "html_url"), assets);
        }
        return best;
    }

    /// <summary>Checks the newest release's assets: both present and uploaded, https on an allowed host, within the size limit.</summary>
    private static UpdateCheckResult ToRelease(ReleaseInfo info)
    {
        string packageName = PackageName(info.Version.ToString(3));
        string checksumName = packageName + ".sha256";
        AssetInfo? package = info.Assets.FirstOrDefault(a => a.Name == packageName && (a.State is null || a.State == "uploaded"));
        AssetInfo? checksum = info.Assets.FirstOrDefault(a => a.Name == checksumName && (a.State is null || a.State == "uploaded"));
        if (package is null || checksum is null || package.Url is null || checksum.Url is null)
            return Unavailable(UpdateError.PackageMissing, info.Tag);
        if (!Uri.TryCreate(package.Url, UriKind.Absolute, out Uri? packageUrl) || !IsAllowed(packageUrl, AllowedHosts))
            return Unavailable(UpdateError.UnsafeUrl, packageUrl?.Host);
        if (!Uri.TryCreate(checksum.Url, UriKind.Absolute, out Uri? checksumUrl) || !IsAllowed(checksumUrl, AllowedHosts))
            return Unavailable(UpdateError.UnsafeUrl, checksumUrl?.Host);
        if (package.Size > MaxPackageBytes)
            return Unavailable(UpdateError.TooLarge);

        Uri page = Uri.TryCreate(info.PageUrl, UriKind.Absolute, out Uri? html) && IsAllowed(html, AllowedHosts)
            ? html
            : new Uri($"https://github.com/{Repository}/releases/tag/{info.Tag}");
        var release = new UpdateRelease(info.Version, info.Tag, info.Notes, packageUrl, checksumUrl, page, package.Size);
        return new UpdateCheckResult(UpdateCheckStatus.Available, release);
    }

    private static string? StringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsTrue(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    /// <summary>https, default port, no user info, host on the list.</summary>
    private static bool IsAllowed(Uri uri, string[] hosts) =>
        uri.IsAbsoluteUri
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo)
        && hosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);

    /// <summary>Address the response really came from (after redirects).</summary>
    private static Uri FinalUri(HttpResponseMessage response, Uri requested) =>
        response.RequestMessage?.RequestUri is { IsAbsoluteUri: true } final ? final : requested;

    // ───────────── download ─────────────

    /// <summary>Whole body up to <paramref name="limit"/> bytes; null when it is longer.</summary>
    private static async Task<byte[]?> ReadLimitedAsync(HttpResponseMessage response, long limit, CancellationToken ct)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit)
                return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>Requests one asset. The request and the final (redirected) address must both pass the host allow-list.</summary>
    private async Task<HttpResponseMessage> GetAssetAsync(Uri url, CancellationToken ct)
    {
        if (!IsAllowed(url, AllowedHosts))
            throw new UpdateException(UpdateError.UnsafeUrl, url.IsAbsoluteUri ? url.Host : null);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddUserAgent(request);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException(UpdateError.ConnectFailed, ex.Message, ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new UpdateException(UpdateError.Timeout, null, ex);
        }
        Uri final = FinalUri(response, url);
        if (!IsAllowed(final, AllowedFinalHosts))
        {
            response.Dispose();
            throw new UpdateException(UpdateError.UnsafeUrl, final.Host);
        }
        if (!response.IsSuccessStatusCode)
        {
            int code = (int)response.StatusCode;
            response.Dispose();
            throw new UpdateException(UpdateError.HttpError, code.ToString(CultureInfo.InvariantCulture));
        }
        return response;
    }

    /// <summary>Reads the single line "&lt;hex&gt; *&lt;asset name&gt;" and returns the hash. The name must be the package's.</summary>
    private async Task<string> ReadChecksumAsync(Uri url, string assetName, CancellationToken ct)
    {
        using HttpResponseMessage response = await GetAssetAsync(url, ct).ConfigureAwait(false);
        byte[]? body = await ReadLimitedAsync(response, MaxChecksumBytes, ct).ConfigureAwait(false)
            ?? throw new UpdateException(UpdateError.ChecksumUnreadable);
        string text = Encoding.UTF8.GetString(body).Trim('\uFEFF', ' ', '\t', '\r', '\n');
        Match m = ChecksumPattern.Match(text);
        if (!m.Success)
            throw new UpdateException(UpdateError.ChecksumUnreadable);
        if (!string.Equals(m.Groups[2].Value, assetName, StringComparison.Ordinal))
            throw new UpdateException(UpdateError.ChecksumWrongFile, m.Groups[2].Value);
        return m.Groups[1].Value.ToLowerInvariant();
    }

    /// <summary>Saves the asset to a file while computing its SHA-256 (lowercase hex). Stops at <see cref="MaxPackageBytes"/>.</summary>
    private async Task<string> DownloadFileAsync(Uri url, string path, IProgress<(long Done, long? Total)>? progress, CancellationToken ct)
    {
        using HttpResponseMessage response = await GetAssetAsync(url, ct).ConfigureAwait(false);
        long? total = response.Content.Headers.ContentLength;
        if (total > MaxPackageBytes)
            throw new UpdateException(UpdateError.TooLarge);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using Stream input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        byte[] buffer = new byte[81920];
        long done = 0;
        progress?.Report((done, total));
        int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            done += read;
            if (done > MaxPackageBytes)
                throw new UpdateException(UpdateError.TooLarge);
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            progress?.Report((done, total));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    // ───────────── extraction ─────────────

    /// <summary>
    /// Validates the whole package first, then extracts it into <paramref name="target"/> without its top folder.
    /// Every entry must be a safe relative path under exactly one top folder, ohMypic.exe must sit directly below it,
    /// and entry count and total size are limited.
    /// </summary>
    private static void ExtractPackage(string package, string target, CancellationToken ct)
    {
        try
        {
            using ZipArchive zip = ZipFile.OpenRead(package);
            if (zip.Entries.Count > MaxPackageEntries)
                throw new UpdateException(UpdateError.TooLarge);
            string? top = null;
            bool hasExecutable = false;
            long totalBytes = 0;
            var plan = new List<(ZipArchiveEntry Entry, string Relative, bool IsDirectory)>();
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (!TryGetSafeRelative(entry.FullName, out string relative))
                    throw new UpdateException(UpdateError.PackageUnsafePath, entry.FullName);
                bool isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
                int slash = relative.IndexOf('/');
                string first = slash < 0 ? relative : relative[..slash];
                string rest = slash < 0 ? "" : relative[(slash + 1)..];
                if (!isDirectory && rest.Length == 0)
                    throw new UpdateException(UpdateError.PackageBadLayout, entry.FullName);
                if (top is null)
                    top = first;
                else if (!string.Equals(top, first, StringComparison.Ordinal))
                    throw new UpdateException(UpdateError.PackageBadLayout, entry.FullName);
                if (rest.Length == 0)
                    continue;
                if (!isDirectory && string.Equals(rest, MainExecutable, StringComparison.OrdinalIgnoreCase))
                    hasExecutable = true;
                totalBytes += Math.Max(0, entry.Length);
                if (totalBytes > MaxExtractedBytes)
                    throw new UpdateException(UpdateError.TooLarge);
                plan.Add((entry, rest, isDirectory));
            }
            if (!hasExecutable)
                throw new UpdateException(UpdateError.PackageNoExecutable);

            Directory.CreateDirectory(target);
            byte[] buffer = new byte[81920];
            foreach (var (entry, relative, isDirectory) in plan)
            {
                ct.ThrowIfCancellationRequested();
                string path = ResolveUnder(target, relative)
                    ?? throw new UpdateException(UpdateError.PackageUnsafePath, entry.FullName);
                if (isDirectory)
                {
                    Directory.CreateDirectory(path);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using Stream input = entry.Open();
                using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                long written = 0;
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    written += read;
                    if (written > entry.Length)
                        throw new UpdateException(UpdateError.PackageUnreadable, entry.FullName);
                    output.Write(buffer, 0, read);
                }
            }
        }
        catch (InvalidDataException ex)
        {
            throw new UpdateException(UpdateError.PackageUnreadable, ex.Message, ex);
        }
    }

    /// <summary>
    /// Validates a package entry name: no root, drive letter, ':', "..", invalid characters, device names or names
    /// Windows would change (trailing '.'/space). On success returns the '/'-separated relative path.
    /// </summary>
    internal static bool TryGetSafeRelative(string rawName, out string relative)
    {
        relative = "";
        string p = rawName.Replace('\\', '/');
        if (p.StartsWith('/') || p.Contains(':'))
            return false;
        var parts = new List<string>();
        foreach (string seg in p.Split('/'))
        {
            if (seg.Length == 0 || seg == ".")
                continue;
            if (seg == ".." || seg.IndexOfAny(InvalidNameChars) >= 0 || seg.EndsWith('.') || seg.EndsWith(' '))
                return false;
            if (ReservedDeviceNames.Contains(seg.Split('.')[0].TrimEnd(' ')))
                return false;
            parts.Add(seg);
        }
        if (parts.Count == 0)
            return false;
        relative = string.Join('/', parts);
        return true;
    }

    /// <summary>Full path of <paramref name="relative"/> under <paramref name="root"/>; null when it would leave root.</summary>
    private static string? ResolveUnder(string root, string relative)
    {
        string full = Path.GetFullPath(Path.Join(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        string r = Path.TrimEndingDirectorySeparator(root);
        bool under = full.Length > r.Length + 1
            && full.StartsWith(r, StringComparison.OrdinalIgnoreCase)
            && (full[r.Length] == Path.DirectorySeparatorChar || full[r.Length] == Path.AltDirectorySeparatorChar);
        return under ? full : null;
    }

    // ───────────── install helpers ─────────────

    /// <summary>Name for the replaced file. A leftover of an earlier update is deleted first; if it is locked, a unique suffix is added.</summary>
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

    /// <summary>Undoes the swap in reverse order, then removes created folders. True when everything was restored.</summary>
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
