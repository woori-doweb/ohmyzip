using System.ComponentModel;
using System.IO;
using System.Windows;
using OhMyPic.App.Models;
using OhMyPic.App.Text;
using OhMyPic.Core;

namespace OhMyPic.App.Dialogs;

/// <summary>
/// Help &gt; Check for Updates…: checks GitHub, shows the current and the new version with the release notes and, on
/// [Update and Restart], downloads (progress, cancellable by closing), verifies and installs the new files.
/// DialogResult true = the new version is installed; the caller restarts the app.
/// </summary>
public partial class UpdateWindow : Window
{
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);

    private readonly AppUpdater _updater;
    private readonly Version _current;
    private UpdateRelease? _release;
    private CancellationTokenSource? _cts;
    private bool _installing;
    private bool _closed;

    public UpdateWindow(AppUpdater updater, Version current)
    {
        InitializeComponent();
        _updater = updater;
        _current = current;
        HeadingText.Text = UpdateText.Checking;
        CurrentVersionText.Text = string.Format(UpdateText.CurrentVersion, VersionText(current));
        SourceInitialized += (_, _) => NativeMethods.ApplyDarkTitleBar(this);
        Loaded += async (_, _) => await CheckAsync();
        Closing += OnClosingWindow;
        Closed += (_, _) => _closed = true;
    }

    /// <summary>Version that was installed (set when DialogResult is true).</summary>
    public Version? InstalledVersion { get; private set; }

    private async Task CheckAsync()
    {
        var cts = new CancellationTokenSource(CheckTimeout);
        _cts = cts;
        UpdateCheckResult check;
        try
        {
            check = await Task.Run(() => _updater.CheckAsync(_current, cts.Token), CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            check = new UpdateCheckResult(UpdateCheckStatus.Unavailable, null, UpdateError.Timeout);
        }
        finally
        {
            _cts = null;
            cts.Dispose();
        }
        if (_closed) return;

        switch (check.Status)
        {
            case UpdateCheckStatus.Available when check.Release is { } release:
                _release = release;
                HeadingText.Text = string.Format(UpdateText.Available_Heading, VersionText(release.Version));
                NewVersionText.Text = string.Format(UpdateText.NewVersion, VersionText(release.Version));
                NewVersionText.Visibility = Visibility.Visible;
                var notes = release.Notes.Replace("\r\n", "\n").Trim();
                NotesText.Text = notes.Length > 0 ? notes : string.Format(UpdateText.NotesNone, release.PageUrl);
                NotesPanel.Visibility = Visibility.Visible;
                InstallButton.Visibility = Visibility.Visible;
                InstallButton.IsDefault = true;
                InstallButton.Focus();
                break;
            case UpdateCheckStatus.UpToDate:
                HeadingText.Text = UpdateText.UpToDate_Heading;
                ShowBody(string.Format(UpdateText.UpToDate_Body, VersionText(_current)));
                break;
            default:
                HeadingText.Text = UpdateText.Unavailable_Heading;
                ShowBody(string.Format(UpdateText.Unavailable_Body, Describe(check.Error, check.Detail), AppUpdater.ReleasesPage));
                break;
        }
    }

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        if (_release is not { } release || _cts is not null || _installing) return;
        InstallButton.IsEnabled = false;
        BodyText.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        Bar.IsIndeterminate = true;
        ProgressText.Text = string.Format(UpdateText.DownloadingUnknown, ImageItem.FormatSize(0));
        CloseButton.Content = DialogText.Cancel_Button;

        var cts = new CancellationTokenSource();
        _cts = cts;
        var progress = new Progress<(long Done, long? Total)>(ReportProgress);
        string staged;
        try
        {
            staged = await Task.Run(() => _updater.DownloadAsync(release, progress, cts.Token), CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            EndProgress();
            ShowBody(UpdateText.DownloadCancelled);
            return;
        }
        catch (UpdateException ex)
        {
            EndProgress();
            HeadingText.Text = UpdateText.DownloadFailed_Heading;
            ShowBody(string.Format(UpdateText.DownloadFailed_Body, Describe(ex.Code, ex.Detail), release.PageUrl));
            return;
        }
        finally
        {
            _cts = null;
            cts.Dispose();
        }
        if (_closed) return;

        // Installing cannot be cancelled: closing the window waits until the swap (or its rollback) is finished.
        _installing = true;
        Bar.IsIndeterminate = true;
        ProgressText.Text = UpdateText.Installing;
        CloseButton.IsEnabled = false;
        try
        {
            await Task.Run(() => _updater.Apply(staged));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _installing = false;
            EndProgress();
            HeadingText.Text = UpdateText.InstallFailed_Heading;
            var (code, detail) = ex is UpdateException u ? (u.Code, u.Detail) : (UpdateError.InstallFailed, ex.Message);
            var reason = Describe(code, detail);
            ShowBody(code == UpdateError.RollbackIncomplete
                ? string.Format(UpdateText.InstallFailed_Incomplete, reason, release.PageUrl)
                : string.Format(UpdateText.InstallFailed_Body, reason));
            return;
        }
        _installing = false;
        InstalledVersion = release.Version;
        DialogResult = true;
    }

    private void ReportProgress((long Done, long? Total) value)
    {
        if (_installing || _closed) return;
        if (value.Total is long total && total > 0)
        {
            Bar.IsIndeterminate = false;
            Bar.Value = Math.Clamp((double)value.Done / total, 0, 1);
            ProgressText.Text = string.Format(UpdateText.Downloading, ImageItem.FormatSize(value.Done), ImageItem.FormatSize(total));
        }
        else
        {
            Bar.IsIndeterminate = true;
            ProgressText.Text = string.Format(UpdateText.DownloadingUnknown, ImageItem.FormatSize(value.Done));
        }
    }

    private void EndProgress()
    {
        ProgressPanel.Visibility = Visibility.Collapsed;
        CloseButton.Content = UpdateText.Close_Button;
        CloseButton.IsEnabled = true;
        InstallButton.IsEnabled = true;
    }

    private void ShowBody(string text)
    {
        BodyText.Text = text;
        BodyText.Visibility = Visibility.Visible;
    }

    private void OnClosingWindow(object? sender, CancelEventArgs e)
    {
        if (_installing)
        {
            e.Cancel = true;
            return;
        }
        _cts?.Cancel();
    }

    private static string VersionText(Version v) => v.Build >= 0 ? v.ToString(3) : v.ToString();

    /// <summary>Sentence for an updater error code (detail fills the {0} of codes that have one).</summary>
    internal static string Describe(UpdateError code, string? detail)
    {
        var text = code switch
        {
            UpdateError.NoRelease => UpdateText.Error_NoRelease,
            UpdateError.ServerBusy => UpdateText.Error_ServerBusy,
            UpdateError.HttpError => UpdateText.Error_HttpError,
            UpdateError.ConnectFailed => UpdateText.Error_ConnectFailed,
            UpdateError.Timeout => UpdateText.Error_Timeout,
            UpdateError.ConnectionLost => UpdateText.Error_ConnectionLost,
            UpdateError.InfoUnreadable => UpdateText.Error_InfoUnreadable,
            UpdateError.PackageMissing => UpdateText.Error_PackageMissing,
            UpdateError.UnsafeUrl => UpdateText.Error_UnsafeUrl,
            UpdateError.TooLarge => UpdateText.Error_TooLarge,
            UpdateError.ChecksumUnreadable => UpdateText.Error_ChecksumUnreadable,
            UpdateError.ChecksumWrongFile => UpdateText.Error_ChecksumWrongFile,
            UpdateError.ChecksumMismatch => UpdateText.Error_ChecksumMismatch,
            UpdateError.PackageUnsafePath => UpdateText.Error_PackageUnsafePath,
            UpdateError.PackageBadLayout => UpdateText.Error_PackageBadLayout,
            UpdateError.PackageNoExecutable => UpdateText.Error_PackageNoExecutable,
            UpdateError.PackageUnreadable => UpdateText.Error_PackageUnreadable,
            UpdateError.DownloadFailed => UpdateText.Error_DownloadFailed,
            UpdateError.StagedMissing => UpdateText.Error_StagedMissing,
            UpdateError.InstallFolderMissing => UpdateText.Error_InstallFolderMissing,
            UpdateError.FileOutsideInstall => UpdateText.Error_FileOutsideInstall,
            UpdateError.TargetIsFolder => UpdateText.Error_TargetIsFolder,
            UpdateError.AccessDenied => UpdateText.Error_AccessDenied,
            UpdateError.InstallFailed => UpdateText.Error_InstallFailed,
            UpdateError.RollbackIncomplete => UpdateText.Error_RollbackIncomplete,
            _ => UpdateText.Error_Unknown,
        };
        return string.Format(text, detail ?? "?");
    }
}
