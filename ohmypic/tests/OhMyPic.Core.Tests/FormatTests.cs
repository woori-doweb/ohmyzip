namespace OhMyPic.Core.Tests;

public sealed class FormatTests : TestBase
{
    [Theory]
    [InlineData(ImageFormatKind.Bmp)]
    [InlineData(ImageFormatKind.Png)]
    [InlineData(ImageFormatKind.Tiff)]
    [InlineData(ImageFormatKind.Gif)]
    public void OP_E02_Lossless_RoundTrip_Exact_Pixels(ImageFormatKind kind)
    {
        var source = Img.Palette(97, 61);
        string path = Temp.Combine("palette" + ImageFormats.DefaultExtension(kind));

        ImageEncoder.Save(source, path, kind);

        var back = Img.Decode(path);
        Img.AssertPixelsEqual(source, back);
        Assert.Equal(kind.ToString().ToUpperInvariant(), ImageLoader.ReadInfo(path).FormatName);
    }

    [Fact]
    public void OP_E03_Jpeg_Quality85_Psnr_And_Size_Ordering()
    {
        var source = Img.Photo(320, 240);
        string q85 = Temp.Combine("q85.jpg"), q95 = Temp.Combine("q95.jpg"), q50 = Temp.Combine("q50.jpg");

        ImageEncoder.Save(source, q85, ImageFormatKind.Jpeg, 85);
        ImageEncoder.Save(source, q95, ImageFormatKind.Jpeg, 95);
        ImageEncoder.Save(source, q50, ImageFormatKind.Jpeg, 50);

        double psnr = Img.Psnr(source, Img.Decode(q85));
        Assert.True(psnr >= 30, $"PSNR {psnr:F2} dB < 30");
        Assert.True(new FileInfo(q95).Length > new FileInfo(q50).Length);
        Assert.Equal("JPEG", ImageLoader.ReadInfo(q85).FormatName);
    }

    [Theory]
    [InlineData(ImageFormatKind.Bmp)]
    [InlineData(ImageFormatKind.Gif)]
    [InlineData(ImageFormatKind.Jpeg)]
    [InlineData(ImageFormatKind.Tiff)]
    [InlineData(ImageFormatKind.Png)]
    public void OP_E17_SaveAs_Each_Format_Reopens_Same_Size(ImageFormatKind kind)
    {
        string input = Fixtures.CopyTo("fixture-01.png", Temp);
        var image = ImageLoader.LoadForDisplay(input, autoRotate: true);
        string output = Temp.Combine("saved" + ImageFormats.DefaultExtension(kind));

        ImageEncoder.Save(image, output, kind);

        ImageInfo info = ImageLoader.ReadInfo(output);
        Assert.Equal(1280, info.Width);
        Assert.Equal(800, info.Height);
        var reopened = ImageLoader.LoadForDisplay(output, autoRotate: true);
        Assert.Equal(1280, reopened.PixelWidth);
        Assert.Equal(800, reopened.PixelHeight);
        Assert.Empty(Util.TempFilesUnder(Temp.Path));
        if (kind is ImageFormatKind.Png or ImageFormatKind.Tiff or ImageFormatKind.Bmp)
            Assert.True(Img.Psnr(image, reopened) == double.PositiveInfinity, "lossless format changed pixels");
        else if (kind == ImageFormatKind.Jpeg)
            Assert.True(Img.Psnr(image, reopened) >= 30);
    }

    [Fact]
    public void OP_E17_Save_Replaces_Existing_File_Atomically()
    {
        string output = Temp.Combine("out.png");
        File.WriteAllText(output, "old");

        ImageEncoder.Save(Img.Palette(10, 10), output, ImageFormatKind.Png);

        Assert.Equal(10, ImageLoader.ReadInfo(output).Width);
        Assert.Equal(["out.png"], Util.FileNames(Temp.Path));
    }
}
