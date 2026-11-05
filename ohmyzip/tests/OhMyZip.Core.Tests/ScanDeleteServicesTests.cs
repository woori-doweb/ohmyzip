using System.Text;
using System.Runtime.InteropServices;
using Xunit.Abstractions;

namespace OhMyZip.Core.Tests;

/// <summary>AMSI 검사와 파일 완전 삭제 서비스 시험. EICAR 시험은 명시적으로 opt-in 해야 한다.</summary>
public sealed class ScanDeleteServicesTests
{
    private const string EicarOptInVariable = "OHMYZIP_RUN_EICAR";
    private const string ArchivePassword = "oz-e67-347";
    private readonly ITestOutputHelper output;

    public ScanDeleteServicesTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public void OZ_E65_Amsi_clean_reports_clean_or_unavailable_honestly()
    {
        ScanReport report = MalwareScanner.ScanBuffer(Encoding.UTF8.GetBytes("ohmyZip ordinary clean bytes"), "clean.txt");
        WriteLiveReport("E65 clean", report);
        Assert.Contains(report.Overall, new[] { ScanVerdict.Clean, ScanVerdict.NotScanned });
        if (report.Overall == ScanVerdict.NotScanned)
            Assert.Contains("검사할 수 없음 — 안전하다는 뜻이 아님", report.Message);
        else
            Assert.Equal(ScanVerdict.Clean, report.Overall);
    }

    [Fact]
    public void OZ_E65_Amsi_admin_blocked_result_is_not_clean()
    {
        ScanItem blocked = MalwareScanner.MapAmsiResult(0x4000, "blocked.txt");
        Assert.Equal(ScanVerdict.NotScanned, blocked.Verdict);
        Assert.Contains("관리자 차단", blocked.Detail);

        ScanItem detected = MalwareScanner.MapAmsiResult(32768, "detected.txt");
        Assert.Equal(ScanVerdict.Detected, detected.Verdict);
    }

    [EicarFact]
    public void OZ_E65_Amsi_eicar_is_opt_in_and_detected_when_live()
    {
        ScanReport availability = MalwareScanner.ScanBuffer(Encoding.UTF8.GetBytes("ohmyZip ordinary clean bytes"), "availability.txt");
        WriteLiveReport("E65 availability", availability);
        Assert.Equal(ScanVerdict.Clean, availability.Overall);

        ScanReport report = MalwareScanner.ScanBuffer(EicarBytes(), "eicar-test.txt");
        WriteLiveReport("E65 EICAR", report);
        Assert.Equal(ScanVerdict.Detected, report.Overall);
        Assert.Contains("1", report.Message);
    }

    [Fact]
    public void OZ_E66_ScanArchive_plain_zip_cleanup_and_cancel_are_honest()
    {
        using var temp = new TempDir();
        string input = Fixtures.CopyTo("input", temp);
        string zip = temp.Combine("plain.zip");
        var created = new ZipEngine().Create(zip,
            [
                Path.Combine(input, "readme.txt"),
                Path.Combine(input, Fixtures.KoreanName),
                Path.Combine(input, "nested"),
            ]);
        Assert.True(created.Succeeded, created.Message + " | " + string.Join("; ", created.Problems));

        byte[] archiveBeforeScan = File.ReadAllBytes(zip);
        var before = ScanTempFolders();
        ScanReport report = MalwareScanner.ScanArchive(zip);
        WriteLiveReport("E66 plain archive", report);
        Assert.Equal(archiveBeforeScan, File.ReadAllBytes(zip));
        Assert.Empty(ScanTempFolders().Except(before, StringComparer.OrdinalIgnoreCase));
        ScanReport baseline = MalwareScanner.ScanBuffer(Encoding.UTF8.GetBytes("ohmyZip ordinary clean bytes"), "baseline.txt");
        if (baseline.Overall == ScanVerdict.NotScanned)
        {
            Assert.Equal(ScanVerdict.NotScanned, report.Overall);
            Assert.Contains("검사할 수 없음 — 안전하다는 뜻이 아님", report.Message);
        }
        else
        {
            Assert.Equal(ScanVerdict.Clean, baseline.Overall);
            Assert.Equal(ScanVerdict.Clean, report.Overall);
        }

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        ScanReport cancelReport = MalwareScanner.ScanArchive(zip, cancel: cancelled.Token);
        WriteLiveReport("E66 cancelled archive", cancelReport);
        Assert.Equal(ScanVerdict.Cancelled, cancelReport.Overall);
    }

