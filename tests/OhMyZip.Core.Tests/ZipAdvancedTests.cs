using System.Buffers.Binary;
using System.Text;

namespace OhMyZip.Core.Tests;

/// <summary>ZIP 고급 기능: 압축 수준, AES-256, ZipCrypto 읽기, 암호 압축파일 수정, 분할, 코드페이지, 설명, 취소, 변조 검출.</summary>
public class ZipAdvancedTests
{
    // 시험 전용 합성 암호. 실제 암호가 아니다.
    private const string Password = "oz-Synth-4417";
    private const string OtherPassword = "oz-Synth-9902";

    /// <summary>
    /// 외부 압축 프로그램의 CLI 로 만든 ZipCrypto 압축파일(input 의 readme.txt·한글이름.txt·nested·empty, 암호 = Password, 2026-10-27).
    /// 한글 이름은 UTF-8 플래그 없이 CP949 바이트 + Info-ZIP 유니코드 경로(0x7075)로 들어 있다.
    /// </summary>
    private const string ExternalZipCryptoBase64 =
        "UEsDBBQAAAAAALhaW10AAAAAAAAAAAAAAAAGAAAAZW1wdHkvUEsDBBQAAAAAALhaW10AAAAAAAAAAAAAAAAHAAAAbmVzdGVkL1BLAwQUAAEACAC4WltdO7Pk" +
        "LCEAAAATAAAAEAAAAG5lc3RlZC9ub3Rlcy50eHQv0SRrAshufa9iVS/aeyU980geMVqVeeyWN0XXVvH4+BVQSwMEFAABAAgAuFpbXVYlP+9BAAAANAAAAAoA" +
        "AAByZWFkbWUudHh03ItMMjZ1FV2iRvT35j1HmMvxSoqB4BrH0fbEH3poQqRT4SnXn1F0tyUwqPDXQ1YC7XcRzwHDieFRbIKfMAZX4KxQSwMEFAABAAgAuFpb" +
        "Xf0fixQqAAAAGQAAAAwAGQDH0bHbwMy4py50eHR1cBUAAWTBQIDtlZzquIDsnbTrpoQudHh0IaQjbFyhyPoQWCmVgXOgttQk1M/LUb5peF4Li5QcrkdfWn5/" +
        "EupDPHjfUEsBAhQAFAAAAAAAuFpbXQAAAAAAAAAAAAAAAAYAJAAAAAAAAAAQAAAAAAAAAGVtcHR5LwoAIAAAAAAAAQAYAFzCWeq5Zd0BXMJZ6rll3QFcwlnq" +
        "uWXdAVBLAQIUABQAAAAAALhaW10AAAAAAAAAAAAAAAAHACQAAAAAAAAAEAAAACQAAABuZXN0ZWQvCgAgAAAAAAABABgAf5hZ6rll3QF9HInquWXdAe7WV+q5" +
        "Zd0BUEsBAhQAFAABAAgAuFpbXTuz5CwhAAAAEwAAABAAJAAAAAAAAAAgAAAASQAAAG5lc3RlZC9ub3Rlcy50eHQKACAAAAAAAAEAGADkrVnquWXdAeStWeq5" +
        "Zd0Bf5hZ6rll3QFQSwECFAAUAAEACAC4WltdViU/70EAAAA0AAAACgAkAAAAAAAAACAAAACYAAAAcmVhZG1lLnR4dAoAIAAAAAAAAQAYAG8DXOq5Zd0BbwNc" +
        "6rll3QG15VvquWXdAVBLAQIUABQAAQAIALhaW139H4sUKgAAABkAAAAMAD0AAAAAAAAAIAAAAAEBAADH0bHbwMy4py50eHQKACAAAAAAAAEAGABUoV3quWXd" +
        "AVShXeq5Zd0BVKFd6rll3QF1cBUAAWTBQIDtlZzquIDsnbTrpoQudHh0UEsFBgAAAAAFAAUA5gEAAG4BAAAAAA==";

    private static readonly CreateOptions Aes = new() { Encryption = EncryptionMethod.Aes256, Password = Password };

    private static List<string> InputSources(string input) =>
        new[] { "readme.txt", Fixtures.KoreanName, "nested", "empty" }.Select(n => Path.Combine(input, n)).ToList();

    private static string CreateAesArchive(TempDir temp, string name = "aes.zip", int level = 5)
    {
        string input = Fixtures.CopyTo("input", temp);
        string zip = temp.Combine(name);
        var created = new ZipEngine().Create(zip, InputSources(input), Aes with { Level = level });
        Assert.True(created.Succeeded, created.Message + string.Join("; ", created.Problems));
        return zip;
    }

    private static List<ZipRecord> Records(string zip, int? codePage = null)
    {
        using var reader = ZipArchiveReader.Open(zip, codePage);
        return [.. reader.Entries];
    }

    private static void AssertNoTemp(string folder) => Assert.Empty(Directory.GetFiles(folder, "~oz*", SearchOption.AllDirectories));

    private static string Detail(OperationResult r) => r.Message + " | " + string.Join("; ", r.Problems);

