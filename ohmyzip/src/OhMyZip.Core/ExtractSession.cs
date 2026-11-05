using OhMyZip.Core.Resources;

namespace OhMyZip.Core;

/// <summary>해제 한도 초과(압축 폭탄·항목 수). 세션 전체를 되돌린다.</summary>
internal sealed class ExtractLimitException(string message) : Exception(message);

/// <summary>
/// 모든 형식이 공유하는 해제 한 번의 상태. 경로 검증, 충돌 정책, 실제 해제량 계수, 임시 파일→이동,
/// 그리고 취소·한도 초과 때 이번 실행이 만든 파일·폴더를 되돌리는 실행 취소 기록을 맡는다.
/// </summary>
internal sealed class ExtractSession
{
    private const int BufferSize = 81920;

    private enum UndoKind { CreatedFile, CreatedDirectory, MovedAside }

    private readonly record struct Undo(UndoKind Kind, string Path, string? Backup = null);

    private readonly string _root;
    private readonly ExtractOptions _options;
    private readonly IProgress<ProgressInfo>? _progress;
    private readonly CancellationToken _cancel;
    private readonly long _totalBytes;
    private readonly List<Undo> _undo = [];
    private readonly List<string> _problems = [];
    private readonly List<string> _notices = [];
    private long _processed;
    private int _entries;
    private int _written;

    private ExtractSession(string root, ExtractOptions options, IProgress<ProgressInfo>? progress, CancellationToken cancel, long totalBytes)
    {
        _root = root;
        _options = options;
        _progress = progress;
        _cancel = cancel;
        _totalBytes = totalBytes;
    }

    /// <summary>선택 필터(<see cref="ExtractOptions.Only"/>)만 적용하는 판정. 세션을 만들기 전 사전 계수에도 쓴다.</summary>
    public static bool IsSelected(ExtractOptions options, string rawName)
    {
        if (options.Only is null)
            return true;
        string p = SafePaths.ToDisplayPath(rawName);
        foreach (string o in options.Only)
        {
            if (SafePaths.IsSelfOrDescendant(p, SafePaths.ToDisplayPath(o)))
                return true;
        }
        return false;
    }

