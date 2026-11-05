using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OhMyPic.App.Commands;
using OhMyPic.App.Dialogs;
using OhMyPic.App.Text;
using OhMyPic.App.Theme;
using OhMyPic.Core;

namespace OhMyPic.App;

/// <summary>
/// Viewer mode: one image filling the client area, zoom (100% = one image pixel per screen pixel), fit modes,
/// view rotation with the save prompt, neighbour preloading, panning, navigator, full screen and slideshow.
/// </summary>
public partial class MainWindow
{
    private const double MinZoom = 0.1;
    private const double MaxZoom = 16;
    private static readonly double[] ZoomSteps = [0.1, 0.125, 1.0 / 6, 0.25, 1.0 / 3, 0.5, 2.0 / 3, 0.75, 1, 1.25, 1.5, 2, 3, 4, 6, 8, 12, 16];

    private List<string> _viewList = [];
    private int _viewIndex = -1;
    private BitmapSource? _bitmap;
    private double _zoom = 1;
    private bool _hasZoom;
    /// <summary>The fit rule re-applied on resize; null after a manual zoom.</summary>
    private FitMode? _activeFit;
    private int _rotation;
    private int _imageVersion;
    private readonly Dictionary<string, Task<BitmapSource>> _preload = new(StringComparer.OrdinalIgnoreCase);

    private bool _fullScreen;
    private Rect _restoreBounds = Rect.Empty;
    private WindowState _restoreState = WindowState.Normal;
    private WindowStyle _restoreStyle = WindowStyle.SingleBorderWindow;
    private System.Windows.ResizeMode _restoreResize = System.Windows.ResizeMode.CanResize;

    private Point? _panStart;
    private Point _panOrigin;

    private string? CurrentPath => _viewIndex >= 0 && _viewIndex < _viewList.Count ? _viewList[_viewIndex] : null;

    private void InitViewer()
    {
        PrevEdgeButton.Command = CommandCatalog.Command("nav.previous");
        PrevEdgeButton.CommandTarget = this;
        NextEdgeButton.Command = CommandCatalog.Command("nav.next");
        NextEdgeButton.CommandTarget = this;
        ViewerRoot.PreviewMouseWheel += OnViewerWheel;
        ViewerRoot.MouseLeftButtonDown += OnViewerMouseDown;
        ViewerRoot.MouseMove += OnViewerMouseMove;
        ViewerRoot.MouseLeftButtonUp += OnViewerMouseUp;
        ViewerRoot.LostMouseCapture += (_, _) => EndPan();
        ViewerRoot.SizeChanged += (_, _) =>
        {
            if (_activeFit is { } fit) ApplyFit(fit);
            else UpdateNavigator();
        };
        ViewerScroll.ScrollChanged += (_, _) => UpdateNavigator();
        DpiChanged += (_, _) =>
        {
            if (_activeFit is { } fit) ApplyFit(fit);
            else ApplyImageSize();
        };
    }

    /// <summary>Viewer options that apply without reloading: background, scaling quality, navigator, file name overlay.</summary>
    private void ApplyViewerSettings()
    {
        Brush? background = null;
        if (!string.Equals(S.BackgroundColor, AppSettings.SystemColor, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (ColorConverter.ConvertFromString(S.BackgroundColor) is Color color)
                {
                    var brush = new SolidColorBrush(color);
                    brush.Freeze();
                    background = brush;
                }
            }
            catch (FormatException)
            {
                // Invalid stored colour: fall back to the theme background.
            }
        }
        if (background is null) ViewerRoot.SetResourceReference(Panel.BackgroundProperty, ThemeManager.Background);
        else ViewerRoot.Background = background;
        RenderOptions.SetBitmapScalingMode(ViewerImage, S.SmoothScaling ? BitmapScalingMode.HighQuality : BitmapScalingMode.NearestNeighbor);
        UpdateNavigator();
        UpdateFullScreenName();
    }

    // ───────────── entering and leaving ─────────────

