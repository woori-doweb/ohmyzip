using System.Buffers.Binary;
using System.IO.Compression;

namespace OhMyZip.Core.Tests;

/// <summary>지역 파일 머리 기반 ZIP 복구 서비스(OZ-E60~E61).</summary>
public sealed class RepairServicesTests
{
    [Fact]
    public void OZ_E60_Repair_recovers_without_central_directory_and_reports_bad_crc()
    {
        using var temp = new TempDir();
        string input = Fixtures.CopyTo("input", temp);
        string source = temp.Combine("source.zip");
        CreateFixtureZip(source, input);
        byte[] originalSource = File.ReadAllBytes(source);

        var damages = new[]
        {
            ("central-truncated", (Func<byte[], byte[]>)TruncateCentralDirectory),
            ("eocd-zeroed", (Func<byte[], byte[]>)ZeroEocd),
            ("entry-corrupt", (Func<byte[], byte[]>)CorruptReadmeData),
        };

        foreach (var (label, damage) in damages)
        {
            string damaged = temp.Combine(label + ".zip");
            byte[] before = damage(originalSource.ToArray());
            File.WriteAllBytes(damaged, before);
            string output = temp.Combine(label + "-repaired.zip");

            RepairReport report = ZipRepair.Repair(damaged, output);

            Assert.Equal(before, File.ReadAllBytes(damaged));
            Assert.True(report.Result.Status == OperationStatus.Succeeded, Detail(report.Result));
            Assert.True(File.Exists(output));

            string extracted = temp.Combine(label + "-out");
            OperationResult extractedResult = new ZipEngine().Extract(output, extracted, new ExtractOptions());
            Assert.True(extractedResult.Succeeded, Detail(extractedResult));

            if (label == "entry-corrupt")
            {
                Assert.Contains(report.Lost, n => n.EndsWith("readme.txt", StringComparison.Ordinal));
                Assert.DoesNotContain("readme.txt", Util.FilesUnder(extracted));
                foreach (var (relative, expectedHash) in Fixtures.ExpectedHashes.Where(kvp => kvp.Key != "readme.txt"))
                {
                    string file = Path.Combine(extracted, relative.Replace('/', Path.DirectorySeparatorChar));
                    Assert.Equal(expectedHash, Util.Sha256(file));
                }
            }
            else
            {
                Assert.Empty(report.Lost);
                string missing = string.Join(
                    ", ",
                    Fixtures.ExpectedHashes.Keys.Where(relative =>
                        !File.Exists(Path.Combine(extracted, relative.Replace('/', Path.DirectorySeparatorChar)))));
                Assert.True(
                    missing.Length == 0,
                    $"{label}: missing={missing}; recovered=[{string.Join(", ", report.Recovered)}]; lost=[{string.Join(", ", report.Lost)}]; result={Detail(report.Result)}");
                Util.AssertExpectedHashes(extracted);
            }
        }

        Assert.Equal(originalSource, File.ReadAllBytes(source));
    }

    [Fact]
    public void OZ_E61_Repair_rejects_existing_output_and_unrecoverable_input_without_artifacts()
    {
        using var temp = new TempDir();
        string input = Fixtures.CopyTo("input", temp);
        string source = temp.Combine("source.zip");
        CreateFixtureZip(source, input);

        string existing = temp.Combine("existing.zip");
        File.WriteAllText(existing, "do not replace");
        RepairReport existingReport = ZipRepair.Repair(source, existing);
        Assert.Equal(OperationStatus.Failed, existingReport.Result.Status);
        Assert.Equal("do not replace", File.ReadAllText(existing));

        string bad = temp.Combine("not-a-zip.bin");
        File.WriteAllBytes(bad, [0x00, 0x01, 0x02, 0x03, 0x04]);
        string badOutput = temp.Combine("bad-output.zip");
        RepairReport badReport = ZipRepair.Repair(bad, badOutput);
        Assert.Equal(OperationStatus.Failed, badReport.Result.Status);
        Assert.False(File.Exists(badOutput));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.repair.tmp"));
    }

    [Fact]
    public void OZ_E61_Repair_cancel_leaves_no_output_and_rejects_zip_slip_names()
    {
        using var temp = new TempDir();
        string archive = temp.Combine("unsafe.zip");
        Util.WriteZip(
            archive,
            ("../escape.txt", Util.Text("outside")),
            ("good.txt", Util.Text("inside")),
            ("/absolute.txt", Util.Text("absolute")));

        string output = temp.Combine("cancelled.zip");
        using var cts = new CancellationTokenSource();
        var report = ZipRepair.Repair(
            archive,
            output,
            new SyncProgress(_ => cts.Cancel()),
            cts.Token);
        Assert.Equal(OperationStatus.Cancelled, report.Result.Status);
        Assert.False(File.Exists(output));

        output = temp.Combine("unsafe-repaired.zip");
        report = ZipRepair.Repair(archive, output);
        Assert.Equal(OperationStatus.Succeeded, report.Result.Status);
        Assert.Contains(report.Lost, n => n.Contains("escape.txt", StringComparison.Ordinal));
        Assert.Contains(report.Lost, n => n.Contains("absolute.txt", StringComparison.Ordinal));
        Assert.DoesNotContain("escape.txt", Util.FilesUnder(Path.GetDirectoryName(output)!));
        Assert.True(File.Exists(output));
    }

