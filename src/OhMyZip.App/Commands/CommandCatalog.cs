using System.Windows.Input;
using OhMyZip.App.Text;

namespace OhMyZip.App.Commands;

/// <summary>
/// 메뉴 한 항목(traceability.json 의 kind=menu_command 행 하나).
/// Submenu 가 있으면 Menu &gt; Submenu &gt; Header 위치. Key 는 ohmyzip_command.
/// </summary>
public sealed record MenuSpec(string Id, string Menu, string? Submenu, string Header, string? Shortcut, string Key);

/// <summary>
/// 명령 표 하나. 메뉴·툴바·단축키는 모두 여기서 만든다(OZ-A06: 툴바 명령 ⊆ 메뉴 명령).
/// 구현 여부는 <see cref="Implemented"/> 한 곳에서만 정한다. 미구현은 메뉴에 비활성 + " (준비 중)".
/// </summary>
public static class CommandCatalog
{
    public static readonly string PendingSuffix = MenuText.PendingSuffix;

    /// <summary>최상위 메뉴 이름(화면에 보이지 않는 식별자). MenuSpec.Menu 는 이 값 중 하나.</summary>
    private static class MenuIds
    {
        public const string File = "file";
        public const string Edit = "edit";
        public const string Find = "find";
        public const string Settings = "settings";
        public const string View = "view";
        public const string Tools = "tools";
        public const string Help = "help";
    }

    /// <summary>최상위 메뉴 7개(표시 순서). Name 은 MenuSpec.Menu 와 대응, Header 는 화면 문구(접근 키 포함).</summary>
    public static readonly IReadOnlyList<(string Name, string Header)> Menus =
    [
        (MenuIds.File, MenuText.Menu_File),
        (MenuIds.Edit, MenuText.Menu_Edit),
        (MenuIds.Find, MenuText.Menu_Find),
        (MenuIds.Settings, MenuText.Menu_Settings),
        (MenuIds.View, MenuText.Menu_View),
        (MenuIds.Tools, MenuText.Menu_Tools),
        (MenuIds.Help, MenuText.Menu_Help),
    ];

