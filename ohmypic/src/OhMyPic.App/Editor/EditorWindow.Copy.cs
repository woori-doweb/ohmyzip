using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using OhMyPic.App.Dialogs;
using OhMyPic.App.Text;
using OhMyPic.Core;
using OhMyPic.Core.Editing;

namespace OhMyPic.App.Editor;

/// <summary>
/// Capture mode and the clipboard commands (design P17). A capture-mode window edits a screenshot that has no file: its
/// name is the capture time, Export suggests the screenshot folder, and a successful copy hands the picture off like an
/// export (closing does not ask). Copy puts only the image on the clipboard (bitmap + PNG): with text or a file list
/// beside it, chat inputs may paste the text instead of the picture. Copy as file path is for inputs that take no images.
/// </summary>
public partial class EditorWindow
{
    private const int ClipboardAttempts = 5;
    private static readonly TimeSpan ClipboardRetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>Registered clipboard format name that image-aware programs read as PNG file bytes.</summary>
    private const string PngClipboardFormat = "PNG";

    /// <summary>
    /// Opens a capture-mode editor (no source file) for a screenshot taken at <paramref name="capturedAt"/>, shows it
    /// maximized on the monitor that contains the mouse cursor, activates it and returns it. Must be called on the UI thread.
    /// The window starts in crop (area select): a drag anywhere selects the area to keep. Once the crop is applied or
    /// cancelled the Pen tool is selected so the user can mark the picture up at once.
    /// </summary>
    /// <exception cref="ImageOperationException">
    /// <see cref="EditErrorCode.TooLarge"/> when the image is too large to edit; the error dialog has already been shown.
    /// </exception>
    public static EditorWindow OpenCapture(RasterImage image, DateTime capturedAt)
    {
        ArgumentNullException.ThrowIfNull(image);
        // Invariant: the format's ':' would otherwise become the culture's time separator.
        string name = string.Format(CultureInfo.InvariantCulture, EditorText.Capture_Name, capturedAt);
        if (!EditSession.CanEdit(image.Width, image.Height, out long estimate))
        {
            MessageDialog.Error(null, string.Format(EditorText.Open_FailedTitle, name), TooLargeText(image.Width, image.Height, estimate));
            throw new ImageOperationException(EditErrorCode.TooLarge,
                $"A {image.Width}x{image.Height} screenshot needs about {estimate:N0} bytes to edit; the limit is {EditSession.MaxEditBytes:N0}.");
        }
        // Placed on the cursor's monitor (the captured one) before it is shown, then maximized there.
        var window = new EditorWindow(null, capturedAt, name, new EditSession(image))
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            WindowState = WindowState.Maximized,
        };
        window.SourceInitialized += (_, _) => NativeMethods.MoveToCursorMonitor(window);
        window.Show();
        window.Activate();
        return window;
    }

    /// <summary>File mode: "name edited" beside the original. Capture mode: the next free screenshot name in the screenshot folder.</summary>
    private ExportDialog CreateExportDialog(RasterImage source, AdjustmentSettings settings)
    {
        if (_path is not null)
            return new ExportDialog(_path, Path.GetDirectoryName(_path),
                string.Format(EditorText.Export_DefaultName, Path.GetFileNameWithoutExtension(_path)), source, settings);
        string folder = ScreenshotFiles.DefaultFolder;
        string baseName = Path.GetFileNameWithoutExtension(ScreenshotFiles.NextPath(folder, _capturedAt ?? DateTime.Now));
        return new ExportDialog(null, folder, baseName, source, settings);
    }

    /// <summary>Copies the full result (adjustments applied) to the clipboard as a bitmap and as PNG bytes.</summary>
    private async Task CopyImageAsync()
    {
        CommitAdjustments();
        var source = _session.Base;
        var settings = _session.Settings;
        SetBusy(true);
        (BitmapSource Bitmap, MemoryStream Png) copied;
        try
        {
            copied = await Task.Run(() =>
            {
                var bitmap = Adjustments.Apply(source, settings).ToBitmap();
                var png = new MemoryStream();
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(png);
                png.Position = 0;
                return (bitmap, png);
            });
        }
        finally
        {
            SetBusy(false);
        }
        var data = new DataObject();
        data.SetImage(copied.Bitmap);
        data.SetData(PngClipboardFormat, copied.Png);
        if (!await SetClipboardAsync(() => Clipboard.SetDataObject(data, copy: true))) return;
        MarkHandedOff(source, settings);
        ShowNotice(string.Format(EditorText.Status_CopiedImage, copied.Bitmap.PixelWidth, copied.Bitmap.PixelHeight));
    }

    /// <summary>
    /// Copies a full file path as text: the opened file itself while it is unchanged, otherwise the result saved as a new
    /// PNG in the screenshot folder (named after the capture time, or the current time in file mode).
    /// </summary>
    private async Task CopyPathAsync()
    {
        CommitAdjustments();
        var source = _session.Base;
        var settings = _session.Settings;
        string path;
        if (_path is not null && !IsDirty)
        {
            path = Path.GetFullPath(_path);
        }
        else
        {
            string folder = ScreenshotFiles.DefaultFolder;
            var time = _capturedAt ?? DateTime.Now;
            string name = Path.GetFileName(ScreenshotFiles.NextPath(folder, time));
            SetBusy(true);
            try
            {
                path = await Task.Run(() => ScreenshotFiles.SavePng(Adjustments.Apply(source, settings), folder, time));
            }
            catch (Exception ex) when (ex is ImageOperationException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                SetBusy(false);
                MessageDialog.Error(this, string.Format(EditorText.Export_FailedTitle, name), MainWindow.DescribeIoError(ex));
                return;
            }
            SetBusy(false);
        }
        if (!await SetClipboardAsync(() => Clipboard.SetText(path))) return;
        MarkHandedOff(source, settings);
        ShowNotice(string.Format(EditorText.Status_CopiedPath, path));
    }

    /// <summary>
    /// Capture mode: a copied picture has been handed off like an export, so closing does not ask about it. File mode keeps
    /// its dirty state (the opened file is still unchanged).
    /// </summary>
    private void MarkHandedOff(RasterImage source, AdjustmentSettings settings)
    {
        if (_path is not null) return;
        _savedBase = source;
        _savedSettings = settings;
        UpdateState();
    }

    /// <summary>
    /// Runs a clipboard write, retrying while another program holds the clipboard open; the last failure is reported with
    /// the clipboard error dialog. True when the write succeeded.
    /// </summary>
    private async Task<bool> SetClipboardAsync(Action write)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                write();
                return true;
            }
            catch (COMException) when (attempt < ClipboardAttempts)
            {
                await Task.Delay(ClipboardRetryDelay);
            }
            catch (ExternalException ex)
            {
                MessageDialog.Error(this, MainText.Clipboard_FailedTitle,
                    string.Format(MainText.Error_ReasonWithDetail, MainText.Clipboard_FailedReason, ex.Message));
                return false;
            }
        }
    }
}
