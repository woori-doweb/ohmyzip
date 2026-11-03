using System.Buffers.Binary;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace OhMyZip.Core;

/// <summary>
/// RAR(RAR4·RAR5) 읽기 전용 엔진(SharpCompress). 목록·검사·해제와 암호(파일만/헤더까지)를 지원한다.
/// RAR 만들기·고치기는 RAR 압축 형식 라이선스 때문에 하지 않는다(NotSupported). 분할 RAR 는 첫 조각만 연다.
/// </summary>
public sealed class RarReadEngine(ReadOptions? read = null) : IArchiveEngine
{
    private readonly ReadOptions _read = read ?? new ReadOptions();

    private const int BufferSize = 81920;
    private const string PasswordMissing = "암호가 필요합니다";
    private const string PasswordWrong = "암호가 올바르지 않습니다";
    private const string WriteUnsupported = "RAR 만들기·고치기는 지원하지 않습니다 (RAR 압축은 라이선스상 만들 수 없습니다)";
    private static readonly byte[] Rar4Signature = "Rar!\x1A\x07\x00"u8.ToArray();
    private static readonly byte[] Rar5Signature = "Rar!\x1A\x07\x01\x00"u8.ToArray();

    public FormatCapabilities Capabilities { get; } = new(Read: true, Test: true, Extract: true, Create: false, Modify: false, Encrypt: false, Split: false, Comment: false);

    private string? Password => string.IsNullOrEmpty(_read.Password) ? null : _read.Password;

    private sealed class Opened(Stream stream, RarArchive archive, List<RarArchiveEntry> entries, bool headerEncrypted, bool rar5) : IDisposable
    {
        public RarArchive Archive { get; } = archive;
        public List<RarArchiveEntry> Entries { get; } = entries;
        public bool HeaderEncrypted { get; } = headerEncrypted;
        public bool Rar5 { get; } = rar5;
        public bool Encrypted => HeaderEncrypted || Entries.Any(IsEncrypted);

        public void Dispose()
        {
            Archive.Dispose();
            stream.Dispose();
        }
    }

    private static bool IsEncrypted(IEntry e) => e.IsEncrypted && !e.IsDirectory;

    /// <summary>
    /// 암호화된 RAR5 항목의 CRC 는 키로 변환된 값이라 원문 CRC 와 비교할 수 없다 → SharpCompress 의 암호 확인값에 맡긴다.
    /// RAR4 와 암호화되지 않은 항목은 CRC-32 를 직접 비교한다.
    /// </summary>
    private static uint? ExpectedCrc(IEntry e, bool rar5) =>
        e.IsDirectory || (rar5 && e.IsEncrypted) || (e.Crc == 0 && e.Size != 0) ? null : (uint)e.Crc;

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

    private static bool IsLink(IEntry e) => !string.IsNullOrEmpty(e.LinkTarget);

    /// <summary>
    /// 서명과 첫 헤더만 읽어 RAR5 여부와 헤더 암호화를 본다. RAR4: 주 헤더(0x73) 플래그 0x0080, RAR5: 첫 블록이 암호화 헤더(형식 4).
    /// </summary>
    private static (bool Rar5, bool HeaderEncrypted) Probe(Stream s)
    {
        Span<byte> head = stackalloc byte[32];
        int n = s.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        head = head[..n];
        if (head.StartsWith(Rar5Signature))
        {
            int pos = Rar5Signature.Length + 4; // 헤더 CRC32
            if (!TryReadVInt(head, ref pos, out _) || !TryReadVInt(head, ref pos, out ulong type))
                return (true, false);
            return (true, type == 4);
        }
        if (head.StartsWith(Rar4Signature) && head.Length >= Rar4Signature.Length + 5)
        {
            int at = Rar4Signature.Length;
            byte type = head[at + 2];
            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(head[(at + 3)..]);
            return (false, type == 0x73 && (flags & 0x0080) != 0);
        }
        return (false, false);
    }

