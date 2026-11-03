using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using OhMyZip.App.Commands;
using OhMyZip.App.Dialogs;
using OhMyZip.App.Models;
using OhMyZip.App.Settings;
using OhMyZip.App.Theme;

namespace OhMyZip.App;

/// <summary>보기 설정: 테마·툴바·목록 방식·칼럼·항상 위·찾기. 모든 값은 AppSettings 에 저장된다.</summary>
public partial class MainWindow
{
    /// <summary>보기 &gt; 툴바 항목 Id → 방식(OZ-C035 '툴바 버튼 설정' 은 대화상자).</summary>
    private static readonly IReadOnlyDictionary<string, ToolbarMode> ToolbarIds = new Dictionary<string, ToolbarMode>(StringComparer.Ordinal)
    {
        ["OZ-C031"] = ToolbarMode.Large,
        ["OZ-C032"] = ToolbarMode.Small,
        ["OZ-C033"] = ToolbarMode.Classic,
        ["OZ-C034"] = ToolbarMode.Hidden,
    };

    private static readonly IReadOnlyDictionary<string, ListMode> ListModeIds = new Dictionary<string, ListMode>(StringComparer.Ordinal)
    {
        ["OZ-C036"] = ListMode.Details,
        ["OZ-C037"] = ListMode.List,
        ["OZ-C038"] = ListMode.Icons,
        ["OZ-C039"] = ListMode.LargeIcons,
    };

    /// <summary>찾기 결과를 한 번에 보여 줄 최대 개수(그 이상은 조건을 좁히라고 안내).</summary>
    private const int MaxFindResults = 5000;

    /// <summary>시작 시·환경 설정 확인·초기화 뒤 한 번에 적용한다.</summary>
    private void ApplySettings(AppSettings s)
    {
        ThemeManager.Override = s.Theme;
        ThemeManager.ReduceMotion = s.ReduceMotion;
        ThemeManager.Reapply(Application.Current);
        Topmost = s.Topmost;
        ApplyToolbar();
        ApplyColumns();
        ApplyListMode();
        StatusBarHost.Visibility = s.ShowStatusBar ? Visibility.Visible : Visibility.Collapsed;
        SetSidebar(s.ShowSidebar);
    }

    private void UpdateRadioChecks()
    {
        foreach (var (id, mode) in ToolbarIds) SetCheckedId(id, S.ToolbarMode == mode);
        foreach (var (id, mode) in ListModeIds) SetCheckedId(id, S.ListMode == mode);
        foreach (var c in AppSettings.ColumnKeys) SetCheckedId(c.Id, S.IsColumnVisible(c.Key));
    }

    // ───────────────────────── 툴바 ─────────────────────────

    private void ToolbarCommand(string? id)
    {
        if (id is not null && ToolbarIds.TryGetValue(id, out var mode))
        {
            S.ToolbarMode = mode;
        }
        else
        {
            var buttons = CommandCatalog.ToolbarGroups.SelectMany(g => g).Select(t => (t.Key, t.Label)).ToList();
            var hidden = SimpleDialogs.ToolbarButtons(this, buttons, S.HiddenToolbarButtons);
            if (hidden is null) return;
            S.HiddenToolbarButtons = hidden;
        }
        SaveSettings();
        ApplyToolbar();
        UpdateUi();
    }

