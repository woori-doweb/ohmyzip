using System.Windows.Media.Imaging;

namespace OhMyPic.Core.Tests;

public sealed class TransformTests : TestBase
{
    // Source pixel (x, y) of a W×H image must land at these destination coordinates.
    public static TheoryData<RotateFlip> Operations => new()
    {
        RotateFlip.Rotate90, RotateFlip.Rotate180, RotateFlip.Rotate270, RotateFlip.FlipHorizontal, RotateFlip.FlipVertical,
    };

    private static (int X, int Y) Map(RotateFlip op, int x, int y, int w, int h) => op switch
    {
        RotateFlip.Rotate90 => (h - 1 - y, x),
        RotateFlip.Rotate180 => (w - 1 - x, h - 1 - y),
        RotateFlip.Rotate270 => (y, w - 1 - x),
        RotateFlip.FlipHorizontal => (w - 1 - x, y),
        RotateFlip.FlipVertical => (x, h - 1 - y),
        _ => (x, y),
    };

    [Theory]
    [MemberData(nameof(Operations))]
    public void OP_E04_RotateFlip_Pixel_Coordinates(RotateFlip op)
    {
        const int w = 7, h = 5;
        var source = Img.Palette(w, h);
        var result = ImageTransforms.Rotate(source, op);

        bool swap = op is RotateFlip.Rotate90 or RotateFlip.Rotate270;
        Assert.Equal(swap ? h : w, result.PixelWidth);
        Assert.Equal(swap ? w : h, result.PixelHeight);
        byte[] src = Img.Bgra(source), dst = Img.Bgra(result);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                var (dx, dy) = Map(op, x, y, w, h);
                Assert.Equal(Img.At(src, w, x, y), Img.At(dst, result.PixelWidth, dx, dy));
            }
        }
    }

    [Fact]
    public void OP_E04_Rotate90_Four_Times_Is_Identity()
    {
        var source = Img.Palette(33, 21);
        BitmapSource current = source;
        for (int i = 0; i < 4; i++)
            current = ImageTransforms.Rotate(current, RotateFlip.Rotate90);
        Img.AssertPixelsEqual(source, current);
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public void OP_E04_Pipeline_Rotate_Lossless_Png_File(RotateFlip op)
    {
        var source = Img.Palette(19, 11);
        string input = Temp.Combine("in.png");
        Img.WritePng(input, source);

        var result = EditPipeline.Run(
            new EditJob([input], new OutputOptions(Suffix: "_r"), Rotate: new RotateOptions(op)),
            null, null, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal(ItemStatus.Written, item.Status);
        Assert.False(item.LosslessJpegRotation);
        Assert.Equal(Temp.Combine("in_r.png"), Assert.Single(item.Outputs));
        Img.AssertPixelsEqual(ImageTransforms.Rotate(source, op), Img.Decode(item.Outputs[0]));
    }

    [Fact]
    public void OP_E06_Exif_Orientation6_AutoRotate_Display_And_Save()
    {
        // Stored 64x48 (multiple of 16 → lossless JPEG path) and 70x50 (re-encode path).
        foreach (var (w, h, expectLossless) in new[] { (64, 48, true), (70, 50, false) })
        {
            string input = Temp.Combine($"exif6_{w}.jpg");
            Img.WriteJpegWithExif(input, Img.Photo(w, h), orientation: 6);

            ImageInfo info = ImageLoader.ReadInfo(input);
            Assert.Equal(6, info.Orientation);
            Assert.Equal((w, h), (info.Width, info.Height));

            var upright = ImageLoader.LoadForDisplay(input, autoRotate: true);
            Assert.Equal((h, w), (upright.PixelWidth, upright.PixelHeight));
            var stored = ImageLoader.LoadForDisplay(input, autoRotate: false);
            Assert.Equal((w, h), (stored.PixelWidth, stored.PixelHeight));
            Assert.True(Img.Psnr(ImageTransforms.ApplyOrientation(stored, 6), upright) == double.PositiveInfinity);

            // Physical rotation: only the EXIF orientation is applied, then saved.
            var result = EditPipeline.Run(
                new EditJob([input], new OutputOptions(Suffix: "_upright"), Rotate: new RotateOptions(RotateFlip.None, AutoRotateByExif: true)),
                null, null, CancellationToken.None);
            var item = Assert.Single(result.Items);
            Assert.Equal(ItemStatus.Written, item.Status);
            Assert.Equal(expectLossless, item.LosslessJpegRotation);

            ImageInfo saved = ImageLoader.ReadInfo(item.Outputs[0]);
            Assert.Equal(1, saved.Orientation);
            Assert.Equal((h, w), (saved.Width, saved.Height));
            Assert.Equal(Img.CameraModel, saved.Get(MetadataKeys.CameraModel));
            double psnr = Img.Psnr(upright, Img.Decode(item.Outputs[0]));
            Assert.True(psnr >= 30, $"saved upright image PSNR {psnr:F1}");
        }
    }

    [Fact]
    public void OP_E06_ApplyOrientation_All_Eight_Values()
    {
        var source = Img.Palette(5, 3);
        var expected = new Dictionary<int, Func<BitmapSource, BitmapSource>>
        {
            [1] = s => s,
            [2] = s => ImageTransforms.Rotate(s, RotateFlip.FlipHorizontal),
            [3] = s => ImageTransforms.Rotate(s, RotateFlip.Rotate180),
            [4] = s => ImageTransforms.Rotate(s, RotateFlip.FlipVertical),
            [5] = s => ImageTransforms.Rotate(ImageTransforms.Rotate(s, RotateFlip.FlipHorizontal), RotateFlip.Rotate270),
            [6] = s => ImageTransforms.Rotate(s, RotateFlip.Rotate90),
            [7] = s => ImageTransforms.Rotate(ImageTransforms.Rotate(s, RotateFlip.FlipHorizontal), RotateFlip.Rotate90),
            [8] = s => ImageTransforms.Rotate(s, RotateFlip.Rotate270),
        };
        foreach (var (orientation, make) in expected)
            Img.AssertPixelsEqual(make(source), ImageTransforms.ApplyOrientation(source, orientation));

        // EXIF 5 is a transpose: (x, y) → (y, x).
        var transposed = ImageTransforms.ApplyOrientation(source, 5);
        byte[] s = Img.Bgra(source), t = Img.Bgra(transposed);
        Assert.Equal(Img.At(s, 5, 4, 1), Img.At(t, 3, 1, 4));
    }
}
