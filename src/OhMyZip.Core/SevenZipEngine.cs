using System.Buffers.Binary;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace OhMyZip.Core;

/// <summary>암호화된 7Z·RAR 를 풀다가 CRC 불일치·복호 오류가 났다(틀린 암호 또는 손상). 세션 전체를 되돌린다.</summary>
internal sealed class WrongPasswordException() : Exception("암호가 올바르지 않습니다");

/// <summary>
/// 암호화 항목용(7Z·RAR): 읽는 동안 CRC·크기를 세고 끝에서 비교한다. 불일치·복호 오류는 <see cref="WrongPasswordException"/>
/// (암호화되지 않은 항목이면 InvalidDataException).
/// </summary>
internal sealed class VerifyingStream(Func<Stream> open, uint? expectedCrc, long expectedSize, bool encrypted) : Stream
{
    private readonly Crc32 _crc = new();
    private Stream? _inner;
    private long _size;

    private Exception Failure(string detail) =>
        encrypted ? new WrongPasswordException() : new InvalidDataException(detail);

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int n;
        try
        {
            _inner ??= open();
            n = _inner.Read(buffer);
        }
        catch (Exception ex) when (encrypted && ex is not OperationCanceledException)
        {
            throw new WrongPasswordException();
        }
        if (n > 0)
        {
            _crc.Append(buffer[..n]);
            _size += n;
            return n;
        }
        if (_size != expectedSize)
            throw Failure($"크기 불일치 (기대 {expectedSize}, 실제 {_size})");
        if (expectedCrc is { } c && c != _crc.Value)
            throw Failure($"CRC 불일치 (기대 {c:X8}, 실제 {_crc.Value:X8})");
        return 0;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => expectedSize;

