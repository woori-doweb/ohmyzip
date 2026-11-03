using System.Text;

namespace OhMyZip.Core;

/// <summary>
/// ZIP 엔진(자체 읽기·쓰기: <see cref="ZipArchiveReader"/>, <see cref="ZipWriter"/>). 목록·검사·해제·생성·추가·삭제·이름변경·설명.
/// 암호: 만들기·추가는 WinZip AES-256(AE-2), 읽기는 AES(AE-1/AE-2, HMAC 검증)와 전통 ZipCrypto(복호화만). 이름 암호화는 ZIP 에 없다.
/// 분할: name.z01…name.zip 만들기·읽기. 분할 압축파일 고치기(추가·삭제·이름변경·설명)는 NotSupported.
/// 수정 작업은 기존 항목을 원시 복사(복호화 없음)해 같은 폴더 임시 파일에 새로 쓰고 성공했을 때만 원본 자리로 옮긴다.
/// </summary>
public sealed class ZipEngine(ReadOptions? read = null) : IArchiveEngine
{
    private readonly ReadOptions _read = read ?? new ReadOptions();

    private const int BufferSize = 81920;
    private const int MaxCommentBytes = 0xFFFF;

    /// <summary>Split=true 는 만들기·읽기만 뜻한다. 분할 압축파일의 수정·설명 쓰기는 NotSupported.</summary>
    public FormatCapabilities Capabilities { get; } = new(Read: true, Test: true, Extract: true, Create: true, Modify: true, Encrypt: true, Split: true, Comment: true);

    // ---------- 읽기 ----------

    private ZipArchiveReader OpenRead(string archivePath) => ZipArchiveReader.Open(archivePath, _read.CodePage);

    private static OperationResult OpenFailure(Exception ex) => ex is ZipVolumeMissingException missing
        ? OperationResult.Fail(missing.Message, missing.FileName)
        : OperationResult.Fail("ZIP 압축파일을 열 수 없습니다", ex.Message);