    /// <summary>메뉴 명령 58개(OZ-C001~C058), 표시 순서.</summary>
    public static readonly IReadOnlyList<MenuSpec> Items =
    [
        new("OZ-C001", MenuIds.File, null, MenuText.Label_OpenArchive, "Ctrl+O", "archive.open"),
        new("OZ-C002", MenuIds.File, null, MenuText.Label_NewArchive, "Ctrl+N", "archive.create"),
        new("OZ-C003", MenuIds.File, null, MenuText.Label_CloseArchive, "F4", "archive.close"),
        new("OZ-C004", MenuIds.File, null, MenuText.Label_SaveAs, null, "archive.save-as"),
        new("OZ-C005", MenuIds.File, null, MenuText.Label_DeleteArchive, "Ctrl+Del", "archive.delete-file"),
        new("OZ-C006", MenuIds.File, null, MenuText.Label_SecureDeleteArchive, "Shift+Ctrl+Del", "archive.shred"),
        new("OZ-C007", MenuIds.File, null, MenuText.Label_OpenContainingFolder, null, "archive.reveal"),
        new("OZ-C008", MenuIds.File, null, MenuText.Label_TestArchive, "Ctrl+T", "archive.test"),
        new("OZ-C009", MenuIds.File, null, MenuText.Label_ScanForMalware, "Ctrl+I", "archive.scan"),
        new("OZ-C010", MenuIds.File, null, MenuText.Label_Extract, "Ctrl+E", "archive.extract"),
        new("OZ-C011", MenuIds.File, null, MenuText.Label_NewWindow, "Ctrl+Shift+N", "app.new-window"),
        new("OZ-C012", MenuIds.File, null, MenuText.Label_NewWindowAdmin, "Ctrl+Alt+N", "app.new-window-admin"),
        new("OZ-C013", MenuIds.File, null, MenuText.Label_Exit, "Ctrl+W", "app.exit"),
        new("OZ-C014", MenuIds.Edit, null, MenuText.Label_Rename, "F2", "entry.rename"),
        new("OZ-C015", MenuIds.Edit, null, MenuText.Label_AddFiles, "Ins", "entry.add"),
        new("OZ-C016", MenuIds.Edit, null, MenuText.Label_DeleteFiles, "Del", "entry.delete"),
        new("OZ-C017", MenuIds.Edit, null, MenuText.Label_EditComment, "Ctrl+Shift+M", "archive.comment"),
        new("OZ-C018", MenuIds.Edit, null, MenuText.Label_CopyToClipboard, "Ctrl+C", "entry.copy"),
        new("OZ-C019", MenuIds.Edit, null, MenuText.Label_PasteFromClipboard, "Ctrl+V", "entry.paste"),
        new("OZ-C020", MenuIds.Edit, null, MenuText.Label_CopyPath, null, "entry.copy-path"),
        new("OZ-C021", MenuIds.Find, null, MenuText.Label_FindFiles, "Ctrl+F", "view.find"),
        new("OZ-C022", MenuIds.Settings, null, MenuText.Label_Settings, "F5", "app.preferences"),
        new("OZ-C023", MenuIds.Settings, null, MenuText.Label_Register, null, "app.register"),
        new("OZ-C024", MenuIds.Settings, null, MenuText.Label_Unregister, null, "app.unregister"),
        new("OZ-C025", MenuIds.Settings, null, MenuText.Label_PasswordManager, null, "app.passwords"),
        new("OZ-C026", MenuIds.View, null, MenuText.Label_StartScreen, "F6", "view.start"),
        new("OZ-C027", MenuIds.View, null, MenuText.Label_FolderTree, "F7", "view.sidebar"),
        new("OZ-C028", MenuIds.View, null, MenuText.Label_ArchiveComment, "F8", "view.comment"),
        new("OZ-C029", MenuIds.View, null, MenuText.Label_ImagePreview, null, "view.preview"),
        new("OZ-C030", MenuIds.View, null, MenuText.Label_StatusBar, null, "view.statusbar"),
        new("OZ-C031", MenuIds.View, MenuText.Submenu_Toolbar, MenuText.Label_ToolbarLarge, null, "view.toolbar"),
        new("OZ-C032", MenuIds.View, MenuText.Submenu_Toolbar, MenuText.Label_ToolbarSmall, null, "view.toolbar"),
        new("OZ-C033", MenuIds.View, MenuText.Submenu_Toolbar, MenuText.Label_ToolbarClassic, null, "view.toolbar"),
        new("OZ-C034", MenuIds.View, MenuText.Submenu_Toolbar, MenuText.Label_ToolbarHide, null, "view.toolbar"),
        new("OZ-C035", MenuIds.View, MenuText.Submenu_Toolbar, MenuText.Label_ToolbarButtons, null, "view.toolbar"),
        new("OZ-C036", MenuIds.View, MenuText.Submenu_FileList, MenuText.Label_ListDetails, null, "view.listmode"),
        new("OZ-C037", MenuIds.View, MenuText.Submenu_FileList, MenuText.Label_ListSimple, null, "view.listmode"),
        new("OZ-C038", MenuIds.View, MenuText.Submenu_FileList, MenuText.Label_ListIcons, null, "view.listmode"),
        new("OZ-C039", MenuIds.View, MenuText.Submenu_FileList, MenuText.Label_ListLargeIcons, null, "view.listmode"),
        new("OZ-C040", MenuIds.View, MenuText.Submenu_Columns, MenuText.Label_ColumnPackedSize, null, "view.columns"),
        new("OZ-C041", MenuIds.View, MenuText.Submenu_Columns, MenuText.Label_ColumnSize, null, "view.columns"),
        new("OZ-C042", MenuIds.View, MenuText.Submenu_Columns, MenuText.Label_ColumnType, null, "view.columns"),
        new("OZ-C043", MenuIds.View, MenuText.Submenu_Columns, MenuText.Label_ColumnModified, null, "view.columns"),
        new("OZ-C044", MenuIds.View, MenuText.Submenu_Columns, MenuText.Label_ColumnMethod, null, "view.columns"),
        new("OZ-C045", MenuIds.View, MenuText.Submenu_Columns, MenuText.Label_ColumnEncryption, null, "view.columns"),
        new("OZ-C046", MenuIds.View, MenuText.Submenu_Columns, "CRC", null, "view.columns"),
        new("OZ-C047", MenuIds.View, MenuText.Submenu_Columns, MenuText.Label_ColumnAttributes, null, "view.columns"),
        new("OZ-C048", MenuIds.View, MenuText.Submenu_Columns, MenuText.Label_ColumnDescription, null, "view.columns"),
        new("OZ-C049", MenuIds.View, MenuText.Submenu_Columns, MenuText.Label_ColumnExtraInfo, null, "view.columns"),
        new("OZ-C050", MenuIds.View, null, MenuText.Label_AlwaysOnTop, null, "view.topmost"),
        new("OZ-C051", MenuIds.View, null, MenuText.Label_ColorTheme, null, "view.theme"),
        new("OZ-C052", MenuIds.Tools, null, MenuText.Label_RepairArchive, null, "tools.repair"),
        new("OZ-C053", MenuIds.Tools, null, MenuText.Label_RecoverPassword, null, "tools.recover-password"),
        new("OZ-C054", MenuIds.Help, null, MenuText.Label_About, "F1", "help.about"),
        new("OZ-C055", MenuIds.Help, null, MenuText.Label_OpenInstallFolder, null, "help.install-folder"),
        new("OZ-C056", MenuIds.Help, null, MenuText.Label_OpenHelp, null, "help.docs"),
        new("OZ-C057", MenuIds.Help, null, MenuText.Label_OpenForum, null, "help.forum"),
        new("OZ-C058", MenuIds.Help, null, MenuText.Label_OpenHomepage, null, "help.home"),
    ];

