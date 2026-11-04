using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit.Abstractions;

namespace OhMyZip.Core.Tests;

/// <summary>자동 업데이트(확인·내려받기·검증·설치·잔여 정리) 시험. 실제 GitHub 시험은 토큰을 줄 때만 돈다.</summary>
public sealed class AppUpdaterTests
{
    private const string LiveVariable = "OHMYZIP_LIVE_UPDATE";
    private const string Notes = "- 압축 파일 목록 정렬 고침\n- 새 창 열기 개선";
    private static readonly Version Current = new(0, 2, 0);
    private readonly ITestOutputHelper output;

    public AppUpdaterTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public async Task OZ_E80_Check_newer_release_is_available()
    {
        using var temp = new TempDir();
        var github = FakeGitHub.Publish("0.2.7", Package("0.2.7"));
        var updater = NewUpdater(github, temp);

        UpdateCheckResult result = await updater.CheckAsync(new Version(0, 2, 0, 0), CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.Available, result.Status);
        UpdateRelease release = Assert.IsType<UpdateRelease>(result.Release);
        Assert.Equal(new Version(0, 2, 7), release.Version);
        Assert.Equal("v0.2.7", release.Tag);
        Assert.Equal(Notes, release.Notes);
        Assert.Equal(FakeGitHub.BrowserUrl("v0.2.7", "ohmyZip-0.2.7-win-x64.zip"), release.PackageUrl);
        Assert.Equal(FakeGitHub.BrowserUrl("v0.2.7", "ohmyZip-0.2.7-win-x64.zip.sha256"), release.ChecksumUrl);
        Assert.Equal(new Uri("https://github.com/woori-doweb/ohmyzip/releases/tag/v0.2.7"), release.PageUrl);
        Assert.Contains("0.2.7", result.Message);

        Assert.Equal(new Uri("https://api.github.com/repos/woori-doweb/ohmyzip/releases/latest"), AppUpdater.LatestReleaseApi);
        var seen = Assert.Single(github.Seen);
        Assert.Equal(AppUpdater.LatestReleaseApi, seen.Url);
        Assert.Equal("ohmyZip/0.2.0", seen.UserAgent);
        Assert.Equal("application/vnd.github+json", seen.Accept);
    }

