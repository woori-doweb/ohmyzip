using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using OhMyZip.Core;

namespace OhMyZip.App.Dialogs;

/// <summary>엔진이 보고한 마지막 진행 값만 보관한다(작업 스레드 → UI 는 타이머로 읽음).</summary>
public sealed class ProgressReporter : IProgress<ProgressInfo>
{
    private ProgressInfo? _latest;
    public ProgressInfo? Latest => Volatile.Read(ref _latest);
    public void Report(ProgressInfo value) => Volatile.Write(ref _latest, value);
}

/// <summary>
/// 오래 걸리는 작업의 모달 진행 창. 처리량/전체·백분율·속도·남은 시간을 ProgressInfo 에서 계산한다.
/// 취소 버튼·Esc 는 실제 CancellationToken 을 취소한다. 작업이 끝나면 스스로 닫힌다.
/// </summary>
public partial class ProgressWindow : Window
{
    private readonly ProgressReporter _reporter;
    private readonly Task _task;
    private readonly CancellationTokenSource _cts;
    private readonly DispatcherTimer _timer;
    private readonly string _idleText;
    private readonly string _heading;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    // 최근 약 3초의 (시각, 처리량) 표본으로 속도를 잰다(평균보다 현재 속도에 가깝게).
    private readonly Queue<(double Seconds, long Bytes)> _samples = new();

    public ProgressWindow(string heading, string idleText, ProgressReporter reporter, Task task, CancellationTokenSource cts)
    {
        InitializeComponent();
        Title = heading;
        _heading = heading;
        HeadingText.Text = heading;
        _idleText = idleText;
        CurrentText.Text = idleText;
        _reporter = reporter;
        _task = task;
        _cts = cts;
        ShowUnknown(0);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => Refresh(), Dispatcher);
        Loaded += (_, _) =>
        {
            if (_task.IsCompleted) { Close(); return; }
            _timer.Start();
            _task.ContinueWith(_ => Dispatcher.BeginInvoke(Close), TaskScheduler.Default);
        };
        SourceInitialized += (_, _) => NativeMethods.ApplyDarkTitleBar(this);
    }

    private void Refresh()
    {
        var p = _reporter.Latest;
        if (p is null) return;
        CurrentText.Text = string.IsNullOrEmpty(p.CurrentPath) ? _idleText : p.CurrentPath;
        if (p.TotalBytes <= 0)
        {
            ShowUnknown(p.ProcessedBytes);
            return;
        }

        double now = _clock.Elapsed.TotalSeconds;
        _samples.Enqueue((now, p.ProcessedBytes));
        while (_samples.Count > 2 && now - _samples.Peek().Seconds > 3) _samples.Dequeue();
        var first = _samples.Peek();
        double span = now - first.Seconds;
        double speed = span > 0.2 ? Math.Max(0, p.ProcessedBytes - first.Bytes) / span : 0;

        Bar.IsIndeterminate = false;
        double percent = Math.Clamp(p.ProcessedBytes * 100.0 / p.TotalBytes, 0, 100);
        Bar.Value = percent;
        var text = string.Format(CultureInfo.CurrentCulture, "{0} / {1} ({2:0}%)", FormatBytes(p.ProcessedBytes), FormatBytes(p.TotalBytes), Math.Floor(percent));
        if (speed > 0)
        {
            text += $" · {FormatBytes((long)speed)}/초";
            long remaining = Math.Max(0, p.TotalBytes - p.ProcessedBytes);
            text += $" · 남은 시간 약 {FormatDuration(TimeSpan.FromSeconds(remaining / speed))}";
        }
        else
        {
            text += " · 속도 계산 중";
        }
        AmountText.Text = text;
    }

    /// <summary>전체 크기를 모를 때만 진행률 없는 막대. 애니메이션 끔(Windows 애니메이션 효과 끔·동작 줄이기)이면 움직이지 않는 빈 막대.</summary>
    private void ShowUnknown(long processed)
    {
        Bar.IsIndeterminate = Theme.ThemeManager.AnimationsEnabled;
        Bar.Value = 0;
        AmountText.Text = processed > 0 ? $"진행률을 알 수 없음 · 처리 {FormatBytes(processed)}" : "진행률을 알 수 없음";
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["바이트", "KB", "MB", "GB", "TB"];
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0
            ? string.Format(CultureInfo.CurrentCulture, "{0:N0}바이트", bytes)
            : string.Format(CultureInfo.CurrentCulture, "{0:0.0} {1}", v, units[u]);
    }

    private static string FormatDuration(TimeSpan t)
    {
        if (t.TotalSeconds < 1) return "1초 미만";
        if (t.TotalMinutes < 1) return $"{(int)t.TotalSeconds}초";
        if (t.TotalHours < 1) return $"{(int)t.TotalMinutes}분 {t.Seconds}초";
        return $"{(int)t.TotalHours}시간 {t.Minutes}분";
    }

    private void OnCancel(object sender, RoutedEventArgs e) => RequestCancel();

    private void RequestCancel()
    {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();
        CancelButton.IsEnabled = false;
        CancelButton.Content = "취소하는 중…";
        HeadingText.Text = $"{_heading} — 취소하는 중…";
        Title = HeadingText.Text;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 작업이 끝나기 전에는 닫지 않는다(닫기 = 취소 요청). 끝나면 ContinueWith 가 다시 Close 한다.
        if (!_task.IsCompleted)
        {
            e.Cancel = true;
            RequestCancel();
        }
        else
        {
            _timer.Stop();
        }
        base.OnClosing(e);
    }
}
