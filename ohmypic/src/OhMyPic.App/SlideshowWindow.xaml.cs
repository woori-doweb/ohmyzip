using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OhMyPic.App.Text;
using OhMyPic.Core;

namespace OhMyPic.App;

/// <summary>
/// Borderless full-screen slideshow on the monitor of the owner window. No control bar (as observed):
/// Left/Right step, Space pauses/resumes, Esc or the right-click menu exits. <see cref="FinalIndex"/> is the image
/// shown when the slideshow ended, so the viewer can continue from it.
/// </summary>
public partial class SlideshowWindow : Window
{
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(400);

    private readonly SlideshowSequence _sequence;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _cursorTimer;
    private int _loadVersion;
    private bool _paused;

    public SlideshowWindow(Window owner, IReadOnlyList<string> items, int startIndex, AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        _sequence = new SlideshowSequence(items, startIndex, settings.LoopAtEnds);
        FinalIndex = _sequence.Index;

        // Start on the owner's monitor: place the restore bounds inside the owner, then maximize there.
        Owner = owner;
        Left = owner.Left + 40;
        Top = owner.Top + 40;
        Width = 640;
        Height = 480;
        WindowState = WindowState.Maximized;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(SlideshowSequence.IntervalSeconds(settings.SlideIntervalSeconds)) };
        _timer.Tick += (_, _) => Step(forward: true, fromTimer: true);
        _cursorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _cursorTimer.Tick += (_, _) =>
        {
            _cursorTimer.Stop();
            if (!SlideMenu.IsOpen) Cursor = Cursors.None;
        };

        PauseItem.Header = MainText.Slideshow_Pause;
        PauseItem.InputGestureText = "Space";
        RenderOptions.SetBitmapScalingMode(FrontImage, settings.SmoothScaling ? BitmapScalingMode.HighQuality : BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetBitmapScalingMode(BackImage, settings.SmoothScaling ? BitmapScalingMode.HighQuality : BitmapScalingMode.NearestNeighbor);

        PreviewKeyDown += OnPreviewKeyDown;
        MouseMove += (_, _) => ShowCursor();
        Loaded += async (_, _) =>
        {
            Root.Focus();
            _cursorTimer.Start();
            await ShowCurrentAsync(fade: false);
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            _cursorTimer.Stop();
            _loadVersion++;
        };
    }

    /// <summary>Index (in the list passed in) of the image on screen when the slideshow ended.</summary>
    public int FinalIndex { get; private set; }

    private void ShowCursor()
    {
        Cursor = null;
        _cursorTimer.Stop();
        _cursorTimer.Start();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;
            case Key.Right:
            case Key.PageDown:
                Step(forward: true, fromTimer: false);
                break;
            case Key.Left:
            case Key.PageUp:
                Step(forward: false, fromTimer: false);
                break;
            case Key.Space:
                TogglePause();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void TogglePause()
    {
        _paused = !_paused;
        PauseItem.Header = _paused ? MainText.Slideshow_Resume : MainText.Slideshow_Pause;
        PausedBorder.Visibility = _paused ? Visibility.Visible : Visibility.Collapsed;
        if (_paused) _timer.Stop();
        else RestartTimer();
    }

    private void RestartTimer()
    {
        _timer.Stop();
        if (!_paused) _timer.Start();
    }

    private async void Step(bool forward, bool fromTimer)
    {
        bool moved = forward ? _sequence.MoveNext() : _sequence.MovePrevious();
        if (!moved)
        {
            // End of the list without looping: a timed step ends the slideshow, a manual step stays put.
            if (fromTimer) Close();
            return;
        }
        FinalIndex = _sequence.Index;
        await ShowCurrentAsync(fade: _settings.SlideFade);
    }

    private async Task ShowCurrentAsync(bool fade)
    {
        int version = ++_loadVersion;
        _timer.Stop();
        string path = _sequence.Current;
        bool autoRotate = _settings.AutoRotateByExif;
        var dpi = VisualTreeHelper.GetDpi(this);
        int maxSide = (int)Math.Ceiling(Math.Max(ActualWidth * dpi.DpiScaleX, ActualHeight * dpi.DpiScaleY));
        BitmapSource? bitmap = null;
        string? error = null;
        try
        {
            bitmap = await Task.Run(() => ImageLoader.LoadForDisplay(path, autoRotate, maxSide));
        }
        catch (Exception ex)
        {
            error = MainWindow.DescribeLoadError(ex);
        }
        if (version != _loadVersion || !IsLoaded) return;

        NameText.Text = Path.GetFileName(path);
        NameBorder.Visibility = _settings.ShowFileNameInFullScreen ? Visibility.Visible : Visibility.Collapsed;
        if (bitmap is null)
        {
            FrontImage.Source = null;
            BackImage.Source = null;
            ErrorText.Text = string.Format(MainText.Error_CannotOpenTitle, Path.GetFileName(path)) + "\n" + error;
            ErrorText.Visibility = Visibility.Visible;
            AutomationProperties.SetName(Root, ErrorText.Text);
        }
        else
        {
            ErrorText.Visibility = Visibility.Collapsed;
            AutomationProperties.SetName(Root, Path.GetFileName(path));
            if (fade && FrontImage.Source is not null && SystemParameters.ClientAreaAnimation)
                CrossFade(bitmap);
            else
            {
                FrontImage.BeginAnimation(OpacityProperty, null);
                BackImage.BeginAnimation(OpacityProperty, null);
                FrontImage.Source = bitmap;
                FrontImage.Opacity = 1;
                BackImage.Source = null;
                BackImage.Opacity = 0;
            }
        }
        RestartTimer();
    }

    /// <summary>The new image fades in on the back layer while the old one fades out, then the layers swap roles.</summary>
    private void CrossFade(BitmapSource next)
    {
        var incoming = BackImage;
        var outgoing = FrontImage;
        incoming.BeginAnimation(OpacityProperty, null);
        outgoing.BeginAnimation(OpacityProperty, null);
        incoming.Source = next;
        incoming.Opacity = 0;
        Panel.SetZIndex(incoming, 1);
        Panel.SetZIndex(outgoing, 0);
        var fadeIn = new DoubleAnimation(0, 1, FadeDuration) { FillBehavior = FillBehavior.HoldEnd };
        var fadeOut = new DoubleAnimation(1, 0, FadeDuration) { FillBehavior = FillBehavior.HoldEnd };
        incoming.BeginAnimation(OpacityProperty, fadeIn);
        outgoing.BeginAnimation(OpacityProperty, fadeOut);
        // Swap the names' roles: the incoming layer is now the front.
        (FrontImage, BackImage) = (incoming, outgoing);
    }

    private void OnPauseClick(object sender, RoutedEventArgs e) => TogglePause();
    private void OnPreviousClick(object sender, RoutedEventArgs e) => Step(forward: false, fromTimer: false);
    private void OnNextClick(object sender, RoutedEventArgs e) => Step(forward: true, fromTimer: false);
    private void OnExitClick(object sender, RoutedEventArgs e) => Close();
}