    private async Task EnterViewerFromSelectionAsync()
    {
        var item = FocusedItem;
        if (item is null) return;
        _viewList = _items.Select(i => i.Path).ToList();
        int index = _items.IndexOf(item);
        SetMode(Mode.Viewer);
        await ShowImageAsync(Math.Max(0, index));
    }

    /// <summary>Opens a file in the viewer; its folder becomes the browsed folder and the viewing list.</summary>
    private async Task OpenFileInViewerAsync(string path)
    {
        var full = Path.GetFullPath(path);
        var folder = Path.GetDirectoryName(full);
        if (folder is not null && !string.Equals(folder, _folder, StringComparison.OrdinalIgnoreCase))
            await NavigateToAsync(folder);
        _viewList = _items.Select(i => i.Path).ToList();
        int index = _viewList.FindIndex(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            // Not part of the listing (hidden file, unknown extension): view it on its own.
            _viewList = [full];
            index = 0;
        }
        SelectPaths([full]);
        SetMode(Mode.Viewer);
        await ShowImageAsync(index);
    }

    /// <summary>Back to the browser with the viewed image selected. False when the user cancelled the rotation prompt.</summary>
    private async Task<bool> LeaveViewerAsync()
    {
        if (!await ResolvePendingRotationAsync()) return false;
        if (_fullScreen) SetFullScreen(false);
        var current = CurrentPath;
        _imageVersion++;
        _bitmap = null;
        ViewerImage.Source = null;
        NavigatorImage.Source = null;
        ClearPreload();
        SetMode(Mode.Browser);
        if (current is not null)
        {
            SelectPaths([current]);
            FocusList();
        }
        return true;
    }

    // ───────────── showing images ─────────────

    private async Task StepAsync(int delta)
    {
        if (_viewList.Count == 0) return;
        if (!await ResolvePendingRotationAsync()) return;
        int next = _viewIndex + delta;
        if (next < 0 || next >= _viewList.Count)
        {
            if (!S.LoopAtEnds) return;
            next = (next % _viewList.Count + _viewList.Count) % _viewList.Count;
        }
        await ShowImageAsync(next, delta);
    }

    private async Task GoToAsync(int index)
    {
        if (index < 0 || index >= _viewList.Count) return;
        if (!await ResolvePendingRotationAsync()) return;
        await ShowImageAsync(index, index >= _viewIndex ? 1 : -1);
    }

