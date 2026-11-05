namespace OhMyPic.Core.Tests;

public sealed class CancellationTests : TestBase
{
    private string[] Inputs()
    {
        string[] inputs = Fixtures.Names.Select(n => Fixtures.CopyTo(n, Temp, "in")).ToArray();
        return inputs;
    }

    [Fact]
    public void OP_E20_Cancel_Between_Items_Leaves_Rest_Unprocessed()
    {
        string[] inputs = Inputs();
        string outFolder = Temp.Combine("out");
        using var cts = new CancellationTokenSource();
        // Cancel as soon as the first item is done (progress reports Done = 1 before item 2 starts).
        var progress = new SyncProgress(p =>
        {
            if (p.Done == 1)
                cts.Cancel();
        });

        var result = EditPipeline.Run(
            new EditJob(inputs, new OutputOptions(OutputFolderMode.Specified, FolderPath: outFolder), Resize: new ResizeOptions(ResizeMode.Percent, 25)),
            null, progress, cts.Token);

        Assert.Equal(3, result.Items.Count);
        Assert.Equal(ItemStatus.Written, result.Items[0].Status);
        Assert.All(result.Items.Skip(1), i =>
        {
            Assert.Equal(ItemStatus.Skipped, i.Status);
            Assert.Equal(EditErrorCode.Cancelled, i.Error);
            Assert.Empty(i.Outputs);
        });
        Assert.True(result.Cancelled);
        Assert.Equal(["fixture-01.png"], Util.FileNames(outFolder));
        Assert.Empty(Util.TempFilesUnder(Temp.Path));
    }

    [Fact]
    public void OP_E20_Cancel_During_Item_Removes_Temp_File()
    {
        string[] inputs = Inputs();
        using var cts = new CancellationTokenSource();
        // Conflict prompt for item 1 arrives mid-item; the user cancels the job there and answers "overwrite".
        string outFolder = Temp.Combine("out");
        Directory.CreateDirectory(outFolder);
        File.WriteAllText(Path.Combine(outFolder, "fixture-01.png"), "existing");
        int asked = 0;

        var result = EditPipeline.Run(
            new EditJob(inputs, new OutputOptions(OutputFolderMode.Specified, FolderPath: outFolder, Conflict: ConflictPolicy.Ask), Resize: new ResizeOptions(ResizeMode.Percent, 25)),
            _ =>
            {
                asked++;
                cts.Cancel();
                return ConflictDecision.Overwrite;
            },
            null, cts.Token);

        Assert.Equal(1, asked);
        Assert.All(result.Items, i => Assert.Equal(EditErrorCode.Cancelled, i.Error));
        Assert.All(result.Items, i => Assert.Empty(i.Outputs));
        // The existing file was not replaced and the encoded temp file is gone.
        Assert.Equal("existing", File.ReadAllText(Path.Combine(outFolder, "fixture-01.png")));
        Assert.Equal(["fixture-01.png"], Util.FileNames(outFolder));
        Assert.Empty(Util.TempFilesUnder(Temp.Path));
    }

    [Fact]
    public void OP_E20_Already_Cancelled_Token_Processes_Nothing()
    {
        string[] inputs = Inputs();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = EditPipeline.Run(
            new EditJob(inputs, new OutputOptions(Suffix: "_x"), Rotate: new RotateOptions(RotateFlip.Rotate90)),
            null, null, cts.Token);
        Assert.Equal(3, result.Items.Count);
        Assert.All(result.Items, i => Assert.Equal(EditErrorCode.Cancelled, i.Error));
        Assert.Equal(Fixtures.Names, Util.FileNames(Temp.Combine("in")));
    }
}
