using System.Globalization;
using System.IO;
using System.Windows;
using OhMyPic.App.Text;
using OhMyPic.Core;

namespace OhMyPic.App.Dialogs.Edit;

/// <summary>
/// Entry points for the image edit commands (rotate / resize / convert / batch). Each method shows its dialog, then runs
/// <see cref="EditPipeline.Run"/> on a background task behind a progress window with Cancel, asks duplicate-name questions
/// on the UI thread, shows an alert only when items failed, and returns the result. Null = the user cancelled before starting.
/// The caller refreshes the folder view and shows the status-bar summary.
/// </summary>
public static class EditDialogs
{
    public static JobResult? ShowRotate(Window owner, IReadOnlyList<string> files, AppSettings settings)
    {
        if (files.Count == 0) return null;
        var dlg = new RotateDialog(files, settings) { Owner = owner };
        if (dlg.ShowDialog() != true) return null;
        return Run(owner, EditText.Progress_Rotate, new EditJob(files, dlg.Output, Rotate: dlg.Options), settings);
    }

    public static JobResult? ShowResize(Window owner, IReadOnlyList<string> files, AppSettings settings)
    {
        if (files.Count == 0) return null;
        var dlg = new ResizeDialog(files) { Owner = owner };
        if (dlg.ShowDialog() != true) return null;
        return Run(owner, EditText.Progress_Resize, new EditJob(files, dlg.Output, Resize: dlg.Options), settings);
    }

    public static JobResult? ShowConvert(Window owner, IReadOnlyList<string> files, AppSettings settings)
    {
        if (files.Count == 0) return null;
        var dlg = new ConvertDialog(files, settings) { Owner = owner };
        if (dlg.ShowDialog() != true) return null;
        RememberQuality(settings, dlg.Options);
        return Run(owner, EditText.Progress_Convert, new EditJob(files, dlg.Output, Convert: dlg.Options), settings);
    }

    public static JobResult? ShowBatch(Window owner, IReadOnlyList<string> files, AppSettings settings)
    {
        var dlg = new BatchEditWizard(files, settings) { Owner = owner };
        if (dlg.ShowDialog() != true) return null;
        var inputs = dlg.Files;
        if (inputs.Count == 0) return null;
        if (dlg.Convert is { } convert) RememberQuality(settings, convert);
        var job = new EditJob(inputs, dlg.Output, dlg.Resize, dlg.Rotate, dlg.Rename, dlg.Convert);
        return Run(owner, EditText.Progress_Batch, job, settings);
    }

    /// <summary>Dialog heading: the file name for one file, otherwise "N files selected".</summary>
    internal static string HeadingFor(IReadOnlyList<string> files) =>
        files.Count == 1 ? Path.GetFileName(files[0]) : string.Format(CultureInfo.CurrentCulture, EditText.Heading_Files, files.Count);

    /// <summary>Localized text for an engine error code (one string per code).</summary>
    public static string ErrorText(EditErrorCode code) => code switch
    {
        EditErrorCode.UnsupportedFormat => EditText.ErrorCode_UnsupportedFormat,
        EditErrorCode.DecodeFailed => EditText.ErrorCode_DecodeFailed,
        EditErrorCode.EncodeFailed => EditText.ErrorCode_EncodeFailed,
        EditErrorCode.TargetSizeUnreachable => EditText.ErrorCode_TargetSizeUnreachable,
        EditErrorCode.TooLarge => EditText.ErrorCode_TooLarge,
        EditErrorCode.IoError => EditText.ErrorCode_IoError,
        EditErrorCode.Cancelled => EditText.ErrorCode_Cancelled,
        _ => EditText.ErrorCode_None,
    };

    /// <summary>With "apply last save quality" on, the JPEG quality chosen here becomes the default next time.</summary>
    private static void RememberQuality(AppSettings settings, ConvertOptions options)
    {
        if (settings.ApplyLastSaveQuality && options.Format == ImageFormatKind.Jpeg)
            settings.JpegQuality = options.JpegQuality;
    }