    /// <summary>Shows the image at <paramref name="index"/>; a failed decode shows the reason inline (no alert).</summary>
    private async Task ShowImageAsync(int index, int direction = 1)
    {
        if (index < 0 || index >= _viewList.Count) return;
        int version = ++_imageVersion;
        _viewIndex = index;
        _rotation = 0;
        var path = _viewList[index];
        UpdateUi();
        BitmapSource? bitmap = null;
        string? error = null;
        try
        {
            bitmap = await LoadBitmapAsync(path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _preload.Remove(path);
            error = DescribeLoadError(ex);
        }
        catch (OutOfMemoryException)
        {
            _preload.Remove(path);
            error = MainText.Error_TooLarge;
        }
        if (version != _imageVersion || !InViewer) return;

        _bitmap = bitmap;
        ViewerImage.Source = bitmap;
        NavigatorImage.Source = bitmap;
        AutomationProperties.SetName(ViewerImage, Path.GetFileName(path));
        AutomationProperties.SetName(ViewerRoot, Path.GetFileName(path));
        if (bitmap is null)
        {
            ViewerErrorHeading.Text = string.Format(MainText.Error_CannotOpenTitle, Path.GetFileName(path));
            ViewerErrorBody.Text = error ?? MainText.Error_Unknown;
            ViewerError.Visibility = Visibility.Visible;
            ViewerScroll.Visibility = Visibility.Collapsed;
        }
        else
        {
            ViewerError.Visibility = Visibility.Collapsed;
            ViewerScroll.Visibility = Visibility.Visible;
            if (S.FitMode == FitMode.KeepZoom && _hasZoom && _activeFit is null) ApplyImageSize();
            else ApplyFit(S.FitMode == FitMode.KeepZoom ? (_activeFit ?? FitMode.ShrinkLargeOnly) : S.FitMode);
            ViewerScroll.ScrollToHorizontalOffset(0);
            ViewerScroll.ScrollToVerticalOffset(0);
        }
        UpdateFullScreenName();
        TrimPreload(index);
        Preload(index + (direction >= 0 ? 1 : -1));
        UpdateUi();
    }

    private Task<BitmapSource> LoadBitmapAsync(string path)
    {
        if (_preload.TryGetValue(path, out var task)) return task;
        bool autoRotate = S.AutoRotateByExif;
        task = Task.Run(() => ImageLoader.LoadForDisplay(path, autoRotate));
        _preload[path] = task;
        return task;
    }

    /// <summary>Starts decoding the neighbour in the direction of travel (wrapping when looping).</summary>
    private void Preload(int index)
    {
        if (_viewList.Count < 2) return;
        if (index < 0 || index >= _viewList.Count)
        {
            if (!S.LoopAtEnds) return;
            index = (index % _viewList.Count + _viewList.Count) % _viewList.Count;
        }
        var task = LoadBitmapAsync(_viewList[index]);
        // Observe failures here; the real error is reported when the image is shown.
        _ = task.ContinueWith(static t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>Keeps only the current image and its direct neighbours decoded.</summary>
    private void TrimPreload(int index)
    {
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = index - 1; i <= index + 1; i++)
        {
            if (_viewList.Count == 0) break;
            int k = (i % _viewList.Count + _viewList.Count) % _viewList.Count;
            keep.Add(_viewList[k]);
        }
        foreach (var key in _preload.Keys.Where(k => !keep.Contains(k)).ToList()) _preload.Remove(key);
    }

    private void ClearPreload() => _preload.Clear();

    private void Invalidate(string path) => _preload.Remove(path);

    private string ViewerStatus()
    {
        if (CurrentPath is not { } path) return "";
        string position = string.Format(MainText.Viewer_Position, _viewIndex + 1, _viewList.Count);
        if (_bitmap is null) return position + "   " + Path.GetFileName(path);
        bool swap = _rotation % 180 != 0;
        int w = swap ? _bitmap.PixelHeight : _bitmap.PixelWidth;
        int h = swap ? _bitmap.PixelWidth : _bitmap.PixelHeight;
        string text = string.Format(MainText.Viewer_Status, position, Path.GetFileName(path), w, h, Math.Round(_zoom * 100));
        if (_rotation != 0) text += "   " + string.Format(MainText.Viewer_Rotated, _rotation);
        return text;
    }

    // ───────────── zoom and fit ─────────────

    /// <summary>Sets the image element size for the current zoom and rotation. Zoom 1 = one image pixel per device pixel.</summary>
    private void ApplyImageSize()
    {
        if (_bitmap is null) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        ViewerImage.Width = _bitmap.PixelWidth * _zoom / dpi.DpiScaleX;
        ViewerImage.Height = _bitmap.PixelHeight * _zoom / dpi.DpiScaleY;
        ViewerImage.LayoutTransform = _rotation == 0 ? Transform.Identity : new RotateTransform(_rotation);
        _hasZoom = true;
        UpdateNavigator();
        UpdateStatus();
        CommandManager.InvalidateRequerySuggested();
    }

    private void ApplyFit(FitMode mode)
    {
        if (_bitmap is null) return;
        if (ViewerRoot.ActualWidth <= 0 || ViewerRoot.ActualHeight <= 0) ViewerRoot.UpdateLayout();
        double vw = Math.Max(1, ViewerRoot.ActualWidth), vh = Math.Max(1, ViewerRoot.ActualHeight);
        var dpi = VisualTreeHelper.GetDpi(this);
        bool swap = _rotation % 180 != 0;
        double w = swap ? _bitmap.PixelHeight : _bitmap.PixelWidth;
        double h = swap ? _bitmap.PixelWidth : _bitmap.PixelHeight;
        double fitW = vw * dpi.DpiScaleX / Math.Max(1, w);
        double fitH = vh * dpi.DpiScaleY / Math.Max(1, h);
        double zoom = mode switch
        {
            FitMode.ShrinkLargeOnly => Math.Min(1, Math.Min(fitW, fitH)),
            FitMode.FitWindow => Math.Min(fitW, fitH),
            FitMode.FitWidth => fitW,
            FitMode.FitHeight => fitH,
            FitMode.ActualSize => 1,
            _ => _zoom,
        };
        // Fit results are not clamped to the zoom steps (a tiny window may need less than 10%), only kept positive.
        _zoom = Math.Max(0.01, Math.Min(MaxZoom, zoom));
        _activeFit = mode is FitMode.ActualSize or FitMode.KeepZoom ? null : mode;
        ApplyImageSize();
    }

    private void ApplyFitMode()
    {
        if (_bitmap is null) return;
        if (S.FitMode == FitMode.KeepZoom) ApplyImageSize();
        else ApplyFit(S.FitMode);
    }

    private void FitToWindow() => ApplyFit(FitMode.FitWindow);

    private void SetActualSize() => SetZoom(1, null);

    private void ZoomStep(int direction, Point? anchor)
    {
        double next = direction > 0
            ? ZoomSteps.FirstOrDefault(s => s > _zoom + 1e-6, MaxZoom)
            : ZoomSteps.LastOrDefault(s => s < _zoom - 1e-6, MinZoom);
        SetZoom(next, anchor);
    }

    /// <summary>Zooms keeping the point under <paramref name="anchor"/> (viewport coordinates; null = centre) in place.</summary>
    private void SetZoom(double zoom, Point? anchor)
    {
        if (_bitmap is null) return;
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        var a = anchor ?? new Point(ViewerScroll.ViewportWidth / 2, ViewerScroll.ViewportHeight / 2);
        double relX = ViewerScroll.ExtentWidth > 0 ? (ViewerScroll.HorizontalOffset + a.X) / ViewerScroll.ExtentWidth : 0.5;
        double relY = ViewerScroll.ExtentHeight > 0 ? (ViewerScroll.VerticalOffset + a.Y) / ViewerScroll.ExtentHeight : 0.5;
        _zoom = zoom;
        _activeFit = null;
        ApplyImageSize();
        ViewerScroll.UpdateLayout();
        ViewerScroll.ScrollToHorizontalOffset(Math.Max(0, relX * ViewerScroll.ExtentWidth - a.X));
        ViewerScroll.ScrollToVerticalOffset(Math.Max(0, relY * ViewerScroll.ExtentHeight - a.Y));
    }

    // ───────────── rotation ─────────────

    /// <summary>Rotates the view only; the file is written when leaving the image (prompt or automatic, per settings).</summary>
    private void RotateView(int degrees)
    {
        if (_bitmap is null) return;
        _rotation = ((_rotation + degrees) % 360 + 360) % 360;
        if (_activeFit is { } fit) ApplyFit(fit);
        else ApplyImageSize();
        UpdateUi();
    }

    private static RotateFlip RotationOperation(int degrees) => degrees switch
    {
        90 => RotateFlip.Rotate90,
        180 => RotateFlip.Rotate180,
        270 => RotateFlip.Rotate270,
        _ => RotateFlip.None,
    };

    /// <summary>The displayed image with the view rotation applied (for Save As, Print, Copy Image).</summary>
    private BitmapSource? RenderedBitmap() =>
        _bitmap is null ? null : _rotation == 0 ? _bitmap : ImageTransforms.Rotate(_bitmap, RotationOperation(_rotation));

    /// <summary>
    /// Settles an unsaved view rotation before the image is left. Ask: Save / Don't Save / Cancel. Overwrite: saves.
    /// Returns false only when the user chose Cancel (stay on the image).
    /// </summary>
    private async Task<bool> ResolvePendingRotationAsync()
    {
        if (!InViewer || _rotation == 0 || CurrentPath is not { } path || _bitmap is null) return true;
        if (S.RotateAutoSave == RotateAutoSaveMode.Ask)
        {
            int choice = MessageDialog.Show(this, DialogKind.Question,
                string.Format(MainText.Rotate_SaveHeading, Path.GetFileName(path)), MainText.Rotate_SaveBody, null,
                [MainText.Rotate_Save, MainText.Rotate_DontSave, DialogText.Cancel_Button], defaultIndex: 0, cancelIndex: 2);
            if (choice == 2) return false;
            if (choice == 1)
            {
                _rotation = 0;
                ApplyFitMode();
                return true;
            }
        }
        await SaveRotationAsync(path);
        return true;
    }

    private async Task SaveRotationExplicitAsync()
    {
        if (CurrentPath is { } path && _rotation != 0) await SaveRotationAsync(path);
    }

    /// <summary>Writes the view rotation into the file itself (lossless for suitable JPEGs) and reloads it.</summary>
    private async Task SaveRotationAsync(string path)
    {
        var job = new EditJob(
            [path],
            new OutputOptions(OutputFolderMode.SameFolder, Conflict: ConflictPolicy.Overwrite),
            Rotate: new RotateOptions(RotationOperation(_rotation), S.AutoRotateByExif));
        _busy = true;
        UpdateUi();
        JobResult result;
        try
        {
            result = await Task.Run(() => EditPipeline.Run(job, null, null, CancellationToken.None));
        }
        finally
        {
            _busy = false;
        }
        _rotation = 0;
        Invalidate(path);
        var item = result.Items.FirstOrDefault();
        if (item is { Status: ItemStatus.Written })
        {
            ShowNotice(string.Format(MainText.Rotate_Saved, Path.GetFileName(path)));
            if (InViewer && string.Equals(CurrentPath, path, StringComparison.OrdinalIgnoreCase)) await ShowImageAsync(_viewIndex);
        }
        else
        {
            ApplyFitMode();
            string reason = item is null ? MainText.Error_Unknown : DescribeErrorCode(item.Error);
            MessageDialog.Error(this, string.Format(MainText.Rotate_SaveFailedTitle, Path.GetFileName(path)), reason);
        }
        UpdateUi();
    }

    // ───────────── mouse: wheel, pan, double click ─────────────

    private void OnViewerWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (_bitmap is null && _viewList.Count == 0) return;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        // Ctrl swaps the configured wheel action.
        bool zoom = (S.WheelAction == WheelAction.Zoom) != ctrl;
        if (zoom)
        {
            if (_bitmap is not null) ZoomStep(e.Delta > 0 ? 1 : -1, e.GetPosition(ViewerScroll));
        }
        else
        {
            var key = e.Delta < 0 ? "nav.next" : "nav.previous";
            if (CanRun(key)) Run(key);
        }
    }

    private bool CanPan => _bitmap is not null && (ViewerScroll.ExtentWidth > ViewerScroll.ViewportWidth + 0.5 || ViewerScroll.ExtentHeight > ViewerScroll.ViewportHeight + 0.5);

    private void OnViewerMouseDown(object sender, MouseButtonEventArgs e)
    {
        ViewerRoot.Focus();
        if (e.ClickCount == 2)
        {
            e.Handled = true;
            EndPan();
            if (CanRun("view.fullScreen")) Run("view.fullScreen");
            return;
        }
        if (!CanPan) return;
        _panStart = e.GetPosition(ViewerRoot);
        _panOrigin = new Point(ViewerScroll.HorizontalOffset, ViewerScroll.VerticalOffset);
        ViewerRoot.CaptureMouse();
        ViewerRoot.Cursor = Cursors.ScrollAll;
        e.Handled = true;
    }

    private void OnViewerMouseMove(object sender, MouseEventArgs e)
    {
        if (_panStart is not { } start) return;
        var p = e.GetPosition(ViewerRoot);
        ViewerScroll.ScrollToHorizontalOffset(_panOrigin.X - (p.X - start.X));
        ViewerScroll.ScrollToVerticalOffset(_panOrigin.Y - (p.Y - start.Y));
    }

    private void OnViewerMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_panStart is null) return;
        ViewerRoot.ReleaseMouseCapture();
        EndPan();
    }

    private void EndPan()
    {
        _panStart = null;
        ViewerRoot.Cursor = null;
    }

    // ───────────── navigator, full screen ─────────────

    /// <summary>Thumbnail of the whole image with the visible part outlined; only while the image is larger than the window.</summary>
    private void UpdateNavigator()
    {
        bool show = S.ShowNavigator && InViewer && CanPan && _bitmap is not null;
        NavigatorBorder.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show || _bitmap is null) return;
        bool swap = _rotation % 180 != 0;
        double w = swap ? _bitmap.PixelHeight : _bitmap.PixelWidth;
        double h = swap ? _bitmap.PixelWidth : _bitmap.PixelHeight;
        const double box = 160;
        double scale = Math.Min(box / w, box / h);
        double navW = Math.Max(8, w * scale), navH = Math.Max(8, h * scale);
        NavigatorHost.Width = navW;
        NavigatorHost.Height = navH;
        NavigatorImage.Width = swap ? navH : navW;
        NavigatorImage.Height = swap ? navW : navH;
        NavigatorImage.LayoutTransform = _rotation == 0 ? Transform.Identity : new RotateTransform(_rotation);
        double ew = Math.Max(1, ViewerScroll.ExtentWidth), eh = Math.Max(1, ViewerScroll.ExtentHeight);
        double rw = Math.Min(1, ViewerScroll.ViewportWidth / ew) * navW;
        double rh = Math.Min(1, ViewerScroll.ViewportHeight / eh) * navH;
        Canvas.SetLeft(NavigatorRect, ViewerScroll.HorizontalOffset / ew * navW);
        Canvas.SetTop(NavigatorRect, ViewerScroll.VerticalOffset / eh * navH);
        NavigatorRect.Width = Math.Max(2, rw);
        NavigatorRect.Height = Math.Max(2, rh);
    }

    private void UpdateFullScreenName()
    {
        bool show = _fullScreen && S.ShowFileNameInFullScreen && CurrentPath is not null;
        FullScreenName.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        FullScreenNameText.Text = CurrentPath is { } p ? Path.GetFileName(p) : "";
    }

    /// <summary>Borderless maximized window without menu, toolbar and status bar.</summary>
    private void SetFullScreen(bool on)
    {
        if (on == _fullScreen) return;
        if (on)
        {
            _restoreState = WindowState;
            _restoreBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            _restoreStyle = WindowStyle;
            _restoreResize = ResizeMode;
            _fullScreen = true;
            // Re-maximizing with no border only covers the taskbar when the window goes through Normal first.
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = System.Windows.ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            _fullScreen = false;
            WindowStyle = _restoreStyle;
            ResizeMode = _restoreResize;
            WindowState = _restoreState;
        }
        UpdateUi();
        UpdateFullScreenName();
        ViewerRoot.Focus();
    }

    // ───────────── slideshow ─────────────

    private async Task StartSlideshowAsync()
    {
        List<string> list;
        int start;
        if (InViewer)
        {
            if (!await ResolvePendingRotationAsync()) return;
            list = _viewList;
            start = Math.Max(0, _viewIndex);
        }
        else
        {
            var selected = SelectedItems();
            if (selected.Count > 1)
            {
                // Several tiles selected: show just those.
                list = selected.Select(i => i.Path).ToList();
                start = 0;
            }
            else
            {
                list = _items.Select(i => i.Path).ToList();
                start = FocusedItem is { } focused ? Math.Max(0, _items.IndexOf(focused)) : 0;
            }
        }
        if (list.Count == 0) return;
        var show = new SlideshowWindow(this, list, start, S);
        show.ShowDialog();
        int final = show.FinalIndex;
        if (final < 0 || final >= list.Count) return;
        if (InViewer && ReferenceEquals(list, _viewList))
        {
            if (final != _viewIndex) await ShowImageAsync(final);
        }
        else if (InBrowser)
        {
            SelectPaths([list[final]]);
            FocusList();
        }
    }
}
