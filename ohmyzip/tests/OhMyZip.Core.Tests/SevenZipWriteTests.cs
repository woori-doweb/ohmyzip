using System.Text;

namespace OhMyZip.Core.Tests;

/// <summary>7Z 쓰기(독자 구현)·AES·분할·고치기 시험. 암호는 시험용 합성 문자열만 쓴다.</summary>
public class SevenZipWriteTests
{
    internal const string Pw = "oz7-synthetic-pass";
    private const string WrongPw = "oz7-synthetic-wrong";

    /// <summary>fixtures/input 의 항목(readme.txt, 한글이름.txt, nested/, empty/)을 루트에 두는 원본 목록.</summary>
    internal static string[] InputSources(TempDir temp)
    {
        string input = Fixtures.CopyTo("input", temp);
        Directory.CreateDirectory(Path.Combine(input, "empty")); // 빈 폴더가 복사에서 빠지지 않게
        return Directory.GetFileSystemEntries(input).OrderBy(p => p, StringComparer.Ordinal).ToArray();
    }

    private static CreateOptions SevenZip(int level = 5, string? password = null, bool encryptNames = false, long? volume = null) => new()
    {
        Format = ArchiveFormat.SevenZip,
        Level = level,
        Password = password,
        Encryption = password is null ? EncryptionMethod.None : EncryptionMethod.Aes256,
        EncryptNames = encryptNames,
        VolumeSize = volume,
    };

    private static void AssertNoTemp(string dir) =>
        Assert.Empty(Directory.GetFiles(dir, "~oz*", SearchOption.AllDirectories));

    private static void AssertRoundTrip(IArchiveEngine engine, string archive, string output)
    {
        var test = engine.Test(archive);
        Assert.True(test.Succeeded, test.Message);
        var result = engine.Extract(archive, output, new ExtractOptions());
        Assert.True(result.Succeeded, result.Message + string.Join("; ", result.Problems));
        Util.AssertExpectedHashes(output);
        Assert.True(Directory.Exists(Path.Combine(output, "empty")), "빈 폴더 empty/ 가 풀리지 않았습니다");
    }

    [Fact]
    public void OZ_E20_SevenZip_create_list_extract_round_trips_hashes_korean_nested_and_empty_dir()
    {
        using var temp = new TempDir();
        string[] sources = InputSources(temp);
        string archive = temp.Combine("new.7z");
        var engine = Archives.For(ArchiveFormat.SevenZip);
        Assert.True(engine.Capabilities.Create);

        var created = engine.Create(archive, sources, SevenZip());
        Assert.True(created.Succeeded, created.Message);
        Assert.Equal(ArchiveFormat.SevenZip, Archives.Detect(archive));

        var list = Archives.For(archive).List(archive);
        Assert.True(list.Result.Succeeded, list.Result.Message);
        var byPath = list.Entries.ToDictionary(e => e.Path);
        Assert.Equal(52, byPath["readme.txt"].Size);
        Assert.False(byPath[Fixtures.KoreanName].IsDirectory);
        Assert.True(byPath["nested"].IsDirectory);
        Assert.True(byPath["empty"].IsDirectory);
        Assert.False(byPath["nested/notes.txt"].Encrypted);
        Assert.NotNull(byPath["nested/notes.txt"].Crc32);
        Assert.NotNull(byPath["readme.txt"].Modified);
        Assert.Equal(File.GetLastWriteTimeUtc(sources.Single(s => s.EndsWith("readme.txt"))),
            byPath["readme.txt"].Modified!.Value.UtcDateTime, TimeSpan.FromSeconds(1));

        AssertRoundTrip(Archives.For(archive), archive, temp.Combine("out"));
        Assert.Equal(3, Util.FilesUnder(temp.Combine("out")).Count);
        AssertNoTemp(temp.Path);

        // 같은 이름이 이미 있으면 덮어쓰지 않는다
        Assert.Equal(OperationStatus.Failed, engine.Create(archive, sources, SevenZip()).Status);
        // 7Z 엔진에 다른 형식을 요청하면 NotSupported
        Assert.Equal(OperationStatus.NotSupported, engine.Create(temp.Combine("x.7z"), sources, new CreateOptions { Format = ArchiveFormat.Zip }).Status);
        Assert.Equal(OperationStatus.Failed, engine.Create(temp.Combine("y.7z"), sources, SevenZip(level: 10)).Status);
        Assert.False(File.Exists(temp.Combine("x.7z")));
    }

