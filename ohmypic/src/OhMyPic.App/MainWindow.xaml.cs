using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using OhMyPic.App.Commands;
using OhMyPic.App.Dialogs;
using OhMyPic.App.Models;
using OhMyPic.App.Settings;
using OhMyPic.App.Text;
using OhMyPic.App.Theme;
using OhMyPic.Core;

namespace OhMyPic.App;

/// <summary>
/// The main window: one window, two modes (browser and viewer). This file: command table → menus, toolbars and key
/// bindings, the single enable rule (<see cref="CanRun"/>), command dispatch, mode switching, title and status bar.
/// Browser logic is in MainWindow.Browser.cs, the viewer in MainWindow.Viewer.cs, file commands in MainWindow.Files.cs.
/// </summary>
public partial class MainWindow : Window
{
    public static readonly DependencyProperty TileWidthProperty = DependencyProperty.Register(nameof(TileWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(174.0));
    public static readonly DependencyProperty TileHeightProperty = DependencyProperty.Register(nameof(TileHeight), typeof(double), typeof(MainWindow), new PropertyMetadata(194.0));
    public static readonly DependencyProperty TileInnerWidthProperty = DependencyProperty.Register(nameof(TileInnerWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(162.0));
    public static readonly DependencyProperty TileInnerHeightProperty = DependencyProperty.Register(nameof(TileInnerHeight), typeof(double), typeof(MainWindow), new PropertyMetadata(188.0));

    /// <summary>Tile cell size in the thumbnail area (the item container, including its padding).</summary>
    public double TileWidth { get => (double)GetValue(TileWidthProperty); set => SetValue(TileWidthProperty, value); }
    public double TileHeight { get => (double)GetValue(TileHeightProperty); set => SetValue(TileHeightProperty, value); }

    /// <summary>Tile content size (cell minus the item container padding).</summary>
    public double TileInnerWidth { get => (double)GetValue(TileInnerWidthProperty); set => SetValue(TileInnerWidthProperty, value); }
    public double TileInnerHeight { get => (double)GetValue(TileInnerHeightProperty); set => SetValue(TileInnerHeightProperty, value); }

    private enum Mode { Browser, Viewer }

    private readonly AppSettings S;
    private readonly UiState _ui;
    private readonly string? _startupPath;
    private readonly bool _startSlideshow;
    private Mode _mode = Mode.Browser;
    private string? _notice;
    private bool _busy;
    private bool _closeConfirmed;

    private readonly Dictionary<string, List<MenuItem>> _menuItems = new(StringComparer.Ordinal);

    /// <param name="startSlideshow">With a file as <paramref name="startupPath"/>: start the slideshow from it (--slideshow).</param>
    public MainWindow(AppSettings settings, string? startupPath, bool startSlideshow = false)
    {
        S = settings;
        _ui = UiState.Load(UiState.DefaultPath);
        _startupPath = startupPath;
        _startSlideshow = startSlideshow;
        // A settings file that could not be read is not worth an alert: defaults are in use, say so in the status bar.
        // A missing file on first start is normal and not reported.
        if (settings.LoadError is not null && File.Exists(AppSettings.DefaultPath))
            _notice = "\u26A0 " + string.Format(MainText.Settings_LoadFailed, settings.LoadError);

        InitializeComponent();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _refreshTimer.Tick += OnRefreshTimer;

        BindCommands();
        BuildMenus();
        BuildToolbars();
        InitBrowser();
        InitViewer();

        PreviewDragEnter += OnPreviewDragOver;
        PreviewDragOver += OnPreviewDragOver;
        PreviewDrop += OnPreviewDrop;
        SourceInitialized += (_, _) =>
        {
            RestorePlacement();
            NativeMethods.ApplyDarkTitleBar(this);
        };
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
        ApplySettings();
        UpdateUi();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        StartAutoUpdate();
        await LoadFolderTreeAsync();
        var path = _startupPath;
        if (path is not null && File.Exists(path))
        {
            await OpenFileInViewerAsync(path);
            if (_startSlideshow)
            {
                await StartSlideshowAsync();
                UpdateUi();
            }
            return;
        }
        if (path is not null && Directory.Exists(path))
        {
            await NavigateToAsync(path);
            return;
        }
        if (path is not null) _notice = "\u26A0 " + string.Format(MainText.Error_PathNotFound, path);
        await NavigateToAsync(StartFolder());
    }

    /// <summary>Last folder when it still exists, otherwise the user's Pictures folder.</summary>
    private string StartFolder()
    {
        if (!string.IsNullOrWhiteSpace(S.LastFolder) && Directory.Exists(S.LastFolder)) return S.LastFolder;
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (!string.IsNullOrEmpty(pictures) && Directory.Exists(pictures)) return pictures;
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    // ───────────── command table → bindings, menus, toolbars ─────────────

    private void BindCommands()
    {
        foreach (var key in CommandCatalog.ImplementedKeys)
            CommandBindings.Add(new CommandBinding(CommandCatalog.Command(key), OnCommandExecuted, OnCommandCanExecute));
        // One input binding per distinct chord. The chord runs the first of its commands that can run in the current
        // mode (Enter = View Image in the browser, Full Screen in the viewer); otherwise the key goes on to the control.
        CommandBindings.Add(new CommandBinding(CommandCatalog.Shortcut, OnShortcutExecuted, OnShortcutCanExecute));
        foreach (var (chord, keys) in CommandCatalog.Chords())
            InputBindings.Add(new InputBinding(CommandCatalog.Shortcut, chord) { CommandParameter = keys });
    }

    private void BuildMenus()
    {
        foreach (var (id, header) in CommandCatalog.Menus)
        {
            var top = new MenuItem { Header = header };
            AutomationProperties.SetAutomationId(top, "menu." + id);
            foreach (var spec in CommandCatalog.Items.Where(i => i.Menu == id))
            {
                if (spec.SeparatorBefore && top.Items.Count > 0) top.Items.Add(new Separator());
                top.Items.Add(CreateMenuItem(spec.Key, "menu."));
            }
            MainMenu.Items.Add(top);
        }
    }

    /// <summary>Menu item for a command key (menu bar, tile context menu, toolbar drop-downs share this).</summary>
    private MenuItem CreateMenuItem(string key, string idPrefix)
    {
        var spec = CommandCatalog.Spec(key);
        var item = new MenuItem { InputGestureText = spec.Gesture ?? "" };
        AutomationProperties.SetAutomationId(item, idPrefix + key);
        if (spec.Implemented)
        {
            item.Header = spec.Label;
            item.Command = CommandCatalog.Command(key);
            item.CommandTarget = this;
            // Toggle/choice items expose their state through UIA TogglePattern; UpdateUi resets IsChecked from the settings.
            item.IsCheckable = spec.Checkable;
            if (!_menuItems.TryGetValue(key, out var list)) _menuItems[key] = list = [];
            list.Add(item);
        }
        else
        {
            // Not implemented yet: visible but not clickable. No enabled no-op.
            item.Header = spec.Label + CommandCatalog.PendingSuffix;
            item.IsEnabled = false;
            item.ToolTip = MenuText.ComingSoon_ToolTip;
        }
        return item;
    }

    private ContextMenu CreateContextMenu(IEnumerable<string?> keys, string idPrefix)
    {
        var menu = new ContextMenu();
        foreach (var key in keys)
        {
            if (key is null) menu.Items.Add(new Separator());
            else menu.Items.Add(CreateMenuItem(key, idPrefix));
        }
        return menu;
    }

    private void BuildToolbars()
    {
        AddToolbar(BrowserToolbarPanel, CommandCatalog.BrowserToolbar, "toolbar.browser.");
        AddToolbar(ViewerToolbarPanel, CommandCatalog.ViewerToolbar, "toolbar.viewer.");
    }

    private void AddToolbar(Panel panel, IReadOnlyList<IReadOnlyList<ToolbarSpec>> groups, string idPrefix)
    {
        var style = (Style)FindResource("Op.ToolbarButton");
        for (int g = 0; g < groups.Count; g++)
        {
            if (g > 0)
            {
                var separator = new Border { Width = 1, Margin = new Thickness(8, 6, 8, 6), Focusable = false };
                separator.SetResourceReference(Border.BackgroundProperty, ThemeManager.Border);
                panel.Children.Add(separator);
            }
            foreach (var t in groups[g])
            {
                var button = new Button { Content = t.Label, Tag = t.Glyph, Style = style };
                AutomationProperties.SetName(button, t.Label);
                AutomationProperties.SetAutomationId(button, idPrefix + t.Key);
                if (t.DropDown is { } dropDown)
                {
                    // Drop-down: every entry is also a menu bar command (OP-A05).
                    button.Content = t.Label + " \u25BE";
                    button.ToolTip = t.Label;
                    var menu = CreateContextMenu(dropDown, idPrefix + t.Key + ".");
                    menu.PlacementTarget = button;
                    menu.Placement = PlacementMode.Bottom;
                    button.ContextMenu = menu;
                    button.Click += (_, _) => menu.IsOpen = true;
                    AutomationProperties.SetHelpText(button, MenuText.DropDown_HelpText);
                }
                else
                {
                    var spec = CommandCatalog.Spec(t.Key);
                    button.Command = CommandCatalog.Command(t.Key);
                    button.CommandTarget = this;
                    button.ToolTip = spec.Gesture is null ? spec.Label : $"{spec.Label} ({spec.Gesture})";
                }
                panel.Children.Add(button);
            }
        }
    }

    // ───────────── dispatch ─────────────

    private void OnCommandCanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = e.Command is RoutedUICommand c && CanRun(c.Name);
        e.Handled = true;
    }

    private void OnCommandExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command is not RoutedUICommand c) return;
        e.Handled = true;
        Run(c.Name);
    }

