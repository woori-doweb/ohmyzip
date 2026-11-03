using System.Globalization;
using System.IO;
using OhMyZip.Core;

namespace OhMyZip.App.Models;

/// <summary>열린 압축파일 하나: 엔진 목록 결과를 폴더 구조로 색인한다. 명시 폴더 항목이 없는 경로도 폴더로 만든다.</summary>
public sealed class ArchiveModel
{
    public string FilePath { get; }
    public string FileName => Path.GetFileName(FilePath);
    public ArchiveFormat Format { get; }
    /// <summary>열 때 쓴 엔진. 암호를 받으면 그 암호에 묶인 엔진으로 바뀐다(<see cref="UsePassword"/>).</summary>
    public IArchiveEngine Engine { get; private set; }
    /// <summary>엔진에 묶인 열기 옵션(암호 포함). 메모리에만 두고 닫으면 버린다.</summary>
    public ReadOptions? Read { get; private set; }
    public IReadOnlyList<ArchiveEntry> Entries { get; }
    /// <summary>압축파일 설명(ListResult.Comment). 없으면 null.</summary>
    public string? Comment { get; }
    public long ArchiveSize { get; }
    public int FileCount { get; }
    /// <summary>압축파일에 기록된 폴더 항목 수. 경로에서만 생기는 상위 폴더는 트리에는 보이지만 세지 않는다(DEF-02).</summary>
    public int FolderCount => Entries.Count(e => e.IsDirectory && e.Path.Trim('/').Length > 0);

    private readonly Dictionary<string, Folder> _folders = new(StringComparer.Ordinal);

    private sealed class Folder(string path)
    {
        public string Path { get; } = path;
        public ArchiveEntry? Entry;
        public readonly SortedSet<string> SubFolders = new(StringComparer.CurrentCultureIgnoreCase);
        public readonly List<ArchiveEntry> Files = [];
    }

    public ArchiveModel(string filePath, ArchiveFormat format, IArchiveEngine engine, ReadOptions? read, IReadOnlyList<ArchiveEntry> entries, string? comment)
    {
        FilePath = filePath;
        Format = format;
        Engine = engine;
        Read = read;
        Entries = entries;
        Comment = comment;
        ArchiveSize = new FileInfo(filePath).Length;
        _folders[""] = new Folder("");
        int files = 0;
        foreach (var e in entries)
        {
            var path = e.Path.Trim('/');
            if (path.Length == 0) continue;
            if (e.IsDirectory)
            {
                EnsureFolder(path).Entry = e;
            }
            else
            {
                EnsureFolder(Parent(path)).Files.Add(e);
                files++;
            }
        }
        FileCount = files;
    }

    private Folder EnsureFolder(string path)
    {
        if (_folders.TryGetValue(path, out var f)) return f;
        f = new Folder(path);
        _folders[path] = f;
        var parent = EnsureFolder(Parent(path));
        parent.SubFolders.Add(path);
        return f;
    }

    public static string Parent(string path)
    {
        int i = path.LastIndexOf('/');
        return i < 0 ? "" : path[..i];
    }

    public static string LeafName(string path)
    {
        int i = path.LastIndexOf('/');
        return i < 0 ? path : path[(i + 1)..];
    }

    public void UsePassword(IArchiveEngine engine, ReadOptions read)
    {
        Engine = engine;
        Read = read;
    }

    public bool FolderExists(string path) => _folders.ContainsKey(path);

    /// <summary>찾기용: 모든 폴더(경로로만 생기는 폴더 포함)와 파일의 전체 경로.</summary>
    public IEnumerable<(string Path, bool IsDirectory)> AllPaths()
    {
        foreach (var path in _folders.Keys)
            if (path.Length > 0) yield return (path, true);
        foreach (var f in _folders.Values)
            foreach (var e in f.Files) yield return (e.Path, false);
    }