    /// <summary>
    /// 메뉴 명령 58개(OZ-C 행) 밖에서 기능 행(OZ-F0xx)이 명령을 요구하는 항목과 원본 메뉴에 없는 앱 고유 항목(OZ-App*).
    /// AfterKey 메뉴 항목 바로 뒤에 둔다.
    /// OZ-F027 알아서 풀기(ui_location: 풀기 드롭다운) → 파일 메뉴 + 툴바 풀기 드롭다운.
    /// OZ-AppUpdate 업데이트 확인 → 도움말 메뉴 정보 뒤(같은 묶음).
    /// </summary>
    public static readonly IReadOnlyList<(string AfterKey, MenuSpec Spec)> FeatureItems =
    [
        ("archive.extract", new("OZ-F027", MenuIds.File, null, MenuText.Label_SmartExtract, null, "archive.extract.smart")),
        ("help.about", new("OZ-AppUpdate", MenuIds.Help, null, MenuText.Label_CheckForUpdates, null, "app.update")),
    ];

    /// <summary>
    /// 이 행 앞에 메뉴 구분선(같은 메뉴·하위 메뉴 안의 묶음 경계). 하위 메뉴 첫 행이면 그 하위 메뉴 머리 앞에 둔다.
    /// </summary>
    public static readonly IReadOnlySet<string> SeparatorBefore = new HashSet<string>(StringComparer.Ordinal)
    {
        "OZ-C003", "OZ-C008", "OZ-C010", "OZ-C011", "OZ-C013",
        "OZ-C017", "OZ-C018",
        "OZ-C023", "OZ-C025",
        "OZ-C031", "OZ-C035", "OZ-C050",
        "OZ-C055", "OZ-C057",
    };