    [Fact]
    public void OZ_E21_SevenZip_level0_is_copy_and_level9_is_smaller_both_round_trip()
    {
        using var temp = new TempDir();
        string[] sources = InputSources(temp);
        string big = temp.Combine("big.txt");
        var sb = new StringBuilder();
        for (int i = 0; i < 4000; i++)
            sb.Append("ohmyZip 압축 수준 시험 줄 ").Append(i % 17).Append('\n');
        File.WriteAllText(big, sb.ToString());
        long inputSize = new FileInfo(big).Length + sources.Where(File.Exists).Sum(f => new FileInfo(f).Length) + 25;
        var engine = new SevenZipEngine();

        string store = temp.Combine("l0.7z");
        string best = temp.Combine("l9.7z");
        Assert.True(engine.Create(store, [.. sources, big], SevenZip(level: 0)).Succeeded);
        Assert.True(engine.Create(best, [.. sources, big], SevenZip(level: 9)).Succeeded);

        long storeSize = new FileInfo(store).Length;
        long bestSize = new FileInfo(best).Length;
        Assert.True(storeSize >= inputSize, $"level 0 크기 {storeSize} < 입력 {inputSize}");
        Assert.True(bestSize < storeSize / 4, $"level 9 크기 {bestSize} 가 level 0 {storeSize} 보다 충분히 작지 않습니다");
        Assert.Equal("None", engine.List(store).Entries.Single(e => e.Path == "big.txt").Method);
        Assert.Equal("LZMA", engine.List(best).Entries.Single(e => e.Path == "big.txt").Method);

        foreach (string archive in new[] { store, best })
        {
            string output = temp.Combine("out-" + Path.GetFileNameWithoutExtension(archive));
            AssertRoundTrip(engine, archive, output);
            Assert.Equal(Util.Sha256(big), Util.Sha256(Path.Combine(output, "big.txt")));
        }
    }

