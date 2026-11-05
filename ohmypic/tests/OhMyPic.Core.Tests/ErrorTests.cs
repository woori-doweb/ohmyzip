namespace OhMyPic.Core.Tests;

public sealed class ErrorTests : TestBase
{
    private static readonly ResizeOptions Half = new(ResizeMode.Percent, 50);

    [Fact]
    public void OP_E16_Pipeline_Returns_Error_Codes_Not_Exceptions()
    {
        string corrupt = Temp.Combine("corrupt.png");
        File.WriteAllBytes(corrupt, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 1, 2, 3]);
        string garbage = Temp.Combine("garbage.jpg");
        File.WriteAllText(garbage, "this is not a jpeg");
        string unsupported = Temp.Combine("document.xyz");
        File.WriteAllText(unsupported, "x");
        string missing = Temp.Combine("missing.png");
        string huge = Temp.Combine("huge.png");
        Img.WriteHugePngHeader(huge);
        string good = Temp.Combine("good.png");
        Img.WritePng(good, Img.Palette(20, 20));
        string[] before = Util.FileNames(Temp.Path).ToArray();

        var result = EditPipeline.Run(
            new EditJob([corrupt, garbage, unsupported, missing, huge, good], new OutputOptions(Suffix: "_out"), Resize: Half),
            null, null, CancellationToken.None);

        Assert.Equal(
            [EditErrorCode.DecodeFailed, EditErrorCode.DecodeFailed, EditErrorCode.UnsupportedFormat, EditErrorCode.IoError, EditErrorCode.TooLarge, EditErrorCode.None],
            result.Items.Select(i => i.Error).ToArray());
        Assert.Equal(5, result.FailedCount);
        Assert.Equal(1, result.WrittenCount);
        Assert.All(result.Items.Take(5), i =>
        {
            Assert.Equal(ItemStatus.Failed, i.Status);
            Assert.Empty(i.Outputs);
            Assert.False(string.IsNullOrEmpty(i.Detail));
        });
        // No partial or temp files: only the inputs plus the one good output.
        Assert.Equal(before.Append("good_out.png").OrderBy(n => n, StringComparer.Ordinal), Util.FileNames(Temp.Path));
        Assert.Empty(Util.TempFilesUnder(Temp.Path));
    }

    [Fact]
    public void OP_E16_Loader_Throws_Typed_Errors()
    {
        string corrupt = Temp.Combine("corrupt.gif");
        File.WriteAllBytes(corrupt, [(byte)'G', (byte)'I', (byte)'F', (byte)'8', 0, 0]);
        string huge = Temp.Combine("huge.png");
        Img.WriteHugePngHeader(huge);

        Assert.Equal(EditErrorCode.DecodeFailed, Assert.Throws<ImageOperationException>(() => ImageLoader.ReadInfo(corrupt)).Code);
        Assert.Equal(EditErrorCode.DecodeFailed, Assert.Throws<ImageOperationException>(() => ImageLoader.LoadForDisplay(corrupt, true)).Code);
        Assert.Equal(EditErrorCode.UnsupportedFormat, Assert.Throws<ImageOperationException>(() => ImageLoader.ReadInfo(Temp.Combine("a.txt"))).Code);
        Assert.Equal(EditErrorCode.IoError, Assert.Throws<ImageOperationException>(() => ImageLoader.LoadThumbnail(Temp.Combine("none.png"), 100)).Code);
        Assert.Equal(EditErrorCode.TooLarge, Assert.Throws<ImageOperationException>(() => ImageLoader.LoadForDisplay(huge, true)).Code);
        Assert.Equal(EditErrorCode.TooLarge, Assert.Throws<ImageOperationException>(() => ImageLoader.LoadFrames(huge)).Code);

        // Header-only info still works for the oversized image.
        ImageInfo info = ImageLoader.ReadInfo(huge);
        Assert.Equal((20000, 20000), (info.Width, info.Height));
        // Files are not left locked after a failure.
        File.Delete(corrupt);
        File.Delete(huge);
    }
}
