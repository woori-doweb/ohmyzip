using System.Windows;

namespace OhMyPic.Core.Editing;

/// <summary>Crop frame aspect ratios (width:height).</summary>
public enum CropAspect
{
    Free,
    Square,
    FourThree,
    SixteenNine,
    NineSixteen,
    FourFive,
}

/// <summary>Geometric editor operations: aspect-constrained crop rectangles, crop, rotate/flip.</summary>
public static class Crop
{
    /// <summary>Reduced width:height ratio of an aspect; <see cref="CropAspect.Free"/> is (0, 0).</summary>
    public static (int Width, int Height) Ratio(CropAspect aspect) => aspect switch
    {
        CropAspect.Free => (0, 0),
        CropAspect.Square => (1, 1),
        CropAspect.FourThree => (4, 3),
        CropAspect.SixteenNine => (16, 9),
        CropAspect.NineSixteen => (9, 16),
        CropAspect.FourFive => (4, 5),
        _ => throw new ArgumentOutOfRangeException(nameof(aspect)),
    };

    /// <summary>
    /// Largest centred rectangle of the aspect inside the image. Its size is an exact integer multiple of
    /// <see cref="Ratio"/>, so the ratio is exact; only an image smaller than one ratio unit gets a rounded size.
    /// <see cref="CropAspect.Free"/> returns the whole image.
    /// </summary>
    public static Int32Rect FitAspect(int imageWidth, int imageHeight, CropAspect aspect)
    {
        CheckImageSize(imageWidth, imageHeight);
        if (aspect == CropAspect.Free)
            return new Int32Rect(0, 0, imageWidth, imageHeight);
        var (aw, ah) = Ratio(aspect);
        int k = Math.Min(imageWidth / aw, imageHeight / ah);
        int w, h;
        if (k >= 1)
        {
            (w, h) = (k * aw, k * ah);
        }
        else if ((long)imageWidth * ah >= (long)imageHeight * aw)
        {
            h = imageHeight;
            w = Math.Clamp((int)Math.Round(imageHeight * aw / (double)ah, MidpointRounding.AwayFromZero), 1, imageWidth);
        }
        else
        {
            w = imageWidth;
            h = Math.Clamp((int)Math.Round(imageWidth * ah / (double)aw, MidpointRounding.AwayFromZero), 1, imageHeight);
        }
        return new Int32Rect((imageWidth - w) / 2, (imageHeight - h) / 2, w, h);
    }

    /// <summary>
    /// Clamps a requested rectangle to the image (at least 1x1), then shrinks it to the largest rectangle of the aspect
    /// (exact integer multiple of <see cref="Ratio"/>) centred inside it. A clamped rectangle smaller than one ratio unit
    /// grows to one unit, kept inside the image.
    /// </summary>
    public static Int32Rect Constrain(Int32Rect requested, CropAspect aspect, int imageWidth, int imageHeight)
    {
        CheckImageSize(imageWidth, imageHeight);
        var (x0, x1) = ClampSpan(requested.X, requested.Width, imageWidth);
        var (y0, y1) = ClampSpan(requested.Y, requested.Height, imageHeight);
        int w = x1 - x0, h = y1 - y0;
        if (aspect == CropAspect.Free)
            return new Int32Rect(x0, y0, w, h);

        var (aw, ah) = Ratio(aspect);
        int k = Math.Min(w / aw, h / ah);
        if (k >= 1)
        {
            int cw = k * aw, ch = k * ah;
            return new Int32Rect(x0 + (w - cw) / 2, y0 + (h - ch) / 2, cw, ch);
        }
        if (imageWidth < aw || imageHeight < ah)
            return FitAspect(imageWidth, imageHeight, aspect);
        return new Int32Rect(Math.Clamp(x0, 0, imageWidth - aw), Math.Clamp(y0, 0, imageHeight - ah), aw, ah);
    }

    /// <summary>Copies the pixels of <paramref name="rect"/>; the rectangle must lie inside the image and be non-empty.</summary>
    public static RasterImage Apply(RasterImage source, Int32Rect rect)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (rect.Width < 1 || rect.Height < 1 || rect.X < 0 || rect.Y < 0
            || (long)rect.X + rect.Width > source.Width || (long)rect.Y + rect.Height > source.Height)
            throw new ArgumentOutOfRangeException(nameof(rect), $"Crop {rect} is outside the {source.Width}x{source.Height} image.");
        int rowBytes = rect.Width * 4;
        byte[] pixels = new byte[(long)rowBytes * rect.Height];
        for (int y = 0; y < rect.Height; y++)
            Buffer.BlockCopy(source.Pixels, ((rect.Y + y) * source.Width + rect.X) * 4, pixels, y * rowBytes, rowBytes);
        return new RasterImage(rect.Width, rect.Height, pixels);
    }

    /// <summary>Rotate/flip through <see cref="ImageTransforms.Rotate"/> (lossless pixel mapping).</summary>
    public static RasterImage Rotate(RasterImage source, RotateFlip operation)
    {
        ArgumentNullException.ThrowIfNull(source);
        return RasterImage.FromBitmap(ImageTransforms.Rotate(source.ToBitmap(), operation));
    }

    private static (int Start, int End) ClampSpan(int start, int length, int limit)
    {
        long s = Math.Clamp((long)start, 0, limit);
        long e = Math.Clamp((long)start + Math.Max(0, length), 0, limit);
        if (e - s < 1)
        {
            s = Math.Min(s, limit - 1);
            e = s + 1;
        }
        return ((int)s, (int)e);
    }

    private static void CheckImageSize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
    }
}