    /// <summary>실제로 동작하는 명령(ohmyzip_command 키). 이 집합 밖은 메뉴에서 비활성. 활성 조건은 MainWindow.CanRun 한 곳.</summary>
    public static readonly IReadOnlySet<string> Implemented = new HashSet<string>(StringComparer.Ordinal)
    {
        "archive.open", "archive.create", "archive.close", "archive.save-as", "archive.delete-file", "archive.reveal",
        "archive.test", "archive.extract", "archive.extract.smart", "archive.comment",
        "app.new-window", "app.exit", "app.preferences",
        "entry.rename", "entry.add", "entry.delete", "entry.copy", "entry.paste", "entry.copy-path",
        "view.find", "view.start", "view.sidebar", "view.comment", "view.statusbar", "view.toolbar", "view.listmode",
        "view.columns", "view.topmost", "view.theme",
        "help.about", "help.install-folder", "help.docs", "app.update",
    };

    /// <summary>툴바 3묶음. 모든 키는 <see cref="Items"/> 에 있어야 한다(OZ-A06).</summary>
    public static readonly IReadOnlyList<IReadOnlyList<ToolbarSpec>> ToolbarGroups =
    [
        [new("archive.open", MenuText.Toolbar_Open, "\uE8E5"), new("archive.create", MenuText.Toolbar_NewArchive, "\uE7B8")],
        [new("archive.extract", MenuText.Toolbar_Extract, "\uE896", IsPrimary: true), new("entry.add", MenuText.Toolbar_Add, "\uE710"), new("entry.delete", MenuText.Toolbar_Delete, "\uE74D")],
        [new("archive.test", MenuText.Toolbar_Test, "\uE73E")],
    ];

    private static readonly Dictionary<string, RoutedUICommand> Routed = BuildRouted();

    private static Dictionary<string, RoutedUICommand> BuildRouted()
    {
        var map = new Dictionary<string, RoutedUICommand>(StringComparer.Ordinal);
        foreach (var item in Items.Concat(FeatureItems.Select(f => f.Spec)))
        {
            if (!Implemented.Contains(item.Key) || map.ContainsKey(item.Key)) continue;
            map[item.Key] = new RoutedUICommand(item.Header, item.Key, typeof(CommandCatalog));
        }
        return map;
    }

    public static bool IsImplemented(string key) => Implemented.Contains(key);

    /// <summary>구현된 명령의 RoutedUICommand. 미구현 키는 예외.</summary>
    public static RoutedUICommand Command(string key) =>
        Routed.TryGetValue(key, out var cmd) ? cmd : throw new KeyNotFoundException(string.Format(MenuText.Error_CommandNotImplemented, key));

    /// <summary>키의 첫 메뉴 항목(헤더·단축키).</summary>
    public static MenuSpec Spec(string key) => Items.Concat(FeatureItems.Select(f => f.Spec)).First(i => i.Key == key);

    /// <summary>명령의 단축키.</summary>
    public static string? Gesture(string key) => Spec(key).Shortcut;

    /// <summary>"Ctrl+Shift+N", "Ins", "Del", "F2" 같은 단축키 표기를 KeyGesture 로.</summary>
    public static KeyGesture ParseGesture(string text)
    {
        var mods = ModifierKeys.None;
        Key key = Key.None;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part)
            {
                case "Ctrl": mods |= ModifierKeys.Control; break;
                case "Shift": mods |= ModifierKeys.Shift; break;
                case "Alt": mods |= ModifierKeys.Alt; break;
                case "Ins": key = Key.Insert; break;
                case "Del": key = Key.Delete; break;
                default: key = (Key)Enum.Parse(typeof(Key), part, ignoreCase: true); break;
            }
        }
        return new KeyGesture(key, mods, text);
    }
}

public sealed record ToolbarSpec(string Key, string Label, string Glyph, bool IsPrimary = false);
