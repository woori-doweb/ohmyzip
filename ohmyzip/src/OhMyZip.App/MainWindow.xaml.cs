using System.Collections;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using OhMyZip.App.Commands;
using OhMyZip.App.Dialogs;
using OhMyZip.App.Models;
using OhMyZip.App.Settings;
using OhMyZip.App.Text;
using OhMyZip.Core;

namespace OhMyZip.App;

/// <summary>
/// 주 창. 이 파일: 명령 표 → 메뉴·툴바·단축키, 활성 규칙(<see cref="CanRun"/> 한 곳), 화면 상태, 탐색, 작업 실행.
/// 보기 설정은 MainWindow.View.cs, 압축 파일·항목 명령은 MainWindow.Archive.cs.
/// </summary>
public partial class MainWindow : Window
{
    private const string AppName = "ohmyZip";

    private readonly string? _startupPath;
    /// <summary>상태줄 알림 한 줄(정보만 주는 결과·시작 시 문제). 다음 명령을 실행하면 지운다.</summary>
    private string? _notice;
    private readonly Dictionary<string, List<MenuItem>> _menuItems = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MenuItem> _menuById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Button> _toolbarButtons = new(StringComparer.Ordinal);
    private readonly List<(Border Separator, IReadOnlyList<ToolbarSpec> Group)> _toolbarSeparators = [];
    private Button? _extractMoreButton;

    private ArchiveModel? _archive;
    private FolderNode? _root;
    private FolderNode? _selectedNode;
    private string _folder = "";
    private ListCollectionView? _view;
    private EntryComparer _comparer = new(nameof(EntryRow.Name), ListSortDirection.Ascending);
    private string _search = "";
    private bool _busy;
    private bool _showStart;
    private bool _startForced;
    private bool _syncingTree;
    private GridLength _sidebarWidth = new(230);

    private static AppSettings S => AppSettings.Current;

