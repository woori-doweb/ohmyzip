using System.Globalization;
using System.Windows.Media.Imaging;
using OhMyPic.Core.Editing;

namespace OhMyPic.Core;

/// <summary>
/// Where "copy path" puts capture PNGs. Names carry the capture time so a pasted path tells when the shot was taken,
/// and a counter keeps two shots in the same second apart.
/// </summary>
public static class ScreenshotFiles
{
    /// <summary>Environment variable that moves the screenshot folder elsewhere; used by tests.</summary>
    public const string FolderVariable = "OHMYPIC_SCREENSHOT_DIR";

    private const string NamePrefix = "ohMypic-";
    private const string Extension = ".png";

    /// <summary>The <see cref="FolderVariable"/> folder when set and non-empty, otherwise Pictures\Screenshots.</summary>
    public static string DefaultFolder =>
        Environment.GetEnvironmentVariable(FolderVariable) is { Length: > 0 } dir
            ? Path.GetFullPath(dir)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");

    /// <summary>
    /// Full path "folder\ohMypic-yyyyMMdd-HHmmss.png" (invariant culture and calendar); when that name is taken,
    /// "-2", "-3", ... is inserted before ".png".
    /// </summary>
    public static string NextPath(string folder, DateTime time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        string full = Path.GetFullPath(folder);
        string stem = NamePrefix + time.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string path = Path.Combine(full, stem + Extension);
        for (int n = 2; Path.Exists(path); n++)
            path = Path.Combine(full, stem + "-" + n.ToString(CultureInfo.InvariantCulture) + Extension);
        return path;
    }

    /// <summary>
    /// Creates the folder, writes the image as PNG to <see cref="NextPath"/> through <see cref="AtomicFile"/> and returns
    /// the full path. The temp file is re-decoded and its size checked before it gets the final name, so a path handed
    /// out always points to a complete image.
    /// </summary>
    /// <exception cref="IOException">The written file does not decode to the image size, or the write failed.</exception>
    /// <exception cref="ImageOperationException"><see cref="EditErrorCode.EncodeFailed"/> when PNG encoding fails.</exception>
    public static string SavePng(RasterImage image, string folder, DateTime time)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        Directory.CreateDirectory(folder);
        string path = NextPath(folder, time);
        byte[] bytes = EditExport.Encode(image, ExportFormat.Png);
        AtomicFile.WriteAllBytes(path, bytes, temp => Verify(temp, image.Width, image.Height));
        return path;
    }

    private static void Verify(string path, int width, int height)
    {
        int w, h;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            BitmapFrame frame = decoder.Frames[0];
            (w, h) = (frame.PixelWidth, frame.PixelHeight);
        }
        if (w != width || h != height)
            throw new IOException($"Written file decodes as {w}x{h}, expected {width}x{height}.");
    }
}
