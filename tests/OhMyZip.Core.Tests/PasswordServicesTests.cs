using System.Collections;
using System.Text;

namespace OhMyZip.Core.Tests;

/// <summary>암호 복구·암호 보관함의 합성 데이터 시험.</summary>
public sealed class PasswordServicesTests
{
    [Fact]
    public void OZ_E62_Password_recovery_dictionary_limits_and_cancel()
    {
        var checkedCandidates = new List<string>();
        var options = new PasswordRecoveryOptions(
            Charset: "012",
            MinLength: 1,
            MaxLength: 3,
            MaxAttempts: 100,
            MaxDuration: TimeSpan.FromSeconds(2),
            Dictionary: ["wrong", "12"]);

        PasswordRecoveryResult found = PasswordRecovery.Recover(
            options,
            (candidate, _) =>
            {
                checkedCandidates.Add(candidate);
                return candidate == "12"
                    ? OperationStatus.Succeeded
                    : OperationStatus.PasswordRequired;
            });

        Assert.Equal(OperationStatus.Succeeded, found.Result.Status);
        Assert.Equal("12", found.Password);
        Assert.Equal(2, found.Attempts);
        Assert.Equal(["wrong", "12"], checkedCandidates);

        PasswordRecoveryResult limited = PasswordRecovery.Recover(
            options with { Dictionary = null, MaxAttempts = 2 },
            (_, _) => OperationStatus.PasswordRequired);
        Assert.Equal(OperationStatus.Failed, limited.Result.Status);
        Assert.Contains("한도 안에서 찾지 못함", limited.Result.Message);
        Assert.Equal(2, limited.Attempts);

        var boundedDictionary = new ThrowingDictionary(maxReadableIndex: 2);
        PasswordRecoveryResult boundedDictionaryResult = PasswordRecovery.Recover(
            options with { Dictionary = boundedDictionary, MaxAttempts = 2 },
            (_, _) => OperationStatus.PasswordRequired);
        Assert.Equal(OperationStatus.Failed, boundedDictionaryResult.Result.Status);
        Assert.Equal(2, boundedDictionaryResult.Attempts);
        Assert.Equal(2, boundedDictionary.Reads);

        var progressValues = new List<ProgressInfo>();
        _ = PasswordRecovery.Recover(
            options with { Dictionary = null, MaxAttempts = 2 },
            (_, _) => OperationStatus.PasswordRequired,
            new SyncProgress(progressValues.Add));
        Assert.Equal([new ProgressInfo(1, 2, null), new ProgressInfo(2, 2, null)], progressValues);

        PasswordRecoveryResult timed = PasswordRecovery.Recover(
            options with { Dictionary = null, MaxDuration = TimeSpan.FromMilliseconds(20) },
            (_, token) =>
            {
                while (!token.IsCancellationRequested)
                    Thread.Sleep(1);
                return OperationStatus.PasswordRequired;
            });
        Assert.Equal(OperationStatus.Failed, timed.Result.Status);
        Assert.Contains("한도 안에서 찾지 못함", timed.Result.Message);

        using var cancelled = new CancellationTokenSource();
        PasswordRecoveryResult cancelledResult = PasswordRecovery.Recover(
            options with { Dictionary = null },
            (_, _) =>
            {
                cancelled.Cancel();
                return OperationStatus.PasswordRequired;
            },
            cancel: cancelled.Token);
        Assert.Equal(OperationStatus.Cancelled, cancelledResult.Result.Status);

        Assert.Equal(
            OperationStatus.Failed,
            PasswordRecovery.Recover(
                options with { MaxLength = 9 },
                (_, _) => OperationStatus.PasswordRequired).Result.Status);
        Assert.Equal(
            OperationStatus.Failed,
            PasswordRecovery.Recover(
                options with { MaxAttempts = 10_000_001 },
                (_, _) => OperationStatus.PasswordRequired).Result.Status);
        Assert.Equal(
            OperationStatus.Failed,
            PasswordRecovery.Recover(
                options with { MaxDuration = TimeSpan.FromHours(1).Add(TimeSpan.FromTicks(1)) },
                (_, _) => OperationStatus.PasswordRequired).Result.Status);
        Assert.Equal(
            OperationStatus.Failed,
            PasswordRecovery.Recover(
                options with { Charset = string.Concat(Enumerable.Repeat("a", 101)) },
                (_, _) => OperationStatus.PasswordRequired).Result.Status);

        using var preCancelled = new CancellationTokenSource();
        preCancelled.Cancel();
        PasswordRecoveryResult preCancelledHugeCharset = PasswordRecovery.Recover(
            options with { Charset = new string('0', 2_000_000) },
            (_, _) => OperationStatus.PasswordRequired,
            cancel: preCancelled.Token);
        Assert.Equal(OperationStatus.Cancelled, preCancelledHugeCharset.Result.Status);
        Assert.Equal(0, preCancelledHugeCharset.Attempts);
    }