    /// <summary>최상위 항목(첫 경로 조각)의 개수. 알아서 풀기가 쓴다.</summary>
    public int TopLevelCount => _folders[""].SubFolders.Count + _folders[""].Files.Count;

    public IEnumerable<string> SubFolders(string path) =>
        _folders.TryGetValue(path, out var f) ? f.SubFolders : [];

    /// <summary>한 폴더의 직속 항목(폴더 먼저 정렬은 목록 쪽에서).</summary>
    public List<EntryRow> RowsIn(string folder)
    {
        var rows = new List<EntryRow>();
        if (!_folders.TryGetValue(folder, out var f)) return rows;
        foreach (var sub in f.SubFolders)
        {
            var subFolder = _folders[sub];
            rows.Add(EntryRow.ForFolder(sub, subFolder.Entry));
        }
        foreach (var e in f.Files)
            rows.Add(EntryRow.ForFile(e));
        return rows;
    }

    /// <summary>선택 경로(폴더면 하위 전체)에 해당하는 파일 항목들. only 가 null 이면 전체 파일.</summary>
    public IEnumerable<ArchiveEntry> FilesUnder(IReadOnlyCollection<string>? only)
    {
        foreach (var e in Entries)
        {
            if (e.IsDirectory) continue;
            if (only is null) { yield return e; continue; }
            foreach (var p in only)
            {
                if (e.Path == p || e.Path.StartsWith(p + "/", StringComparison.Ordinal)) { yield return e; break; }
            }
        }
    }
}

/// <summary>파일 목록 한 행. 문자열 칼럼은 표시용, 정렬은 원시 값으로.</summary>
public sealed class EntryRow
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public bool IsDirectory { get; init; }
    public long? Size { get; init; }
    public long? CompressedSize { get; init; }
    public DateTimeOffset? Modified { get; init; }
    public uint? Crc32 { get; init; }
    public string? Method { get; init; }
    public bool Encrypted { get; init; }
    public required string TypeName { get; init; }

    public string Glyph => IsDirectory ? "\uE8B7" : "\uE8A5";
    public string SizeText => Size is { } s ? s.ToString("N0", CultureInfo.CurrentCulture) : "";
    public string CompressedSizeText => CompressedSize is { } s ? s.ToString("N0", CultureInfo.CurrentCulture) : "";
    public string ModifiedText => Modified is { } m ? m.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "";
    public string CrcText => Crc32 is { } c ? c.ToString("X8", CultureInfo.InvariantCulture) : "";
    public string MethodText => Method ?? "";
    /// <summary>엔진은 '암호 걸림' 여부와 압축 방식만 알려 준다. 그 이상(ZipCrypto/AES 구분)은 지어내지 않는다.</summary>
    public string EncryptionText => !Encrypted ? "" : string.IsNullOrEmpty(Method) ? "암호화됨" : $"암호화됨 ({Method})";
    // 속성·파일 설명·부가 정보: 엔진 계약(ArchiveEntry)에 값이 없으므로 빈 칸. 가짜 값을 만들지 않는다.
    public string AttributesText => "";
    public string DescriptionText => "";
    public string ExtraText => "";

    public static EntryRow ForFolder(string path, ArchiveEntry? entry) => new()
    {
        Name = ArchiveModel.LeafName(path),
        Path = path,
        IsDirectory = true,
        Modified = entry?.Modified,
        TypeName = FileTypeNames.Folder,
    };

    public static EntryRow ForFile(ArchiveEntry e) => new()
    {
        Name = ArchiveModel.LeafName(e.Path),
        Path = e.Path,
        IsDirectory = false,
        Size = e.Size,
        CompressedSize = e.CompressedSize,
        Modified = e.Modified,
        Crc32 = e.Crc32,
        Method = e.Method,
        Encrypted = e.Encrypted,
        TypeName = FileTypeNames.ForFileName(ArchiveModel.LeafName(e.Path)),
    };

    public override string ToString() => Name;
}