    public override long Position
    {
        get => _size;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>
    /// 닫을 때 남은 데이터를 건너뛰며 복호 오류가 날 수 있는데, 그 예외가 이미 보고 중인 틀린 암호·CRC 오류를 가리지 않게 삼킨다
    /// (데이터 검증은 끝까지 읽는 Read 에서 끝났다).
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
            new QuietDisposer(_inner).Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>읽기 전용 자원을 닫다가 난 예외를 삼킨다(먼저 난 예외·결과를 가리지 않게).</summary>
internal sealed class QuietDisposer(IDisposable? inner) : IDisposable
{
    public void Dispose()
    {
        try
        {
            inner?.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
    }
}

/// <summary>
/// 7Z 엔진. 읽기(목록·검사·해제, AES 복호)는 SharpCompress, 쓰기는 <see cref="SevenZipWriter"/>.
/// 분할(name.7z.001…)은 읽기·만들기를 지원하고, 고치기(추가·삭제·이름 바꾸기)는 전체를 임시 사본에 다시 써서 바꿔 넣는다.
/// </summary>
public sealed class SevenZipEngine(ReadOptions? read = null) : IArchiveEngine
{
    private readonly ReadOptions _read = read ?? new ReadOptions();

    private const int BufferSize = 81920;
    private const int FileAttributeReparsePoint = 0x400;
    private const int FileAttributeUnixExtension = 0x8000;
    private const string PasswordMissing = "암호가 필요합니다";
    private const string PasswordWrong = "암호가 올바르지 않습니다";
    /// <summary>고치기(다시 쓰기) 때 쓰는 압축 수준.</summary>
    private const int ModifyLevel = 5;
    private static readonly byte[] SevenZipSignature = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];

    public FormatCapabilities Capabilities { get; } = new(Read: true, Test: true, Extract: true, Create: true, Modify: true, Encrypt: true, Split: true, Comment: false);

    private string? Password => string.IsNullOrEmpty(_read.Password) ? null : _read.Password;

    /// <summary>링크 항목: LinkTarget, Windows 재분석 지점 속성, 또는 유닉스 확장 속성의 S_IFLNK.</summary>
    private static bool IsLink(IEntry e)
    {
        if (!string.IsNullOrEmpty(e.LinkTarget))
            return true;
        if (e.Attrib is not { } attrib)
            return false;
        if ((attrib & FileAttributeReparsePoint) != 0)
            return true;
        return (attrib & FileAttributeUnixExtension) != 0 && ((attrib >>> 16) & 0xF000) == 0xA000;
    }

    /// <summary>7Z 는 CRC 가 없을 수 있다(0). 크기 0 이 아닌데 0 이면 검증하지 않는다.</summary>
    private static uint? ExpectedCrc(IEntry e) => e.IsDirectory || (e.Crc == 0 && e.Size != 0) ? null : (uint)e.Crc;

    private static DateTimeOffset? Modified(IEntry e)
    {
        if (e.LastModifiedTime is not { } t)
            return null;
        try
        {
            return new DateTimeOffset(t);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static DateTime? ModifiedUtc(IEntry e) => Modified(e)?.UtcDateTime;

    /// <summary>
    /// 압축 방식 이름. 빈 파일은 폴더(코더)가 없고, 일부 암호화 압축 파일은 코더 구성을 SharpCompress 가 방식으로 옮기지 못해
    /// CompressionType 이 예외를 던진다(실측: 다른 도구로 만든 AES 7Z 에서 InvalidFormatException) → 그때는 방식 없음(null).
    /// </summary>
    private static string? Method(SevenZipArchiveEntry e)
    {
        if (e.IsDirectory || e.Size == 0)
            return null;
        try
        {
            return e.CompressionType.ToString();
        }
        catch (Exception ex) when (ex is SharpCompressException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// 암호화된 solid 폴더에서는 건너뛰는 항목도 풀면서 지나가므로, 다음 항목으로 넘어갈 때 난 복호 오류도 틀린 암호로 본다.
    /// </summary>
    private static bool MoveNext(IReader reader, bool encrypted)
    {
        try
        {
            return reader.MoveToNextEntry();
        }
        catch (Exception ex) when (encrypted && ex is not (OperationCanceledException or WrongPasswordException))
        {
            throw new WrongPasswordException();
        }
    }

    /// <summary>
    /// SharpCompress 1.0.0 은 데이터 스트림이 없는 빈 파일(폴더에 속하지 않음)도 IsEncrypted=true 로 보고한다.
    /// 빈 항목은 암호화할 데이터가 없으므로 크기 0·폴더는 암호화 아님으로 본다.
    /// </summary>
    private static bool IsEncrypted(SevenZipArchiveEntry e) => e.IsEncrypted && !e.IsDirectory && e.Size > 0;

    /// <summary>SharpCompress 가 열기에 실패하면 경로로 연 파일 핸들을 놓치므로, 스트림을 직접 열고 직접 닫는다.</summary>
    private static FileStream OpenFile(string archivePath) =>
        new(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);

    // ---------- 열기 ----------

    /// <summary>열린 압축 파일. 분할이면 Stream 은 조각을 이은 <see cref="VolumeReadStream"/>.</summary>
    private sealed class Opened(Stream stream, SevenZipArchive archive, List<SevenZipArchiveEntry> entries, bool headerEncrypted) : IDisposable
    {
        public SevenZipArchive Archive { get; } = archive;
        public List<SevenZipArchiveEntry> Entries { get; } = entries;
        public bool HeaderEncrypted { get; } = headerEncrypted;
        public bool Encrypted => HeaderEncrypted || Entries.Any(IsEncrypted);

        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Archive.Dispose();
            stream.Dispose();
        }
    }

    /// <summary>
    /// 시작 헤더만 읽어 필요한 전체 길이와 "헤더 암호화"(kEncodedHeader 의 코더에 7zAES 가 있음) 여부를 본다.
    /// 7Z 시작 헤더가 아니면 null.
    /// </summary>
    private static (long Required, bool HeaderEncrypted)? Probe(Stream s)
    {
        Span<byte> start = stackalloc byte[32];
        if (s.ReadAtLeast(start, 32, throwOnEndOfStream: false) < 32 || !start[..6].SequenceEqual(SevenZipSignature))
            return null;
        ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(start[12..]);
        ulong size = BinaryPrimitives.ReadUInt64LittleEndian(start[20..]);
        if (offset > long.MaxValue / 4 || size > long.MaxValue / 4)
            return null;
        long required = 32 + (long)offset + (long)size;
        bool encrypted = false;
        if (size > 0 && required <= s.Length)
        {
            s.Position = 32 + (long)offset;
            byte[] head = new byte[(int)Math.Min(size, 4096)];
            int n = s.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            encrypted = n > 0 && head[0] == 0x17 && head.AsSpan(0, n).IndexOf(SevenZipAes.CoderId) >= 0;
        }
        return (required, encrypted);
    }

    private OperationResult? TryOpen(string archivePath, out Opened opened)
    {
        opened = null!;
        Stream stream;
        List<string>? parts = null;
        try
        {
            if (Volumes.IsFirstPart(archivePath))
            {
                parts = Volumes.ConsecutiveParts(Path.GetFullPath(archivePath));
                if (parts.Count == 0)
                    return OperationResult.Fail("7Z 압축 파일을 열 수 없습니다", "첫 조각이 없습니다");
                stream = new VolumeReadStream(parts);
            }
            else
            {
                stream = OpenFile(archivePath);
            }
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("7Z 압축 파일을 열 수 없습니다", ex.Message);
        }

        try
        {
            bool headerEncrypted = false;
            if (Probe(stream) is { } probe)
            {
                if (parts is not null && stream.Length < probe.Required)
                {
                    string missing = Path.GetFileName(Volumes.PartPath(parts[0][..^4], parts.Count + 1));
                    stream.Dispose();
                    return OperationResult.Fail($"분할 조각이 없습니다: {missing}", missing);
                }
                headerEncrypted = probe.HeaderEncrypted;
            }
            if (headerEncrypted && Password is null)
            {
                stream.Dispose();
                return OperationResult.NeedPassword($"{PasswordMissing} (파일 목록이 암호화된 7Z)");
            }
            stream.Position = 0;
            SevenZipArchive? archive = null;
            try
            {
                archive = SevenZipArchive.Open(stream, new ReaderOptions { Password = Password, LeaveStreamOpen = true });
                var entries = archive.Entries.Where(e => !e.IsAnti).ToList();
                opened = new Opened(stream, archive, entries, headerEncrypted);
                return null;
            }
            catch (Exception ex)
            {
                archive?.Dispose();
                stream.Dispose();
                return headerEncrypted
                    ? OperationResult.NeedPassword(PasswordWrong)
                    : OperationResult.Fail("7Z 압축 파일을 열 수 없습니다", ex.Message);
            }
        }
        catch (Exception ex)
        {
            stream.Dispose();
            return OperationResult.Fail("7Z 압축 파일을 열 수 없습니다", ex.Message);
        }
    }

    // ---------- 읽기 ----------

    public ListResult List(string archivePath, CancellationToken cancel = default)
    {
        if (TryOpen(archivePath, out var opened) is { } error)
            return new ListResult(error, []);
        try
        {
            using (opened)
            {
                var entries = new List<ArchiveEntry>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var e in opened.Entries)
                {
                    cancel.ThrowIfCancellationRequested();
                    string path = SafePaths.ToDisplayPath(e.Key ?? "");
                    if (path.Length == 0)
                        continue;
                    if (seen.Add(path))
                    {
                        entries.Add(new ArchiveEntry(path, e.IsDirectory, e.IsDirectory ? 0 : e.Size, e.IsDirectory ? 0 : e.CompressedSize,
                            Modified(e), ExpectedCrc(e), Method(e), IsEncrypted(e) || (opened.HeaderEncrypted && !e.IsDirectory)));
                    }
                }
                // DEF-02: 목록은 압축 파일에 기록된 항목만 돌려준다(경로로만 생기는 폴더는 앱이 트리에서 만든다).
                return new ListResult(OperationResult.Ok($"항목 {entries.Count}개"), entries);
            }
        }
        catch (OperationCanceledException)
        {
            return new ListResult(OperationResult.Cancelled(), []);
        }
        catch (Exception ex)
        {
            return new ListResult(OperationResult.Fail("7Z 압축 파일을 읽을 수 없습니다", ex.Message), []);
        }
    }

    public OperationResult Test(string archivePath, IProgress<ProgressInfo>? progress = null, CancellationToken cancel = default)
    {
        if (TryOpen(archivePath, out var opened) is { } error)
            return error;
        try
        {
            using (opened)
            {
                bool encrypted = opened.Encrypted;
                if (encrypted && Password is null)
                    return OperationResult.NeedPassword(PasswordMissing);
                long total = opened.Entries.Where(e => !e.IsDirectory).Sum(e => e.Size);
                long processed = 0;
                int files = 0;
                var problems = new List<string>();
                byte[] buffer = new byte[BufferSize];
                var reader = opened.Archive.ExtractAllEntries();
                using var quiet = new QuietDisposer(reader);
                while (MoveNext(reader, encrypted))
                {
                    cancel.ThrowIfCancellationRequested();
                    var e = reader.Entry;
                    if (e.IsDirectory || e is SevenZipArchiveEntry { IsAnti: true })
                        continue;
                    files++;
                    string path = SafePaths.ToDisplayPath(e.Key ?? "");
                    try
                    {
                        using var s = new VerifyingStream(reader.OpenEntryStream, ExpectedCrc(e), e.Size, encrypted);
                        int n;
                        while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            cancel.ThrowIfCancellationRequested();
                            processed += n;
                            progress?.Report(new ProgressInfo(processed, total, path));
                        }
                    }
                    catch (Exception ex) when (ex is SharpCompressException or InvalidDataException or IOException or NotSupportedException)
                    {
                        problems.Add($"{path}: {ex.Message}");
                    }
                }
                return problems.Count == 0
                    ? OperationResult.Ok($"파일 {files}개 이상 없음")
                    : new OperationResult(OperationStatus.Failed, $"손상된 항목 {problems.Count}개", problems);
            }
        }
        catch (WrongPasswordException)
        {
            return OperationResult.NeedPassword($"{PasswordWrong} (또는 압축 파일 손상)");
        }
        catch (OperationCanceledException)
        {
            return OperationResult.Cancelled();
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("7Z 압축 파일을 검사할 수 없습니다", ex.Message);
        }
    }

    public OperationResult Extract(string archivePath, string destinationDirectory, ExtractOptions options,
        IProgress<ProgressInfo>? progress = null, CancellationToken cancel = default)
    {
        if (TryOpen(archivePath, out var opened) is { } error)
            return error;
        using (opened)
        {
            var selected = opened.Entries.Where(e => ExtractSession.IsSelected(options, e.Key ?? "")).ToList();
            bool encrypted = opened.HeaderEncrypted || selected.Any(IsEncrypted);
            if (encrypted && Password is null)
                return OperationResult.NeedPassword(PasswordMissing);
            long total = selected.Where(e => !e.IsDirectory).Sum(e => e.Size);
            var begin = ExtractSession.TryBegin(destinationDirectory, options, selected.Count, progress, cancel, total, out var session);
            if (begin is not null)
                return begin;
            try
            {
                var reader = opened.Archive.ExtractAllEntries();
                using var quiet = new QuietDisposer(reader);
                while (MoveNext(reader, encrypted))
                {
                    var e = reader.Entry;
                    if (e is SevenZipArchiveEntry { IsAnti: true } || !ExtractSession.IsSelected(options, e.Key ?? ""))
                        continue;
                    if (e.IsDirectory)
                        session.AddDirectory(e.Key ?? "", IsLink(e));
                    else if (encrypted)
                        session.AddFile(e.Key ?? "", IsLink(e), ExpectedCrc(e), Modified(e),
                            () => new VerifyingStream(reader.OpenEntryStream, ExpectedCrc(e), e.Size, encrypted: true));
                    else
                        session.AddFile(e.Key ?? "", IsLink(e), ExpectedCrc(e), Modified(e), reader.OpenEntryStream);
                }
                return session.Complete();
            }
            catch (WrongPasswordException)
            {
                return session.Rollback(OperationResult.NeedPassword($"{PasswordWrong} (또는 압축 파일 손상). 이번에 만든 파일을 지웠습니다"));
            }
            catch (OperationCanceledException)
            {
                return session.Rollback(OperationResult.Cancelled("해제를 취소했습니다. 이번에 만든 파일을 지웠습니다"));
            }
            catch (ExtractLimitException ex)
            {
                return session.Rollback(OperationResult.Fail(ex.Message, ex.Message));
            }
            catch (Exception ex)
            {
                return session.Rollback(OperationResult.Fail("해제 중 오류로 중단했습니다", ex.Message));
            }
        }
    }

    // ---------- 만들기 ----------

    /// <summary>
    /// 옵션 규칙: Level 0~9(0 = Copy 코더), 암호는 Encryption=Aes256 + 비어 있지 않은 Password 일 때만(암호만 있고 None 이면 NotSupported,
    /// Aes256 인데 암호가 비면 Failed), EncryptNames 는 암호가 있을 때만, VolumeSize 는 최소 64 KiB(작으면 Failed).
    /// VolumeSize 가 있으면 archivePath(name.7z) 대신 name.7z.001, .002 … 를 만든다.
    /// </summary>
    public OperationResult Create(string archivePath, IReadOnlyList<string> sources, CreateOptions? options = null, IProgress<ProgressInfo>? progress = null, CancellationToken cancel = default)
    {
        options ??= new CreateOptions { Format = ArchiveFormat.SevenZip };
        if (options.Format != ArchiveFormat.SevenZip)
            return OperationResult.Unsupported("7Z 엔진은 7Z 만 만듭니다");
        if (options.Level is < 0 or > 9)
            return OperationResult.Fail("압축 수준은 0~9 사이여야 합니다", options.Level.ToString());
        bool hasPassword = !string.IsNullOrEmpty(options.Password);
        if (options.Encryption == EncryptionMethod.Aes256 && !hasPassword)
            return OperationResult.Fail("암호화하려면 암호를 입력하세요");
        if (hasPassword && options.Encryption != EncryptionMethod.Aes256)
            return OperationResult.Unsupported("7Z 암호는 AES-256 으로만 겁니다. 암호화 방식을 AES-256 으로 지정하세요");
        if (options.EncryptNames && !hasPassword)
            return OperationResult.Unsupported("파일 이름 암호화는 암호가 있을 때만 쓸 수 있습니다");
        if (options.VolumeSize is { } vs && vs < Volumes.MinVolumeSize)
            return OperationResult.Fail($"분할 크기는 최소 {Volumes.MinVolumeSize / 1024} KiB 입니다", vs.ToString());

        string full;
        try
        {
            full = Path.GetFullPath(archivePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return OperationResult.Fail("압축 파일 경로가 올바르지 않습니다", ex.Message);
        }
        bool split = options.VolumeSize is not null;
        string firstOutput = split ? Volumes.PartPath(full, 1) : full;
        if (File.Exists(full) || Directory.Exists(full) || File.Exists(firstOutput))
            return OperationResult.Fail("같은 이름의 파일이 이미 있습니다", File.Exists(firstOutput) ? firstOutput : full);
        if (sources.Count == 0)
            return OperationResult.Fail("압축할 파일이 없습니다");
        if (!Directory.Exists(Path.GetDirectoryName(full)))
            return OperationResult.Fail("압축 파일을 둘 폴더가 없습니다", Path.GetDirectoryName(full) ?? full);

        string temp = AtomicFile.TempSibling(full);
        var problems = new List<string>();
        var sourceItems = SourceTree.Expand(sources, "", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { full, temp }, problems);
        if (problems.Count > 0)
            return new OperationResult(OperationStatus.Failed, "압축할 원본을 찾을 수 없습니다", problems);
        if (SourceTree.DuplicateName(sourceItems) is { } dup)
            return OperationResult.Fail("원본 이름이 겹칩니다", dup);

        var writeOptions = new SevenZipWriteOptions(options.Level, hasPassword ? options.Password : null, options.EncryptNames);
        var items = sourceItems.Select(ToItem);
        long total = SourceTree.TotalBytes(sourceItems);
        VolumeWriteStream? volumes = null;
        var committed = new List<string>();
        try
        {
            if (split)
            {
                using (volumes = new VolumeWriteStream(i => Volumes.PartPath(temp, i), options.VolumeSize!.Value))
                    SevenZipWriter.Write(volumes, items, writeOptions, total, progress, cancel);
                cancel.ThrowIfCancellationRequested();
                for (int i = 0; i < volumes.Parts.Count; i++)
                {
                    string target = Volumes.PartPath(full, i + 1);
                    AtomicFile.Commit(volumes.Parts[i], target, overwrite: false);
                    committed.Add(target);
                }
                return OperationResult.Ok($"항목 {sourceItems.Count}개로 분할 압축 파일 {volumes.Parts.Count}조각을 만들었습니다");
            }
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                SevenZipWriter.Write(fs, items, writeOptions, total, progress, cancel);
            cancel.ThrowIfCancellationRequested();
            AtomicFile.Commit(temp, full, overwrite: false);
            return OperationResult.Ok($"항목 {sourceItems.Count}개로 압축 파일을 만들었습니다");
        }
        catch (OperationCanceledException)
        {
            RemoveAll(committed);
            return OperationResult.Cancelled("압축을 취소했습니다");
        }
        catch (Exception ex)
        {
            RemoveAll(committed);
            return OperationResult.Fail("압축 파일을 만들지 못했습니다", ex.Message);
        }
        finally
        {
            AtomicFile.TryDelete(temp);
            if (volumes is not null)
                RemoveAll(volumes.Parts);
        }
    }

    private static void RemoveAll(IEnumerable<string> paths)
    {
        foreach (string p in paths)
            AtomicFile.TryDelete(p);
    }

    private static SevenZipItem ToItem(SourceEntry s) =>
        new(s.EntryName, s.IsDirectory, SourceTree.ModifiedUtc(s), s.IsDirectory ? null : SourceTree.OpenFile(s.FilePath!));

    // ---------- 고치기(전체 다시 쓰기) ----------

    /// <summary>항목 경로 집합(기록된 항목 + 경로에서 생기는 상위 폴더)과 폴더인 경로 집합.</summary>
    private sealed record ArchiveIndex(HashSet<string> All, HashSet<string> Folders);

    private static ArchiveIndex Index(IEnumerable<SevenZipArchiveEntry> entries)
    {
        var all = new HashSet<string>(StringComparer.Ordinal);
        var folders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            string p = SafePaths.ToDisplayPath(e.Key ?? "");
            if (p.Length == 0)
                continue;
            all.Add(p);
            if (e.IsDirectory)
                folders.Add(p);
            foreach (string parent in SafePaths.Parents(p))
            {
                all.Add(parent);
                folders.Add(parent);
            }
        }
        return new ArchiveIndex(all, folders);
    }

    /// <summary>
    /// 기존 항목을 풀면서(map 이 null 을 돌려주면 버림, 아니면 그 이름으로) 새 7Z 를 같은 폴더 임시 파일에 쓰고, 성공했을 때만 원본과 바꾼다.
    /// 암호화된 압축 파일은 같은 암호로 다시 암호화하고(헤더 암호화도 유지), 압축 수준은 <see cref="ModifyLevel"/>.
    /// </summary>
    private OperationResult Rewrite(string archivePath, Func<ArchiveIndex, OperationResult?> validate, Func<string, string?> map,
        Func<ArchiveIndex, IEnumerable<SourceEntry>> extra, string successMessage, IProgress<ProgressInfo>? progress, CancellationToken cancel)
    {
        string full;
        try
        {
            full = Path.GetFullPath(archivePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return OperationResult.Fail("압축 파일 경로가 올바르지 않습니다", ex.Message);
        }
        if (Volumes.IsFirstPart(full))
            return OperationResult.Unsupported("분할된 7Z 는 고칠 수 없습니다. 풀어서 다시 만드세요");
        if (!File.Exists(full))
            return OperationResult.Fail("압축 파일이 없습니다", archivePath);
        if (TryOpen(full, out var opened) is { } error)
            return error;

        string temp = AtomicFile.TempSibling(full);
        try
        {
            var index = Index(opened.Entries);
            if (validate(index) is { } invalid)
                return invalid;
            bool encrypted = opened.Encrypted;
            if (encrypted && Password is null)
                return OperationResult.NeedPassword(PasswordMissing);
            var added = extra(index).ToList();
            long total = opened.Entries.Where(e => !e.IsDirectory).Sum(e => e.Size) + SourceTree.TotalBytes(added);
            var writeOptions = new SevenZipWriteOptions(ModifyLevel, encrypted ? Password : null, opened.HeaderEncrypted);
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                SevenZipWriter.Write(fs, RewriteItems(opened, map, added, encrypted), writeOptions, total, progress, cancel);
            cancel.ThrowIfCancellationRequested();
            opened.Dispose();
            AtomicFile.Commit(temp, full, overwrite: true);
            return OperationResult.Ok(successMessage);
        }
        catch (WrongPasswordException)
        {
            return OperationResult.NeedPassword($"{PasswordWrong}. 압축 파일은 바뀌지 않았습니다");
        }
        catch (OperationCanceledException)
        {
            return OperationResult.Cancelled("취소했습니다. 압축 파일은 바뀌지 않았습니다");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("압축 파일을 고치지 못했습니다. 원본은 바뀌지 않았습니다", ex.Message);
        }
        finally
        {
            opened.Dispose();
            AtomicFile.TryDelete(temp);
        }
    }

    private static IEnumerable<SevenZipItem> RewriteItems(Opened opened, Func<string, string?> map, List<SourceEntry> added, bool encrypted)
    {
        var reader = opened.Archive.ExtractAllEntries();
        using (new QuietDisposer(reader))
        {
            while (MoveNext(reader, encrypted))
            {
                var e = reader.Entry;
                if (e is SevenZipArchiveEntry { IsAnti: true })
                    continue;
                string path = SafePaths.ToDisplayPath(e.Key ?? "");
                if (path.Length == 0 || map(path) is not { } target)
                    continue;
                if (e.IsDirectory)
                    yield return new SevenZipItem(target, true, ModifiedUtc(e), null);
                else
                    yield return new SevenZipItem(target, false, ModifiedUtc(e),
                        () => new VerifyingStream(reader.OpenEntryStream, ExpectedCrc(e), e.Size, encrypted));
            }
        }
        foreach (var s in added)
            yield return ToItem(s);
    }

    public OperationResult Add(string archivePath, IReadOnlyList<string> sources, string targetFolder = "", IProgress<ProgressInfo>? progress = null, CancellationToken cancel = default)
    {
        string folder = SafePaths.ToDisplayPath(targetFolder ?? "");
        if (folder.Length > 0 && (!SafePaths.TryGetSafeRelative(folder, out string safeFolder, out _) || safeFolder != folder))
            return OperationResult.Fail("대상 폴더 이름이 올바르지 않습니다", folder);
        if (sources.Count == 0)
            return OperationResult.Fail("추가할 파일이 없습니다");
        string prefix = folder.Length == 0 ? "" : folder + "/";
        var problems = new List<string>();
        string fullArchive;
        try
        {
            fullArchive = Path.GetFullPath(archivePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return OperationResult.Fail("압축 파일 경로가 올바르지 않습니다", ex.Message);
        }
        var items = SourceTree.Expand(sources, prefix, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { fullArchive }, problems);
        if (problems.Count > 0)
            return new OperationResult(OperationStatus.Failed, "추가할 원본을 찾을 수 없습니다. 압축 파일은 바뀌지 않았습니다", problems);
        if (SourceTree.DuplicateName(items) is { } dup)
            return OperationResult.Fail("추가할 원본 이름이 겹칩니다", dup);
        int fileCount = items.Count(i => !i.IsDirectory);

        return Rewrite(archivePath,
            validate: index =>
            {
                if (folder.Length > 0 && index.All.Contains(folder) && !index.Folders.Contains(folder))
                    return OperationResult.Fail("대상 경로가 폴더가 아니라 파일입니다", folder);
                var existing = new HashSet<string>(index.All, StringComparer.OrdinalIgnoreCase);
                var clash = items.Where(i => existing.Contains(i.EntryName)).Select(i => $"이미 있음: {i.EntryName}").ToArray();
                return clash.Length > 0 ? OperationResult.Fail("같은 이름의 항목이 이미 있습니다. 압축 파일은 바뀌지 않았습니다", clash) : null;
            },
            map: path => path,
            extra: index => folder.Length > 0 && !index.Folders.Contains(folder)
                ? [new SourceEntry(folder, null, true), .. items]
                : items,
            successMessage: $"파일 {fileCount}개를 추가했습니다",
            progress,
            cancel);
    }

    public OperationResult Delete(string archivePath, IReadOnlyList<string> entryPaths, IProgress<ProgressInfo>? progress = null, CancellationToken cancel = default)
    {
        var targets = entryPaths.Select(SafePaths.ToDisplayPath).Where(p => p.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (targets.Count == 0)
            return OperationResult.Fail("지울 항목이 없습니다");
        return Rewrite(archivePath,
            validate: index =>
            {
                var missing = targets.Where(t => !index.All.Contains(t)).Select(t => $"없는 항목: {t}").ToArray();
                return missing.Length > 0 ? OperationResult.Fail("압축 파일에 없는 항목입니다", missing) : null;
            },
            map: path => targets.Any(t => SafePaths.IsSelfOrDescendant(path, t)) ? null : path,
            extra: _ => [],
            successMessage: $"항목 {targets.Count}개를 지웠습니다",
            progress,
            cancel);
    }

    public OperationResult Rename(string archivePath, string entryPath, string newName, IProgress<ProgressInfo>? progress = null, CancellationToken cancel = default)
    {
        string old = SafePaths.ToDisplayPath(entryPath ?? "");
        if (old.Length == 0)
            return OperationResult.Fail("이름을 바꿀 항목이 없습니다");
        if (SafePaths.ValidateLeafName(newName) is { } nameError)
            return OperationResult.Fail("새 이름이 올바르지 않습니다", nameError);
        string parent = SafePaths.ParentOf(old);
        string renamed = parent.Length == 0 ? newName : parent + "/" + newName;
        if (renamed == old)
            return OperationResult.Ok("이름이 같아 바꾸지 않았습니다");

        return Rewrite(archivePath,
            validate: index =>
            {
                if (!index.All.Contains(old))
                    return OperationResult.Fail("압축 파일에 없는 항목입니다", old);
                bool caseOnly = string.Equals(renamed, old, StringComparison.OrdinalIgnoreCase);
                if (!caseOnly && new HashSet<string>(index.All, StringComparer.OrdinalIgnoreCase).Contains(renamed))
                    return OperationResult.Fail("같은 이름의 항목이 이미 있습니다. 압축 파일은 바뀌지 않았습니다", renamed);
                return null;
            },
            map: path => SafePaths.IsSelfOrDescendant(path, old) ? renamed + path[old.Length..] : path,
            extra: _ => [],
            successMessage: $"'{old}' → '{renamed}'",
            progress,
            cancel);
    }

    public OperationResult SetComment(string archivePath, string? comment, CancellationToken cancel = default)
        => OperationResult.Unsupported("이 형식은 압축 파일 설명을 지원하지 않습니다");
}
