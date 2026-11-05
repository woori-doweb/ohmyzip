using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using OhMyPic.App.Theme;

namespace OhMyPic.App;

internal static class NativeMethods
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Matches the standard Windows title bar to the app theme (dark/light). Window buttons stay the Windows ones.
    /// Safe to call before the handle exists (does nothing then); call it from SourceInitialized.
    /// </summary>
    internal static void ApplyDarkTitleBar(Window window)
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

    private const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
    private const uint SEE_MASK_NOASYNC = 0x00000100;
    private const uint SEE_MASK_FLAG_NO_UI = 0x00000400;
    private const int SW_SHOW = 5;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpVerb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIconOrMonitor;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);

    /// <summary>
    /// Opens the Windows shell properties sheet of a file ("properties" verb with SEE_MASK_INVOKEIDLIST).
    /// Returns null on success, otherwise the Win32 error text.
    /// </summary>
    internal static string? ShowFileProperties(Window owner, string path)
    {
        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask = SEE_MASK_INVOKEIDLIST | SEE_MASK_NOASYNC | SEE_MASK_FLAG_NO_UI,
            hwnd = new WindowInteropHelper(owner).Handle,
            lpVerb = "properties",
            lpFile = path,
            nShow = SW_SHOW,
        };
        if (ShellExecuteEx(ref info)) return null;
        return new Win32Exception(Marshal.GetLastWin32Error()).Message;
    }
}