    private static bool TryReadVInt(ReadOnlySpan<byte> data, ref int pos, out ulong value)
    {
        value = 0;
        for (int shift = 0; shift < 64 && pos < data.Length; shift += 7)
        {
            byte b = data[pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return true;
        }
        return false;
    }

    private OperationResult? TryOpen(string archivePath, out Opened opened)
    {
        opened = null!;
        FileStream stream;
        try
        {
            stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("RAR 압축파일을 열 수 없습니다", ex.Message);
        }
        RarArchive? archive = null;
        bool headerEncrypted = false;
        try
        {
            var (rar5, encryptedHeader) = Probe(stream);
            headerEncrypted = encryptedHeader;
            if (headerEncrypted && Password is null)
            {
                stream.Dispose();
                return OperationResult.NeedPassword($"{PasswordMissing} (파일 목록이 암호화된 RAR)");
            }
            stream.Position = 0;
            archive = RarArchive.Open(stream, new ReaderOptions { Password = Password, LeaveStreamOpen = true });
            var entries = archive.Entries.ToList();
            opened = new Opened(stream, archive, entries, headerEncrypted, rar5);
            return null;
        }
        catch (Exception ex)
        {
            archive?.Dispose();
            stream.Dispose();
            if (headerEncrypted || ex is CryptographicException)
                return OperationResult.NeedPassword(Password is null ? PasswordMissing : PasswordWrong);
            return OperationResult.Fail("RAR 압축파일을 열 수 없습니다", ex.Message);
        }
    }

    /// <summary>solid RAR 는 차례로만 풀 수 있고, 아니면 항목마다 연다.</summary>
    private static IEnumerable<(IEntry Entry, Func<Stream> Open)> Contents(Opened opened)
    {
        if (opened.Archive.IsSolid)
        {
            var reader = opened.Archive.ExtractAllEntries();
            using var quiet = new QuietDisposer(reader);
            while (reader.MoveToNextEntry())
                yield return (reader.Entry, reader.OpenEntryStream);
        }
        else
        {
            foreach (var e in opened.Entries)
                yield return (e, e.OpenEntryStream);
        }
    }

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
                            Modified(e), ExpectedCrc(e, opened.Rar5), e.IsDirectory ? null : e.CompressionType.ToString(), IsEncrypted(e)));
                    }
                }
                // DEF-02: 목록은 압축파일에 기록된 항목만 돌려준다(경로로만 생기는 폴더는 앱이 트리에서 만든다).
                return new ListResult(OperationResult.Ok($"항목 {entries.Count}개"), entries);
            }
        }
        catch (OperationCanceledException)
        {
            return new ListResult(OperationResult.Cancelled(), []);
        }
        catch (Exception ex)
        {
            return new ListResult(OperationResult.Fail("RAR 압축파일을 읽을 수 없습니다", ex.Message), []);
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
                if (opened.Encrypted && Password is null)
                    return OperationResult.NeedPassword(PasswordMissing);
                long total = opened.Entries.Where(e => !e.IsDirectory).Sum(e => e.Size);
                long processed = 0;
                int files = 0;
                var problems = new List<string>();
                byte[] buffer = new byte[BufferSize];
                foreach (var (e, open) in Contents(opened))
                {
                    cancel.ThrowIfCancellationRequested();
                    if (e.IsDirectory)
                        continue;
                    files++;
                    string path = SafePaths.ToDisplayPath(e.Key ?? "");
                    try
                    {
                        using var s = new VerifyingStream(open, ExpectedCrc(e, opened.Rar5), e.Size, IsEncrypted(e));
                        int n;
                        while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            cancel.ThrowIfCancellationRequested();
                            processed += n;
                            progress?.Report(new ProgressInfo(processed, total, path));
                        }
                    }
                    catch (Exception ex) when (ex is SharpCompressException or InvalidDataException or IOException or NotSupportedException or FormatException)
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
            return OperationResult.NeedPassword($"{PasswordWrong} (또는 압축파일 손상)");
        }
        catch (OperationCanceledException)
        {
            return OperationResult.Cancelled();
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("RAR 압축파일을 검사할 수 없습니다", ex.Message);
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
            if ((opened.HeaderEncrypted || selected.Any(IsEncrypted)) && Password is null)
                return OperationResult.NeedPassword(PasswordMissing);
            long total = selected.Where(e => !e.IsDirectory).Sum(e => e.Size);
            var begin = ExtractSession.TryBegin(destinationDirectory, options, selected.Count, progress, cancel, total, out var session);
            if (begin is not null)
                return begin;
            try
            {
                foreach (var (e, open) in Contents(opened))
                {
                    if (!ExtractSession.IsSelected(options, e.Key ?? ""))
                        continue;
                    if (e.IsDirectory)
                        session.AddDirectory(e.Key ?? "", IsLink(e));
                    else if (IsEncrypted(e))
                        session.AddFile(e.Key ?? "", IsLink(e), ExpectedCrc(e, opened.Rar5), Modified(e),
                            () => new VerifyingStream(open, ExpectedCrc(e, opened.Rar5), e.Size, encrypted: true));
                    else
                        session.AddFile(e.Key ?? "", IsLink(e), ExpectedCrc(e, opened.Rar5), Modified(e), open);
                }
                return session.Complete();
            }
            catch (WrongPasswordException)
            {
                return session.Rollback(OperationResult.NeedPassword($"{PasswordWrong} (또는 압축파일 손상). 이번에 만든 파일을 지웠습니다"));
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

    public OperationResult Create(string archivePath, IReadOnlyList<string> sources, CreateOptions? options = null, IProgress<ProgressInfo>? progress = null, CancellationToken cancel = default)
        => OperationResult.Unsupported(WriteUnsupported);

    public OperationResult Add(string archivePath, IReadOnlyList<string> sources, string targetFolder = "", IProgress<ProgressInfo>? progress = null, CancellationToken cancel = default)
        => OperationResult.Unsupported(WriteUnsupported);

    public OperationResult Delete(string archivePath, IReadOnlyList<string> entryPaths, IProgress<ProgressInfo>? progress = null, CancellationToken cancel = default)
        => OperationResult.Unsupported(WriteUnsupported);

    public OperationResult Rename(string archivePath, string entryPath, string newName, IProgress<ProgressInfo>? progress = null, CancellationToken cancel = default)
        => OperationResult.Unsupported(WriteUnsupported);

    public OperationResult SetComment(string archivePath, string? comment, CancellationToken cancel = default)
        => OperationResult.Unsupported("RAR 압축파일 설명은 지원하지 않습니다");
}