    [Fact]
    public void OZ_E61_Repair_does_not_accept_streamed_payload_without_verifiable_crc()
    {
        using var temp = new TempDir();
        string source = temp.Combine("stream-source.zip");
        Util.WriteZip(source, CompressionLevel.Optimal, [("file.txt", Util.Text("streamed payload"), 0)]);
        byte[] bytes = File.ReadAllBytes(source);
        int local = FindLocalByName(bytes, "file.txt");
        Assert.True(local >= 0);
        int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(local + 26));
        int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(local + 28));
        int data = local + 30 + nameLength + extraLength;
        int compressed = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(local + 18)));
        int dataEnd = data + compressed;
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(local + 6));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(local + 6), (ushort)(flags | 0x0008));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(local + 18), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(local + 22), 0);
        string damaged = temp.Combine("streamed-no-descriptor.zip");
        File.WriteAllBytes(damaged, bytes[..dataEnd]);

        string output = temp.Combine("streamed-no-descriptor-repaired.zip");
        RepairReport report = ZipRepair.Repair(damaged, output);

        Assert.Equal(OperationStatus.Failed, report.Result.Status);
        Assert.Contains(report.Lost, n => n.EndsWith("file.txt", StringComparison.Ordinal));
        Assert.Contains(report.Result.Problems, p => p.Contains("설명자", StringComparison.Ordinal) || p.Contains("CRC", StringComparison.Ordinal));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void OZ_E61_Repair_stops_before_unbounded_entry_count()
    {
        using var temp = new TempDir();
        const int count = 200_001;
        string damaged = temp.Combine("many-local-headers.zip");
        using (var stream = File.Create(damaged))
        {
            byte[] header = new byte[31];
            BinaryPrimitives.WriteUInt32LittleEndian(header, 0x04034B50);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), 20);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(26), 1);
            header[30] = (byte)'a';
            for (int i = 0; i < count; i++)
                stream.Write(header);
        }

        string output = temp.Combine("many-local-headers-repaired.zip");
        RepairReport report = ZipRepair.Repair(damaged, output);

        Assert.Equal(OperationStatus.Failed, report.Result.Status);
        Assert.Contains(report.Result.Problems, p => p.Contains("항목 수", StringComparison.Ordinal));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void OZ_E61_Repair_accepts_unsigned_zip64_descriptor_at_eof()
    {
        using var temp = new TempDir();
        byte[] data = Util.Text("unsigned ZIP64 descriptor payload");
        string archive = temp.Combine("unsigned-zip64.zip");
        File.WriteAllBytes(archive, BuildStreamedStoredArchive("zip64.bin", data, zip64: true));

        string output = temp.Combine("unsigned-zip64-repaired.zip");
        RepairReport report = ZipRepair.Repair(archive, output);

        Assert.True(report.Result.Succeeded, Detail(report.Result));
        Assert.Contains("zip64.bin", report.Recovered);
        string extracted = temp.Combine("unsigned-zip64-out");
        OperationResult extractedResult = new ZipEngine().Extract(output, extracted, new ExtractOptions());
        Assert.True(extractedResult.Succeeded, Detail(extractedResult));
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)),
            Util.Sha256(Path.Combine(extracted, "zip64.bin")));
    }

    [Fact]
    public void OZ_E61_Repair_skips_fake_stored_descriptor_until_crc_matches()
    {
        using var temp = new TempDir();
        byte[] data = BuildFakeDescriptorPayload();
        string archive = temp.Combine("fake-descriptor.zip");
        File.WriteAllBytes(archive, BuildStreamedStoredArchive("stored.bin", data, zip64: false));

        string output = temp.Combine("fake-descriptor-repaired.zip");
        RepairReport report = ZipRepair.Repair(archive, output);

        Assert.True(report.Result.Succeeded, Detail(report.Result));
        string extracted = temp.Combine("fake-descriptor-out");
        OperationResult extractedResult = new ZipEngine().Extract(output, extracted, new ExtractOptions());
        Assert.True(extractedResult.Succeeded, Detail(extractedResult));
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)),
            Util.Sha256(Path.Combine(extracted, "stored.bin")));
    }

    [Fact]
    public void OZ_E61_Repair_cancellation_is_honored_during_recovery()
    {
        using var temp = new TempDir();
        byte[] data = Util.Text("cancel me");
        string archive = temp.Combine("cancel-source.zip");
        File.WriteAllBytes(archive, BuildStreamedStoredArchive("cancel.bin", data, zip64: true));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        string output = temp.Combine("cancel-repaired.zip");
        RepairReport report = ZipRepair.Repair(archive, output, cancel: cts.Token);

        Assert.Equal(OperationStatus.Cancelled, report.Result.Status);
        Assert.False(File.Exists(output));
    }

    private static void CreateFixtureZip(string path, string input)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        archive.CreateEntry("nested/", CompressionLevel.NoCompression);
        foreach (string name in new[] { "readme.txt", Fixtures.KoreanName, "nested/notes.txt" })
        {
            ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
            using FileStream source = File.OpenRead(Path.Combine(input, name.Replace('/', Path.DirectorySeparatorChar)));
            using Stream target = entry.Open();
            source.CopyTo(target);
        }
    }

    private static byte[] BuildStreamedStoredArchive(
        string name,
        byte[] data,
        bool zip64)
    {
        byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        using var stream = new MemoryStream();
        byte[] header = new byte[30];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x04034B50);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), 20);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), 0x0808); // UTF-8 + descriptor
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), 0); // stored
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(26), checked((ushort)nameBytes.Length));
        stream.Write(header);
        stream.Write(nameBytes);

        uint crc = Crc(data);
        stream.Write(data);

        if (!zip64)
        {
            byte[] descriptor = new byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor, 0x08074B50);
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(4), crc);
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(8), checked((uint)data.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(12), checked((uint)data.Length));
            stream.Write(descriptor);
        }
        else
        {
            byte[] descriptor = new byte[20];
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor, crc);
            BinaryPrimitives.WriteUInt64LittleEndian(descriptor.AsSpan(4), checked((ulong)data.Length));
            BinaryPrimitives.WriteUInt64LittleEndian(descriptor.AsSpan(12), checked((ulong)data.Length));
            stream.Write(descriptor);
        }
        return stream.ToArray();
    }

    private static byte[] BuildFakeDescriptorPayload()
    {
        byte[] prefix = Util.Text("prefix");
        byte[] suffix = Util.Text("suffix after fake descriptor");
        byte[] fake = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(fake, 0x08074B50);
        BinaryPrimitives.WriteUInt32LittleEndian(fake.AsSpan(4), 0xDEADBEEF);
        BinaryPrimitives.WriteUInt32LittleEndian(fake.AsSpan(8), checked((uint)prefix.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(fake.AsSpan(12), checked((uint)prefix.Length));
        return [.. prefix, .. fake, .. suffix];
    }

    private static uint Crc(byte[] data)
    {
        var crc = new OhMyZip.Core.Crc32();
        crc.Append(data);
        return crc.Value;
    }

    private static byte[] TruncateCentralDirectory(byte[] bytes)
    {
        int central = FindSignature(bytes, 0x02014B50);
        Assert.True(central > 0);
        return bytes[..central];
    }

    private static byte[] ZeroEocd(byte[] bytes)
    {
        int eocd = FindSignature(bytes, 0x06054B50);
        Assert.True(eocd >= 0);
        Array.Clear(bytes, eocd, Math.Min(22, bytes.Length - eocd));
        return bytes;
    }

    private static byte[] CorruptReadmeData(byte[] bytes)
    {
        int local = FindLocalByName(bytes, "readme.txt");
        Assert.True(local >= 0);
        int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(local + 26));
        int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(local + 28));
        int data = local + 30 + nameLength + extraLength;
        int compressed = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(local + 18)));
        Assert.True(compressed > 0);
        bytes[data + Math.Min(compressed - 1, 2)] ^= 0x5A;
        return bytes;
    }

    private static int FindLocalByName(byte[] bytes, string name)
    {
        byte[] expected = System.Text.Encoding.UTF8.GetBytes(name);
        for (int p = 0; p + 30 + expected.Length <= bytes.Length; p++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p)) != 0x04034B50)
                continue;
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(p + 26));
            if (nameLength == expected.Length
                && bytes.AsSpan(p + 30, nameLength).SequenceEqual(expected))
                return p;
        }
        return -1;
    }

    private static int FindSignature(byte[] bytes, uint signature)
    {
        for (int p = 0; p + 4 <= bytes.Length; p++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p)) == signature)
                return p;
        }
        return -1;
    }

    private static string Detail(OperationResult result) =>
        result.Message + " | " + string.Join("; ", result.Problems);
}
