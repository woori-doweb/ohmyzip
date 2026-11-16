using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using Microsoft.Win32;
using OhMyZip.App.Text;
using OhMyZip.App.Settings;
using OhMyZip.Core;

namespace OhMyZip.App.Dialogs;

/// <summary>
/// 환경 설정(F5). 9개 범주를 두고, 동작하는 설정과 '준비 중' 안내를 구분해 보여 준다.
/// 확인 = 저장 + 적용, 취소 = 아무것도 바꾸지 않음, 초기화 = 확인 후 모든 설정을 기본값으로 저장 + 적용.
/// 파일 연결·탐색기 메뉴 페이지의 단추는 확인·취소와 무관하게 누르는 즉시 현재 사용자(HKCU) 범위에 적용한다.
/// </summary>
public partial class PreferencesWindow : Window
{
    public static readonly IReadOnlyList<string> Categories =
    [
        DialogText.Pref_Category_General, DialogText.Pref_Category_FileAssoc, DialogText.Pref_Category_ShellMenu,
        DialogText.Pref_Category_Extract, DialogText.Pref_Category_Create, DialogText.Pref_Category_ViewEdit,
        DialogText.Pref_Category_Security, DialogText.Pref_Category_Advanced, DialogText.Pref_Category_Language,
    ];

    private static readonly (AppTheme Value, string Label)[] Themes =
    [
        (AppTheme.System, DialogText.Theme_System), (AppTheme.Light, DialogText.Theme_Light), (AppTheme.Dark, DialogText.Theme_Dark),
    ];

    private static readonly (AppLanguage Value, string Label)[] Languages =
    [
        (AppLanguage.System, DialogText.Pref_Language_System),
        (AppLanguage.Korean, DialogText.Pref_Language_Korean),
        (AppLanguage.English, DialogText.Pref_Language_English),
    ];

    private static readonly (ExtractDestination Value, string Label)[] Destinations =
    [
        (ExtractDestination.ArchiveFolder, DialogText.Pref_Dest_ArchiveFolder),
        (ExtractDestination.ArchiveNameFolder, DialogText.Pref_Dest_ArchiveNameFolder),
        (ExtractDestination.LastUsed, DialogText.Pref_Dest_LastUsed),
    ];

    private static readonly (ConflictChoice Value, string Label)[] Conflicts =
    [
        (ConflictChoice.Ask, DialogText.Conflict_Ask), (ConflictChoice.Overwrite, DialogText.Conflict_Overwrite),
        (ConflictChoice.Skip, DialogText.Conflict_Skip), (ConflictChoice.Rename, DialogText.Conflict_Rename),
    ];

    private static readonly (ListMode Value, string Label)[] ListModes =
    [
        (ListMode.Details, DialogText.Pref_ListMode_Details), (ListMode.List, DialogText.Pref_ListMode_List),
        (ListMode.Icons, DialogText.Pref_ListMode_Icons), (ListMode.LargeIcons, DialogText.Pref_ListMode_LargeIcons),
    ];

    private readonly Action<AppSettings> _apply;
    private readonly AppSettings _working;
    /// <summary>창을 열 때의 언어 설정(이 창에서 언어를 바꿨는지 판단).</summary>
    private readonly AppLanguage _openedLanguage;
    /// <summary>언어를 바꾼 뒤 지금 다시 시작하기로 했는지.</summary>
    private bool _restart;
    private readonly Dictionary<string, CheckBox> _columnBoxes = new(StringComparer.Ordinal);