    [Fact]
    public void OZ_E22_SevenZip_aes256_data_only_password_round_trip_and_wrong_or_missing_password()
    {
        using var temp = new TempDir();
        string[] sources = InputSources(temp);
        string archive = temp.Combine("aes.7z");
        Assert.True(new SevenZipEngine().Create(archive, sources, SevenZip(level: 0, password: Pw)).Succeeded);
        // Copy 코더여도 평문(readme.txt 전체 내용)이 그대로 보이면 안 된다
        byte[] plain = File.ReadAllBytes(sources.Single(s => Path.GetFileName(s) == "readme.txt"));
        Assert.True(File.ReadAllBytes(archive).AsSpan().IndexOf(plain) < 0, "암호화된 7Z 에 평문이 보입니다");

        var noPassword = Archives.For(archive);
        var list = noPassword.List(archive);
        Assert.True(list.Result.Succeeded, list.Result.Message); // 이름은 암호화하지 않았다
        Assert.True(list.Entries.Single(e => e.Path == "readme.txt").Encrypted);

        string missingOut = temp.Combine("missing");
        var missing = noPassword.Extract(archive, missingOut, new ExtractOptions());
        Assert.Equal(OperationStatus.PasswordRequired, missing.Status);
        Assert.Contains("암호가 필요합니다", missing.Message);
        Assert.False(Directory.Exists(missingOut));
        Assert.Equal(OperationStatus.PasswordRequired, noPassword.Test(archive).Status);

        var wrong = Archives.For(archive, new ReadOptions { Password = WrongPw });
        string wrongOut = temp.Combine("wrong");
        var wrongResult = wrong.Extract(archive, wrongOut, new ExtractOptions());
        Assert.Equal(OperationStatus.PasswordRequired, wrongResult.Status);
        Assert.Contains("암호가 올바르지 않습니다", wrongResult.Message);
        Assert.Empty(Util.FilesUnder(wrongOut));
        Assert.False(Directory.Exists(wrongOut), "틀린 암호로 만든 폴더가 남았습니다");
        Assert.Equal(OperationStatus.PasswordRequired, wrong.Test(archive).Status);
        Assert.DoesNotContain(Pw, wrongResult.Message + string.Join("", wrongResult.Problems));

        AssertRoundTrip(Archives.For(archive, new ReadOptions { Password = Pw }), archive, temp.Combine("ok"));

        // LZMA + AES 사슬
        string lzma = temp.Combine("aes-lzma.7z");
        Assert.True(new SevenZipEngine().Create(lzma, sources, SevenZip(level: 9, password: Pw)).Succeeded);
        AssertRoundTrip(new SevenZipEngine(new ReadOptions { Password = Pw }), lzma, temp.Combine("ok-lzma"));
        Assert.Equal(OperationStatus.PasswordRequired, new SevenZipEngine(new ReadOptions { Password = WrongPw }).Test(lzma).Status);

        // 조용히 무시하지 않는 옵션 조합
        var engine = new SevenZipEngine();
        Assert.Equal(OperationStatus.NotSupported,
            engine.Create(temp.Combine("a.7z"), sources, SevenZip() with { Password = Pw }).Status);
        Assert.Equal(OperationStatus.Failed,
            engine.Create(temp.Combine("b.7z"), sources, SevenZip() with { Encryption = EncryptionMethod.Aes256 }).Status);
        Assert.Equal(OperationStatus.NotSupported,
            engine.Create(temp.Combine("c.7z"), sources, SevenZip() with { EncryptNames = true }).Status);
        Assert.False(File.Exists(temp.Combine("a.7z")) || File.Exists(temp.Combine("b.7z")) || File.Exists(temp.Combine("c.7z")));
        AssertNoTemp(temp.Path);
    }

    [Fact]
    public void OZ_E23_SevenZip_encrypted_names_need_password_to_list()
    {
        using var temp = new TempDir();
        string[] sources = InputSources(temp);
        string archive = temp.Combine("names.7z");
        Assert.True(new SevenZipEngine().Create(archive, sources, SevenZip(password: Pw, encryptNames: true)).Succeeded);
        byte[] raw = File.ReadAllBytes(archive);
        Assert.True(raw.AsSpan().IndexOf(Encoding.Unicode.GetBytes("readme")) < 0, "헤더 암호화인데 이름이 보입니다");

        var noPassword = Archives.For(archive);
        var list = noPassword.List(archive);
        Assert.Equal(OperationStatus.PasswordRequired, list.Result.Status);
        Assert.Contains("암호가 필요합니다", list.Result.Message);
        Assert.Empty(list.Entries);
        Assert.Equal(OperationStatus.PasswordRequired, noPassword.Test(archive).Status);
        Assert.Equal(OperationStatus.PasswordRequired, noPassword.Extract(archive, temp.Combine("x"), new ExtractOptions()).Status);
        Assert.False(Directory.Exists(temp.Combine("x")));

        var wrong = new SevenZipEngine(new ReadOptions { Password = WrongPw }).List(archive);
        Assert.Equal(OperationStatus.PasswordRequired, wrong.Result.Status);
        Assert.Contains("암호가 올바르지 않습니다", wrong.Result.Message);

        var engine = new SevenZipEngine(new ReadOptions { Password = Pw });
        var ok = engine.List(archive);
        Assert.True(ok.Result.Succeeded, ok.Result.Message);
        Assert.Contains(ok.Entries, e => e.Path == "nested/notes.txt" && e.Encrypted);
        AssertRoundTrip(engine, archive, temp.Combine("out"));
    }

