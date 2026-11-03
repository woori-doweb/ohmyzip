using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using OhMyZip.App.Theme;

namespace OhMyZip.App;

internal static class NativeMethods
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>표준 Windows 제목 표시줄을 앱 테마(다크/라이트)에 맞춘다. 창 제어 버튼은 Windows 것을 그대로 쓴다.</summary>
    public static void ApplyDarkTitleBar(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int on = ThemeManager.IsDark && !ThemeManager.IsHighContrast ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    /// <summary>
    /// 파일 하나를 휴지통으로 보낸다(FOF_ALLOWUNDO). 확인은 호출 쪽이 이미 받았으므로 셔 확인창을 띄우지 않는다.
    /// 성공하면 null, 실패하면 오류 문장.
    /// </summary>
    public static string? MoveToRecycleBin(Window owner, string path)
    {
        var op = new SHFILEOPSTRUCT
        {
            hwnd = new WindowInteropHelper(owner).Handle,
            wFunc = FO_DELETE,
            pFrom = path + "\0\0", // 이중 NUL 끝 목록
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        int code = SHFileOperation(ref op);
        if (code != 0) return $"휴지통으로 보내지 못했습니다(오류 코드 0x{code:X}).";
        if (op.fAnyOperationsAborted) return "휴지통으로 보내기가 중단되었습니다.";
        return System.IO.File.Exists(path) ? "파일이 아직 남아 있습니다." : null;
    }
}
