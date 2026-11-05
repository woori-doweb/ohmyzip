namespace OhMyPic.Core.Tests;

public sealed class AlphaTests : TestBase
{
    // Left third transparent red, middle third half-transparent blue, right third opaque green; plus a soft ramp.
    private static readonly System.Windows.Media.Imaging.BitmapSource AlphaImage = Img.Create(60, 20, (x, y) =>
        x < 20 ? ((byte)0, (byte)0, (byte)255, (byte)0)
        : x < 40 ? ((byte)255, (byte)0, (byte)0, (byte)128)
        : ((byte)0, (byte)200, (byte)0, (byte)(y == 0 ? 64 : 255)));

    private string RunConvert(ImageFormatKind kind)
    {
        string input = Temp.Combine("alpha.png");
        if (!File.Exists(input))
            Img.WritePng(input, AlphaImage);
        var item = Assert.Single(EditPipeline.Run(
            new EditJob([input], new OutputOptions(Suffix: "_" + kind), Convert: new ConvertOptions(kind, JpegQuality: 95)),
            null, null, CancellationToken.None).Items);
        Assert.Equal(ItemStatus.Written, item.Status);
        return item.Outputs[0];
    }

    [Theory]
    [InlineData(ImageFormatKind.Png)]
    [InlineData(ImageFormatKind.Tiff)]
    public void OP_E09_Alpha_Preserved_In_Png_And_Tiff(ImageFormatKind kind)
    {
        string output = RunConvert(kind);
        Assert.True(ImageLoader.ReadInfo(output).HasAlpha);
        Img.AssertPixelsEqual(AlphaImage, Img.Decode(output));
    }

    [Theory]
    [InlineData(ImageFormatKind.Bmp, 0)]
    [InlineData(ImageFormatKind.Jpeg, 6)]
    public void OP_E09_Alpha_Flattened_On_White_For_Bmp_And_Jpeg(ImageFormatKind kind, int tolerance)
    {
        string output = RunConvert(kind);
        Assert.False(ImageLoader.ReadInfo(output).HasAlpha);
        byte[] px = Img.Bgra(Img.Decode(output));

        void Near((byte B, byte G, byte R) expected, int x, int y)
        {
            var (b, g, r, a) = Img.At(px, 60, x, y);
            Assert.Equal(255, a);
            Assert.InRange(b, expected.B - tolerance, expected.B + tolerance);
            Assert.InRange(g, expected.G - tolerance, expected.G + tolerance);
            Assert.InRange(r, expected.R - tolerance, expected.R + tolerance);
        }

        Near((255, 255, 255), 10, 10); // fully transparent → white
        Near((255, 127, 127), 30, 10); // 50 % blue over white: (255·128 + 255·127)/255, 255·127/255
        Near((0, 200, 0), 50, 10);     // opaque unchanged
        if (kind == ImageFormatKind.Bmp)
            Img.AssertPixelsEqual(ImageTransforms.FlattenOnWhite(AlphaImage), Img.Decode(output));
    }
}
