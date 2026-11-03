using System.IO.Compression;

namespace OhMyZip.Core.Tests;

public class ZipEngineTests
{
    [Fact]
    public void OZ_E01_List_sample_zip_shows_three_files_and_recorded_folder()
    {
        using var temp = new TempDir();
        string zip = Fixtures.CopyTo("sample.zip", temp);

        Assert.Equal(ArchiveFormat.Zip, Archives.Detect(zip));
        var list = Archives.For(zip).List(zip);

        Assert.True(list.Result.Succeeded, list.Result.Message);
        var files = list.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Path);
        var folders = list.Entries.Where(e => e.IsDirectory).Select(e => e.Path).OrderBy(p => p, StringComparer.Ordinal).ToList();
        Assert.Equal(["nested/notes.txt", "readme.txt", Fixtures.KoreanName], files.Keys.OrderBy(k => k, StringComparer.Ordinal));
        // 2026-10-27 DEF-02: 목록은 기록된 항목만, 폴더 2→1 (fixture 를 만든 외부 프로그램 실측 기준)
        Assert.Equal(["empty"], folders);
        Assert.Equal(52, files["readme.txt"].Size);
        Assert.Equal(19, files["nested/notes.txt"].Size);
        Assert.Equal(25, files[Fixtures.KoreanName].Size);
        Assert.All(files.Values, f => Assert.NotNull(f.Crc32));
        Assert.Equal("Deflate", files["readme.txt"].Method);
        Assert.All(list.Entries, e =>
        {
            Assert.DoesNotContain('\\', e.Path);
            Assert.False(e.Path.StartsWith('/') || e.Path.EndsWith('/'));
        });
    }

    [Fact]
    public void OZ_E02_Test_sample_ok_and_invalid_zip_is_failed_result()
    {
        using var temp = new TempDir();
        string zip = Fixtures.CopyTo("sample.zip", temp);
        string invalid = Fixtures.CopyTo("invalid.zip", temp);

        var ok = new ZipEngine().Test(zip);
        Assert.Equal(OperationStatus.Succeeded, ok.Status);

        Assert.Equal(ArchiveFormat.Unknown, Archives.Detect(invalid));
        var direct = new ZipEngine();
        Assert.Equal(OperationStatus.Failed, direct.Test(invalid).Status);
        Assert.Equal(OperationStatus.Failed, direct.List(invalid).Result.Status);
        Assert.Equal(OperationStatus.Failed, direct.Extract(invalid, temp.Combine("out"), new ExtractOptions()).Status);
        Assert.Equal(OperationStatus.Failed, Archives.For(invalid).Test(invalid).Status);
    }

    private static (string Path, byte[] Original) CorruptStoredZip(TempDir temp)
    {
        string zip = temp.Combine("corrupt.zip");
        byte[] data = Util.Text(string.Concat(Enumerable.Repeat("ohmyZip CRC 시험 데이터 ", 50)));
        Util.WriteZip(zip, CompressionLevel.NoCompression, [("data.txt", data, 0)]);
        byte[] bytes = File.ReadAllBytes(zip);
        int nameLength = BitConverter.ToUInt16(bytes, 26);
        int extraLength = BitConverter.ToUInt16(bytes, 28);
        int dataStart = 30 + nameLength + extraLength;
        bytes[dataStart + 100] ^= 0x5A;
        File.WriteAllBytes(zip, bytes);
        return (zip, data);
    }

    [Fact]
    public void OZ_E02_Test_detects_corrupted_data_byte_by_crc()
    {
        using var temp = new TempDir();
        var (zip, _) = CorruptStoredZip(temp);

        var result = new ZipEngine().Test(zip);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Contains(result.Problems, p => p.Contains("data.txt") && p.Contains("CRC"));
    }

    [Fact]
    public void OZ_E02_Test_detects_corrupted_deflate_stream()
    {
        using var temp = new TempDir();
        string zip = temp.Combine("deflate.zip");
        var rnd = new Random(7);
        byte[] data = new byte[200_000];
        for (int i = 0; i < data.Length; i++)
            data[i] = (byte)"abcdefgh"[rnd.Next(8)];
        Util.WriteZip(zip, CompressionLevel.Optimal, [("d.bin", data, 0)]);
        byte[] bytes = File.ReadAllBytes(zip);
        int dataStart = 30 + BitConverter.ToUInt16(bytes, 26) + BitConverter.ToUInt16(bytes, 28);
        bytes[dataStart + 2000] ^= 0xFF;
        File.WriteAllBytes(zip, bytes);

        Assert.Equal(OperationStatus.Failed, new ZipEngine().Test(zip).Status);
    }

    [Fact]
    public void OZ_E02_Extract_of_corrupted_entry_fails_and_leaves_no_file()
    {
        using var temp = new TempDir();
        var (zip, _) = CorruptStoredZip(temp);
        string output = temp.Combine("out");

        var result = new ZipEngine().Extract(zip, output, new ExtractOptions());

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Empty(Util.FilesUnder(output));
    }

    [Fact]
    public void OZ_E03_Extract_sample_zip_matches_original_hashes_and_creates_empty_folder()
    {
        using var temp = new TempDir();
        string zip = Fixtures.CopyTo("sample.zip", temp);
        string output = temp.Combine("out");

        var result = Archives.For(zip).Extract(zip, output, new ExtractOptions());

        Assert.True(result.Succeeded, result.Message + string.Join("; ", result.Problems));
        Util.AssertExpectedHashes(output);
        Assert.True(Directory.Exists(Path.Combine(output, "empty")));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(output, "empty")));
        Assert.Equal(3, Util.FilesUnder(output).Count);
    }

    [Fact]
    public void OZ_E03_Extract_with_Only_selects_folder_and_keeps_full_path()
    {
        using var temp = new TempDir();
        string zip = Fixtures.CopyTo("sample.zip", temp);
        string output = temp.Combine("out");

        var result = new ZipEngine().Extract(zip, output, new ExtractOptions { Only = ["nested"] });

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(["nested/notes.txt"], Util.FilesUnder(output));
        Assert.Equal(Fixtures.NotesHash, Util.Sha256(Path.Combine(output, "nested", "notes.txt")));
    }

    [Fact]
    public void OZ_E05_Create_from_input_then_extract_round_trips_hashes_korean_nested_and_empty()
    {
        using var temp = new TempDir();
        string input = Fixtures.CopyTo("input", temp);
        string zip = temp.Combine("made.zip");
        var sources = new[] { "readme.txt", Fixtures.KoreanName, "nested", "empty" }.Select(n => Path.Combine(input, n)).ToList();
        var engine = Archives.For(ArchiveFormat.Zip);

        var created = engine.Create(zip, sources);
        Assert.True(created.Succeeded, created.Message + string.Join("; ", created.Problems));
        Assert.Equal(ArchiveFormat.Zip, Archives.Detect(zip));
        Assert.Single(Directory.GetFiles(temp.Path));

        var list = engine.List(zip);
        Assert.Contains(list.Entries, e => e.Path == Fixtures.KoreanName && !e.IsDirectory);
        Assert.Contains(list.Entries, e => e.Path == "empty" && e.IsDirectory);
        Assert.Contains(list.Entries, e => e.Path == "nested/notes.txt");

        // 한글 이름은 UTF-8 플래그(일반 목적 비트 11)와 UTF-8 바이트로 저장한다.
        using (var reader = ZipArchiveReader.Open(zip, null))
        {
            var korean = Assert.Single(reader.Entries, r => r.NameBytes.SequenceEqual(System.Text.Encoding.UTF8.GetBytes(Fixtures.KoreanName)));
            Assert.NotEqual(0, korean.Flags & 0x0800);
        }

        Assert.Equal(OperationStatus.Succeeded, engine.Test(zip).Status);
        string output = temp.Combine("out");
        var extracted = engine.Extract(zip, output, new ExtractOptions());
        Assert.True(extracted.Succeeded, extracted.Message);
        Util.AssertExpectedHashes(output);
        Assert.True(Directory.Exists(Path.Combine(output, "empty")));
    }

    [Fact]
    public void OZ_E05_Create_with_folder_source_keeps_folder_name_and_refuses_existing_archive()
    {
        using var temp = new TempDir();
        string input = Fixtures.CopyTo("input", temp);
        string zip = temp.Combine("folder.zip");
        var engine = new ZipEngine();

        Assert.True(engine.Create(zip, [input]).Succeeded);
        var paths = engine.List(zip).Entries.Select(e => e.Path).ToHashSet();
        Assert.Contains("input/nested/notes.txt", paths);
        Assert.Contains("input/empty", paths);
        Assert.Contains($"input/{Fixtures.KoreanName}", paths);

        string before = Util.Sha256(zip);
        var again = engine.Create(zip, [input]);
        Assert.Equal(OperationStatus.Failed, again.Status);
        Assert.Equal(before, Util.Sha256(zip));

        var missing = engine.Create(temp.Combine("missing.zip"), [temp.Combine("nope.txt")]);
        Assert.Equal(OperationStatus.Failed, missing.Status);
        Assert.False(File.Exists(temp.Combine("missing.zip")));
    }

    [Fact]
    public void OZ_E05_Korean_name_in_sample_zip_decodes_from_utf8_flag()
    {
        using var temp = new TempDir();
        string zip = Fixtures.CopyTo("sample.zip", temp);
        using (var reader = ZipArchiveReader.Open(zip, null))
            Assert.Contains(reader.Entries, r => (r.Flags & 0x0800) != 0);
        Assert.Contains(new ZipEngine().List(zip).Entries, e => e.Path == Fixtures.KoreanName);
    }

    [Fact]
    public void OZ_E06_Add_rename_list_delete_on_copy_of_mutable_zip()
    {
        using var temp = new TempDir();
        string zip = Fixtures.CopyTo("mutable.zip", temp);
        string added = Fixtures.CopyTo("added.txt", temp);
        string addedHash = Util.Sha256(added);
        var engine = Archives.For(zip);
        Assert.True(engine.Capabilities.Modify);

        var add = engine.Add(zip, [added]);
        Assert.True(add.Succeeded, add.Message + string.Join("; ", add.Problems));
        Assert.Contains(engine.List(zip).Entries, e => e.Path == "added.txt");

        var rename = engine.Rename(zip, "added.txt", "이름바뀜.txt");
        Assert.True(rename.Succeeded, rename.Message + string.Join("; ", rename.Problems));
        var paths = engine.List(zip).Entries.Select(e => e.Path).ToHashSet();
        Assert.Contains("이름바뀜.txt", paths);
        Assert.DoesNotContain("added.txt", paths);

        string check = temp.Combine("check");
        Assert.True(engine.Extract(zip, check, new ExtractOptions { Only = ["이름바뀜.txt"] }).Succeeded);
        Assert.Equal(addedHash, Util.Sha256(Path.Combine(check, "이름바뀜.txt")));

        var delete = engine.Delete(zip, ["이름바뀜.txt"]);
        Assert.True(delete.Succeeded, delete.Message);
        paths = engine.List(zip).Entries.Select(e => e.Path).ToHashSet();
        Assert.DoesNotContain("이름바뀜.txt", paths);

        Assert.Equal(OperationStatus.Succeeded, engine.Test(zip).Status);
        string output = temp.Combine("out");
        Assert.True(engine.Extract(zip, output, new ExtractOptions()).Succeeded);
        Util.AssertExpectedHashes(output);
        Assert.True(Directory.Exists(Path.Combine(output, "empty")));
        Assert.Equal(3, Util.FilesUnder(output).Count);
        Assert.Single(Directory.GetFiles(temp.Path, "*.zip"));
        Assert.Empty(Directory.GetFiles(temp.Path, "~oz*"));
    }

    [Fact]
    public void OZ_E06_Rename_folder_renames_children_and_add_into_folder_and_delete_folder()
    {
        using var temp = new TempDir();
        string zip = Fixtures.CopyTo("mutable.zip", temp);
        string added = Fixtures.CopyTo("added.txt", temp);
        var engine = new ZipEngine();

        Assert.True(engine.Add(zip, [added], "nested").Succeeded);
        Assert.Contains(engine.List(zip).Entries, e => e.Path == "nested/added.txt");

        var rename = engine.Rename(zip, "nested", "moved");
        Assert.True(rename.Succeeded, rename.Message + string.Join("; ", rename.Problems));
        var paths = engine.List(zip).Entries.Select(e => e.Path).ToHashSet();
        Assert.Contains("moved/notes.txt", paths);
        Assert.Contains("moved/added.txt", paths);
        // 2026-10-27 DEF-02: 목록은 기록된 항목만 — mutable.zip 의 nested 는 경로로만 생기는 폴더라 이름을 바꾼 moved 도 목록에 없다.
        Assert.DoesNotContain("moved", paths);
        Assert.DoesNotContain(paths, p => p.StartsWith("nested", StringComparison.Ordinal));

        string output = temp.Combine("out");
        Assert.True(engine.Extract(zip, output, new ExtractOptions()).Succeeded);
        Assert.Equal(Fixtures.NotesHash, Util.Sha256(Path.Combine(output, "moved", "notes.txt")));

        Assert.True(engine.Delete(zip, ["moved"]).Succeeded);
        paths = engine.List(zip).Entries.Select(e => e.Path).ToHashSet();
        Assert.DoesNotContain(paths, p => p.StartsWith("moved", StringComparison.Ordinal));
        Assert.Contains("readme.txt", paths);
    }

    [Fact]
    public void OZ_E11_Failed_modifications_leave_archive_bytes_unchanged()
    {
        using var temp = new TempDir();
        string zip = Fixtures.CopyTo("mutable.zip", temp);
        string added = Fixtures.CopyTo("added.txt", temp);
        string before = Util.Sha256(zip);
        var engine = new ZipEngine();

        void AssertUnchanged(OperationResult result, OperationStatus expected)
        {
            Assert.Equal(expected, result.Status);
            Assert.Equal(before, Util.Sha256(zip));
            Assert.Empty(Directory.GetFiles(temp.Path, "~oz*"));
        }

        AssertUnchanged(engine.Add(zip, [temp.Combine("does-not-exist.txt")]), OperationStatus.Failed);
        AssertUnchanged(engine.Add(zip, [added, temp.Combine("does-not-exist.txt")]), OperationStatus.Failed);

        // 같은 이름이 이미 있으면 조용히 중복하지 않고 실패한다.
        File.WriteAllText(temp.Combine("readme.txt"), "다른 내용");
        var duplicate = engine.Add(zip, [temp.Combine("readme.txt")]);
        AssertUnchanged(duplicate, OperationStatus.Failed);
        Assert.Contains(duplicate.Problems, p => p.Contains("readme.txt"));

        AssertUnchanged(engine.Rename(zip, "readme.txt", Fixtures.KoreanName), OperationStatus.Failed);
        AssertUnchanged(engine.Rename(zip, Fixtures.KoreanName, "README.txt"), OperationStatus.Failed);
        AssertUnchanged(engine.Rename(zip, "readme.txt", "a/b.txt"), OperationStatus.Failed);
        AssertUnchanged(engine.Rename(zip, "readme.txt", "a\\b.txt"), OperationStatus.Failed);
        AssertUnchanged(engine.Rename(zip, "readme.txt", ".."), OperationStatus.Failed);
        AssertUnchanged(engine.Rename(zip, "nested", "empty"), OperationStatus.Failed);
        AssertUnchanged(engine.Rename(zip, "no-such.txt", "x.txt"), OperationStatus.Failed);
        AssertUnchanged(engine.Delete(zip, ["no-such.txt"]), OperationStatus.Failed);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        AssertUnchanged(engine.Add(zip, [added], "", cancel: cancelled.Token), OperationStatus.Cancelled);
        AssertUnchanged(engine.Delete(zip, ["readme.txt"], cancel: cancelled.Token), OperationStatus.Cancelled);

        string notZip = Fixtures.CopyTo("invalid.zip", temp);
        string notZipBefore = Util.Sha256(notZip);
        Assert.Equal(OperationStatus.Failed, engine.Add(notZip, [added]).Status);
        Assert.Equal(notZipBefore, Util.Sha256(notZip));
    }
}
