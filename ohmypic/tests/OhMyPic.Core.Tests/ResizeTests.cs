namespace OhMyPic.Core.Tests;

public sealed class ResizeTests : TestBase
{
    private ItemResult RunResize(string input, ResizeOptions resize, ConvertOptions? convert = null, string suffix = "_s")
    {
        var result = EditPipeline.Run(new EditJob([input], new OutputOptions(Suffix: suffix), Resize: resize, Convert: convert), null, null, CancellationToken.None);
        return Assert.Single(result.Items);
    }

    [Fact]
    public void OP_E07_Resize_Percent_50()
    {
        string input = Fixtures.CopyTo("fixture-01.png", Temp);
        var item = RunResize(input, new ResizeOptions(ResizeMode.Percent, Percent: 50));
        Assert.Equal(ItemStatus.Written, item.Status);
        var info = ImageLoader.ReadInfo(item.Outputs[0]);
        Assert.Equal((640, 400), (info.Width, info.Height));
        Assert.Equal("PNG", info.FormatName);
    }

    [Fact]
    public void OP_E07_Resize_Pixels_800_KeepAspect()
    {
        string input = Fixtures.CopyTo("fixture-01.png", Temp);
        var item = RunResize(input, new ResizeOptions(ResizeMode.Pixels, Width: 800, KeepAspect: true));
        var info = ImageLoader.ReadInfo(item.Outputs[0]);
        Assert.Equal((800, 500), (info.Width, info.Height));
    }

    [Fact]
    public void OP_E07_Resize_LongAxis_300()
    {
        string input = Fixtures.CopyTo("fixture-01.png", Temp);
        var item = RunResize(input, new ResizeOptions(ResizeMode.LongAxis, LongAxis: 300));
        var info = ImageLoader.ReadInfo(item.Outputs[0]);
        // 800 × 300 / 1280 = 187.5 → 188 (midpoint away from zero).
        Assert.Equal((300, 188), (info.Width, info.Height));
    }

    [Fact]
    public void OP_E07_Resize_TargetFileSize_Jpeg_Fits()
    {
        string input = Fixtures.CopyTo("fixture-01.png", Temp);
        const int targetKb = 40;
        var item = RunResize(input, new ResizeOptions(ResizeMode.TargetFileSize, TargetKilobytes: targetKb), new ConvertOptions(ImageFormatKind.Jpeg));
        Assert.Equal(ItemStatus.Written, item.Status);
        long size = new FileInfo(item.Outputs[0]).Length;
        Assert.True(size <= targetKb * 1024, $"{size} B > {targetKb} KB");
        Assert.Equal("JPEG", ImageLoader.ReadInfo(item.Outputs[0]).FormatName);

        // A tiny target forces down-scaling as well; still within the limit.
        var small = RunResize(input, new ResizeOptions(ResizeMode.TargetFileSize, TargetKilobytes: 3), new ConvertOptions(ImageFormatKind.Jpeg), "_tiny");
        Assert.Equal(ItemStatus.Written, small.Status);
        Assert.True(new FileInfo(small.Outputs[0]).Length <= 3 * 1024);
        var info = ImageLoader.ReadInfo(small.Outputs[0]);
        Assert.True(info.Width < 1280);
        Assert.Equal(1.6, info.Width / (double)info.Height, 1);
    }

    [Fact]
    public void OP_E07_Resize_TargetFileSize_Unreachable()
    {
        // The kept EXIF block alone (≈ 40 KB description) is larger than the 8 KB target: no pixel size can fit.
        string input = Temp.Combine("bigexif.jpg");
        Img.WriteJpegWithExif(input, Img.Photo(400, 300), description: new string('d', 40_000));

        var item = RunResize(input, new ResizeOptions(ResizeMode.TargetFileSize, TargetKilobytes: 8));

        Assert.Equal(ItemStatus.Failed, item.Status);
        Assert.Equal(EditErrorCode.TargetSizeUnreachable, item.Error);
        Assert.Empty(item.Outputs);
        Assert.Equal(["bigexif.jpg"], Util.FileNames(Temp.Path));

        // Same input without the description fits.
        string plain = Temp.Combine("plain.jpg");
        Img.WriteJpegWithExif(plain, Img.Photo(400, 300));
        var ok = RunResize(plain, new ResizeOptions(ResizeMode.TargetFileSize, TargetKilobytes: 8));
        Assert.Equal(ItemStatus.Written, ok.Status);
        Assert.True(new FileInfo(ok.Outputs[0]).Length <= 8 * 1024);

        // Lossless target with an impossible size: a 1 KB PNG of a 16 px-floor image that stays noisy.
        var ex = Assert.Throws<ImageOperationException>(() => ImageEncoder.EncodeToTargetSize(Img.Noise(16, 16), ImageFormatKind.Png, 1));
        Assert.Equal(EditErrorCode.TargetSizeUnreachable, ex.Code);
    }

    [Fact]
    public void OP_E07_ComputeSize_Rules()
    {
        Assert.Equal((640, 400), ImageTransforms.ComputeSize(1280, 800, new ResizeOptions(ResizeMode.Percent, 50)));
        Assert.Equal((800, 500), ImageTransforms.ComputeSize(1280, 800, new ResizeOptions(ResizeMode.Pixels, Width: 800)));
        Assert.Equal((480, 300), ImageTransforms.ComputeSize(1280, 800, new ResizeOptions(ResizeMode.Pixels, Height: 300)));
        Assert.Equal((800, 300), ImageTransforms.ComputeSize(1280, 800, new ResizeOptions(ResizeMode.Pixels, Width: 800, Height: 300, KeepAspect: false)));
        // Both sides with KeepAspect: fit inside the box.
        Assert.Equal((480, 300), ImageTransforms.ComputeSize(1280, 800, new ResizeOptions(ResizeMode.Pixels, Width: 800, Height: 300)));
        Assert.Equal((188, 300), ImageTransforms.ComputeSize(800, 1280, new ResizeOptions(ResizeMode.LongAxis, LongAxis: 300)));
        // 3 × 0.5 = 1.5 → 2 (away from zero); never below 1 px.
        Assert.Equal((2, 1), ImageTransforms.ComputeSize(3, 1, new ResizeOptions(ResizeMode.Percent, 50)));
        Assert.Equal((1, 1), ImageTransforms.ComputeSize(10, 10, new ResizeOptions(ResizeMode.Percent, 0.1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageTransforms.ComputeSize(10, 10, new ResizeOptions(ResizeMode.Percent, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageTransforms.ComputeSize(10, 10, new ResizeOptions(ResizeMode.LongAxis)));
    }
}
