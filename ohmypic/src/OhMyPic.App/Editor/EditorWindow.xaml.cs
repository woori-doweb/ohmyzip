using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OhMyPic.App.Commands;
using OhMyPic.App.Dialogs;
using OhMyPic.App.Text;
using OhMyPic.App.Theme;
using OhMyPic.Core;
using OhMyPic.Core.Editing;

namespace OhMyPic.App.Editor;

/// <summary>
/// Single-image editor (design P15): its own top-level window beside the viewer. Canvas in the middle, inspector on the
/// right (crop, rotate/flip, adjustments, presets), menu bar File/Edit/Image/View with a toolbar subset. Edits go through
/// <see cref="EditSession"/>; the canvas shows <see cref="Adjustments.ApplyPreview"/> at the displayed size, rendered on a
/// background task (latest wins). A window edits either a file or a screenshot (capture mode: no source file, opened by
/// the Print Screen agent). This file: opening, commands, zoom, preview render, status, closing.
/// Adjustments/presets are in EditorWindow.Adjust.cs, the crop frame in EditorWindow.Crop.cs, the drawing tools in
/// EditorWindow.Draw.cs, capture mode and the clipboard commands in EditorWindow.Copy.cs.
/// </summary>
public partial class EditorWindow : Window
{
    private const double MinZoom = 0.02;
    private const double MaxZoom = 8;
    private const double ZoomFactor = 1.25;

    /// <summary>Canvas margin around the image (CanvasHost Margin) on each side, in DIP.</summary>
    private const double CanvasMargin = 16;

    /// <summary>One editor command. Unimplemented rows (later phases) stay visible, disabled, with the "(Coming soon)" suffix.</summary>
    private sealed record EditorCommand(
        string Key,
        string Menu,
        string Label,
        string? Gesture = null,
        string[]? Keys = null,
        bool Implemented = true,
        bool SeparatorBefore = false);

    private static readonly IReadOnlyList<(string Id, string Header)> Menus =
    [
        ("File", MenuText.Menu_File),
        ("Edit", MenuText.Menu_Edit),
        ("Image", MenuText.Menu_Image),
        ("View", MenuText.Menu_View),
    ];

    private static readonly IReadOnlyList<EditorCommand> CommandTable =
    [
        new("Export", "File", EditorText.Cmd_Export, "Ctrl+Shift+S"),
        new("Close", "File", EditorText.Cmd_Close, "Ctrl+W", SeparatorBefore: true),

        new("Undo", "Edit", EditorText.Cmd_Undo, "Ctrl+Z"),
        new("Redo", "Edit", EditorText.Cmd_Redo, "Ctrl+Y", Keys: ["Ctrl+Y", "Ctrl+Shift+Z"]),
        new("Copy", "Edit", EditorText.Cmd_Copy, "Ctrl+C", SeparatorBefore: true),
        new("CopyPath", "Edit", EditorText.Cmd_CopyPath, "Ctrl+Shift+C"),

        new("RotateRight", "Image", EditorText.Cmd_RotateRight, "Ctrl+]", Keys: ["Ctrl+OemCloseBrackets"]),
        new("RotateLeft", "Image", EditorText.Cmd_RotateLeft, "Ctrl+[", Keys: ["Ctrl+OemOpenBrackets"]),
        new("Rotate180", "Image", EditorText.Cmd_Rotate180),
        new("FlipHorizontal", "Image", EditorText.Cmd_FlipHorizontal),
        new("FlipVertical", "Image", EditorText.Cmd_FlipVertical),
        new("Crop", "Image", EditorText.Cmd_Crop, SeparatorBefore: true),
        // Later phases (P7/P8, gate G1): visible, disabled.
        new("Layers", "Image", EditorText.Cmd_Layers, Implemented: false, SeparatorBefore: true),
        new("Text", "Image", EditorText.Cmd_Text, Implemented: false),
        new("Brush", "Image", EditorText.Cmd_Brush, Implemented: false),
        new("MosaicBlur", "Image", EditorText.Cmd_MosaicBlur, Implemented: false),
        new("BackgroundEraser", "Image", EditorText.Cmd_BackgroundEraser, Implemented: false),
        new("HealingBrush", "Image", EditorText.Cmd_HealingBrush, Implemented: false),
        new("RemoveBackground", "Image", EditorText.Cmd_RemoveBackground, Implemented: false),

        // Same keys as the viewer.
        new("Fit", "View", MenuText.Label_ZoomFit, "*", Keys: ["Multiply", "Shift+D8"]),
        new("ActualSize", "View", MenuText.Label_ZoomActual, "0", Keys: ["D0", "NumPad0", "Ctrl+D0", "Ctrl+NumPad0"]),
        new("ZoomIn", "View", MenuText.Label_ZoomIn, "+", Keys: ["OemPlus", "Shift+OemPlus", "Add", "Ctrl+OemPlus", "Ctrl+Add"], SeparatorBefore: true),
        new("ZoomOut", "View", MenuText.Label_ZoomOut, "-", Keys: ["OemMinus", "Subtract", "Ctrl+OemMinus", "Ctrl+Subtract"]),
    ];

