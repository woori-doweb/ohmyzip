namespace OhMyZip.Core.Tests;

public class SevenZipAndFormatTests
{
    [Fact]
    public void OZ_E04_SevenZip_list_test_extract_matches_original_hashes()
    {
        using var temp = new TempDir();
        string archive = Fixtures.CopyTo("created.7z", temp);

        Assert.Equal(ArchiveFormat.SevenZip, Archives.Detect(archive));
        var engine = Archives.For(archive);
        Assert.IsType<SevenZipEngine>(engine);

        var list = engine.List(archive);
        Assert.True(list.Result.Succeeded, list.Result.Message);
        var files = list.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Path);
        Assert.Equal(["nested/notes.txt", "readme.txt", Fixtures.KoreanName], files.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(["empty", "nested"], list.Entries.Where(e => e.IsDirectory).Select(e => e.Path).OrderBy(p => p, StringComparer.Ordinal));
        Assert.Equal(52, files["readme.txt"].Size);

        var test = engine.Test(archive);
        Assert.True(test.Succeeded, test.Message + string.Join("; ", test.Problems));

        string output = temp.Combine("out");
        var result = engine.Extract(archive, output, new ExtractOptions());
        Assert.True(result.Succeeded, result.Message + string.Join("; ", result.Problems));
        Util.AssertExpectedHashes(output);
        Assert.True(Directory.Exists(Path.Combine(output, "empty")));
        Assert.Equal(3, Util.FilesUnder(output).Count);

        // 두 번째 해제: 기본 Skip 은 스트림을 열지 않고 다음 항목으로 넘어가야 한다(솔리드 7Z).
        var again = engine.Extract(archive, output, new ExtractOptions());
        Assert.Equal(OperationStatus.Succeeded, again.Status);
        Assert.Equal(3, again.Problems.Count);
        Util.AssertExpectedHashes(output);

        string only = temp.Combine("only");
        Assert.True(engine.Extract(archive, only, new ExtractOptions { Only = [Fixtures.KoreanName] }).Succeeded);
        Assert.Equal([Fixtures.KoreanName], Util.FilesUnder(only));
        Assert.Equal(Fixtures.KoreanHash, Util.Sha256(Path.Combine(only, Fixtures.KoreanName)));
    }

    [Fact]
    public void OZ_E10_SevenZip_cancel_during_extract_removes_created_files()
    {
        using var temp = new TempDir();
        string archive = Fixtures.CopyTo("created.7z", temp);
        string before = Util.Sha256(archive);
        string output = temp.Combine("out");
        using var cts = new CancellationTokenSource();
        int reports = 0;
        var progress = new SyncProgress(_ =>
        {
            if (++reports == 2)
                cts.Cancel();
        });

        var result = new SevenZipEngine().Extract(archive, output, new ExtractOptions(), progress, cts.Token);

        Assert.Equal(OperationStatus.Cancelled, result.Status);
        Assert.True(reports >= 2);
        Assert.False(Directory.Exists(output));
        Assert.Equal(before, Util.Sha256(archive));
    }

    [Fact]
    public void OZ_E04_SevenZip_corrupted_archive_is_failed_result()
    {
        using var temp = new TempDir();
        string archive = Fixtures.CopyTo("created.7z", temp);
        byte[] bytes = File.ReadAllBytes(archive);
        bytes[40] ^= 0xFF; // 압축 데이터 안쪽 한 바이트
        File.WriteAllBytes(archive, bytes);
        var engine = new SevenZipEngine();

        Assert.Equal(OperationStatus.Failed, engine.Test(archive).Status);
        string output = temp.Combine("out");
        Assert.Equal(OperationStatus.Failed, engine.Extract(archive, output, new ExtractOptions()).Status);
        Assert.Empty(Directory.GetFiles(temp.Path, "~oz*", SearchOption.AllDirectories));
    }

    // OZ-E12 → OZ-E20 (2026-10-27): 7Z 쓰기 구현으로 NotSupported 기대를 긍정 시험으로 교체
    [Fact]
    public void OZ_E12_SevenZip_created_7z_fixture_can_be_modified_by_rewrite()
    {
        using var temp = new TempDir();
        string archive = Fixtures.CopyTo("created.7z", temp);
        string added = Fixtures.CopyTo("added.txt", temp);
        var engine = Archives.For(archive);

        Assert.Equal(new FormatCapabilities(true, true, true, true, true, true, true, false), engine.Capabilities);
        Assert.Equal(OperationStatus.NotSupported, engine.SetComment(archive, "x").Status);
        Assert.True(engine.Add(archive, [added]).Succeeded);
        string output = temp.Combine("out");
        Assert.True(engine.Extract(archive, output, new ExtractOptions()).Succeeded);
        Util.AssertExpectedHashes(output);
        Assert.Equal(Util.Sha256(added), Util.Sha256(Path.Combine(output, "added.txt")));
    }

    [Fact]
    public void OZ_E12_Unknown_format_returns_failed_results_never_throws()
    {
        using var temp = new TempDir();
        string invalid = Fixtures.CopyTo("invalid.zip", temp);
        string missing = temp.Combine("missing.zip");

        Assert.Equal(ArchiveFormat.Unknown, Archives.Detect(invalid));
        Assert.Equal(ArchiveFormat.Unknown, Archives.Detect(missing));
        Assert.Equal(ArchiveFormat.Unknown, Archives.Detect(temp.Path));
        var engine = Archives.For(invalid);
        Assert.Equal(new FormatCapabilities(false, false, false, false, false, false, false, false), engine.Capabilities);

        var list = engine.List(invalid);
        Assert.Equal(OperationStatus.Failed, list.Result.Status);
        Assert.Equal("지원하지 않거나 알 수 없는 압축파일 형식", list.Result.Message);
        Assert.Empty(list.Entries);
        Assert.Equal(OperationStatus.Failed, engine.Test(invalid).Status);
        Assert.Equal(OperationStatus.Failed, engine.Extract(invalid, temp.Combine("out"), new ExtractOptions()).Status);
        Assert.Equal(OperationStatus.Failed, engine.Create(temp.Combine("x.zip"), [invalid]).Status);
        Assert.Equal(OperationStatus.Failed, engine.Add(invalid, [invalid]).Status);
        Assert.Equal(OperationStatus.Failed, engine.Delete(invalid, ["a"]).Status);
        Assert.Equal(OperationStatus.Failed, engine.Rename(invalid, "a", "b").Status);
        Assert.False(Directory.Exists(temp.Combine("out")));

        Assert.Equal(OperationStatus.Failed, Archives.For(missing).List(missing).Result.Status);
        Assert.Equal(OperationStatus.Failed, new SevenZipEngine().List(invalid).Result.Status);
        Assert.Equal(OperationStatus.Failed, new SevenZipEngine().Extract(invalid, temp.Combine("out"), new ExtractOptions()).Status);
    }

    [Fact]
    public void OZ_E12_Empty_zip_is_detected_as_zip()
    {
        using var temp = new TempDir();
        string empty = temp.Combine("empty.zip");
        using (System.IO.Compression.ZipFile.Open(empty, System.IO.Compression.ZipArchiveMode.Create))
        {
        }
        Assert.Equal(ArchiveFormat.Zip, Archives.Detect(empty));
        var list = Archives.For(empty).List(empty);
        Assert.True(list.Result.Succeeded);
        Assert.Empty(list.Entries);
    }
}
