using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;
using OhMyPic.App.Text;
using OhMyPic.Core;

namespace OhMyPic.App.Dialogs.Edit;

/// <summary>Keeps only the latest progress value the pipeline reported (worker thread → UI reads it on a timer).</summary>
public sealed class EditProgressReporter : IProgress<JobProgress>
{
    private JobProgress? _latest;

    public JobProgress? Latest => Volatile.Read(ref _latest);

    public void Report(JobProgress value) => Volatile.Write(ref _latest, value);
}

/// <summary>
/// Modal progress window for an edit job: bar, current file, "n of m" and Cancel (a real CancellationToken).
/// Closing the window requests cancellation; it closes itself once the job task has finished.
/// </summary>
public partial class EditProgressWindow : Window
{
    private readonly EditProgressReporter _reporter;
    private readonly Task _task;
    private readonly CancellationTokenSource _cts;
    private readonly DispatcherTimer _timer;
    private readonly string _heading;

    public EditProgressWindow(string heading, int total, EditProgressReporter reporter, Task task, CancellationTokenSource cts)
    {
        InitializeComponent();
        Title = heading;
        _heading = heading;
        HeadingText.Text = heading;
        AutomationProperties.SetName(this, heading);
        CurrentText.Text = EditText.Progress_Starting;
        _reporter = reporter;
        _task = task;
        _cts = cts;
        ShowCount(0, total);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => Refresh(), Dispatcher);
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
        if (!string.IsNullOrEmpty(p.Current)) CurrentText.Text = Path.GetFileName(p.Current);
        ShowCount(p.Done, p.Total);
    }

    private void ShowCount(int done, int total)
    {
        Bar.Maximum = Math.Max(1, total);
        Bar.Value = Math.Clamp(done, 0, Math.Max(1, total));
        CountText.Text = string.Format(CultureInfo.CurrentCulture, EditText.Progress_Count, done, total);
    }

    private void OnCancel(object sender, RoutedEventArgs e) => RequestCancel();

    private void RequestCancel()
    {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();
        CancelButton.IsEnabled = false;
        CancelButton.Content = EditText.Progress_Canceling;
        HeadingText.Text = string.Format(EditText.Progress_CancelingHeading, _heading);
        Title = HeadingText.Text;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Never close before the job finishes: closing means "cancel". The ContinueWith above closes again when done.
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