    public ListResult List(string archivePath, CancellationToken cancel = default)
    {
        try
        {
            using var zip = OpenRead(archivePath);
            // 2026-10-27 DEF-02: 목록은 압축파일에 기록된 항목만 돌려준다(경로로만 생기는 상위 폴더는 합성하지 않음).
            var entries = new List<ArchiveEntry>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in zip.Entries)
            {
                cancel.ThrowIfCancellationRequested();
                string path = SafePaths.ToDisplayPath(r.Name);
                if (path.Length == 0 || !seen.Add(path))
                    continue;
                bool dir = r.IsDirectory;
                entries.Add(new ArchiveEntry(path, dir, dir ? 0 : r.Size, dir ? 0 : r.CompressedSize,
                    r.Modified, r.ExpectedCrc, dir ? null : r.DisplayMethod, r.IsEncrypted));
            }
            return new ListResult(OperationResult.Ok($"항목 {entries.Count}개"), entries, zip.Directory.Comment);
        }
        catch (OperationCanceledException)
        {
            return new ListResult(OperationResult.Cancelled(), []);
        }
        catch (Exception ex)
        {
            return new ListResult(OpenFailure(ex), []);
        }
    }

    public OperationResult Test(string archivePath, IProgress<ProgressInfo>? progress = null, CancellationToken cancel = default)
    {
        try
        {
            using var zip = OpenRead(archivePath);
            var files = zip.Entries.Where(e => !e.IsDirectory).ToList();
            if (files.Any(e => e.IsEncrypted) && string.IsNullOrEmpty(_read.Password))
                return OperationResult.NeedPassword(ZipPasswordException.MissingMessage);
            long total = files.Sum(e => e.Size);
            long processed = 0;
            var problems = new List<string>();
            byte[] buffer = new byte[BufferSize];
            foreach (var e in files)
            {
                cancel.ThrowIfCancellationRequested();
                string path = SafePaths.ToDisplayPath(e.Name);
                try
                {
                    var crc = new Crc32();
                    long size = 0;
                    using (var s = zip.OpenEntry(e, _read.Password))
                    {
                        int n;
                        while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            cancel.ThrowIfCancellationRequested();
                            crc.Append(buffer.AsSpan(0, n));
                            size += n;
                            processed += n;
                            progress?.Report(new ProgressInfo(processed, total, path));
                        }
                    }
                    if (e.ExpectedCrc is { } expected && crc.Value != expected)
                        problems.Add($"{path}: CRC 불일치 (기대 {expected:X8}, 실제 {crc.Value:X8})");
                    else if (size != e.Size)
                        problems.Add($"{path}: 크기 불일치 (기대 {e.Size}, 실제 {size})");
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException or SharpCompress.Common.SharpCompressException)
                {
                    problems.Add($"{path}: {ex.Message}");
                }
            }
            return problems.Count == 0
                ? OperationResult.Ok($"파일 {files.Count}개 이상 없음")
                : new OperationResult(OperationStatus.Failed, $"손상된 항목 {problems.Count}개", problems);
        }
        catch (OperationCanceledException)
        {
            return OperationResult.Cancelled();
        }
        catch (ZipPasswordException ex)
        {
            return OperationResult.NeedPassword(ex.Message);
        }
        catch (Exception ex)
        {
            return OpenFailure(ex);
        }
    }

    public OperationResult Extract(string archivePath, string destinationDirectory, ExtractOptions options,
        IProgress<ProgressInfo>? progress = null, CancellationToken cancel = default)
    {
        ZipArchiveReader zip;
        try
        {
            zip = OpenRead(archivePath);
        }
        catch (Exception ex)
        {
            return OpenFailure(ex);
        }
        using (zip)
        {
            var selected = zip.Entries.Where(e => ExtractSession.IsSelected(options, e.Name)).ToList();
            // 암호 확인은 대상 폴더를 만들기 전에 한다: 없거나 틀리면 아무 것도 남기지 않는다.
            if (selected.FirstOrDefault(e => e.IsEncrypted && !e.IsDirectory) is { } encrypted)
            {
                try
                {
                    zip.OpenEntry(encrypted, _read.Password).Dispose();
                }
                catch (ZipPasswordException ex)
                {
                    return OperationResult.NeedPassword(ex.Message);
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
                {
                    // 항목 자체의 문제는 해제 중에 그 항목의 문제로 기록한다.
                }
            }
            long total = selected.Where(e => !e.IsDirectory).Sum(e => e.Size);
            var begin = ExtractSession.TryBegin(destinationDirectory, options, selected.Count, progress, cancel, total, out var session);
            if (begin is not null)
                return begin;
            try
            {
                foreach (var e in selected)
                {
                    if (e.IsDirectory)
                        session.AddDirectory(e.Name, e.IsSymlink);
                    else
                        session.AddFile(e.Name, e.IsSymlink, e.ExpectedCrc, e.Modified, () => zip.OpenEntry(e, _read.Password));
                }
                return session.Complete();
            }
            catch (OperationCanceledException)
            {
                return session.Rollback(OperationResult.Cancelled("해제를 취소했습니다. 이번에 만든 파일을 지웠습니다"));
            }
            catch (ZipPasswordException ex)
            {
                return session.Rollback(OperationResult.NeedPassword(ex.Message));
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

    // ---------- 쓰기 ----------

    private sealed record SourceItem(string EntryName, string SourcePath, bool IsDirectory);

    /// <summary>원본 파일·폴더를 항목 목록으로 펼친다. 폴더는 이름째, 빈 폴더 포함. 링크 폴더는 따라가지 않는다.</summary>
    private static List<SourceItem> Expand(IReadOnlyList<string> sources, string prefix, ISet<string> exclude, List<string> problems)
    {
        var items = new List<SourceItem>();
        foreach (string source in sources)
        {
            string full;
            try
            {
                full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                problems.Add($"경로가 올바르지 않음: {source}");
                continue;
            }
            string name = Path.GetFileName(full);
            if (File.Exists(full))
            {
                items.Add(new SourceItem(prefix + name, full, false));
            }
            else if (Directory.Exists(full) && name.Length > 0)
            {
                AddFolder(new DirectoryInfo(full), prefix + name, items, exclude);
            }
            else
            {
                problems.Add($"원본이 없음: {source}");
            }
        }
        return items;
    }

    private static void AddFolder(DirectoryInfo dir, string entryName, List<SourceItem> items, ISet<string> exclude)
    {
        items.Add(new SourceItem(entryName + "/", dir.FullName, true));
        if ((dir.Attributes & FileAttributes.ReparsePoint) != 0)
            return;
        foreach (var child in dir.EnumerateFileSystemInfos().OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            if (exclude.Contains(child.FullName))
                continue;
            if (child is DirectoryInfo sub)
                AddFolder(sub, entryName + "/" + sub.Name, items, exclude);
            else
                items.Add(new SourceItem(entryName + "/" + child.Name, child.FullName, false));
        }
    }

    private static void WriteItems(ZipWriter writer, List<SourceItem> items, int level, string? password, IProgress<ProgressInfo>? progress, CancellationToken cancel)
    {
        long total = items.Where(i => !i.IsDirectory).Sum(i => new FileInfo(i.SourcePath).Length);
        long processed = 0;
        foreach (var item in items)
        {
            cancel.ThrowIfCancellationRequested();
            if (item.IsDirectory)
            {
                writer.AddDirectory(item.EntryName, Directory.GetLastWriteTime(item.SourcePath));
                continue;
            }
            using var source = new FileStream(item.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);
            writer.AddFile(item.EntryName, source, File.GetLastWriteTime(item.SourcePath), level, password, n =>
            {
                processed += n;
                progress?.Report(new ProgressInfo(processed, total, item.EntryName));
            }, cancel);
        }
    }

    private static string? DuplicateName(List<SourceItem> items)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in items)
        {
            if (!names.Add(i.EntryName))
                return i.EntryName.TrimEnd('/');
        }
        return null;
    }

    /// <summary>
    /// 압축 수준 0 = 저장, 1~3 = 빠른 Deflate, 4~6 = 최적, 7~9 = 최소 크기(<see cref="ZipWriter.MapLevel"/>).
    /// Encryption=Aes256 + 암호 → WinZip AES-256(AE-2). 암호만 있고 Encryption=None, 또는 EncryptNames 는 NotSupported.
    /// VolumeSize(64 KiB 이상) → name.z01…name.zip 분할. 한 조각에 다 들어가면 보통 ZIP 한 파일.
    /// </summary>
    public OperationResult Create(string archivePath, IReadOnlyList<string> sources, CreateOptions? options = null, IProgress<ProgressInfo>? progress = null, CancellationToken cancel = default)
    {
        options ??= new CreateOptions();
        if (options.Format != ArchiveFormat.Zip)
            return OperationResult.Unsupported("ZIP 엔진은 ZIP 만 만듭니다");
        if (options.Level is < 0 or > 9)
            return OperationResult.Fail("압축 수준은 0~9 사이여야 합니다", options.Level.ToString());
        if (options.EncryptNames)
            return OperationResult.Unsupported("ZIP 은 파일 이름을 암호화할 수 없습니다 (7Z 를 쓰세요)");
        bool hasPassword = !string.IsNullOrEmpty(options.Password);
        if (options.Encryption == EncryptionMethod.None && hasPassword)
            return OperationResult.Unsupported("암호를 쓰려면 암호화 방식(AES-256)을 지정해야 합니다");
        if (options.Encryption == EncryptionMethod.Aes256 && !hasPassword)
            return OperationResult.Fail("암호가 비어 있습니다");
        if (options.VolumeSize is { } volume && volume < ZipVolumeWriter.MinVolumeSize)
            return OperationResult.Fail("분할 크기는 64 KiB 이상이어야 합니다", volume.ToString());
        string full;
        try
        {
            full = Path.GetFullPath(archivePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return OperationResult.Fail("압축파일 경로가 올바르지 않습니다", ex.Message);
        }
        if (options.VolumeSize is not null && !string.Equals(Path.GetExtension(full), ".zip", StringComparison.OrdinalIgnoreCase))
            return OperationResult.Fail("분할 ZIP 의 마지막 조각 이름은 .zip 으로 끝나야 합니다", full);
        if (File.Exists(full) || Directory.Exists(full))
            return OperationResult.Fail("같은 이름의 파일이 이미 있습니다", full);
        if (options.VolumeSize is not null && File.Exists(ZipVolumeSet.PartPath(full, 1)))
            return OperationResult.Fail("같은 이름의 분할 조각이 이미 있습니다", ZipVolumeSet.PartPath(full, 1));
        if (sources.Count == 0)
            return OperationResult.Fail("압축할 파일이 없습니다");
        if (!Directory.Exists(Path.GetDirectoryName(full)))
            return OperationResult.Fail("압축파일을 둘 폴더가 없습니다", Path.GetDirectoryName(full) ?? full);

        var problems = new List<string>();
        var items = Expand(sources, "", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { full }, problems);
        if (problems.Count > 0)
            return new OperationResult(OperationStatus.Failed, "압축할 원본을 찾을 수 없습니다", problems);
        if (DuplicateName(items) is { } dup)
            return OperationResult.Fail("원본 이름이 겹칩니다", dup);
        ZipVolumeWriter? output = null;
        try
        {
            output = ZipVolumeWriter.Create(full, options.VolumeSize);
            var writer = new ZipWriter(output);
            WriteItems(writer, items, options.Level, hasPassword ? options.Password : null, progress, cancel);
            writer.Finish([]);
            cancel.ThrowIfCancellationRequested();
            var parts = output.Commit(overwrite: false);
            return OperationResult.Ok(parts.Count > 1
                ? $"항목 {items.Count}개로 분할 압축파일 {parts.Count}조각을 만들었습니다"
                : $"항목 {items.Count}개로 압축파일을 만들었습니다");
        }
        catch (OperationCanceledException)
        {
            return OperationResult.Cancelled("압축을 취소했습니다");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("압축파일을 만들지 못했습니다", ex.Message);
        }
        finally
        {
            output?.Dispose();
        }
    }

    /// <summary>항목 경로 집합(명시 항목 + 암시 폴더), 그리고 폴더인 경로 집합.</summary>
    private static (HashSet<string> All, HashSet<string> Folders) Index(ZipArchiveReader zip)
    {
        var all = new HashSet<string>(StringComparer.Ordinal);
        var folders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in zip.Entries)
        {
            string p = SafePaths.ToDisplayPath(e.Name);
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
        return (all, folders);
    }

    /// <summary>
    /// 원본을 읽어 같은 폴더 임시 파일에 새 압축파일을 쓰고(write), 성공했을 때만 원본 자리로 원자적으로 옮긴다.
    /// validate 가 문제를 돌려주면 아무 파일도 만들지 않는다. comment=null 이면 기존 설명을 그대로 둔다.
    /// </summary>
    private OperationResult Modify(string archivePath, Func<ZipArchiveReader, OperationResult?> validate,
        Action<ZipArchiveReader, ZipWriter> write, byte[]? comment, string successMessage, CancellationToken cancel)
    {
        string full;
        ZipArchiveReader zip;
        try
        {
            full = Path.GetFullPath(archivePath);
            if (!File.Exists(full))
                return OperationResult.Fail("압축파일이 없습니다", archivePath);
            zip = OpenRead(full);
        }
        catch (Exception ex)
        {
            return OpenFailure(ex);
        }

        ZipVolumeWriter? output = null;
        try
        {
            using (zip)
            {
                if (zip.Volumes.IsSplit)
                    return OperationResult.Unsupported("분할 ZIP 은 고칠 수 없습니다. 압축파일은 바뀌지 않았습니다");
                if (validate(zip) is { } invalid)
                    return invalid;
                output = ZipVolumeWriter.Create(full, null);
                var writer = new ZipWriter(output);
                write(zip, writer);
                writer.Finish(comment ?? zip.Directory.CommentBytes);
            }
            cancel.ThrowIfCancellationRequested();
            output.Commit(overwrite: true);
            return OperationResult.Ok(successMessage);
        }
        catch (OperationCanceledException)
        {
            return OperationResult.Cancelled("취소했습니다. 압축파일은 바뀌지 않았습니다");
        }
        catch (ZipPasswordException ex)
        {
            return OperationResult.NeedPassword(ex.Message);
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("압축파일을 고치지 못했습니다. 원본은 바뀌지 않았습니다", ex.Message);
        }
        finally
        {
            output?.Dispose();
        }
    }

    /// <summary>기존 항목을 원시 복사한다. rename 이 이름을 돌려주면 그 이름으로, skip 이 true 면 뺀다.</summary>
    private static void CopyExisting(ZipArchiveReader zip, ZipWriter writer, Func<ZipRecord, bool> skip, Func<ZipRecord, string?> rename,
        IProgress<ProgressInfo>? progress, CancellationToken cancel)
    {
        var kept = zip.Entries.Where(e => !skip(e)).ToList();
        long total = kept.Sum(e => e.CompressedSize);
        long processed = 0;
        foreach (var e in kept)
        {
            cancel.ThrowIfCancellationRequested();
            writer.CopyRaw(zip, e, rename(e), n =>
            {
                processed += n;
                progress?.Report(new ProgressInfo(processed, total, SafePaths.ToDisplayPath(e.Name)));
            }, cancel);
        }
    }

    /// <summary>암호 걸린 압축파일에 추가하면 엔진의 암호(ReadOptions.Password)로 AES-256 암호화한다. 암호가 없으면 PasswordRequired, 기존 항목과 맞지 않으면 PasswordRequired.</summary>
    public OperationResult Add(string archivePath, IReadOnlyList<string> sources, string targetFolder = "", IProgress<ProgressInfo>? progress = null, CancellationToken cancel = default)
    {
        string folder = SafePaths.ToDisplayPath(targetFolder ?? "");
        if (folder.Length > 0 && (!SafePaths.TryGetSafeRelative(folder, out string safeFolder, out _) || safeFolder != folder))
            return OperationResult.Fail("대상 폴더 이름이 올바르지 않습니다", folder);
        if (sources.Count == 0)
            return OperationResult.Fail("추가할 파일이 없습니다");
        string prefix = folder.Length == 0 ? "" : folder + "/";
        var problems = new List<string>();
        string fullArchive = Path.GetFullPath(archivePath);
        var items = Expand(sources, prefix, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { fullArchive }, problems);
        if (problems.Count > 0)
            return new OperationResult(OperationStatus.Failed, "추가할 원본을 찾을 수 없습니다. 압축파일은 바뀌지 않았습니다", problems);
        if (DuplicateName(items) is { } dup)
            return OperationResult.Fail("추가할 원본 이름이 겹칩니다", dup);
        int fileCount = items.Count(i => !i.IsDirectory);
        string? password = string.IsNullOrEmpty(_read.Password) ? null : _read.Password;
        bool addFolder = false;

        return Modify(archivePath,
            validate: zip =>
            {
                var (all, folders) = Index(zip);
                if (folder.Length > 0 && all.Contains(folder) && !folders.Contains(folder))
                    return OperationResult.Fail("대상 경로가 폴더가 아니라 파일입니다", folder);
                var existing = new HashSet<string>(all, StringComparer.OrdinalIgnoreCase);
                var clash = items
                    .Where(i => existing.Contains(i.EntryName.TrimEnd('/')))
                    .Select(i => $"이미 있음: {i.EntryName.TrimEnd('/')}")
                    .ToArray();
                if (clash.Length > 0)
                    return OperationResult.Fail("같은 이름의 항목이 이미 있습니다. 압축파일은 바뀌지 않았습니다", clash);
                if (zip.IsEncrypted)
                {
                    if (password is null)
                        return OperationResult.NeedPassword(ZipPasswordException.MissingMessage);
                    zip.CheckPassword(password);
                }
                addFolder = folder.Length > 0 && !folders.Contains(folder);
                return null;
            },
            write: (zip, writer) =>
            {
                CopyExisting(zip, writer, _ => false, _ => null, null, cancel);
                if (addFolder)
                    writer.AddDirectory(prefix, DateTime.Now);
                WriteItems(writer, items, new CreateOptions().Level, password, progress, cancel);
            },
            comment: null,
            successMessage: $"파일 {fileCount}개를 추가했습니다",
            cancel);
    }

    public OperationResult Delete(string archivePath, IReadOnlyList<string> entryPaths, IProgress<ProgressInfo>? progress = null, CancellationToken cancel = default)
    {
        var targets = entryPaths.Select(SafePaths.ToDisplayPath).Where(p => p.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (targets.Count == 0)
            return OperationResult.Fail("지울 항목이 없습니다");
        return Modify(archivePath,
            validate: zip =>
            {
                var all = Index(zip).All;
                var missing = targets.Where(t => !all.Contains(t)).Select(t => $"없는 항목: {t}").ToArray();
                return missing.Length > 0 ? OperationResult.Fail("압축파일에 없는 항목입니다", missing) : null;
            },
            write: (zip, writer) => CopyExisting(zip, writer,
                skip: e => targets.Any(t => SafePaths.IsSelfOrDescendant(SafePaths.ToDisplayPath(e.Name), t)),
                rename: _ => null, progress, cancel),
            comment: null,
            successMessage: $"항목 {targets.Count}개를 지웠습니다",
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

        return Modify(archivePath,
            validate: zip =>
            {
                var all = Index(zip).All;
                if (!all.Contains(old))
                    return OperationResult.Fail("압축파일에 없는 항목입니다", old);
                bool caseOnly = string.Equals(renamed, old, StringComparison.OrdinalIgnoreCase);
                if (!caseOnly && new HashSet<string>(all, StringComparer.OrdinalIgnoreCase).Contains(renamed))
                    return OperationResult.Fail("같은 이름의 항목이 이미 있습니다. 압축파일은 바뀌지 않았습니다", renamed);
                return null;
            },
            write: (zip, writer) => CopyExisting(zip, writer, skip: _ => false,
                rename: e =>
                {
                    string display = SafePaths.ToDisplayPath(e.Name);
                    if (!SafePaths.IsSelfOrDescendant(display, old))
                        return null;
                    string target = renamed + display[old.Length..];
                    return e.IsDirectory ? target + "/" : target;
                }, progress, cancel),
            comment: null,
            successMessage: $"'{old}' → '{renamed}'",
            cancel);
    }

    /// <summary>압축파일 설명을 UTF-8 로 쓴다(최대 65535 바이트). null/빈 문자열은 지운다. 분할 압축파일은 NotSupported.</summary>
    public OperationResult SetComment(string archivePath, string? comment, CancellationToken cancel = default)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(comment ?? "");
        if (bytes.Length > MaxCommentBytes)
            return OperationResult.Fail("설명이 너무 깁니다 (UTF-8 65535 바이트 이하)", $"{bytes.Length} 바이트");
        return Modify(archivePath,
            validate: _ => null,
            write: (zip, writer) => CopyExisting(zip, writer, _ => false, _ => null, null, cancel),
            comment: bytes,
            successMessage: bytes.Length == 0 ? "설명을 지웠습니다" : "설명을 저장했습니다",
            cancel);
    }
}
