using System.IO.Compression;

namespace OhMyZip.Core.Tests;

public class ExtractSafetyTests
{
    [Fact]
    public void OZ_E07_Zip_slip_and_absolute_entries_are_rejected_and_nothing_escapes_root()
    {
        using var temp = new TempDir();
        string marker = $"oz-evil-{Guid.NewGuid():N}.txt";
        string driveRoot = Path.GetPathRoot(temp.Path)!;
        string zip = temp.Combine("evil.zip");
        Util.WriteZip(zip,
            ($"../{marker}", Util.Text("slip")),
            ($"{driveRoot[0]}:/{marker}", Util.Text("drive")),
            ($"/{marker}", Util.Text("rooted")),
            ($"a/../../{marker}", Util.Text("nested slip")),
            ($"..\\{marker}", Util.Text("backslash slip")),
            ("ok.txt", Util.Text("fine")));
        string output = temp.Combine("sandbox", "root");

        var result = Archives.For(zip).Extract(zip, output, new ExtractOptions { Conflict = ConflictPolicy.Overwrite });

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(5, result.Problems.Count(p => p.Contains(marker)));
        Assert.Equal(["ok.txt"], Util.FilesUnder(output));
        Assert.Equal(["root/ok.txt"], Util.FilesUnder(temp.Combine("sandbox")));
        Assert.False(File.Exists(Path.Combine(temp.Path, marker)));
        Assert.False(File.Exists(Path.Combine(driveRoot, marker)));
        Assert.Empty(Directory.GetFiles(temp.Path, marker, SearchOption.AllDirectories));
    }

    [Fact]
    public void OZ_E07_Symlink_entries_are_rejected()
    {
        using var temp = new TempDir();
        string zip = temp.Combine("link.zip");
        int symlinkMode = unchecked((int)(0xA1FFu << 16));
        Util.WriteZip(zip, CompressionLevel.Optimal,
        [
            ("link", Util.Text("C:/Windows"), symlinkMode),
            ("plain.txt", Util.Text("plain"), 0),
        ]);
        string output = temp.Combine("out");

        var result = new ZipEngine().Extract(zip, output, new ExtractOptions());

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Contains(result.Problems, p => p.Contains("link") && p.Contains("링크"));
        Assert.Equal(["plain.txt"], Util.FilesUnder(output));
    }

    [Fact]
    public void OZ_E08_Conflict_policies_skip_by_default_overwrite_and_rename()
    {
        using var temp = new TempDir();
        string zip = Fixtures.CopyTo("sample.zip", temp);
        string output = temp.Combine("out");
        Directory.CreateDirectory(output);
        string readme = Path.Combine(output, "readme.txt");
        File.WriteAllText(readme, "내 파일");
        string localHash = Util.Sha256(readme);
        var engine = new ZipEngine();

        Assert.Equal(ConflictPolicy.Skip, new ExtractOptions().Conflict);
        var skip = engine.Extract(zip, output, new ExtractOptions());
        Assert.Equal(OperationStatus.Succeeded, skip.Status);
        Assert.Contains(skip.Problems, p => p.Contains("readme.txt"));
        Assert.Equal(localHash, Util.Sha256(readme));
        Assert.Equal(Fixtures.KoreanHash, Util.Sha256(Path.Combine(output, Fixtures.KoreanName)));

        var rename = engine.Extract(zip, output, new ExtractOptions { Conflict = ConflictPolicy.Rename });
        Assert.True(rename.Succeeded, rename.Message);
        Assert.Equal(localHash, Util.Sha256(readme));
        Assert.Equal(Fixtures.ReadmeHash, Util.Sha256(Path.Combine(output, "readme (2).txt")));
        Assert.Equal(Fixtures.KoreanHash, Util.Sha256(Path.Combine(output, "한글이름 (2).txt")));
        Assert.Equal(Fixtures.NotesHash, Util.Sha256(Path.Combine(output, "nested", "notes (2).txt")));

        var overwrite = engine.Extract(zip, output, new ExtractOptions { Conflict = ConflictPolicy.Overwrite });
        Assert.True(overwrite.Succeeded, overwrite.Message);
        Assert.Equal(Fixtures.ReadmeHash, Util.Sha256(readme));
        Assert.Equal(6, Util.FilesUnder(output).Count);
        Assert.Empty(Directory.GetFiles(output, "~oz*", SearchOption.AllDirectories));
    }

