using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using OhMyPic.App.Dialogs;
using OhMyPic.App.Dialogs.Edit;
using OhMyPic.App.Settings;
using OhMyPic.App.Text;
using OhMyPic.Core;

namespace OhMyPic.App;

/// <summary>File commands: open, save as, print, properties, clipboard, delete, rename, edit dialogs, help.</summary>
public partial class MainWindow
{
    private const string PreferredDropEffect = "Preferred DropEffect";
    private const int DropEffectCopy = 1;
    private const int DropEffectMove = 2;
    private const int DropEffectLink = 4;

    // ───────────── open ─────────────

    private async Task OpenFileAsync()
    {
        var patterns = string.Join(";", ImageFormats.ReadableExtensions.Select(e => "*" + e));
        var dlg = new OpenFileDialog
        {
            Title = MenuText.Label_OpenFile,
            Filter = $"{MainText.Filter_Images}|{patterns}|{MainText.Filter_AllFiles}|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (CurrentFolder is { } folder && Directory.Exists(folder)) dlg.InitialDirectory = folder;
        if (dlg.ShowDialog(this) != true) return;
        if (InViewer && !await ResolvePendingRotationAsync()) return;
        await OpenFileInViewerAsync(dlg.FileName);
    }

    private async Task OpenFolderAsync()
    {
        var dlg = new OpenFolderDialog { Title = MenuText.Label_OpenFolder, Multiselect = false };
        if (CurrentFolder is { } folder && Directory.Exists(folder)) dlg.InitialDirectory = folder;
        if (dlg.ShowDialog(this) != true) return;
        if (InViewer && !await LeaveViewerAsync()) return;
        await NavigateToAsync(dlg.FolderName);
        FocusList();
    }

    // ───────────── save as, print, copy image ─────────────

    /// <summary>The image a single-image command works on: the displayed one (with view rotation), or the selected file.</summary>
    private async Task<(BitmapSource? Bitmap, string? Path)> SingleImageAsync()
    {
        if (InViewer) return (RenderedBitmap(), CurrentPath);
        var items = SelectedItems();
        if (items.Count != 1) return (null, null);
        var path = items[0].Path;
        bool autoRotate = S.AutoRotateByExif;
        try
        {
            var bitmap = await Task.Run(() => ImageLoader.LoadForDisplay(path, autoRotate));
            return (bitmap, path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            MessageDialog.Error(this, string.Format(MainText.Error_CannotOpenTitle, Path.GetFileName(path)), DescribeLoadError(ex));
            return (null, null);
        }
    }

    private async Task SaveAsAsync()
    {
        var (bitmap, source) = await SingleImageAsync();
        if (bitmap is null || source is null) return;
        var kinds = ImageFormats.WritableFormats;
        var current = ImageFormats.FromExtension(Path.GetExtension(source)) ?? ImageFormatKind.Png;
        var filter = string.Join("|", kinds.Select(k => $"{FormatLabel(k)}|*{string.Join(";*", ExtensionsOf(k))}"));
        var dlg = new SaveFileDialog
        {
            Title = MenuText.Label_SaveAs,
            Filter = filter,
            FilterIndex = Math.Max(0, IndexOf(kinds, current)) + 1,
            FileName = string.Format(MainText.SaveAs_DefaultName, Path.GetFileNameWithoutExtension(source)) + ImageFormats.DefaultExtension(current),
            AddExtension = true,
            DefaultExt = ImageFormats.DefaultExtension(current),
            OverwritePrompt = true,
        };
        var folder = Path.GetDirectoryName(source);
        if (folder is not null && Directory.Exists(folder)) dlg.InitialDirectory = folder;
        if (dlg.ShowDialog(this) != true) return;
        var target = dlg.FileName;
        var kind = ImageFormats.FromExtension(Path.GetExtension(target))
            ?? kinds[Math.Clamp(dlg.FilterIndex - 1, 0, kinds.Count - 1)];
        if (ImageFormats.FromExtension(Path.GetExtension(target)) is null) target += ImageFormats.DefaultExtension(kind);
        int quality = Math.Clamp(S.JpegQuality, 1, 100);
        _busy = true;
        UpdateUi();
        try
        {
            await Task.Run(() => ImageEncoder.Save(bitmap, target, kind, quality));
            ShowNotice(string.Format(MainText.SaveAs_Done, Path.GetFileName(target)));
            if (string.Equals(Path.GetDirectoryName(target), _folder, StringComparison.OrdinalIgnoreCase))
                RestartRefreshTimer();
        }
        catch (Exception ex) when (ex is ImageOperationException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            MessageDialog.Error(this, string.Format(MainText.SaveAs_FailedTitle, Path.GetFileName(target)), DescribeIoError(ex));
        }
        finally
        {
            _busy = false;
        }
    }

    private static int IndexOf(IReadOnlyList<ImageFormatKind> kinds, ImageFormatKind kind)
    {
        for (int i = 0; i < kinds.Count; i++)
            if (kinds[i] == kind) return i;
        return -1;
    }

    private static string FormatLabel(ImageFormatKind kind) => kind switch
    {
        ImageFormatKind.Bmp => MainText.Format_Bmp,
        ImageFormatKind.Gif => MainText.Format_Gif,
        ImageFormatKind.Jpeg => MainText.Format_Jpeg,
        ImageFormatKind.Tiff => MainText.Format_Tiff,
        _ => MainText.Format_Png,
    };

    private static string[] ExtensionsOf(ImageFormatKind kind) => kind switch
    {
        ImageFormatKind.Bmp => [".bmp", ".dib"],
        ImageFormatKind.Gif => [".gif"],
        ImageFormatKind.Jpeg => [".jpg", ".jpeg", ".jpe", ".jfif"],
        ImageFormatKind.Tiff => [".tif", ".tiff"],
        _ => [".png"],
    };

    /// <summary>Prints one image centred on the page, scaled down to the printable area (never enlarged).</summary>
    private async Task PrintAsync()
    {
        var (bitmap, source) = await SingleImageAsync();
        if (bitmap is null || source is null) return;
        var dlg = new PrintDialog { UserPageRangeEnabled = false };
        if (dlg.ShowDialog() != true) return;
        double width = dlg.PrintableAreaWidth, height = dlg.PrintableAreaHeight;
        const double margin = 48;
        var image = new System.Windows.Controls.Image
        {
            Source = bitmap,
            Stretch = System.Windows.Media.Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(margin),
        };
        var page = new Grid { Width = width, Height = height, Background = System.Windows.Media.Brushes.White };
        page.Children.Add(image);
        page.Measure(new Size(width, height));
        page.Arrange(new Rect(0, 0, width, height));
        page.UpdateLayout();
        try
        {
            dlg.PrintVisual(page, Path.GetFileName(source));
            ShowNotice(string.Format(MainText.Print_Sent, Path.GetFileName(source)));
        }
        catch (Exception ex) when (ex is System.Printing.PrintQueueException or InvalidOperationException or IOException)
        {
            MessageDialog.Error(this, string.Format(MainText.Print_FailedTitle, Path.GetFileName(source)), string.Format(MainText.Error_ReasonWithDetail, MainText.Print_FailedReason, ex.Message));
        }
    }

    private async Task CopyImageAsync()
    {
        var (bitmap, source) = await SingleImageAsync();
        if (bitmap is null || source is null) return;
        try
        {
            Clipboard.SetImage(bitmap);
            ShowNotice(string.Format(MainText.CopyImage_Done, Path.GetFileName(source)));
        }
        catch (Exception ex) when (ex is ExternalException)
        {
            MessageDialog.Error(this, MainText.Clipboard_FailedTitle, string.Format(MainText.Error_ReasonWithDetail, MainText.Clipboard_FailedReason, ex.Message));
        }
    }

    private void ShowProperties()
    {
        var path = TargetPaths().FirstOrDefault();
        if (path is null) return;
        var error = NativeMethods.ShowFileProperties(this, path);
        if (error is not null)
            MessageDialog.Error(this, string.Format(MainText.Properties_FailedTitle, Path.GetFileName(path)), error);
    }

    // ───────────── clipboard: copy, cut, paste ─────────────

    /// <summary>Puts the files on the clipboard the way Explorer does (file drop list + preferred drop effect).</summary>
    private void CopyFilesToClipboard(bool cut)
    {
        var paths = TargetPaths();
        if (paths.Count == 0) return;
        var list = new StringCollection();
        list.AddRange(paths.ToArray());
        var data = new DataObject();
        data.SetFileDropList(list);
        int effect = cut ? DropEffectMove : DropEffectCopy | DropEffectLink;
        data.SetData(PreferredDropEffect, new MemoryStream(BitConverter.GetBytes(effect)));
        try
        {
            Clipboard.SetDataObject(data, copy: true);
            ShowNotice(string.Format(cut ? MainText.Cut_Done : MainText.Copy_Done, paths.Count));
        }
        catch (Exception ex) when (ex is ExternalException)
        {
            MessageDialog.Error(this, MainText.Clipboard_FailedTitle, string.Format(MainText.Error_ReasonWithDetail, MainText.Clipboard_FailedReason, ex.Message));
        }
    }

    private static bool ClipboardHasFiles()
    {
        try
        {
            return Clipboard.ContainsFileDropList();
        }
        catch (ExternalException)
        {
            return false;
        }
    }

    private static bool ClipboardIsCut(IDataObject data)
    {
        try
        {
            if (data.GetData(PreferredDropEffect) is MemoryStream stream && stream.Length >= 4)
            {
                var bytes = new byte[4];
                stream.Position = 0;
                stream.ReadExactly(bytes);
                int effect = BitConverter.ToInt32(bytes, 0);
                return (effect & DropEffectMove) != 0 && (effect & DropEffectCopy) == 0;
            }
        }
        catch (Exception ex) when (ex is ExternalException or IOException)
        {
        }
        return false;
    }

    /// <summary>
    /// Copies (or moves, after Cut) the clipboard files into the current folder. Name clashes ask Replace / Keep Both / Skip;
    /// moving into the same folder does nothing. Failures are listed in one alert at the end.
    /// </summary>
    private async Task PasteAsync()
    {
        var folder = CurrentFolder;
        if (folder is null) return;
        IDataObject? data;
        string[] files;
        try
        {
            data = Clipboard.GetDataObject();
            files = Clipboard.GetFileDropList().Cast<string>().ToArray();
        }
        catch (ExternalException ex)
        {
            MessageDialog.Error(this, MainText.Clipboard_FailedTitle, string.Format(MainText.Error_ReasonWithDetail, MainText.Clipboard_FailedReason, ex.Message));
            return;
        }
        bool move = data is not null && ClipboardIsCut(data);
        var sources = files.Where(File.Exists).ToList();
        int notFiles = files.Length - sources.Count;
        if (sources.Count == 0)
        {
            ShowNotice("\u26A0 " + MainText.Paste_NothingToPaste);
            return;
        }

        ConflictDecision Ask(ConflictInfo conflict) => Dispatcher.Invoke(() =>
        {
            int choice = MessageDialog.Show(this, DialogKind.Question,
                string.Format(MainText.Conflict_Heading, Path.GetFileName(conflict.ProposedOutput)),
                string.Format(MainText.Conflict_Body, Path.GetDirectoryName(conflict.ProposedOutput)), null,
                [MainText.Conflict_Replace, MainText.Conflict_KeepBoth, MainText.Conflict_Skip], defaultIndex: 1, cancelIndex: 2);
            return choice switch
            {
                0 => ConflictDecision.Overwrite,
                1 => ConflictDecision.Rename,
                _ => ConflictDecision.Skip,
            };
        });

        var written = new List<string>();
        var problems = new List<string>();
        int skipped = 0;
        _busy = true;
        UpdateUi();
        try
        {
            await Task.Run(() =>
            {
                foreach (var source in sources)
                {
                    try
                    {
                        string? result = move ? FileOps.MoveTo(source, folder, Ask) : FileOps.CopyTo(source, folder, Ask);
                        if (result is null) skipped++;
                        else written.Add(result);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                    {
                        problems.Add(string.Format(MainText.Problem_Line, Path.GetFileName(source), DescribeIoError(ex)));
                    }
                }
            });
        }
        finally
        {
            _busy = false;
        }
        if (move)
        {
            // The moved files are gone from their old place; a second paste would fail.
            try
            {
                Clipboard.Clear();
            }
            catch (ExternalException)
            {
            }
        }
        ShowNotice(string.Format(MainText.Paste_Done, written.Count, skipped + notFiles));
        if (problems.Count > 0)
            MessageDialog.Error(this, string.Format(MainText.Paste_FailedTitle, problems.Count), MainText.Paste_FailedBody, problems);
        if (InBrowser && string.Equals(folder, _folder, StringComparison.OrdinalIgnoreCase)) await LoadFolderAsync(written);
    }

    // ───────────── delete, rename ─────────────

    private async Task DeleteAsync()
    {
        var paths = TargetPaths();
        if (paths.Count == 0) return;
        if (!S.DeleteWithoutAsking)
        {
            string heading = paths.Count == 1
                ? string.Format(MainText.Delete_HeadingOne, Path.GetFileName(paths[0]))
                : string.Format(MainText.Delete_HeadingMany, paths.Count);
            string body = S.UseRecycleBin ? MainText.Delete_BodyRecycle : MainText.Delete_BodyPermanent;
            if (!MessageDialog.Confirm(this, heading, body, MainText.Delete_Verb)) return;
        }
        // Deleting the viewed image drops its unsaved view rotation.
        _rotation = 0;
        int firstIndex = InBrowser && FocusedItem is { } focused ? _items.IndexOf(focused) : -1;
        var problems = new List<string>();
        var deleted = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                if (S.UseRecycleBin) FileOps.DeleteToRecycleBin([path]);
                else FileOps.DeletePermanently(path);
                if (!File.Exists(path)) deleted.Add(path);
            }
            catch (OperationCanceledException)
            {
                // The shell's own dialog was cancelled for this file.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                problems.Add(string.Format(MainText.Problem_Line, Path.GetFileName(path), DescribeIoError(ex)));
            }
        }
        if (problems.Count > 0)
            MessageDialog.Error(this, string.Format(MainText.Delete_FailedTitle, problems.Count), MainText.Delete_FailedBody, problems);
        ShowNotice(string.Format(S.UseRecycleBin ? MainText.Delete_DoneRecycle : MainText.Delete_DonePermanent, deleted.Count));

        if (InViewer)
        {
            foreach (var path in deleted)
            {
                Invalidate(path);
                int i = _viewList.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                if (i < 0) continue;
                _viewList.RemoveAt(i);
                if (i < _viewIndex) _viewIndex--;
            }
            if (_viewList.Count == 0)
            {
                _viewIndex = -1;
                await LeaveViewerAsync();
                await LoadFolderAsync(null);
                return;
            }
            if (deleted.Count > 0) await ShowImageAsync(Math.Min(_viewIndex, _viewList.Count - 1));
            await LoadFolderAsync(null);
            return;
        }
        await LoadFolderAsync(null);
        // Keep the place in the list: select what now sits where the first deleted tile was.
        if (firstIndex >= 0 && _items.Count > 0)
        {
            SelectPaths([_items[Math.Min(firstIndex, _items.Count - 1)].Path]);
            FocusList();
        }
    }

    /// <summary>Renames the file stem; the extension stays as it is.</summary>
    private async Task RenameAsync()
    {
        var path = TargetPaths().FirstOrDefault();
        if (path is null) return;
        if (InViewer && !await ResolvePendingRotationAsync()) return;
        var folder = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        string? Validate(string value)
        {
            var name = value.Trim();
            if (name.Length == 0) return MainText.Rename_Empty;
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.')) return MainText.Rename_Invalid;
            var target = Path.Combine(folder, name + extension);
            if (!string.Equals(target, path, StringComparison.OrdinalIgnoreCase) && (File.Exists(target) || Directory.Exists(target)))
                return string.Format(MainText.Rename_Exists, name + extension);
            return null;
        }
        var newStem = InputDialog.Ask(this, MainText.Rename_Title, string.Format(MainText.Rename_Prompt, Path.GetFileName(path)), stem, extension, Validate, MainText.Rename_Verb);
        if (newStem is null || newStem.Trim() == stem) return;
        string renamed;
        try
        {
            renamed = FileOps.Rename(path, newStem.Trim() + extension);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            MessageDialog.Error(this, string.Format(MainText.Rename_FailedTitle, Path.GetFileName(path)), DescribeIoError(ex));
            return;
        }
        ShowNotice(string.Format(MainText.Rename_Done, Path.GetFileName(renamed)));
        if (InViewer)
        {
            Invalidate(path);
            int i = _viewList.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) _viewList[i] = renamed;
            UpdateFullScreenName();
            await LoadFolderAsync(null);
            return;
        }
        await LoadFolderAsync([renamed]);
        FocusList();
    }

