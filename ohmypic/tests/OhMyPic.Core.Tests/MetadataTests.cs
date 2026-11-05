namespace OhMyPic.Core.Tests;

public sealed class MetadataTests : TestBase
{
    private string MakeJpeg(string name = "camera.jpg", int width = 96, int height = 64)
    {
        string path = Temp.Combine(name);
        Img.WriteJpegWithExif(path, Img.Photo(width, height));
        return path;
    }

    private static void AssertCamera(string path)
    {
        ImageInfo info = ImageLoader.ReadInfo(path);
        Assert.Equal(Img.CameraModel, info.Get(MetadataKeys.CameraModel));
        Assert.Equal(Img.DateTakenDisplay, info.Get(MetadataKeys.DateTaken));
    }

    [Fact]
    public void OP_E08_ReadInfo_Camera_Fields()
    {
        string path = MakeJpeg();
        ImageInfo info = ImageLoader.ReadInfo(path);
        Assert.Equal("TestMake", info.Get(MetadataKeys.CameraMake));
        AssertCamera(path);
        Assert.Null(info.Get(MetadataKeys.IsoSpeed));
    }

    private static (string Label, EditJob Job)[] Jobs(string input) =>
    [
        ("rotate-lossless", new EditJob([input], new OutputOptions(Suffix: "_a"), Rotate: new RotateOptions(RotateFlip.Rotate90))),
        ("rotate-reencode", new EditJob([input], new OutputOptions(Suffix: "_b"), Rotate: new RotateOptions(RotateFlip.FlipHorizontal), Convert: new ConvertOptions(ImageFormatKind.Jpeg))),
        ("resize", new EditJob([input], new OutputOptions(Suffix: "_c"), Resize: new ResizeOptions(ResizeMode.Percent, 50))),
        ("to-jpg", new EditJob([input], new OutputOptions(Suffix: "_d"), Convert: new ConvertOptions(ImageFormatKind.Jpeg, 70))),
        ("to-tif", new EditJob([input], new OutputOptions(Suffix: "_e"), Convert: new ConvertOptions(ImageFormatKind.Tiff))),
    ];

    [Fact]
    public void OP_E08_Metadata_Survives_Rotate_Resize_Convert()
    {
        string input = MakeJpeg();
        foreach (var (label, job) in Jobs(input))
        {
            var item = Assert.Single(EditPipeline.Run(job, null, null, CancellationToken.None).Items);
            Assert.True(item.Status == ItemStatus.Written, $"{label}: {item.Error} {item.Detail}");
            AssertCamera(item.Outputs[0]);
        }

        // TIFF output is a real TIFF, and TIFF → JPEG keeps the fields too.
        string tif = Temp.Combine("camera_e.tif");
        Assert.Equal("TIFF", ImageLoader.ReadInfo(tif).FormatName);
        var back = Assert.Single(EditPipeline.Run(
            new EditJob([tif], new OutputOptions(Suffix: "_back"), Convert: new ConvertOptions(ImageFormatKind.Jpeg)),
            null, null, CancellationToken.None).Items);
        Assert.Equal(ItemStatus.Written, back.Status);
        AssertCamera(back.Outputs[0]);
    }

    [Fact]
    public void OP_E08_Png_Target_Carries_No_Exif_But_Still_Writes()
    {
        string input = MakeJpeg();
        var item = Assert.Single(EditPipeline.Run(
            new EditJob([input], new OutputOptions(), Convert: new ConvertOptions(ImageFormatKind.Png)),
            null, null, CancellationToken.None).Items);
        Assert.Equal(ItemStatus.Written, item.Status);
        Assert.Equal(Temp.Combine("camera.png"), item.Outputs[0]);
        Assert.Empty(ImageLoader.ReadInfo(item.Outputs[0]).Camera);
    }
}
