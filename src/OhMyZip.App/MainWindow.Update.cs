using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using OhMyZip.App.Dialogs;
using OhMyZip.App.Settings;
using OhMyZip.Core;

namespace OhMyZip.App;

/// <summary>
/// 업데이트. 창이 뜨면(환경 설정에서 켜 두었을 때 하루 한 번) 조용히 확인·내려받아 두고 프로그램을 닫을 때 설치한다(App.OnExit).
/// 도움말 &gt; 업데이트 확인… 은 결과를 보여 주고, 새 버전이 있으면 묻고 내려받아 바로 설치한 뒤 다시 시작한다.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan AutoUpdateInterval = TimeSpan.FromDays(1);
    private static readonly TimeSpan ManualCheckTimeout = TimeSpan.FromSeconds(30);
    private const int MaxNoteLines = 15;

    /// <summary>자동 확인·내려받기나 메뉴 업데이트가 진행 중이면 true(업데이트 확인 메뉴 비활성).</summary>
    private bool _updating;
    private CancellationTokenSource? _autoUpdateCts;

    private bool CanCheckUpdate => !_updating;

    /// <summary>창이 뜰 때 한 번: 자동 업데이트가 켜져 있고 마지막 확인에서 하루가 지났으면 백그라운드로 확인·내려받기.</summary>
    private void StartAutoUpdate()
    {
        if (!S.AutoUpdate || App.PendingUpdate is not null) return;
        if (S.LastUpdateCheck is { } last)
        {
            var age = DateTimeOffset.Now - last;
            if (age >= TimeSpan.Zero && age < AutoUpdateInterval) return;
        }
        Closed += (_, _) => _autoUpdateCts?.Cancel();
        _ = AutoUpdateAsync();
    }

    /// <summary>시작 시 확인은 사용자를 방해하지 않는다: 받기를 마쳤을 때만 상태줄에 알리고, 확인 실패·오류는 아무것도 표시하지 않는다.</summary>
    private async Task AutoUpdateAsync()
    {
        var cts = new CancellationTokenSource();
        _autoUpdateCts = cts;
        SetUpdating(true);
        try
        {
            var updater = App.CreateUpdater();
            var current = App.CurrentVersion;
            var check = await Task.Run(() => updater.CheckAsync(current, cts.Token), cts.Token);
            if (check.Status != UpdateCheckStatus.Available || check.Release is not { } release)
            {
                // 새 버전을 찾은 확인은 기록하지 않는다(받기·설치가 실패하면 다음 실행에서 다시 받도록).
                S.LastUpdateCheck = DateTimeOffset.Now;
                AppSettings.SaveCurrent();
                return;
            }
            var staged = await Task.Run(() => updater.DownloadAsync(release, null, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            App.PendingUpdate = (staged, release.Version);
            ShowNotice($"새 버전 {VersionText(release.Version)} 을(를) 받았습니다. 프로그램을 닫으면 설치됩니다.");
        }
        catch (Exception)
        {
            // 네트워크가 없거나 내려받기·검증에 실패해도 시작 시에는 알리지 않는다(다음 실행에서 다시 시도).
        }
        finally
        {
            _autoUpdateCts = null;
            cts.Dispose();
            SetUpdating(false);
        }
    }

    /// <summary>도움말 &gt; 업데이트 확인…</summary>
    private async Task CheckForUpdateAsync()
    {
        var updater = App.CreateUpdater();
        var current = App.CurrentVersion;
        UpdateCheckResult check;
        SetUpdating(true);
        _notice = "업데이트를 확인하는 중…";
        UpdateStatusText();
        AnnounceStatus();
        try
        {
            using var timeout = new CancellationTokenSource(ManualCheckTimeout);
            check = await Task.Run(() => updater.CheckAsync(current, timeout.Token), CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            check = new UpdateCheckResult(UpdateCheckStatus.Unavailable, null,
                $"업데이트 서버가 {ManualCheckTimeout.TotalSeconds:0}초 안에 응답하지 않았습니다.");
        }
        finally
        {
            _notice = null;
            UpdateStatusText();
            SetUpdating(false);
        }

        switch (check.Status)
        {
            case UpdateCheckStatus.UpToDate:
                ShowNotice($"최신 버전입니다 ({VersionText(current)}).");
                return;
            case UpdateCheckStatus.Available when check.Release is { } release:
                await InstallUpdateAsync(updater, current, release);
                return;
            default:
                MessageDialog.Show(this, DialogKind.Warning, "업데이트를 확인할 수 없습니다",
                    $"{check.Message}\n인터넷 연결을 확인한 뒤 도움말 > 업데이트 확인… 을 다시 누르세요. "
                    + $"계속 안 되면 릴리스 페이지에서 새 버전을 직접 받을 수 있습니다: https://github.com/{AppUpdater.Repository}/releases",
                    null, ["확인"]);
                return;
        }
    }

    /// <summary>새 버전 안내 → 내려받기(진행 창, 취소 가능) → 설치 → 같은 인자로 새 버전 실행 → 이 창 닫기.</summary>
    private async Task InstallUpdateAsync(AppUpdater updater, Version current, UpdateRelease release)
    {
        var newVersion = VersionText(release.Version);
        int answer = MessageDialog.Show(this, DialogKind.Question, $"새 버전 {newVersion} 을(를) 설치할까요?",
            $"현재 버전: {VersionText(current)}\n새 버전: {newVersion}\n\n바뀐 점:\n{FormatNotes(release)}\n\n"
            + "업데이트하면 새 버전을 내려받아 설치한 뒤 ohmyZip 을 다시 시작합니다.",
            null, ["업데이트", "취소"], defaultIndex: 0, cancelIndex: 1);
        if (answer != 0) return;

        string staged;
        try
        {
            staged = await DownloadWithProgressAsync(updater, release);
        }
        catch (OperationCanceledException)
        {
            ShowNotice("업데이트 내려받기를 취소했습니다. 프로그램은 바뀌지 않았습니다.");
            return;
        }
        catch (IOException ex)
        {
            MessageDialog.Error(this, "업데이트를 내려받지 못했습니다", ex.Message);
            return;
        }
        catch (Exception ex)
        {
            MessageDialog.Error(this, "업데이트를 내려받지 못했습니다",
                $"{ex.Message}\n인터넷 연결을 확인한 뒤 도움말 > 업데이트 확인… 을 다시 누르세요.");
            return;
        }

        SetUpdating(true);
        try
        {
            await Task.Run(() => updater.Apply(staged));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException || ex.InnerException is UnauthorizedAccessException)
        {
            MessageDialog.Error(this, "업데이트를 설치하지 못했습니다",
                $"설치 폴더에 쓸 권한이 없습니다: {AppContext.BaseDirectory}\n"
                + "ohmyZip 을 관리자 권한으로 실행해 다시 업데이트하거나, 쓰기 권한이 있는 폴더(예: 사용자 폴더)로 옮겨 설치하세요. "
                + $"릴리스 페이지에서 압축 파일을 받아 직접 풀어 넣어도 됩니다: {release.PageUrl}");
            return;
        }
        catch (IOException ex)
        {
            MessageDialog.Error(this, "업데이트를 설치하지 못했습니다", ex.Message);
            return;
        }
        finally
        {
            SetUpdating(false);
        }

        // 설치를 마쳤으므로 닫을 때 다시 설치하지 않는다.
        App.PendingUpdate = null;
        RestartApplication(newVersion);
    }

    /// <summary>작업 실행(RunAsync)과 같은 규칙: 300ms 넘게 걸리면 모달 진행 창(진행률·속도·남은 시간·취소).</summary>
    private async Task<string> DownloadWithProgressAsync(AppUpdater updater, UpdateRelease release)
    {
        _busy = true;
        SetUpdating(true);
        using var cts = new CancellationTokenSource();
        var reporter = new ProgressReporter();
        var name = Path.GetFileName(release.PackageUrl.LocalPath);
        var progress = new DownloadProgress(reporter, name);
        var task = Task.Run(() => updater.DownloadAsync(release, progress, cts.Token), CancellationToken.None);
        try
        {
            if (await Task.WhenAny(task, Task.Delay(300)) != task)
            {
                var window = new ProgressWindow("업데이트 내려받는 중", name, reporter, task, cts) { Owner = this };
                window.ShowDialog();
            }
            return await task;
        }
        finally
        {
            _busy = false;
            SetUpdating(false);
        }
    }

    /// <summary>설치한 새 버전을 시작할 때와 같은 인자로 실행하고 이 창을 닫는다(인자 배열).</summary>
    private void RestartApplication(string newVersion)
    {
        var exe = App.ExecutablePath;
        if (exe is null)
        {
            MessageDialog.Info(this, $"새 버전 {newVersion} 을(를) 설치했습니다", "ohmyZip 을 다시 실행하면 새 버전이 시작됩니다.");
            Close();
            return;
        }
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        if (Directory.Exists(App.StartupDirectory)) psi.WorkingDirectory = App.StartupDirectory;
        foreach (var arg in App.StartupArgs) psi.ArgumentList.Add(arg);
        try
        {
            using var _ = Process.Start(psi);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            MessageDialog.Error(this, "새 버전을 시작하지 못했습니다",
                $"업데이트는 설치했습니다. ohmyZip 을 닫은 뒤 다시 실행하세요.\n{ex.Message}");
            return;
        }
        Close();
    }

    private void SetUpdating(bool value)
    {
        _updating = value;
        UpdateTooltips();
        RefreshCommandState();
    }

    /// <summary>릴리스 노트(직전 버전 이후 커밋 이력)를 앞 몇 줄만. 나머지는 릴리스 페이지에서 본다.</summary>
    private static string FormatNotes(UpdateRelease release)
    {
        var lines = release.Notes.Replace("\r\n", "\n").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return $"(설명 없음 — 릴리스 페이지: {release.PageUrl})";
        var shown = string.Join("\n", lines.Take(MaxNoteLines));
        return lines.Length <= MaxNoteLines ? shown : $"{shown}\n… 외 {lines.Length - MaxNoteLines:N0}줄 — 전체 내용: {release.PageUrl}";
    }

    private static string VersionText(Version v) => v.Build >= 0 ? v.ToString(3) : v.ToString();

    /// <summary>내려받기 진행(바이트)을 진행 창이 읽는 ProgressInfo 로 옮긴다. 전체 크기를 모르면 0(진행률을 알 수 없음).</summary>
    private sealed class DownloadProgress(ProgressReporter reporter, string name) : IProgress<(long Done, long? Total)>
    {
        public void Report((long Done, long? Total) value) =>
            reporter.Report(new ProgressInfo(value.Done, value.Total ?? 0, name));
    }
}