    [Fact]
    public void OZ_E09_Bomb_exceeding_total_byte_limit_aborts_and_removes_partial_files()
    {
        using var temp = new TempDir();
        string zip = temp.Combine("bomb.zip");
        Util.WriteZip(zip,
            ("first.txt", Util.Text("작은 파일")),
            ("bomb.bin", new byte[50 * 1024 * 1024]));
        Assert.True(new FileInfo(zip).Length < 1024 * 1024, "압축된 폭탄은 1MB 보다 작아야 시험 의미가 있다");
        string output = temp.Combine("out");

        var result = new ZipEngine().Extract(zip, output, new ExtractOptions { MaxTotalBytes = 1024 * 1024 });

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Contains("한도", result.Message);
        Assert.False(Directory.Exists(output), "이번 실행이 만든 대상 폴더까지 지워야 한다");
        Assert.Equal(["bomb.zip"], Util.FilesUnder(temp.Path));
    }

    [Fact]
    public void OZ_E09_Entry_count_limit_fails_before_writing()
    {
        using var temp = new TempDir();
        string zip = temp.Combine("many.zip");
        Util.WriteZip(zip, Enumerable.Range(0, 10).Select(i => ($"f{i}.txt", Util.Text(i.ToString()))).ToArray());
        string output = temp.Combine("out");
        Directory.CreateDirectory(output);

        var result = new ZipEngine().Extract(zip, output, new ExtractOptions { MaxEntries = 5 });

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Empty(Util.FilesUnder(output));
    }

    [Fact]
    public void OZ_E10_Cancel_mid_extract_removes_new_files_restores_overwritten_and_keeps_archive()
    {
        using var temp = new TempDir();
        string zip = temp.Combine("big.zip");
        var rnd = new Random(1);
        byte[] a = new byte[2 * 1024 * 1024];
        byte[] b = new byte[5 * 1024 * 1024];
        rnd.NextBytes(a);
        rnd.NextBytes(b);
        Util.WriteZip(zip, ("a.bin", a), ("sub/b.bin", b), ("c.txt", Util.Text("c")));
        string archiveHash = Util.Sha256(zip);
        string output = temp.Combine("out");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "a.bin"), "ORIGINAL");
        File.WriteAllText(Path.Combine(output, "keep.txt"), "keep");

        using var cts = new CancellationTokenSource();
        long lastSeen = 0;
        var progress = new SyncProgress(p =>
        {
            lastSeen = p.ProcessedBytes;
            if (p.ProcessedBytes > 3 * 1024 * 1024)
                cts.Cancel();
        });

        var result = Archives.For(zip).Extract(zip, output, new ExtractOptions { Conflict = ConflictPolicy.Overwrite }, progress, cts.Token);

        Assert.Equal(OperationStatus.Cancelled, result.Status);
        Assert.True(lastSeen > 3 * 1024 * 1024 && lastSeen < a.Length + b.Length, "취소는 해제 도중이어야 한다");
        Assert.Equal(["a.bin", "keep.txt"], Util.FilesUnder(output));
        Assert.Equal("ORIGINAL", File.ReadAllText(Path.Combine(output, "a.bin")));
        Assert.False(Directory.Exists(Path.Combine(output, "sub")));
        Assert.Equal(archiveHash, Util.Sha256(zip));
    }

    [Fact]
    public void OZ_E10_Cancel_mid_create_leaves_no_archive()
    {
        using var temp = new TempDir();
        string source = temp.Combine("src");
        Directory.CreateDirectory(source);
        var rnd = new Random(2);
        byte[] data = new byte[4 * 1024 * 1024];
        rnd.NextBytes(data);
        File.WriteAllBytes(Path.Combine(source, "big.bin"), data);
        string zip = temp.Combine("new.zip");
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p =>
        {
            if (p.ProcessedBytes > 1024 * 1024)
                cts.Cancel();
        });

        var result = new ZipEngine().Create(zip, [source], null, progress, cts.Token);

        Assert.Equal(OperationStatus.Cancelled, result.Status);
        Assert.False(File.Exists(zip));
        Assert.Empty(Directory.GetFiles(temp.Path));
    }
}
