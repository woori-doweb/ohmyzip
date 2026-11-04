using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using Microsoft.Win32;
using OhMyZip.App.Settings;
using OhMyZip.Core;

namespace OhMyZip.App.Dialogs;

/// <summary>
/// 환경 설정(F5). 9개 범주를 두고, 동작하는 설정과 '준비 중' 안내를 구분해 보여 준다.
/// 확인 = 저장 + 적용, 취소 = 아무것도 바꾸지 않음, 초기화 = 확인 후 모든 설정을 기본값으로 저장 + 적용.
/// 파일 연결 페이지의 단추는 확인·취소와 무관하게 누르는 즉시 현재 사용자(HKCU) 범위에 적용한다.
/// </summary>
public partial class PreferencesWindow : Window
{
    public static readonly IReadOnlyList<string> Categories =
    [
        "일반 설정", "파일 연결", "탐색기 메뉴", "압축 풀기", "압축하기", "보기/편집", "보안 설정", "고급 설정", "언어 설정",
    ];

    private static readonly (AppTheme Value, string Label)[] Themes =
    [
        (AppTheme.System, "시스템 설정 따르기"), (AppTheme.Light, "라이트"), (AppTheme.Dark, "다크"),
    ];

    private static readonly (ExtractDestination Value, string Label)[] Destinations =
    [
        (ExtractDestination.ArchiveFolder, "압축 파일이 있는 폴더"),
        (ExtractDestination.ArchiveNameFolder, "압축 파일 이름으로 된 폴더"),
        (ExtractDestination.LastUsed, "마지막으로 푼 위치"),
    ];

    private static readonly (ConflictChoice Value, string Label)[] Conflicts =
    [
        (ConflictChoice.Ask, "묻기"), (ConflictChoice.Overwrite, "덮어쓰기"), (ConflictChoice.Skip, "건너뛰기"), (ConflictChoice.Rename, "이름 바꾸기"),
    ];

    private static readonly (ListMode Value, string Label)[] ListModes =
    [
        (ListMode.Details, "자세히"), (ListMode.List, "간단히"), (ListMode.Icons, "아이콘"), (ListMode.LargeIcons, "큰 아이콘"),
    ];

    private readonly Action<AppSettings> _apply;
    private readonly AppSettings _working;
    private readonly Dictionary<string, CheckBox> _columnBoxes = new(StringComparer.Ordinal);

    private PreferencesWindow(Action<AppSettings> apply)
    {
        InitializeComponent();
        _apply = apply;
        _working = AppSettings.Current.Clone();
        CategoryList.ItemsSource = Categories;
        ThemeBox.ItemsSource = Themes.Select(t => t.Label).ToList();
        DestinationBox.ItemsSource = Destinations.Select(d => d.Label).ToList();
        ConflictBox.ItemsSource = Conflicts.Select(c => c.Label).ToList();
        FormatBox.ItemsSource = CreateArchiveDialog.Formats.Select(f => f.Label).ToList();
        LevelBox.ItemsSource = CreateArchiveDialog.LevelLabels;
        ListModeBox.ItemsSource = ListModes.Select(m => m.Label).ToList();
        foreach (var (key, _, header, _) in AppSettings.ColumnKeys)
        {
            var box = new CheckBox { Content = header, Margin = new Thickness(0, 6, 18, 0), MinWidth = 110 };
            AutomationProperties.SetAutomationId(box, "OZ-Pref-Column-" + key);
            _columnBoxes[key] = box;
            ColumnPanel.Children.Add(box);
        }
        SettingsPathText.Text = $"설정 파일: {AppSettings.FilePath}";
        FileAssocExtensionsText.Text = $"대상 확장자: {ExtensionList}. 확장자의 지금 기본 프로그램 설정은 바꾸지 않고 '연결 프로그램' 후보로만 더합니다.";
        RefreshFileAssocStatus();
        Load(_working);
        CategoryList.SelectedIndex = 0;
        Loaded += (_, _) => (CategoryList.ItemContainerGenerator.ContainerFromIndex(0) as UIElement)?.Focus();
        SourceInitialized += (_, _) => NativeMethods.ApplyDarkTitleBar(this);
    }

    private static int Index<T>((T Value, string Label)[] items, T value) where T : struct, Enum =>
        Math.Max(0, Array.FindIndex(items, i => EqualityComparer<T>.Default.Equals(i.Value, value)));