    [Fact]
    public void OZ_E40_Compression_level_changes_method_and_size()
    {
        using var temp = new TempDir();
        string source = temp.Combine("text.txt");
        var rnd = new Random(40);
        var sb = new StringBuilder();
        string[] words = ["ohmyZip", "압축", "level", "deflate", "보관함", "zip", "store", "fast", "smallest", "데이터"];
        for (int i = 0; i < 60_000; i++)
            sb.Append(words[rnd.Next(words.Length)]).Append(rnd.Next(100)).Append(' ');
        File.WriteAllText(source, sb.ToString());
        long original = new FileInfo(source).Length;
        var engine = new ZipEngine();

        var sizes = new Dictionary<int, long>();
        foreach (int level in new[] { 0, 1, 5, 9 })
        {
            string zip = temp.Combine($"l{level}.zip");
            var created = engine.Create(zip, [source], new CreateOptions { Level = level });
            Assert.True(created.Succeeded, Detail(created));
            var record = Assert.Single(Records(zip));
            Assert.Equal(level == 0 ? 0 : 8, record.Method);
            Assert.Equal(original, record.Size);
            sizes[level] = record.CompressedSize;
            var entry = Assert.Single(engine.List(zip).Entries);
            Assert.Equal(level == 0 ? "Store" : "Deflate", entry.Method);
            Assert.Equal(OperationStatus.Succeeded, engine.Test(zip).Status);
        }
        Assert.Equal(original, sizes[0]);
        Assert.True(sizes[1] < sizes[0], $"수준 1({sizes[1]}) < 저장({sizes[0]})");
        Assert.True(sizes[5] < sizes[1], $"수준 5({sizes[5]}) < 수준 1({sizes[1]})");
        Assert.True(sizes[9] <= sizes[5], $"수준 9({sizes[9]}) <= 수준 5({sizes[5]})");

        Assert.Equal(OperationStatus.Failed, engine.Create(temp.Combine("bad.zip"), [source], new CreateOptions { Level = 10 }).Status);
        Assert.False(File.Exists(temp.Combine("bad.zip")));
    }

