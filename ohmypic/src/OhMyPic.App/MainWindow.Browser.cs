using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OhMyPic.App.Commands;
using OhMyPic.App.Models;
using OhMyPic.App.Text;
using OhMyPic.Core;

namespace OhMyPic.App;

/// <summary>Browser mode: folder tree, address bar, thumbnail/list/details/filmstrip views, inspector panes.</summary>
public partial class MainWindow
{
    private readonly DispatcherTimer _refreshTimer;
    private readonly ObservableCollection<FolderNode> _treeRoots = [];
    private List<ImageItem> _items = [];
    private string? _folder;
    private ThumbnailLoader? _loader;
    private int _loaderSide;
    private FileSystemWatcher? _watcher;
    private int _listVersion;
    private int _previewVersion;
    private bool _syncingSelection;
    private bool _treeNavigationSuppressed;

    private void InitBrowser()
    {
        FolderTree.ItemsSource = _treeRoots;
        FolderTree.SelectedItemChanged += OnTreeSelectionChanged;
        UpButton.Command = CommandCatalog.Command("nav.up");
        UpButton.CommandTarget = this;
        AddressBox.PreviewKeyDown += OnAddressKeyDown;
        AddressBox.GotKeyboardFocus += (_, _) => AddressBox.SelectAll();

        TileList.ContextMenu = CreateContextMenu(CommandCatalog.TileContextMenu, "context.");
        DetailsList.ContextMenu = CreateContextMenu(CommandCatalog.TileContextMenu, "context.details.");
        foreach (var list in new ListBox[] { TileList, DetailsList })
        {
            list.SelectionChanged += OnListSelectionChanged;
            list.MouseDoubleClick += OnListDoubleClick;
        }
        TreeSplitter.DragCompleted += (_, _) =>
        {
            if (TreeColumn.ActualWidth > 40) _ui.FolderTreeWidth = TreeColumn.ActualWidth;
        };
        FilmstripImage.SizeChanged += (_, _) => { if (S.ViewFormat == ViewFormat.Filmstrip) _ = UpdateSelectionPreviewAsync(); };
    }

    // ───────────── selection helpers ─────────────

    /// <summary>The list control of the current view format (tiles or details).</summary>
    private ListBox ActiveList => S.ViewFormat == ViewFormat.Details ? DetailsList : TileList;

    private List<ImageItem> SelectedItems() => ActiveList.SelectedItems.OfType<ImageItem>().ToList();

    private int SelectedCount => ActiveList.SelectedItems.Count;

    private List<string> SelectedPaths() => SelectedItems().Select(i => i.Path).ToList();

    private ImageItem? FocusedItem => ActiveList.SelectedItem as ImageItem;

    /// <summary>The folder file operations work in: the browsed folder, or the folder of the image in the viewer.</summary>
    private string? CurrentFolder => InViewer && CurrentPath is not null ? Path.GetDirectoryName(CurrentPath) : _folder;

    private string? ParentFolder => _folder is null ? null : Directory.GetParent(_folder.TrimEnd(Path.DirectorySeparatorChar))?.FullName;

    /// <summary>Files a command applies to: the image in the viewer, or the selected tiles.</summary>
    private List<string> TargetPaths() => InViewer ? (CurrentPath is { } p ? [p] : []) : SelectedPaths();

    private void FocusList()
    {
        var list = ActiveList;
        if (list.SelectedItem is { } item && list.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container)
            container.Focus();
        else
            list.Focus();
    }

    private void SelectPaths(IReadOnlyCollection<string>? paths)
    {
        var list = ActiveList;
        _syncingSelection = true;
        try
        {
            list.SelectedItems.Clear();
            if (paths is { Count: > 0 })
            {
                var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
                ImageItem? first = null;
                foreach (var item in _items)
                {
                    if (!set.Contains(item.Path)) continue;
                    list.SelectedItems.Add(item);
                    first ??= item;
                }
                if (first is not null) list.ScrollIntoView(first);
            }
        }
        finally
        {
            _syncingSelection = false;
        }
        OnSelectionSettled();
    }