    /// <summary>Toolbar groups (command key, Segoe icon glyph). Every entry is also a menu command.</summary>
    private static readonly IReadOnlyList<IReadOnlyList<(string Key, string Glyph)>> Toolbar =
    [
        [("Copy", "\uE8C8"), ("CopyPath", "\uE71B")],
        [("Export", "\uE74E")],
        [("Undo", "\uE7A7"), ("Redo", "\uE7A6")],
        [("RotateRight", "\uE7AD"), ("Crop", "\uE7A8")],
        [("Fit", "\uE9A6"), ("ZoomIn", "\uE8A3"), ("ZoomOut", "\uE71F")],
    ];

    /// <summary>The edited file; null in capture mode.</summary>
    private readonly string? _path;

    /// <summary>When the screenshot was taken; null in file mode.</summary>
    private readonly DateTime? _capturedAt;

    private readonly string _name;
    private readonly EditSession _session;
    private readonly Dictionary<string, RoutedUICommand> _commands = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _renderTimer;
    private readonly DispatcherTimer _commitTimer;

    /// <summary>Adjustments shown on the canvas. Differs from the session while a slider is being changed (not yet committed).</summary>
    private AdjustmentSettings _live;

    /// <summary>
    /// State last written by Export (or, in capture mode, handed off by Copy/CopyPath); the window is "dirty" while the
    /// session differs from it.
    /// </summary>
    private RasterImage _savedBase;
    private AdjustmentSettings _savedSettings = AdjustmentSettings.Identity;

    /// <summary>Base image the canvas layout and preset thumbnails were made for.</summary>
    private RasterImage? _layoutBase;
    private double _zoom = 1;
    private bool _fit = true;
    private bool _busy;
    private bool _closed;
    private bool _rendering;
    private int _renderVersion;
    private int _reportedDrops;

    private EditorWindow(string? path, DateTime? capturedAt, string name, EditSession session)
    {
        _path = path;
        _capturedAt = capturedAt;
        _name = name;
        _session = session;
        _live = session.Settings;
        _savedBase = session.Base;
        InitializeComponent();
        Title = string.Format(EditorText.Window_Title, _name);
        AutomationProperties.SetName(this, Title);

        _renderTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(40) };
        _renderTimer.Tick += OnRenderTick;
        // Keyboard and track clicks on a slider commit once the value has rested (one undo step per change burst).
        _commitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _commitTimer.Tick += (_, _) => CommitAdjustments();

        BuildMenus();
        BuildToolbar();
        BuildRotatePanel();
        BuildAdjustPanel();
        BuildPresetPanel();
        InitCrop();
        InitDraw();