    private void ApplyToolbar()
    {
        var mode = S.ToolbarMode;
        ToolbarPanel.Visibility = mode == ToolbarMode.Hidden ? Visibility.Collapsed : Visibility.Visible;
        foreach (var group in CommandCatalog.ToolbarGroups)
        {
            foreach (var t in group)
            {
                var button = _toolbarButtons[t.Key];
                string style = (mode, t.IsPrimary) switch
                {
                    (ToolbarMode.Small, true) => "Oz.ToolbarSmallAccentButton",
                    (ToolbarMode.Small, false) => "Oz.ToolbarSmallButton",
                    (ToolbarMode.Classic, true) => "Oz.ToolbarClassicAccentButton",
                    (ToolbarMode.Classic, false) => "Oz.ToolbarClassicButton",
                    (_, true) => "Oz.ToolbarAccentButton",
                    _ => "Oz.ToolbarButton",
                };
                button.Style = (Style)FindResource(style);
                button.Visibility = S.HiddenToolbarButtons.Contains(t.Key) ? Visibility.Collapsed : Visibility.Visible;
            }
        }
        if (_extractMoreButton is not null)
        {
            _extractMoreButton.Style = (Style)FindResource(S.ToolbarMode == ToolbarMode.Classic ? "Oz.ToolbarClassicButton" : "Oz.ToolbarSmallButton");
            _extractMoreButton.Content = S.ToolbarMode == ToolbarMode.Classic ? "▾" : "풀기 방식";
            _extractMoreButton.MinWidth = 22;
            _extractMoreButton.Padding = new Thickness(2, 4, 2, 4);
            _extractMoreButton.Visibility = _toolbarButtons["archive.extract"].Visibility;
        }
        // 묶음 전체가 숨겨지면 앞 구분 여백도 숨긴다.
        foreach (var (separator, group) in _toolbarSeparators)
            separator.Visibility = group.All(t => S.HiddenToolbarButtons.Contains(t.Key)) ? Visibility.Collapsed : Visibility.Visible;
    }

    // ───────────────────────── 목록 방식·칼럼 ─────────────────────────

    private void ListModeCommand(string? id)
    {
        if (id is null || !ListModeIds.TryGetValue(id, out var mode)) return;
        S.ListMode = mode;
        SaveSettings();
        ApplyListMode();
        FocusList();
    }