    public MainWindow(string? startupPath, string? settingsError)
    {
        _startupPath = startupPath;
        // 설정을 못 읽으면 기본값(상태 표시줄 켜짐)으로 시작하므로 알림 창 대신 상태줄에 알린다.
        _notice = settingsError is null ? null : "\u26A0 " + settingsError;
        InitializeComponent();
        BindCommands();
        BuildMenu();
        BuildToolbar();
        EmptyOpenButton.Command = CommandCatalog.Command("archive.open");
        EmptyCreateButton.Command = CommandCatalog.Command("archive.create");

        PreviewDragEnter += OnPreviewDragOver;
        PreviewDragOver += OnPreviewDragOver;
        PreviewDrop += OnPreviewDrop;
        SourceInitialized += (_, _) => NativeMethods.ApplyDarkTitleBar(this);
        Loaded += OnLoaded;
        Closed += (_, _) => CleanupClipboardTemp();
        ApplySettings(S);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_notice is not null) AnnounceStatus();
        StartAutoUpdate();
        if (_startupPath is not null) await OpenArchiveAsync(_startupPath);
        if (_archive is null) EmptyOpenButton.Focus();
    }

    // ───────────────────────── 명령 표 → 바인딩·메뉴·툴바 ─────────────────────────

    private void BindCommands()
    {
        foreach (var key in CommandCatalog.Implemented)
        {
            var cmd = CommandCatalog.Command(key);
            CommandBindings.Add(new CommandBinding(cmd, OnCommandExecuted, OnCommandCanExecute));
            var shortcut = CommandCatalog.Gesture(key);
            if (shortcut is null) continue;
            var gesture = CommandCatalog.ParseGesture(shortcut);
            InputBindings.Add(new KeyBinding(cmd, gesture));
            // DataGrid 의 기본 Del/F2/Ctrl+C 바인딩(읽기 전용이라 실행 불가)이 키를 삼키지 않도록 목록에도 같은 바인딩을 둔다.
            EntryGrid.InputBindings.Add(new KeyBinding(cmd, gesture));
            EntryList.InputBindings.Add(new KeyBinding(cmd, gesture));
        }
    }

    private void BuildMenu()
    {
        foreach (var (name, header) in CommandCatalog.Menus)
        {
            var top = new MenuItem { Header = header };
            var submenus = new Dictionary<string, MenuItem>(StringComparer.Ordinal);
            foreach (var spec in CommandCatalog.Items.Where(i => i.Menu == name))
            {
                AddMenuItem(top, submenus, spec);
                foreach (var (_, feature) in CommandCatalog.FeatureItems.Where(f => f.AfterKey == spec.Key))
                    AddMenuItem(top, submenus, feature);
            }
            MainMenu.Items.Add(top);
        }
    }

    private void AddMenuItem(MenuItem top, Dictionary<string, MenuItem> submenus, MenuSpec spec)
    {
        var parent = top;
        bool separator = CommandCatalog.SeparatorBefore.Contains(spec.Id);
        if (spec.Submenu is not null && !submenus.TryGetValue(spec.Submenu, out parent))
        {
            parent = new MenuItem { Header = spec.Submenu };
            submenus[spec.Submenu] = parent;
            // 하위 메뉴 첫 행의 구분선은 하위 메뉴 머리 앞(바깥 메뉴)에 둔다.
            if (separator) top.Items.Add(new Separator());
            separator = false;
            top.Items.Add(parent);
        }
        var item = new MenuItem { InputGestureText = spec.Shortcut ?? "", Tag = spec };
        AutomationProperties.SetAutomationId(item, spec.Id);
        if (CommandCatalog.IsImplemented(spec.Key))
        {
            item.Header = spec.Header;
            item.Command = CommandCatalog.Command(spec.Key);
            // 같은 키를 쓰는 여러 항목(툴바 크게/작게…, 칼럼 10개)은 행 Id 로 구분한다.
            item.CommandParameter = spec.Id;
            // 켜기/끄기·하나 고르기 항목은 체크 상태를 UIA TogglePattern 으로도 내보인다(색·글리프만으로 전달하지 않음).
            // 실제 상태는 명령 실행 뒤 UpdateUi 가 설정값으로 다시 맞춘다.
            item.IsCheckable = spec.Key is "view.start" or "view.sidebar" or "view.statusbar" or "view.comment" or "view.topmost"
                or "view.listmode" or "view.columns" || (spec.Key == "view.toolbar" && spec.Id != "OZ-C035");
            if (!_menuItems.TryGetValue(spec.Key, out var list)) _menuItems[spec.Key] = list = [];
            list.Add(item);
            _menuById[spec.Id] = item;
        }
        else
        {
            // 미구현: 보이되 누를 수 없음. 클릭 가능한 빈 동작을 만들지 않는다.
            item.Header = spec.Header + CommandCatalog.PendingSuffix;
            item.IsEnabled = false;
            item.ToolTip = MainText.Menu_NotImplementedToolTip;
        }
        if (separator) parent.Items.Add(new Separator());
        parent.Items.Add(item);
    }

    private void BuildToolbar()
    {
        var groups = CommandCatalog.ToolbarGroups;
        for (int g = 0; g < groups.Count; g++)
        {
            if (g > 0)
            {
                var separator = new Border { Width = 20, Focusable = false };
                _toolbarSeparators.Add((separator, groups[g]));
                ToolbarPanel.Children.Add(separator);
            }
            foreach (var t in groups[g])
            {
                var button = new Button
                {
                    Content = t.Label,
                    Tag = t.Glyph,
                    Command = CommandCatalog.Command(t.Key),
                    CommandParameter = CommandCatalog.Spec(t.Key).Id,
                };
                AutomationProperties.SetName(button, t.Label);
                AutomationProperties.SetAutomationId(button, t.Key);
                _toolbarButtons[t.Key] = button;
                ToolbarPanel.Children.Add(button);
                if (t.Key == "archive.extract") ToolbarPanel.Children.Add(_extractMoreButton = BuildExtractMoreButton());
            }
        }
    }

    /// <summary>풀기 드롭다운(OZ-F027 ui_location): 압축 풀기… / 알아서 풀기. 두 명령 모두 파일 메뉴에도 있다(OZ-A06).</summary>
    private Button BuildExtractMoreButton()
    {
        var button = new Button { Tag = "\uE70D", Content = MainText.ExtractMore_Button, MinWidth = 32, Padding = new Thickness(2, 4, 2, 4), Margin = new Thickness(0, 0, 2, 0) };
        AutomationProperties.SetName(button, MainText.ExtractMore_Name);
        AutomationProperties.SetAutomationId(button, "archive.extract.more");
        button.ToolTip = MainText.ExtractMore_ToolTip;
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        foreach (var key in new[] { "archive.extract", "archive.extract.smart" })
        {
            var spec = CommandCatalog.Spec(key);
            var item = new MenuItem
            {
                Header = spec.Header,
                InputGestureText = spec.Shortcut ?? "",
                Command = CommandCatalog.Command(key),
                CommandParameter = spec.Id,
                CommandTarget = this,
            };
            AutomationProperties.SetAutomationId(item, "toolbar-" + key);
            menu.Items.Add(item);
        }
        button.ContextMenu = menu;
        button.Click += (_, _) => menu.IsOpen = true;
        return button;
    }

    private void OnCommandCanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = e.Command is RoutedUICommand c && CanRun(c.Name);
        e.Handled = true;
    }

    private async void OnCommandExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command is not RoutedUICommand c) return;
        e.Handled = true;
        // 새 명령을 시작하면 지난 알림을 지운다.
        if (_notice is not null)
        {
            _notice = null;
            UpdateStatusText();
        }
        if (!CanRun(c.Name)) return;
        try
        {
            await ExecuteAsync(c.Name, e.Parameter as string);
        }
        catch (Exception ex)
        {
            MessageDialog.Error(this, MainText.OperationFailed_Title, ex.Message);
        }
    }

    private bool ShowingStart => _archive is null || _showStart;
    private bool Browsing => _archive is not null && !_showStart;
    private bool Finding => FindPanel.Visibility == Visibility.Visible;
    private bool DetailsMode => S.ListMode == ListMode.Details;
    private Selector ActiveList => DetailsMode ? EntryGrid : EntryList;
    private IList ActiveSelection => DetailsMode ? EntryGrid.SelectedItems : EntryList.SelectedItems;
    private int SelectionCount => Browsing && !Finding ? ActiveSelection.Count : 0;

    /// <summary>
    /// 활성 규칙 한 곳(메뉴·툴바·단축키 모두 이 값을 CanExecute 로 본다).
    /// 압축 파일 없음 / 선택 없음 / 여러 개 선택 / 형식이 Modify·Comment 미지원 / 작업 중 이면 비활성.
    /// </summary>
    private bool CanRun(string key)
    {
        if (key == "app.exit") return true;
        if (_busy) return false;
        var caps = _archive?.Engine.Capabilities;
        bool browsing = Browsing && !Finding;
        int selected = SelectionCount;
        return key switch
        {
            "archive.open" or "archive.create" or "app.new-window" or "app.preferences" or "help.about" or "help.install-folder" or "help.docs"
                or "view.start" or "view.sidebar" or "view.statusbar" or "view.comment" or "view.toolbar" or "view.listmode" or "view.columns"
                or "view.topmost" or "view.theme" => true,
            "view.find" => Browsing,
            "app.update" => CanCheckUpdate,
            "archive.close" or "archive.reveal" or "archive.save-as" or "archive.delete-file" => _archive is not null,
            "archive.test" => caps is { Test: true },
            "archive.extract" or "archive.extract.smart" => caps is { Extract: true },
            "archive.comment" => caps is { Comment: true },
            "entry.add" => browsing && caps is { Modify: true },
            "entry.paste" => browsing && caps is { Modify: true } && ClipboardHasFiles(),
            "entry.delete" => browsing && caps is { Modify: true } && selected > 0,
            "entry.rename" => browsing && caps is { Modify: true } && selected == 1,
            "entry.copy" => browsing && caps is { Extract: true } && selected > 0,
            "entry.copy-path" => browsing && selected > 0,
            _ => false,
        };
    }

    private static bool ClipboardHasFiles()
    {
        try
        {
            return Clipboard.ContainsFileDropList();
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return false;
        }
    }

    private Task ExecuteAsync(string key, string? id)
    {
        switch (key)
        {
            case "archive.open": return OpenWithDialogAsync();
            case "archive.create": return CreateAsync();
            case "archive.close": CloseArchive(); break;
            case "archive.save-as": return SaveAsAsync();
            case "archive.delete-file": DeleteArchiveFile(); break;
            case "archive.reveal": RevealArchive(); break;
            case "archive.test": return TestAsync();
            case "archive.extract": return ExtractAsync();
            case "archive.extract.smart": return SmartExtractAsync();
            case "archive.comment": return EditCommentAsync();
            case "app.new-window": StartNewWindow(); break;
            case "app.exit": Close(); break;
            case "app.preferences":
                if (PreferencesWindow.Show(this, ApplySettings)) RestartForLanguage();
                break;
            case "entry.rename": return RenameAsync();
            case "entry.add": return AddAsync();
            case "entry.delete": return DeleteAsync();
            case "entry.copy": return CopyEntriesAsync();
            case "entry.paste": return PasteAsync();
            case "entry.copy-path": CopyEntryPaths(); break;
            case "view.find":
                SearchBox.Focus();
                SearchBox.SelectAll();
                break;
            case "view.start": ToggleStart(); break;
            case "view.sidebar": S.ShowSidebar = !S.ShowSidebar; SaveSettings(); SetSidebar(S.ShowSidebar); break;
            case "view.statusbar": S.ShowStatusBar = !S.ShowStatusBar; SaveSettings(); ApplyStatusBar(); break;
            case "view.comment": S.ShowComment = !S.ShowComment; SaveSettings(); UpdateUi(); break;
            case "view.toolbar": ToolbarCommand(id); break;
            case "view.listmode": ListModeCommand(id); break;
            case "view.columns": ColumnCommand(id); break;
            case "view.topmost": S.Topmost = !S.Topmost; SaveSettings(); Topmost = S.Topmost; UpdateUi(); break;
            case "view.theme": ThemeCommand(); break;
            case "help.about": new AboutWindow { Owner = this }.ShowDialog(); break;
            case "help.install-folder": OpenFolderInExplorer(AppContext.BaseDirectory); break;
            case "help.docs": OpenHelp(); break;
            case "app.update": return CheckForUpdateAsync();
        }
        return Task.CompletedTask;
    }

    // ───────────────────────── 화면 상태 ─────────────────────────

    /// <summary>
    /// 알림 창 대신 상태줄에 한 줄로 알린다(사용자가 할 일이 없는 결과). 다음 명령을 실행하면 지워진다.
    /// 상태 표시줄을 꺼 두어 보이지 않으면 알림이 사라지지 않도록 정보 알림 창으로 보여 준다.
    /// </summary>
    private void ShowNotice(string text)
    {
        if (StatusBarHost.Visibility != Visibility.Visible)
        {
            MessageDialog.Info(this, text);
            return;
        }
        _notice = text;
        UpdateUi();
        AnnounceStatus();
    }

    /// <summary>화면 읽기 프로그램이 바뀐 상태줄 문장을 읽도록 알린다(StatusText 의 LiveSetting=Polite).</summary>
    private void AnnounceStatus() =>
        (UIElementAutomationPeer.FromElement(StatusText) ?? UIElementAutomationPeer.CreatePeerForElement(StatusText))
            ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);

    /// <summary>상태줄 문장: 알림(있으면) + 압축 파일 요약.</summary>
    private void UpdateStatusText()
    {
        var summary = _archive is null
            ? MainText.Status_NoArchive
            : string.Format(MainText.Status_Summary, _archive.FileCount, _archive.FolderCount, _archive.ArchiveSize);
        StatusText.Text = _notice is null ? summary : $"{_notice}  ·  {summary}";
    }

    private void UpdateUi()
    {
        Title = _archive is null ? AppName : $"{_archive.FileName} — {AppName}";
        bool start = ShowingStart;
        BrowserGrid.Visibility = start ? Visibility.Collapsed : Visibility.Visible;
        EmptyState.Visibility = start ? Visibility.Visible : Visibility.Collapsed;
        // '시작 시 시작 화면 표시' 를 끄면 압축 파일이 없을 때 안내 대신 빈 창(F6 으로 볼 수 있음).
        StartContent.Visibility = _archive is not null || S.ShowStartScreen || _startForced ? Visibility.Visible : Visibility.Collapsed;
        EmptyReturnHint.Visibility = _archive is not null && _showStart ? Visibility.Visible : Visibility.Collapsed;
        SearchBox.IsEnabled = !start;
        UpdateStatusText();

        CommentPane.Visibility = S.ShowComment && _archive is not null ? Visibility.Visible : Visibility.Collapsed;
        CommentText.Text = string.IsNullOrEmpty(_archive?.Comment) ? MainText.Comment_None : _archive!.Comment;

        SetChecked("view.start", start);
        SetChecked("view.sidebar", S.ShowSidebar);
        SetChecked("view.statusbar", S.ShowStatusBar);
        SetChecked("view.comment", S.ShowComment);
        SetChecked("view.topmost", S.Topmost);
        UpdateRadioChecks();
        UpdateTooltips();
        RefreshCommandState();
    }

    /// <summary>작업 중·선택 변경 뒤: 명령 CanExecute 를 다시 묻고, 명령이 아닌 풀기 드롭다운 단추도 맞춘다.</summary>
    private void RefreshCommandState()
    {
        if (_extractMoreButton is not null) _extractMoreButton.IsEnabled = CanRun("archive.extract");
        CommandManager.InvalidateRequerySuggested();
    }

    private void SetChecked(string key, bool value)
    {
        if (!_menuItems.TryGetValue(key, out var items)) return;
        foreach (var item in items) item.IsChecked = value;
    }

    private void SetCheckedId(string id, bool value)
    {
        if (_menuById.TryGetValue(id, out var item)) item.IsChecked = value;
    }

    private void UpdateTooltips()
    {
        foreach (var key in CommandCatalog.Implemented)
        {
            var spec = CommandCatalog.Spec(key);
            var reason = DisabledReason(key);
            var shortcut = CommandCatalog.Gesture(key);
            // 말줄임표는 메뉴 항목의 '추가 입력 필요' 표시라 설명인 툴팁에는 넣지 않는다.
            var name = spec.Header.TrimEnd('…');
            var baseText = shortcut is null ? name : $"{name} ({shortcut})";
            var full = reason is null ? baseText : $"{baseText}\n{reason}";
            if (_toolbarButtons.TryGetValue(key, out var button))
            {
                button.ToolTip = full;
                AutomationProperties.SetHelpText(button, full);
            }
            if (_menuItems.TryGetValue(key, out var items))
                foreach (var item in items) item.ToolTip = reason;
        }
    }

    /// <summary>명령을 쓸 수 없는 이유(툴팁). 쓸 수 있으면 null.</summary>
    private string? DisabledReason(string key)
    {
        if (_busy && key != "app.exit") return MainText.Disabled_Busy;
        var caps = _archive?.Engine.Capabilities;
        string format = _archive?.Format switch
        {
            ArchiveFormat.Zip => "ZIP", ArchiveFormat.SevenZip => "7Z", ArchiveFormat.Tar => "TAR", ArchiveFormat.TarGz => "TAR.GZ",
            ArchiveFormat.GZip => "GZ", ArchiveFormat.Rar => "RAR", _ => MainText.Disabled_FormatUnknown,
        };
        bool needsArchive = key is "archive.close" or "archive.reveal" or "archive.save-as" or "archive.delete-file" or "archive.test"
            or "archive.extract" or "archive.extract.smart" or "archive.comment" or "view.find"
            or "entry.add" or "entry.paste" or "entry.delete" or "entry.rename" or "entry.copy" or "entry.copy-path";
        if (needsArchive && _archive is null) return MainText.Disabled_NoArchive;
        bool entryCommand = key.StartsWith("entry.", StringComparison.Ordinal);
        if (entryCommand && _showStart) return MainText.Disabled_StartScreen;
        if (entryCommand && Finding) return MainText.Disabled_FindResults;
        switch (key)
        {
            case "entry.add" or "entry.paste" or "entry.delete" or "entry.rename" when caps is { Modify: false }:
                return string.Format(MainText.Disabled_ReadOnly, format);
            case "archive.comment" when caps is { Comment: false }:
                return string.Format(MainText.Disabled_NoCommentEdit, format);
            case "archive.test" when caps is { Test: false }:
                return string.Format(MainText.Disabled_NoTest, format);
            case "archive.extract" or "archive.extract.smart" or "entry.copy" when caps is { Extract: false }:
                return string.Format(MainText.Disabled_NoExtract, format);
            case "entry.paste" when !ClipboardHasFiles():
                return MainText.Clipboard_NoFiles;
            case "entry.delete" or "entry.copy" or "entry.copy-path" when SelectionCount == 0:
                return MainText.Disabled_SelectItem;
            case "entry.rename" when SelectionCount != 1:
                return MainText.Disabled_SelectOne;
            case "app.update" when !CanCheckUpdate:
                return MainText.Disabled_Updating;
            default:
                return null;
        }
    }

    private void ToggleStart()
    {
        if (_archive is null)
        {
            // 압축 파일이 없을 때는 '시작 화면 표시' 를 꺼 둔 경우에만 안내를 켜고 끈다.
            _showStart = false;
            if (!S.ShowStartScreen) _startForced = !_startForced;
            UpdateUi();
            return;
        }
        _showStart = !_showStart;
        UpdateUi();
        if (_showStart) EmptyOpenButton.Focus();
        else FocusList();
    }

    private void SetSidebar(bool visible)
    {
        bool shown = SidebarHost.Visibility == Visibility.Visible;
        if (visible != shown)
        {
            if (visible)
            {
                SidebarColumn.MinWidth = 120;
                SidebarColumn.Width = _sidebarWidth;
                SidebarHost.Visibility = Visibility.Visible;
                SidebarSplitter.Visibility = Visibility.Visible;
            }
            else
            {
                _sidebarWidth = SidebarColumn.Width;
                SidebarColumn.MinWidth = 0;
                SidebarColumn.Width = new GridLength(0);
                SidebarHost.Visibility = Visibility.Collapsed;
                SidebarSplitter.Visibility = Visibility.Collapsed;
            }
        }
        UpdateUi();
    }

    private void ApplyStatusBar()
    {
        StatusBarHost.Visibility = S.ShowStatusBar ? Visibility.Visible : Visibility.Collapsed;
        UpdateUi();
    }

    // ───────────────────────── 탐색 ─────────────────────────

    private void Navigate(string folder, string? selectName = null)
    {
        if (_archive is null) return;
        if (!_archive.FolderExists(folder)) folder = "";
        _folder = folder;

        _view = new ListCollectionView(_archive.RowsIn(folder)) { CustomSort = _comparer };
        EntryGrid.ItemsSource = _view;
        EntryList.ItemsSource = _view;
        ApplySortGlyphs();

        PathText.Text = folder.Length == 0 ? _archive.FileName : $"{_archive.FileName}  ›  {folder.Replace("/", "  ›  ")}";
        UpButton.IsEnabled = folder.Length > 0;
        SyncTree(folder);

        if (selectName is not null)
        {
            var row = _view.Cast<EntryRow>().FirstOrDefault(r => r.Name == selectName);
            if (row is not null)
            {
                ActiveList.SelectedItem = row;
                if (DetailsMode) EntryGrid.ScrollIntoView(row);
                else EntryList.ScrollIntoView(row);
            }
        }
        UpdateUi();
    }

    private void SyncTree(string folder)
    {
        if (_root is null) return;
        var node = _root.Find(folder);
        if (node is null) return;
        _syncingTree = true;
        try
        {
            for (var p = node.Parent; p is not null; p = p.Parent) p.IsExpanded = true;
            if (_selectedNode is not null && _selectedNode != node) _selectedNode.IsSelected = false;
            node.IsSelected = true;
            _selectedNode = node;
        }
        finally
        {
            _syncingTree = false;
        }
    }

    private void GoUp()
    {
        if (_archive is null || _folder.Length == 0) return;
        var leaf = ArchiveModel.LeafName(_folder);
        Navigate(ArchiveModel.Parent(_folder), leaf);
        FocusList();
    }

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_syncingTree || e.NewValue is not FolderNode node) return;
        _selectedNode = node;
        if (Finding) SearchBox.Text = "";
        if (node.Path != _folder) Navigate(node.Path);
    }

    private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        var source = e.OriginalSource as DependencyObject;
        object? item = sender == EntryGrid ? FindAncestor<DataGridRow>(source)?.Item : FindAncestor<ListBoxItem>(source)?.DataContext;
        if (item is EntryRow { IsDirectory: true } entry)
        {
            Navigate(entry.Path);
            e.Handled = true;
        }
    }

    private void OnBrowseKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Back && Keyboard.Modifiers == ModifierKeys.None)
        {
            GoUp();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && (sender == EntryGrid || sender == EntryList) && Keyboard.Modifiers == ModifierKeys.None
                 && ActiveSelection.Count == 1 && ActiveSelection[0] is EntryRow { IsDirectory: true } dir)
        {
            Navigate(dir.Path);
            FocusList();
            e.Handled = true;
        }
    }

    private void OnUpClick(object sender, RoutedEventArgs e) => GoUp();

    private void OnGridSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateTooltips();
        RefreshCommandState();
    }

    private void OnGridSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var member = e.Column.SortMemberPath;
        var direction = _comparer.Member == member && _comparer.Direction == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;
        _comparer = new EntryComparer(member, direction);
        if (_view is not null) _view.CustomSort = _comparer;
        ApplySortGlyphs();
    }

    private void ApplySortGlyphs()
    {
        foreach (var column in EntryGrid.Columns)
            column.SortDirection = column.SortMemberPath == _comparer.Member ? _comparer.Direction : null;
    }

    private List<EntryRow> SelectedRows() => SelectionCount == 0 ? [] : ActiveSelection.OfType<EntryRow>().ToList();

    /// <summary>목록에 키보드 포커스를 줄 때 현재 셀(항목)을 두어 방향키가 바로 동작하게 한다(선택은 바꾸지 않음).</summary>
    private void FocusList()
    {
        if (BrowserGrid.Visibility != Visibility.Visible) return;
        if (!DetailsMode)
        {
            FocusEntryList();
            return;
        }
        if (_view is null || _view.IsEmpty || EntryGrid.Columns.Count == 0)
        {
            EntryGrid.Focus();
            return;
        }
        bool hadSelection = EntryGrid.SelectedItems.Count > 0;
        var item = EntryGrid.SelectedItem ?? _view.GetItemAt(0);
        EntryGrid.CurrentCell = new DataGridCellInfo(item, EntryGrid.Columns[0]);
        EntryGrid.ScrollIntoView(item);
        EntryGrid.UpdateLayout();
        if (EntryGrid.ItemContainerGenerator.ContainerFromItem(item) is DataGridRow row
            && EntryGrid.Columns[0].GetCellContent(row)?.Parent is DataGridCell cell)
            cell.Focus();
        else
            EntryGrid.Focus();
        // FullRow 목록은 셀 포커스만으로 행을 선택한다. 선택이 없던 상태를 유지해야
        // 풀기(Ctrl+E)가 전체를 푼다(DEF-01: 열자마자 첫 행만 풀리던 결함).
        if (!hadSelection) EntryGrid.UnselectAll();
    }

    private void FocusEntryList()
    {
        if (_view is null || _view.IsEmpty)
        {
            EntryList.Focus();
            return;
        }
        bool hadSelection = EntryList.SelectedItems.Count > 0;
        var item = EntryList.SelectedItem ?? _view.GetItemAt(0);
        EntryList.ScrollIntoView(item);
        EntryList.UpdateLayout();
        if (EntryList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container) container.Focus();
        else EntryList.Focus();
        if (!hadSelection) EntryList.UnselectAll(); // DEF-01 과 같은 규칙
    }

    private void OnGridGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Tab 으로 목록 자체에 포커스가 오면 첫(또는 선택된) 행의 셀로 넘긴다.
        if (e.NewFocus == EntryGrid) Dispatcher.BeginInvoke(FocusList);
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null and not T)
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    // ───────────────────────── 끌어 놓기 ─────────────────────────

    private void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnPreviewDrop(object sender, DragEventArgs e)
    {
        if (_busy || e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
        e.Handled = true;
        try
        {
            await OpenArchiveAsync(files[0]);
        }
        catch (Exception ex)
        {
            MessageDialog.Error(this, MainText.OpenArchiveFailed_Title, ex.Message);
        }
    }

    // ───────────────────────── 작업 실행(UI 스레드 밖) ─────────────────────────

    /// <summary>작업을 백그라운드에서 실행하고, 300ms 넘게 걸리면 모달 진행 창(진행률·속도·남은 시간·취소)을 띄운다.</summary>
    private async Task<T> RunAsync<T>(string heading, string idleText, Func<IProgress<ProgressInfo>, CancellationToken, T> work)
    {
        _busy = true;
        UpdateTooltips();
        RefreshCommandState();
        using var cts = new CancellationTokenSource();
        var reporter = new ProgressReporter();
        var task = Task.Run(() => work(reporter, cts.Token), CancellationToken.None);
        try
        {
            if (await Task.WhenAny(task, Task.Delay(300)) != task)
            {
                var progress = new ProgressWindow(heading, idleText, reporter, task, cts) { Owner = this };
                progress.ShowDialog();
            }
            return await task;
        }
        finally
        {
            _busy = false;
            UpdateTooltips();
            RefreshCommandState();
        }
    }

    private Task<OperationResult> RunOperationAsync(string heading, string idleText, Func<IProgress<ProgressInfo>, CancellationToken, OperationResult> work) =>
        RunAsync(heading, idleText, (progress, cancel) =>
        {
            try
            {
                return work(progress, cancel);
            }
            catch (OperationCanceledException)
            {
                return OperationResult.Cancelled();
            }
            catch (Exception ex)
            {
                return OperationResult.Fail(ex.Message);
            }
        });

    private void SaveSettings()
    {
        var error = AppSettings.SaveCurrent();
        if (error is not null) MessageDialog.Error(this, MainText.SaveSettingsFailed_Title, error);
    }
}
