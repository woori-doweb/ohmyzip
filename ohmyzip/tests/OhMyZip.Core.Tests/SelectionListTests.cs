using System.Text;

namespace OhMyZip.Core.Tests;

/// <summary>탐색기 메뉴 처리기가 넘기는 선택 목록 파일(--list)의 해석·정리와 '「이름.zip」으로 압축' 대상 이름. 모든 파일은 임시 폴더에 만든다.</summary>
public sealed class SelectionListTests : IDisposable
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    private string MakeFile(params string[] parts)
    {
        string path = _temp.Combine(parts);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    private string MakeFolder(params string[] parts) => Directory.CreateDirectory(_temp.Combine(parts)).FullName;

    private string WriteList(string content, Encoding? encoding = null)
    {
        string list = _temp.Combine("selections", Guid.NewGuid().ToString("D") + ".txt");
        Directory.CreateDirectory(Path.GetDirectoryName(list)!);
        File.WriteAllText(list, content, encoding ?? Utf8NoBom);
        return list;
    }

    [Fact]
    public void OZ_I05_Read_returns_existing_full_paths_in_order_and_deletes_the_list()
    {
        string file = MakeFile("items", "report.txt");
        string folder = MakeFolder("items", "photos");
        string korean = MakeFile("items", "한글 이름.txt");
        string missing = _temp.Combine("items", "missing.txt");
        string list = WriteList(
            file + "\r\n"
            + "\r\n"
            + "   " + folder + "  \r\n"
            + missing + "\r\n"
            + "relative.txt\r\n"
            + "\t\r\n"
            + korean + "\n");

        var paths = SelectionList.Read(list);

        Assert.Equal(new[] { file, folder, korean }, paths.ToArray());
        Assert.False(File.Exists(list));
    }

    [Fact]
    public void OZ_I05_Read_accepts_a_byte_order_mark()
    {
        string file = MakeFile("bom", "a.zip");
        string list = WriteList(file + "\r\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Assert.Equal(new[] { file }, SelectionList.Read(list).ToArray());
        Assert.False(File.Exists(list));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\r\n\r\n   \r\n")]
    public void OZ_I05_Read_of_an_empty_list_returns_nothing_and_deletes_it(string content)
    {
        string list = WriteList(content);

        Assert.Empty(SelectionList.Read(list));
        Assert.False(File.Exists(list));
    }

    [Fact]
    public void OZ_I05_Read_of_only_missing_paths_returns_nothing_and_deletes_the_list()
    {
        string list = WriteList(_temp.Combine("gone", "a.zip") + "\r\n" + _temp.Combine("gone", "b") + "\r\n");

        Assert.Empty(SelectionList.Read(list));
        Assert.False(File.Exists(list));
    }

    [Fact]
    public void OZ_I05_Read_of_a_missing_list_file_returns_nothing()
    {
        Assert.Empty(SelectionList.Read(_temp.Combine("selections", "missing.txt")));
        Assert.Empty(SelectionList.Read(_temp.Combine("no-folder", "missing.txt")));
        Assert.Throws<ArgumentException>(() => SelectionList.Read(" "));
    }

    [Fact]
    public void OZ_I05_CleanupStale_deletes_only_old_lists_and_ignores_errors()
    {
        string dir = MakeFolder("selections");
        DateTime now = new(2026, 11, 15, 4, 0, 0, DateTimeKind.Utc);
        string old = Path.Combine(dir, "old.txt");
        string fresh = Path.Combine(dir, "fresh.txt");
        string oldOther = Path.Combine(dir, "old.log");
        string locked = Path.Combine(dir, "locked.txt");
        foreach (string path in new[] { old, fresh, oldOther, locked })
            File.WriteAllText(path, "C:\\a.zip");
        File.SetLastWriteTimeUtc(old, now - TimeSpan.FromDays(2));
        File.SetLastWriteTimeUtc(fresh, now - TimeSpan.FromHours(23));
        File.SetLastWriteTimeUtc(oldOther, now - TimeSpan.FromDays(2));
        File.SetLastWriteTimeUtc(locked, now - TimeSpan.FromDays(3));

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            SelectionList.CleanupStale(dir, TimeSpan.FromDays(1), now);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(oldOther));
        // 지우지 못한 파일은 오류 없이 남는다(다음 실행 때 다시 정리).
        Assert.True(File.Exists(locked));

        SelectionList.CleanupStale(dir, TimeSpan.FromDays(1), now);
        Assert.False(File.Exists(locked));
        Assert.True(File.Exists(fresh));

        // 폴더가 없어도 조용히 넘어간다.
        SelectionList.CleanupStale(_temp.Combine("missing"), TimeSpan.FromDays(1), now);
        SelectionList.CleanupStale("", TimeSpan.FromDays(1), now);
    }

    [Fact]
    public void OZ_I05_DefaultDirectory_is_under_local_app_data()
    {
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ohmyZip", "selections"),
            SelectionList.DefaultDirectory);
    }

    [Fact]
    public void OZ_I05_CompressTargetPath_names_a_single_item_after_itself()
    {
        string parent = MakeFolder("single");
        Assert.Equal(Path.Combine(parent, "report.zip"), SelectionList.CompressTargetPath([MakeFile("single", "report.txt")]));
        Assert.Equal(Path.Combine(parent, "README.zip"), SelectionList.CompressTargetPath([MakeFile("single", "README")]));
        Assert.Equal(Path.Combine(parent, "backup.tar.zip"), SelectionList.CompressTargetPath([MakeFile("single", "backup.tar.gz")]));
        Assert.Equal(Path.Combine(parent, ".gitignore.zip"), SelectionList.CompressTargetPath([MakeFile("single", ".gitignore")]));
        // 폴더는 확장자처럼 보이는 부분도 이름이다.
        string folder = MakeFolder("single", "photos.2026");
        Assert.Equal(Path.Combine(parent, "photos.2026.zip"), SelectionList.CompressTargetPath([folder]));
        Assert.Equal(Path.Combine(parent, "photos.2026.zip"), SelectionList.CompressTargetPath([folder + Path.DirectorySeparatorChar]));
    }

    [Fact]
    public void OZ_I05_CompressTargetPath_names_several_items_after_the_parent_folder()
    {
        string parent = MakeFolder("project");
        string a = MakeFile("project", "a.txt");
        string b = MakeFolder("project", "docs");
        string c = MakeFile("project", "c.bin");

        Assert.Equal(Path.Combine(parent, "project.zip"), SelectionList.CompressTargetPath([a, b, c]));
        // 부모 폴더 비교는 대소문자를 가리지 않는다.
        Assert.Equal(Path.Combine(parent, "project.zip"), SelectionList.CompressTargetPath([a, c.ToUpperInvariant()]));
    }

    [Fact]
    public void OZ_I05_CompressTargetPath_numbers_names_that_already_exist()
    {
        string parent = MakeFolder("collide");
        string report = MakeFile("collide", "report.txt");
        MakeFile("collide", "report.zip");

        Assert.Equal(Path.Combine(parent, "report (2).zip"), SelectionList.CompressTargetPath([report]));
        MakeFile("collide", "report (2).zip");
        // 같은 이름의 폴더도 겹친 것으로 본다.
        MakeFolder("collide", "report (3).zip");
        Assert.Equal(Path.Combine(parent, "report (4).zip"), SelectionList.CompressTargetPath([report]));

        string other = MakeFile("collide", "other.txt");
        MakeFile("collide", "collide.zip");
        Assert.Equal(Path.Combine(parent, "collide (2).zip"), SelectionList.CompressTargetPath([report, other]));
    }

    [Fact]
    public void OZ_I05_CompressTargetPath_rejects_different_parent_folders_and_roots()
    {
        string a = MakeFile("left", "a.txt");
        string b = MakeFile("right", "b.txt");

        Assert.Null(SelectionList.CompressTargetPath([a, b]));
        Assert.Null(SelectionList.CompressTargetPath([Path.GetPathRoot(_temp.Path)!]));
        Assert.Throws<ArgumentException>(() => SelectionList.CompressTargetPath([]));
    }
}