        CanvasScroll.SizeChanged += (_, _) => { if (_fit) ApplyZoom(); };
        CanvasScroll.PreviewMouseWheel += OnCanvasWheel;
        KeyDown += OnWindowKeyDown;
        SourceInitialized += (_, _) => NativeMethods.ApplyDarkTitleBar(this);
        Loaded += (_, _) =>
        {
            SyncFromSession();
            // Capture mode starts by choosing the area: the crop layer has its size by now.
            if (_capturedAt is not null) Run("Crop");
        };
        Closing += OnClosing;
        Closed += OnClosed;
    }

    // ───────────── opening and closing (static entry points) ─────────────

    /// <summary>
    /// Opens <paramref name="path"/> in a new editor window, or activates the window that already edits it. Too large
    /// images and decode failures are reported with the message dialog over <paramref name="owner"/>.
    /// </summary>
    public static async Task OpenAsync(Window owner, string path)
    {
        var existing = Application.Current.Windows.OfType<EditorWindow>()
            .FirstOrDefault(w => !w._closed && string.Equals(w._path, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        string title = string.Format(EditorText.Open_FailedTitle, Path.GetFileName(path));
        EditSession session;
        try
        {
            // Size check before the full decode: a refused image is never decoded at full size.
            var info = await Task.Run(() => ImageLoader.ReadInfo(path));
            if (!EditSession.CanEdit(info.Width, info.Height, out long estimate))
            {
                MessageDialog.Error(owner, title, TooLargeText(info.Width, info.Height, estimate));
                return;
            }
            session = await Task.Run(() => new EditSession(RasterImage.FromBitmap(ImageLoader.LoadForDisplay(path, autoRotate: true))));
        }
        catch (Exception ex) when (ex is ImageOperationException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            MessageDialog.Error(owner, title, MainWindow.DescribeLoadError(ex));
            return;
        }
        var window = new EditorWindow(path, null, Path.GetFileName(path), session);
        window.Show();
    }

    /// <summary>Closes every editor window; each asks about unsaved changes. False when one of them stays open.</summary>
    public static bool CloseAll()
    {
        foreach (var window in Application.Current.Windows.OfType<EditorWindow>().ToList())
        {
            if (window._closed) continue;
            window.Close();
            if (!window._closed) return false;
        }
        return true;
    }

    private static string TooLargeText(int width, int height, long estimate) =>
        string.Format(EditorText.Open_TooLarge, width, height, FormatBytes(estimate), FormatBytes(EditSession.MaxEditBytes));

    /// <summary>File/memory size for display: KB below 1 MB, otherwise MB with one decimal (1 KB = 1024 bytes).</summary>
    internal static string FormatBytes(long bytes)
    {
        const double Kilo = 1024, Mega = 1024 * 1024;
        if (bytes < Mega)
            return string.Format(EditorText.Size_Bytes, Math.Max(1, (long)Math.Ceiling(bytes / Kilo)).ToString("N0", CultureInfo.CurrentCulture));
        return string.Format(EditorText.Size_Megabytes, (bytes / Mega).ToString("N1", CultureInfo.CurrentCulture));
    }

    private bool IsDirty =>
        !ReferenceEquals(_session.Base, _savedBase) || _session.Settings != _savedSettings || _live != _savedSettings;

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_busy)
        {
            e.Cancel = true;
            return;
        }
        CancelStroke();
        if (_cropping) EndCrop();
        CommitAdjustments();
        if (!IsDirty) return;
        int choice = MessageDialog.Show(this, DialogKind.Question, string.Format(EditorText.Close_Heading, _name), EditorText.Close_Body, null,
            [EditorText.Close_Save, EditorText.Close_DontSave, DialogText.Cancel_Button], defaultIndex: 0, cancelIndex: 2);
        if (choice == 2 || (choice == 0 && !Export())) e.Cancel = true;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _renderTimer.Stop();
        _commitTimer.Stop();
        CanvasImage.Source = null;
    }

    // ───────────── commands: menus, toolbar, key bindings ─────────────

    private RoutedUICommand Command(string key)
    {
        if (_commands.TryGetValue(key, out var existing)) return existing;
        var spec = CommandTable.First(c => c.Key == key);
        var command = new RoutedUICommand(spec.Label, key, typeof(EditorWindow));
        _commands[key] = command;
        CommandBindings.Add(new CommandBinding(command,
            (_, e) => { e.Handled = true; Run(key); },
            (_, e) => { e.CanExecute = CanRun(key); e.Handled = true; }));
        string[] chords = spec.Keys ?? (spec.Gesture is null ? [] : [spec.Gesture]);
        foreach (var chord in chords)
            InputBindings.Add(new InputBinding(command, KeyChord.Parse(chord)));
        return command;
    }

    private void BuildMenus()
    {
        foreach (var (id, header) in Menus)
        {
            var top = new MenuItem { Header = header };
            AutomationProperties.SetAutomationId(top, "OP-Editor-Menu-" + id);
            foreach (var spec in CommandTable.Where(c => c.Menu == id))
            {
                if (spec.SeparatorBefore && top.Items.Count > 0) top.Items.Add(new Separator());
                var item = new MenuItem { InputGestureText = spec.Gesture ?? "" };
                AutomationProperties.SetAutomationId(item, "OP-Editor-Menu-" + spec.Key);
                if (spec.Implemented)
                {
                    item.Header = spec.Label;
                    item.Command = Command(spec.Key);
                    item.CommandTarget = this;
                }
                else
                {
                    // Later phase: visible but not clickable. No enabled no-op.
                    item.Header = spec.Label + CommandCatalog.PendingSuffix;
                    item.IsEnabled = false;
                    item.ToolTip = MenuText.ComingSoon_ToolTip;
                }
                AutomationProperties.SetName(item, (string)item.Header);
                top.Items.Add(item);
            }
            EditorMenu.Items.Add(top);
        }
    }

    private void BuildToolbar()
    {
        var style = (Style)FindResource("Op.ToolbarSmallButton");
        for (int g = 0; g < Toolbar.Count; g++)
        {
            if (g > 0)
            {
                var separator = new Border { Width = 1, Margin = new Thickness(8, 6, 8, 6), Focusable = false };
                separator.SetResourceReference(Border.BackgroundProperty, ThemeManager.Border);
                ToolbarPanel.Children.Add(separator);
            }
            foreach (var (key, glyph) in Toolbar[g])
            {
                var spec = CommandTable.First(c => c.Key == key);
                var button = new Button
                {
                    Tag = glyph,
                    Style = style,
                    Command = Command(key),
                    CommandTarget = this,
                    ToolTip = spec.Gesture is null ? spec.Label : $"{spec.Label} ({spec.Gesture})",
                };
                AutomationProperties.SetName(button, spec.Label);
                AutomationProperties.SetAutomationId(button, "OP-Editor-" + key);
                ToolbarPanel.Children.Add(button);
            }
        }
    }

    /// <summary>Inspector rotate/flip buttons and the crop start button run the same commands as the menu.</summary>
    private void BuildRotatePanel()
    {
        var style = (Style)FindResource("Ed.PanelButton");
        foreach (var key in new[] { "RotateLeft", "RotateRight", "Rotate180", "FlipHorizontal", "FlipVertical" })
        {
            var spec = CommandTable.First(c => c.Key == key);
            var button = new Button { Content = spec.Label, Style = style, Command = Command(key), CommandTarget = this };
            AutomationProperties.SetName(button, spec.Label);
            AutomationProperties.SetAutomationId(button, "OP-Editor-Panel-" + key);
            RotatePanel.Children.Add(button);
        }
        CropStartButton.Command = Command("Crop");
        CropStartButton.CommandTarget = this;
    }

    /// <summary>The one enable rule for menus, toolbar, inspector buttons and keys.</summary>
    private bool CanRun(string key)
    {
        if (key == "Close") return true;
        if (_busy || _closed) return false;
        return key switch
        {
            "Export" or "Copy" or "CopyPath" or "Crop" or "RotateRight" or "RotateLeft" or "Rotate180" or "FlipHorizontal" or "FlipVertical" => !_cropping,
            "Undo" => !_cropping && (_session.CanUndo || _live != _session.Settings),
            // A pending slider change is committed before Redo would run, and that clears the redo list.
            "Redo" => !_cropping && _session.CanRedo && _live == _session.Settings,
            "CropApply" or "CropCancel" => _cropping,
            "ZoomIn" => _zoom < MaxZoom - 1e-9,
            "ZoomOut" => _zoom > MinZoom + 1e-9,
            "Fit" or "ActualSize" => true,
            _ => false,
        };
    }

    private async void Run(string key)
    {
        if (!CanRun(key)) return;
        try
        {
            await ExecuteAsync(key);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SetBusy(false);
            MessageDialog.Error(this, MainText.Error_CommandFailedTitle, string.Format(MainText.Error_CommandFailedBody, ex.Message));
            SyncFromSession();
        }
        UpdateState();
    }

    private async Task ExecuteAsync(string key)
    {
        switch (key)
        {
            case "Export": Export(); break;
            case "Close": Close(); break;
            case "Copy": await CopyImageAsync(); break;
            case "CopyPath": await CopyPathAsync(); break;
            case "Undo":
                CommitAdjustments();
                if (_session.CanUndo) _session.Undo();
                SyncFromSession();
                break;
            case "Redo":
                _session.Redo();
                SyncFromSession();
                break;
            case "RotateRight": await EditAsync(s => s.Rotate(RotateFlip.Rotate90)); break;
            case "RotateLeft": await EditAsync(s => s.Rotate(RotateFlip.Rotate270)); break;
            case "Rotate180": await EditAsync(s => s.Rotate(RotateFlip.Rotate180)); break;
            case "FlipHorizontal": await EditAsync(s => s.Rotate(RotateFlip.FlipHorizontal)); break;
            case "FlipVertical": await EditAsync(s => s.Rotate(RotateFlip.FlipVertical)); break;
            case "Crop":
                SelectTool(null);
                StartCrop();
                break;
            case "CropApply": await ApplyCropAsync(); break;
            case "CropCancel": CancelCrop(); break;
            case "Fit":
                _fit = true;
                ApplyZoom();
                break;
            case "ActualSize": SetZoom(1); break;
            case "ZoomIn": SetZoom(_zoom * ZoomFactor); break;
            case "ZoomOut": SetZoom(_zoom / ZoomFactor); break;
        }
    }

    /// <summary>Runs a base-image session step (rotate, flip, crop, draw) off the UI thread; editing is blocked meanwhile.</summary>
    private async Task EditAsync(Action<EditSession> edit)
    {
        CommitAdjustments();
        SetBusy(true);
        try
        {
            var session = _session;
            await Task.Run(() => edit(session));
        }
        finally
        {
            SetBusy(false);
        }
        SyncFromSession();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        InspectorPanel.IsEnabled = !busy;
        Cursor = busy ? Cursors.Wait : null;
        if (busy) ShowNotice(EditorText.Status_Working);
        else if (StatusText.Text == EditorText.Status_Working) ShowNotice("");
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// Esc cancels a stroke being drawn first, then an active crop (in capture mode the Pen comes on). In capture mode an
    /// Esc with neither closes the window (asking only when there are changes that were not copied or exported).
    /// </summary>
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (e.Key == Key.Escape && _stroking)
        {
            e.Handled = true;
            CancelStroke();
        }
        else if (_cropping)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Run("CropApply");
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Run("CropCancel");
            }
        }
        else if (e.Key == Key.Escape && _capturedAt is not null)
        {
            e.Handled = true;
            Close();
        }
    }

    // ───────────── export ─────────────

    /// <summary>Shows the export dialog for the current result (modal). True when a file was written.</summary>
    private bool Export()
    {
        CommitAdjustments();
        var source = _session.Base;
        var settings = _session.Settings;
        var dialog = CreateExportDialog(source, settings);
        dialog.Owner = this;
        if (dialog.ShowDialog() != true || dialog.SavedPath is not { } saved) return false;
        _savedBase = source;
        _savedSettings = settings;
        ShowNotice(string.Format(EditorText.Status_Exported, Path.GetFileName(saved)));
        UpdateState();
        return true;
    }

    // ───────────── session → screen ─────────────

    /// <summary>Re-reads the session after any committed change, undo or redo: sliders, layout, thumbnails, preview, status.</summary>
    private void SyncFromSession()
    {
        if (_closed) return;
        _commitTimer.Stop();
        _live = _session.Settings;
        SetSliders(_live);
        if (!ReferenceEquals(_layoutBase, _session.Base))
        {
            _layoutBase = _session.Base;
            ApplyZoom();
            RefreshPresetThumbnails();
        }
        else
        {
            RequestRender();
        }
        ReportDroppedHistory();
        UpdateState();
    }

    private void ReportDroppedHistory()
    {
        if (_session.DroppedHistory <= _reportedDrops) return;
        _reportedDrops = _session.DroppedHistory;
        ShowNotice(EditorText.Status_HistoryDropped);
    }

    private void ShowNotice(string text) => StatusText.Text = text;

    private void UpdateState()
    {
        if (_closed) return;
        var b = _session.Base;
        SizeText.Text = string.Format(EditorText.Size_Pixels, b.Width, b.Height);
        ZoomText.Text = string.Format(EditorText.Status_Zoom, Math.Round(_zoom * 100).ToString(CultureInfo.CurrentCulture));
        UnsavedText.Visibility = IsDirty ? Visibility.Visible : Visibility.Collapsed;
        CropApplyButton.IsEnabled = _cropping && !_busy;
        CropCancelButton.IsEnabled = _cropping && !_busy;
        var cropping = _cropping ? Visibility.Visible : Visibility.Collapsed;
        CropSizeText.Visibility = cropping;
        CropDragHint.Visibility = cropping;
        CropHint.Visibility = cropping;
        CommandManager.InvalidateRequerySuggested();
    }

    // ───────────── zoom ─────────────

    private void SetZoom(double zoom)
    {
        _fit = false;
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        ApplyZoom();
    }

    /// <summary>Sizes the image box (and crop layer) to base size × zoom; in Fit mode the zoom follows the canvas size.</summary>
    private void ApplyZoom()
    {
        if (_closed) return;
        var b = _session.Base;
        if (_fit)
        {
            double w = CanvasScroll.ActualWidth - 2 * CanvasMargin, h = CanvasScroll.ActualHeight - 2 * CanvasMargin;
            _zoom = w > 1 && h > 1 ? Math.Clamp(Math.Min(w / b.Width, h / b.Height), MinZoom, MaxZoom) : 1;
        }
        var bars = _fit ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        CanvasScroll.HorizontalScrollBarVisibility = bars;
        CanvasScroll.VerticalScrollBarVisibility = bars;
        CanvasImage.Width = b.Width * _zoom;
        CanvasImage.Height = b.Height * _zoom;
        CropLayer.Width = CanvasImage.Width;
        CropLayer.Height = CanvasImage.Height;
        DrawLayer.Width = CanvasImage.Width;
        DrawLayer.Height = CanvasImage.Height;
        // A stroke's preview is in display coordinates of the old size.
        CancelStroke();
        UpdateCropVisuals();
        RequestRender();
        UpdateState();
    }

    private void OnCanvasWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        e.Handled = true;
        SetZoom(e.Delta > 0 ? _zoom * ZoomFactor : _zoom / ZoomFactor);
    }

    // ───────────── preview render ─────────────

    /// <summary>Asks for a new canvas render. Debounced; one render runs at a time and the newest request wins.</summary>
    private void RequestRender()
    {
        _renderVersion++;
        _renderTimer.Stop();
        _renderTimer.Start();
    }

    private async void OnRenderTick(object? sender, EventArgs e)
    {
        _renderTimer.Stop();
        // A running render loops again when it sees a newer version.
        if (_rendering || _closed) return;
        _rendering = true;
        try
        {
            while (!_closed)
            {
                int version = _renderVersion;
                var source = _session.Base;
                var settings = _live;
                int side = PreviewSide(source);
                var bitmap = await Task.Run(() => Adjustments.ApplyPreview(source, settings, side).ToBitmap());
                if (version != _renderVersion) continue;
                if (!_closed) CanvasImage.Source = bitmap;
                break;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowNotice(string.Format(EditorText.Status_PreviewFailed, ex.Message));
        }
        finally
        {
            _rendering = false;
        }
    }

    /// <summary>Long side of the preview in device pixels: the displayed size, never more than the image.</summary>
    private int PreviewSide(RasterImage source)
    {
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int full = Math.Max(source.Width, source.Height);
        return Math.Clamp((int)Math.Ceiling(full * _zoom * dpi), 1, full);
    }
}
