using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OhMyPic.Core.Tests;

/// <summary>
/// Updater: release selection in the shared repository, download verification, safe extraction, install swap and
/// leftover cleanup. No network: every request is answered by <see cref="FakeGitHub"/>; all files live in a temp folder.
/// </summary>
public sealed class UpdaterTests : IDisposable
{
    private const string Notes = "- Faster thumbnails\n- Fixed slideshow order";
    private const string Tag = "ohmypic-v0.2.10";
    private const string NewVer = "0.2.10";
    private static readonly Version Current = new(0, 2, 0);
    private readonly TempDir temp = new();

    public void Dispose() => temp.Dispose();

    // ───────────── check ─────────────

    [Fact]
    public async Task OP_E23_Check_picks_highest_ohmypic_prerelease_and_ignores_other_tags()
    {
        var github = new FakeGitHub();
        github.List(
            github.Release("v9.9.9", "9.9.9", prerelease: false, assetPrefix: "ohmyZip"),
            github.Release("ohmypic-v0.9.0", "0.9.0", draft: true),
            github.Release("ohmypic-v0.5.0-beta", "0.5.0"),
            github.Release("ohmypic-v0.6", "0.6.0"),
            github.Release("OHMYPIC-v0.7.0", "0.7.0"),
            github.Release("ohmyzip-v0.8.0", "0.8.0"),
            github.Release("ohmypic-v0.2.9", "0.2.9"),
            github.Release(Tag, NewVer),
            github.Release("ohmypic-v0.2.2", "0.2.2"));
        var updater = NewUpdater(github);

        UpdateCheckResult result = await updater.CheckAsync(new Version(0, 2, 0, 0), CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.Available, result.Status);
        Assert.Equal(UpdateError.None, result.Error);
        UpdateRelease release = Assert.IsType<UpdateRelease>(result.Release);
        Assert.Equal(new Version(0, 2, 10), release.Version);
        Assert.Equal(Tag, release.Tag);
        Assert.Equal(Notes, release.Notes);
        Assert.Equal(FakeGitHub.BrowserUrl(Tag, "ohMypic-0.2.10-win-x64.zip"), release.PackageUrl);
        Assert.Equal(FakeGitHub.BrowserUrl(Tag, "ohMypic-0.2.10-win-x64.zip.sha256"), release.ChecksumUrl);
        Assert.Equal(new Uri("https://github.com/woori-doweb/ohmyzip/releases/tag/ohmypic-v0.2.10"), release.PageUrl);

        // The shared repository's "latest" release belongs to the other app; ohMypic reads the release list.
        Assert.Equal("https://api.github.com/repos/woori-doweb/ohmyzip/releases?per_page=50", AppUpdater.ReleasesApi.AbsoluteUri);
        var seen = Assert.Single(github.Seen);
        Assert.Equal(AppUpdater.ReleasesApi.AbsoluteUri, seen.Url.AbsoluteUri);
        Assert.DoesNotContain("/releases/latest", seen.Url.AbsoluteUri);
        Assert.Equal("ohMypic/0.2.0", seen.UserAgent);
        Assert.Equal("application/vnd.github+json", seen.Accept);
    }