    private PreferencesWindow(Action<AppSettings> apply)
    {
        InitializeComponent();
        _apply = apply;
        _working = AppSettings.Current.Clone();
        _openedLanguage = _working.Language;
        CategoryList.ItemsSource = Categories;
        ThemeBox.ItemsSource = Themes.Select(t => t.Label).ToList();
        LanguageBox.ItemsSource = Languages.Select(l => l.Label).ToList();
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
        SettingsPathText.Text = string.Format(DialogText.Pref_SettingsFile, AppSettings.FilePath);
        FileAssocExtensionsText.Text = string.Format(DialogText.Pref_FileAssocExtensions, ExtensionList);
        RefreshFileAssocStatus();
        ShellMenuItemsText.Text = string.Format(DialogText.Pref_ShellMenuItems, ShellMenuItemList, JoinList(ShellMenu.Extensions), FileSystemObjectItemList);
        RefreshShellMenuStatus();
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
        LanguageBox.SelectedIndex = Index(Languages, s.Language);
        ReduceMotionBox.IsChecked = s.ReduceMotion;
        AutoUpdateBox.IsChecked = s.AutoUpdate;
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
        s.Language = Languages[Math.Max(0, LanguageBox.SelectedIndex)].Value;
        s.ReduceMotion = ReduceMotionBox.IsChecked == true;
        s.AutoUpdate = AutoUpdateBox.IsChecked == true;
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
        if (error is not null) MessageDialog.Error(this, DialogText.Pref_SaveFailed_Title, error);
        else _restart = AskRestart(_working.Language);
        DialogResult = true;
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        int answer = MessageDialog.Show(this, DialogKind.Question, DialogText.Pref_ResetConfirm_Title,
            string.Format(DialogText.Pref_ResetConfirmBody, AppSettings.FilePath),
            null, [DialogText.Pref_ResetConfirm_Button, DialogText.Cancel_Button], defaultIndex: 0, cancelIndex: 1);
        if (answer != 0) return;
        var defaults = new AppSettings();
        var error = AppSettings.Save(defaults);
        _apply(defaults);
        if (error is not null) MessageDialog.Error(this, DialogText.Pref_ResetSaveFailed_Title, error);
        else _restart = AskRestart(defaults.Language);
        DialogResult = true;
    }

    /// <summary>
    /// 이 창에서 언어를 바꿔 화면 언어가 달라지면 지금 다시 시작할지 묻는다(설정은 이미 저장됨).
    /// 저장에 실패했으면 묻지 않는다(다시 시작해도 옛 언어로 뜬다).
    /// </summary>
    private bool AskRestart(AppLanguage saved)
    {
        if (saved == _openedLanguage || !UiLanguage.NeedsRestart(saved)) return false;
        int answer = MessageDialog.Show(this, DialogKind.Question, DialogText.Pref_RestartConfirm_Title,
            DialogText.Pref_RestartConfirmBody, null,
            [DialogText.Pref_Restart_Button, DialogText.Pref_RestartLater_Button], defaultIndex: 0, cancelIndex: 1);
        return answer == 0;
    }

    // ───────────────────────── 파일 연결·탐색기 메뉴(즉시 적용) ─────────────────────────

    private static string JoinList(IEnumerable<string> items) => string.Join(", ", items);

    private static bool IsRegistryError(Exception ex) =>
        ex is UnauthorizedAccessException or SecurityException or IOException or ArgumentException;

    /// <summary>
    /// 등록 상태 문장과 단추 활성을 맞춘다(파일 연결·탐색기 메뉴 공통). read 는 실행 파일 경로로 상태를 읽어 (문장, 무언가 등록돼 있는지)를 돌려준다.
    /// 실행 파일 경로를 모르면 등록을 끄고, 등록돼 있지 않으면 해제를 끄며, 상태를 알 수 없으면 해제는 누를 수 있게 둔다.
    /// </summary>
    private static void RefreshRegistrationStatus(TextBlock status, Button register, Button unregister, Func<string, (string Text, bool Registered)> read)
    {
        string? exePath = Environment.ProcessPath;
        register.IsEnabled = exePath is not null;
        if (exePath is null)
        {
            status.Text = DialogText.Pref_FileAssocNoExe;
            unregister.IsEnabled = true;
            return;
        }
        try
        {
            (status.Text, unregister.IsEnabled) = read(exePath);
        }
        catch (Exception ex) when (IsRegistryError(ex))
        {
            status.Text = string.Format(DialogText.Pref_FileAssocReadFailed, ex.Message);
            unregister.IsEnabled = true;
        }
    }