    [Fact]
    public void OZ_E41_Aes_create_lists_encrypted_and_extracts_hashes_korean_nested_empty()
    {
        using var temp = new TempDir();
        string zip = CreateAesArchive(temp);
        var engine = new ZipEngine(new ReadOptions { Password = Password });

        var list = new ZipEngine().List(zip);
        Assert.True(list.Result.Succeeded, Detail(list.Result));
        var files = list.Entries.Where(e => !e.IsDirectory).ToList();
        Assert.Equal(3, files.Count);
        Assert.All(files, f =>
        {
            Assert.True(f.Encrypted);
            Assert.Equal("AES-256", f.Method);
        });
        Assert.Contains(list.Entries, e => e.Path == Fixtures.KoreanName);
        Assert.Contains(list.Entries, e => e.Path == "empty" && e.IsDirectory && !e.Encrypted);

        // WinZip AE-2 구조: 플래그 비트 0, 방식 99, 0x9901(버전 2, "AE", 강도 3, 실제 방식 8), CRC 0, 이름은 UTF-8 플래그로 읽을 수 있다.
        foreach (var r in Records(zip).Where(r => !r.IsDirectory))
        {
            Assert.Equal(0x0801, r.Flags & 0x0801);
            Assert.Equal(99, r.Method);
            Assert.Equal(new ZipAesInfo(2, 3, 8), r.Aes);
            Assert.Equal(0u, r.Crc);
            Assert.Equal(51, r.VersionNeeded);
            Assert.True(r.CompressedSize >= r.Size + 16 + 2 + 10 - r.Size / 2, "소금 16 + 검증값 2 + 인증 코드 10 바이트가 붙는다");
        }
        byte[] bytes = File.ReadAllBytes(zip);
        Assert.True(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Fixtures.KoreanName)) >= 0, "이름은 암호화하지 않는다");
        Assert.True(bytes.AsSpan().IndexOf(File.ReadAllBytes(Path.Combine(temp.Path, "input", "readme.txt"))) < 0, "내용은 평문으로 남지 않는다");

        Assert.Equal(OperationStatus.Succeeded, engine.Test(zip).Status);
        string output = temp.Combine("out");
        var extracted = engine.Extract(zip, output, new ExtractOptions());
        Assert.True(extracted.Succeeded, Detail(extracted));
        Util.AssertExpectedHashes(output);
        Assert.Equal(3, Util.FilesUnder(output).Count);
        Assert.True(Directory.Exists(Path.Combine(output, "empty")));

        // 저장(수준 0) + AES 도 같은 길로 왕복한다.
        using var storedTemp = new TempDir();
        string stored = CreateAesArchive(storedTemp, "stored.zip", level: 0);
        Assert.All(Records(stored).Where(r => !r.IsDirectory), r => Assert.Equal(new ZipAesInfo(2, 3, 0), r.Aes));
        Assert.Equal(OperationStatus.Succeeded, engine.Test(stored).Status);

        // 지원하지 않는 조합은 조용히 무시하지 않는다.
        string input = Path.Combine(temp.Path, "input");
        var plain = new ZipEngine();
        Assert.Equal(OperationStatus.NotSupported, plain.Create(temp.Combine("n1.zip"), [input], Aes with { EncryptNames = true }).Status);
        Assert.Equal(OperationStatus.NotSupported, plain.Create(temp.Combine("n2.zip"), [input], new CreateOptions { Password = Password }).Status);
        Assert.Equal(OperationStatus.Failed, plain.Create(temp.Combine("n3.zip"), [input], new CreateOptions { Encryption = EncryptionMethod.Aes256 }).Status);
        Assert.False(File.Exists(temp.Combine("n1.zip")) || File.Exists(temp.Combine("n2.zip")) || File.Exists(temp.Combine("n3.zip")));
        Assert.True(plain.Capabilities.Encrypt);
    }

    [Fact]
    public void OZ_E41_Ae1_entry_with_crc_is_read_and_crc_is_verified()
    {
        using var temp = new TempDir();
        string zip = CreateAesArchive(temp, level: 0);
        var readme = Records(zip).Single(r => r.Name == "readme.txt");
        byte[] bytes = File.ReadAllBytes(zip);
        uint crc = ZipCentralDirectory.Crc(File.ReadAllBytes(Path.Combine(temp.Path, "input", "readme.txt")));
        // AE-2 → AE-1: 0x9901 의 공급자 버전을 1 로, CRC 칸에 실제 CRC 를 넣는다(지역 머리와 중앙 디렉터리 모두).
        PatchAes(bytes, readme, version: 1, crc);
        string ae1 = temp.Combine("ae1.zip");
        File.WriteAllBytes(ae1, bytes);
        var engine = new ZipEngine(new ReadOptions { Password = Password });

        var record = Records(ae1).Single(r => r.Name == "readme.txt");
        Assert.Equal(new ZipAesInfo(1, 3, 0), record.Aes);
        Assert.Equal(crc, record.ExpectedCrc);
        Assert.Equal(OperationStatus.Succeeded, engine.Test(ae1).Status);
        string output = temp.Combine("out");
        Assert.True(engine.Extract(ae1, output, new ExtractOptions()).Succeeded);
        Assert.Equal(Fixtures.ReadmeHash, Util.Sha256(Path.Combine(output, "readme.txt")));

        PatchAes(bytes, readme, version: 1, crc ^ 0x1);
        string badCrc = temp.Combine("ae1-badcrc.zip");
        File.WriteAllBytes(badCrc, bytes);
        var test = engine.Test(badCrc);
        Assert.Equal(OperationStatus.Failed, test.Status);
        Assert.Contains(test.Problems, p => p.Contains("readme.txt") && p.Contains("CRC"));
    }

    private static void PatchAes(byte[] bytes, ZipRecord record, ushort version, uint crc)
    {
        int local = (int)record.LocalOffset;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(local + 14), crc);
        int localExtra = local + 30 + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(local + 26));
        Assert.Equal(0x9901, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(localExtra)));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(localExtra + 4), version);
        int central = FindCentral(bytes, record.NameBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(central + 16), crc);
        int centralExtra = central + 46 + record.NameBytes.Length;
        Assert.Equal(0x9901, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(centralExtra)));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(centralExtra + 4), version);
    }

    private static int FindCentral(byte[] bytes, byte[] name)
    {
        for (int i = 0; i + 46 <= bytes.Length; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i)) == 0x02014b50
                && BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i + 28)) == name.Length
                && bytes.AsSpan(i + 46, name.Length).SequenceEqual(name))
                return i;
        }
        throw new InvalidOperationException("중앙 디렉터리 항목을 찾지 못했습니다");
    }

    [Fact]
    public void OZ_E42_Missing_or_wrong_password_is_password_required_and_leaves_nothing()
    {
        using var temp = new TempDir();
        string zip = CreateAesArchive(temp);
        string before = Util.Sha256(zip);

        var noPassword = new ZipEngine();
        var test = noPassword.Test(zip);
        Assert.Equal(OperationStatus.PasswordRequired, test.Status);
        Assert.Equal("암호가 필요합니다", test.Message);
        string out1 = temp.Combine("out1");
        var extract = noPassword.Extract(zip, out1, new ExtractOptions());
        Assert.Equal(OperationStatus.PasswordRequired, extract.Status);
        Assert.Equal("암호가 필요합니다", extract.Message);
        Assert.False(Directory.Exists(out1));

        var wrong = new ZipEngine(new ReadOptions { Password = OtherPassword });
        test = wrong.Test(zip);
        Assert.Equal(OperationStatus.PasswordRequired, test.Status);
        Assert.Equal("암호가 올바르지 않습니다", test.Message);
        string out2 = temp.Combine("out2");
        extract = wrong.Extract(zip, out2, new ExtractOptions());
        Assert.Equal(OperationStatus.PasswordRequired, extract.Status);
        Assert.Equal("암호가 올바르지 않습니다", extract.Message);
        Assert.False(Directory.Exists(out2));
        Assert.Equal(before, Util.Sha256(zip));

        // 항목마다 암호가 다르면: 첫 항목은 풀리지만 뒤 항목에서 틀림 → 세션 전체를 되돌려 아무 파일도 남기지 않는다.
        string mixed = temp.Combine("mixed.zip");
        using (var output = ZipVolumeWriter.Create(mixed, null))
        {
            var writer = new ZipWriter(output);
            using (var a = new MemoryStream(Util.Text("첫 항목")))
                writer.AddFile("a.txt", a, DateTime.Now, 5, Password, null, default);
            using (var b = new MemoryStream(Util.Text("다른 암호 항목")))
                writer.AddFile("sub/b.txt", b, DateTime.Now, 5, OtherPassword, null, default);
            writer.Finish([]);
            output.Commit(overwrite: false);
        }
        string out3 = temp.Combine("out3");
        Directory.CreateDirectory(out3);
        var mixedResult = new ZipEngine(new ReadOptions { Password = Password }).Extract(mixed, out3, new ExtractOptions());
        Assert.Equal(OperationStatus.PasswordRequired, mixedResult.Status);
        Assert.Equal("암호가 올바르지 않습니다", mixedResult.Message);
        Assert.Empty(Directory.GetFileSystemEntries(out3));
        Assert.DoesNotContain(mixedResult.Problems.Append(mixedResult.Message), p => p.Contains(Password) || p.Contains(OtherPassword));
    }

    [Fact]
    public void OZ_E43_ZipCrypto_archive_made_by_external_tool_lists_tests_and_extracts()
    {
        using var temp = new TempDir();
        string zip = temp.Combine("zipcrypto.zip");
        File.WriteAllBytes(zip, Convert.FromBase64String(ExternalZipCryptoBase64));
        Assert.Equal(ArchiveFormat.Zip, Archives.Detect(zip));

        var list = Archives.For(zip).List(zip);
        Assert.True(list.Result.Succeeded, Detail(list.Result));
        var files = list.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Path);
        Assert.Equal(["nested/notes.txt", "readme.txt", Fixtures.KoreanName], files.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.All(files.Values, f =>
        {
            Assert.True(f.Encrypted);
            Assert.Equal("ZipCrypto", f.Method);
        });

        Assert.Equal(OperationStatus.PasswordRequired, Archives.For(zip).Test(zip).Status);
        var wrong = Archives.For(zip, new ReadOptions { Password = OtherPassword }).Test(zip);
        Assert.Equal(OperationStatus.PasswordRequired, wrong.Status);
        Assert.Equal("암호가 올바르지 않습니다", wrong.Message);

        var engine = Archives.For(zip, new ReadOptions { Password = Password });
        Assert.Equal(OperationStatus.Succeeded, engine.Test(zip).Status);
        string output = temp.Combine("out");
        var extracted = engine.Extract(zip, output, new ExtractOptions());
        Assert.True(extracted.Succeeded, Detail(extracted));
        Util.AssertExpectedHashes(output);
        Assert.True(Directory.Exists(Path.Combine(output, "empty")));
        Assert.False(Directory.Exists(temp.Combine("out-wrong")));
        Assert.Equal(OperationStatus.PasswordRequired, Archives.For(zip, new ReadOptions { Password = OtherPassword }).Extract(zip, temp.Combine("out-wrong"), new ExtractOptions()).Status);
        Assert.False(Directory.Exists(temp.Combine("out-wrong")));
    }

    [Fact]
    public void OZ_E44_Encrypted_archive_delete_rename_raw_copy_and_add_uses_engine_password()
    {
        using var temp = new TempDir();
        string zip = CreateAesArchive(temp);
        string added = Fixtures.CopyTo("added.txt", temp);
        var noPassword = new ZipEngine();

        var delete = noPassword.Delete(zip, ["readme.txt"]);
        Assert.True(delete.Succeeded, Detail(delete));
        var rename = noPassword.Rename(zip, Fixtures.KoreanName, "새이름.txt");
        Assert.True(rename.Succeeded, Detail(rename));
        var paths = noPassword.List(zip).Entries.Select(e => e.Path).ToHashSet();
        Assert.DoesNotContain("readme.txt", paths);
        Assert.Contains("새이름.txt", paths);

        string before = Util.Sha256(zip);
        var addNoPassword = noPassword.Add(zip, [added]);
        Assert.Equal(OperationStatus.PasswordRequired, addNoPassword.Status);
        Assert.Equal("암호가 필요합니다", addNoPassword.Message);
        var addWrong = new ZipEngine(new ReadOptions { Password = OtherPassword }).Add(zip, [added]);
        Assert.Equal(OperationStatus.PasswordRequired, addWrong.Status);
        Assert.Equal("암호가 올바르지 않습니다", addWrong.Message);
        Assert.Equal(before, Util.Sha256(zip));
        AssertNoTemp(temp.Path);

        var engine = new ZipEngine(new ReadOptions { Password = Password });
        var add = engine.Add(zip, [added], "nested");
        Assert.True(add.Succeeded, Detail(add));
        var entry = Assert.Single(engine.List(zip).Entries, e => e.Path == "nested/added.txt");
        Assert.True(entry.Encrypted);
        Assert.Equal("AES-256", entry.Method);

        Assert.Equal(OperationStatus.Succeeded, engine.Test(zip).Status);
        string output = temp.Combine("out");
        var extracted = engine.Extract(zip, output, new ExtractOptions());
        Assert.True(extracted.Succeeded, Detail(extracted));
        Assert.Equal(["nested/added.txt", "nested/notes.txt", "새이름.txt"], Util.FilesUnder(output));
        Assert.Equal(Fixtures.KoreanHash, Util.Sha256(Path.Combine(output, "새이름.txt")));
        Assert.Equal(Fixtures.NotesHash, Util.Sha256(Path.Combine(output, "nested", "notes.txt")));
        Assert.Equal(Util.Sha256(added), Util.Sha256(Path.Combine(output, "nested", "added.txt")));
        AssertNoTemp(temp.Path);

        // 다른 프로그램이 만든 ZipCrypto 도 원시 복사로 이름을 바꾸고 지울 수 있다(복호화 없음).
        string zc = temp.Combine("zc.zip");
        File.WriteAllBytes(zc, Convert.FromBase64String(ExternalZipCryptoBase64));
        Assert.True(noPassword.Rename(zc, "nested", "moved").Succeeded);
        Assert.True(noPassword.Delete(zc, ["readme.txt"]).Succeeded);
        var zcEngine = new ZipEngine(new ReadOptions { Password = Password });
        Assert.Equal(OperationStatus.Succeeded, zcEngine.Test(zc).Status);
        string zcOut = temp.Combine("zc-out");
        Assert.True(zcEngine.Extract(zc, zcOut, new ExtractOptions()).Succeeded);
        Assert.Equal(["moved/notes.txt", Fixtures.KoreanName], Util.FilesUnder(zcOut));
        Assert.Equal(Fixtures.NotesHash, Util.Sha256(Path.Combine(zcOut, "moved", "notes.txt")));
        Assert.Equal(Fixtures.KoreanHash, Util.Sha256(Path.Combine(zcOut, Fixtures.KoreanName)));
    }

    private static void WriteRandom(string path, int length, int seed)
    {
        byte[] data = new byte[length];
        new Random(seed).NextBytes(data);
        File.WriteAllBytes(path, data);
    }

    [Fact]
    public void OZ_E45_Split_create_exact_sizes_round_trip_missing_and_corrupted_parts()
    {
        using var temp = new TempDir();
        string src = temp.Combine("src");
        Directory.CreateDirectory(src);
        WriteRandom(Path.Combine(src, "a.bin"), 150_000, 1);
        WriteRandom(Path.Combine(src, "b.bin"), 70_000, 2);
        File.WriteAllText(Path.Combine(src, "한글.txt"), "분할 시험");
        var hashes = Directory.GetFiles(src).ToDictionary(f => "src/" + Path.GetFileName(f), Util.Sha256);
        string outDir = temp.Combine("set");
        Directory.CreateDirectory(outDir);
        string zip = Path.Combine(outDir, "split.zip");
        const long volume = 64 * 1024;
        var engine = new ZipEngine();

        var created = engine.Create(zip, [src], new CreateOptions { VolumeSize = volume });
        Assert.True(created.Succeeded, Detail(created));
        string[] parts = ["split.z01", "split.z02", "split.z03", "split.zip"];
        Assert.Equal(parts, Directory.GetFiles(outDir).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal));
        foreach (string p in parts[..^1])
            Assert.Equal(volume, new FileInfo(Path.Combine(outDir, p)).Length);
        long last = new FileInfo(zip).Length;
        Assert.InRange(last, 1, volume);
        long total = parts.Sum(p => new FileInfo(Path.Combine(outDir, p)).Length);
        Assert.InRange(total, 4 + 150_000 + 70_000, 4 + 150_000 + 70_000 + 2_000);
        byte[] head = File.ReadAllBytes(Path.Combine(outDir, "split.z01"))[..8];
        Assert.Equal([0x50, 0x4B, 0x07, 0x08, 0x50, 0x4B, 0x03, 0x04], head);

        // 디스크 번호와 디스크 안 위치(APPNOTE 8.5)
        var records = Records(zip);
        Assert.Equal(0, records[0].Disk);
        Assert.Equal(4, records[0].LocalOffset);
        Assert.Contains(records, r => r.Disk > 0);
        Assert.All(records, r => Assert.InRange(r.LocalOffset, 0, volume - 1));

        Assert.Equal(ArchiveFormat.Zip, Archives.Detect(zip));
        Assert.Equal(ArchiveFormat.Zip, Archives.Detect(Path.Combine(outDir, "split.z01")));
        foreach (string open in new[] { zip, Path.Combine(outDir, "split.z01") })
        {
            var e = Archives.For(open);
            Assert.True(e.Capabilities.Split);
            var list = e.List(open);
            Assert.True(list.Result.Succeeded, Detail(list.Result));
            Assert.Equal(hashes.Keys.Order(StringComparer.Ordinal), list.Entries.Where(x => !x.IsDirectory).Select(x => x.Path).Order(StringComparer.Ordinal));
            Assert.Equal(OperationStatus.Succeeded, e.Test(open).Status);
            string output = temp.Combine("out-" + Path.GetExtension(open).TrimStart('.'));
            var extracted = e.Extract(open, output, new ExtractOptions());
            Assert.True(extracted.Succeeded, Detail(extracted));
            foreach (var (path, hash) in hashes)
                Assert.Equal(hash, Util.Sha256(Path.Combine(output, path)));
        }

        // 분할 압축파일 고치기는 NotSupported, 바이트는 그대로.
        var before = parts.Select(p => Util.Sha256(Path.Combine(outDir, p))).ToList();
        Assert.Equal(OperationStatus.NotSupported, engine.Delete(zip, ["src/b.bin"]).Status);
        Assert.Equal(OperationStatus.NotSupported, engine.Rename(zip, "src/b.bin", "c.bin").Status);
        Assert.Equal(OperationStatus.NotSupported, engine.Add(zip, [Path.Combine(src, "a.bin")], "x").Status);
        Assert.Equal(OperationStatus.NotSupported, engine.SetComment(zip, "설명").Status);
        Assert.Equal(before, parts.Select(p => Util.Sha256(Path.Combine(outDir, p))).ToList());
        AssertNoTemp(outDir);

        // 이미 있는 이름이면 실패하고 아무 것도 바꾸지 않는다.
        Assert.Equal(OperationStatus.Failed, engine.Create(zip, [src], new CreateOptions { VolumeSize = volume }).Status);
        Assert.Equal(OperationStatus.Failed, engine.Create(temp.Combine("tiny.zip"), [src], new CreateOptions { VolumeSize = volume - 1 }).Status);
        Assert.False(File.Exists(temp.Combine("tiny.zip")));

        // 조각이 빠지면 빠진 파일 이름을 대며 실패.
        string missingDir = temp.Combine("missing");
        Directory.CreateDirectory(missingDir);
        foreach (string p in parts.Where(p => p != "split.z02"))
            File.Copy(Path.Combine(outDir, p), Path.Combine(missingDir, p));
        string missingZip = Path.Combine(missingDir, "split.zip");
        var missingList = engine.List(missingZip);
        Assert.Equal(OperationStatus.Failed, missingList.Result.Status);
        Assert.Contains("split.z02", missingList.Result.Message);
        Assert.Equal(OperationStatus.Failed, engine.Test(missingZip).Status);
        Assert.Equal(OperationStatus.Failed, engine.Extract(missingZip, temp.Combine("out-missing"), new ExtractOptions()).Status);
        Assert.False(Directory.Exists(temp.Combine("out-missing")));
        File.Delete(missingZip);
        var noLast = engine.List(Path.Combine(missingDir, "split.z01"));
        Assert.Equal(OperationStatus.Failed, noLast.Result.Status);
        Assert.Contains("split.zip", noLast.Result.Message);

        // 가운데 조각 데이터 1바이트 손상 → 검사 실패.
        string corruptDir = temp.Combine("corrupt");
        Directory.CreateDirectory(corruptDir);
        foreach (string p in parts)
            File.Copy(Path.Combine(outDir, p), Path.Combine(corruptDir, p));
        string z02 = Path.Combine(corruptDir, "split.z02");
        byte[] bytes = File.ReadAllBytes(z02);
        bytes[100] ^= 0x5A;
        File.WriteAllBytes(z02, bytes);
        var corrupt = engine.Test(Path.Combine(corruptDir, "split.zip"));
        Assert.Equal(OperationStatus.Failed, corrupt.Status);
        Assert.NotEmpty(corrupt.Problems);
    }

    [Fact]
    public void OZ_E45_Split_that_fits_one_volume_is_plain_zip_and_split_aes_round_trips()
    {
        using var temp = new TempDir();
        string input = Fixtures.CopyTo("input", temp);
        string small = temp.Combine("small.zip");
        var engine = new ZipEngine(new ReadOptions { Password = Password });

        Assert.True(engine.Create(small, InputSources(input), new CreateOptions { VolumeSize = 64 * 1024 }).Succeeded);
        Assert.False(File.Exists(temp.Combine("small.z01")));
        Assert.Equal([0x50, 0x4B, 0x03, 0x04], File.ReadAllBytes(small)[..4]);
        Assert.Equal(OperationStatus.Succeeded, engine.Test(small).Status);
        Assert.All(Records(small), r => Assert.Equal(0, r.Disk));

        string src = temp.Combine("big.bin");
        WriteRandom(src, 200_000, 3);
        string splitAes = temp.Combine("split-aes.zip");
        var created = engine.Create(splitAes, [src], Aes with { VolumeSize = 64 * 1024 });
        Assert.True(created.Succeeded, Detail(created));
        Assert.True(File.Exists(temp.Combine("split-aes.z03")));
        Assert.Equal(OperationStatus.Succeeded, engine.Test(splitAes).Status);
        string output = temp.Combine("out");
        Assert.True(engine.Extract(splitAes, output, new ExtractOptions()).Succeeded);
        Assert.Equal(Util.Sha256(src), Util.Sha256(Path.Combine(output, "big.bin")));
    }

    /// <summary>UTF-8 플래그 없이 이름 바이트를 그대로 넣은 저장 방식 ZIP(코드페이지 시험용). comment 는 EOCD 설명 바이트.</summary>
    private static void WriteRawZip(string path, byte[] comment, params (byte[] Name, byte[] Data)[] entries)
    {
        using var ms = new MemoryStream();
        var central = new MemoryStream();
        var w = new BinaryWriter(ms);
        var c = new BinaryWriter(central);
        foreach (var (name, data) in entries)
        {
            uint offset = (uint)ms.Position;
            uint crc = ZipCentralDirectory.Crc(data);
            w.Write(0x04034b50u); w.Write((ushort)20); w.Write((ushort)0); w.Write((ushort)0); w.Write(0x5B5A0000u);
            w.Write(crc); w.Write((uint)data.Length); w.Write((uint)data.Length); w.Write((ushort)name.Length); w.Write((ushort)0);
            w.Write(name); w.Write(data);
            c.Write(0x02014b50u); c.Write((ushort)20); c.Write((ushort)20); c.Write((ushort)0); c.Write((ushort)0); c.Write(0x5B5A0000u);
            c.Write(crc); c.Write((uint)data.Length); c.Write((uint)data.Length); c.Write((ushort)name.Length); c.Write((ushort)0); c.Write((ushort)0);
            c.Write((ushort)0); c.Write((ushort)0); c.Write(0u); c.Write(offset); c.Write(name);
        }
        uint cdOffset = (uint)ms.Position;
        w.Write(central.ToArray());
        w.Write(0x06054b50u); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)entries.Length); w.Write((ushort)entries.Length);
        w.Write((uint)central.Length); w.Write(cdOffset); w.Write((ushort)comment.Length); w.Write(comment);
        File.WriteAllBytes(path, ms.ToArray());
    }

    [Fact]
    public void OZ_E46_Code_page_override_for_names_without_utf8_flag()
    {
        using var temp = new TempDir();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        const string korean = "한글이름.txt", japanese = "日本語のファイル.txt", chinese = "中文文件.txt";
        string kr = temp.Combine("cp949.zip"), jp = temp.Combine("sjis.zip"), cn = temp.Combine("gbk.zip"), utf = temp.Combine("utf8-noflag.zip");
        WriteRawZip(kr, [], (Encoding.GetEncoding(949).GetBytes(korean), Util.Text("kr")), (Encoding.ASCII.GetBytes("plain.txt"), Util.Text("p")));
        WriteRawZip(jp, [], (Encoding.GetEncoding(932).GetBytes(japanese), Util.Text("jp")));
        WriteRawZip(cn, [], (Encoding.GetEncoding(936).GetBytes(chinese), Util.Text("cn")));
        WriteRawZip(utf, [], (Encoding.UTF8.GetBytes(japanese), Util.Text("u")));

        static List<string> Names(string zip, int? cp) =>
            new ZipEngine(new ReadOptions { CodePage = cp }).List(zip).Entries.Select(e => e.Path).ToList();

        // 자동: 올바른 UTF-8 이면 UTF-8, 아니면 CP949.
        Assert.Contains(korean, Names(kr, null));
        Assert.Contains("plain.txt", Names(kr, null));
        Assert.Equal([japanese], Names(utf, null));
        Assert.DoesNotContain(japanese, Names(jp, null));
        Assert.Equal([Encoding.GetEncoding(949).GetString(Encoding.GetEncoding(932).GetBytes(japanese))], Names(jp, null));

        // 지정: 949, 932, 936, 65001.
        Assert.Contains(korean, Names(kr, 949));
        Assert.Equal([japanese], Names(jp, 932));
        Assert.Equal([chinese], Names(cn, 936));
        Assert.Equal([japanese], Names(utf, 65001));
        Assert.DoesNotContain(korean, Names(kr, 932));

        var engine = new ZipEngine(new ReadOptions { CodePage = 932 });
        string output = temp.Combine("out");
        var extracted = engine.Extract(jp, output, new ExtractOptions());
        Assert.True(extracted.Succeeded, Detail(extracted));
        Assert.Equal([japanese], Util.FilesUnder(output));
        Assert.Equal(OperationStatus.Succeeded, engine.Test(jp).Status);

        // 이름을 바꾸면 UTF-8 + 플래그 비트 11 로 쓰고, 바꾸지 않은 항목은 원래 바이트를 그대로 둔다.
        Assert.True(new ZipEngine(new ReadOptions { CodePage = 949 }).Rename(kr, "plain.txt", "새.txt").Succeeded);
        var records = Records(kr);
        var renamed = Assert.Single(records, r => r.Name == "새.txt");
        Assert.NotEqual(0, renamed.Flags & 0x0800);
        var kept = Assert.Single(records, r => r.Name == korean);
        Assert.Equal(0, kept.Flags & 0x0800);
        Assert.Equal(Encoding.GetEncoding(949).GetBytes(korean), kept.NameBytes);

        // 지원하지 않는 코드페이지 번호는 예외 없이 실패 결과.
        Assert.Equal(OperationStatus.Failed, new ZipEngine(new ReadOptions { CodePage = 123456 }).List(jp).Result.Status);
    }

    [Fact]
    public void OZ_E47_Comment_set_get_clear_is_atomic_and_decodes_legacy_bytes()
    {
        using var temp = new TempDir();
        string zip = Fixtures.CopyTo("mutable.zip", temp);
        var engine = new ZipEngine();
        Assert.True(engine.Capabilities.Comment);
        Assert.Null(engine.List(zip).Comment);

        const string comment = "ohmyZip 설명\r\n두 번째 줄";
        var set = engine.SetComment(zip, comment);
        Assert.True(set.Succeeded, Detail(set));
        Assert.Equal(comment, engine.List(zip).Comment);
        byte[] bytes = File.ReadAllBytes(zip);
        byte[] utf8 = Encoding.UTF8.GetBytes(comment);
        Assert.True(bytes.AsSpan(bytes.Length - utf8.Length).SequenceEqual(utf8), "설명은 EOCD 끝에 UTF-8 로 쓴다");
        Assert.Equal(OperationStatus.Succeeded, engine.Test(zip).Status);

        // 다른 수정에도 설명은 남는다.
        Assert.True(engine.Delete(zip, ["readme.txt"]).Succeeded);
        Assert.Equal(comment, engine.List(zip).Comment);

        // 너무 길면 실패, 취소하면 그대로.
        string before = Util.Sha256(zip);
        var tooLong = engine.SetComment(zip, new string('가', 21_846));
        Assert.Equal(OperationStatus.Failed, tooLong.Status);
        Assert.Equal(before, Util.Sha256(zip));
        Assert.True(engine.SetComment(zip, new string('a', 65_535)).Succeeded);
        Assert.Equal(65_535, engine.List(zip).Comment!.Length);
        before = Util.Sha256(zip);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Equal(OperationStatus.Cancelled, engine.SetComment(zip, "취소될 설명", cancelled.Token).Status);
        Assert.Equal(before, Util.Sha256(zip));
        AssertNoTemp(temp.Path);

        Assert.True(engine.SetComment(zip, null).Succeeded);
        Assert.Null(engine.List(zip).Comment);
        Assert.Equal(OperationStatus.Succeeded, engine.Test(zip).Status);
        string output = temp.Combine("out");
        Assert.True(engine.Extract(zip, output, new ExtractOptions()).Succeeded);
        Assert.Equal(Fixtures.KoreanHash, Util.Sha256(Path.Combine(output, Fixtures.KoreanName)));

        // UTF-8 이 아닌 설명: 자동은 CP949, 코드페이지 지정 시 그 코드페이지.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string legacy = temp.Combine("legacy.zip");
        WriteRawZip(legacy, Encoding.GetEncoding(949).GetBytes("한국어 설명"), (Util.Text("a.txt"), Util.Text("a")));
        Assert.Equal("한국어 설명", engine.List(legacy).Comment);
        string sjis = temp.Combine("sjis-comment.zip");
        WriteRawZip(sjis, Encoding.GetEncoding(932).GetBytes("日本語の説明"), (Util.Text("a.txt"), Util.Text("a")));
        Assert.Equal("日本語の説明", new ZipEngine(new ReadOptions { CodePage = 932 }).List(sjis).Comment);
    }

    [Fact]
    public void OZ_E48_Cancel_during_aes_create_split_create_and_modify_leaves_original_and_no_temp()
    {
        using var temp = new TempDir();
        string src = temp.Combine("src");
        Directory.CreateDirectory(src);
        WriteRandom(Path.Combine(src, "big.bin"), 4 * 1024 * 1024, 48);

        foreach (var options in new[] { Aes, Aes with { VolumeSize = 256 * 1024 } })
        {
            string zip = temp.Combine("new.zip");
            using var cts = new CancellationTokenSource();
            var progress = new SyncProgress(p =>
            {
                if (p.ProcessedBytes > 1024 * 1024)
                    cts.Cancel();
            });
            var result = new ZipEngine().Create(zip, [src], options, progress, cts.Token);
            Assert.Equal(OperationStatus.Cancelled, result.Status);
            Assert.Equal(["src/big.bin"], Util.FilesUnder(temp.Path));
        }

        string archive = temp.Combine("archive.zip");
        Assert.True(new ZipEngine().Create(archive, [Path.Combine(src, "big.bin")], Aes).Succeeded);
        string before = Util.Sha256(archive);
        string extra = temp.Combine("extra.bin");
        WriteRandom(extra, 3 * 1024 * 1024, 49);
        var engine = new ZipEngine(new ReadOptions { Password = Password });

        using (var cts = new CancellationTokenSource())
        {
            var progress = new SyncProgress(p =>
            {
                if (p.ProcessedBytes > 1024 * 1024)
                    cts.Cancel();
            });
            Assert.Equal(OperationStatus.Cancelled, engine.Add(archive, [extra], "", progress, cts.Token).Status);
        }
        Assert.Equal(before, Util.Sha256(archive));
        AssertNoTemp(temp.Path);

        using (var cts = new CancellationTokenSource())
        {
            File.WriteAllText(temp.Combine("small.txt"), "작은 파일");
            Assert.True(engine.Add(archive, [temp.Combine("small.txt")]).Succeeded);
            before = Util.Sha256(archive);
            var progress = new SyncProgress(p =>
            {
                if (p.ProcessedBytes > 1024 * 1024)
                    cts.Cancel();
            });
            Assert.Equal(OperationStatus.Cancelled, engine.Delete(archive, ["small.txt"], progress, cts.Token).Status);
        }
        Assert.Equal(before, Util.Sha256(archive));
        AssertNoTemp(temp.Path);
        Assert.Equal(OperationStatus.Succeeded, engine.Test(archive).Status);
    }

    [Fact]
    public void OZ_E50_Zip64_end_record_for_65536_entries_and_zip64_extra_fields_are_read()
    {
        using var temp = new TempDir();
        string many = temp.Combine("many.zip");
        using (var zipOut = ZipVolumeWriter.Create(many, null))
        {
            var writer = new ZipWriter(zipOut);
            for (int i = 0; i < 0x10000; i++)
            {
                using var data = new MemoryStream(Util.Text(i.ToString()));
                writer.AddFile($"d{i / 1000}/f{i}.txt", data, DateTime.Now, 0, null, null, default);
            }
            writer.Finish([]);
            zipOut.Commit(overwrite: false);
        }
        byte[] bytes = File.ReadAllBytes(many);
        int eocd = bytes.Length - 22;
        Assert.Equal(0x06054b50u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(eocd)));
        Assert.Equal(0xFFFF, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(eocd + 10)));
        Assert.Equal(0x07064b50u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(eocd - 20)));
        var engine = new ZipEngine();
        var list = engine.List(many);
        Assert.True(list.Result.Succeeded, Detail(list.Result));
        Assert.Equal(0x10000, list.Entries.Count);
        Assert.Equal(OperationStatus.Succeeded, engine.Test(many).Status);

        // 중앙 디렉터리의 크기·위치가 0xFFFFFFFF 이고 실제 값은 ZIP64 확장 필드에 있는 항목.
        string z64 = temp.Combine("zip64-extra.zip");
        byte[] content = Util.Text("zip64 확장 필드 시험");
        byte[] name = Util.Text("z.txt");
        uint crc = ZipCentralDirectory.Crc(content);
        using (var ms = new MemoryStream())
        {
            var w = new BinaryWriter(ms);
            w.Write(0x04034b50u); w.Write((ushort)45); w.Write((ushort)0x0800); w.Write((ushort)0); w.Write(0x5B5A0000u);
            w.Write(crc); w.Write(0xFFFFFFFFu); w.Write(0xFFFFFFFFu); w.Write((ushort)name.Length); w.Write((ushort)20);
            w.Write(name); w.Write((ushort)1); w.Write((ushort)16); w.Write((ulong)content.Length); w.Write((ulong)content.Length);
            w.Write(content);
            long cd = ms.Position;
            w.Write(0x02014b50u); w.Write((ushort)45); w.Write((ushort)45); w.Write((ushort)0x0800); w.Write((ushort)0); w.Write(0x5B5A0000u);
            w.Write(crc); w.Write(0xFFFFFFFFu); w.Write(0xFFFFFFFFu); w.Write((ushort)name.Length); w.Write((ushort)28); w.Write((ushort)0);
            w.Write((ushort)0); w.Write((ushort)0); w.Write(0u); w.Write(0xFFFFFFFFu); w.Write(name);
            w.Write((ushort)1); w.Write((ushort)24); w.Write((ulong)content.Length); w.Write((ulong)content.Length); w.Write(0UL);
            long cdSize = ms.Position - cd;
            w.Write(0x06054b50u); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)1);
            w.Write((uint)cdSize); w.Write((uint)cd); w.Write((ushort)0);
            File.WriteAllBytes(z64, ms.ToArray());
        }
        var entry = Assert.Single(engine.List(z64).Entries);
        Assert.Equal(content.Length, entry.Size);
        Assert.Equal(OperationStatus.Succeeded, engine.Test(z64).Status);
        string output = temp.Combine("out");
        Assert.True(engine.Extract(z64, output, new ExtractOptions()).Succeeded);
        Assert.Equal(content, File.ReadAllBytes(Path.Combine(output, "z.txt")));

        // 원시 복사(이름 바꾸기)도 ZIP64 지역 확장 필드를 그대로 옮긴다.
        Assert.True(engine.Rename(z64, "z.txt", "y.txt").Succeeded);
        Assert.Equal(OperationStatus.Succeeded, engine.Test(z64).Status);
        Assert.Equal("y.txt", Assert.Single(engine.List(z64).Entries).Path);
    }

    [Fact]
    public void OZ_E49_Hmac_detects_one_flipped_ciphertext_byte()
    {
        using var temp = new TempDir();
        string src = temp.Combine("secret.txt");
        File.WriteAllText(src, string.Concat(Enumerable.Repeat("변조 검출 시험 데이터 ", 200)));
        string zip = temp.Combine("tamper.zip");
        Assert.True(new ZipEngine().Create(zip, [src], Aes with { Level = 0 }).Succeeded);
        var record = Assert.Single(Records(zip));
        byte[] bytes = File.ReadAllBytes(zip);
        int dataStart = (int)record.LocalOffset + 30 + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((int)record.LocalOffset + 26))
            + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((int)record.LocalOffset + 28));
        bytes[dataStart + 16 + 2 + 500] ^= 0x01;
        File.WriteAllBytes(zip, bytes);
        var engine = new ZipEngine(new ReadOptions { Password = Password });

        var test = engine.Test(zip);
        Assert.Equal(OperationStatus.Failed, test.Status);
        Assert.Contains(test.Problems, p => p.Contains("secret.txt") && p.Contains("HMAC"));

        string output = temp.Combine("out");
        var extract = engine.Extract(zip, output, new ExtractOptions());
        Assert.Equal(OperationStatus.Failed, extract.Status);
        Assert.Contains(extract.Problems, p => p.Contains("HMAC"));
        Assert.Empty(Util.FilesUnder(output));
        AssertNoTemp(temp.Path);

        // 인증 코드 자체를 바꿔도 검출한다.
        bytes = File.ReadAllBytes(zip);
        bytes[dataStart + 16 + 2 + 500] ^= 0x01;
        bytes[dataStart + (int)record.CompressedSize - 1] ^= 0x80;
        File.WriteAllBytes(zip, bytes);
        Assert.Equal(OperationStatus.Failed, engine.Test(zip).Status);
    }
}
