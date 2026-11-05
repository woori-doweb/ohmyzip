using System.Windows.Media.Imaging;
using OhMyPic.Core;

namespace OhMyPic.App.Dialogs.Edit;

/// <summary>Background loading for the small previews in the edit dialogs. Failures yield null; the dialog shows "No preview".</summary>
internal static class EditPreview
{
    /// <summary>Longest side, in pixels, decoded for a preview.</summary>
    public const int PreviewSide = 400;

    /// <summary>Stored pixels (not EXIF-rotated) plus the EXIF orientation, so the panel can show both auto-rotate states.</summary>
    public static Task<(BitmapSource Image, int Orientation)?> LoadAsync(string path, int maxSide) => Task.Run<(BitmapSource Image, int Orientation)?>(() =>
    {
        try
        {
            var info = ImageLoader.ReadInfo(path);
            var image = ImageLoader.LoadForDisplay(path, autoRotate: false, maxDecodeSide: maxSide);
            if (image.CanFreeze && !image.IsFrozen) image.Freeze();
            return (image, info.Orientation);
        }
        catch (ImageOperationException)
        {
            return null;
        }
    });

    public static Task<ImageInfo?> ReadInfoAsync(string path) => Task.Run<ImageInfo?>(() =>
    {
        try
        {
            return ImageLoader.ReadInfo(path);
        }
        catch (ImageOperationException)
        {
            return null;
        }
    });
}

/// <summary>
/// Last-used options of each edit dialog for the current session only (nothing is written to settings).
/// Output options are kept per dialog because each one has its own default text.
/// </summary>
internal static class EditSession
{
    public static RotateOptions? Rotate;
    public static ResizeOptions? Resize;
    public static ConvertOptions? Convert;
    public static RenameRule? Rename;
    public static OutputOptions? RotateOutput;
    public static OutputOptions? ResizeOutput;
    public static OutputOptions? ConvertOutput;
    public static OutputOptions? BatchOutput;
    public static ResizeOptions? BatchResize;
    public static RotateOptions? BatchRotate;
    public static ConvertOptions? BatchConvert;

    /// <summary>Batch task cards: resize, rotate, rename, convert. All on until the user changes them.</summary>
    public static bool[] BatchTasks = [true, true, true, true];

    /// <summary>Default output for a fresh dialog: same folder, ask on duplicates, keep the original.</summary>
    public static OutputOptions DefaultOutput() => new(Conflict: ConflictPolicy.Ask);
}