    [Fact]
    public void OZ_E24_SevenZip_volumes_exact_sizes_round_trip_missing_and_corrupted_parts_fail()
    {
        using var temp = new TempDir();
        string[] sources = InputSources(temp);
        string random = temp.Combine("random.bin");
        File.WriteAllBytes(random, System.Security.Cryptography.RandomNumberGenerator.GetBytes(200_000));
        string dir = temp.Combine("vol");
        Directory.CreateDirectory(dir);
        string archive = Path.Combine(dir, "name.7z");
        const long volume = 64 * 1024;

        var created = new SevenZipEngine().Create(archive, [.. sources, random], SevenZip(level: 1, volume: volume));
        Assert.True(created.Succeeded, created.Message);
        Assert.False(File.Exists(archive));
        var parts = Directory.GetFiles(dir).Select(p => Path.GetFileName(p)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(["name.7z.001", "name.7z.002", "name.7z.003", "name.7z.004"], parts);
        for (int i = 0; i < parts.Count - 1; i++)
            Assert.Equal(volume, new FileInfo(Path.Combine(dir, parts[i])).Length);
        Assert.InRange(new FileInfo(Path.Combine(dir, parts[^1])).Length, 1, volume);

        string first = Path.Combine(dir, "name.7z.001");
        Assert.Equal(ArchiveFormat.SevenZip, Archives.Detect(first));
        var engine = Archives.For(first);
        Assert.True(engine.List(first).Result.Succeeded);
        AssertRoundTrip(engine, first, temp.Combine("out"));
        Assert.Equal(Util.Sha256(random), Util.Sha256(temp.Combine("out", "random.bin")));
        Assert.Equal(OperationStatus.NotSupported, engine.Delete(first, ["readme.txt"]).Status);

        // 손상된 조각: 데이터 한 바이트
        string corruptDir = temp.Combine("corrupt");
        Directory.CreateDirectory(corruptDir);
        foreach (string p in parts)
            File.Copy(Path.Combine(dir, p), Path.Combine(corruptDir, p));
        string corruptPart = Path.Combine(corruptDir, "name.7z.002");
        byte[] bytes = File.ReadAllBytes(corruptPart);
        bytes[1000] ^= 0x55;
        File.WriteAllBytes(corruptPart, bytes);
        string corruptFirst = Path.Combine(corruptDir, "name.7z.001");
        Assert.Equal(OperationStatus.Failed, engine.Test(corruptFirst).Status);
        var corruptExtract = engine.Extract(corruptFirst, temp.Combine("corrupt-out"), new ExtractOptions());
        Assert.Equal(OperationStatus.Failed, corruptExtract.Status);
        Assert.False(File.Exists(temp.Combine("corrupt-out", "random.bin")));

        // 가운데 조각이 없음
        File.Delete(Path.Combine(dir, "name.7z.002"));
        var missing = engine.List(first);
        Assert.Equal(OperationStatus.Failed, missing.Result.Status);
        Assert.Contains("name.7z.002", missing.Result.Message);
        Assert.Equal(OperationStatus.Failed, engine.Test(first).Status);
        Assert.Equal(OperationStatus.Failed, engine.Extract(first, temp.Combine("missing-out"), new ExtractOptions()).Status);
        Assert.False(Directory.Exists(temp.Combine("missing-out")));

        // 너무 작은 분할 크기
        var tooSmall = new SevenZipEngine().Create(temp.Combine("small.7z"), sources, SevenZip(volume: 1000));
        Assert.Equal(OperationStatus.Failed, tooSmall.Status);
        Assert.Contains("64 KiB", tooSmall.Message);
        Assert.Empty(Directory.GetFiles(temp.Path, "small.7z*"));
        AssertNoTemp(temp.Path);
    }

    [Fact]
    public void OZ_E25_SevenZip_add_rename_delete_plain()
    {
        using var temp = new TempDir();
        string[] sources = InputSources(temp);
        string emptyFile = temp.Combine("zero.txt");
        File.WriteAllBytes(emptyFile, []);
        string added = Fixtures.CopyTo("added.txt", temp);
        string archive = temp.Combine("m.7z");
        var engine = new SevenZipEngine();
        Assert.True(engine.Capabilities.Modify);
        Assert.True(engine.Create(archive, [.. sources, emptyFile], SevenZip()).Succeeded);

        int reports = 0;
        var add = engine.Add(archive, [added], "", new SyncProgress(_ => reports++));
        Assert.True(add.Succeeded, add.Message);
        Assert.True(reports > 0);
        Assert.True(engine.Add(archive, [added], "newdir/sub").Succeeded);
        Assert.Equal(OperationStatus.Failed, engine.Add(archive, [added]).Status); // 이미 있음
        Assert.True(engine.Rename(archive, "readme.txt", "readme2.txt").Succeeded);
        Assert.True(engine.Rename(archive, "nested", "nested2").Succeeded);
        Assert.True(engine.Delete(archive, [Fixtures.KoreanName]).Succeeded);
        Assert.Equal(OperationStatus.Failed, engine.Delete(archive, ["no-such.txt"]).Status);
        Assert.Equal(OperationStatus.Failed, engine.Rename(archive, "readme2.txt", "../x.txt").Status);

        var paths = engine.List(archive).Entries.Select(e => e.Path).ToHashSet();
        Assert.Contains("readme2.txt", paths);
        Assert.Contains("nested2/notes.txt", paths);
        Assert.Contains("added.txt", paths);
        Assert.Contains("newdir/sub/added.txt", paths);
        Assert.Contains("empty", paths);
        Assert.Contains("zero.txt", paths);
        Assert.DoesNotContain("readme.txt", paths);
        Assert.DoesNotContain("nested/notes.txt", paths);
        Assert.DoesNotContain(Fixtures.KoreanName, paths);

        string output = temp.Combine("out");
        Assert.True(engine.Test(archive).Succeeded);
        Assert.True(engine.Extract(archive, output, new ExtractOptions()).Succeeded);
        Assert.Equal(Fixtures.ReadmeHash, Util.Sha256(Path.Combine(output, "readme2.txt")));
        Assert.Equal(Fixtures.NotesHash, Util.Sha256(Path.Combine(output, "nested2", "notes.txt")));
        Assert.Equal(Util.Sha256(added), Util.Sha256(Path.Combine(output, "newdir", "sub", "added.txt")));
        Assert.Equal(0, new FileInfo(Path.Combine(output, "zero.txt")).Length);
        Assert.True(Directory.Exists(Path.Combine(output, "empty")));
        AssertNoTemp(temp.Path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OZ_E26_SevenZip_modify_encrypted_archive_keeps_password(bool encryptNames)
    {
        using var temp = new TempDir();
        string[] sources = InputSources(temp);
        string added = Fixtures.CopyTo("added.txt", temp);
        string archive = temp.Combine("enc.7z");
        Assert.True(new SevenZipEngine().Create(archive, sources, SevenZip(password: Pw, encryptNames: encryptNames)).Succeeded);
        string before = Util.Sha256(archive);

        // 암호 없이·틀린 암호로는 고치지 못하고 원본은 그대로
        var noPw = new SevenZipEngine().Add(archive, [added]);
        Assert.Equal(OperationStatus.PasswordRequired, noPw.Status);
        var wrong = new SevenZipEngine(new ReadOptions { Password = WrongPw }).Delete(archive, ["readme.txt"]);
        Assert.Equal(OperationStatus.PasswordRequired, wrong.Status);
        Assert.Equal(before, Util.Sha256(archive));
        AssertNoTemp(temp.Path);

        var engine = new SevenZipEngine(new ReadOptions { Password = Pw });
        Assert.True(engine.Add(archive, [added], "box").Succeeded);
        Assert.True(engine.Rename(archive, "readme.txt", "r.txt").Succeeded);
        Assert.True(engine.Delete(archive, ["nested"]).Succeeded);

        var plainList = new SevenZipEngine().List(archive);
        if (encryptNames)
        {
            Assert.Equal(OperationStatus.PasswordRequired, plainList.Result.Status);
        }
        else
        {
            Assert.True(plainList.Result.Succeeded);
            Assert.All(plainList.Entries.Where(e => !e.IsDirectory), e => Assert.True(e.Encrypted));
        }
        Assert.Equal(OperationStatus.PasswordRequired, new SevenZipEngine().Extract(archive, temp.Combine("np"), new ExtractOptions()).Status);

        string output = temp.Combine("out");
        var result = engine.Extract(archive, output, new ExtractOptions());
        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(["box/added.txt", "r.txt", Fixtures.KoreanName], Util.FilesUnder(output));
        Assert.Equal(Fixtures.ReadmeHash, Util.Sha256(Path.Combine(output, "r.txt")));
        Assert.Equal(Fixtures.KoreanHash, Util.Sha256(Path.Combine(output, Fixtures.KoreanName)));
        Assert.True(Directory.Exists(Path.Combine(output, "empty")));
    }

    [Fact]
    public void OZ_E27_SevenZip_cancel_create_leaves_nothing_and_cancel_modify_keeps_original()
    {
        using var temp = new TempDir();
        string[] sources = InputSources(temp);
        string big = temp.Combine("big.bin");
        File.WriteAllBytes(big, System.Security.Cryptography.RandomNumberGenerator.GetBytes(400_000));
        string outDir = temp.Combine("o");
        Directory.CreateDirectory(outDir);

        foreach (var options in new[] { SevenZip(), SevenZip(volume: 64 * 1024), SevenZip(password: Pw, encryptNames: true) })
        {
            using var cts = new CancellationTokenSource();
            int reports = 0;
            var progress = new SyncProgress(_ =>
            {
                if (++reports == 2)
                    cts.Cancel();
            });
            var result = new SevenZipEngine().Create(Path.Combine(outDir, "c.7z"), [.. sources, big], options, progress, cts.Token);
            Assert.Equal(OperationStatus.Cancelled, result.Status);
            Assert.Empty(Directory.GetFileSystemEntries(outDir));
        }

        string archive = temp.Combine("m.7z");
        Assert.True(new SevenZipEngine().Create(archive, [.. sources, big], SevenZip()).Succeeded);
        string before = Util.Sha256(archive);
        string added = Fixtures.CopyTo("added.txt", temp);
        var engine = new SevenZipEngine();
        Func<CancellationToken, IProgress<ProgressInfo>, OperationResult>[] ops =
        [
            (c, p) => engine.Add(archive, [added], "", p, c),
            (c, p) => engine.Delete(archive, ["readme.txt"], p, c),
            (c, p) => engine.Rename(archive, "readme.txt", "x.txt", p, c),
        ];
        foreach (var op in ops)
        {
            using var cts = new CancellationTokenSource();
            int reports = 0;
            var result = op(cts.Token, new SyncProgress(_ =>
            {
                if (++reports == 2)
                    cts.Cancel();
            }));
            Assert.Equal(OperationStatus.Cancelled, result.Status);
            Assert.Equal(before, Util.Sha256(archive));
            AssertNoTemp(temp.Path);
        }
    }

    [Fact]
    public void OZ_E28_SevenZip_crafted_traversal_names_are_rejected_on_extract()
    {
        using var temp = new TempDir();
        string archive = temp.Combine("evil.7z");
        using (var fs = File.Create(archive))
        {
            SevenZipItem File(string name, string text) =>
                new(name, false, DateTime.UtcNow, () => new MemoryStream(Encoding.UTF8.GetBytes(text)));
            SevenZipWriter.Write(fs,
                [File("../evil.txt", "evil"), File("..\\evil2.txt", "evil"), File("C:/abs.txt", "abs"), File("ok.txt", "ok")],
                new SevenZipWriteOptions(5), 0, null, CancellationToken.None);
        }
        var engine = Archives.For(archive);
        Assert.IsType<SevenZipEngine>(engine);
        Assert.Contains(engine.List(archive).Entries, e => e.Path == "../evil.txt");

        string root = temp.Combine("sub", "out");
        var result = engine.Extract(archive, root, new ExtractOptions());
        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Contains(result.Problems, p => p.Contains("../evil.txt"));
        Assert.False(File.Exists(temp.Combine("sub", "evil.txt")));
        Assert.False(File.Exists(temp.Combine("sub", "evil2.txt")));
        Assert.Equal(["ok.txt"], Util.FilesUnder(temp.Combine("sub")).Select(p => p.Replace("out/", "")).ToList());
    }
}
