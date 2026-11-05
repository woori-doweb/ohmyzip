namespace OhMyPic.Core.Tests;

public sealed class LoaderTests : TestBase
{
    [Fact]
    public void OP_E01_Fixture01_Info_And_DisplayDecode()
    {
        string path = Fixtures.CopyTo("fixture-01.png", Temp);

        ImageInfo info = ImageLoader.ReadInfo(path);
        Assert.Equal(1280, info.Width);
        Assert.Equal(800, info.Height);
        Assert.Equal(1, info.FrameCount);
        Assert.Equal("PNG", info.FormatName);
        Assert.Equal(1, info.Orientation);
        Assert.True(info.BitsPerPixel > 0);

        var display = ImageLoader.LoadForDisplay(path, autoRotate: true);
        Assert.True(display.IsFrozen);
        Assert.Equal(1280, display.PixelWidth);
        Assert.Equal(800, display.PixelHeight);
        // Same pixels as a direct full decode.
        Img.AssertPixelsEqual(Img.Decode(path), display);

        // The file is not locked after loading.
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
        }
        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void OP_E01_DisplayDecode_Downscaled_And_Thumbnail()
    {
        string path = Fixtures.CopyTo("fixture-02.png", Temp);

        var limited = ImageLoader.LoadForDisplay(path, autoRotate: false, maxDecodeSide: 640);
        Assert.Equal(640, limited.PixelWidth);
        Assert.Equal(400, limited.PixelHeight);

        var thumb = ImageLoader.LoadThumbnail(path, 150);
        Assert.True(thumb.IsFrozen);
        Assert.Equal(150, thumb.PixelWidth);
        Assert.Equal(94, thumb.PixelHeight);

        // Never upscaled.
        var big = ImageLoader.LoadThumbnail(path, 4000);
        Assert.Equal(1280, big.PixelWidth);
    }

    [Fact]
    public void OP_E01_ReadableExtensions_Include_BuiltIns_And_Writable()
    {
        foreach (string ext in ImageFormats.BuiltInReadableExtensions)
            Assert.Contains(ext, ImageFormats.ReadableExtensions);
        Assert.True(ImageFormats.IsReadable(@"C:\x\photo.JPG"));
        Assert.True(ImageFormats.IsReadable("a.tiff"));
        Assert.False(ImageFormats.IsReadable("a.txt"));
        Assert.False(ImageFormats.IsReadable("noextension"));
        Assert.Equal(ImageFormatKind.Jpeg, ImageFormats.FromExtension(".jpeg"));
        Assert.Equal(ImageFormatKind.Tiff, ImageFormats.FromExtension("TIF"));
        Assert.Null(ImageFormats.FromExtension(".ico"));
        Assert.Equal(".jpg", ImageFormats.DefaultExtension(ImageFormatKind.Jpeg));
        Assert.Equal(".tif", ImageFormats.DefaultExtension(ImageFormatKind.Tiff));
        Assert.Equal(5, ImageFormats.WritableFormats.Count);
        // Installed-codec discovery never throws and only yields dotted lower-case extensions.
        Assert.All(ImageFormats.InstalledDecoderExtensions, e => Assert.Matches("^\\.[a-z0-9_\\-]+$", e));
    }
}
