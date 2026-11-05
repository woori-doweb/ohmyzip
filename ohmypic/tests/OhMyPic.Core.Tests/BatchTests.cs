namespace OhMyPic.Core.Tests;

public sealed class BatchTests : TestBase
{
    [Fact]
    public void OP_E11_Batch_Resize_Rotate_Rename_Convert_Fixtures()
    {
        string[] inputs = Fixtures.Names.Select(n => Fixtures.CopyTo(n, Temp, "in")).ToArray();
        var inputHashes = inputs.ToDictionary(p => p, Util.Sha256);
        string outFolder = Temp.Combine("out");
        var progress = new List<JobProgress>();

        var job = new EditJob(
            inputs,
            new OutputOptions(OutputFolderMode.Specified, FolderPath: outFolder),
            Resize: new ResizeOptions(ResizeMode.Percent, 50),
            Rotate: new RotateOptions(RotateFlip.Rotate90),
            Rename: new RenameRule("batch_[%N]"),
            Convert: new ConvertOptions(ImageFormatKind.Jpeg, 85));
        var result = EditPipeline.Run(job, null, new SyncProgress(progress.Add), CancellationToken.None);

        Assert.Equal(3, result.WrittenCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(["batch_1.jpg", "batch_2.jpg", "batch_3.jpg"], Util.FileNames(outFolder));
        for (int i = 0; i < 3; i++)
        {
            var item = result.Items[i];
            Assert.Equal(inputs[i], item.Input);
            Assert.Equal(Path.Combine(outFolder, $"batch_{i + 1}.jpg"), Assert.Single(item.Outputs));
            ImageInfo info = ImageLoader.ReadInfo(item.Outputs[0]);
            Assert.Equal("JPEG", info.FormatName);
            Assert.Equal((400, 640), (info.Width, info.Height));
            // Direction: matches the original resized then rotated clockwise.
            var expected = ImageTransforms.Rotate(
                ImageTransforms.Resize(Img.Decode(inputs[i]), new ResizeOptions(ResizeMode.Percent, 50)), RotateFlip.Rotate90);
            double psnr = Img.Psnr(expected, Img.Decode(item.Outputs[0]));
            Assert.True(psnr >= 30, $"batch_{i + 1}.jpg PSNR {psnr:F1}");
        }

        foreach (var (path, hash) in inputHashes)
            Assert.Equal(hash, Util.Sha256(path));
        Assert.Equal(new JobProgress(0, 3, inputs[0]), progress[0]);
        Assert.Equal(new JobProgress(3, 3, ""), progress[^1]);
        Assert.Empty(Util.TempFilesUnder(Temp.Path));
    }
}
