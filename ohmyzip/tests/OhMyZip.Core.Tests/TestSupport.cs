using System.IO.Compression;
using System.Security.Cryptography;

namespace OhMyZip.Core.Tests;

/// <summary>외부 압축 프로그램으로 만든 읽기 전용 fixtures 위치와 기대 해시.</summary>
internal static class Fixtures
{
    public const string KoreanName = "한글이름.txt";
    public const string KoreanHash = "0D533A0C9CC35E7FB3644A8AB246139E93B13749C7F7A53EC7CD9783DC4637AD";
    public const string ReadmeHash = "6573767E97332F4B5AC251B85BA2B8EDECE55542D80BFC232EA52592CB7C1D1F";
    public const string NotesHash = "9312D317A08DE22FE7618BDA3D8F93951D2DF0B77992A20E169A907B8D157996";

    /// <summary>상대경로('/') → SHA-256. fixtures 의 input 원본 파일 해시.</summary>
    public static readonly IReadOnlyDictionary<string, string> ExpectedHashes = new Dictionary<string, string>
    {
        [KoreanName] = KoreanHash,
        ["readme.txt"] = ReadmeHash,
        ["nested/notes.txt"] = NotesHash,
    };

    private static readonly Lazy<string> Dir = new(Locate);

    public static string Directory => Dir.Value;

    private static string Locate()
    {
        string? env = Environment.GetEnvironmentVariable("OHMYZIP_FIXTURES");
        if (!string.IsNullOrEmpty(env) && System.IO.Directory.Exists(env))
            return env;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            string candidate = Path.Combine(d.FullName, "data", "ohmyzip", "fixtures");
            if (System.IO.Directory.Exists(candidate))
                return candidate;
        }
        throw new DirectoryNotFoundException("fixtures 를 찾지 못했습니다. OHMYZIP_FIXTURES 환경 변수를 지정하세요.");
    }

    /// <summary>fixture 파일(또는 폴더)을 임시 폴더로 복사해 그 경로를 돌려준다. 원본은 건드리지 않는다.</summary>
    public static string CopyTo(string name, TempDir temp)
    {
        string source = Path.Combine(Directory, name);
        string target = Path.Combine(temp.Path, Path.GetFileName(name));
        if (File.Exists(source))
            File.Copy(source, target);
        else
            CopyDirectory(source, target);
        return target;
    }

    private static void CopyDirectory(string source, string target)
    {
        System.IO.Directory.CreateDirectory(target);
        foreach (string dir in System.IO.Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
        foreach (string file in System.IO.Directory.GetFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
    }
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ohmyzip-tests", Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Progress&lt;T&gt; 는 비동기로 넘기므로, 시험에서는 그 자리에서 바로 부르는 진행 보고를 쓴다.</summary>
internal sealed class SyncProgress(Action<ProgressInfo> onReport) : IProgress<ProgressInfo>
{
    public void Report(ProgressInfo value) => onReport(value);
}

internal static class Util
{
    public static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>폴더 아래 모든 파일 → 상대경로('/') 목록.</summary>
    public static List<string> FilesUnder(string root) =>
        Directory.Exists(root)
            ? Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList()
            : [];

    public static void AssertExpectedHashes(string root)
    {
        foreach (var (relative, hash) in Fixtures.ExpectedHashes)
        {
            string file = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(file), $"해제 결과에 {relative} 가 없습니다");
            Assert.Equal(hash, Sha256(file));
        }
    }

    /// <summary>임의 항목 이름·내용으로 ZIP 을 직접 만든다(위험한 이름 시험용).</summary>
    public static void WriteZip(string path, params (string Name, byte[] Data)[] entries) =>
        WriteZip(path, CompressionLevel.Optimal, entries.Select(e => (e.Name, e.Data, 0)).ToArray());

    public static void WriteZip(string path, CompressionLevel level, (string Name, byte[] Data, int ExternalAttributes)[] entries)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, data, attrs) in entries)
        {
            var entry = zip.CreateEntry(name, level);
            entry.ExternalAttributes = attrs;
            using var s = entry.Open();
            s.Write(data);
        }
    }

    public static byte[] Text(string s) => System.Text.Encoding.UTF8.GetBytes(s);
}