    private static JobResult Run(Window owner, string heading, EditJob job, AppSettings settings)
    {
        // The engine deletes originals permanently. When the user keeps deleted files in the Recycle Bin, the engine
        // keeps the originals and they are recycled here after each output was written and verified.
        bool recycleOriginals = job.Output.DeleteOriginal && settings.UseRecycleBin;
        if (recycleOriginals) job = job with { Output = job.Output with { DeleteOriginal = false } };

        using var cts = new CancellationTokenSource();
        var reporter = new EditProgressReporter();
        var recycleProblems = new List<string>();
        EditProgressWindow? window = null;
        ConflictDecision? applyToAll = null;
        var dispatcher = owner.Dispatcher;

        // Called on the worker thread; the question is asked on the UI thread. Conflicts are asked one at a time.
        ConflictDecision Ask(ConflictInfo info)
        {
            if (applyToAll is { } remembered) return remembered;
            return dispatcher.Invoke(() =>
            {
                var (decision, all) = ConflictDialog.Ask((Window?)window ?? owner, info);
                if (all) applyToAll = decision;
                return decision;
            });
        }

        var task = Task.Run(() => EditPipeline.Run(job, Ask, reporter, cts.Token));
        window = new EditProgressWindow(heading, job.Inputs.Count, reporter, task, cts) { Owner = owner };
        window.ShowDialog();

        JobResult jobResult;
        try
        {
            jobResult = task.GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The engine reports per-item problems in the result; reaching here means the job itself could not run.
            EditMessageDialog.Show(owner, EditMessageDialog.Kind.Error, EditText.Run_FailedHeading, ex.Message, null, [EditText.Ok_Button], 0, 0);
            return new JobResult([]);
        }

        // On the UI thread: the shell may show its own error dialog for a file it cannot recycle.
        if (recycleOriginals) RecycleOriginals(jobResult, recycleProblems);
        ShowFailures(owner, jobResult, recycleProblems);
        return jobResult;
    }

    /// <summary>Recycles originals of written items whose outputs are different files (never the file just written).</summary>
    private static void RecycleOriginals(JobResult result, List<string> problems)
    {
        foreach (var item in result.Items)
        {
            if (item.Status != ItemStatus.Written || item.Outputs.Count == 0) continue;
            string input;
            try { input = Path.GetFullPath(item.Input); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (item.Outputs.Any(o => string.Equals(Path.GetFullPath(o), input, StringComparison.OrdinalIgnoreCase))) continue;
            try
            {
                FileOps.DeleteToRecycleBin([input]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or ArgumentException)
            {
                problems.Add(string.Format(CultureInfo.CurrentCulture, EditText.Result_RecycleFailed, Path.GetFileName(input)));
            }
        }
    }

    /// <summary>Alert only when something failed (cancelled items are not failures). Success goes to the status bar.</summary>
    private static void ShowFailures(Window owner, JobResult result, IReadOnlyList<string> extraProblems)
    {
        var failed = result.Items.Where(i => i.Status == ItemStatus.Failed && i.Error != EditErrorCode.Cancelled).ToList();
        if (failed.Count == 0 && extraProblems.Count == 0) return;
        var lines = failed
            .Select(i => string.Format(CultureInfo.CurrentCulture, EditText.Result_Item, Path.GetFileName(i.Input), ErrorText(i.Error)))
            .Concat(extraProblems)
            .ToList();
        string heading = failed.Count > 0
            ? string.Format(CultureInfo.CurrentCulture, EditText.Result_FailedHeading, failed.Count, result.Items.Count)
            : EditText.Result_RecycleHeading;
        EditMessageDialog.Show(owner, EditMessageDialog.Kind.Error, heading, EditText.Result_Body, lines, [EditText.Ok_Button], 0, 0);
    }
}