    [Theory]
    [InlineData("0.2.10")]
    [InlineData("0.2.10.7")]
    [InlineData("0.3.0")]
    [InlineData("1.0.0")]
    public async Task OP_E23_Check_same_or_newer_installed_version_is_up_to_date(string installed)
    {
        var github = new FakeGitHub();
        github.List(github.Release("v9.9.9", "9.9.9", prerelease: false, assetPrefix: "ohmyZip"), github.Release(Tag, NewVer));
        var updater = NewUpdater(github);

        UpdateCheckResult result = await updater.CheckAsync(System.Version.Parse(installed), CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.Null(result.Release);
    }

    [Theory]
    [InlineData("not-found", UpdateError.NoRelease)]
    [InlineData("only-other-app-and-drafts", UpdateError.NoRelease)]
    [InlineData("forbidden", UpdateError.ServerBusy)]
    [InlineData("server-error", UpdateError.HttpError)]
    [InlineData("network", UpdateError.ConnectFailed)]
    [InlineData("timeout", UpdateError.Timeout)]
    [InlineData("not-json", UpdateError.InfoUnreadable)]
    [InlineData("object-not-array", UpdateError.InfoUnreadable)]
    [InlineData("list-redirected-elsewhere", UpdateError.UnsafeUrl)]
    [InlineData("no-checksum", UpdateError.PackageMissing)]
    [InlineData("package-still-uploading", UpdateError.PackageMissing)]
    [InlineData("http-asset", UpdateError.UnsafeUrl)]
    [InlineData("foreign-host-asset", UpdateError.UnsafeUrl)]
    [InlineData("package-too-large", UpdateError.TooLarge)]
    public async Task OP_E23_Check_problems_are_unavailable_without_exception(string problem, UpdateError expected)
    {
        var github = new FakeGitHub();
        switch (problem)
        {
            case "not-found": github.RespondToList(_ => new HttpResponseMessage(HttpStatusCode.NotFound)); break;
            case "only-other-app-and-drafts":
                github.List(github.Release("v0.3.0", "0.3.0", prerelease: false, assetPrefix: "ohmyZip"), github.Release("ohmypic-v0.3.0", "0.3.0", draft: true));
                break;
            case "forbidden": github.RespondToList(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)); break;
            case "server-error": github.RespondToList(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)); break;
            case "network": github.RespondToList(_ => throw new HttpRequestException("offline")); break;
            case "timeout": github.RespondToList(_ => throw new TaskCanceledException("timeout")); break;
            case "not-json": github.RespondToList(_ => FakeGitHub.Json("<html>rate limited</html>")); break;
            case "object-not-array": github.RespondToList(_ => FakeGitHub.Json("{\"tag_name\":\"ohmypic-v0.3.0\"}")); break;
            case "list-redirected-elsewhere":
                github.RespondToList(_ =>
                {
                    HttpResponseMessage response = FakeGitHub.Json("[]");
                    response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://evil.example/releases");
                    return response;
                });
                break;
            case "no-checksum": github.List(github.Release(Tag, NewVer, withChecksum: false)); break;
            case "package-still-uploading": github.List(github.Release(Tag, NewVer, packageState: "starter")); break;
            case "http-asset": github.List(github.Release(Tag, NewVer, urlBase: "http://github.com")); break;
            case "foreign-host-asset": github.List(github.Release(Tag, NewVer, urlBase: "https://example.com")); break;
            case "package-too-large": github.List(github.Release(Tag, NewVer, packageSize: AppUpdater.MaxPackageBytes + 1)); break;
            default: throw new ArgumentOutOfRangeException(nameof(problem));
        }
        var updater = NewUpdater(github);