    [Theory]
    [InlineData("0.2.7")]
    [InlineData("0.2.7.9")]
    [InlineData("0.3.0")]
    [InlineData("1.0.0")]
    public async Task OZ_E80_Check_same_or_newer_installed_version_is_up_to_date(string installed)
    {
        using var temp = new TempDir();
        var updater = NewUpdater(FakeGitHub.Publish("0.2.7", Package("0.2.7")), temp);

        UpdateCheckResult result = await updater.CheckAsync(Version.Parse(installed), CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.Null(result.Release);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Theory]
    [InlineData("not-found")]
    [InlineData("forbidden")]
    [InlineData("server-error")]
    [InlineData("network")]
    [InlineData("timeout")]
    [InlineData("not-json")]
    [InlineData("wrong-shape")]
    [InlineData("draft")]
    [InlineData("tag-without-v")]
    [InlineData("tag-two-parts")]
    [InlineData("tag-suffix")]
    [InlineData("no-package")]
    [InlineData("no-checksum")]
    [InlineData("package-still-uploading")]
    public async Task OZ_E81_Check_problems_are_unavailable_without_exception(string problem)
    {
        using var temp = new TempDir();
        byte[] package = Package("0.2.7");
        FakeGitHub github = problem switch
        {
            "not-found" => FakeGitHub.Respond(HttpStatusCode.NotFound),
            "forbidden" => FakeGitHub.Respond(HttpStatusCode.Forbidden),
            "server-error" => FakeGitHub.Respond(HttpStatusCode.InternalServerError),
            "network" => FakeGitHub.Respond(_ => throw new HttpRequestException("offline")),
            "timeout" => FakeGitHub.Respond(_ => throw new TaskCanceledException("timeout")),
            "not-json" => FakeGitHub.Respond(_ => FakeGitHub.Json("<html>rate limited</html>")),
            "wrong-shape" => FakeGitHub.Respond(_ => FakeGitHub.Json("[1, 2, 3]")),
            "draft" => FakeGitHub.Publish("0.2.7", package, draft: true),
            "tag-without-v" => FakeGitHub.Publish("0.2.7", package, tag: "0.2.7"),
            "tag-two-parts" => FakeGitHub.Publish("0.2.7", package, tag: "v0.2"),
            "tag-suffix" => FakeGitHub.Publish("0.2.7", package, tag: "v0.2.7-beta"),
            "no-package" => FakeGitHub.Publish("0.2.7", package, withPackage: false),
            "no-checksum" => FakeGitHub.Publish("0.2.7", package, withChecksum: false),
            "package-still-uploading" => FakeGitHub.Publish("0.2.7", package, packageState: "starter"),
            _ => throw new ArgumentOutOfRangeException(nameof(problem)),
        };
        var updater = NewUpdater(github, temp);

        UpdateCheckResult result = await updater.CheckAsync(Current, CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.Unavailable, result.Status);
        Assert.Null(result.Release);
        Assert.Contains("확인하세요", result.Message);
    }

    [Fact]
    public async Task OZ_E82_Download_verifies_and_stages_without_top_folder()
    {
        using var temp = new TempDir();
        byte[] package = Package("0.2.7");
        var github = FakeGitHub.Publish("0.2.7", package);
        var updater = NewUpdater(github, temp);
        UpdateRelease release = (await updater.CheckAsync(Current, CancellationToken.None)).Release!;
        var progress = new ListProgress();

        string staged = await updater.DownloadAsync(release, progress, CancellationToken.None);

        Assert.Equal(temp.Combine("work", "staged-0.2.7"), staged);
        Assert.Equal(["OhMyZip.Core.dll", "ohmyZip.exe", "sub/data.txt"], Util.FilesUnder(staged));
        Assert.Equal("new exe 0.2.7", File.ReadAllText(Path.Combine(staged, "ohmyZip.exe")));
        Assert.Equal("new data 0.2.7", File.ReadAllText(Path.Combine(staged, "sub", "data.txt")));
        Assert.Equal([staged], Directory.GetFileSystemEntries(temp.Combine("work")));
        Assert.Equal(((long)package.Length, (long?)package.Length), progress.Items[^1]);

        // 토큰 없는 HttpClient 는 browser_download_url 을 쓴다.
        Assert.Contains(github.Seen, s => s.Url == release.PackageUrl && s.Accept == "");
        Assert.Contains(github.Seen, s => s.Url == release.ChecksumUrl && s.UserAgent == "ohmyZip/0.2.0");
        Assert.DoesNotContain(github.Seen, s => s.Url.Host == "api.github.com" && s.Url != AppUpdater.LatestReleaseApi);

        // 이미 검증해 둔 준비 폴더는 다시 받지 않고 쓴다.
        int requests = github.Seen.Count;
        Assert.Equal(staged, await updater.DownloadAsync(release, null, CancellationToken.None));
        Assert.Equal(requests, github.Seen.Count);
    }

    [Fact]
    public async Task OZ_E82_Download_with_token_uses_api_asset_url_as_octet_stream()
    {
        using var temp = new TempDir();
        var github = FakeGitHub.Publish("0.2.7", Package("0.2.7"));
        var http = new HttpClient(github);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");
        var updater = new AppUpdater(http, temp.Combine("install"), temp.Combine("work"));
        UpdateRelease release = (await updater.CheckAsync(Current, CancellationToken.None)).Release!;

        string staged = await updater.DownloadAsync(release, null, CancellationToken.None);

        Assert.Equal("new exe 0.2.7", File.ReadAllText(Path.Combine(staged, "ohmyZip.exe")));
        Assert.DoesNotContain(github.Seen, s => s.Url == release.PackageUrl || s.Url == release.ChecksumUrl);
        Assert.Equal(2, github.Seen.Count(s => s.Url.AbsolutePath.Contains("/releases/assets/") && s.Accept == "application/octet-stream"));
    }

    [Fact]
    public async Task OZ_E82_Download_two_windows_at_once_share_one_stage()
    {
        using var temp = new TempDir();
        byte[] package = Package("0.2.7");
        var release = new UpdateRelease(new Version(0, 2, 7), "v0.2.7", Notes,
            FakeGitHub.BrowserUrl("v0.2.7", "ohmyZip-0.2.7-win-x64.zip"),
            FakeGitHub.BrowserUrl("v0.2.7", "ohmyZip-0.2.7-win-x64.zip.sha256"),
            new Uri("https://github.com/woori-doweb/ohmyzip/releases/tag/v0.2.7"));
        var first = NewUpdater(FakeGitHub.Publish("0.2.7", package), temp);
        var second = NewUpdater(FakeGitHub.Publish("0.2.7", package), temp);

        string[] staged = await Task.WhenAll(
            Task.Run(() => first.DownloadAsync(release, null, CancellationToken.None)),
            Task.Run(() => second.DownloadAsync(release, null, CancellationToken.None)));

        Assert.Equal(staged[0], staged[1]);
        Assert.Equal(["OhMyZip.Core.dll", "ohmyZip.exe", "sub/data.txt"], Util.FilesUnder(staged[0]));
        Assert.Equal([temp.Combine("work", "staged-0.2.7")], Directory.GetFileSystemEntries(temp.Combine("work")));
    }

    [Theory]
    [InlineData("hash-mismatch")]
    [InlineData("name-mismatch")]
    [InlineData("bad-format")]
    [InlineData("checksum-missing")]
    public async Task OZ_E83_Download_checksum_problem_throws_and_leaves_no_partial_files(string problem)
    {
        using var temp = new TempDir();
        byte[] package = Package("0.2.7");
        string hash = FakeGitHub.Sha256Hex(package);
        var github = problem switch
        {
            "hash-mismatch" => FakeGitHub.Publish("0.2.7", package, checksumLine: $"{new string('0', 64)} *ohmyZip-0.2.7-win-x64.zip\n"),
            "name-mismatch" => FakeGitHub.Publish("0.2.7", package, checksumLine: $"{hash} *ohmyZip-0.2.6-win-x64.zip\n"),
            "bad-format" => FakeGitHub.Publish("0.2.7", package, checksumLine: "not a checksum"),
            "checksum-missing" => FakeGitHub.Publish("0.2.7", package, checksumStatus: HttpStatusCode.NotFound),
            _ => throw new ArgumentOutOfRangeException(nameof(problem)),
        };
        var updater = NewUpdater(github, temp);
        UpdateRelease release = (await updater.CheckAsync(Current, CancellationToken.None)).Release!;

        var error = await Assert.ThrowsAnyAsync<IOException>(() => updater.DownloadAsync(release, null, CancellationToken.None));

        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.Empty(Directory.GetFileSystemEntries(temp.Combine("work")));
    }

    [Theory]
    [InlineData("parent-escape")]
    [InlineData("nested-escape")]
    [InlineData("backslash-escape")]
    [InlineData("absolute")]
    [InlineData("drive")]
    [InlineData("two-top-folders")]
    [InlineData("top-level-file")]
    [InlineData("no-exe")]
    [InlineData("exe-not-directly-under-top")]
    [InlineData("not-a-zip")]
    public async Task OZ_E84_Download_rejects_unsafe_or_malformed_package(string problem)
    {
        using var temp = new TempDir();
        const string top = "ohmyZip-0.2.7/";
        const string exe = top + "ohmyZip.exe";
        byte[] package = problem switch
        {
            "parent-escape" => RawZip((exe, "exe"), ("../x", "escaped")),
            "nested-escape" => RawZip((exe, "exe"), (top + "../../x", "escaped")),
            "backslash-escape" => RawZip((exe, "exe"), ("ohmyZip-0.2.7\\..\\..\\x", "escaped")),
            "absolute" => RawZip(("/" + exe, "exe")),
            "drive" => RawZip((exe, "exe"), ("C:/x", "escaped")),
            "two-top-folders" => RawZip((exe, "exe"), ("other/readme.txt", "other")),
            "top-level-file" => RawZip((exe, "exe"), ("x", "loose")),
            "no-exe" => RawZip((top + "readme.txt", "readme")),
            "exe-not-directly-under-top" => RawZip((top + "sub/ohmyZip.exe", "exe")),
            "not-a-zip" => Util.Text("this is not a zip file"),
            _ => throw new ArgumentOutOfRangeException(nameof(problem)),
        };
        var updater = NewUpdater(FakeGitHub.Publish("0.2.7", package), temp);
        UpdateRelease release = (await updater.CheckAsync(Current, CancellationToken.None)).Release!;

        await Assert.ThrowsAnyAsync<IOException>(() => updater.DownloadAsync(release, null, CancellationToken.None));

        Assert.Empty(Directory.GetFileSystemEntries(temp.Combine("work")));
        Assert.Empty(Directory.GetFiles(temp.Path, "x", SearchOption.AllDirectories));
    }

    [Fact]
    public void OZ_E85_Apply_replaces_files_and_keeps_old_ones_as_leftovers()
    {
        using var temp = new TempDir();
        string install = temp.Combine("install");
        Write(install, "ohmyZip.exe", "old exe");
        Write(install, "OhMyZip.Core.dll", "old dll");
        Write(install, "sub/data.txt", "old data");
        Write(install, "keep.txt", "keep");
        Write(install, "ohmyZip.exe.ohmyzip-old", "stale from last update");
        string staged = temp.Combine("work", "staged-0.2.7");
        Write(staged, "ohmyZip.exe", "new exe");
        Write(staged, "OhMyZip.Core.dll", "new dll");
        Write(staged, "sub/data.txt", "new data");
        Write(staged, "sub/deeper/added.txt", "added");
        var updater = NewUpdater(new FakeGitHub(), temp);

        updater.Apply(staged);

        Assert.Equal("new exe", Read(install, "ohmyZip.exe"));
        Assert.Equal("new dll", Read(install, "OhMyZip.Core.dll"));
        Assert.Equal("new data", Read(install, "sub/data.txt"));
        Assert.Equal("added", Read(install, "sub/deeper/added.txt"));
        Assert.Equal("keep", Read(install, "keep.txt"));
        Assert.Equal("old exe", Read(install, "ohmyZip.exe.ohmyzip-old"));
        Assert.Equal("old dll", Read(install, "OhMyZip.Core.dll.ohmyzip-old"));
        Assert.Equal("old data", Read(install, "sub/data.txt.ohmyzip-old"));
        Assert.False(File.Exists(Path.Combine(install, "sub", "deeper", "added.txt.ohmyzip-old")));
        Assert.Equal(["OhMyZip.Core.dll", "ohmyZip.exe", "sub/data.txt", "sub/deeper/added.txt"], Util.FilesUnder(staged));

        AppUpdater.CleanupLeftovers(install);

        Assert.Equal(["OhMyZip.Core.dll", "keep.txt", "ohmyZip.exe", "sub/data.txt", "sub/deeper/added.txt"], Util.FilesUnder(install));
    }

    [Fact]
    public void OZ_E86_Apply_failure_midway_restores_original_bytes()
    {
        using var temp = new TempDir();
        string install = temp.Combine("install");
        Write(install, "OhMyZip.Core.dll", "old dll");
        Write(install, "ohmyZip.exe", "old exe");
        Write(install, "zz.txt/inner.txt", "a folder where the update wants a file");
        string staged = temp.Combine("work", "staged-0.2.7");
        Write(staged, "OhMyZip.Core.dll", "new dll");
        Write(staged, "ohmyZip.exe", "new exe");
        Write(staged, "sub/data.txt", "new data");
        Write(staged, "zz.txt", "cannot be placed");
        var before = Snapshot(install);
        var updater = NewUpdater(new FakeGitHub(), temp);

        var error = Assert.ThrowsAny<IOException>(() => updater.Apply(staged));

        Assert.Contains("되돌렸습니다", error.Message);
        Assert.Contains("zz.txt", error.Message);
        Assert.Equal(before, Snapshot(install));
        Assert.False(Directory.Exists(Path.Combine(install, "sub")));
    }

    [Fact]
    public void OZ_E87_Apply_uses_unique_leftover_name_when_previous_one_is_locked()
    {
        using var temp = new TempDir();
        string install = temp.Combine("install");
        Write(install, "ohmyZip.exe", "old exe");
        Write(install, "ohmyZip.exe.ohmyzip-old", "still running from last update");
        string staged = temp.Combine("work", "staged-0.2.7");
        Write(staged, "ohmyZip.exe", "new exe");
        var updater = NewUpdater(new FakeGitHub(), temp);

        using (File.Open(Path.Combine(install, "ohmyZip.exe.ohmyzip-old"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            updater.Apply(staged);

            Assert.Equal("new exe", Read(install, "ohmyZip.exe"));
            string moved = Assert.Single(Directory.GetFiles(install, "ohmyZip.exe.ohmyzip-old-*"));
            Assert.Equal("old exe", File.ReadAllText(moved));

            AppUpdater.CleanupLeftovers(install);
            Assert.True(File.Exists(Path.Combine(install, "ohmyZip.exe.ohmyzip-old")));
        }
        Assert.Equal("still running from last update", Read(install, "ohmyZip.exe.ohmyzip-old"));

        AppUpdater.CleanupLeftovers(install);

        Assert.Equal(["ohmyZip.exe"], Util.FilesUnder(install));
    }

    [Fact]
    public void OZ_E88_CleanupLeftovers_removes_only_leftovers()
    {
        using var temp = new TempDir();
        string install = temp.Combine("install");
        Write(install, "a.dll.ohmyzip-old", "x");
        Write(install, "sub/b.dll.ohmyzip-old", "x");
        Write(install, "c.exe.ohmyzip-old-0123456789abcdef", "x");
        Write(install, "D.EXE.OHMYZIP-OLD", "x");
        Write(install, "readonly.dll.ohmyzip-old", "x");
        File.SetAttributes(Path.Combine(install, "readonly.dll.ohmyzip-old"), FileAttributes.ReadOnly);
        Write(install, "keep.txt", "keep");
        Write(install, "notes.ohmyzip-old.txt", "keep");
        Write(install, "ohmyzip-old.txt", "keep");
        Write(install, "sub/ohmyZip.exe", "keep");

        AppUpdater.CleanupLeftovers(install);
        AppUpdater.CleanupLeftovers(temp.Combine("does-not-exist"));

        Assert.Equal(["keep.txt", "notes.ohmyzip-old.txt", "ohmyzip-old.txt", "sub/ohmyZip.exe"], Util.FilesUnder(install));
    }

    [LiveUpdateFact]
    public async Task OZ_E89_Live_latest_release_downloads_and_applies()
    {
        using var temp = new TempDir();
        using var http = new HttpClient();
        string install = temp.Combine("install");
        byte[] placeholder = Util.Text("placeholder for an older ohmyZip.exe");
        Directory.CreateDirectory(install);
        File.WriteAllBytes(Path.Combine(install, "ohmyZip.exe"), placeholder);
        var updater = new AppUpdater(http, install, temp.Combine("work"));

        UpdateCheckResult check = await updater.CheckAsync(new Version(0, 0, 1), CancellationToken.None);
        output.WriteLine($"check: status={check.Status}; tag={check.Release?.Tag}; message={check.Message}");
        Assert.Equal(UpdateCheckStatus.Available, check.Status);
        UpdateRelease release = check.Release!;

        var progress = new ListProgress();
        string staged = await updater.DownloadAsync(release, progress, CancellationToken.None);
        output.WriteLine($"downloaded: {progress.Items[^1].Done} bytes; staged={staged}; files={Util.FilesUnder(staged).Count}");
        updater.Apply(staged);

        byte[] installed = File.ReadAllBytes(Path.Combine(install, "ohmyZip.exe"));
        Assert.Equal(File.ReadAllBytes(Path.Combine(staged, "ohmyZip.exe")), installed);
        Assert.NotEqual(placeholder, installed);
        Assert.Equal(placeholder, File.ReadAllBytes(Path.Combine(install, "ohmyZip.exe.ohmyzip-old")));
        Assert.Equal(Util.FilesUnder(staged).Count + 1, Util.FilesUnder(install).Count);
    }

    private static AppUpdater NewUpdater(FakeGitHub github, TempDir temp) =>
        new(new HttpClient(github), temp.Combine("install"), temp.Combine("work"));

    /// <summary>배포 형식 그대로의 설치 파일: 최상위 폴더 "ohmyZip-버전/" 아래 파일들.</summary>
    private static byte[] Package(string version) =>
        RawZip(
            ($"ohmyZip-{version}/", ""),
            ($"ohmyZip-{version}/ohmyZip.exe", $"new exe {version}"),
            ($"ohmyZip-{version}/OhMyZip.Core.dll", $"new dll {version}"),
            ($"ohmyZip-{version}/sub/data.txt", $"new data {version}"));

    /// <summary>임의 항목 이름으로 메모리에서 ZIP 을 만든다. 이름이 '/' 로 끝나면 폴더 항목.</summary>
    private static byte[] RawZip(params (string Name, string Text)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in entries)
            {
                ZipArchiveEntry entry = zip.CreateEntry(name);
                if (name.EndsWith('/'))
                    continue;
                using Stream s = entry.Open();
                s.Write(Util.Text(text));
            }
        }
        return stream.ToArray();
    }

    private static void Write(string root, string relative, string text)
    {
        string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Util.Text(text));
    }

