using System.IO;
using System.Runtime.InteropServices;

namespace OhMyZip.App.Models;

/// <summary>Windows 셸이 알려 주는 파일 종류 이름("텍스트 문서" 등). 실제 파일 없이 확장자로만 묻는다. UI 스레드 전용.</summary>
public static class FileTypeNames
{
    public const string Folder = "파일 폴더";
    private static readonly Dictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string ForFileName(string name)
    {
        var ext = Path.GetExtension(name);
        if (Cache.TryGetValue(ext, out var cached)) return cached;
        string result = Query(string.IsNullOrEmpty(ext) ? "file" : "file" + ext) ?? "";
        if (string.IsNullOrWhiteSpace(result))
            result = string.IsNullOrEmpty(ext) ? "파일" : $"{ext.TrimStart('.').ToUpperInvariant()} 파일";
        Cache[ext] = result;
        return result;
    }

    private static string? Query(string fakeName)
    {
        try
        {
            var info = new SHFILEINFO();
            var ok = SHGetFileInfo(fakeName, FILE_ATTRIBUTE_NORMAL, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_TYPENAME | SHGFI_USEFILEATTRIBUTES);
            return ok == IntPtr.Zero ? null : info.szTypeName;
        }
        catch
        {
            return null;
        }
    }

    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const uint SHGFI_TYPENAME = 0x400;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x10;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);
}
