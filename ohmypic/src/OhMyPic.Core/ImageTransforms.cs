using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OhMyPic.Core;

/// <summary>Pixel transforms on in-memory images. Every result is frozen.</summary>
public static class ImageTransforms
{
    public static BitmapSource Rotate(BitmapSource source, RotateFlip operation) => Apply(source, Dihedral.From(operation));

    /// <summary>Turns stored pixels into display orientation for an EXIF orientation value (1..8; others = unchanged).</summary>
    public static BitmapSource ApplyOrientation(BitmapSource source, int exifOrientation) =>
        Apply(source, Dihedral.FromExifOrientation(exifOrientation));

    /// <summary>
    /// Geometric resize. <see cref="ResizeMode.TargetFileSize"/> depends on the encoder and is not a pixel transform:
    /// use <see cref="ImageEncoder.EncodeToTargetSize"/> or an <see cref="EditJob"/>.
    /// </summary>
    public static BitmapSource Resize(BitmapSource source, ResizeOptions options)
    {
        if (options.Mode == ResizeMode.TargetFileSize)
            throw new ArgumentException("TargetFileSize is applied while encoding; use ImageEncoder.EncodeToTargetSize.", nameof(options));
        var (w, h) = ComputeSize(source.PixelWidth, source.PixelHeight, options);
        return ScaleTo(source, w, h);
    }

    /// <summary>Output size for a resize (TargetFileSize keeps the size; the encoder shrinks later if needed).</summary>
    public static (int Width, int Height) ComputeSize(int width, int height, ResizeOptions options)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Image size must be positive.");
        switch (options.Mode)
        {
            case ResizeMode.Percent:
                if (!(options.Percent > 0) || double.IsInfinity(options.Percent))
                    throw new ArgumentOutOfRangeException(nameof(options), "Percent must be greater than 0.");
                return (Round(width * options.Percent / 100.0), Round(height * options.Percent / 100.0));
            case ResizeMode.Pixels:
            {
                if (options.Width < 0 || options.Height < 0)
                    throw new ArgumentOutOfRangeException(nameof(options), "Width and Height must not be negative.");
                if (options.Width == 0 && options.Height == 0)
                    return (width, height);
                if (!options.KeepAspect)
                    return (options.Width > 0 ? options.Width : width, options.Height > 0 ? options.Height : height);
                if (options.Width > 0 && options.Height > 0)
                {
                    // Fit inside the box.
                    double sx = options.Width / (double)width, sy = options.Height / (double)height;
                    return sx <= sy
                        ? (options.Width, Round(height * sx))
                        : (Round(width * sy), options.Height);
                }
                return options.Width > 0
                    ? (options.Width, Round(height * (options.Width / (double)width)))
                    : (Round(width * (options.Height / (double)height)), options.Height);
            }
            case ResizeMode.LongAxis:
                if (options.LongAxis <= 0)
                    throw new ArgumentOutOfRangeException(nameof(options), "LongAxis must be greater than 0.");
                return width >= height
                    ? (options.LongAxis, Round(height * (options.LongAxis / (double)width)))
                    : (Round(width * (options.LongAxis / (double)height)), options.LongAxis);
            case ResizeMode.TargetFileSize:
                if (options.TargetKilobytes <= 0)
                    throw new ArgumentOutOfRangeException(nameof(options), "TargetKilobytes must be greater than 0.");
                return (width, height);
            default:
                throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    /// <summary>Composites any alpha onto white and returns an opaque Bgr24 image; opaque inputs are returned unchanged.</summary>
    public static BitmapSource FlattenOnWhite(BitmapSource source)
    {
        if (!PixelTools.HasAlpha(source))
            return source;
        int w = source.PixelWidth, h = source.PixelHeight;
        byte[] bgra = PixelTools.ToBgra32(source);
        byte[] bgr = new byte[w * h * 3];
        for (int i = 0, o = 0; i < bgra.Length; i += 4, o += 3)
        {
            int a = bgra[i + 3];
            for (int c = 0; c < 3; c++)
                bgr[o + c] = (byte)((bgra[i + c] * a + 255 * (255 - a) + 127) / 255);
        }
        return PixelTools.Create(w, h, source.DpiX, source.DpiY, PixelFormats.Bgr24, null, bgr, w * 3);
    }

    internal static BitmapSource Apply(BitmapSource source, Dihedral transform)
    {
        if (transform.IsIdentity)
            return source;
        BitmapSource result = source;
        if (transform.Flip)
            result = Transformed(result, new ScaleTransform(-1, 1));
        if (transform.Quarter != 0)
            result = Transformed(result, new RotateTransform(90 * transform.Quarter));
        return result;
    }

    /// <summary>High-quality (WIC Fant) scale to an exact pixel size.</summary>
    internal static BitmapSource ScaleTo(BitmapSource source, int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == source.PixelWidth && height == source.PixelHeight)
            return source;
        bool alpha = PixelTools.HasAlpha(source);
        // Scale premultiplied so transparent pixels do not bleed their colour into the edges.
        BitmapSource input = PixelTools.Convert(source, alpha ? PixelFormats.Pbgra32 : PixelTools.OpaqueWorkingFormat(source.Format));
        double sx = width / (double)input.PixelWidth, sy = height / (double)input.PixelHeight;
        var scaled = new TransformedBitmap(input, new ScaleTransform(sx, sy));
        scaled.Freeze();
        if (scaled.PixelWidth != width || scaled.PixelHeight != height)
            throw new InvalidOperationException($"Scaler produced {scaled.PixelWidth}x{scaled.PixelHeight}, expected {width}x{height}.");
        return alpha ? PixelTools.Convert(scaled, PixelFormats.Bgra32) : scaled;
    }

    private static int Round(double value) => Math.Max(1, (int)Math.Round(value, MidpointRounding.AwayFromZero));

    private static BitmapSource Transformed(BitmapSource source, Transform transform)
    {
        var result = new TransformedBitmap(source, transform);
        result.Freeze();
        return result;
    }
}