    [Fact]
    public void OZ_E66_ScanArchive_aes_without_password_is_not_scanned()
    {
        using var temp = new TempDir();
        string input = Fixtures.CopyTo("input", temp);
        string zip = temp.Combine("encrypted.zip");
        var created = new ZipEngine().Create(zip,
            [
                Path.Combine(input, "readme.txt"),
                Path.Combine(input, Fixtures.KoreanName),
            ],
            new CreateOptions { Encryption = EncryptionMethod.Aes256, Password = ArchivePassword });
        Assert.True(created.Succeeded, created.Message + " | " + string.Join("; ", created.Problems));

        byte[] archiveBeforeScan = File.ReadAllBytes(zip);
        var before = ScanTempFolders();
        ScanReport report = MalwareScanner.ScanArchive(zip);
        WriteLiveReport("E66 encrypted archive without password", report);
        Assert.Equal(archiveBeforeScan, File.ReadAllBytes(zip));
        Assert.Empty(ScanTempFolders().Except(before, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(ScanVerdict.NotScanned, report.Overall);
        Assert.Contains("암호가 필요", string.Join(" ", report.Items.Select(i => i.Detail).Append(report.Message)));
        Assert.Contains("검사할 수 없음 — 안전하다는 뜻이 아님", report.Message);
    }

    [Fact]
    public void OZ_E66_ScanArchive_rejects_duplicate_normalized_paths_and_preserves_source()
    {
        using var temp = new TempDir();
        string zip = temp.Combine("duplicate.zip");
        Util.WriteZip(zip,
            ("same.txt", Util.Text("first")),
            ("same.txt", Util.Text("second")));
        byte[] before = File.ReadAllBytes(zip);
        var tempBefore = ScanTempFolders();

        ScanReport report = MalwareScanner.ScanArchive(zip);
        WriteLiveReport("E66 duplicate paths", report);
        Assert.Equal(ScanVerdict.NotScanned, report.Overall);
        Assert.Contains("중복", report.Message + " " + string.Join(" ", report.Items.Select(i => i.Detail)));
        Assert.Equal(before, File.ReadAllBytes(zip));
        Assert.Empty(ScanTempFolders().Except(tempBefore, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void OZ_E66_ScanArchive_rejects_path_escape_without_writing_outside_snapshot()
    {
        using var temp = new TempDir();
        string zip = temp.Combine("escape.zip");
        Util.WriteZip(zip, ("../outside.txt", Util.Text("must not escape")));
        byte[] before = File.ReadAllBytes(zip);
        var tempBefore = ScanTempFolders();

        ScanReport report = MalwareScanner.ScanArchive(zip);
        WriteLiveReport("E66 path containment", report);
        Assert.Equal(ScanVerdict.NotScanned, report.Overall);
        Assert.DoesNotContain("outside.txt", Directory.GetFiles(temp.Path, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetFileName(f) ?? ""));
        Assert.Equal(before, File.ReadAllBytes(zip));
        Assert.Empty(ScanTempFolders().Except(tempBefore, StringComparer.OrdinalIgnoreCase));
    }

    [EicarFact]
    public void OZ_E67_ScanArchive_eicar_aes_is_opt_in_and_detected_with_password()
    {
        ScanReport availability = MalwareScanner.ScanBuffer(Encoding.UTF8.GetBytes("ohmyZip ordinary clean bytes"), "availability.txt");
        WriteLiveReport("E67 availability", availability);
        Assert.Equal(ScanVerdict.Clean, availability.Overall);

        using var temp = new TempDir();
        string zip = temp.Combine("eicar-aes.zip");
        CreateAesArchiveFromMemory(zip, EicarBytes(), ArchivePassword);
        Assert.True(File.Exists(zip), "E67 cannot verify detection: synthetic archive is unavailable.");

        ScanReport report = MalwareScanner.ScanArchive(zip, new ReadOptions { Password = ArchivePassword });
        WriteLiveReport("E67 EICAR archive", report);
        Assert.True(File.Exists(zip), "E67 cannot verify source preservation: synthetic archive is unavailable.");
        Assert.Equal(ScanVerdict.Detected, report.Overall);
        Assert.Contains("1", report.Message);
    }

    [Fact]
    public void OZ_E68_SecureDelete_deletes_files_and_refuses_directory_readonly()
    {
        using var temp = new TempDir();
        byte[] original = Encoding.UTF8.GetBytes("synthetic content for secure deletion");
        string target = temp.Combine("delete-me.bin");
        File.WriteAllBytes(target, original);

        OperationResult deleted = SecureDelete.Shred(target);
        Assert.Equal(OperationStatus.Succeeded, deleted.Status);
        Assert.Contains("SSD 에서는 완전 삭제를 보장할 수 없음", deleted.Message);
        Assert.False(File.Exists(target));
        foreach (string file in Directory.GetFiles(temp.Path, "*", SearchOption.AllDirectories))
            Assert.False(original.SequenceEqual(File.ReadAllBytes(file)));

        string directory = temp.Combine("directory");
        Directory.CreateDirectory(directory);
        OperationResult directoryResult = SecureDelete.Shred(directory);
        Assert.Equal(OperationStatus.Failed, directoryResult.Status);
        Assert.True(Directory.Exists(directory));

        string readOnly = temp.Combine("read-only.bin");
        File.WriteAllBytes(readOnly, original);
        File.SetAttributes(readOnly, File.GetAttributes(readOnly) | FileAttributes.ReadOnly);
        try
        {
            OperationResult readOnlyResult = SecureDelete.Shred(readOnly);
            Assert.Equal(OperationStatus.Failed, readOnlyResult.Status);
            Assert.True(File.Exists(readOnly));
            Assert.Equal(original, File.ReadAllBytes(readOnly));
        }
        finally
        {
            if (File.Exists(readOnly))
                File.SetAttributes(readOnly, FileAttributes.Normal);
        }

        string reparseTarget = temp.Combine("reparse-target.bin");
        string reparsePath = temp.Combine("reparse.bin");
        File.WriteAllBytes(reparseTarget, original);
        try
        {
            File.CreateSymbolicLink(reparsePath, reparseTarget);
            OperationResult reparseResult = SecureDelete.Shred(reparsePath);
            Assert.Equal(OperationStatus.Failed, reparseResult.Status);
            Assert.True(File.Exists(reparseTarget));
            Assert.Equal(original, File.ReadAllBytes(reparseTarget));
        }
        catch (IOException)
        {
            output.WriteLine("E68 reparse refusal not run: symbolic-link creation is unavailable.");
        }
        catch (UnauthorizedAccessException)
        {
            output.WriteLine("E68 reparse refusal not run: symbolic-link creation requires an unavailable privilege.");
        }
        catch (PlatformNotSupportedException)
        {
            output.WriteLine("E68 reparse refusal not run: symbolic-link creation is not supported.");
        }
        finally
        {
            if (File.Exists(reparsePath))
                File.Delete(reparsePath);
        }

        string cancelledPath = temp.Combine("cancelled.bin");
        File.WriteAllBytes(cancelledPath, original);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        OperationResult cancelled = SecureDelete.Shred(cancelledPath, cancellation.Token);
        Assert.Equal(OperationStatus.Cancelled, cancelled.Status);
        Assert.Equal(original, File.ReadAllBytes(cancelledPath));
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode,
        SetLastError = true, ExactSpelling = true)]
    private static extern bool CreateHardLink(string link, string existing, IntPtr security);

    [Fact]
    public void OZ_E68_SecureDelete_refuses_hardlinks_without_changing_either_name()
    {
        using var temp = new TempDir();
        string original = temp.Combine("original.bin");
        string alias = temp.Combine("alias.bin");
        byte[] content = Encoding.UTF8.GetBytes("synthetic hardlink preservation fixture");
        File.WriteAllBytes(original, content);
        Assert.True(CreateHardLink(alias, original, IntPtr.Zero),
            $"CreateHardLink failed: {Marshal.GetLastWin32Error()}");

        OperationResult result = SecureDelete.Shred(alias);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Contains("하드 링크", result.Message);
        Assert.Equal(content, File.ReadAllBytes(original));
        Assert.Equal(content, File.ReadAllBytes(alias));
    }

    private static byte[] EicarBytes()
    {
        // Deliberately assembled at runtime: the standard test string is not present as one source literal.
        string value = string.Concat(
            "X5O!P%@AP[4\\PZX54(P^)",
            "7CC)7}$EICAR-STANDARD-",
            "ANTIVIRUS-TEST-FILE!$H+H*");
        return Encoding.ASCII.GetBytes(value);
    }

    private static void CreateAesArchiveFromMemory(string path, byte[] content, string password)
    {
        using var output = ZipVolumeWriter.Create(path, null);
        var writer = new ZipWriter(output);
        using var source = new MemoryStream(content, writable: false);
        writer.AddFile("eicar-test.txt", source, DateTime.Now, 5, password, null, default);
        writer.Finish([]);
        output.Commit(overwrite: false);
    }

    private static HashSet<string> ScanTempFolders() =>
        Directory.GetDirectories(Path.GetTempPath(), MalwareScanner.TempFolderPrefix + "*", SearchOption.TopDirectoryOnly)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private void WriteLiveReport(string label, ScanReport report)
    {
        output.WriteLine($"{label}: verdict={report.Overall}; engine={report.Engine}; message={report.Message}");
        foreach (ScanItem item in report.Items)
            output.WriteLine($"{label}: item path={item.Path}; verdict={item.Verdict}; detail={item.Detail ?? "(none)"}");
    }

    private sealed class EicarFactAttribute : FactAttribute
    {
        public EicarFactAttribute()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable(EicarOptInVariable), "1", StringComparison.Ordinal))
            {
                Skip = $"EICAR test not run; set {EicarOptInVariable}=1 to opt in and permit the standard antivirus test history entry.";
                return;
            }

            try
            {
                ScanReport availability = MalwareScanner.ScanBuffer(Encoding.UTF8.GetBytes("ohmyZip ordinary clean bytes"), "discovery.txt");
                if (availability.Overall == ScanVerdict.NotScanned)
                    Skip = $"EICAR test not run: AMSI unavailable ({availability.Message}).";
            }
            catch (Exception ex)
            {
                Skip = $"EICAR test not run: AMSI availability could not be established ({ex.GetType().Name}).";
            }
        }
    }
}
