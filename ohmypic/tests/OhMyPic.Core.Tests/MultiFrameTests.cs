using System.Windows.Media;

namespace OhMyPic.Core.Tests;

public sealed class MultiFrameTests : TestBase
{
    private static readonly Color[] FrameColors = [Colors.Red, Colors.Lime, Colors.Blue];

    private string Gif()
    {
        string path = Temp.Combine("anim.gif");
        Img.WriteThreeFrameGif(path);
        return path;
    }

    private static void AssertFrameColor(System.Windows.Media.Imaging.BitmapSource frame, Color expected)
    {
        byte[] px = Img.Bgra(frame);
        // (10, 10) is the frame colour, (1, 1) the white marker.
        Assert.Equal((expected.B, expected.G, expected.R, (byte)255), Img.At(px, frame.PixelWidth, 10, 10));
        Assert.Equal(((byte)255, (byte)255, (byte)255, (byte)255), Img.At(px, frame.PixelWidth, 1, 1));
    }

    private ItemResult Convert(string input, ImageFormatKind kind, FrameMode mode, string suffix) =>
        Assert.Single(EditPipeline.Run(
            new EditJob([input], new OutputOptions(Suffix: suffix), Convert: new ConvertOptions(kind, Frames: mode)),
            null, null, CancellationToken.None).Items);

    [Fact]
    public void OP_E15_Gif_Info_And_LoadFrames()
    {
        string gif = Gif();
        ImageInfo info = ImageLoader.ReadInfo(gif);
        Assert.Equal(3, info.FrameCount);
        Assert.Equal("GIF", info.FormatName);
        var frames = ImageLoader.LoadFrames(gif);
        Assert.Equal(3, frames.Count);
        for (int i = 0; i < 3; i++)
        {
            Assert.True(frames[i].IsFrozen);
            AssertFrameColor(frames[i], FrameColors[i]);
        }
    }

    [Fact]
    public void OP_E15_FirstOnly_Gives_One_File()
    {
        var item = Convert(Gif(), ImageFormatKind.Png, FrameMode.FirstOnly, "_first");
        Assert.Equal(ItemStatus.Written, item.Status);
        string output = Assert.Single(item.Outputs);
        Assert.Equal(Temp.Combine("anim_first.png"), output);
        Assert.Equal(1, Img.FrameCount(output));
        AssertFrameColor(Img.Decode(output), FrameColors[0]);
    }

    [Fact]
    public void OP_E15_SplitEach_Gives_Three_Files()
    {
        var item = Convert(Gif(), ImageFormatKind.Png, FrameMode.SplitEach, "");
        Assert.Equal(ItemStatus.Written, item.Status);
        Assert.Equal(3, item.Outputs.Count);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(Temp.Combine($"anim_{i + 1}.png"), item.Outputs[i]);
            AssertFrameColor(Img.Decode(item.Outputs[i]), FrameColors[i]);
        }
    }

    [Fact]
    public void OP_E15_CombineOne_Gives_Three_Page_Tiff()
    {
        var item = Convert(Gif(), ImageFormatKind.Tiff, FrameMode.CombineOne, "");
        Assert.Equal(ItemStatus.Written, item.Status);
        string tif = Assert.Single(item.Outputs);
        Assert.Equal(Temp.Combine("anim.tif"), tif);
        ImageInfo info = ImageLoader.ReadInfo(tif);
        Assert.Equal("TIFF", info.FormatName);
        Assert.Equal(3, info.FrameCount);
        for (int i = 0; i < 3; i++)
            AssertFrameColor(Img.Decode(tif, i), FrameColors[i]);

        // And back: TIFF pages → animated GIF keeps three frames.
        var gifBack = Convert(tif, ImageFormatKind.Gif, FrameMode.CombineOne, "_back");
        Assert.Equal(ItemStatus.Written, gifBack.Status);
        Assert.Equal(3, ImageLoader.ReadInfo(gifBack.Outputs[0]).FrameCount);
    }

    [Fact]
    public void OP_E15_CombineOne_Rejected_For_Single_Frame_Formats()
    {
        string gif = Gif();
        var item = Convert(gif, ImageFormatKind.Jpeg, FrameMode.CombineOne, "");
        Assert.Equal(ItemStatus.Failed, item.Status);
        Assert.Equal(EditErrorCode.UnsupportedFormat, item.Error);
        Assert.Equal(["anim.gif"], Util.FileNames(Temp.Path));
    }

    [Fact]
    public void OP_E15_Rotate_Without_Convert_Keeps_All_Gif_Frames()
    {
        var item = Assert.Single(EditPipeline.Run(
            new EditJob([Gif()], new OutputOptions(Suffix: "_r"), Rotate: new RotateOptions(RotateFlip.Rotate180)),
            null, null, CancellationToken.None).Items);
        Assert.Equal(ItemStatus.Written, item.Status);
        Assert.Equal(3, Img.FrameCount(item.Outputs[0]));
    }
}
