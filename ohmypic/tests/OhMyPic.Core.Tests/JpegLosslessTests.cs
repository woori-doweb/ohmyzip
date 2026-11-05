using Xunit.Abstractions;

namespace OhMyPic.Core.Tests;

/// <summary>
/// Observed on this machine (WIC, Windows 11): the JpegBitmapEncoder Rotation path transcodes losslessly —
/// rotating 90° and back 270° decodes to exactly the original pixels (max diff 0, also for noise). Comparing the
/// rotated file with "decode, then rotate" differs by up to 1 for greyscale and up to 3 for 4:2:0 colour JPEGs; that
/// residue comes from the decoder (chroma upsampling / IDCT rounding is not rotation-symmetric), not from recompression.
/// </summary>
public sealed class JpegLosslessTests(ITestOutputHelper output) : TestBase
{
    private static ItemResult Rotate(string input, RotateFlip op, string suffix) =>
        Assert.Single(EditPipeline.Run(
            new EditJob([input], new OutputOptions(Suffix: suffix), Rotate: new RotateOptions(op)),
            null, null, CancellationToken.None).Items);

    [Fact]
    public void OP_E05_Jpeg_Rotate90_Lossless_Matches_Decode_Then_Rotate_Within_2()
    {
        // Greyscale JPEG: no chroma upsampling, so the decoder-side residue is isolated from the transform.
        string input = Temp.Combine("gray.jpg");
        Img.WriteJpegWithExif(input, Img.Photo(256, 160), quality: 80, gray: true);
        var expected = ImageTransforms.Rotate(Img.Decode(input), RotateFlip.Rotate90);

        var item = Rotate(input, RotateFlip.Rotate90, "_r90");

        Assert.Equal(ItemStatus.Written, item.Status);
        Assert.True(item.LosslessJpegRotation);
        var rotated = Img.Decode(item.Outputs[0]);
        Assert.Equal((160, 256), (rotated.PixelWidth, rotated.PixelHeight));
        int diff = Img.MaxChannelDiff(expected, rotated);
        output.WriteLine($"OP-E05 observation (gray 256x160 q80): lossless rotate vs decode-then-rotate max channel diff = {diff}");
        Assert.True(diff <= 2, $"max channel diff {diff} > 2");

        ImageInfo info = ImageLoader.ReadInfo(item.Outputs[0]);
        Assert.Equal(1, info.Orientation);
        Assert.Equal(Img.CameraModel, info.Get(MetadataKeys.CameraModel));
        Assert.Equal(Img.DateTakenDisplay, info.Get(MetadataKeys.DateTaken));
        Assert.Equal("TestMake", info.Get(MetadataKeys.CameraMake));
    }

    [Fact]
    public void OP_E05_Jpeg_Color_Rotation_Is_Lossless_And_Keeps_Exif()
    {
        string input = Temp.Combine("color.jpg");
        Img.WriteJpegWithExif(input, Img.Noise(256, 160, seed: 5), quality: 80);
        var original = Img.Decode(input);

        var r90 = Rotate(input, RotateFlip.Rotate90, "_a");
        Assert.True(r90.LosslessJpegRotation);
        int vsDecodeRotate = Img.MaxChannelDiff(ImageTransforms.Rotate(original, RotateFlip.Rotate90), Img.Decode(r90.Outputs[0]));

        // 90° then 270° through the lossless path must give back exactly the original pixels.
        var back = Rotate(r90.Outputs[0], RotateFlip.Rotate270, "_b");
        Assert.True(back.LosslessJpegRotation);
        int roundTrip = Img.MaxChannelDiff(original, Img.Decode(back.Outputs[0]));

        // The same round trip through ordinary re-encoding (even at quality 95) does not.
        string reencoded = Temp.Combine("reencoded.jpg");
        ImageEncoder.Save(ImageTransforms.Rotate(original, RotateFlip.Rotate90), reencoded, ImageFormatKind.Jpeg, 95);
        string reencodedBack = Temp.Combine("reencoded_back.jpg");
        ImageEncoder.Save(ImageTransforms.Rotate(Img.Decode(reencoded), RotateFlip.Rotate270), reencodedBack, ImageFormatKind.Jpeg, 95);
        int reencodeRoundTrip = Img.MaxChannelDiff(original, Img.Decode(reencodedBack));

        output.WriteLine($"OP-E05 observation (colour noise 256x160 q80): rotate vs decode-then-rotate = {vsDecodeRotate}; " +
                         $"lossless 90+270 round trip = {roundTrip}; re-encode q95 round trip = {reencodeRoundTrip}");
        Assert.Equal(0, roundTrip);
        Assert.True(reencodeRoundTrip > 0);
        Assert.True(vsDecodeRotate <= 3, $"colour residue {vsDecodeRotate} > 3");

        ImageInfo info = ImageLoader.ReadInfo(back.Outputs[0]);
        Assert.Equal(1, info.Orientation);
        Assert.Equal(Img.CameraModel, info.Get(MetadataKeys.CameraModel));
        Assert.Equal(Img.DateTakenDisplay, info.Get(MetadataKeys.DateTaken));
    }

    [Fact]
    public void OP_E05_Jpeg_Rotate90_Four_Times_Returns_Original_Pixels()
    {
        string input = Temp.Combine("loop.jpg");
        Img.WriteJpegWithExif(input, Img.Photo(128, 96), quality: 75);
        var original = Img.Decode(input);

        string current = input;
        for (int i = 0; i < 4; i++)
        {
            var item = Rotate(current, RotateFlip.Rotate90, "r");
            Assert.True(item.LosslessJpegRotation);
            current = item.Outputs[0];
        }

        Assert.Equal(0, Img.MaxChannelDiff(original, Img.Decode(current)));
    }

    [Fact]
    public void OP_E05_Jpeg_Rotation_Falls_Back_When_Not_Lossless_Eligible()
    {
        // 100x70 is not a multiple of 16 → re-encode (flagged false), result still correct.
        string input = Temp.Combine("odd.jpg");
        Img.WriteJpegWithExif(input, Img.Photo(100, 70));
        var expected = ImageTransforms.Rotate(Img.Decode(input), RotateFlip.Rotate270);

        var item = Rotate(input, RotateFlip.Rotate270, "_r");

        Assert.Equal(ItemStatus.Written, item.Status);
        Assert.False(item.LosslessJpegRotation);
        Assert.True(Img.Psnr(expected, Img.Decode(item.Outputs[0])) >= 30);
        Assert.Equal(Img.CameraModel, ImageLoader.ReadInfo(item.Outputs[0]).Get(MetadataKeys.CameraModel));

        // A resize step also disables the lossless path.
        string multiple = Temp.Combine("m16.jpg");
        Img.WriteJpegWithExif(multiple, Img.Photo(64, 64));
        var resized = Assert.Single(EditPipeline.Run(
            new EditJob([multiple], new OutputOptions(Suffix: "_s"), Resize: new ResizeOptions(ResizeMode.Percent, 50), Rotate: new RotateOptions(RotateFlip.Rotate90)),
            null, null, CancellationToken.None).Items);
        Assert.False(resized.LosslessJpegRotation);
        Assert.Equal(32, Img.Decode(resized.Outputs[0]).PixelWidth);
    }
}
