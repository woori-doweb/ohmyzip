namespace OhMyPic.Core.Tests;

public sealed class OutputTests : TestBase
{
    private static readonly ResizeOptions Half = new(ResizeMode.Percent, 50);

    private string Input(string name = "pic.png")
    {
        string path = Temp.Combine(name);
        Img.WritePng(path, Img.Palette(40, 20));
        return path;
    }

    private static ItemResult Run(string input, OutputOptions output, Func<ConflictInfo, ConflictDecision>? ask = null) =>
        Assert.Single(EditPipeline.Run(new EditJob([input], output, Resize: Half), ask, null, CancellationToken.None).Items);

    [Fact]
    public void OP_E10_Output_SameFolder_SubFolder_Specified_With_Prefix_Suffix()
    {
        string input = Input();

        var same = Run(input, new OutputOptions(Prefix: "pre_", Suffix: "_suf"));
        Assert.Equal(Temp.Combine("pre_pic_suf.png"), Assert.Single(same.Outputs));

        var sub = Run(input, new OutputOptions(OutputFolderMode.SubFolder));
        Assert.Equal(Temp.Combine("edited", "pic.png"), Assert.Single(sub.Outputs));

        var subNamed = Run(input, new OutputOptions(OutputFolderMode.SubFolder, SubFolderName: "small"));
        Assert.Equal(Temp.Combine("small", "pic.png"), Assert.Single(subNamed.Outputs));

        string target = Temp.Combine("elsewhere", "deep");
        var specified = Run(input, new OutputOptions(OutputFolderMode.Specified, FolderPath: target, Suffix: "-x"));
        Assert.Equal(Path.Combine(target, "pic-x.png"), Assert.Single(specified.Outputs));

        foreach (var r in new[] { same, sub, subNamed, specified })
        {
            Assert.Equal(ItemStatus.Written, r.Status);
            Assert.Equal(20, ImageLoader.ReadInfo(r.Outputs[0]).Width);
        }
        // Input untouched.
        Assert.Equal(40, ImageLoader.ReadInfo(input).Width);

        var missingFolder = Run(input, new OutputOptions(OutputFolderMode.Specified));
        Assert.Equal(ItemStatus.Failed, missingFolder.Status);
        Assert.Equal(EditErrorCode.IoError, missingFolder.Error);
    }

    [Fact]
    public void OP_E10_Conflict_Skip_Overwrite_Ask()
    {
        string input = Input();
        string existing = Temp.Combine("pic_s.png");
        File.WriteAllText(existing, "keep me");

        var skip = Run(input, new OutputOptions(Suffix: "_s", Conflict: ConflictPolicy.Skip));
        Assert.Equal(ItemStatus.Skipped, skip.Status);
        Assert.Equal("keep me", File.ReadAllText(existing));

        // Ask without a callback behaves like Skip.
        var askNull = Run(input, new OutputOptions(Suffix: "_s", Conflict: ConflictPolicy.Ask));
        Assert.Equal(ItemStatus.Skipped, askNull.Status);

        var asked = new List<ConflictInfo>();
        var askSkip = Run(input, new OutputOptions(Suffix: "_s", Conflict: ConflictPolicy.Ask), c => { asked.Add(c); return ConflictDecision.Skip; });
        Assert.Equal(ItemStatus.Skipped, askSkip.Status);
        Assert.Equal(new ConflictInfo(input, existing), Assert.Single(asked));

        var renamed = Run(input, new OutputOptions(Suffix: "_s", Conflict: ConflictPolicy.Ask), _ => ConflictDecision.Rename);
        Assert.Equal(Temp.Combine("pic_s (2).png"), Assert.Single(renamed.Outputs));
        var renamedAgain = Run(input, new OutputOptions(Suffix: "_s", Conflict: ConflictPolicy.Ask), _ => ConflictDecision.Rename);
        Assert.Equal(Temp.Combine("pic_s (3).png"), Assert.Single(renamedAgain.Outputs));
        Assert.Equal("keep me", File.ReadAllText(existing));

        var overwrite = Run(input, new OutputOptions(Suffix: "_s", Conflict: ConflictPolicy.Overwrite));
        Assert.Equal(ItemStatus.Written, overwrite.Status);
        Assert.Equal(20, ImageLoader.ReadInfo(existing).Width);

        var askOverwrite = Run(input, new OutputOptions(Suffix: "_s", Conflict: ConflictPolicy.Ask), _ => ConflictDecision.Overwrite);
        Assert.Equal(ItemStatus.Written, askOverwrite.Status);
        Assert.Empty(Util.TempFilesUnder(Temp.Path));
    }

    [Fact]
    public void OP_E10_DeleteOriginal_Only_After_Verified_Output()
    {
        // Successful edit: original removed, output present and decodable.
        string input = Input("a.png");
        var ok = Run(input, new OutputOptions(Suffix: "_s", DeleteOriginal: true));
        Assert.Equal(ItemStatus.Written, ok.Status);
        Assert.False(File.Exists(input));
        Assert.Equal(20, ImageLoader.ReadInfo(ok.Outputs[0]).Width);

        // Failed decode: original kept.
        string corrupt = Temp.Combine("broken.png");
        File.WriteAllBytes(corrupt, [0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4, 5]);
        var failed = Run(corrupt, new OutputOptions(Suffix: "_s", DeleteOriginal: true));
        Assert.Equal(ItemStatus.Failed, failed.Status);
        Assert.True(File.Exists(corrupt));

        // Output skipped by conflict: original kept.
        string b = Input("b.png");
        File.WriteAllText(Temp.Combine("b_s.png"), "x");
        var skipped = Run(b, new OutputOptions(Suffix: "_s", DeleteOriginal: true));
        Assert.Equal(ItemStatus.Skipped, skipped.Status);
        Assert.True(File.Exists(b));

        // Output path == input path (overwrite in place): the result is the file, never deleted.
        string c = Input("c.png");
        var inPlace = Run(c, new OutputOptions(Conflict: ConflictPolicy.Overwrite, DeleteOriginal: true));
        Assert.Equal(ItemStatus.Written, inPlace.Status);
        Assert.Equal(c, inPlace.Outputs[0]);
        Assert.Equal(20, ImageLoader.ReadInfo(c).Width);
    }

    [Fact]
    public void OP_E10_Two_Inputs_Same_Output_Name_Do_Not_Clobber()
    {
        string a = Input("same.png");
        Directory.CreateDirectory(Temp.Combine("other"));
        string b = Temp.Combine("other", "same.png");
        Img.WritePng(b, Img.Palette(60, 30));
        string target = Temp.Combine("out");

        var result = EditPipeline.Run(
            new EditJob([a, b], new OutputOptions(OutputFolderMode.Specified, FolderPath: target), Resize: Half),
            null, null, CancellationToken.None);

        Assert.Equal(ItemStatus.Written, result.Items[0].Status);
        Assert.Equal(ItemStatus.Skipped, result.Items[1].Status);
        Assert.Equal(1, result.WrittenCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.Equal(20, ImageLoader.ReadInfo(Path.Combine(target, "same.png")).Width);
    }
}
