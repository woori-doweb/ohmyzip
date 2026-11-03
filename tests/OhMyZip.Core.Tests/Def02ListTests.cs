using System.Formats.Tar;
using System.IO.Compression;

namespace OhMyZip.Core.Tests;

/// <summary>DEF-02(2026-10-27): 목록은 압축파일에 기록된 항목만 돌려준다. 경로로만 생기는 상위 폴더를 항목으로 합성하지 않는다.</summary>
public class Def02ListTests
{
    [Fact]
    public void OZ_E35_List_does_not_synthesize_implicit_parent_folders_zip_and_tar()
    {
        using var temp = new TempDir();
        string zip = temp.Combine("implicit.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var s = z.CreateEntry("p/q/x.txt").Open()) s.Write("x"u8);
            z.CreateEntry("e1/");
        }
        string tar = temp.Combine("implicit.tar");
        using (var fs = File.Create(tar))
        using (var w = new TarWriter(fs, TarEntryFormat.Pax))
        {
            var e = new PaxTarEntry(TarEntryType.RegularFile, "p/q/x.txt") { DataStream = new MemoryStream("x"u8.ToArray()) };
            w.WriteEntry(e);
        }

        var zipList = Archives.For(zip).List(zip);
        Assert.True(zipList.Result.Succeeded, zipList.Result.Message);
        Assert.Equal(["e1"], zipList.Entries.Where(e => e.IsDirectory).Select(e => e.Path));
        Assert.Contains(zipList.Entries, e => e.Path == "p/q/x.txt");

        var tarList = Archives.For(tar).List(tar);
        Assert.True(tarList.Result.Succeeded, tarList.Result.Message);
        Assert.DoesNotContain(tarList.Entries, e => e.IsDirectory);
        Assert.Equal(["p/q/x.txt"], tarList.Entries.Select(e => e.Path));

        // 해제는 경로의 상위 폴더를 그대로 만든다.
        string output = temp.Combine("out");
        Assert.True(Archives.For(tar).Extract(tar, output, new ExtractOptions()).Succeeded);
        Assert.True(File.Exists(Path.Combine(output, "p", "q", "x.txt")));
    }
}
