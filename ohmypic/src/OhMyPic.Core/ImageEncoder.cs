using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OhMyPic.Core;

/// <summary>Result of a target-file-size encode.</summary>
public sealed record TargetSizeResult(byte[] Bytes, int Width, int Height, int Quality);

/// <summary>WIC encoding for the five writable formats.</summary>
public static class ImageEncoder
{
    /// <summary>Target-size search never shrinks the shorter side below this many pixels.</summary>
    public const int MinTargetSide = 16;

    public const int MinSearchQuality = 10;
    public const int MaxSearchQuality = 95;

    /// <summary>Encodes and writes atomically (temp file in the same folder, then replace).</summary>
    public static void Save(BitmapSource image, string path, ImageFormatKind kind, int jpegQuality = 85, BitmapMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        AtomicFile.WriteAllBytes(path, Encode([image], kind, jpegQuality, metadata));
    }

    /// <summary>
    /// Encodes one or more frames. JPEG/BMP flatten alpha onto white; GIF uses an exact palette when the image has at
    /// most 256 colours and WIC palette quantisation otherwise. Multiple frames need TIFF or GIF.
    /// </summary>
    public static byte[] Encode(IReadOnlyList<BitmapSource> frames, ImageFormatKind kind, int jpegQuality = 85, BitmapMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count == 0)
            throw new ArgumentException("At least one frame is required.", nameof(frames));
        if (frames.Count > 1 && !ImageFormats.SupportsMultipleFrames(kind))
            throw new ArgumentException($"{kind} cannot hold multiple frames.", nameof(frames));
        ArgumentOutOfRangeException.ThrowIfLessThan(jpegQuality, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(jpegQuality, 100);

        BitmapEncoder encoder = kind switch
        {
            ImageFormatKind.Bmp => new BmpBitmapEncoder(),
            ImageFormatKind.Gif => new GifBitmapEncoder(),
            ImageFormatKind.Jpeg => new JpegBitmapEncoder { QualityLevel = jpegQuality },
            ImageFormatKind.Tiff => new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw },
            ImageFormatKind.Png => new PngBitmapEncoder(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        for (int i = 0; i < frames.Count; i++)
        {
            BitmapSource prepared = Prepare(frames[i], kind);
            encoder.Frames.Add(BitmapFrame.Create(prepared, null, i == 0 ? metadata : null, null));
        }
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Metadata for a re-encoded file: keeps EXIF when <paramref name="target"/> is JPEG or TIFF (null otherwise).
    /// With <paramref name="physicallyRotated"/> the Orientation tag is written as 1.
    /// </summary>
    public static BitmapMetadata? CopyMetadata(BitmapMetadata? source, ImageFormatKind target, bool physicallyRotated, int width, int height) =>
        MetadataTools.CopyFor(source, target, physicallyRotated, width, height);

    /// <summary>
    /// Smallest change that fits <paramref name="targetKilobytes"/> (× 1024 bytes): JPEG first lowers quality
    /// (binary search 10..95, highest quality that fits), then shrinks; other formats only shrink.
    /// Throws <see cref="ImageOperationException"/> with <see cref="EditErrorCode.TargetSizeUnreachable"/> when even the
    /// smallest allowed size (<see cref="MinTargetSide"/>) does not fit.
    /// </summary>
    public static TargetSizeResult EncodeToTargetSize(BitmapSource image, ImageFormatKind kind, int targetKilobytes, BitmapMetadata? metadata = null) =>
        EncodeToTargetSize([image], kind, targetKilobytes, (_, _) => metadata);

    internal static TargetSizeResult EncodeToTargetSize(
        IReadOnlyList<BitmapSource> frames, ImageFormatKind kind, int targetKilobytes, Func<int, int, BitmapMetadata?> metadataFor)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(targetKilobytes, 1);
        long target = targetKilobytes * 1024L;
        int width = frames[0].PixelWidth, height = frames[0].PixelHeight;
        while (true)
        {
            int w = width, h = height;
            IReadOnlyList<BitmapSource> scaled = frames.Select(f => ImageTransforms.ScaleTo(f, w, h)).ToList();
            BitmapMetadata? metadata = metadataFor(w, h);
            int quality = kind == ImageFormatKind.Jpeg ? MinSearchQuality : 100;
            byte[] bytes = Encode(scaled, kind, kind == ImageFormatKind.Jpeg ? quality : 85, metadata);
            if (bytes.LongLength <= target)
            {
                if (kind == ImageFormatKind.Jpeg)
                {
                    int lo = MinSearchQuality, hi = MaxSearchQuality;
                    while (lo < hi)
                    {
                        int mid = (lo + hi + 1) / 2;
                        byte[] attempt = Encode(scaled, kind, mid, metadata);
                        if (attempt.LongLength <= target)
                        {
                            lo = mid;
                            bytes = attempt;
                        }
                        else
                        {
                            hi = mid - 1;
                        }
                    }
                    quality = lo;
                }
                return new TargetSizeResult(bytes, w, h, quality);
            }

            if (Math.Min(width, height) <= MinTargetSide)
                break;
            // Area scales roughly with size; aim a little under the target, but always shrink by 10-50 %.
            double factor = Math.Clamp(Math.Sqrt(target / (double)bytes.LongLength) * 0.95, 0.5, 0.9);
            double floor = MinTargetSide / (double)Math.Min(width, height);
            factor = Math.Max(factor, floor);
            int nextW = Math.Max(1, (int)Math.Round(width * factor, MidpointRounding.AwayFromZero));
            int nextH = Math.Max(1, (int)Math.Round(height * factor, MidpointRounding.AwayFromZero));
            if (nextW >= width && nextH >= height)
                break;
            (width, height) = (nextW, nextH);
        }
        throw new ImageOperationException(EditErrorCode.TargetSizeUnreachable,
            $"Cannot fit {kind} output into {targetKilobytes} KB even at {width}x{height}.");
    }

    /// <summary>Converts a frame into a pixel format the target encoder stores without surprises.</summary>
    internal static BitmapSource Prepare(BitmapSource source, ImageFormatKind kind)
    {
        PixelFormat f = source.Format;
        switch (kind)
        {
            case ImageFormatKind.Jpeg:
            {
                BitmapSource flat = ImageTransforms.FlattenOnWhite(source);
                return flat.Format == PixelFormats.Gray8 || flat.Format == PixelFormats.Bgr24 ? flat : PixelTools.Convert(flat, PixelFormats.Bgr24);
            }
            case ImageFormatKind.Bmp:
            {
                BitmapSource flat = ImageTransforms.FlattenOnWhite(source);
                return PixelTools.IsIndexed(flat.Format) || flat.Format == PixelFormats.Bgr24 ? flat : PixelTools.Convert(flat, PixelFormats.Bgr24);
            }
            case ImageFormatKind.Gif:
                return ToIndexed8(source);
            case ImageFormatKind.Png:
            case ImageFormatKind.Tiff:
            {
                bool alpha = PixelTools.HasAlpha(source);
                if (PixelTools.IsIndexed(f) && (!alpha || kind == ImageFormatKind.Png))
                    return source;
                if (f == PixelFormats.Bgra32 || f == PixelFormats.Bgr24 || f == PixelFormats.Gray8 || f == PixelFormats.BlackWhite
                    || f == PixelFormats.Gray16 || f == PixelFormats.Rgb48 || f == PixelFormats.Rgba64)
                    return source;
                return PixelTools.Convert(source, alpha ? PixelFormats.Bgra32 : PixelFormats.Bgr24);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    /// <summary>8-bit palette image. Exact when the source has ≤ 256 colours (alpha &lt; 128 becomes one transparent entry).</summary>
    private static BitmapSource ToIndexed8(BitmapSource source)
    {
        if (source.Format == PixelFormats.Indexed8)
            return source;
        int w = source.PixelWidth, h = source.PixelHeight;
        byte[] bgra = PixelTools.ToBgra32(source);
        var index = new Dictionary<uint, byte>();
        var colors = new List<Color>();
        byte[] indices = new byte[w * h];
        bool exact = true;
        for (int p = 0, i = 0; i < bgra.Length; p++, i += 4)
        {
            uint key = bgra[i + 3] < 128 ? 0u : 0xFF000000u | ((uint)bgra[i + 2] << 16) | ((uint)bgra[i + 1] << 8) | bgra[i];
            if (!index.TryGetValue(key, out byte idx))
            {
                if (colors.Count == 256)
                {
                    exact = false;
                    break;
                }
                idx = (byte)colors.Count;
                index[key] = idx;
                colors.Add(Color.FromArgb((byte)(key >> 24), (byte)(key >> 16), (byte)(key >> 8), (byte)key));
            }
            indices[p] = idx;
        }
        if (exact)
            return PixelTools.Create(w, h, source.DpiX, source.DpiY, PixelFormats.Indexed8, new BitmapPalette(colors), indices, w);

        BitmapSource flat = PixelTools.Convert(ImageTransforms.FlattenOnWhite(source), PixelFormats.Bgr32);
        var quantised = new FormatConvertedBitmap(flat, PixelFormats.Indexed8, new BitmapPalette(flat, 256), 0);
        quantised.Freeze();
        return quantised;
    }

    /// <summary>Maps encoder exceptions to <see cref="EditErrorCode.EncodeFailed"/>.</summary>
    internal static T GuardEncode<T>(Func<T> encode)
    {
        try
        {
            return encode();
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or COMException or ArgumentException or OverflowException)
        {
            throw new ImageOperationException(EditErrorCode.EncodeFailed, ex.Message, ex);
        }
    }
}