    /// <summary>대상 폴더를 준비하고 세션을 연다. 실패하면 오류 결과를 돌려준다.</summary>
    public static OperationResult? TryBegin(string destinationDirectory, ExtractOptions options, int selectedEntries,
        IProgress<ProgressInfo>? progress, CancellationToken cancel, long totalBytes, out ExtractSession session)
    {
        session = null!;
        if (selectedEntries > options.MaxEntries)
            return OperationResult.Fail(EngineText.Extract_TooManyItems, string.Format(EngineText.Extract_TooManyItemsDetail, selectedEntries, options.MaxEntries));
        string root;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDirectory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return OperationResult.Fail(EngineText.Extract_DestinationInvalid, ex.Message);
        }
        session = new ExtractSession(root, options, progress, cancel, totalBytes);
        string? error = session.EnsureDirectory(root);
        if (error is not null)
            return OperationResult.Fail(EngineText.Extract_DestinationCreateFailed, error);
        return null;
    }

    private void CountEntry()
    {
        _entries++;
        if (_entries > _options.MaxEntries)
            throw new ExtractLimitException(string.Format(EngineText.Extract_ItemLimitExceeded, _options.MaxEntries));
    }

    public void Reject(string rawName, string reason) => _problems.Add(string.Format(EngineText.Extract_Rejected, rawName, reason));

    private bool TryResolve(string rawName, out string relative, out string full)
    {
        full = "";
        if (!SafePaths.TryGetSafeRelative(rawName, out relative, out string error))
        {
            Reject(rawName, error);
            return false;
        }
        string? resolved = SafePaths.ResolveUnder(_root, relative);
        if (resolved is null)
        {
            Reject(rawName, EngineText.Extract_OutsideDestination);
            return false;
        }
        full = resolved;
        return true;
    }

    public void AddDirectory(string rawName, bool isLink)
    {
        _cancel.ThrowIfCancellationRequested();
        CountEntry();
        if (isLink)
        {
            Reject(rawName, EngineText.Extract_SymlinkItem);
            return;
        }
        if (!TryResolve(rawName, out string relative, out string full))
            return;
        string? error = EnsureDirectory(full);
        if (error is not null)
            _problems.Add($"{relative}: {error}");
    }

    public void AddFile(string rawName, bool isLink, uint? expectedCrc, DateTimeOffset? modified, Func<Stream> open)
    {
        _cancel.ThrowIfCancellationRequested();
        CountEntry();
        if (isLink)
        {
            Reject(rawName, EngineText.Extract_SymlinkItem);
            return;
        }
        if (!TryResolve(rawName, out string relative, out string full))
            return;
        string? dirError = EnsureDirectory(Path.GetDirectoryName(full)!);
        if (dirError is not null)
        {
            _problems.Add($"{relative}: {dirError}");
            return;
        }

        bool moveAside = false;
        if (File.Exists(full) || Directory.Exists(full))
        {
            switch (_options.Conflict)
            {
                case ConflictPolicy.Skip:
                    _notices.Add(string.Format(EngineText.Extract_SkippedExisting, relative));
                    return;
                case ConflictPolicy.Overwrite:
                    if (Directory.Exists(full))
                    {
                        _problems.Add(string.Format(EngineText.Extract_FolderInTheWay, relative));
                        return;
                    }
                    moveAside = true;
                    break;
                case ConflictPolicy.Rename:
                    full = SafePaths.UniqueSibling(full);
                    break;
            }
        }

        string temp = AtomicFile.TempSibling(full);
        try
        {
            uint actualCrc;
            try
            {
                actualCrc = CopyCounted(open, temp, relative);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or SharpCompress.Common.SharpCompressException or NotSupportedException or UnauthorizedAccessException)
            {
                _problems.Add(string.Format(EngineText.Extract_DecompressFailed, relative, ex.Message));
                return;
            }
            if (expectedCrc is { } crc && crc != actualCrc)
            {
                _problems.Add(string.Format(EngineText.Common_CrcMismatch, relative, crc, actualCrc));
                return;
            }
            if (modified is { } m)
                TrySetTime(temp, m);
            if (moveAside)
            {
                string backup = AtomicFile.TempSibling(full, ".ozbak");
                File.Move(full, backup);
                _undo.Add(new Undo(UndoKind.MovedAside, full, backup));
                File.Move(temp, full);
            }
            else
            {
                File.Move(temp, full, overwrite: false);
            }
            _undo.Add(new Undo(UndoKind.CreatedFile, full));
            _written++;
        }
        catch (IOException ex)
        {
            _problems.Add(string.Format(EngineText.Extract_WriteFailed, relative, ex.Message));
        }
        finally
        {
            AtomicFile.TryDelete(temp);
        }
    }

    private uint CopyCounted(Func<Stream> open, string temp, string relative)
    {
        var crc = new Crc32();
        byte[] buffer = new byte[BufferSize];
        using Stream source = open();
        using var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize);
        while (true)
        {
            _cancel.ThrowIfCancellationRequested();
            int n = source.Read(buffer, 0, buffer.Length);
            if (n <= 0)
                break;
            _processed += n;
            if (_processed > _options.MaxTotalBytes)
                throw new ExtractLimitException(string.Format(EngineText.Extract_TotalLimitExceeded, _options.MaxTotalBytes));
            crc.Append(buffer.AsSpan(0, n));
            target.Write(buffer, 0, n);
            _progress?.Report(new ProgressInfo(_processed, _totalBytes, relative));
        }
        return crc.Value;
    }

    private static void TrySetTime(string path, DateTimeOffset modified)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, modified.UtcDateTime);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>폴더를 만들고(없는 단계마다 실행 취소 기록), 루트 아래의 기존 단계가 링크(재분석 지점)면 거부한다.</summary>
    private string? EnsureDirectory(string dir)
    {
        var missing = new Stack<string>();
        string? cur = dir;
        while (cur is not null && !Directory.Exists(cur))
        {
            if (File.Exists(cur))
                return EngineText.Extract_FileInTheWay;
            missing.Push(cur);
            cur = Path.GetDirectoryName(cur);
        }
        for (string? c = cur; c is not null && SafePaths.IsStrictlyUnder(_root, c); c = Path.GetDirectoryName(c))
        {
            if ((File.GetAttributes(c) & FileAttributes.ReparsePoint) != 0)
                return EngineText.Extract_ReparsePointPath;
        }
        while (missing.Count > 0)
        {
            string d = missing.Pop();
            Directory.CreateDirectory(d);
            _undo.Add(new Undo(UndoKind.CreatedDirectory, d));
        }
        return null;
    }

    /// <summary>정상 종료: 덮어쓰기 백업을 지우고 결과를 만든다.</summary>
    public OperationResult Complete()
    {
        foreach (var u in _undo)
        {
            if (u.Kind == UndoKind.MovedAside)
                AtomicFile.TryDelete(u.Backup!);
        }
        _progress?.Report(new ProgressInfo(_processed, _totalBytes, null));
        if (_problems.Count > 0)
            return new OperationResult(OperationStatus.Failed, string.Format(EngineText.Extract_PartialFailure, _problems.Count, _written), [.. _problems, .. _notices]);
        string message = _notices.Count > 0
            ? string.Format(EngineText.Extract_DoneWithSkipped, _written, _notices.Count)
            : string.Format(EngineText.Extract_Done, _written);
        return new OperationResult(OperationStatus.Succeeded, message, [.. _notices]);
    }

    /// <summary>이번 실행이 만든 파일·폴더를 역순으로 지우고 덮어쓴 파일을 되돌린다.</summary>
    public OperationResult Rollback(OperationResult result)
    {
        for (int i = _undo.Count - 1; i >= 0; i--)
        {
            var u = _undo[i];
            try
            {
                switch (u.Kind)
                {
                    case UndoKind.CreatedFile:
                        if (File.Exists(u.Path))
                            File.Delete(u.Path);
                        break;
                    case UndoKind.CreatedDirectory:
                        if (Directory.Exists(u.Path) && !Directory.EnumerateFileSystemEntries(u.Path).Any())
                            Directory.Delete(u.Path);
                        break;
                    case UndoKind.MovedAside:
                        if (File.Exists(u.Path))
                            File.Delete(u.Path);
                        File.Move(u.Backup!, u.Path);
                        break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _problems.Add(string.Format(EngineText.Extract_UndoFailed, u.Path, ex.Message));
            }
        }
        _undo.Clear();
        return result with { Problems = [.. result.Problems, .. _problems] };
    }
}
