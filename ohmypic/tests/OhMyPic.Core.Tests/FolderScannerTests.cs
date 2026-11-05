namespace OhMyPic.Core.Tests;

public sealed class FolderScannerTests : TestBase
{
    private string MakeFolder()
    {
        string folder = Temp.Combine("browse");
        Directory.CreateDirectory(folder);
        var baseTime = new DateTime(2026, 1, 1, 12, 0, 0);
        // name, size in bytes, minutes after base
        (string Name, int Size, int Minutes)[] files =
        [
            ("file10.png", 300, 1),
            ("file2.png", 100, 3),
            ("File1.PNG", 500, 2),
            ("b.jpg", 200, 4),
            ("a.gif", 400, 0),
        ];
        foreach (var (name, size, minutes) in files)
        {
            string path = Path.Combine(folder, name);
            File.WriteAllBytes(path, new byte[size]);
            File.SetLastWriteTime(path, baseTime.AddMinutes(minutes));
        }
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "not an image");
        File.WriteAllBytes(Path.Combine(folder, "noext"), new byte[10]);
        string hidden = Path.Combine(folder, "hidden.png");
        File.WriteAllBytes(hidden, new byte[50]);
        File.SetLastWriteTime(hidden, baseTime.AddMinutes(5));
        File.SetAttributes(hidden, FileAttributes.Hidden);
        Directory.CreateDirectory(Path.Combine(folder, "sub.png"));
        return folder;
    }

    private static string[] Names(IReadOnlyList<ImageEntry> entries) => entries.Select(e => e.Name).ToArray();

    [Fact]
    public void OP_E13_Only_Supported_Extensions_Natural_Name_Order()
    {
        string folder = MakeFolder();
        var entries = FolderScanner.List(folder, SortKey.Name, descending: false, includeHidden: false);
        Assert.Equal(["a.gif", "b.jpg", "File1.PNG", "file2.png", "file10.png"], Names(entries));
        Assert.Equal(["file10.png", "file2.png", "File1.PNG", "b.jpg", "a.gif"],
            Names(FolderScanner.List(folder, SortKey.Name, descending: true, includeHidden: false)));

        var first = entries[2];
        Assert.Equal(Path.Combine(folder, "File1.PNG"), first.Path);
        Assert.Equal(".png", first.Extension);
        Assert.Equal(500, first.Size);
    }

    [Fact]
    public void OP_E13_Sort_By_Type_Size_Date_Both_Directions()
    {
        string folder = MakeFolder();
        Assert.Equal(["a.gif", "b.jpg", "File1.PNG", "file2.png", "file10.png"],
            Names(FolderScanner.List(folder, SortKey.Type, false, false)));
        // Descending type; equal types still in natural name order.
        Assert.Equal(["File1.PNG", "file2.png", "file10.png", "b.jpg", "a.gif"],
            Names(FolderScanner.List(folder, SortKey.Type, true, false)));
        Assert.Equal(["file2.png", "b.jpg", "file10.png", "a.gif", "File1.PNG"],
            Names(FolderScanner.List(folder, SortKey.Size, false, false)));
        Assert.Equal(["File1.PNG", "a.gif", "file10.png", "b.jpg", "file2.png"],
            Names(FolderScanner.List(folder, SortKey.Size, true, false)));
        Assert.Equal(["a.gif", "file10.png", "File1.PNG", "file2.png", "b.jpg"],
            Names(FolderScanner.List(folder, SortKey.Date, false, false)));
        Assert.Equal(["b.jpg", "file2.png", "File1.PNG", "file10.png", "a.gif"],
            Names(FolderScanner.List(folder, SortKey.Date, true, false)));
    }

    [Fact]
    public void OP_E13_Hidden_Files_Option()
    {
        string folder = MakeFolder();
        Assert.DoesNotContain("hidden.png", Names(FolderScanner.List(folder, SortKey.Name, false, includeHidden: false)));
        var all = FolderScanner.List(folder, SortKey.Date, true, includeHidden: true);
        Assert.Equal("hidden.png", all[0].Name);
        Assert.Equal(6, all.Count);
    }

    [Fact]
    public void OP_E13_Natural_Comparer()
    {
        var c = NaturalStringComparer.Instance;
        Assert.True(c.Compare("file2", "file10") < 0);
        Assert.True(c.Compare("File2", "file10") < 0);
        Assert.Equal(0, c.Compare("ABC", "abc"));
        Assert.True(c.Compare("a", "a1") < 0);
        Assert.True(c.Compare("img9.png", "img09.png") < 0);
        Assert.True(c.Compare("x99999999999999999999", "x100000000000000000000") < 0);
    }
}