    /// <summary>
    /// 등록하고 탐색기에 알린다. 실패하면 상태를 다시 읽고, 일부만 기록됐을 수 있으므로 해제는 항상 누를 수 있게 두고 알린 뒤 false.
    /// </summary>
    private bool TryRegister(Action register, Action refresh, Button unregister, string failedTitle)
    {
        try
        {
            register();
            FileAssociations.NotifyShell();
            return true;
        }
        catch (Exception ex) when (IsRegistryError(ex))
        {
            refresh();
            unregister.IsEnabled = true;
            MessageDialog.Error(this, failedTitle, string.Format(DialogText.Pref_RegisterPartial, ex.Message));
            return false;
        }
    }

    /// <summary>해제하고 탐색기에 알린다. 실패하면 상태를 다시 읽고 알린 뒤 false.</summary>
    private bool TryUnregister(Action unregister, Action refresh, string failedTitle)
    {
        try
        {
            unregister();
            FileAssociations.NotifyShell();
            return true;
        }
        catch (Exception ex) when (IsRegistryError(ex))
        {
            refresh();
            MessageDialog.Error(this, failedTitle, ex.Message);
            return false;
        }
    }

    /// <summary>알림 창 대신 상태 문장으로 알린 결과를 화면 읽기 프로그램이 읽도록 알린다(LiveSetting=Polite).</summary>
    private static void Announce(TextBlock status) =>
        (UIElementAutomationPeer.FromElement(status) ?? UIElementAutomationPeer.CreatePeerForElement(status))
            ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);

    private static string ExtensionList => JoinList(FileAssociations.Extensions);

    private void RefreshFileAssocStatus() =>
        RefreshRegistrationStatus(FileAssocStatusText, FileAssocRegisterButton, FileAssocUnregisterButton,
            exePath => FileAssociations.GetStatus(Registry.CurrentUser, exePath) switch
            {
                FileAssociationStatus.Registered => (DialogText.Pref_FileAssocRegistered, true),
                FileAssociationStatus.RegisteredElsewhere => (DialogText.Pref_FileAssocRegisteredElsewhere, true),
                _ => (DialogText.Pref_FileAssocNotRegistered, false),
            });

    private void OnFileAssocRegister(object sender, RoutedEventArgs e)
    {
        string? exePath = Environment.ProcessPath;
        if (exePath is null)
        {
            MessageDialog.Error(this, DialogText.Pref_RegisterFailed_Title, DialogText.Pref_ExePathUnknown);
            return;
        }
        int answer = MessageDialog.Show(this, DialogKind.Question, DialogText.Pref_Register_Title,
            string.Format(DialogText.Pref_RegisterBody, ExtensionList, exePath,
                FileAssociations.ProgIdPath, FileAssociations.ApplicationPath, FileAssociations.CapabilitiesPath,
                FileAssociations.RegisteredApplicationsPath, FileAssociations.AppName,
                FileAssociations.ClassesPath, FileAssociations.ProgId),
            null, [DialogText.Pref_Register_Button, DialogText.Cancel_Button], defaultIndex: 0, cancelIndex: 1);
        if (answer != 0) return;
        if (!TryRegister(() => FileAssociations.Register(Registry.CurrentUser, exePath), RefreshFileAssocStatus,
                FileAssocUnregisterButton, DialogText.Pref_RegisterFailed_Title))
            return;
        RefreshFileAssocStatus();
        if (OpenDefaultAppsSettings())
            FileAssocStatusText.Text += " " + DialogText.Pref_RegisterDone;
        Announce(FileAssocStatusText);
    }

    private void OnFileAssocUnregister(object sender, RoutedEventArgs e)
    {
        if (!TryUnregister(() => FileAssociations.Unregister(Registry.CurrentUser), RefreshFileAssocStatus, DialogText.Pref_UnregisterFailed_Title))
            return;
        RefreshFileAssocStatus();
        // 상태 문장이 해제 전과 같을 수 있으므로(실행 파일 경로를 모를 때 등) 결과를 덧붙인다.
        FileAssocStatusText.Text += " " + DialogText.Pref_UnregisterDone;
        Announce(FileAssocStatusText);
    }

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
            MessageDialog.Error(this, DialogText.Pref_OpenSettingsFailed_Title,
                string.Format(DialogText.Pref_OpenSettingsFailedBody, ex.Message));
            return false;
        }
    }

    /// <summary>압축 파일의 탐색기 하위 메뉴에 보일 문구(지금 화면 언어, 표시 순서).</summary>
    private static string ShellMenuItemList => JoinList(ShellMenu.Commands.Select(ShellMenu.Label));

    /// <summary>그 밖의 파일·폴더의 탐색기 하위 메뉴에 보일 문구.</summary>
    private static string FileSystemObjectItemList => JoinList(ShellMenu.FileSystemObjectCommands.Select(ShellMenu.Label));

    private void RefreshShellMenuStatus() =>
        RefreshRegistrationStatus(ShellMenuStatusText, ShellMenuRegisterButton, ShellMenuUnregisterButton,
            exePath => ShellMenu.GetStatus(Registry.CurrentUser, exePath) switch
            {
                ShellMenuStatus.Registered => (DialogText.Pref_ShellMenuRegistered, true),
                ShellMenuStatus.RegisteredElsewhere => (DialogText.Pref_ShellMenuRegisteredElsewhere, true),
                _ => (DialogText.Pref_ShellMenuNotRegistered, false),
            });

    private void OnShellMenuRegister(object sender, RoutedEventArgs e)
    {
        string? exePath = Environment.ProcessPath;
        if (exePath is null)
        {
            MessageDialog.Error(this, DialogText.Pref_ShellMenuRegisterFailed_Title, DialogText.Pref_ExePathUnknown);
            return;
        }
        // 동의하기 전에 기록할 키를 빠짐없이 보인다: 메뉴 키(형식마다) + 명령 처리기 CLSID 키(명령마다).
        string menuKeys = string.Join("\n  ", ShellMenu.MenuPaths.Select(path => @"HKCU\" + path));
        string classKeys = string.Join("\n  ", ShellMenu.ClassIdPaths.Select(path => @"HKCU\" + path));
        int answer = MessageDialog.Show(this, DialogKind.Question, DialogText.Pref_ShellMenuRegister_Title,
            string.Format(DialogText.Pref_ShellMenuRegisterBody, ShellMenuItemList, JoinList(ShellMenu.Extensions), FileSystemObjectItemList,
                exePath, ShellMenu.HandlerPath(exePath), menuKeys, classKeys),
            null, [DialogText.Pref_Register_Button, DialogText.Cancel_Button], defaultIndex: 0, cancelIndex: 1);
        if (answer != 0) return;
        if (!TryRegister(() => ShellMenu.Register(Registry.CurrentUser, exePath), RefreshShellMenuStatus,
                ShellMenuUnregisterButton, DialogText.Pref_ShellMenuRegisterFailed_Title))
            return;
        RefreshShellMenuStatus();
        ShellMenuStatusText.Text += " " + DialogText.Pref_ShellMenuRegisterDone;
        Announce(ShellMenuStatusText);
    }

    private void OnShellMenuUnregister(object sender, RoutedEventArgs e)
    {
        if (!TryUnregister(() => ShellMenu.Unregister(Registry.CurrentUser), RefreshShellMenuStatus, DialogText.Pref_ShellMenuUnregisterFailed_Title))
            return;
        RefreshShellMenuStatus();
        // 상태 문장이 해제 전과 같을 수 있으므로(실행 파일 경로를 모를 때 등) 결과를 덧붙인다.
        ShellMenuStatusText.Text += " " + DialogText.Pref_ShellMenuUnregisterDone;
        Announce(ShellMenuStatusText);
    }

    /// <summary>apply 는 확인·초기화 때만 불린다(취소하면 아무것도 바꾸지 않음).</summary>
    /// <returns>언어를 바꾸고 지금 다시 시작하기로 했으면 true(다시 시작은 호출한 창이 한다).</returns>
    public static bool Show(Window owner, Action<AppSettings> apply)
    {
        var window = new PreferencesWindow(apply) { Owner = owner };
        return window.ShowDialog() == true && window._restart;
    }
}