    private void OnShortcutCanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = e.Parameter is IReadOnlyList<string> keys && keys.Any(k => CanRunFromKeyboard(k));
        // No command applies in this mode: let the key continue to the focused control (text box, list, tree).
        e.ContinueRouting = !e.CanExecute;
        e.Handled = true;
    }

    private void OnShortcutExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        e.Handled = true;
        if (e.Parameter is not IReadOnlyList<string> keys) return;
        var key = keys.FirstOrDefault(k => CanRunFromKeyboard(k));
        if (key is not null) Run(key);
    }

    /// <summary>
    /// File commands act on the selected tiles; while the folder tree has keyboard focus their keys (Del, F2, Ctrl+C...)
    /// belong to the tree, so they are not taken from it. Menus and toolbar buttons still run them.
    /// </summary>
    private bool CanRunFromKeyboard(string key)
    {
        if (FolderTree.IsKeyboardFocusWithin && key is "edit.copy" or "edit.cut" or "edit.delete" or "edit.rename" or "edit.selectAll"
                or "edit.copyImage" or "file.properties")
            return false;
        return CanRun(key);
    }

    private async void Run(string key)
    {
        if (!CanRun(key)) return;
        // A new command clears the previous notice.
        _notice = null;
        try
        {
            await ExecuteAsync(key);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _busy = false;
            MessageDialog.Error(this, MainText.Error_CommandFailedTitle, string.Format(MainText.Error_CommandFailedBody, ex.Message));
        }
        UpdateUi();
    }

    private async Task ExecuteAsync(string key)
    {
        if (key.StartsWith("view.format.", StringComparison.Ordinal))
        {
            SetViewFormat(FormatKeys[key]);
            return;
        }
        if (key.StartsWith("view.sort.", StringComparison.Ordinal))
        {
            await SetSortAsync(key);
            return;
        }
        switch (key)
        {
            case "file.openFile": await OpenFileAsync(); break;
            case "file.openFolder": await OpenFolderAsync(); break;
            case "file.saveAs": await SaveAsAsync(); break;
            case "file.saveRotation": await SaveRotationExplicitAsync(); break;
            case "file.print": await PrintAsync(); break;
            case "file.properties": ShowProperties(); break;
            case "file.exit": Close(); break;

            case "edit.copy": CopyFilesToClipboard(cut: false); break;
            case "edit.cut": CopyFilesToClipboard(cut: true); break;
            case "edit.paste": await PasteAsync(); break;
            case "edit.copyImage": await CopyImageAsync(); break;
            case "edit.delete": await DeleteAsync(); break;
            case "edit.rename": await RenameAsync(); break;
            case "edit.selectAll": ActiveList.SelectAll(); break;

            case "view.browser":
                // Esc leaves full screen first, then returns to the browser.
                if (_fullScreen) SetFullScreen(false);
                else await LeaveViewerAsync();
                break;
            case "view.viewer": await EnterViewerFromSelectionAsync(); break;
            case "view.pane.preview": _ui.ShowPreviewPane = !_ui.ShowPreviewPane; ApplyPanes(); UpdateInspector(); break;
            case "view.pane.camera": _ui.ShowCameraPane = !_ui.ShowCameraPane; ApplyPanes(); UpdateInspector(); break;
            case "view.folderTree": _ui.ShowFolderTree = !_ui.ShowFolderTree; ApplyPanes(); break;
            case "nav.up": await GoUpAsync(); break;
            case "view.refresh": await LoadFolderAsync(SelectedPaths()); break;
            case "zoom.in": ZoomStep(+1, null); break;
            case "zoom.out": ZoomStep(-1, null); break;
            case "zoom.actual": SetActualSize(); break;
            case "zoom.fit": FitToWindow(); break;
            case "nav.next": await StepAsync(+1); break;
            case "nav.previous": await StepAsync(-1); break;
            case "nav.first": await GoToAsync(0); break;
            case "nav.last": await GoToAsync(_viewList.Count - 1); break;
            case "view.fullScreen": SetFullScreen(!_fullScreen); break;
            case "view.alwaysOnTop": S.AlwaysOnTop = !S.AlwaysOnTop; Topmost = S.AlwaysOnTop; SaveSettings(showError: false); break;

            case "image.rotateRight": RotateView(90); break;
            case "image.rotateLeft": RotateView(-90); break;
            case "image.rotateDialog":
            case "image.resizeDialog":
            case "image.convertDialog":
            case "image.batchDialog":
                await RunEditDialogAsync(key);
                break;

            case "slideshow.start": await StartSlideshowAsync(); break;

            case "tools.settings": await OpenPreferencesAsync(PreferencesWindow.GeneralPage); break;
            case "settings.view.fitMode": await OpenPreferencesAsync(PreferencesWindow.ViewPage); break;
            case "settings.language": await OpenPreferencesAsync(PreferencesWindow.GeneralPage); break;
            case "settings.associations": await OpenPreferencesAsync(PreferencesWindow.AssociationsPage); break;
            case "settings.shellMenu": await OpenPreferencesAsync(PreferencesWindow.ShellMenuPage); break;

            case "help.contents": OpenHelp(); break;
            case "help.update": await CheckForUpdatesAsync(); break;
            case "help.about": new AboutWindow { Owner = this }.ShowDialog(); break;
        }
    }

    private static readonly Dictionary<string, ViewFormat> FormatKeys = new(StringComparer.Ordinal)
    {
        ["view.format.thumbnails"] = ViewFormat.Thumbnails,
        ["view.format.largeIcons"] = ViewFormat.LargeIcons,
        ["view.format.icons"] = ViewFormat.Icons,
        ["view.format.list"] = ViewFormat.List,
        ["view.format.details"] = ViewFormat.Details,
        ["view.format.filmstrip"] = ViewFormat.Filmstrip,
    };

    private static readonly Dictionary<string, SortKey> SortKeys = new(StringComparer.Ordinal)
    {
        ["view.sort.name"] = SortKey.Name,
        ["view.sort.type"] = SortKey.Type,
        ["view.sort.size"] = SortKey.Size,
        ["view.sort.date"] = SortKey.Date,
    };

    private bool InBrowser => _mode == Mode.Browser;
    private bool InViewer => _mode == Mode.Viewer;

    /// <summary>
    /// The one enable rule. Menu items, toolbar buttons, the tile context menu and key chords all ask this.
    /// </summary>
    private bool CanRun(string key)
    {
        if (key is "file.exit" or "help.about" or "help.contents") return true;
        if (_busy) return false;
        int selected = InBrowser ? SelectedCount : 0;
        bool hasCurrent = InViewer && CurrentPath is not null;
        bool hasImage = InViewer && _bitmap is not null;
        if (key.StartsWith("view.format.", StringComparison.Ordinal) || key.StartsWith("view.sort.", StringComparison.Ordinal))
            return InBrowser;
        return key switch
        {
            "file.openFile" or "file.openFolder" or "tools.settings" or "settings.view.fitMode" or "settings.language" or "settings.associations" or "settings.shellMenu" or "view.alwaysOnTop" => true,
            "help.update" => !_updating,
            "file.saveAs" or "file.print" or "edit.copyImage" => InViewer ? hasImage : selected == 1,
            "file.saveRotation" => hasImage && _rotation != 0,
            "file.properties" or "edit.rename" => InViewer ? hasCurrent : selected == 1,
            "edit.copy" or "edit.cut" or "edit.delete" => InViewer ? hasCurrent : selected > 0,
            "image.rotateDialog" or "image.resizeDialog" or "image.convertDialog" => InViewer ? hasCurrent : selected > 0,
            "image.batchDialog" => InViewer ? hasCurrent : _folder is not null,
            "edit.paste" => CurrentFolder is not null && ClipboardHasFiles(),
            "edit.selectAll" => InBrowser && _items.Count > 0 && !AddressBox.IsKeyboardFocusWithin,
            "view.browser" => InViewer,
            "view.viewer" => InBrowser && FocusedItem is not null && !FolderTree.IsKeyboardFocusWithin && !AddressBox.IsKeyboardFocusWithin,
            "view.pane.preview" or "view.pane.camera" or "view.folderTree" => InBrowser,
            "view.refresh" => InBrowser && _folder is not null,
            "nav.up" => InBrowser && ParentFolder is not null && !AddressBox.IsKeyboardFocusWithin,
            "zoom.in" => hasImage && _zoom < MaxZoom - 1e-6,
            "zoom.out" => hasImage && _zoom > MinZoom + 1e-6,
            "zoom.actual" or "zoom.fit" or "image.rotateRight" or "image.rotateLeft" => hasImage,
            "nav.next" => InViewer && (_viewIndex < _viewList.Count - 1 || (S.LoopAtEnds && _viewList.Count > 1)),
            "nav.previous" => InViewer && (_viewIndex > 0 || (S.LoopAtEnds && _viewList.Count > 1)),
            "nav.first" => InViewer && _viewIndex > 0,
            "nav.last" => InViewer && _viewIndex < _viewList.Count - 1,
            "view.fullScreen" => InViewer,
            "slideshow.start" => InViewer ? _viewList.Count > 0 : _items.Count > 0,
            _ => false,
        };
    }

    // ───────────── UI state ─────────────

    /// <summary>Re-sync checks, chrome, title and status after any change.</summary>
    private void UpdateUi()
    {
        foreach (var (key, items) in _menuItems)
        {
            bool? check = key switch
            {
                _ when FormatKeys.TryGetValue(key, out var f) => S.ViewFormat == f,
                _ when SortKeys.TryGetValue(key, out var k) => S.SortKey == k,
                "view.sort.ascending" => !S.SortDescending,
                "view.sort.descending" => S.SortDescending,
                "view.pane.preview" => _ui.ShowPreviewPane,
                "view.pane.camera" => _ui.ShowCameraPane,
                "view.folderTree" => _ui.ShowFolderTree,
                "view.fullScreen" => _fullScreen,
                "view.alwaysOnTop" => S.AlwaysOnTop,
                _ => null,
            };
            if (check is null) continue;
            foreach (var item in items) item.IsChecked = check.Value;
        }
        bool viewer = InViewer;
        BrowserToolbarPanel.Visibility = viewer ? Visibility.Collapsed : Visibility.Visible;
        ViewerToolbarPanel.Visibility = viewer ? Visibility.Visible : Visibility.Collapsed;
        AddressBar.Visibility = viewer ? Visibility.Collapsed : Visibility.Visible;
        BrowserRoot.Visibility = viewer ? Visibility.Collapsed : Visibility.Visible;
        ViewerRoot.Visibility = viewer ? Visibility.Visible : Visibility.Collapsed;
        bool chrome = !_fullScreen;
        MainMenu.Visibility = chrome ? Visibility.Visible : Visibility.Collapsed;
        ToolbarHost.Visibility = chrome ? Visibility.Visible : Visibility.Collapsed;
        MainStatusBar.Visibility = chrome ? Visibility.Visible : Visibility.Collapsed;
        if (viewer) AddressBar.Visibility = Visibility.Collapsed;
        UpdateTitle();
        UpdateStatus();
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Design P05: "file name — ohMypic" in the viewer, "folder name — ohMypic" in the browser.</summary>
    private void UpdateTitle()
    {
        string? subject = InViewer
            ? (CurrentPath is { } p ? Path.GetFileName(p) : null)
            : (_folder is { } f ? FolderDisplayName(f) : null);
        Title = subject is null ? DialogText.App_Name : string.Format(MainText.Title_Format, subject);
    }

    private static string FolderDisplayName(string folder)
    {
        var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? folder : name;
    }

    private void UpdateStatus()
    {
        if (InViewer)
        {
            StatusText.Text = _notice ?? ViewerStatus();
            StatusRightText.Text = "";
            return;
        }
        StatusText.Text = _notice ?? string.Format(MainText.Status_Count, _items.Count);
        var selected = SelectedItems();
        StatusRightText.Text = selected.Count == 0
            ? ""
            : string.Format(MainText.Status_Selection, selected.Count, ImageItem.FormatSize(selected.Sum(i => i.Size)));
    }

    private void ShowNotice(string text)
    {
        _notice = text;
        UpdateStatus();
    }

    private void SetMode(Mode mode)
    {
        _mode = mode;
        UpdateUi();
        if (mode == Mode.Viewer) ViewerRoot.Focus();
        else FocusList();
    }

    /// <summary>Applies display settings that do not need a reload (after start and after the Settings dialog).</summary>
    private void ApplySettings()
    {
        Topmost = S.AlwaysOnTop;
        ApplyViewerSettings();
        ApplyViewFormat();
        ApplyPanes();
    }

    private void SaveSettings(bool showError)
    {
        try
        {
            S.Save(AppSettings.DefaultPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (showError) MessageDialog.Error(this, MainText.Settings_SaveFailedTitle, DescribeIoError(ex));
            else ShowNotice("\u26A0 " + MainText.Settings_SaveFailedTitle);
        }
    }

    private async Task OpenPreferencesAsync(int page)
    {
        bool hidden = S.ShowHiddenFiles;
        int thumbSize = S.ThumbnailSize;
        bool autoRotate = S.AutoRotateByExif;
        var dlg = new PreferencesWindow(S, page) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        SaveSettings(showError: true);
        ApplySettings();
        if (UiLanguage.NeedsRestart(S.Language)) _notice = MainText.Language_NextStart;
        if (hidden != S.ShowHiddenFiles) ResetFolderTree();
        if (hidden != S.ShowHiddenFiles || thumbSize != S.ThumbnailSize || autoRotate != S.AutoRotateByExif)
            await LoadFolderAsync(SelectedPaths(), forceReload: true);
        if (InViewer)
        {
            if (autoRotate != S.AutoRotateByExif && CurrentPath is not null)
            {
                ClearPreload();
                await ShowImageAsync(_viewIndex);
            }
            else
            {
                ApplyFitMode();
            }
        }
    }

    // ───────────── window placement, drag & drop, closing ─────────────

    private void RestorePlacement()
    {
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (_ui.Width >= MinWidth && _ui.Height >= MinHeight)
        {
            Width = _ui.Width;
            Height = _ui.Height;
        }
        if (!double.IsNaN(_ui.Left) && !double.IsNaN(_ui.Top))
        {
            var rect = new Rect(_ui.Left, _ui.Top, Width, Height);
            // Only restore a position that is still (mostly) on a screen.
            if (screen.IntersectsWith(rect) && rect.Left >= screen.Left - 100 && rect.Top >= screen.Top - 20 && rect.Left < screen.Right - 100 && rect.Top < screen.Bottom - 100)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = _ui.Left;
                Top = _ui.Top;
            }
        }
        if (_ui.Maximized) WindowState = WindowState.Maximized;
    }

    private void SavePlacement()
    {
        var bounds = _fullScreen ? _restoreBounds : (WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds);
        if (!bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0)
        {
            _ui.Left = bounds.Left;
            _ui.Top = bounds.Top;
            _ui.Width = bounds.Width;
            _ui.Height = bounds.Height;
        }
        _ui.Maximized = _fullScreen ? _restoreState == WindowState.Maximized : WindowState == WindowState.Maximized;
        _ui.FolderTreeWidth = TreeColumn.ActualWidth > 40 ? TreeColumn.ActualWidth : _ui.FolderTreeWidth;
        _ui.Save(UiState.DefaultPath);
    }

    private void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>A dropped folder opens in the browser, a dropped file in the viewer (first item only).</summary>
    private async void OnPreviewDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths) return;
        e.Handled = true;
        Activate();
        var path = paths[0];
        try
        {
            if (Directory.Exists(path))
            {
                if (InViewer && !await LeaveViewerAsync()) return;
                await NavigateToAsync(path);
            }
            else if (File.Exists(path))
            {
                if (InViewer && !await ResolvePendingRotationAsync()) return;
                await OpenFileInViewerAsync(path);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            MessageDialog.Error(this, string.Format(MainText.Error_CannotOpenTitle, Path.GetFileName(path)), DescribeIoError(ex));
        }
        UpdateUi();
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closeConfirmed) return;
        if (InViewer && _rotation != 0)
        {
            // Ask about the rotated image first; close again once it is resolved.
            e.Cancel = true;
            if (await ResolvePendingRotationAsync())
            {
                _closeConfirmed = true;
                Close();
            }
            return;
        }
        _closeConfirmed = true;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        SavePlacement();
        S.LastFolder = _folder ?? S.LastFolder;
        try
        {
            S.Save(AppSettings.DefaultPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Closing: nowhere left to report; the next start reads the previous file.
        }
        _loader?.Dispose();
        _watcher?.Dispose();
        _refreshTimer.Stop();
    }

    // ───────────── error text ─────────────

    /// <summary>Plain-language reason for a failed open/decode. The raw message is never shown alone.</summary>
    internal static string DescribeLoadError(Exception ex) => ex switch
    {
        ImageOperationException op => DescribeErrorCode(op.Code),
        FileNotFoundException or DirectoryNotFoundException => MainText.Error_NotFound,
        UnauthorizedAccessException => MainText.Error_AccessDenied,
        IOException => MainText.Error_Io,
        _ => MainText.Error_Unknown,
    };

    internal static string DescribeErrorCode(EditErrorCode code) => code switch
    {
        EditErrorCode.UnsupportedFormat => MainText.Error_Unsupported,
        EditErrorCode.DecodeFailed => MainText.Error_DecodeFailed,
        EditErrorCode.EncodeFailed => MainText.Error_EncodeFailed,
        EditErrorCode.TargetSizeUnreachable => MainText.Error_TargetSize,
        EditErrorCode.TooLarge => MainText.Error_TooLarge,
        EditErrorCode.IoError => MainText.Error_Io,
        EditErrorCode.Cancelled => MainText.Error_Cancelled,
        _ => MainText.Error_Unknown,
    };

    /// <summary>Reason text for file-system errors, with the system message as detail.</summary>
    internal static string DescribeIoError(Exception ex)
    {
        string reason = ex switch
        {
            FileNotFoundException or DirectoryNotFoundException => MainText.Error_NotFound,
            UnauthorizedAccessException => MainText.Error_AccessDenied,
            ImageOperationException op => DescribeErrorCode(op.Code),
            IOException => MainText.Error_Io,
            _ => MainText.Error_Unknown,
        };
        return string.Format(MainText.Error_ReasonWithDetail, reason, ex.Message);
    }
}