    private void Load(AppSettings s)
    {
        ShowStartScreenBox.IsChecked = s.ShowStartScreen;
        ThemeBox.SelectedIndex = Index(Themes, s.Theme);
        ReduceMotionBox.IsChecked = s.ReduceMotion;
        DestinationBox.SelectedIndex = Index(Destinations, s.ExtractDestination);
        ConflictBox.SelectedIndex = Index(Conflicts, s.ExtractConflict);
        OpenAfterBox.IsChecked = s.OpenFolderAfterExtract;
        var formatIndex = CreateArchiveDialog.Formats.Select((f, i) => (f, i)).FirstOrDefault(x => x.f.Format == s.CreateFormat).i;
        FormatBox.SelectedIndex = formatIndex;
        LevelBox.SelectedIndex = Math.Clamp(s.CreateLevel, 0, 9);
        ConfirmDeleteBox.IsChecked = s.ConfirmDelete;
        ListModeBox.SelectedIndex = Index(ListModes, s.ListMode);
        foreach (var (key, box) in _columnBoxes) box.IsChecked = s.IsColumnVisible(key);
    }

    private void Store(AppSettings s)
    {
        s.ShowStartScreen = ShowStartScreenBox.IsChecked == true;
        s.Theme = Themes[Math.Max(0, ThemeBox.SelectedIndex)].Value;
        s.ReduceMotion = ReduceMotionBox.IsChecked == true;
        s.ExtractDestination = Destinations[Math.Max(0, DestinationBox.SelectedIndex)].Value;
        s.ExtractConflict = Conflicts[Math.Max(0, ConflictBox.SelectedIndex)].Value;
        s.OpenFolderAfterExtract = OpenAfterBox.IsChecked == true;
        s.CreateFormat = CreateArchiveDialog.Formats[Math.Max(0, FormatBox.SelectedIndex)].Format;
        s.CreateLevel = Math.Clamp(LevelBox.SelectedIndex, 0, 9);
        s.ConfirmDelete = ConfirmDeleteBox.IsChecked == true;
        s.ListMode = ListModes[Math.Max(0, ListModeBox.SelectedIndex)].Value;
        foreach (var (key, box) in _columnBoxes) s.Columns[key] = box.IsChecked == true;
    }

    private void OnCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        int index = CategoryList.SelectedIndex;
        for (int i = 0; i < Pages.Children.Count; i++)
            Pages.Children[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Store(_working);
        var error = AppSettings.Save(_working);
        _apply(_working);
        if (error is not null) MessageDialog.Error(this, "설정을 적용했지만 저장하지 못했습니다", error);
        DialogResult = true;
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        int answer = MessageDialog.Show(this, DialogKind.Question, "모든 ohmyZip 설정을 기본값으로 되돌릴까요?",
            $"범위: 모든 ohmyZip 설정을 기본값으로(일반·압축 풀기·압축하기·보기/편집, 보기 메뉴의 툴바·칼럼·목록 방식·항상 위 포함). 바로 저장되며 되돌릴 수 없습니다.\n설정 파일: {AppSettings.FilePath}",
            null, ["초기화", "취소"], defaultIndex: 0, cancelIndex: 1);
        if (answer != 0) return;
        var defaults = new AppSettings();
        var error = AppSettings.Save(defaults);
        _apply(defaults);
        if (error is not null) MessageDialog.Error(this, "기본값을 적용했지만 저장하지 못했습니다", error);
        DialogResult = true;
    }

    // ───────────────────────── 파일 연결(즉시 적용) ─────────────────────────

    private static string ExtensionList => string.Join(", ", FileAssociations.Extensions);

    private static bool IsRegistryError(Exception ex) =>
        ex is UnauthorizedAccessException or SecurityException or IOException or ArgumentException;

    private void RefreshFileAssocStatus()
    {
        string? exePath = Environment.ProcessPath;
        FileAssocRegisterButton.IsEnabled = exePath is not null;
        if (exePath is null)
        {
            FileAssocStatusText.Text = "ⓘ 실행 파일 경로를 알 수 없어 등록할 수 없습니다.";
            FileAssocUnregisterButton.IsEnabled = true;
            return;
        }
        FileAssociationStatus status;
        try
        {
            status = FileAssociations.GetStatus(Registry.CurrentUser, exePath);
        }
        catch (Exception ex) when (IsRegistryError(ex))
        {
            FileAssocStatusText.Text = $"ⓘ 등록 상태를 읽지 못했습니다: {ex.Message}";
            FileAssocUnregisterButton.IsEnabled = true;
            return;
        }
        FileAssocStatusText.Text = status switch
        {
            FileAssociationStatus.Registered =>
                "ⓘ 등록됨 — Windows 기본 앱에서 ohmyZip 을 고를 수 있습니다. 지금 기본 프로그램인지는 Windows 기본 앱 설정에서 확인하세요.",
            FileAssociationStatus.RegisteredElsewhere =>
                "ⓘ 다른 위치의 ohmyZip 이 등록돼 있음 — 다시 등록하면 이 위치로 바뀝니다.",
            _ => "ⓘ 등록되지 않음 — Windows 기본 앱 목록에 ohmyZip 이 없습니다. 압축 파일은 ohmyZip 창으로 끌어 놓거나 열기(Ctrl+O)로 여세요.",
        };
        FileAssocUnregisterButton.IsEnabled = status != FileAssociationStatus.NotRegistered;
    }

