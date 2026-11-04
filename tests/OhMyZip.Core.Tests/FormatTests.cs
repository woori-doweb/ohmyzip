using System.IO.Compression;
using System.Text;

namespace OhMyZip.Core.Tests;

/// <summary>TAR · TAR.GZ · GZ · RAR(읽기) 시험.</summary>
public class FormatTests
{
    /// <summary>
    /// tests/OhMyZip.Core.Tests/TestData 위치. csproj 를 고치지 않으려고 시험 어셈블리 위치에서 위로 올라가며 찾는다.
    /// </summary>
    private static readonly Lazy<string> TestDataDir = new(() =>
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            string candidate = Path.Combine(d.FullName, "tests", "OhMyZip.Core.Tests", "TestData");
            if (Directory.Exists(candidate))
                return candidate;
        }
        throw new DirectoryNotFoundException("tests/OhMyZip.Core.Tests/TestData 를 찾지 못했습니다");
    });

    /// <summary>TestData 파일을 임시 폴더로 복사한다(원본은 건드리지 않음).</summary>
    private static string CopyTestData(string relative, TempDir temp)
    {
        string target = Path.Combine(temp.Path, Path.GetFileName(relative));
        File.Copy(Path.Combine(TestDataDir.Value, relative), target);
        return target;
    }

    private static void AssertInputRoundTrip(IArchiveEngine engine, string archive, string output)
    {
        var test = engine.Test(archive);
        Assert.True(test.Succeeded, test.Message + string.Join("; ", test.Problems));
        var result = engine.Extract(archive, output, new ExtractOptions());
        Assert.True(result.Succeeded, result.Message + string.Join("; ", result.Problems));
        Util.AssertExpectedHashes(output);
        Assert.True(Directory.Exists(Path.Combine(output, "empty")), "빈 폴더 empty/ 가 풀리지 않았습니다");
    }

    private static void AssertNoTemp(string dir) =>
        Assert.Empty(Directory.GetFiles(dir, "~oz*", SearchOption.AllDirectories));

    /// <summary>TAR/TAR.GZ 공통: 만들기 → 목록 → 검사 → 해제, 긴 이름·한글, 고치기(추가·이름 바꾸기·삭제).</summary>
    private static void TarFamilyRoundTrip(ArchiveFormat format, string extension)
    {
        using var temp = new TempDir();
        string[] sources = SevenZipWriteTests.InputSources(temp);
        string longName = new string('L', 110) + "_긴이름.txt";
        string longFile = temp.Combine(longName);
        File.WriteAllText(longFile, "long name payload");
        string archive = temp.Combine("t" + extension);
        var engine = Archives.For(format);
        Assert.IsType<TarEngine>(engine);
        Assert.Equal(new FormatCapabilities(true, true, true, true, true, false, false, false), engine.Capabilities);

        var created = engine.Create(archive, [.. sources, longFile], new CreateOptions { Format = format });
        Assert.True(created.Succeeded, created.Message);
        Assert.Equal(format, Archives.Detect(archive));
        var opened = Archives.For(archive);

        var list = opened.List(archive);
        Assert.True(list.Result.Succeeded, list.Result.Message);
        var byPath = list.Entries.ToDictionary(e => e.Path);
        Assert.Equal(52, byPath["readme.txt"].Size);
        Assert.True(byPath["empty"].IsDirectory);
        Assert.True(byPath["nested"].IsDirectory);
        Assert.Contains(Fixtures.KoreanName, byPath.Keys);
        Assert.Contains(longName, byPath.Keys);

        string output = temp.Combine("out");
        AssertInputRoundTrip(opened, archive, output);
        Assert.Equal(Util.Sha256(longFile), Util.Sha256(Path.Combine(output, longName)));

        string added = Fixtures.CopyTo("added.txt", temp);
        Assert.True(opened.Add(archive, [added], "box").Succeeded);
        Assert.True(opened.Rename(archive, "nested", "nested2").Succeeded);
        Assert.True(opened.Delete(archive, [Fixtures.KoreanName, longName]).Succeeded);
        Assert.Equal(OperationStatus.Failed, opened.Delete(archive, ["no-such"]).Status);
        string modified = temp.Combine("mod");
        var extracted = opened.Extract(archive, modified, new ExtractOptions());
        Assert.True(extracted.Succeeded, extracted.Message);
        Assert.Equal(["box/added.txt", "nested2/notes.txt", "readme.txt"], Util.FilesUnder(modified));
        Assert.Equal(Fixtures.NotesHash, Util.Sha256(Path.Combine(modified, "nested2", "notes.txt")));
        Assert.True(Directory.Exists(Path.Combine(modified, "empty")));

        // 조용히 무시하지 않는 옵션
        Assert.Equal(OperationStatus.NotSupported, engine.Create(temp.Combine("p" + extension), sources,
            new CreateOptions { Format = format, Password = "x", Encryption = EncryptionMethod.Aes256 }).Status);
        Assert.Equal(OperationStatus.NotSupported, engine.Create(temp.Combine("v" + extension), sources,
            new CreateOptions { Format = format, VolumeSize = 1 << 20 }).Status);
        Assert.Equal(OperationStatus.NotSupported, engine.SetComment(archive, "x").Status);
        Assert.False(File.Exists(temp.Combine("p" + extension)) || File.Exists(temp.Combine("v" + extension)));
        AssertNoTemp(temp.Path);
    }

    [Fact]
    public void OZ_E29_Tar_create_list_test_extract_and_modify_round_trip()
    {
        TarFamilyRoundTrip(ArchiveFormat.Tar, ".tar");

        using var temp = new TempDir();
        string[] sources = SevenZipWriteTests.InputSources(temp);
        string archive = temp.Combine("c.tar");
        var engine = new TarEngine(ArchiveFormat.Tar);
        Assert.Equal(OperationStatus.NotSupported, engine.Create(archive, sources, new CreateOptions { Format = ArchiveFormat.Tar, Level = 9 }).Status);
        Assert.True(engine.Create(archive, sources, new CreateOptions { Format = ArchiveFormat.Tar }).Succeeded);
        // 첫 헤더 블록의 체크섬 필드를 깨뜨린다
        byte[] bytes = File.ReadAllBytes(archive);
        bytes[150] ^= 0x07;
        File.WriteAllBytes(archive, bytes);
        Assert.Equal(OperationStatus.Failed, engine.Test(archive).Status);
        Assert.Equal(OperationStatus.Failed, engine.Extract(archive, temp.Combine("bad"), new ExtractOptions()).Status);
        Assert.Empty(Util.FilesUnder(temp.Combine("bad")));
    }

    [Fact]
    public void OZ_E29_Tar_traversal_and_link_entries_are_rejected_on_extract()
    {
        using var temp = new TempDir();
        string archive = temp.Combine("evil.tar");
        using (var fs = File.Create(archive))
        using (var writer = new System.Formats.Tar.TarWriter(fs, System.Formats.Tar.TarEntryFormat.Pax))
        {
            foreach (string name in new[] { "../evil.txt", "ok.txt" })
            {
                writer.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, name)
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes("x")),
                });
            }
            writer.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.SymbolicLink, "link") { LinkName = "../../outside" });
        }
        var engine = Archives.For(archive);
        string root = temp.Combine("sub", "out");
        var result = engine.Extract(archive, root, new ExtractOptions());
        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Contains(result.Problems, p => p.Contains("../evil.txt"));
        Assert.Contains(result.Problems, p => p.Contains("link"));
        Assert.False(File.Exists(temp.Combine("sub", "evil.txt")));
        Assert.Equal(["out/ok.txt"], Util.FilesUnder(temp.Combine("sub")));
    }

    [Fact]
    public void OZ_E30_TarGz_create_list_test_extract_modify_and_corruption()
    {
        TarFamilyRoundTrip(ArchiveFormat.TarGz, ".tar.gz");

        using var temp = new TempDir();
        string[] sources = SevenZipWriteTests.InputSources(temp);
        var engine = new TarEngine(ArchiveFormat.TarGz);
        string store = temp.Combine("l0.tar.gz");
        string best = temp.Combine("l9.tar.gz");
        Assert.True(engine.Create(store, sources, new CreateOptions { Format = ArchiveFormat.TarGz, Level = 0 }).Succeeded);
        Assert.True(engine.Create(best, sources, new CreateOptions { Format = ArchiveFormat.TarGz, Level = 9 }).Succeeded);
        Assert.True(new FileInfo(best).Length < new FileInfo(store).Length);

        // GZ 꼬리의 CRC 를 깨뜨리면 검사가 실패한다
        byte[] bytes = File.ReadAllBytes(best);
        bytes[^8] ^= 0xFF;
        File.WriteAllBytes(best, bytes);
        Assert.Equal(OperationStatus.Failed, engine.Test(best).Status);

        // 취소하면 아무것도 남지 않는다
        string big = temp.Combine("big.bin");
        File.WriteAllBytes(big, System.Security.Cryptography.RandomNumberGenerator.GetBytes(300_000));
        string outDir = temp.Combine("o");
        Directory.CreateDirectory(outDir);
        using var cts = new CancellationTokenSource();
        int reports = 0;
        var cancelled = engine.Create(Path.Combine(outDir, "c.tar.gz"), [big], new CreateOptions { Format = ArchiveFormat.TarGz }, new SyncProgress(_ =>
        {
            if (++reports == 2)
                cts.Cancel();
        }), cts.Token);
        Assert.Equal(OperationStatus.Cancelled, cancelled.Status);
        Assert.Empty(Directory.GetFileSystemEntries(outDir));
    }

    [Fact]
    public void OZ_E31_GZip_single_file_create_list_test_extract()
    {
        using var temp = new TempDir();
        string korean = Fixtures.CopyTo(Path.Combine("input", Fixtures.KoreanName), temp);
        string readme = Fixtures.CopyTo(Path.Combine("input", "readme.txt"), temp);
        string archive = temp.Combine(Fixtures.KoreanName + ".gz");
        var engine = Archives.For(ArchiveFormat.GZip);
        Assert.IsType<TarEngine>(engine);
        Assert.Equal(new FormatCapabilities(true, true, true, true, false, false, false, false), engine.Capabilities);

        Assert.True(engine.Create(archive, [korean], new CreateOptions { Format = ArchiveFormat.GZip, Level = 9 }).Succeeded);
        Assert.Equal(ArchiveFormat.GZip, Archives.Detect(archive));
        var list = Archives.For(archive).List(archive);
        Assert.True(list.Result.Succeeded, list.Result.Message);
        var entry = Assert.Single(list.Entries);
        Assert.Equal(Fixtures.KoreanName, entry.Path); // ASCII 가 아닌 이름은 FNAME 없이 압축 파일 이름에서
        Assert.Equal(new FileInfo(korean).Length, entry.Size);
        Assert.NotNull(entry.Crc32);
        Assert.True(engine.Test(archive).Succeeded);
        string output = temp.Combine("out");
        Assert.True(engine.Extract(archive, output, new ExtractOptions()).Succeeded);
        Assert.Equal(Fixtures.KoreanHash, Util.Sha256(Path.Combine(output, Fixtures.KoreanName)));

        // ASCII 이름은 머리의 FNAME 으로 남는다
        string ascii = temp.Combine("other-name.gz");
        Assert.True(engine.Create(ascii, [readme], new CreateOptions { Format = ArchiveFormat.GZip }).Succeeded);
        Assert.Equal("readme.txt", Assert.Single(engine.List(ascii).Entries).Path);
        string asciiOut = temp.Combine("ascii-out");
        Assert.True(engine.Extract(ascii, asciiOut, new ExtractOptions()).Succeeded);
        Assert.Equal(Fixtures.ReadmeHash, Util.Sha256(Path.Combine(asciiOut, "readme.txt")));

        // FNAME 이 없으면 압축 파일 이름에서 .gz 를 뺍 이름
        string plain = temp.Combine("data.bin.gz");
        using (var fs = File.Create(plain))
        using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
            gz.Write(File.ReadAllBytes(readme));
        Assert.Equal("data.bin", Assert.Single(engine.List(plain).Entries).Path);
        Assert.True(engine.Test(plain).Succeeded);

        // 여러 원본·폴더는 NotSupported, 고치기도 NotSupported
        Assert.Equal(OperationStatus.NotSupported, engine.Create(temp.Combine("two.gz"), [korean, readme], new CreateOptions { Format = ArchiveFormat.GZip }).Status);
        Assert.Equal(OperationStatus.NotSupported, engine.Create(temp.Combine("dir.gz"), [output], new CreateOptions { Format = ArchiveFormat.GZip }).Status);
        Assert.Equal(OperationStatus.NotSupported, engine.Add(archive, [readme]).Status);
        Assert.Equal(OperationStatus.NotSupported, engine.Delete(archive, [Fixtures.KoreanName]).Status);
        Assert.False(File.Exists(temp.Combine("two.gz")) || File.Exists(temp.Combine("dir.gz")));

        // 꼬리 CRC 가 깨지면 검사·해제 모두 실패하고 파일을 남기지 않는다
        byte[] bytes = File.ReadAllBytes(archive);
        bytes[^8] ^= 0xFF;
        File.WriteAllBytes(archive, bytes);
        Assert.Equal(OperationStatus.Failed, engine.Test(archive).Status);
        string bad = temp.Combine("bad");
        Assert.Equal(OperationStatus.Failed, engine.Extract(archive, bad, new ExtractOptions()).Status);
        Assert.Empty(Util.FilesUnder(bad));
        AssertNoTemp(temp.Path);
    }

    /// <summary>SharpCompress 시험 자료(TestData/rar/SOURCES.md)의 내용. 기대 해시는 별도 외부 도구 교차 해제 결과(레인 logs/crosscheck-fixture-*.txt)와 같다.</summary>
    private static readonly Dictionary<string, string> RarHashes = new()
    {
        ["exe/test.exe"] = "8557928804F57ECC340B3BB38B095A3607474EC8DEB0076F316FCFE02B562106",
        ["jpg/test.jpg"] = "B251C7501FB0F55DD4A92FEABE0A6F5733BC40A02679498155FAE9B30138FC53",
        ["тест.txt"] = "4D581D93D369F6E1C9B295FF38D82DABD577F927DFAF0C35818C015C85E322D9",
    };

    /// <summary>SharpCompress 시험 자료에 공개된 암호(합성·비밀 아님).</summary>
    private const string RarFixturePassword = "test";

    private static void AssertRarContents(string output)
    {
        Assert.Equal(RarHashes.Keys.OrderBy(k => k, StringComparer.Ordinal), Util.FilesUnder(output));
        foreach (var (path, hash) in RarHashes)
            Assert.Equal(hash, Util.Sha256(Path.Combine(output, path)));
    }

    [Theory]
    [InlineData("Rar4.rar")]
    [InlineData("Rar5.rar")]
    public void OZ_E32_Rar_read_list_test_extract(string fixture)
    {
        using var temp = new TempDir();
        string archive = CopyTestData(Path.Combine("rar", fixture), temp);
        string before = Util.Sha256(archive);
        Assert.Equal(ArchiveFormat.Rar, Archives.Detect(archive));
        var engine = Archives.For(archive);
        Assert.IsType<RarReadEngine>(engine);
        Assert.Equal(new FormatCapabilities(true, true, true, false, false, false, false, false), engine.Capabilities);

        var list = engine.List(archive);
        Assert.True(list.Result.Succeeded, list.Result.Message);
        Assert.Equal(15498, list.Entries.Single(e => e.Path == "тест.txt").Size);
        Assert.True(list.Entries.Single(e => e.Path == "exe").IsDirectory);
        Assert.All(list.Entries, e => Assert.False(e.Encrypted));
        Assert.True(engine.Test(archive).Succeeded);

        string output = temp.Combine("out");
        var result = engine.Extract(archive, output, new ExtractOptions());
        Assert.True(result.Succeeded, result.Message);
        AssertRarContents(output);

        Assert.Equal(OperationStatus.NotSupported, engine.Create(temp.Combine("new.rar"), [output]).Status);
        Assert.Equal(OperationStatus.NotSupported, engine.Add(archive, [output]).Status);
        Assert.Equal(OperationStatus.NotSupported, engine.Delete(archive, ["тест.txt"]).Status);
        Assert.Equal(OperationStatus.NotSupported, engine.Rename(archive, "тест.txt", "x.txt").Status);
        Assert.Equal(before, Util.Sha256(archive));
        Assert.False(File.Exists(temp.Combine("new.rar")));

        // 손상: 압축 데이터 가운데 한 바이트
        byte[] bytes = File.ReadAllBytes(archive);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(archive, bytes);
        Assert.Equal(OperationStatus.Failed, engine.Test(archive).Status);
    }

    [Theory]
    [InlineData("Rar5.encrypted_filesOnly.rar", false)]
    [InlineData("Rar5.encrypted_filesAndHeader.rar", true)]
    [InlineData("Rar.encrypted_filesAndHeader.rar", true)]
    public void OZ_E33_Rar_encrypted_needs_password_and_wrong_password_leaves_nothing(string fixture, bool headerEncrypted)
    {
        using var temp = new TempDir();
        string archive = CopyTestData(Path.Combine("rar", fixture), temp);

        var noPassword = Archives.For(archive);
        var list = noPassword.List(archive);
        if (headerEncrypted)
        {
            Assert.Equal(OperationStatus.PasswordRequired, list.Result.Status);
            Assert.Contains("암호가 필요합니다", list.Result.Message);
        }
        else
        {
            Assert.True(list.Result.Succeeded, list.Result.Message);
            Assert.True(list.Entries.Single(e => e.Path == "тест.txt").Encrypted);
        }
        string missingOut = temp.Combine("missing");
        var missing = noPassword.Extract(archive, missingOut, new ExtractOptions());
        Assert.Equal(OperationStatus.PasswordRequired, missing.Status);
        Assert.Contains("암호가 필요합니다", missing.Message);
        Assert.False(Directory.Exists(missingOut));
        Assert.Equal(OperationStatus.PasswordRequired, noPassword.Test(archive).Status);

        var wrong = Archives.For(archive, new ReadOptions { Password = "oz-synthetic-wrong" });
        string wrongOut = temp.Combine("wrong");
        var wrongResult = wrong.Extract(archive, wrongOut, new ExtractOptions());
        Assert.Equal(OperationStatus.PasswordRequired, wrongResult.Status);
        Assert.Contains("암호가 올바르지 않습니다", wrongResult.Message);
        Assert.Empty(Util.FilesUnder(wrongOut));
        Assert.Equal(OperationStatus.PasswordRequired, wrong.Test(archive).Status);

        var engine = Archives.For(archive, new ReadOptions { Password = RarFixturePassword });
        Assert.True(engine.List(archive).Result.Succeeded);
        Assert.True(engine.Test(archive).Succeeded);
        string output = temp.Combine("out");
        var result = engine.Extract(archive, output, new ExtractOptions());
        Assert.True(result.Succeeded, result.Message);
        AssertRarContents(output);
    }

    /// <summary>
    /// TAR 헤더 블록 하나(512바이트)와 데이터(512 배수로 채움)를 쓴다. ustar=false 면 옛 v7 헤더(magic 없음).
    /// 체크섬은 규격대로 체크섬 필드를 공백으로 본 바이트 합.
    /// </summary>
    private static void WriteRawTarEntry(Stream output, byte[] name, char type, byte[] data, bool ustar = true)
    {
        byte[] header = new byte[512];
        name.CopyTo(header, 0);
        void Octal(int offset, int length, long value) =>
            Encoding.ASCII.GetBytes(Convert.ToString(value, 8).PadLeft(length - 1, '0')).CopyTo(header, offset);
        Octal(100, 8, type == '5' ? 0b111_101_101 : 0b110_100_100);
        Octal(108, 8, 0);
        Octal(116, 8, 0);
        Octal(124, 12, data.Length);
        Octal(136, 12, 1_700_000_000);
        header[156] = (byte)type;
        if (ustar)
        {
            "ustar\0"u8.CopyTo(header.AsSpan(257));
            "00"u8.CopyTo(header.AsSpan(263));
        }
        for (int i = 148; i < 156; i++)
            header[i] = (byte)' ';
        int sum = header.Sum(b => b);
        Encoding.ASCII.GetBytes(Convert.ToString(sum, 8).PadLeft(6, '0') + "\0 ").CopyTo(header, 148);
        output.Write(header);
        output.Write(data);
        output.Write(new byte[(512 - data.Length % 512) % 512]);
    }

    [Fact]
    public void OZ_E34_Tar_from_other_tools_cp949_ustar_gnu_longname_and_v7_are_read()
    {
        using var temp = new TempDir();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        byte[] koreanPayload = Encoding.UTF8.GetBytes("cp949 name payload");
        byte[] longPayload = Encoding.UTF8.GetBytes("gnu long name payload");
        byte[] v7Payload = Encoding.UTF8.GetBytes("v7 payload");
        string longName = "deep/" + new string('n', 120) + ".txt";
        string archive = temp.Combine("legacy.tar");
        using (var fs = File.Create(archive))
        {
            // 한국어 Windows 도구처럼 ustar 이름을 CP949 로 쓴 항목
            WriteRawTarEntry(fs, Encoding.GetEncoding(949).GetBytes(Fixtures.KoreanName), '0', koreanPayload);
            // GNU 긴 이름('L' 항목 뒤 짧은 이름의 실제 항목)
            byte[] longBytes = [.. Encoding.ASCII.GetBytes(longName), 0];
            WriteRawTarEntry(fs, Encoding.ASCII.GetBytes("././@LongLink"), 'L', longBytes);
            WriteRawTarEntry(fs, Encoding.ASCII.GetBytes(longName[..99]), '0', longPayload);
            // 옛 v7: magic 없음, 폴더는 이름 끝 '/'
            WriteRawTarEntry(fs, Encoding.ASCII.GetBytes("v7dir/"), '\0', [], ustar: false);
            WriteRawTarEntry(fs, Encoding.ASCII.GetBytes("v7dir/v7.txt"), '\0', v7Payload, ustar: false);
            fs.Write(new byte[1024]);
        }
        Assert.Equal(ArchiveFormat.Tar, Archives.Detect(archive));
        var engine = Archives.For(archive);

        var list = engine.List(archive);
        Assert.True(list.Result.Succeeded, list.Result.Message);
        var byPath = list.Entries.ToDictionary(e => e.Path);
        Assert.Equal(koreanPayload.Length, byPath[Fixtures.KoreanName].Size);
        Assert.Equal(longPayload.Length, byPath[longName].Size);
        Assert.True(byPath["v7dir"].IsDirectory);
        Assert.Equal(v7Payload.Length, byPath["v7dir/v7.txt"].Size);
        Assert.Equal(new DateTimeOffset(2023, 11, 14, 22, 13, 20, TimeSpan.Zero), byPath["v7dir/v7.txt"].Modified);
        Assert.True(engine.Test(archive).Succeeded);

        string output = temp.Combine("out");
        var result = engine.Extract(archive, output, new ExtractOptions());
        Assert.True(result.Succeeded, result.Message + string.Join("; ", result.Problems));
        Assert.Equal([longName, "v7dir/v7.txt", Fixtures.KoreanName], Util.FilesUnder(output));
        Assert.Equal(koreanPayload, File.ReadAllBytes(Path.Combine(output, Fixtures.KoreanName)));
        Assert.Equal(longPayload, File.ReadAllBytes(Path.Combine(output, longName)));

        // 코드페이지를 강제하면 그것으로 읽는다(여기서는 Latin-1 → 한글 이름이 아니게 됨)
        var forced = Archives.For(archive, new ReadOptions { CodePage = 28591 }).List(archive);
        Assert.True(forced.Result.Succeeded, forced.Result.Message);
        Assert.DoesNotContain(forced.Entries, e => e.Path == Fixtures.KoreanName);

        // 고치기: 다시 쓴 TAR 는 PAX(UTF-8) 이름이 되고 내용은 그대로다
        Assert.True(engine.Rename(archive, "v7dir/v7.txt", "renamed.txt").Succeeded);
        string modified = temp.Combine("mod");
        Assert.True(engine.Extract(archive, modified, new ExtractOptions()).Succeeded);
        Assert.Equal([longName, "v7dir/renamed.txt", Fixtures.KoreanName], Util.FilesUnder(modified));
        Assert.Equal(koreanPayload, File.ReadAllBytes(Path.Combine(modified, Fixtures.KoreanName)));
        AssertNoTemp(temp.Path);
    }
}
