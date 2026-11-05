namespace OhMyPic.Core;

/// <summary>Formats the engine can write. Every one of them can also be read.</summary>
public enum ImageFormatKind
{
    Bmp,
    Gif,
    Jpeg,
    Tiff,
    Png,
}

/// <summary>One camera/EXIF field. Keys are stable: see <see cref="MetadataKeys"/>.</summary>
public sealed record MetadataItem(string Key, string Value);

/// <summary>Stable keys used in <see cref="ImageInfo.Camera"/>.</summary>
public static class MetadataKeys
{
    public const string CameraMake = "CameraMake";
    public const string CameraModel = "CameraModel";
    public const string DateTaken = "DateTaken";
    public const string ExposureTime = "ExposureTime";
    public const string FNumber = "FNumber";
    public const string IsoSpeed = "IsoSpeed";
    public const string FocalLength = "FocalLength";
}

/// <summary>Header-level facts about an image file. Width/Height are the stored (not EXIF-rotated) size.</summary>
public sealed record ImageInfo(
    int Width,
    int Height,
    int FrameCount,
    string FormatName,
    double DpiX,
    double DpiY,
    int BitsPerPixel,
    bool HasAlpha,
    int Orientation,
    IReadOnlyList<MetadataItem> Camera)
{
    /// <summary>Value of a camera field, or null when the file does not carry it.</summary>
    public string? Get(string key) => Camera.FirstOrDefault(m => m.Key == key)?.Value;
}

public enum RotateFlip
{
    None,
    Rotate90,
    Rotate180,
    Rotate270,
    FlipHorizontal,
    FlipVertical,
}

public enum ResizeMode
{
    Percent,
    Pixels,
    LongAxis,
    TargetFileSize,
}

/// <summary>
/// Resize step. Percent uses <see cref="Percent"/>; Pixels uses Width/Height (+KeepAspect); LongAxis uses
/// <see cref="LongAxis"/>; TargetFileSize uses <see cref="TargetKilobytes"/> (1 KB = 1024 bytes) and is applied
/// when the final file is encoded.
/// </summary>
public sealed record ResizeOptions(
    ResizeMode Mode,
    double Percent = 100,
    int Width = 0,
    int Height = 0,
    bool KeepAspect = true,
    int LongAxis = 0,
    int TargetKilobytes = 0);

public enum FrameMode
{
    /// <summary>Only the first frame/page is processed.</summary>
    FirstOnly,

    /// <summary>Each frame/page becomes its own file (<c>name_1</c>, <c>name_2</c>, ...).</summary>
    SplitEach,

    /// <summary>All frames go into one multi-frame file. Only valid for TIFF and GIF targets.</summary>
    CombineOne,
}

public sealed record ConvertOptions(ImageFormatKind Format, int JpegQuality = 85, FrameMode Frames = FrameMode.FirstOnly);

/// <summary>Rotate step. When <see cref="AutoRotateByExif"/> is set the EXIF orientation is applied first.</summary>
public sealed record RotateOptions(RotateFlip Operation, bool AutoRotateByExif = true);

public enum OutputFolderMode
{
    SameFolder,
    SubFolder,
    Specified,
}

public enum ConflictPolicy
{
    Ask,
    Skip,
    Overwrite,
}

public enum ConflictDecision
{
    Skip,
    Overwrite,

    /// <summary>Append <c> (2)</c>, <c> (3)</c>, ... to the file name until it is free.</summary>
    Rename,
}

public sealed record OutputOptions(
    OutputFolderMode Folder = OutputFolderMode.SameFolder,
    string? FolderPath = null,
    string SubFolderName = "edited",
    string? Prefix = null,
    string? Suffix = null,
    ConflictPolicy Conflict = ConflictPolicy.Skip,
    bool DeleteOriginal = false);

public enum NameCase
{
    Keep,
    Lower,
    Upper,
}

/// <summary>
/// One batch edit. Steps always run in the order resize → rotate → rename → convert; any step may be null.
/// </summary>
public sealed record EditJob(
    IReadOnlyList<string> Inputs,
    OutputOptions Output,
    ResizeOptions? Resize = null,
    RotateOptions? Rotate = null,
    RenameRule? Rename = null,
    ConvertOptions? Convert = null);

public enum ItemStatus
{
    Written,
    Skipped,
    Failed,
}

public enum EditErrorCode
{
    None,
    UnsupportedFormat,
    DecodeFailed,
    EncodeFailed,
    TargetSizeUnreachable,
    TooLarge,
    IoError,
    Cancelled,
}

public sealed record ItemResult(
    string Input,
    IReadOnlyList<string> Outputs,
    ItemStatus Status,
    EditErrorCode Error,
    string? Detail,
    bool LosslessJpegRotation);

public sealed record JobResult(IReadOnlyList<ItemResult> Items)
{
    public int WrittenCount => Items.Count(i => i.Status == ItemStatus.Written);

    public int SkippedCount => Items.Count(i => i.Status == ItemStatus.Skipped);

    public int FailedCount => Items.Count(i => i.Status == ItemStatus.Failed);

    /// <summary>True when the job stopped early because cancellation was requested.</summary>
    public bool Cancelled => Items.Any(i => i.Error == EditErrorCode.Cancelled);
}

public sealed record ConflictInfo(string Input, string ProposedOutput);

public sealed record JobProgress(int Done, int Total, string Current);

public enum SortKey
{
    Name,
    Type,
    Size,
    Date,
}

public sealed record ImageEntry(string Path, string Name, string Extension, long Size, DateTime Modified);

/// <summary>Typed failure from the loader/encoder (decode error, pixel-count guard, unreachable target size, ...).</summary>
public sealed class ImageOperationException(EditErrorCode code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public EditErrorCode Code { get; } = code;
}
