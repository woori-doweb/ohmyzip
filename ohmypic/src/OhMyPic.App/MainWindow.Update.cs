using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using OhMyPic.App.Dialogs;
using OhMyPic.App.Settings;
using OhMyPic.App.Text;
using OhMyPic.Core;

namespace OhMyPic.App;

/// <summary>
/// Updates. After the window has loaded (when Settings allow it, at most once a day) a quiet background check downloads
/// a new version, which App.OnExit installs. Help &gt; Check for Updates… opens the update window, which can install
/// right away; the app then restarts on the same image or folder.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan AutoUpdateInterval = TimeSpan.FromDays(1);

    /// <summary>True while the automatic check/download or the update window runs (Check for Updates is disabled).</summary>
    private bool _updating;
    private CancellationTokenSource? _autoUpdateCts;

    /// <summary>Once per window: starts the background check when it is on and the last finished check is a day old.</summary>
    private void StartAutoUpdate()
    {
        if (!S.CheckForUpdates || !App.BackgroundUpdatesAllowed || App.PendingUpdate is not null || _autoUpdateCts is not null) return;
        if (_ui.LastUpdateCheck is { } last)
        {
            var age = DateTimeOffset.Now - last;
            if (age >= TimeSpan.Zero && age < AutoUpdateInterval) return;
        }
        Closed += (_, _) => _autoUpdateCts?.Cancel();
        _ = AutoUpdateAsync();
    }

    /// <summary>Never interrupts: only a finished download is announced in the status bar; failures stay silent.</summary>
    private async Task AutoUpdateAsync()
    {
        var cts = new CancellationTokenSource();
        _autoUpdateCts = cts;
        _updating = true;
        try
        {
            var updater = App.CreateUpdater();
            var check = await Task.Run(() => updater.CheckAsync(App.CurrentVersion, cts.Token), cts.Token);
            if (check.Status != UpdateCheckStatus.Available || check.Release is not { } release)
            {
                // A check that found a new version is not recorded, so a failed download is retried on the next start.
                _ui.LastUpdateCheck = DateTimeOffset.Now;
                _ui.Save(UiState.DefaultPath);
                return;
            }
            var staged = await Task.Run(() => updater.DownloadAsync(release, null, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            App.PendingUpdate = (staged, release.Version);
            ShowNotice(string.Format(UpdateText.Ready_Notice, release.Version.ToString(3)));
        }
        catch (Exception)
        {
            // No network, a rejected download or a closed window: nothing is shown; the next start tries again.
        }
        finally
        {
            _autoUpdateCts = null;
            cts.Dispose();
            _updating = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>Help &gt; Check for Updates…</summary>
    private async Task CheckForUpdatesAsync()
    {
        Version? installed;
        _updating = true;
        try
        {
            var dialog = new UpdateWindow(App.CreateUpdater(), App.CurrentVersion) { Owner = this };
            installed = dialog.ShowDialog() == true ? dialog.InstalledVersion : null;
        }
        finally
        {
            _updating = false;
        }
        if (installed is null) return;
        // Already installed: nothing is left for the exit-time install.
        App.PendingUpdate = null;
        await RestartAfterUpdateAsync(installed);
    }

    /// <summary>Starts the new version on the current image (viewer) or folder (browser) and closes this window.</summary>
    private async Task RestartAfterUpdateAsync(Version installed)
    {
        var version = installed.ToString(3);
        // Settle an unsaved rotation first so closing does not ask after the new process has started.
        if (!await ResolvePendingRotationAsync())
        {
            ShowNotice(string.Format(UpdateText.Installed_NextStart, version));
            return;
        }
        var exe = Path.Combine(AppContext.BaseDirectory, AppUpdater.MainExecutable);
        var reopen = InViewer ? CurrentPath : _folder;
        var error = StartSelf(exe, reopen);
        if (error is not null)
        {
            MessageDialog.Info(this, string.Format(UpdateText.RestartFailed_Title, version), string.Format(UpdateText.RestartFailed_Body, error));
            return;
        }
        _closeConfirmed = true;
        Close();
    }

    /// <summary>Starts the exe with an optional path argument (argument list, no shell). Null on success, else the reason.</summary>
    private static string? StartSelf(string exe, string? path)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
        if (!string.IsNullOrEmpty(path)) psi.ArgumentList.Add(path);
        try
        {
            using var process = Process.Start(psi);
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return ex.Message;
        }
    }
}
