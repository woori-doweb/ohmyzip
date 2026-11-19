using System.ComponentModel;
using System.Runtime.InteropServices;
using OhMyPic.Core.Editing;
using static OhMyPic.App.NativeMethods;

namespace OhMyPic.App;

/// <summary>Desktop capture for the Print Screen agent (GDI, physical pixels: the app manifest is PerMonitorV2).</summary>
internal static class ScreenCapture
{
    /// <summary>
    /// Copies the whole monitor that contains the mouse cursor. The pixels are read straight into a byte array
    /// (top-down 32 bpp DIB) instead of an interop BitmapSource: the result carries no dispatcher affinity, so the hook
    /// thread can capture and hand it to the UI thread, and undefined desktop alpha is never treated as premultiplied.
    /// Every pixel's alpha is set to 255.
    /// </summary>
    internal static RasterImage CaptureMonitorUnderCursor()
    {
        if (!GetCursorPos(out var cursor)) throw new Win32Exception(Marshal.GetLastWin32Error());
        IntPtr monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) throw new Win32Exception();
        RECT r = info.rcMonitor;
        int width = r.Right - r.Left, height = r.Bottom - r.Top;
        if (width < 1 || height < 1) throw new Win32Exception();

        IntPtr screenDc = IntPtr.Zero, memoryDc = IntPtr.Zero, bitmap = IntPtr.Zero;
        try
        {
            screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) throw new Win32Exception();
            memoryDc = CreateCompatibleDC(screenDc);
            if (memoryDc == IntPtr.Zero) throw new Win32Exception();
            bitmap = CreateCompatibleBitmap(screenDc, width, height);
            if (bitmap == IntPtr.Zero) throw new Win32Exception();

            IntPtr previous = SelectObject(memoryDc, bitmap);
            // CAPTUREBLT includes layered (translucent) windows, which are part of what the user sees.
            bool copied = BitBlt(memoryDc, 0, 0, width, height, screenDc, r.Left, r.Top, SRCCOPY | CAPTUREBLT);
            int error = Marshal.GetLastWin32Error();
            // GetDIBits needs the bitmap deselected.
            SelectObject(memoryDc, previous);
            if (!copied) throw new Win32Exception(error);

            var header = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // negative = top-down rows, the RasterImage order
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB,
            };
            byte[] pixels = new byte[(long)width * height * 4];
            if (GetDIBits(screenDc, bitmap, 0, (uint)height, pixels, ref header, DIB_RGB_COLORS) != height)
                throw new Win32Exception();
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            return new RasterImage(width, height, pixels);
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
            if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
