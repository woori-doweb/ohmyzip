namespace OhMyZip.App.Theme;

/// <summary>
/// 색 토큰 한 벌. 모든 값은 불투명 #RRGGBB. 대비 계산 시험(OZ-A02)은 <see cref="Tokens.ContrastPairs"/> 를 읽는다.
/// </summary>
public sealed record Palette(
    string Name,
    string Background,     // 파일 목록·대화상자 본문(불투명)
    string Text,           // 본문 글자
    string SecondaryText,  // 보조 글자(12px)
    string DisabledText,   // 비활성 메뉴·버튼 글자. 비활성 UI 구성요소는 WCAG 1.4.3 대비 요구 예외라 ContrastPairs 에 넣지 않는다
    string Chrome,         // 툴바·메뉴·상태줄 표면(단색)
    string Sidebar,        // 사이드바 표면(단색, 약간 색조)
    string Border,         // 구분선(장식용, 글자 아님)
    string Accent,         // 주요 동작(풀기)·포커스
    string AccentHover,    // 주요 동작 마우스 올림
    string OnAccent,       // 강조 배경 위 글자
    string Selection,      // 선택 행 배경
    string OnSelection,    // 선택 행 글자
    string Hover,          // 마우스 올림 배경
    string Error);         // 오류 아이콘·문장(색만으로 전달하지 않음: 아이콘+문장 동반)

public static class Tokens
{
    public static readonly Palette Light = new(
        Name: "Light",
        Background: "#FFFFFF",
        Text: "#1B1B1F",
        SecondaryText: "#5E5E66",
        DisabledText: "#9A9AA2",
        Chrome: "#F3F4F6",
        Sidebar: "#EEF0F3",
        Border: "#D4D6DB",
        Accent: "#005FB8",
        AccentHover: "#004C93",
        OnAccent: "#FFFFFF",
        Selection: "#CFE3F7",
        OnSelection: "#1B1B1F",
        Hover: "#E6E8EC",
        Error: "#C42B1C");

    public static readonly Palette Dark = new(
        Name: "Dark",
        Background: "#1C1C1E",
        Text: "#F2F2F5",
        SecondaryText: "#A9A9B2",
        DisabledText: "#6E6E76",
        Chrome: "#2A2A2D",
        Sidebar: "#252528",
        Border: "#3E3E43",
        Accent: "#4CB8F5",
        AccentHover: "#7ACBF8",
        OnAccent: "#0B0B0C",
        Selection: "#0F4C75",
        OnSelection: "#F2F2F5",
        Hover: "#36363A",
        Error: "#FF9C94");

    /// <summary>
    /// 화면에서 글자·글리프로 쓰이는 (전경, 배경) 조합. 각각 4.5:1 이상이어야 한다.
    /// DisabledText 는 비활성 구성요소라 WCAG 대비 요구 예외이므로 넣지 않는다.
    /// </summary>
    public static IEnumerable<(string Label, string Foreground, string Background)> ContrastPairs(Palette p)
    {
        yield return ("Text/Background", p.Text, p.Background);
        yield return ("SecondaryText/Background", p.SecondaryText, p.Background); // 열 머리글, 검색 안내 문구(입력란 배경)
        yield return ("Text/Chrome", p.Text, p.Chrome);
        yield return ("SecondaryText/Chrome", p.SecondaryText, p.Chrome);
        yield return ("Text/Sidebar", p.Text, p.Sidebar);
        yield return ("SecondaryText/Sidebar", p.SecondaryText, p.Sidebar);
        yield return ("Text/Hover", p.Text, p.Hover);
        yield return ("SecondaryText/Hover", p.SecondaryText, p.Hover);
        yield return ("OnAccent/Accent", p.OnAccent, p.Accent);
        yield return ("OnAccent/AccentHover", p.OnAccent, p.AccentHover);
        yield return ("OnSelection/Selection", p.OnSelection, p.Selection);
        yield return ("Error/Background", p.Error, p.Background);
        yield return ("Accent/Background", p.Accent, p.Background); // 키보드 포커스 테두리·진행 막대
        yield return ("Accent/Chrome", p.Accent, p.Chrome);
    }

    /// <summary>WCAG 2.x 대비율.</summary>
    public static double ContrastRatio(string foregroundHex, string backgroundHex)
    {
        double a = RelativeLuminance(foregroundHex), b = RelativeLuminance(backgroundHex);
        double hi = Math.Max(a, b), lo = Math.Min(a, b);
        return (hi + 0.05) / (lo + 0.05);
    }

    public static double RelativeLuminance(string hex)
    {
        var (r, g, b) = Parse(hex);
        static double Lin(byte c)
        {
            double s = c / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Lin(r) + 0.7152 * Lin(g) + 0.0722 * Lin(b);
    }

    public static (byte R, byte G, byte B) Parse(string hex)
    {
        var h = hex.TrimStart('#');
        if (h.Length != 6) throw new FormatException(string.Format(OhMyZip.App.Text.MainText.Token_InvalidHex, hex));
        return (Convert.ToByte(h[..2], 16), Convert.ToByte(h[2..4], 16), Convert.ToByte(h[4..6], 16));
    }
}
