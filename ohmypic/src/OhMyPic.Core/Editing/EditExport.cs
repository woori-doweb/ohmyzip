using System.Windows.Media.Imaging;

namespace OhMyPic.Core.Editing;

/// <summary>Editor export formats.</summary>
public enum ExportFormat
{
    Png,
    Jpeg,
}

/// <summary>Encodes the rendered editor image (optionally scaled) and writes it atomically.</summary>
public static class EditExport
{
    public const int DefaultJpegQuality = 85;

    /// <summary>Output pixel size for a scale factor (each side rounded, at least 1).</summary>
    public static (int Width, int Height) ScaledSize(int width, int height, double scale)
    {
        CheckScale(scale);
        return ImageTransforms.ComputeSize(width, height, Percent(scale));
    }

    /// <summary>
    /// Encodes the image scaled by <paramref name="scale"/> (any value &gt; 0, high-quality resize). PNG keeps alpha;
    /// JPEG is flattened onto white by the shared encoder.
    /// </summary>
    public static byte[] Encode(RasterImage image, ExportFormat format, int jpegQuality = DefaultJpegQuality, double scale = 1)
    {
        ArgumentNullException.ThrowIfNull(image);
        CheckScale(scale);
        ArgumentOutOfRangeException.ThrowIfLessThan(jpegQuality, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(jpegQuality, 100);
        ImageFormatKind kind = Kind(format);
        return ImageEncoder.GuardEncode(() =>
        {
            BitmapSource bitmap = image.ToBitmap();
            if (scale != 1)
                bitmap = ImageTransforms.Resize(bitmap, Percent(scale));
            return ImageEncoder.Encode([bitmap], kind, jpegQuality);
        });
    }

    /// <summary>Output size and exact file size, obtained by encoding in memory.</summary>
    public static (int Width, int Height, long Bytes) Estimate(RasterImage image, ExportFormat format, int jpegQuality = DefaultJpegQuality, double scale = 1)
    {
        ArgumentNullException.ThrowIfNull(image);
        var (w, h) = ScaledSize(image.Width, image.Height, scale);
        return (w, h, Encode(image, format, jpegQuality, scale).LongLength);
    }

    /// <summary>
    /// Encodes and writes through <see cref="AtomicFile"/>; the temp file is re-decoded and its size checked before it
    /// replaces <paramref name="path"/>.
    /// </summary>
    public static void Save(string path, RasterImage image, ExportFormat format, int jpegQuality = DefaultJpegQuality, double scale = 1)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(image);
        var (w, h) = ScaledSize(image.Width, image.Height, scale);
        byte[] bytes = Encode(image, format, jpegQuality, scale);
        AtomicFile.WriteAllBytes(path, bytes, temp => Verify(temp, w, h));
    }

    private static void Verify(string path, int width, int height)
    {
        int w, h;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            BitmapFrame frame = decoder.Frames[0];
            (w, h) = (frame.PixelWidth, frame.PixelHeight);
        }
        if (w != width || h != height)
            throw new ImageOperationException(EditErrorCode.EncodeFailed, $"Written file decodes as {w}x{h}, expected {width}x{height}.");
    }

    private static ImageFormatKind Kind(ExportFormat format) => format switch
    {
        ExportFormat.Png => ImageFormatKind.Png,
        ExportFormat.Jpeg => ImageFormatKind.Jpeg,
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    private static ResizeOptions Percent(double scale) => new(ResizeMode.Percent, Percent: scale * 100);

    private static void CheckScale(double scale)
    {
        if (!(scale > 0) || double.IsInfinity(scale))
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "Scale must be a finite value greater than 0.");
    }
}
