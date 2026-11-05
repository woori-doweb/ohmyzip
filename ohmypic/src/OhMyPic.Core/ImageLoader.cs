using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OhMyPic.Core;

/// <summary>
/// Decoding through WIC. Files are read into memory first, so nothing stays locked after a call returns.
/// Every failure surfaces as <see cref="ImageOperationException"/> with a typed <see cref="EditErrorCode"/>.
/// </summary>
public static class ImageLoader
{
    /// <summary>Largest frame (width × height) the loader will decode.</summary>
    public const long DefaultMaxPixels = 200_000_000;

    public static ImageInfo ReadInfo(string path) => Guard(path, () =>
    {
        EnsureReadableExtension(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        if (decoder.Frames.Count == 0)
            throw new ImageOperationException(EditErrorCode.DecodeFailed, "The file contains no image frames.");
        BitmapFrame frame = decoder.Frames[0];
        BitmapMetadata? metadata = MetadataTools.TryGetMetadata(frame);
        return new ImageInfo(
            frame.PixelWidth,
            frame.PixelHeight,
            decoder.Frames.Count,
            FormatName(decoder),
            frame.DpiX,
            frame.DpiY,
            frame.Format.BitsPerPixel,
            PixelTools.HasAlpha(frame.Format, PixelTools.SafePalette(frame)),
            MetadataTools.ReadOrientation(metadata),
            MetadataTools.ReadCamera(metadata));
    });

    /// <summary>
    /// First frame for on-screen display. <paramref name="maxDecodeSide"/> &gt; 0 decodes at most that many pixels on the
    /// long side (WIC scales while decoding). With <paramref name="autoRotate"/> the EXIF orientation is applied.
    /// </summary>
    public static BitmapSource LoadForDisplay(string path, bool autoRotate, int maxDecodeSide = 0) => Guard(path, () =>
    {
        EnsureReadableExtension(path);
        byte[] bytes = File.ReadAllBytes(path);
        using var stream = new MemoryStream(bytes, writable: false);
        var (width, height, orientation) = Probe(stream);
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        if (maxDecodeSide > 0 && Math.Max(width, height) > maxDecodeSide)
        {
            // Both sides set explicitly so the short side follows the engine's rounding rule (WPF alone truncates).
            var (w, h) = ImageTransforms.ComputeSize(width, height, new ResizeOptions(ResizeMode.LongAxis, LongAxis: maxDecodeSide));
            image.DecodePixelWidth = w;
            image.DecodePixelHeight = h;
        }
        image.EndInit();
        image.Freeze();
        BitmapSource result = image;
        if (autoRotate && orientation != 1)
            result = Materialize(ImageTransforms.ApplyOrientation(result, orientation));
        return result;
    });

    /// <summary>Small preview: decoded directly at <paramref name="maxSide"/> on the long side (never upscaled).</summary>
    public static BitmapSource LoadThumbnail(string path, int maxSide, bool autoRotate = true)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSide, 1);
        return LoadForDisplay(path, autoRotate, maxSide);
    }

    /// <summary>All frames/pages at full size. GIF frames are composited onto the logical screen as they would play.</summary>
    public static IReadOnlyList<BitmapFrame> LoadFrames(string path) => Guard(path, () =>
    {
        var decoded = Decode(path, allFrames: true);
        return decoded.Frames.Select(f =>
        {
            var frame = f as BitmapFrame ?? BitmapFrame.Create(f);
            if (frame.CanFreeze && !frame.IsFrozen)
                frame.Freeze();
            return frame;
        }).ToList();
    });

    /// <summary>Full decode for editing: raw pixel format (no colour conversion), metadata clone, orientation.</summary>
    internal static DecodedImage Decode(string path, bool allFrames)
    {
        EnsureReadableExtension(path);
        byte[] bytes = File.ReadAllBytes(path);
        using var stream = new MemoryStream(bytes, writable: false);
        Probe(stream);
        stream.Position = 0;
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0)
            throw new ImageOperationException(EditErrorCode.DecodeFailed, "The file contains no image frames.");
        foreach (BitmapFrame f in decoder.Frames)
            CheckPixels(f.PixelWidth, f.PixelHeight);
        BitmapFrame first = decoder.Frames[0];
        BitmapMetadata? metadata = MetadataTools.TryGetMetadata(first);
        BitmapMetadata? clone = null;
        if (metadata is not null)
        {
            try
            {
                clone = metadata.Clone();
                clone.Freeze();
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or COMException)
            {
                clone = null;
            }
        }
        IReadOnlyList<BitmapSource> frames = decoder is GifBitmapDecoder gif
            ? GifComposer.Compose(gif, allFrames)
            : (allFrames ? decoder.Frames.Cast<BitmapSource>().ToList() : [first]);
        foreach (BitmapSource frame in frames)
        {
            if (frame.CanFreeze && !frame.IsFrozen)
                frame.Freeze();
        }
        return new DecodedImage(
            frames,
            decoder.Frames.Count,
            clone,
            MetadataTools.ReadOrientation(metadata),
            SourceKind(decoder),
            FormatName(decoder));
    }

    /// <summary>Header-only probe: stored size and EXIF orientation. Enforces the pixel guard before any pixel decode.</summary>
    private static (int Width, int Height, int Orientation) Probe(Stream stream)
    {
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
        if (decoder.Frames.Count == 0)
            throw new ImageOperationException(EditErrorCode.DecodeFailed, "The file contains no image frames.");
        BitmapFrame frame = decoder.Frames[0];
        CheckPixels(frame.PixelWidth, frame.PixelHeight);
        return (frame.PixelWidth, frame.PixelHeight, MetadataTools.ReadOrientation(MetadataTools.TryGetMetadata(frame)));
    }

    internal static void CheckPixels(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ImageOperationException(EditErrorCode.DecodeFailed, "The image has no pixels.");
        if ((long)width * height > DefaultMaxPixels)
            throw new ImageOperationException(EditErrorCode.TooLarge, $"{width}x{height} exceeds the {DefaultMaxPixels:N0}-pixel limit.");
    }

    private static void EnsureReadableExtension(string path)
    {
        if (!ImageFormats.IsReadable(path))
            throw new ImageOperationException(EditErrorCode.UnsupportedFormat, $"'{Path.GetExtension(path)}' is not a readable image format.");
    }

    internal static BitmapSource Materialize(BitmapSource source)
    {
        var cached = new CachedBitmap(source, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        cached.Freeze();
        return cached;
    }

    internal static ImageFormatKind? SourceKind(BitmapDecoder decoder) => decoder switch
    {
        BmpBitmapDecoder => ImageFormatKind.Bmp,
        GifBitmapDecoder => ImageFormatKind.Gif,
        JpegBitmapDecoder => ImageFormatKind.Jpeg,
        TiffBitmapDecoder => ImageFormatKind.Tiff,
        PngBitmapDecoder => ImageFormatKind.Png,
        _ => null,
    };

    private static string FormatName(BitmapDecoder decoder)
    {
        switch (decoder)
        {
            case BmpBitmapDecoder: return "BMP";
            case GifBitmapDecoder: return "GIF";
            case JpegBitmapDecoder: return "JPEG";
            case TiffBitmapDecoder: return "TIFF";
            case PngBitmapDecoder: return "PNG";
            case IconBitmapDecoder: return "ICO";
            case WmpBitmapDecoder: return "JPEG XR";
        }
        try
        {
            string name = decoder.CodecInfo?.FriendlyName ?? "";
            if (name.EndsWith(" Decoder", StringComparison.OrdinalIgnoreCase))
                name = name[..^" Decoder".Length];
            return name.Length > 0 ? name : "Unknown";
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or COMException)
        {
            return "Unknown";
        }
    }

    /// <summary>Runs a load and maps WIC/IO exceptions to <see cref="ImageOperationException"/>.</summary>
    internal static T Guard<T>(string path, Func<T> load)
    {
        try
        {
            return load();
        }
        catch (ImageOperationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or PathTooLongException)
        {
            throw new ImageOperationException(EditErrorCode.IoError, ex.Message, ex);
        }
        catch (Exception ex) when (ex is FileFormatException or NotSupportedException or COMException or ArgumentException
                                       or InvalidOperationException or OverflowException or EndOfStreamException)
        {
            throw new ImageOperationException(EditErrorCode.DecodeFailed, $"Cannot decode '{Path.GetFileName(path)}': {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            // Sharing violations, device errors, ...: the file could not be read at all.
            throw new ImageOperationException(EditErrorCode.IoError, ex.Message, ex);
        }
        catch (OutOfMemoryException ex)
        {
            throw new ImageOperationException(EditErrorCode.TooLarge, ex.Message, ex);
        }
    }
}

/// <summary>Decoded frames plus what the edit pipeline needs to know about the source.</summary>
internal sealed record DecodedImage(
    IReadOnlyList<BitmapSource> Frames,
    int TotalFrameCount,
    BitmapMetadata? Metadata,
    int Orientation,
    ImageFormatKind? SourceKind,
    string FormatName);

/// <summary>Plays a GIF into full-size frames (logical screen, frame offsets, disposal methods).</summary>
internal static class GifComposer
{
    public static IReadOnlyList<BitmapSource> Compose(GifBitmapDecoder decoder, bool allFrames)
    {
        BitmapFrame first = decoder.Frames[0];
        int screenW = ReadUShort(decoder.Metadata as BitmapMetadata, "/logscrdesc/Width") ?? first.PixelWidth;
        int screenH = ReadUShort(decoder.Metadata as BitmapMetadata, "/logscrdesc/Height") ?? first.PixelHeight;
        if (screenW <= 0 || screenH <= 0)
            (screenW, screenH) = (first.PixelWidth, first.PixelHeight);
        ImageLoader.CheckPixels(screenW, screenH);
        int count = allFrames ? decoder.Frames.Count : 1;
        int stride = screenW * 4;
        byte[] canvas = new byte[stride * screenH];
        var result = new List<BitmapSource>(count);
        for (int i = 0; i < count; i++)
        {
            BitmapFrame frame = decoder.Frames[i];
            var meta = MetadataTools.TryGetMetadata(frame);
            int left = ReadUShort(meta, "/imgdesc/Left") ?? 0;
            int top = ReadUShort(meta, "/imgdesc/Top") ?? 0;
            int disposal = meta is not null && MetadataTools.TryQuery(meta, "/grctlext/Disposal") is byte d ? d : 0;
            byte[]? saved = disposal == 3 ? (byte[])canvas.Clone() : null;

            int fw = frame.PixelWidth, fh = frame.PixelHeight;
            byte[] pixels = PixelTools.ToBgra32(frame);
            for (int y = 0; y < fh; y++)
            {
                int cy = top + y;
                if (cy < 0 || cy >= screenH)
                    continue;
                for (int x = 0; x < fw; x++)
                {
                    int cx = left + x;
                    if (cx < 0 || cx >= screenW)
                        continue;
                    int s = (y * fw + x) * 4;
                    if (pixels[s + 3] == 0)
                        continue;
                    Buffer.BlockCopy(pixels, s, canvas, cy * stride + cx * 4, 4);
                }
            }
            result.Add(PixelTools.Create(screenW, screenH, frame.DpiX, frame.DpiY, PixelFormats.Bgra32, null, (byte[])canvas.Clone(), stride));

            if (disposal == 2)
            {
                for (int y = Math.Max(0, top); y < Math.Min(screenH, top + fh); y++)
                    Array.Clear(canvas, y * stride + Math.Max(0, left) * 4, Math.Max(0, Math.Min(screenW, left + fw) - Math.Max(0, left)) * 4);
            }
            else if (saved is not null)
            {
                canvas = saved;
            }
        }
        return result;
    }

    private static int? ReadUShort(BitmapMetadata? metadata, string query) =>
        metadata is not null && MetadataTools.TryQuery(metadata, query) is ushort u ? u : null;
}