    [Fact]
    public void OZ_E63_Password_recovery_finds_synthetic_aes_password_and_rejects_plain_zip()
    {
        using var temp = new TempDir();
        string input = Fixtures.CopyTo("input", temp);
        string archive = temp.Combine("aes-password.zip");
        string password = "007";
        var sources = new[]
        {
            Path.Combine(input, "readme.txt"),
            Path.Combine(input, Fixtures.KoreanName),
            Path.Combine(input, "nested"),
        };

        OperationResult created = new ZipEngine().Create(
            archive,
            sources,
            new CreateOptions { Encryption = EncryptionMethod.Aes256, Password = password });
        Assert.True(created.Succeeded, created.Message + string.Join("; ", created.Problems));

        PasswordRecoveryResult recovered = PasswordRecovery.Recover(
            archive,
            new PasswordRecoveryOptions(
                Charset: "0123456789",
                MinLength: 1,
                MaxLength: 3,
                MaxAttempts: 1000,
                MaxDuration: TimeSpan.FromSeconds(30)));
        Assert.Equal(OperationStatus.Succeeded, recovered.Result.Status);
        Assert.Equal(password, recovered.Password);
        Assert.InRange(recovered.Attempts, 1, 1000);

        string plain = temp.Combine("plain.zip");
        OperationResult plainCreated = new ZipEngine().Create(plain, [sources[0]]);
        Assert.True(plainCreated.Succeeded, plainCreated.Message + string.Join("; ", plainCreated.Problems));
        PasswordRecoveryResult notEncrypted = PasswordRecovery.Recover(
            plain,
            new PasswordRecoveryOptions("0123456789", 1, 3, 100, TimeSpan.FromSeconds(2)));
        Assert.Equal(OperationStatus.Failed, notEncrypted.Result.Status);
        Assert.Contains("암호가 걸린 압축파일이 아닙니다", notEncrypted.Result.Message);
        Assert.Null(notEncrypted.Password);
        Assert.Equal(0, notEncrypted.Attempts);

        string corrupt = temp.Combine("not-an-archive.bin");
        File.WriteAllBytes(corrupt, [0x01, 0x02, 0x03]);
        PasswordRecoveryResult probeFailure = PasswordRecovery.Recover(
            corrupt,
            new PasswordRecoveryOptions("0123456789", 1, 3, 100, TimeSpan.FromSeconds(2)));
        Assert.Equal(OperationStatus.Failed, probeFailure.Result.Status);
        Assert.DoesNotContain("암호가 걸린 압축파일이 아닙니다", probeFailure.Result.Message);

        string probeFolder = temp.Combine("probe-folder");
        Directory.CreateDirectory(probeFolder);
        PasswordRecoveryResult probeDeadline = PasswordRecovery.Recover(
            probeFolder,
            new PasswordRecoveryOptions("012", 1, 2, 10, TimeSpan.FromTicks(1)));
        Assert.Equal(OperationStatus.Failed, probeDeadline.Result.Status);
        Assert.Contains("한도 안에서 찾지 못함", probeDeadline.Result.Message);
        Assert.Equal(0, probeDeadline.Attempts);
    }

    [Fact]
    public async Task OZ_E64_Password_vault_crud_roundtrip_and_corruption_preservation()
    {
        using var temp = new TempDir();
        string path = temp.Combine("passwords.dat");
        var vault = new PasswordVault(path);

        Assert.Empty(vault.List());
        string password = "synthetic-vault-password";
        VaultEntry entry = vault.Add("합성 시험", password);
        Assert.Equal("합성 시험", entry.Label);
        Assert.NotEqual(Guid.Empty, Guid.ParseExact(entry.Id, "N"));
        Assert.Equal(password, vault.Reveal(entry.Id));
        Assert.Single(vault.List());

        byte[] encrypted = File.ReadAllBytes(path);
        Assert.False(Contains(encrypted, Encoding.UTF8.GetBytes(password)));
        Assert.False(Contains(encrypted, Encoding.Unicode.GetBytes(password)));

        var second = new PasswordVault(path);
        Assert.Equal(password, second.Reveal(entry.Id));
        Assert.True(second.Remove(entry.Id));
        Assert.False(second.Remove(entry.Id));
        Assert.Null(second.Reveal(entry.Id));

        VaultEntry secondEntry = second.Add("두 번째", "synthetic-second-password");
        second.Clear();
        Assert.Empty(new PasswordVault(path).List());
        Assert.Null(new PasswordVault(path).Reveal(secondEntry.Id));

        const int concurrentAdds = 24;
        Task<VaultEntry>[] additions = Enumerable.Range(0, concurrentAdds)
            .Select(i => Task.Run(() =>
                new PasswordVault(path).Add($"동시-{i}", $"synthetic-concurrent-{i}")))
            .ToArray();
        await Task.WhenAll(additions);
        IReadOnlyList<VaultEntry> afterConcurrentAdds = new PasswordVault(path).List();
        Assert.Equal(concurrentAdds, afterConcurrentAdds.Count);
        Assert.Equal(
            Enumerable.Range(0, concurrentAdds).Select(i => $"동시-{i}").OrderBy(x => x, StringComparer.Ordinal),
            afterConcurrentAdds.Select(e => e.Label).OrderBy(x => x, StringComparer.Ordinal));

        byte[] corrupt = [0x01, 0x02, 0x03, 0x04, 0x05];
        File.WriteAllBytes(path, corrupt);
        Assert.Throws<InvalidDataException>(() => new PasswordVault(path).List());
        Assert.Equal(corrupt, File.ReadAllBytes(path));
    }

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0)
            return true;
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
                return true;
        }
        return false;
    }

    private sealed class ThrowingDictionary(int maxReadableIndex) : IReadOnlyList<string>
    {
        private readonly int _maxReadableIndex = maxReadableIndex;
        private int _reads;

        public int Reads => _reads;

        public int Count => 10_000;

        public string this[int index]
        {
            get
            {
                if (index >= _maxReadableIndex)
                    throw new InvalidOperationException("dictionary read exceeded attempt cap");
                _reads++;
                return "not-the-password";
            }
        }

        public IEnumerator<string> GetEnumerator() =>
            throw new InvalidOperationException("dictionary must not be enumerated wholesale");

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
