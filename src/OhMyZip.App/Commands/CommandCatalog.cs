using System.Windows.Input;

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
    public const string PendingSuffix = " (준비 중)";

    /// <summary>최상위 메뉴 7개(표시 순서). Name 은 MenuSpec.Menu 와 대응.</summary>
    public static readonly IReadOnlyList<(string Name, string Header)> Menus =
    [
        ("파일", "파일(_F)"),
        ("편집", "편집(_E)"),
        ("찾기", "찾기(_I)"),
        ("설정", "설정(_S)"),
        ("보기", "보기(_V)"),
        ("도구", "도구(_T)"),
        ("도움말", "도움말(_A)"),
    ];

    /// <summary>메뉴 명령 58개(OZ-C001~C058), 표시 순서.</summary>
    public static readonly IReadOnlyList<MenuSpec> Items =
    [
        new("OZ-C001", "파일", null, "압축 파일 열기", "Ctrl+O", "archive.open"),
        new("OZ-C002", "파일", null, "새로 압축", "Ctrl+N", "archive.create"),
        new("OZ-C003", "파일", null, "압축 파일 닫기", "F4", "archive.close"),
        new("OZ-C004", "파일", null, "다른 이름으로 저장", null, "archive.save-as"),
        new("OZ-C005", "파일", null, "압축 파일 삭제", "Ctrl+Del", "archive.delete-file"),
        new("OZ-C006", "파일", null, "압축 파일 완전 삭제", "Shift+Ctrl+Del", "archive.shred"),
        new("OZ-C007", "파일", null, "압축 파일이 있는 폴더 열기", null, "archive.reveal"),
        new("OZ-C008", "파일", null, "압축 파일 테스트", "Ctrl+T", "archive.test"),
        new("OZ-C009", "파일", null, "압축 파일 악성코드 검사", "Ctrl+I", "archive.scan"),
        new("OZ-C010", "파일", null, "압축 풀기", "Ctrl+E", "archive.extract"),
        new("OZ-C011", "파일", null, "새창 띄우기", "Ctrl+Shift+N", "app.new-window"),
        new("OZ-C012", "파일", null, "새창 띄우기(관리자 권한)", "Ctrl+Alt+N", "app.new-window-admin"),
        new("OZ-C013", "파일", null, "종료", "Ctrl+W", "app.exit"),
        new("OZ-C014", "편집", null, "이름 바꾸기", "F2", "entry.rename"),
        new("OZ-C015", "편집", null, "파일 추가", "Ins", "entry.add"),
        new("OZ-C016", "편집", null, "파일 삭제", "Del", "entry.delete"),
        new("OZ-C017", "편집", null, "압축 파일 설명 편집", "Ctrl+Shift+M", "archive.comment"),
        new("OZ-C018", "편집", null, "클립보드로 파일 복사하기", "Ctrl+C", "entry.copy"),
        new("OZ-C019", "편집", null, "클립보드에서 파일 붙여 넣기", "Ctrl+V", "entry.paste"),
        new("OZ-C020", "편집", null, "파일의 경로명을 클립보드로 복사", null, "entry.copy-path"),
        new("OZ-C021", "찾기", null, "파일 찾기", "Ctrl+F", "view.find"),
        new("OZ-C022", "설정", null, "환경 설정(F5)", null, "app.preferences"),
        new("OZ-C023", "설정", null, "제품 등록...", null, "app.register"),
        new("OZ-C024", "설정", null, "제품 등록 삭제...", null, "app.unregister"),
        new("OZ-C025", "설정", null, "암호 관리자...", null, "app.passwords"),
        new("OZ-C026", "보기", null, "시작 화면", "F6", "view.start"),
        new("OZ-C027", "보기", null, "트리 컨트롤", "F7", "view.sidebar"),
        new("OZ-C028", "보기", null, "압축 파일 설명", "F8", "view.comment"),
        new("OZ-C029", "보기", null, "이미지 파일 미리 보기 사용", null, "view.preview"),
        new("OZ-C030", "보기", null, "상태 표시줄", null, "view.statusbar"),
        new("OZ-C031", "보기", "툴바", "크게", null, "view.toolbar"),
        new("OZ-C032", "보기", "툴바", "작게", null, "view.toolbar"),
        new("OZ-C033", "보기", "툴바", "구식", null, "view.toolbar"),
        new("OZ-C034", "보기", "툴바", "숨기기", null, "view.toolbar"),
        new("OZ-C035", "보기", "툴바", "툴바 버튼 설정", null, "view.toolbar"),
        new("OZ-C036", "보기", "파일 목록", "자세히", null, "view.listmode"),
        new("OZ-C037", "보기", "파일 목록", "간단히", null, "view.listmode"),
        new("OZ-C038", "보기", "파일 목록", "아이콘", null, "view.listmode"),
        new("OZ-C039", "보기", "파일 목록", "큰 아이콘", null, "view.listmode"),
        new("OZ-C040", "보기", "칼럼 설정", "압축 크기", null, "view.columns"),
        new("OZ-C041", "보기", "칼럼 설정", "원본 크기", null, "view.columns"),
        new("OZ-C042", "보기", "칼럼 설정", "파일 종류", null, "view.columns"),
        new("OZ-C043", "보기", "칼럼 설정", "수정한 날짜", null, "view.columns"),
        new("OZ-C044", "보기", "칼럼 설정", "압축 방식", null, "view.columns"),
        new("OZ-C045", "보기", "칼럼 설정", "암호 방식", null, "view.columns"),
        new("OZ-C046", "보기", "칼럼 설정", "CRC", null, "view.columns"),
        new("OZ-C047", "보기", "칼럼 설정", "속성", null, "view.columns"),
        new("OZ-C048", "보기", "칼럼 설정", "파일 설명", null, "view.columns"),
        new("OZ-C049", "보기", "칼럼 설정", "부가 정보", null, "view.columns"),
        new("OZ-C050", "보기", null, "항상 위에 표시", null, "view.topmost"),
        new("OZ-C051", "보기", null, "색상 설정...", null, "view.theme"),
        new("OZ-C052", "도구", null, "손상된 압축 파일 복구...", null, "tools.repair"),
        new("OZ-C053", "도구", null, "압축 파일 암호 복구...", null, "tools.recover-password"),
        new("OZ-C054", "도움말", null, "정보", "F1", "help.about"),
        new("OZ-C055", "도움말", null, "프로그램 설치 폴더 열기", null, "help.install-folder"),
        new("OZ-C056", "도움말", null, "도움말 페이지 열기", null, "help.docs"),
        new("OZ-C057", "도움말", null, "토론 게시판 열기", null, "help.forum"),
        new("OZ-C058", "도움말", null, "홈페이지 열기", null, "help.home"),
    ];

    /// <summary>
    /// 메뉴 명령 58개(OZ-C 행) 밖에서 기능 행(OZ-F0xx)이 명령을 요구하는 항목. AfterKey 메뉴 항목 바로 뒤에 둔다.
    /// OZ-F027 알아서 풀기(ui_location: 풀기 드롭다운) → 파일 메뉴 + 툴바 풀기 드롭다운.
    /// </summary>
    public static readonly IReadOnlyList<(string AfterKey, MenuSpec Spec)> FeatureItems =
    [
        ("archive.extract", new("OZ-F027", "파일", null, "알아서 풀기", null, "archive.extract.smart")),
    ];

    /// <summary>메뉴 문구에 이미 들어 있어 Shortcut 칸을 비워 둔 단축키("환경 설정(F5)").</summary>
    public static readonly IReadOnlyDictionary<string, string> ExtraGestures = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["app.preferences"] = "F5",
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
        "help.about", "help.install-folder", "help.docs",
    };

    /// <summary>툴바 3묶음(D02). 모든 키는 <see cref="Items"/> 에 있어야 한다(D01, OZ-A06).</summary>
    public static readonly IReadOnlyList<IReadOnlyList<ToolbarSpec>> ToolbarGroups =
    [
        [new("archive.open", "열기", "\uE8E5"), new("archive.create", "새 압축", "\uE7B8")],
        [new("archive.extract", "풀기", "\uE896", IsPrimary: true), new("entry.add", "추가", "\uE710"), new("entry.delete", "삭제", "\uE74D")],
        [new("archive.test", "테스트", "\uE73E")],
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
        Routed.TryGetValue(key, out var cmd) ? cmd : throw new KeyNotFoundException($"구현되지 않은 명령: {key}");

    /// <summary>키의 첫 메뉴 항목(헤더·단축키).</summary>
    public static MenuSpec Spec(string key) => Items.Concat(FeatureItems.Select(f => f.Spec)).First(i => i.Key == key);

    /// <summary>명령의 단축키(Shortcut 또는 <see cref="ExtraGestures"/>).</summary>
    public static string? Gesture(string key) => Spec(key).Shortcut ?? ExtraGestures.GetValueOrDefault(key);

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