    private void OnFileAssocRegister(object sender, RoutedEventArgs e)
    {
        string? exePath = Environment.ProcessPath;
        if (exePath is null)
        {
            MessageDialog.Error(this, "파일 연결을 등록하지 못했습니다", "실행 파일 경로를 알 수 없습니다.");
            return;
        }
        int answer = MessageDialog.Show(this, DialogKind.Question, "ohmyZip 을 압축 파일 기본 프로그램 후보로 등록할까요?",
            "현재 사용자(HKCU) 범위에만 기록하며 관리자 권한이 필요하지 않습니다. 확장자의 지금 기본 프로그램 설정은 바꾸지 않습니다.\n"
            + "등록한 뒤 열리는 Windows 기본 앱 설정에서 ohmyZip 을 골라야 기본 프로그램이 됩니다.\n"
            + "되돌리기: 이 페이지의 '등록 해제' (또는 ohmyZip.exe --unregister-file-types).\n\n"
            + $"대상 확장자: {ExtensionList}\n"
            + $"실행 파일: {exePath}\n"
            + "기록할 곳(모두 HKCU 아래):\n"
            + $"  {FileAssociations.ProgIdPath}\n"
            + $"  {FileAssociations.ApplicationPath}\n"
            + $"  {FileAssociations.CapabilitiesPath}\n"
            + $"  {FileAssociations.RegisteredApplicationsPath} 의 {FileAssociations.AppName} 값\n"
            + $"  {FileAssociations.ClassesPath}\\<확장자>\\OpenWithProgids 의 {FileAssociations.ProgId} 값",
            null, ["등록", "취소"], defaultIndex: 0, cancelIndex: 1);
        if (answer != 0) return;
        try
        {
            FileAssociations.Register(Registry.CurrentUser, exePath);
            FileAssociations.NotifyShell();
        }
        catch (Exception ex) when (IsRegistryError(ex))
        {
            RefreshFileAssocStatus();
            // 일부만 기록됐을 수 있으므로 해제는 항상 누를 수 있게 둔다.
            FileAssocUnregisterButton.IsEnabled = true;
            MessageDialog.Error(this, "파일 연결을 등록하지 못했습니다", $"{ex.Message}\n일부만 기록됐을 수 있습니다. '등록 해제' 로 지울 수 있습니다.");
            return;
        }
        RefreshFileAssocStatus();
        if (OpenDefaultAppsSettings())
            FileAssocStatusText.Text += " 열린 Windows 기본 앱 설정에서 ohmyZip 을 고르면 기본 프로그램이 됩니다.";
        AnnounceFileAssocStatus();
    }

    private void OnFileAssocUnregister(object sender, RoutedEventArgs e)
    {
        try
        {
            FileAssociations.Unregister(Registry.CurrentUser);
            FileAssociations.NotifyShell();
        }
        catch (Exception ex) when (IsRegistryError(ex))
        {
            RefreshFileAssocStatus();
            MessageDialog.Error(this, "파일 연결 등록을 해제하지 못했습니다", ex.Message);
            return;
        }
        RefreshFileAssocStatus();
        // 상태 문장이 해제 전과 같을 수 있으므로(실행 파일 경로를 모를 때 등) 결과를 덧붙인다.
        FileAssocStatusText.Text += " 등록을 해제했습니다. 압축 파일을 열 프로그램은 Windows 기본 앱 설정에서 다시 고르세요.";
        AnnounceFileAssocStatus();
    }

    /// <summary>알림 창 대신 상태 문장으로 알린 결과를 화면 읽기 프로그램이 읽도록 알린다(LiveSetting=Polite).</summary>
    private void AnnounceFileAssocStatus() =>
        (UIElementAutomationPeer.FromElement(FileAssocStatusText) ?? UIElementAutomationPeer.CreatePeerForElement(FileAssocStatusText))
            ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);

    private void OnFileAssocOpenSettings(object sender, RoutedEventArgs e) => OpenDefaultAppsSettings();

    private bool OpenDefaultAppsSettings()
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(FileAssociations.SettingsUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            MessageDialog.Error(this, "Windows 기본 앱 설정을 열 수 없습니다",
                $"Windows 설정 > 앱 > 기본 앱 에서 ohmyZip 을 찾아 고르세요.\n{ex.Message}");
            return false;
        }
    }

    /// <summary>apply 는 확인·초기화 때만 불린다(취소하면 아무것도 바꾸지 않음).</summary>
    public static bool Show(Window owner, Action<AppSettings> apply) =>
        new PreferencesWindow(apply) { Owner = owner }.ShowDialog() == true;
}
