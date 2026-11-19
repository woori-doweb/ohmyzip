using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OhMyPic.Core.Editing;

/// <summary>
/// Editor working buffer: straight (non-premultiplied) BGRA32, stride = width * 4. Editing operations never change a
/// buffer in place; they return a new image, so an instance can be shared by the history without copying.
/// </summary>
public sealed class RasterImage
{
    public RasterImage(int width, int height, byte[] pixels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.LongLength != (long)width * height * 4)
            throw new ArgumentException($"Expected {(long)width * height * 4} bytes for {width}x{height} BGRA32, got {pixels.LongLength}.", nameof(pixels));
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>BGRA32 straight-alpha pixels, row by row.</summary>
    public byte[] Pixels { get; }

    public int Stride => Width * 4;

    public long ByteSize => Pixels.LongLength;

    public static RasterImage FromBitmap(BitmapSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new RasterImage(source.PixelWidth, source.PixelHeight, PixelTools.ToBgra32(source));
    }

    /// <summary>Frozen 96-dpi BGRA32 bitmap holding a copy of the pixels.</summary>
    public BitmapSource ToBitmap() => PixelTools.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, Pixels, Stride);

    public RasterImage Clone() => new(Width, Height, (byte[])Pixels.Clone());
}