    private void ApplyListMode()
    {
        var mode = S.ListMode;
        // 방식을 바꾸면 선택은 비운다(두 목록이 다른 선택을 들고 있지 않게).
        EntryGrid.UnselectAll();
        EntryList.UnselectAll();
        EntryGrid.Visibility = mode == ListMode.Details ? Visibility.Visible : Visibility.Collapsed;
        EntryList.Visibility = mode == ListMode.Details ? Visibility.Collapsed : Visibility.Visible;
        if (mode != ListMode.Details)
        {
            EntryList.ItemTemplate = (DataTemplate)FindResource(mode switch
            {
                ListMode.List => "Oz.ListTemplate",
                ListMode.Icons => "Oz.IconTemplate",
                _ => "Oz.LargeIconTemplate",
            });
            bool vertical = mode == ListMode.List;
            EntryList.ItemsPanel = (ItemsPanelTemplate)FindResource(vertical ? "Oz.VerticalWrap" : "Oz.HorizontalWrap");
            // 간단히 = 세로로 채우고 가로 스크롤(탐색기 '목록'), 아이콘 = 가로로 채우고 세로 스크롤.
            ScrollViewer.SetHorizontalScrollBarVisibility(EntryList, vertical ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(EntryList, vertical ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
        }
        UpdateUi();
    }

    private DataGridColumn ColumnFor(string key) => key switch
    {
        "compressed" => ColCompressed,
        "size" => ColSize,
        "type" => ColType,
        "modified" => ColModified,
        "method" => ColMethod,
        "encryption" => ColEncryption,
        "crc" => ColCrc,
        "attributes" => ColAttributes,
        "description" => ColDescription,
        _ => ColExtra,
    };

    private void ColumnCommand(string? id)
    {
        var column = AppSettings.ColumnKeys.FirstOrDefault(c => c.Id == id);
        if (column.Key is null) return;
        S.Columns[column.Key] = !S.IsColumnVisible(column.Key);
        SaveSettings();
        ApplyColumns();
        UpdateUi();
    }

    private void ApplyColumns()
    {
        foreach (var c in AppSettings.ColumnKeys)
            ColumnFor(c.Key).Visibility = S.IsColumnVisible(c.Key) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>목록 머리글 오른쪽 클릭 = 칼럼 설정(OZ-F018). 메뉴의 보기 &gt; 칼럼 설정과 같은 명령.</summary>
    private void OnGridHeaderRightClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject) is null) return;
        var menu = new ContextMenu { PlacementTarget = EntryGrid, Placement = PlacementMode.MousePoint };
        foreach (var c in AppSettings.ColumnKeys)
        {
            var item = new MenuItem
            {
                Header = c.Header,
                Command = CommandCatalog.Command("view.columns"),
                CommandParameter = c.Id,
                CommandTarget = this,
                IsChecked = S.IsColumnVisible(c.Key),
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
        e.Handled = true;
    }

    // ───────────────────────── 테마 ─────────────────────────

    private void ThemeCommand()
    {
        var theme = SimpleDialogs.ColorTheme(this, S.Theme);
        if (theme is null || theme == S.Theme) return;
        S.Theme = theme.Value;
        SaveSettings();
        ThemeManager.Override = S.Theme;
        ThemeManager.Reapply(Application.Current);
    }

    // ───────────────────────── 찾기(Ctrl+F) ─────────────────────────

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _search = SearchBox.Text.Trim();
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateFind();
    }

    /// <summary>압축파일 전체(모든 폴더)에서 이름을 찾는다. 결과는 전체 경로 목록.</summary>
    private void UpdateFind()
    {
        if (_archive is null || _search.Length == 0)
        {
            FindPanel.Visibility = Visibility.Collapsed;
            FindResults.ItemsSource = null;
            UpdateUi();
            return;
        }
        var query = new FindQuery(_search);
        var hits = _archive.AllPaths()
            .Where(p => query.IsMatch(p.Path))
            .OrderBy(p => p.Path, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxFindResults + 1)
            .Select(p => new FindHit(p.Path, p.IsDirectory))
            .ToList();
        bool more = hits.Count > MaxFindResults;
        if (more) hits.RemoveAt(hits.Count - 1);
        FindResults.ItemsSource = hits;
        FindSummary.Text = hits.Count == 0
            ? $"'{_search}' 와(과) 맞는 항목이 없습니다 (압축파일 전체에서 찾음)."
            : more
                ? $"'{_search}' 찾기 결과: 처음 {MaxFindResults:N0}개만 표시 — 조건을 좁히세요. Enter = 위치로 이동, Esc = 지우기"
                : $"'{_search}' 찾기 결과: {hits.Count:N0}개 (압축파일 전체). Enter = 위치로 이동, Esc = 지우기";
        FindPanel.Visibility = Visibility.Visible;
        UpdateUi();
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (SearchBox.Text.Length > 0) SearchBox.Text = "";
            else FocusList();
            e.Handled = true;
        }
        else if (e.Key is Key.Enter or Key.Down)
        {
            if (Finding && FindResults.Items.Count > 0)
            {
                if (e.Key == Key.Enter && FindResults.Items.Count == 1)
                {
                    GoToHit((FindHit)FindResults.Items[0]);
                }
                else
                {
                    FindResults.SelectedIndex = 0;
                    FindResults.UpdateLayout();
                    (FindResults.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
                }
            }
            else if (!Finding)
            {
                FocusList();
            }
            e.Handled = true;
        }
    }

    private void OnFindResultsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && FindResults.SelectedItem is FindHit hit)
        {
            GoToHit(hit);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            SearchBox.Text = "";
            FocusList();
            e.Handled = true;
        }
    }

    private void OnFindResultsDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is FindHit hit)
        {
            GoToHit(hit);
            e.Handled = true;
        }
    }

    /// <summary>찾기 결과 → 그 항목이 있는 폴더로 가서 항목을 선택한다.</summary>
    private void GoToHit(FindHit hit)
    {
        SearchBox.Text = "";
        Navigate(ArchiveModel.Parent(hit.Path), ArchiveModel.LeafName(hit.Path));
        FocusList();
    }
}