    private static string Read(string root, string relative) =>
        File.ReadAllText(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>폴더 아래 모든 폴더와 파일(바이트 해시)을 비교 가능한 목록으로.</summary>
    private static List<string> Snapshot(string root) =>
        Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
            .Select(d => "dir " + Path.GetRelativePath(root, d))
            .Concat(Util.FilesUnder(root).Select(f => $"file {f} {Util.Sha256(Path.Combine(root, f))}"))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

    private sealed class ListProgress : IProgress<(long Done, long? Total)>
    {
        public List<(long Done, long? Total)> Items { get; } = [];

        public void Report((long Done, long? Total) value) => Items.Add(value);
    }

    /// <summary>GitHub Releases API·자산 다운로드를 흉내 내는 처리기. 받은 요청의 URL·User-Agent·Accept 를 기록한다.</summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> routes = new(StringComparer.Ordinal);
        private readonly List<(Uri Url, string UserAgent, string Accept)> seen = [];

        public List<(Uri Url, string UserAgent, string Accept)> Seen
        {
            get
            {
                lock (seen)
                    return [.. seen];
            }
        }

        public static Uri BrowserUrl(string tag, string name) => new($"https://github.com/{AppUpdater.Repository}/releases/download/{tag}/{name}");

        private static Uri ApiAssetUrl(int id) => new($"https://api.github.com/repos/{AppUpdater.Repository}/releases/assets/{id}");

        public static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        public static FakeGitHub Respond(HttpStatusCode code) => Respond(_ => new HttpResponseMessage(code));

        public static FakeGitHub Respond(Func<HttpRequestMessage, HttpResponseMessage> latest)
        {
            var github = new FakeGitHub();
            github.routes[AppUpdater.LatestReleaseApi.AbsoluteUri] = latest;
            return github;
        }

        /// <summary>최신 릴리스 하나를 게시한다. 자산은 browser_download_url 과 API url(octet-stream 요청 때만 바이트) 둘 다 응답한다.</summary>
        public static FakeGitHub Publish(
            string version,
            byte[] package,
            string? tag = null,
            string? checksumLine = null,
            HttpStatusCode checksumStatus = HttpStatusCode.OK,
            bool withPackage = true,
            bool withChecksum = true,
            string packageState = "uploaded",
            bool draft = false)
        {
            tag ??= "v" + version;
            string packageName = $"ohmyZip-{version}-win-x64.zip";
            string checksumName = packageName + ".sha256";
            byte[] checksum = Encoding.UTF8.GetBytes(checksumLine ?? $"{Sha256Hex(package)} *{packageName}\n");
            var github = new FakeGitHub();
            var assets = new List<object>();
            if (withPackage)
                github.AddAsset(assets, 101, tag, packageName, packageState, _ => Bytes(package));
            if (withChecksum)
                github.AddAsset(assets, 102, tag, checksumName, "uploaded", _ => checksumStatus == HttpStatusCode.OK ? Bytes(checksum) : new HttpResponseMessage(checksumStatus));
            string json = JsonSerializer.Serialize(new
            {
                tag_name = tag,
                name = tag,
                body = Notes,
                html_url = $"https://github.com/{AppUpdater.Repository}/releases/tag/{tag}",
                draft,
                prerelease = false,
                assets,
            });
            github.routes[AppUpdater.LatestReleaseApi.AbsoluteUri] = _ => Json(json);
            return github;
        }

        public static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Bytes(byte[] data) =>
            new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };

        private void AddAsset(List<object> assets, int id, string tag, string name, string state, Func<HttpRequestMessage, HttpResponseMessage> download)
        {
            Uri browser = BrowserUrl(tag, name);
            Uri api = ApiAssetUrl(id);
            assets.Add(new { id, name, state, url = api.AbsoluteUri, browser_download_url = browser.AbsoluteUri });
            routes[browser.AbsoluteUri] = download;
            routes[api.AbsoluteUri] = request =>
                request.Headers.Accept.Any(a => a.MediaType == "application/octet-stream")
                    ? download(request)
                    : Json(JsonSerializer.Serialize(new { id, name }));
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri url = request.RequestUri!;
            lock (seen)
                seen.Add((url, request.Headers.UserAgent.ToString(), request.Headers.Accept.ToString()));
            HttpResponseMessage response = routes.TryGetValue(url.AbsoluteUri, out var respond)
                ? respond(request)
                : new HttpResponseMessage(HttpStatusCode.NotFound);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private sealed class LiveUpdateFactAttribute : FactAttribute
    {
        public LiveUpdateFactAttribute()
        {
            if (Environment.GetEnvironmentVariable(LiveVariable) != "1")
                Skip = $"Live update test not run; set {LiveVariable}=1 to update from the public {AppUpdater.Repository} releases without a token.";
        }
    }
}
