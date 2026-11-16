using System.Runtime.Versioning;
using System.Security;
using System.Text;

namespace OhMyZip.Core;

/// <summary>
/// 탐색기 메뉴 처리기(ShellCommand.dll)가 넘기는 선택 목록 파일(<c>--list "목록 파일"</c>)의 해석과 정리. UI 의존 없음.
/// 목록 파일: UTF-8, 한 줄에 경로 하나. 처리기는 <see cref="DefaultDirectory"/> 아래 &lt;GUID&gt;.txt 로 쓴다.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SelectionList
{
    /// <summary>처리기가 목록 파일을 쓰는 폴더: %LOCALAPPDATA%\ohmyZip\selections. 만들지는 않는다.</summary>
    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FileAssociations.AppName, "selections");

    /// <summary>
    /// 목록 파일을 읽어 지금 있는 파일·폴더의 전체 경로만 파일 순서대로 돌려준다(앞뒤 공백 제거, 빈 줄·상대 경로·없는 경로는 건너뜀).
    /// 읽은 뒤에는 비어 있어도 목록 파일을 지운다(지우지 못하면 무시, 남은 것은 <see cref="CleanupStale"/> 가 정리한다).
    /// 목록 파일이 없으면 빈 목록. 그 밖의 읽기 오류는 그대로 던진다.
    /// </summary>
    public static IReadOnlyList<string> Read(string listPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listPath);
        string[] lines;
        try
        {
            lines = File.ReadAllLines(listPath, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return [];
        }
        finally
        {
            TryDelete(listPath);
        }

        var paths = new List<string>();
        foreach (string line in lines)
        {
            string path = line.Trim();
            if (path.Length == 0 || !Path.IsPathFullyQualified(path))
                continue;
            if (File.Exists(path) || Directory.Exists(path))
                paths.Add(path);
        }
        return paths;
    }

    /// <summary>폴더 안에서 마지막으로 쓴 시각이 <paramref name="maxAge"/> 보다 오래된 *.txt 를 지운다. 폴더가 없거나 지우지 못해도 조용히 넘어간다.</summary>
    public static void CleanupStale(string selectionsDir, TimeSpan maxAge, DateTime nowUtc)
    {
        DateTime limit = nowUtc - maxAge;
        IEnumerable<string> files;
        try
        {
            if (string.IsNullOrWhiteSpace(selectionsDir) || !Directory.Exists(selectionsDir))
                return;
            files = Directory.EnumerateFiles(selectionsDir, "*.txt").ToList();
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            return;
        }

        foreach (string file in files)
        {
            try
            {
                if (string.Equals(Path.GetExtension(file), ".txt", StringComparison.OrdinalIgnoreCase)
                    && File.GetLastWriteTimeUtc(file) < limit)
                    File.Delete(file);
            }
            catch (Exception ex) when (IsFileError(ex))
            {
            }
        }
    }

    /// <summary>
    /// '「이름.zip」으로 압축' 의 대상 경로. 위치는 첫 항목의 부모 폴더, 이름은 항목이 하나면 그 이름(파일은 확장자 제외),
    /// 여럿이면 부모 폴더 이름. 같은 이름의 파일·폴더가 있으면 「이름 (2).zip」, 「이름 (3).zip」….
    /// 항목들의 부모 폴더가 다르거나 이름을 만들 수 없으면(드라이브 루트 등) null. 빈 목록은 <see cref="ArgumentException"/>.
    /// </summary>
    public static string? CompressTargetPath(IReadOnlyList<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
            throw new ArgumentException(null, nameof(items));

        string? parent = null;
        foreach (string item in items)
        {
            string? itemParent = ParentOf(item);
            if (itemParent is null)
                return null;
            if (parent is null)
                parent = itemParent;
            else if (!string.Equals(Path.TrimEndingDirectorySeparator(parent), Path.TrimEndingDirectorySeparator(itemParent), StringComparison.OrdinalIgnoreCase))
                return null;
        }

        string name;
        if (items.Count == 1)
        {
            string item = Path.TrimEndingDirectorySeparator(Path.GetFullPath(items[0]));
            name = Path.GetFileName(item);
            if (!Directory.Exists(item) && Path.GetFileNameWithoutExtension(item) is { Length: > 0 } stem)
                name = stem;
        }
        else
        {
            name = Path.GetFileName(Path.TrimEndingDirectorySeparator(parent!));
        }
        if (name.Length == 0)
            return null;

        string candidate = Path.Combine(parent!, name + ".zip");
        for (int n = 2; File.Exists(candidate) || Directory.Exists(candidate); n++)
            candidate = Path.Combine(parent!, $"{name} ({n}).zip");
        return candidate;
    }

    /// <summary>항목의 부모 폴더 전체 경로. 드라이브 루트처럼 부모가 없으면 null.</summary>
    private static string? ParentOf(string item)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(item);
        return Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(item)));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (IsFileError(ex))
        {
        }
    }

    private static bool IsFileError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or SecurityException;
}