    private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || !ReferenceEquals(sender, ActiveList)) return;
        // A new selection clears the previous notice (it described an older action).
        _notice = null;
        OnSelectionSettled();
    }

    private void OnSelectionSettled()
    {
        UpdateStatus();
        UpdateInspector();
        if (S.ViewFormat == ViewFormat.Filmstrip) _ = UpdateSelectionPreviewAsync();
        CommandManager.InvalidateRequerySuggested();
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list || e.ChangedButton != MouseButton.Left) return;
        if (e.OriginalSource is not DependencyObject source || ItemsControl.ContainerFromElement(list, source) is not ListBoxItem) return;
        e.Handled = true;
        if (CanRun("view.viewer")) Run("view.viewer");
    }

    // ───────────── folder tree ─────────────

    private async Task LoadFolderTreeAsync()
    {
        _treeRoots.Clear();
        AddRoot(Environment.SpecialFolder.MyPictures, "\uE91B");
        AddRoot(Environment.SpecialFolder.Desktop, "\uE8FC");
        // Drive readiness can block (network, optical drives): ask off the UI thread.
        var drives = await Task.Run(() =>
        {
            var result = new List<(string Name, string Path)>();
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady) continue;
                    var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.Name.TrimEnd('\\') : $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})";
                    result.Add((label, drive.RootDirectory.FullName));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
            return result;
        });
        foreach (var (name, path) in drives) _treeRoots.Add(new FolderNode(name, path, () => S.ShowHiddenFiles, "\uEDA2"));
    }

    private void AddRoot(Environment.SpecialFolder folder, string glyph)
    {
        var path = Environment.GetFolderPath(folder);
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
        _treeRoots.Add(new FolderNode(FolderDisplayName(path), path, () => S.ShowHiddenFiles, glyph));
    }

    private void ResetFolderTree()
    {
        foreach (var root in _treeRoots) root.Reset();
    }

    private async void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_treeNavigationSuppressed || e.NewValue is not FolderNode { IsPlaceholder: false } node) return;
        if (_folder is not null && string.Equals(_folder, node.Path, StringComparison.OrdinalIgnoreCase)) return;
        await NavigateToAsync(node.Path, fromTree: true);
        UpdateUi();
    }

    /// <summary>Drops the tree selection so the same node can be picked again after navigating elsewhere.</summary>
    private void ClearTreeSelection()
    {
        if (FolderTree.SelectedItem is not FolderNode node) return;
        _treeNavigationSuppressed = true;
        node.IsSelected = false;
        _treeNavigationSuppressed = false;
    }

    // ───────────── address bar and navigation ─────────────

    private async void OnAddressKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            AddressBox.Text = _folder ?? "";
            FocusList();
            return;
        }
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        var text = Environment.ExpandEnvironmentVariables(AddressBox.Text.Trim().Trim('"'));
        if (text.Length == 0) return;
        _notice = null;
        string full;
        try
        {
            full = Path.GetFullPath(text);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            ShowNotice("\u26A0 " + string.Format(MainText.Error_PathNotFound, text));
            return;
        }
        if (Directory.Exists(full))
        {
            await NavigateToAsync(full);
            FocusList();
        }
        else if (File.Exists(full))
        {
            await OpenFileInViewerAsync(full);
        }
        else
        {
            ShowNotice("\u26A0 " + string.Format(MainText.Error_PathNotFound, text));
        }
        UpdateUi();
    }

    private async Task GoUpAsync()
    {
        var child = _folder;
        var parent = ParentFolder;
        if (parent is null) return;
        await NavigateToAsync(parent);
        // Keep the place: the folder just left is not a tile (only images are), so focus the list.
        if (child is not null) FocusList();
    }

    /// <summary>Opens a folder in the browser. Missing or unreadable folders leave the current view and say why in the status bar.</summary>
    private async Task NavigateToAsync(string folder, bool fromTree = false)
    {
        string full;
        try
        {
            full = Path.GetFullPath(folder);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            ShowNotice("\u26A0 " + string.Format(MainText.Error_PathNotFound, folder));
            return;
        }
        if (!Directory.Exists(full))
        {
            ShowNotice("\u26A0 " + string.Format(MainText.Error_PathNotFound, folder));
            return;
        }
        bool changed = !string.Equals(_folder, full, StringComparison.OrdinalIgnoreCase);
        _folder = full;
        S.LastFolder = full;
        AddressBox.Text = full;
        if (!fromTree) ClearTreeSelection();
        if (changed) WatchFolder(full);
        await LoadFolderAsync(null, forceReload: changed);
        UpdateTitle();
    }

    // ───────────── listing ─────────────

    /// <summary>
    /// (Re)lists the current folder. Items whose file is unchanged keep their loaded thumbnail unless
    /// <paramref name="forceReload"/> (folder, thumbnail size or hidden-file setting changed).
    /// </summary>
    private async Task LoadFolderAsync(IReadOnlyCollection<string>? select, bool forceReload = false)
    {
        var folder = _folder;
        if (folder is null) return;
        int version = ++_listVersion;
        var key = S.SortKey;
        bool descending = S.SortDescending;
        bool hidden = S.ShowHiddenFiles;
        IReadOnlyList<ImageEntry> entries;
        try
        {
            entries = await Task.Run(() => FolderScanner.List(folder, key, descending, hidden));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            if (version != _listVersion) return;
            entries = [];
            _notice = "\u26A0 " + string.Format(MainText.Error_FolderUnreadable, FolderDisplayName(folder), DescribeIoError(ex));
        }
        if (version != _listVersion || !string.Equals(folder, _folder, StringComparison.OrdinalIgnoreCase)) return;

        int side = DecodeSide();
        if (forceReload || _loader is null || side != _loaderSide)
        {
            _loader?.Dispose();
            _loader = new ThumbnailLoader(Dispatcher, side, S.AutoRotateByExif);
            _loaderSide = side;
            forceReload = true;
        }
        var old = forceReload
            ? new Dictionary<string, ImageItem>(StringComparer.OrdinalIgnoreCase)
            : _items.GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var items = new List<ImageItem>(entries.Count);
        foreach (var entry in entries)
        {
            if (old.TryGetValue(entry.Path, out var existing) && existing.SameFile(entry)) items.Add(existing);
            else items.Add(new ImageItem(entry, _loader));
        }
        _items = items;
        _syncingSelection = true;
        TileList.ItemsSource = items;
        DetailsList.ItemsSource = items;
        _syncingSelection = false;
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectPaths(select);
        if ((select is null || select.Count == 0) && items.Count > 0) ActiveList.ScrollIntoView(items[0]);
        UpdateUi();
    }

    /// <summary>Pixel size thumbnails are decoded at for the current view format (DPI aware).</summary>
    private int DecodeSide()
    {
        double dip = S.ViewFormat switch
        {
            ViewFormat.List or ViewFormat.Details => 32,
            ViewFormat.Icons => 64,
            ViewFormat.LargeIcons => 128,
            ViewFormat.Filmstrip => 96,
            _ => Math.Clamp(S.ThumbnailSize, AppSettings.MinThumbnailSize, AppSettings.MaxThumbnailSize),
        };
        var dpi = VisualTreeHelper.GetDpi(this);
        return (int)Math.Ceiling(dip * Math.Max(dpi.DpiScaleX, dpi.DpiScaleY));
    }

    private void WatchFolder(string folder)
    {
        _watcher?.Dispose();
        _watcher = null;
        try
        {
            var watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            FileSystemEventHandler changed = (_, _) => Dispatcher.InvokeAsync(RestartRefreshTimer);
            watcher.Created += changed;
            watcher.Deleted += changed;
            watcher.Changed += changed;
            watcher.Renamed += (_, _) => Dispatcher.InvokeAsync(RestartRefreshTimer);
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // No live refresh for this folder (some network shares); View > Refresh still works.
        }
    }

    private void RestartRefreshTimer()
    {
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    private async void OnRefreshTimer(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        if (_busy) return;
        await LoadFolderAsync(SelectedPaths());
    }

    // ───────────── view format, sort, panes ─────────────

    private async void SetViewFormat(ViewFormat format)
    {
        if (S.ViewFormat == format) return;
        var selected = SelectedPaths();
        S.ViewFormat = format;
        ApplyViewFormat();
        SaveSettings(showError: false);
        await LoadFolderAsync(selected);
        FocusList();
    }

    private async Task SetSortAsync(string key)
    {
        if (SortKeys.TryGetValue(key, out var sortKey)) S.SortKey = sortKey;
        else if (key == "view.sort.ascending") S.SortDescending = false;
        else if (key == "view.sort.descending") S.SortDescending = true;
        SaveSettings(showError: false);
        await LoadFolderAsync(SelectedPaths());
    }

    /// <summary>Tile geometry, panel and template for the current view format.</summary>
    private void ApplyViewFormat()
    {
        var format = S.ViewFormat;
        bool details = format == ViewFormat.Details;
        bool strip = format == ViewFormat.Filmstrip;
        bool resolution = S.ShowResolutionOnThumbnail && format is ViewFormat.Thumbnails or ViewFormat.LargeIcons or ViewFormat.Filmstrip;
        double picture = format switch
        {
            ViewFormat.Icons => 64,
            ViewFormat.LargeIcons => 128,
            ViewFormat.Filmstrip => 96,
            _ => Math.Clamp(S.ThumbnailSize, AppSettings.MinThumbnailSize, AppSettings.MaxThumbnailSize),
        };
        if (format == ViewFormat.List)
        {
            TileInnerWidth = 220;
            TileInnerHeight = 22;
            TileList.ItemTemplate = (DataTemplate)FindResource("ListTemplate");
        }
        else
        {
            TileInnerWidth = picture + 8;
            TileInnerHeight = picture + 8 + 20 + (resolution ? 16 : 0);
            TileList.ItemTemplate = (DataTemplate)FindResource(resolution ? "TileResolutionTemplate" : "TileTemplate");
        }
        // The item container adds Padding 6,3 around the content.
        TileWidth = TileInnerWidth + 12;
        TileHeight = TileInnerHeight + 6;

        TileList.ItemsPanel = (ItemsPanelTemplate)FindResource(strip ? "StripPanelTemplate" : "WrapPanelTemplate");
        ScrollViewer.SetHorizontalScrollBarVisibility(TileList, strip ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(TileList, strip ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
        FilmstripPreviewRow.Height = strip ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        ListRow.Height = strip ? new GridLength(TileHeight + 28) : new GridLength(1, GridUnitType.Star);
        if (!strip) FilmstripImage.Source = null;

        // Carry the selection over when switching between the tile list and the details list.
        var from = details ? TileList : DetailsList;
        var to = details ? DetailsList : TileList;
        if (from.SelectedItems.Count > 0)
        {
            _syncingSelection = true;
            to.SelectedItems.Clear();
            foreach (var item in from.SelectedItems.OfType<ImageItem>().ToList()) to.SelectedItems.Add(item);
            from.SelectedItems.Clear();
            _syncingSelection = false;
        }
        TileList.Visibility = details ? Visibility.Collapsed : Visibility.Visible;
        DetailsList.Visibility = details ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyPanes()
    {
        if (_ui.ShowFolderTree)
        {
            TreeColumn.Width = new GridLength(Math.Clamp(_ui.FolderTreeWidth, 120, 600));
            TreeColumn.MinWidth = 120;
            FolderTree.Visibility = Visibility.Visible;
            TreeSplitter.Visibility = Visibility.Visible;
        }
        else
        {
            if (TreeColumn.ActualWidth > 40) _ui.FolderTreeWidth = TreeColumn.ActualWidth;
            TreeColumn.MinWidth = 0;
            TreeColumn.Width = new GridLength(0);
            FolderTree.Visibility = Visibility.Collapsed;
            TreeSplitter.Visibility = Visibility.Collapsed;
        }
        bool inspector = _ui.ShowPreviewPane || _ui.ShowCameraPane;
        InspectorColumn.Width = inspector ? new GridLength(280) : new GridLength(0);
        InspectorPane.Visibility = inspector ? Visibility.Visible : Visibility.Collapsed;
        InspectorSplitter.Visibility = inspector ? Visibility.Visible : Visibility.Collapsed;
        PreviewSection.Visibility = _ui.ShowPreviewPane ? Visibility.Visible : Visibility.Collapsed;
        CameraSection.Visibility = _ui.ShowCameraPane ? Visibility.Visible : Visibility.Collapsed;
        CameraSection.Margin = _ui.ShowPreviewPane ? new Thickness(0, 16, 0, 0) : new Thickness(0);
    }

    // ───────────── inspector and filmstrip preview ─────────────

    private void UpdateInspector()
    {
        if (!_ui.ShowPreviewPane && !_ui.ShowCameraPane) return;
        _ = UpdateSelectionPreviewAsync();
    }

    /// <summary>Loads the single selected image for the preview pane, the filmstrip preview and the camera pane.</summary>
    private async Task UpdateSelectionPreviewAsync()
    {
        int version = ++_previewVersion;
        var items = SelectedItems();
        var item = items.Count == 1 ? items[0] : null;
        bool strip = S.ViewFormat == ViewFormat.Filmstrip;
        bool preview = _ui.ShowPreviewPane;
        bool camera = _ui.ShowCameraPane;
        PreviewName.Text = item?.Name ?? (items.Count > 1 ? string.Format(MainText.Pane_ManySelected, items.Count) : "");
        if (item is null)
        {
            PreviewImage.Source = null;
            FilmstripImage.Source = null;
            ShowCameraRows(null);
            return;
        }
        var dpi = VisualTreeHelper.GetDpi(this);
        double dip = strip ? Math.Max(FilmstripImage.ActualWidth, FilmstripImage.ActualHeight) : 0;
        dip = Math.Max(dip, preview ? 280 : 0);
        int side = (int)Math.Ceiling(Math.Max(dip, 64) * Math.Max(dpi.DpiScaleX, dpi.DpiScaleY));
        bool autoRotate = S.AutoRotateByExif;
        var path = item.Path;
        BitmapSource? bitmap = null;
        ImageInfo? info = null;
        try
        {
            (bitmap, info) = await Task.Run(() =>
            {
                BitmapSource? b = null;
                ImageInfo? i = null;
                try
                {
                    if (preview || strip) b = ImageLoader.LoadForDisplay(path, autoRotate, side);
                }
                catch (Exception ex) when (ex is ImageOperationException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
                {
                }
                try
                {
                    if (camera) i = ImageLoader.ReadInfo(path);
                }
                catch (Exception ex) when (ex is ImageOperationException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
                {
                }
                return (b, i);
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
        if (version != _previewVersion) return;
        PreviewImage.Source = preview ? bitmap : null;
        FilmstripImage.Source = strip ? bitmap : null;
        ShowCameraRows(camera ? info : null);
    }

    private void ShowCameraRows(ImageInfo? info)
    {
        var rows = new List<InfoRow>();
        if (info is not null)
        {
            void Add(string label, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value)) rows.Add(new InfoRow(label, value));
            }
            Add(MainText.Camera_Make, info.Get(MetadataKeys.CameraMake));
            Add(MainText.Camera_Model, info.Get(MetadataKeys.CameraModel));
            Add(MainText.Camera_DateTaken, info.Get(MetadataKeys.DateTaken));
            Add(MainText.Camera_Exposure, info.Get(MetadataKeys.ExposureTime));
            Add(MainText.Camera_FNumber, info.Get(MetadataKeys.FNumber));
            Add(MainText.Camera_Iso, info.Get(MetadataKeys.IsoSpeed));
            Add(MainText.Camera_FocalLength, info.Get(MetadataKeys.FocalLength));
        }
        bool hasCamera = rows.Count > 0;
        if (info is not null)
        {
            rows.Add(new InfoRow(MainText.Camera_Dimensions, string.Format(MainText.Dimensions_Format, info.Width, info.Height)));
            rows.Add(new InfoRow(MainText.Camera_Format, info.FormatName));
        }
        CameraList.ItemsSource = rows;
        CameraEmpty.Visibility = hasCamera ? Visibility.Collapsed : Visibility.Visible;
    }
}
