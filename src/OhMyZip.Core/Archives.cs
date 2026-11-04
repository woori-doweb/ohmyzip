using System.IO.Compression;
using OhMyZip.Core.Resources;

namespace OhMyZip.Core;

/// <summary>형식 판별과 엔진 선택. UI 는 이것만 쓴다.</summary>
public static class Archives
{
    private static readonly byte[] ZipLocalHeader = "PK\x03\x04"u8.ToArray();
    private static readonly byte[] ZipEmptyArchive = "PK\x05\x06"u8.ToArray();
    /// <summary>분할 ZIP 첫 조각(.z01)의 시작 표식(APPNOTE 8.5.3).</summary>
    private static readonly byte[] ZipSpanMarker = "PK\x07\x08"u8.ToArray();
    /// <summary>분할을 요청했지만 한 조각에 다 들어간 ZIP 의 시작 표식 'PK00'(APPNOTE 8.5.4). 뒤는 일반 ZIP 이다.</summary>
    private static readonly byte[] ZipSingleSegmentMarker = "PK00"u8.ToArray();
    private static readonly byte[] SevenZipSignature = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];
    private static readonly byte[] RarSignature = "Rar!\x1A\x07"u8.ToArray();
    private static readonly byte[] GZipSignature = [0x1F, 0x8B];
    private static readonly byte[] TarMagic = "ustar"u8.ToArray();

    /// <summary>
    /// 앞 바이트(매직)로 판별한다. 확장자는 보지 않는다. 예외: 분할 ZIP 의 마지막 조각(name.zip)은 앞이 데이터라
    /// 같은 폴더의 name.z01 이 분할 표식으로 시작하면 ZIP 으로 본다. 없는 파일·읽기 오류는 Unknown.
    /// </summary>
    public static ArchiveFormat Detect(string archivePath)
    {
        byte[] head = new byte[512];
        int read;
        try
        {
            using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            read = fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return ArchiveFormat.Unknown;
        }
        ReadOnlySpan<byte> h = head.AsSpan(0, read);
        if (h.StartsWith(ZipLocalHeader) || h.StartsWith(ZipEmptyArchive) || h.StartsWith(ZipSpanMarker) || h.StartsWith(ZipSingleSegmentMarker))
            return ArchiveFormat.Zip;
        if (h.StartsWith(SevenZipSignature))
            return ArchiveFormat.SevenZip;
        if (h.StartsWith(RarSignature))
            return ArchiveFormat.Rar;
        if (h.StartsWith(GZipSignature))
            return IsTarInsideGZip(archivePath) ? ArchiveFormat.TarGz : ArchiveFormat.GZip;
        if (read >= 262 && h.Slice(257, 5).SequenceEqual(TarMagic))
            return ArchiveFormat.Tar;
        if (IsLastZipVolume(archivePath))
            return ArchiveFormat.Zip;
        return ArchiveFormat.Unknown;
    }

    private static bool IsTarInsideGZip(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            byte[] block = new byte[512];
            int n = gz.ReadAtLeast(block, block.Length, throwOnEndOfStream: false);
            return n >= 262 && block.AsSpan(257, 5).SequenceEqual(TarMagic);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsLastZipVolume(string path)
    {
        if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return false;
        string first = path[..^4] + ".z01";
        try
        {
            if (!File.Exists(first))
                return false;
            using var fs = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> marker = stackalloc byte[4];
            return fs.ReadAtLeast(marker, 4, throwOnEndOfStream: false) == 4 && marker.SequenceEqual(ZipSpanMarker);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static IArchiveEngine For(ArchiveFormat format, ReadOptions? read = null) => format switch
    {
        ArchiveFormat.Zip => new ZipEngine(read),
        ArchiveFormat.SevenZip => new SevenZipEngine(read),
        ArchiveFormat.Tar or ArchiveFormat.GZip or ArchiveFormat.TarGz => new TarEngine(format, read),
        ArchiveFormat.Rar => new RarReadEngine(read),
        _ => new UnknownFormatEngine(),
    };

    public static IArchiveEngine For(string archivePath, ReadOptions? read = null) => For(Detect(archivePath), read);

    /// <summary>새 압축 대화상자가 형식별 옵션을 켜고 끌 때 쓴다.</summary>
    public static FormatCapabilities CapabilitiesOf(ArchiveFormat format) => For(format).Capabilities;

    /// <summary>새로 만들 때 붙이는 확장자(점 포함).</summary>
    public static string DefaultExtension(ArchiveFormat format) => format switch
    {
        ArchiveFormat.Zip => ".zip",
        ArchiveFormat.SevenZip => ".7z",
        ArchiveFormat.Tar => ".tar",
        ArchiveFormat.GZip => ".gz",
        ArchiveFormat.TarGz => ".tar.gz",
        ArchiveFormat.Rar => ".rar",
        _ => "",
    };
}

/// <summary>판별하지 못한 파일용 엔진. 어떤 작업도 예외 없이 실패 결과를 돌려준다.</summary>
internal sealed class UnknownFormatEngine : IArchiveEngine
{
    private static OperationResult Failure() => OperationResult.Fail(EngineText.Archives_UnknownFormat);

    public FormatCapabilities Capabilities { get; } = new(false, false, false, false, false, false, false, false);

    public ListResult List(string archivePath, CancellationToken cancel = default) => new(Failure(), []);

    public OperationResult Test(string archivePath, IProgress<ProgressInfo>? progress = null, CancellationToken cancel = default) => Failure();

    public OperationResult Extract(string archivePath, string destinationDirectory, ExtractOptions options, IProgress<ProgressInfo>? progress = null, CancellationToken cancel = default) => Failure();

    public OperationResult Create(string archivePath, IReadOnlyList<string> sources, CreateOptions? options = null, IProgress<ProgressInfo>? progress = null, CancellationToken cancel = default) => Failure();

    public OperationResult Add(string archivePath, IReadOnlyList<string> sources, string targetFolder = "", IProgress<ProgressInfo>? progress = null, CancellationToken cancel = default) => Failure();

    public OperationResult Delete(string archivePath, IReadOnlyList<string> entryPaths, IProgress<ProgressInfo>? progress = null, CancellationToken cancel = default) => Failure();

    public OperationResult Rename(string archivePath, string entryPath, string newName, IProgress<ProgressInfo>? progress = null, CancellationToken cancel = default) => Failure();

    public OperationResult SetComment(string archivePath, string? comment, CancellationToken cancel = default) => Failure();
}
