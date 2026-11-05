using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OhMyZip.Core.Resources;

namespace OhMyZip.Core;

/// <summary>DPAPI(CurrentUser)로 암호를 보관하는 한 파일짜리 보관함.</summary>
public sealed class PasswordVault
{
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(10);
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("ohmyZip.PasswordVault.CurrentUser.v1");

    private readonly string _storePath;
    private readonly string _mutexName;

    public PasswordVault(string storePath)
    {
        if (string.IsNullOrWhiteSpace(storePath))
            throw new ArgumentException(ServiceText.Vault_PathEmpty, nameof(storePath));
        try
        {
            _storePath = Path.GetFullPath(storePath);
            _mutexName = MutexName(_storePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException(ServiceText.Vault_PathInvalid, nameof(storePath), ex);
        }
    }

    /// <summary>OHMYZIP_DATA_DIR 또는 사용자 AppData 아래의 기본 보관 파일.</summary>
    public static string DefaultPath
    {
        get
        {
            string? root = Environment.GetEnvironmentVariable("OHMYZIP_DATA_DIR");
            if (string.IsNullOrWhiteSpace(root))
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (string.IsNullOrWhiteSpace(appData))
                    throw new InvalidOperationException(ServiceText.Vault_AppDataMissing);
                root = Path.Combine(appData, "ohmyZip");
            }
            return Path.Combine(root, "passwords.dat");
        }
    }

    public IReadOnlyList<VaultEntry> List()
    {
        using var gate = EnterLock();
        return LoadCore().Select(ToPublic).ToArray();
    }

    public VaultEntry Add(string label, string password)
    {
        if (string.IsNullOrWhiteSpace(label))
            throw new ArgumentException(ServiceText.Vault_LabelEmpty, nameof(label));
        if (password is null)
            throw new ArgumentNullException(nameof(password));

        using var gate = EnterLock();
        List<StoredEntry> entries = LoadCore();
        var entry = new StoredEntry(Guid.NewGuid().ToString("N"), label, password, DateTimeOffset.UtcNow);
        entries.Add(entry);
        SaveCore(entries);
        return ToPublic(entry);
    }

    public string? Reveal(string id)
    {
        using var gate = EnterLock();
        List<StoredEntry> entries = LoadCore();
        if (string.IsNullOrEmpty(id))
            return null;
        StoredEntry? entry = entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));
        return entry?.Password;
    }

    public bool Remove(string id)
    {
        using var gate = EnterLock();
        List<StoredEntry> entries = LoadCore();
        if (string.IsNullOrEmpty(id))
            return false;
        int index = entries.FindIndex(e => string.Equals(e.Id, id, StringComparison.Ordinal));
        if (index < 0)
            return false;
        entries.RemoveAt(index);
        SaveCore(entries);
        return true;
    }

    public void Clear()
    {
        using var gate = EnterLock();
        if (!File.Exists(_storePath))
            return;
        // 먼저 읽어야 손상된 파일을 정상적인 빈 보관함으로 덮어쓰지 않는다.
        _ = LoadCore();
        SaveCore([]);
    }

    private List<StoredEntry> LoadCore()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(ServiceText.Vault_WindowsOnly);
        if (!File.Exists(_storePath))
            return [];

        byte[] encrypted;
        encrypted = File.ReadAllBytes(_storePath);

        if (encrypted.Length == 0)
            throw Corrupt();

        byte[]? plain = null;
        try
        {
            plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            if (plain.Length == 0)
                throw Corrupt();
            var document = JsonSerializer.Deserialize<StoredDocument>(plain, JsonOptions);
            if (document?.Entries is null)
                throw Corrupt();
            return ValidateEntries(document.Entries);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or NotSupportedException or ArgumentException)
        {
            throw Corrupt();
        }
        finally
        {
            if (plain is not null)
                CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    private void SaveCore(IReadOnlyList<StoredEntry> entries)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(ServiceText.Vault_WindowsOnly);
        byte[]? plain = null;
        byte[]? encrypted = null;
        string? temp = null;
        try
        {
            plain = JsonSerializer.SerializeToUtf8Bytes(
                new StoredDocument(entries.ToList()),
                JsonOptions);
            encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            string directory = Path.GetDirectoryName(_storePath)!;
            Directory.CreateDirectory(directory);
            temp = AtomicFile.TempSibling(_storePath, ".vault.tmp");
            using (var stream = new FileStream(
                temp,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                stream.Write(encrypted);
                stream.Flush(flushToDisk: true);
            }

            // 임시 파일과 대상은 같은 폴더이므로 마지막 이동만 원자적으로 수행한다.
            AtomicFile.Commit(temp, _storePath, overwrite: true);
            temp = null;
        }
        finally
        {
            if (plain is not null)
                CryptographicOperations.ZeroMemory(plain);
            if (encrypted is not null)
                CryptographicOperations.ZeroMemory(encrypted);
            if (temp is not null)
                AtomicFile.TryDelete(temp);
        }
    }

    private static List<StoredEntry> ValidateEntries(List<StoredEntry>? entries)
    {
        if (entries is null)
            throw Corrupt();

        var result = new List<StoredEntry>(entries.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (StoredEntry? entry in entries)
        {
            if (entry is null
                || string.IsNullOrWhiteSpace(entry.Id)
                || !Guid.TryParseExact(entry.Id, "N", out _)
                || !ids.Add(entry.Id)
                || string.IsNullOrWhiteSpace(entry.Label)
                || entry.Password is null
                || entry.Created == default)
            {
                throw Corrupt();
            }
            result.Add(entry);
        }
        return result;
    }

    private static VaultEntry ToPublic(StoredEntry entry) =>
        new(entry.Id, entry.Label, entry.Created);

    private static InvalidDataException Corrupt() =>
        new(ServiceText.Vault_Corrupt);

    private VaultMutex EnterLock()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(ServiceText.Vault_WindowsOnly);
        Mutex mutex = new(initiallyOwned: false, _mutexName);
        bool acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(LockWait);
            }
            catch (AbandonedMutexException)
            {
                // AbandonedMutexException도 뮤텍스 소유권은 이 호출에 넘어온다.
                // 각 작업이 잠금 안에서 LoadCore를 수행해 파일을 다시 검증한다.
                acquired = true;
            }

            if (!acquired)
                throw new TimeoutException(ServiceText.Vault_LockTimeout);
            return new VaultMutex(mutex);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    private static string MutexName(string path)
    {
        string normalized = OperatingSystem.IsWindows()
            ? path.ToUpperInvariant()
            : path;
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return $@"Local\ohmyZip.PasswordVault.{Convert.ToHexString(hash)}";
    }

    private sealed class VaultMutex(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                mutex.ReleaseMutex();
            }
            finally
            {
                mutex.Dispose();
            }
        }
    }

    private sealed record StoredDocument(List<StoredEntry> Entries);

    private sealed record StoredEntry(
        string Id,
        string Label,
        string Password,
        DateTimeOffset Created);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        MaxDepth = 16,
    };
}

/// <summary>암호 자체는 저장하지 않고 메타데이터만 앱에 돌려준다.</summary>
public sealed record VaultEntry(string Id, string Label, DateTimeOffset Created);
