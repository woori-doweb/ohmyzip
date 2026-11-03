namespace OhMyZip.Core;

/// <summary>같은 폴더의 임시 파일에 쓰고 마지막에 한 번에 바꿔 넣는 도우미. 실패·취소 때 원본은 그대로 남는다.</summary>
internal static class AtomicFile
{
    /// <summary>target 과 같은 폴더의 겹치지 않는 임시 경로(같은 볼륨이라 이동이 원자적).</summary>
    public static string TempSibling(string target, string suffix = ".oztmp")
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(target))!;
        return Path.Join(dir, $"~oz{Guid.NewGuid():N}{suffix}");
    }

    /// <summary>임시 파일을 대상 자리로 옮긴다. overwrite=false 이면 대상이 생겨 있을 때 IOException.</summary>
    public static void Commit(string temp, string target, bool overwrite) => File.Move(temp, target, overwrite);

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
