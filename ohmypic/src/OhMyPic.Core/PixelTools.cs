using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OhMyPic.Core;

/// <summary>Small pixel-format helpers shared by the loader, transforms and encoder.</summary>
internal static class PixelTools
{
    private static readonly PixelFormat[] AlphaFormats =
    [
        PixelFormats.Bgra32, PixelFormats.Pbgra32, PixelFormats.Rgba64, PixelFormats.Prgba64,
        PixelFormats.Rgba128Float, PixelFormats.Prgba128Float,
    ];

    private static readonly PixelFormat[] IndexedFormats =
        [PixelFormats.Indexed1, PixelFormats.Indexed2, PixelFormats.Indexed4, PixelFormats.Indexed8];

    public static bool IsIndexed(PixelFormat format) => IndexedFormats.Contains(format);

    public static bool HasAlpha(PixelFormat format, BitmapPalette? palette)
    {
        if (AlphaFormats.Contains(format))
            return true;
        return IsIndexed(format) && palette is not null && palette.Colors.Any(c => c.A < 255);
    }

    public static bool HasAlpha(BitmapSource source) => HasAlpha(source.Format, SafePalette(source));

    public static BitmapPalette? SafePalette(BitmapSource source)
    {
        try
        {
            return source.Palette;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Format used for scaling opaque images (keeps grey and 24/32-bit colour as-is, expands the rest).</summary>
    public static PixelFormat OpaqueWorkingFormat(PixelFormat format) =>
        format == PixelFormats.Bgr24 || format == PixelFormats.Bgr32 || format == PixelFormats.Gray8 || format == PixelFormats.Rgb24
            ? format
            : PixelFormats.Bgr32;

    public static BitmapSource Convert(BitmapSource source, PixelFormat format)
    {
        if (source.Format == format)
            return source;
        var converted = new FormatConvertedBitmap(source, format, null, 0);
        converted.Freeze();
        return converted;
    }

    /// <summary>Non-premultiplied BGRA bytes, stride = width * 4.</summary>
    public static byte[] ToBgra32(BitmapSource source)
    {
        BitmapSource bgra = Convert(source, PixelFormats.Bgra32);
        int stride = bgra.PixelWidth * 4;
        byte[] pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    public static BitmapSource Create(int width, int height, double dpiX, double dpiY, PixelFormat format, BitmapPalette? palette, byte[] pixels, int stride)
    {
        var bitmap = BitmapSource.Create(width, height, Dpi(dpiX), Dpi(dpiY), format, palette, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    public static double Dpi(double value) => value > 0 && double.IsFinite(value) ? value : 96;
}
