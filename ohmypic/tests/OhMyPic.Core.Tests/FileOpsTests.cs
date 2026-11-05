namespace OhMyPic.Core.Tests;

/// <summary>Temp-folder file operations. The Recycle Bin path is verified in the app tests (OP-U08), never here.</summary>
public sealed class FileOpsTests : TestBase
{
    private string Make(string name, string content)
    {
        string path = Temp.Combine(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void OP_E14_Copy_With_Conflicts()
    {
        string src = Fixtures.CopyTo("fixture-01.png", Temp, "src");
        string dst = Temp.Combine("dst");

        string? copied = FileOps.CopyTo(src, dst);
        Assert.Equal(Path.Combine(dst, "fixture-01.png"), copied);
        Assert.Equal(Util.Sha256(src), Util.Sha256(copied!));

        // Conflict: default skip, explicit rename, explicit overwrite.
        File.WriteAllText(copied!, "other");
        Assert.Null(FileOps.CopyTo(src, dst));
        Assert.Equal("other", File.ReadAllText(copied!));
        ConflictInfo? seen = null;
        string? renamed = FileOps.CopyTo(src, dst, c => { seen = c; return ConflictDecision.Rename; });
        Assert.Equal(Path.Combine(dst, "fixture-01 (2).png"), renamed);
        Assert.Equal(new ConflictInfo(src, copied!), seen);
        Assert.Equal(copied, FileOps.CopyTo(src, dst, _ => ConflictDecision.Overwrite));
        Assert.Equal(Util.Sha256(src), Util.Sha256(copied!));

        // Copy into its own folder makes a numbered copy.
        Assert.Equal(Temp.Combine("src", "fixture-01 (2).png"), FileOps.CopyTo(src, Temp.Combine("src")));
        Assert.Empty(Util.TempFilesUnder(Temp.Path));
    }

    [Fact]
    public void OP_E14_Move_With_Conflicts()
    {
        string a = Make("a/pic.png", "A");
        string dst = Temp.Combine("dst");
        string? moved = FileOps.MoveTo(a, dst);
        Assert.Equal(Path.Combine(dst, "pic.png"), moved);
        Assert.False(File.Exists(a));
        Assert.Equal("A", File.ReadAllText(moved!));

        string b = Make("b/pic.png", "B");
        Assert.Null(FileOps.MoveTo(b, dst));
        Assert.True(File.Exists(b));
        Assert.Equal(Path.Combine(dst, "pic (2).png"), FileOps.MoveTo(b, dst, _ => ConflictDecision.Rename));

        string c = Make("c/pic.png", "C");
        Assert.Equal(moved, FileOps.MoveTo(c, dst, _ => ConflictDecision.Overwrite));
        Assert.Equal("C", File.ReadAllText(moved!));
        Assert.False(File.Exists(c));

        // Same folder: no-op.
        Assert.Equal(moved, FileOps.MoveTo(moved!, dst));
        Assert.True(File.Exists(moved));
    }

    [Fact]
    public void OP_E14_Rename_Rules()
    {
        string path = Make("photo.jpg", "x");
        string renamed = FileOps.Rename(path, "holiday");
        Assert.Equal(Temp.Combine("holiday.jpg"), renamed);
        Assert.False(File.Exists(path));

        string withExt = FileOps.Rename(renamed, "holiday.jpeg");
        Assert.Equal(Temp.Combine("holiday.jpeg"), withExt);

        string caseOnly = FileOps.Rename(withExt, "Holiday.jpeg");
        Assert.Equal("Holiday.jpeg", Path.GetFileName(Directory.GetFiles(Temp.Path).Single()));
        Assert.Equal(Temp.Combine("Holiday.jpeg"), caseOnly);

        Make("taken.jpeg", "y");
        Assert.Throws<IOException>(() => FileOps.Rename(caseOnly, "taken"));
        Assert.Throws<ArgumentException>(() => FileOps.Rename(caseOnly, "bad:name"));
        Assert.Throws<ArgumentException>(() => FileOps.Rename(caseOnly, "a/b"));
        Assert.Throws<ArgumentException>(() => FileOps.Rename(caseOnly, "  "));
        Assert.True(File.Exists(caseOnly));
    }

    [Fact]
    public void OP_E14_Delete_Permanently()
    {
        string a = Make("a.png", "a"), b = Make("b.png", "b"), c = Make("c.png", "c");
        FileOps.DeletePermanently(a);
        Assert.False(File.Exists(a));
        FileOps.DeletePermanently([b, c]);
        Assert.Empty(Directory.GetFiles(Temp.Path));
        // Already gone: no error.
        FileOps.DeletePermanently(a);
    }
}