        UpdateCheckResult result = await updater.CheckAsync(Current, CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.Unavailable, result.Status);
        Assert.Equal(expected, result.Error);
        Assert.Null(result.Release);
    }

    [Fact]
    public void OP_E23_Release_list_address_must_be_https_on_github()
    {
        using var http = new HttpClient(new FakeGitHub());
        Assert.Throws<ArgumentException>(() => new AppUpdater(http, temp.Combine("install"), temp.Combine("work"), new Uri("http://api.github.com/repos/x/y/releases")));
        Assert.Throws<ArgumentException>(() => new AppUpdater(http, temp.Combine("install"), temp.Combine("work"), new Uri("https://example.com/releases")));
    }

    // ───────────── download ─────────────

    [Fact]
    public async Task OP_E23_Download_verifies_sha256_and_stages_without_top_folder()
    {
        byte[] package = Package(NewVer);
        var github = new FakeGitHub();
        github.List(github.Release(Tag, NewVer, package: package));
        var updater = NewUpdater(github);
        UpdateRelease release = (await updater.CheckAsync(Current, CancellationToken.None)).Release!;
        var progress = new ListProgress();

        string staged = await updater.DownloadAsync(release, progress, CancellationToken.None);

        Assert.Equal(temp.Combine("work", "staged-0.2.10"), staged);
        Assert.Equal(new[] { "OhMyPic.Core.dll", "README.txt", "ko/ohMypic.resources.dll", "ohMypic.exe" }, FilesUnder(staged));
        Assert.Equal("new exe 0.2.10", File.ReadAllText(Path.Combine(staged, "ohMypic.exe")));
        Assert.Equal(new[] { staged }, Directory.GetFileSystemEntries(temp.Combine("work")));
        Assert.Equal(((long)package.Length, (long?)package.Length), progress.Items[^1]);
        Assert.Contains(github.Seen, s => s.Url == release.ChecksumUrl && s.UserAgent == "ohMypic/0.2.0");

        // A verified staging folder is reused without downloading again.
        int requests = github.Seen.Count;
        Assert.Equal(staged, await updater.DownloadAsync(release, null, CancellationToken.None));
        Assert.Equal(requests, github.Seen.Count);
    }

    [Theory]
    [InlineData("hash-mismatch", UpdateError.ChecksumMismatch)]
    [InlineData("name-mismatch", UpdateError.ChecksumWrongFile)]
    [InlineData("bad-format", UpdateError.ChecksumUnreadable)]
    [InlineData("checksum-not-found", UpdateError.HttpError)]
    public async Task OP_E23_Download_rejects_sha256_problems_and_leaves_nothing(string problem, UpdateError expected)
    {
        byte[] package = Package(NewVer);
        string hash = FakeGitHub.Sha256Hex(package);
        var github = new FakeGitHub();
        github.List(problem switch
        {
            "hash-mismatch" => github.Release(Tag, NewVer, package: package, checksumLine: $"{new string('0', 64)} *ohMypic-0.2.10-win-x64.zip\n"),
            "name-mismatch" => github.Release(Tag, NewVer, package: package, checksumLine: $"{hash} *ohMypic-0.2.9-win-x64.zip\n"),
            "bad-format" => github.Release(Tag, NewVer, package: package, checksumLine: "not a checksum"),
            "checksum-not-found" => github.Release(Tag, NewVer, package: package, checksumStatus: HttpStatusCode.NotFound),
            _ => throw new ArgumentOutOfRangeException(nameof(problem)),
        });
        var updater = NewUpdater(github);
        UpdateRelease release = (await updater.CheckAsync(Current, CancellationToken.None)).Release!;

        var error = await Assert.ThrowsAsync<UpdateException>(() => updater.DownloadAsync(release, null, CancellationToken.None));

        Assert.Equal(expected, error.Code);
        Assert.Empty(Directory.GetFileSystemEntries(temp.Combine("work")));
    }

    [Theory]
    [InlineData("parent-escape")]
    [InlineData("nested-escape")]
    [InlineData("backslash-escape")]
    [InlineData("absolute")]
    [InlineData("drive")]
    [InlineData("device-name")]
    public async Task OP_E23_Download_rejects_path_traversal_entries(string problem)
    {
        const string top = "ohMypic-0.2.10/";
        const string exe = top + "ohMypic.exe";
        byte[] package = problem switch
        {
            "parent-escape" => RawZip((exe, "exe"), ("../escaped.txt", "escaped")),
            "nested-escape" => RawZip((exe, "exe"), (top + "../../escaped.txt", "escaped")),
            "backslash-escape" => RawZip((exe, "exe"), ("ohMypic-0.2.10\\..\\..\\escaped.txt", "escaped")),
            "absolute" => RawZip(("/" + exe, "exe")),
            "drive" => RawZip((exe, "exe"), ("C:/escaped.txt", "escaped")),
            "device-name" => RawZip((exe, "exe"), (top + "CON.txt", "device")),
            _ => throw new ArgumentOutOfRangeException(nameof(problem)),
        };

        var error = await DownloadExpectingFailure(package);

        Assert.Equal(UpdateError.PackageUnsafePath, error.Code);
        Assert.False(File.Exists(temp.Combine("escaped.txt")));
        Assert.False(File.Exists(temp.Combine("work", "escaped.txt")));
        Assert.Empty(Directory.GetFileSystemEntries(temp.Combine("work")));
    }

    [Theory]
    [InlineData("two-top-folders", UpdateError.PackageBadLayout)]
    [InlineData("top-level-file", UpdateError.PackageBadLayout)]
    [InlineData("no-exe", UpdateError.PackageNoExecutable)]
    [InlineData("exe-not-directly-under-top", UpdateError.PackageNoExecutable)]
    [InlineData("not-a-zip", UpdateError.PackageUnreadable)]
    public async Task OP_E23_Download_rejects_malformed_package(string problem, UpdateError expected)
    {
        const string top = "ohMypic-0.2.10/";
        byte[] package = problem switch
        {
            "two-top-folders" => RawZip((top + "ohMypic.exe", "exe"), ("other/readme.txt", "other")),
            "top-level-file" => RawZip((top + "ohMypic.exe", "exe"), ("readme.txt", "loose")),
            "no-exe" => RawZip((top + "OhMyPic.Core.dll", "dll")),
            "exe-not-directly-under-top" => RawZip((top + "bin/ohMypic.exe", "exe")),
            "not-a-zip" => Encoding.UTF8.GetBytes("this is not a zip file"),
            _ => throw new ArgumentOutOfRangeException(nameof(problem)),
        };

        var error = await DownloadExpectingFailure(package);

        Assert.Equal(expected, error.Code);
        Assert.Empty(Directory.GetFileSystemEntries(temp.Combine("work")));
    }

    [Theory]
    [InlineData("https://evil.example/ohMypic.zip", false)]
    [InlineData("http://objects.githubusercontent.com/ohMypic.zip", false)]
    [InlineData("https://objects.githubusercontent.com/ohMypic.zip", true)]
    [InlineData("https://release-assets.githubusercontent.com/ohMypic.zip", true)]
    public async Task OP_E23_Download_accepts_only_redirects_to_github_hosts(string finalUrl, bool accepted)
    {
        var github = new FakeGitHub();
        github.List(github.Release(Tag, NewVer, packageRedirect: new Uri(finalUrl)));
        var updater = NewUpdater(github);
        UpdateRelease release = (await updater.CheckAsync(Current, CancellationToken.None)).Release!;

        if (accepted)
        {
            string staged = await updater.DownloadAsync(release, null, CancellationToken.None);
            Assert.True(File.Exists(Path.Combine(staged, "ohMypic.exe")));
            return;
        }
        var error = await Assert.ThrowsAsync<UpdateException>(() => updater.DownloadAsync(release, null, CancellationToken.None));
        Assert.Equal(UpdateError.UnsafeUrl, error.Code);
        Assert.Empty(Directory.GetFileSystemEntries(temp.Combine("work")));
    }

    // ───────────── install and cleanup ─────────────

    [Fact]
    public void OP_E23_Apply_swaps_files_and_next_start_removes_leftovers()
    {
        string install = temp.Combine("install");
        Write(install, "ohMypic.exe", "old exe");
        Write(install, "OhMyPic.Core.dll", "old dll");
        Write(install, "user-notes.txt", "keep");
        string staged = temp.Combine("work", "staged-0.2.10");
        Write(staged, "ohMypic.exe", "new exe");
        Write(staged, "OhMyPic.Core.dll", "new dll");
        Write(staged, "ko/ohMypic.resources.dll", "new ko");
        var updater = NewUpdater(new FakeGitHub());

        updater.Apply(staged);

        Assert.Equal("new exe", Read(install, "ohMypic.exe"));
        Assert.Equal("new dll", Read(install, "OhMyPic.Core.dll"));
        Assert.Equal("new ko", Read(install, "ko/ohMypic.resources.dll"));
        Assert.Equal("old exe", Read(install, "ohMypic.exe.ohmypic-old"));
        Assert.Equal("old dll", Read(install, "OhMyPic.Core.dll.ohmypic-old"));
        Assert.Equal("keep", Read(install, "user-notes.txt"));

        // The old exe is still running (cannot be deleted): cleanup skips it and removes the rest.
        string oldExe = Path.Combine(install, "ohMypic.exe.ohmypic-old");
        using (File.Open(oldExe, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            AppUpdater.CleanupLeftovers(install);
            Assert.True(File.Exists(oldExe));
            Assert.False(File.Exists(Path.Combine(install, "OhMyPic.Core.dll.ohmypic-old")));
        }

        // Next start: the process has exited, so the last leftover goes too.
        AppUpdater.CleanupLeftovers(install);

        Assert.Equal(new[] { "OhMyPic.Core.dll", "ko/ohMypic.resources.dll", "ohMypic.exe", "user-notes.txt" }, FilesUnder(install));
    }

    [Fact]
    public void OP_E23_Apply_uses_unique_leftover_name_when_previous_one_is_locked()
    {
        string install = temp.Combine("install");
        Write(install, "ohMypic.exe", "old exe");
        Write(install, "ohMypic.exe.ohmypic-old", "still running from the last update");
        string staged = temp.Combine("work", "staged-0.2.10");
        Write(staged, "ohMypic.exe", "new exe");
        var updater = NewUpdater(new FakeGitHub());

        using (File.Open(Path.Combine(install, "ohMypic.exe.ohmypic-old"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            updater.Apply(staged);

            Assert.Equal("new exe", Read(install, "ohMypic.exe"));
            string moved = Assert.Single(Directory.GetFiles(install, "ohMypic.exe.ohmypic-old-*"));
            Assert.Equal("old exe", File.ReadAllText(moved));
        }

        AppUpdater.CleanupLeftovers(install);

        Assert.Equal(new[] { "ohMypic.exe" }, FilesUnder(install));
    }

    [Fact]
    public void OP_E23_Apply_failure_rolls_back_every_file()
    {
        string install = temp.Combine("install");
        Write(install, "ohMypic.exe", "old exe");
        Directory.CreateDirectory(Path.Combine(install, "zz.txt"));
        string staged = temp.Combine("work", "staged-0.2.10");
        Write(staged, "ohMypic.exe", "new exe");
        Write(staged, "sub/new.txt", "new");
        Write(staged, "zz.txt", "blocked by a folder");
        List<string> before = Snapshot(install);
        var updater = NewUpdater(new FakeGitHub());

        var error = Assert.Throws<UpdateException>(() => updater.Apply(staged));

        Assert.Equal(UpdateError.TargetIsFolder, error.Code);
        Assert.Equal("zz.txt", error.Detail);
        Assert.Equal(before, Snapshot(install));
        Assert.False(Directory.Exists(Path.Combine(install, "sub")));
    }

    [Fact]
    public void OP_E23_CleanupLeftovers_removes_only_leftovers()
    {
        string install = temp.Combine("install");
        Write(install, "a.dll.ohmypic-old", "x");
        Write(install, "ko/b.dll.ohmypic-old", "x");
        Write(install, "c.exe.ohmypic-old-0123456789abcdef", "x");
        Write(install, "D.EXE.OHMYPIC-OLD", "x");
        Write(install, "readonly.dll.ohmypic-old", "x");
        File.SetAttributes(Path.Combine(install, "readonly.dll.ohmypic-old"), FileAttributes.ReadOnly);
        Write(install, "keep.txt", "keep");
        Write(install, "notes.ohmypic-old.txt", "keep");
        Write(install, "ohmypic-old.txt", "keep");
        Write(install, "ko/ohMypic.resources.dll", "keep");

        AppUpdater.CleanupLeftovers(install);
        AppUpdater.CleanupLeftovers(temp.Combine("does-not-exist"));

        Assert.Equal(new[] { "keep.txt", "ko/ohMypic.resources.dll", "notes.ohmypic-old.txt", "ohmypic-old.txt" }, FilesUnder(install));
    }

    [Fact]
    public void OP_E23_CleanupWorkFolder_keeps_only_newer_stages_and_recent_downloads()
    {
        string work = temp.Combine("work");
        foreach (string name in new[] { "staged-0.1.0", "staged-0.2.0", "staged-0.3.0", "staged-broken", "download-0.3.0-new", "download-0.3.0-old", "keep" })
            Write(work, name + "/file.txt", "x");
        Directory.SetLastWriteTimeUtc(Path.Combine(work, "download-0.3.0-old"), DateTime.UtcNow - TimeSpan.FromDays(3));

        AppUpdater.CleanupWorkFolder(work, new Version(0, 2, 0));
        AppUpdater.CleanupWorkFolder(temp.Combine("does-not-exist"), new Version(0, 2, 0));

        Assert.Equal(
            new[] { "download-0.3.0-new", "keep", "staged-0.3.0" },
            Directory.GetDirectories(work).Select(d => Path.GetFileName(d)!).OrderBy(n => n, StringComparer.Ordinal));
    }

    // ───────────── versions and setting ─────────────

    [Theory]
    [InlineData("0.1.0-dev", "0.1.0")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.2.3+abcdef", "1.2.3")]
    [InlineData("1.2.3.4", "1.2.3")]
    [InlineData(" 10.20.30 ", "10.20.30")]
    [InlineData("1.2", null)]
    [InlineData("v1.2.3", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void OP_E23_ParseReleaseVersion_reads_three_leading_numbers(string? text, string? expected)
    {
        Assert.Equal(expected is null ? null : System.Version.Parse(expected), AppUpdater.ParseReleaseVersion(text));
    }

    [Fact]
    public void OP_E23_InstalledVersion_reads_the_product_version_of_the_app_assembly()
    {
        string install = temp.Combine("install");
        Directory.CreateDirectory(install);
        Assert.Null(AppUpdater.InstalledVersion(install));

        File.Copy(typeof(AppUpdater).Assembly.Location, Path.Combine(install, AppUpdater.MainAssembly));

        Assert.Equal(AppUpdater.VersionOf(typeof(AppUpdater).Assembly), AppUpdater.InstalledVersion(install));
    }

    [Fact]
    public void OP_E23_CheckForUpdates_setting_defaults_on_and_round_trips()
    {
        Assert.True(new AppSettings().CheckForUpdates);
        string path = temp.Combine("cfg", "settings.json");

        new AppSettings { CheckForUpdates = false }.Save(path);
        Assert.False(AppSettings.Load(path).CheckForUpdates);

        // A settings file written before the option existed keeps updates on.
        File.WriteAllText(path, "{ \"Language\": \"en\" }");
        Assert.True(AppSettings.Load(path).CheckForUpdates);
    }

    // ───────────── helpers ─────────────

    private AppUpdater NewUpdater(FakeGitHub github) =>
        new(new HttpClient(github), temp.Combine("install"), temp.Combine("work"));

    private async Task<UpdateException> DownloadExpectingFailure(byte[] package)
    {
        var github = new FakeGitHub();
        github.List(github.Release(Tag, NewVer, package: package));
        var updater = NewUpdater(github);
        UpdateRelease release = (await updater.CheckAsync(Current, CancellationToken.None)).Release!;
        return await Assert.ThrowsAsync<UpdateException>(() => updater.DownloadAsync(release, null, CancellationToken.None));
    }

    /// <summary>Package in the release layout: one top folder "ohMypic-&lt;version&gt;/" with the app files.</summary>
    private static byte[] Package(string version) =>
        RawZip(
            ($"ohMypic-{version}/", ""),
            ($"ohMypic-{version}/ohMypic.exe", $"new exe {version}"),
            ($"ohMypic-{version}/OhMyPic.Core.dll", $"new dll {version}"),
            ($"ohMypic-{version}/ko/ohMypic.resources.dll", $"new ko {version}"),
            ($"ohMypic-{version}/README.txt", $"readme {version}"));

    /// <summary>ZIP with arbitrary entry names, built in memory. A name ending in '/' is a folder entry.</summary>
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
                s.Write(Encoding.UTF8.GetBytes(text));
            }
        }
        return stream.ToArray();
    }

    private static void Write(string root, string relative, string text)
    {
        string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static string Read(string root, string relative) =>
        File.ReadAllText(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>All files below a folder as '/'-separated relative paths, ordinal order.</summary>
    private static List<string> FilesUnder(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

    /// <summary>Every folder and file (with content hash) below a folder, for before/after comparison.</summary>
    private static List<string> Snapshot(string root) =>
        Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
            .Select(d => "dir " + Path.GetRelativePath(root, d))
            .Concat(FilesUnder(root).Select(f => $"file {f} {Util.Sha256(Path.Combine(root, f))}"))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

    private sealed class ListProgress : IProgress<(long Done, long? Total)>
    {
        public List<(long Done, long? Total)> Items { get; } = [];

        public void Report((long Done, long? Total) value) => Items.Add(value);
    }

    /// <summary>Answers the GitHub release list and asset downloads from memory and records every request.</summary>
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

        public static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        public static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Bytes(byte[] data) =>
            new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };

        public void RespondToList(Func<HttpRequestMessage, HttpResponseMessage> respond) => routes[AppUpdater.ReleasesApi.AbsoluteUri] = respond;

        /// <summary>Publishes the release list (newest first does not matter: the updater compares versions).</summary>
        public void List(params object[] releases)
        {
            string json = JsonSerializer.Serialize(releases);
            RespondToList(_ => Json(json));
        }

        /// <summary>One release object for the list; its package and .sha256 assets are served at their download URLs.</summary>
        public object Release(
            string tag,
            string version,
            bool draft = false,
            bool prerelease = true,
            byte[]? package = null,
            string? checksumLine = null,
            HttpStatusCode checksumStatus = HttpStatusCode.OK,
            bool withChecksum = true,
            string packageState = "uploaded",
            string urlBase = "https://github.com",
            long? packageSize = null,
            Uri? packageRedirect = null,
            string assetPrefix = "ohMypic")
        {
            byte[] packageBytes = package ?? Package(version);
            string packageName = $"{assetPrefix}-{version}-win-x64.zip";
            string checksumName = packageName + ".sha256";
            byte[] checksum = Encoding.UTF8.GetBytes(checksumLine ?? $"{Sha256Hex(packageBytes)} *{packageName}\n");
            var packageUrl = new Uri($"{urlBase}/{AppUpdater.Repository}/releases/download/{tag}/{packageName}");
            var checksumUrl = new Uri($"{urlBase}/{AppUpdater.Repository}/releases/download/{tag}/{checksumName}");

            var assets = new List<object>
            {
                new { name = packageName, state = packageState, size = packageSize ?? packageBytes.Length, browser_download_url = packageUrl.AbsoluteUri },
            };
            routes[packageUrl.AbsoluteUri] = _ =>
            {
                HttpResponseMessage response = Bytes(packageBytes);
                if (packageRedirect is not null)
                    response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, packageRedirect);
                return response;
            };
            if (withChecksum)
            {
                assets.Add(new { name = checksumName, state = "uploaded", size = (long)checksum.Length, browser_download_url = checksumUrl.AbsoluteUri });
                routes[checksumUrl.AbsoluteUri] = _ => checksumStatus == HttpStatusCode.OK ? Bytes(checksum) : new HttpResponseMessage(checksumStatus);
            }
            return new
            {
                tag_name = tag,
                name = tag,
                body = Notes,
                html_url = $"https://github.com/{AppUpdater.Repository}/releases/tag/{tag}",
                draft,
                prerelease,
                assets,
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri url = request.RequestUri!;
            lock (seen)
                seen.Add((url, request.Headers.UserAgent.ToString(), request.Headers.Accept.ToString()));
            HttpResponseMessage response = routes.TryGetValue(url.AbsoluteUri, out var respond)
                ? respond(request)
                : new HttpResponseMessage(HttpStatusCode.NotFound);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }
}
