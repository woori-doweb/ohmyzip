namespace OhMyZip.Core;

/// <summary>압축파일 항목 이름을 다루는 공용 규칙. 해제 경로 검증(Zip Slip·절대경로·장치 이름)은 모두 여기서 한다.</summary>
internal static class SafePaths
{
    private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>표시용 경로: '/' 구분, 앞뒤 '/' 없음. 검증은 하지 않는다(목록에는 위험한 이름도 그대로 보여 준다).</summary>
    public static string ToDisplayPath(string rawName) => rawName.Replace('\\', '/').Trim('/');

    /// <summary>항목 이름이 폴더 표시('/' 또는 '\' 로 끝남)인지.</summary>
    public static bool EndsWithSeparator(string rawName) => rawName.EndsWith('/') || rawName.EndsWith('\\');

    /// <summary>표시 경로의 상위 폴더 경로들(가까운 것부터). "a/b/c" → "a/b", "a".</summary>
    public static IEnumerable<string> Parents(string displayPath)
    {
        for (int i = displayPath.LastIndexOf('/'); i > 0; i = displayPath.LastIndexOf('/', i - 1))
            yield return displayPath[..i];
    }

    public static string ParentOf(string displayPath)
    {
        int i = displayPath.LastIndexOf('/');
        return i < 0 ? "" : displayPath[..i];
    }

    /// <summary>path 가 folder 자신이거나 그 하위인지(대소문자 구분).</summary>
    public static bool IsSelfOrDescendant(string path, string folder) =>
        folder.Length == 0 || path == folder || (path.Length > folder.Length && path[folder.Length] == '/' && path.StartsWith(folder, StringComparison.Ordinal));

    /// <summary>
    /// 해제용 상대 경로를 검증한다. 루트 기준 절대경로·드라이브 문자·':'·'..'·장치 이름·Windows 가 바꿔 버리는 이름(끝 '.'/공백)을 거부한다.
    /// 성공하면 '/' 구분 상대경로를 돌려준다.
    /// </summary>
    public static bool TryGetSafeRelative(string rawName, out string relative, out string error)
    {
        relative = "";
        error = "";
        string p = rawName.Replace('\\', '/');
        if (p.StartsWith('/'))
        {
            error = "절대 경로 항목";
            return false;
        }
        if (p.Contains(':'))
        {
            error = "드라이브 문자 또는 ':' 가 든 경로";
            return false;
        }
        var parts = new List<string>();
        foreach (string seg in p.Split('/'))
        {
            if (seg.Length == 0 || seg == ".")
                continue;
            if (seg == "..")
            {
                error = "상위 폴더('..')로 나가는 경로";
                return false;
            }
            if (seg.IndexOfAny(InvalidNameChars) >= 0)
            {
                error = "파일 이름에 쓸 수 없는 문자";
                return false;
            }
            if (seg.EndsWith('.') || seg.EndsWith(' '))
            {
                error = "끝이 '.' 또는 공백인 이름";
                return false;
            }
            string stem = seg.Split('.')[0].TrimEnd(' ');
            if (ReservedDeviceNames.Contains(stem))
            {
                error = "Windows 장치 이름";
                return false;
            }
            parts.Add(seg);
        }
        if (parts.Count == 0)
        {
            error = "빈 경로";
            return false;
        }
        relative = string.Join('/', parts);
        return true;
    }

    /// <summary>root 아래의 전체 경로를 만든다. 결과가 root 밖이면 null.</summary>
    public static string? ResolveUnder(string root, string relative)
    {
        string full = Path.GetFullPath(Path.Join(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        return IsStrictlyUnder(root, full) ? full : null;
    }

    /// <summary>full 이 root 의 진짜 하위인지(root 자신은 아님). 둘 다 정규화된 전체 경로여야 한다.</summary>
    public static bool IsStrictlyUnder(string root, string full)
    {
        string r = Path.TrimEndingDirectorySeparator(root);
        return full.Length > r.Length + 1
            && full.StartsWith(r, StringComparison.OrdinalIgnoreCase)
            && (full[r.Length] == Path.DirectorySeparatorChar || full[r.Length] == Path.AltDirectorySeparatorChar);
    }

    /// <summary>압축파일 안 새 이름(한 단계 이름) 검증. 오류 문구 또는 null.</summary>
    public static string? ValidateLeafName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "이름이 비어 있습니다";
        if (name.Contains('/') || name.Contains('\\'))
            return "이름에 '/' 나 '\\' 를 쓸 수 없습니다";
        if (!TryGetSafeRelative(name, out _, out string error))
            return error;
        return null;
    }

    /// <summary>"이름 (2).확장자" 식으로 비어 있는 경로를 찾는다.</summary>
    public static string UniqueSibling(string full)
    {
        string dir = Path.GetDirectoryName(full)!;
        string stem = Path.GetFileNameWithoutExtension(full);
        string ext = Path.GetExtension(full);
        for (int n = 2; ; n++)
        {
            string candidate = Path.Join(dir, $"{stem} ({n}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }
    }
}