    // ───────────── edit dialogs ─────────────

    /// <summary>Runs one of the edit dialogs on the viewed image or the selected tiles, then refreshes and summarizes.</summary>
    private async Task RunEditDialogAsync(string key)
    {
        if (InViewer && !await ResolvePendingRotationAsync()) return;
        var files = TargetPaths();
        var viewed = InViewer ? CurrentPath : null;
        JobResult? result = key switch
        {
            "image.rotateDialog" => EditDialogs.ShowRotate(this, files, S),
            "image.resizeDialog" => EditDialogs.ShowResize(this, files, S),
            "image.convertDialog" => EditDialogs.ShowConvert(this, files, S),
            _ => EditDialogs.ShowBatch(this, files, S),
        };
        if (result is null) return;
        // The dialogs may remember the last JPEG quality.
        SaveSettings(showError: false);
        string summary = string.Format(MainText.Edit_Summary, result.WrittenCount, result.SkippedCount, result.FailedCount);
        if (result.Cancelled) summary += " " + MainText.Edit_Cancelled;
        ShowNotice((result.FailedCount > 0 ? "\u26A0 " : "") + summary);

        var outputs = result.Items.SelectMany(i => i.Outputs).ToList();
        if (viewed is not null && InViewer)
        {
            Invalidate(viewed);
            var item = result.Items.FirstOrDefault(i => string.Equals(i.Input, viewed, StringComparison.OrdinalIgnoreCase));
            if (!File.Exists(viewed))
            {
                // The original was replaced by its output (delete original): follow it, or drop it from the list.
                var next = item?.Outputs.FirstOrDefault(File.Exists);
                if (next is not null) _viewList[_viewIndex] = next;
                else _viewList.RemoveAt(_viewIndex);
            }
            if (_viewList.Count == 0)
            {
                _viewIndex = -1;
                await LeaveViewerAsync();
            }
            else
            {
                await ShowImageAsync(Math.Min(_viewIndex, _viewList.Count - 1));
            }
            await LoadFolderAsync(null, forceReload: true);
            return;
        }
        var here = outputs.Where(o => string.Equals(Path.GetDirectoryName(o), _folder, StringComparison.OrdinalIgnoreCase)).ToList();
        // Outputs may overwrite listed files: reload thumbnails.
        await LoadFolderAsync(here.Count > 0 ? here : SelectedPaths(), forceReload: true);
    }

    // ───────────── help ─────────────

    private void OpenHelp()
    {
        var dir = AppContext.BaseDirectory;
        var korean = Path.Combine(dir, "HELP.ko.md");
        var english = Path.Combine(dir, "HELP.md");
        var file = UiLanguage.IsKorean && File.Exists(korean) ? korean : english;
        if (!File.Exists(file))
        {
            MessageDialog.Error(this, MainText.Help_MissingTitle, string.Format(MainText.Help_MissingBody, file));
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            // No application is associated with .md: Notepad can always show it.
            try
            {
                Process.Start(new ProcessStartInfo("notepad.exe", "\"" + file + "\"") { UseShellExecute = true });
            }
            catch (Win32Exception ex)
            {
                MessageDialog.Error(this, MainText.Help_MissingTitle, string.Format(MainText.Error_ReasonWithDetail, MainText.Help_OpenFailed, ex.Message));
            }
        }
    }
}
