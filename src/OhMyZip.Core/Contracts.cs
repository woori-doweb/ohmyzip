namespace OhMyZip.Core;

/// <summary>압축파일 형식. 엔진이 실제로 시험을 통과한 기능만 <see cref="FormatCapabilities"/> 에 true 로 둔다.</summary>
public enum ArchiveFormat { Zip, SevenZip, Tar, GZip, TarGz, Rar, Unknown }

/// <summary>
/// 형식·엔진이 실제로 지원하는 작업. UI 의 명령 활성화와 새 압축 옵션은 이 값만 본다.
/// Encrypt = 만들 때 암호화, Split = 만들 때 분할, Comment = 압축파일 설명 읽기/쓰기.
/// </summary>
public sealed record FormatCapabilities(bool Read, bool Test, bool Extract, bool Create, bool Modify,
    bool Encrypt, bool Split, bool Comment);

/// <summary>압축파일 안의 항목 하나. Path 는 항상 '/' 구분, 앞 '/' 없음, 폴더는 IsDirectory=true(끝 '/' 없음).</summary>
public sealed record ArchiveEntry(
    string Path,
    bool IsDirectory,
    long Size,
    long CompressedSize,
    DateTimeOffset? Modified,
    uint? Crc32,
    string? Method,
    bool Encrypted);

/// <summary>PasswordRequired = 암호가 없거나 틀림(메시지로 구분). UI 는 암호를 물어 같은 작업을 다시 시도한다.</summary>
public enum OperationStatus { Succeeded, Failed, Cancelled, NotSupported, PasswordRequired }

/// <summary>모든 엔진 작업의 결과. 예상 가능한 실패(형식 오류·경로 탈출·한도 초과·미지원·암호)는 예외가 아니라 이 값으로 돌려준다.</summary>
public sealed record OperationResult(OperationStatus Status, string Message, IReadOnlyList<string> Problems)
{
    public static OperationResult Ok(string message = "") => new(OperationStatus.Succeeded, message, []);
    public static OperationResult Fail(string message, params string[] problems) => new(OperationStatus.Failed, message, problems);
    public static OperationResult Unsupported(string message) => new(OperationStatus.NotSupported, message, []);
    public static OperationResult Cancelled(string message = "취소됨") => new(OperationStatus.Cancelled, message, []);
    public static OperationResult NeedPassword(string message) => new(OperationStatus.PasswordRequired, message, []);
    public bool Succeeded => Status == OperationStatus.Succeeded;
}

/// <summary>Comment 는 압축파일 설명(ZIP 주석). 없거나 형식이 지원하지 않으면 null.</summary>
public sealed record ListResult(OperationResult Result, IReadOnlyList<ArchiveEntry> Entries, string? Comment = null);

/// <summary>대상 폴더에 같은 이름의 파일이 있을 때. 기본은 덮어쓰지 않는다(Skip).</summary>
public enum ConflictPolicy { Skip, Overwrite, Rename }

public sealed record ExtractOptions
{
    public ConflictPolicy Conflict { get; init; } = ConflictPolicy.Skip;
    /// <summary>해제 총량 한도(바이트). 압축 폭탄 방어.</summary>
    public long MaxTotalBytes { get; init; } = 8L * 1024 * 1024 * 1024;
    public int MaxEntries { get; init; } = 200_000;
    /// <summary>null 이면 전체, 아니면 이 경로들(과 하위 항목)만.</summary>
    public IReadOnlyCollection<string>? Only { get; init; }
}

public enum EncryptionMethod { None, Aes256 }

/// <summary>새 압축 옵션. 엔진은 지원하지 않는 조합을 조용히 무시하지 않고 NotSupported 로 돌려준다.</summary>
public sealed record CreateOptions
{
    public ArchiveFormat Format { get; init; } = ArchiveFormat.Zip;
    /// <summary>0 = 저장(무압축) … 9 = 최고 압축. 엔진별 실제 인자 매핑은 각 엔진 주석과 시험에 있다.</summary>
    public int Level { get; init; } = 5;
    /// <summary>null 또는 빈 문자열이면 암호 없음. 로그·예외 메시지에 넣지 않는다.</summary>
    public string? Password { get; init; }
    public EncryptionMethod Encryption { get; init; } = EncryptionMethod.None;
    /// <summary>7Z 만: 파일 목록(헤더)까지 암호화.</summary>
    public bool EncryptNames { get; init; }
    /// <summary>null 이면 분할하지 않음. 7Z: name.7z.001…, ZIP: name.z01…name.zip.</summary>
    public long? VolumeSize { get; init; }
}

/// <summary>열기 옵션. 엔진 인스턴스에 묶인다(<see cref="Archives.For(string, ReadOptions?)"/>).</summary>
public sealed record ReadOptions
{
    /// <summary>암호. 로그·예외 메시지에 넣지 않는다. 같은 엔진으로 Add 하면 새 항목도 이 암호(AES-256)로 암호화한다.</summary>
    public string? Password { get; init; }
    /// <summary>ZIP 이름 코드페이지 강제(예: 949, 932, 936, 65001). null 이면 자동(UTF-8 플래그 → UTF-8 유효성 → CP949).</summary>
    public int? CodePage { get; init; }
}

public sealed record ProgressInfo(long ProcessedBytes, long TotalBytes, string? CurrentPath);

public interface IArchiveEngine
{
    FormatCapabilities Capabilities { get; }
    ListResult List(string archivePath, CancellationToken cancel = default);
    OperationResult Test(string archivePath, IProgress<ProgressInfo>? progress = null, CancellationToken cancel = default);
    OperationResult Extract(string archivePath, string destinationDirectory, ExtractOptions options, IProgress<ProgressInfo>? progress = null, CancellationToken cancel = default);
    /// <summary>sources 는 파일 또는 폴더 경로. 폴더는 이름째(빈 폴더 포함) 들어간다. 기존 archivePath 가 있으면 실패. options=null 이면 기본값.</summary>
    OperationResult Create(string archivePath, IReadOnlyList<string> sources, CreateOptions? options = null, IProgress<ProgressInfo>? progress = null, CancellationToken cancel = default);
    /// <summary>targetFolder 는 압축파일 안 폴더 경로("" = 루트). 실패·취소하면 원본 압축파일 바이트는 바뀌지 않는다.</summary>
    OperationResult Add(string archivePath, IReadOnlyList<string> sources, string targetFolder = "", IProgress<ProgressInfo>? progress = null, CancellationToken cancel = default);
    OperationResult Delete(string archivePath, IReadOnlyList<string> entryPaths, IProgress<ProgressInfo>? progress = null, CancellationToken cancel = default);
    OperationResult Rename(string archivePath, string entryPath, string newName, IProgress<ProgressInfo>? progress = null, CancellationToken cancel = default);
    /// <summary>압축파일 설명 쓰기. null/빈 문자열은 설명 삭제. Capabilities.Comment=false 면 NotSupported.</summary>
    OperationResult SetComment(string archivePath, string? comment, CancellationToken cancel = default);
}

// 정적 클래스 Archives(Detect/For/CapabilitiesOf) 구현은 Archives.cs 에 있다.
