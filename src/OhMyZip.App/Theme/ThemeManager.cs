using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using OhMyZip.App.Settings;

namespace OhMyZip.App.Theme;

/// <summary>
/// 시작 시 Windows 앱 테마(라이트/다크)·고대비를 읽어 브러시 자원을 채운다.
/// 모든 화면은 DynamicResource 로 이 키들을 참조하므로 설정이 바뀌면 Reapply 로 브러시와 제목 표시줄을 다시 맞춘다.
/// </summary>
public static class ThemeManager
{
    public const string Background = "Oz.Background";
    public const string Text = "Oz.Text";
    public const string SecondaryText = "Oz.SecondaryText";
    public const string DisabledText = "Oz.DisabledText";
    public const string Chrome = "Oz.Chrome";
    public const string ChromeText = "Oz.ChromeText";
    public const string Sidebar = "Oz.Sidebar";
    public const string Border = "Oz.Border";
    public const string Accent = "Oz.Accent";
    public const string AccentHover = "Oz.AccentHover";
    public const string OnAccent = "Oz.OnAccent";
    public const string Selection = "Oz.Selection";
    public const string OnSelection = "Oz.OnSelection";
    public const string Hover = "Oz.Hover";
    public const string OnHover = "Oz.OnHover";
    public const string Error = "Oz.Error";
    public const string Focus = "Oz.Focus";

    public static bool IsDark { get; private set; }
    public static bool IsHighContrast { get; private set; }

    /// <summary>앱 내부 테마 재정의(환경 설정·색상 설정). 고대비가 켜져 있으면 항상 시스템 색이 이긴다.</summary>
    public static AppTheme Override { get; set; } = AppTheme.System;

    /// <summary>앱 내부 '동작 줄이기'. Windows 애니메이션 끔과 함께 움직이는 표시를 쓰지 않는다.</summary>
    public static bool ReduceMotion { get; set; }

    public static bool AnimationsEnabled => SystemParameters.ClientAreaAnimation && !ReduceMotion;

    /// <summary>설정을 바꾼 뒤: 브러시를 다시 채우고 열린 창의 제목 표시줄도 맞춘다.</summary>
    public static void Reapply(Application app)
    {
        Apply(app);
        foreach (Window w in app.Windows) NativeMethods.ApplyDarkTitleBar(w);
    }

    public static void Initialize(Application app)
    {
        Apply(app);
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.HighContrast))
                app.Dispatcher.BeginInvoke(() => Reapply(app));
        };
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
                app.Dispatcher.BeginInvoke(() => Reapply(app));
        };
    }

    public static void Apply(Application app)
    {
        var r = app.Resources;
        IsHighContrast = SystemParameters.HighContrast;
        if (IsHighContrast)
        {
            // 고대비: 시스템 색만, 반투명 없음. 앱 다크 제목 표시줄도 강제하지 않는다.
            IsDark = false;
            r[Background] = SystemColors.WindowBrush;
            r[Text] = SystemColors.WindowTextBrush;
            r[SecondaryText] = SystemColors.WindowTextBrush;
            r[DisabledText] = SystemColors.GrayTextBrush;
            r[Chrome] = SystemColors.ControlBrush;
            r[ChromeText] = SystemColors.ControlTextBrush;
            r[Sidebar] = SystemColors.WindowBrush;
            r[Border] = SystemColors.WindowTextBrush;
            r[Accent] = SystemColors.HighlightBrush;
            r[AccentHover] = SystemColors.HighlightBrush;
            r[OnAccent] = SystemColors.HighlightTextBrush;
            r[Selection] = SystemColors.HighlightBrush;
            r[OnSelection] = SystemColors.HighlightTextBrush;
            r[Hover] = SystemColors.HighlightBrush;
            r[OnHover] = SystemColors.HighlightTextBrush;
            r[Error] = SystemColors.WindowTextBrush;
            r[Focus] = SystemColors.WindowTextBrush;
            // 시스템 선택색 재정의를 지워 컨트롤이 고대비 시스템 색을 쓰게 한다.
            r.Remove(SystemColors.HighlightBrushKey);
            r.Remove(SystemColors.HighlightTextBrushKey);
            r.Remove(SystemColors.InactiveSelectionHighlightBrushKey);
            r.Remove(SystemColors.InactiveSelectionHighlightTextBrushKey);
            return;
        }

        IsDark = Override switch
        {
            AppTheme.Light => false,
            AppTheme.Dark => true,
            _ => !AppsUseLightTheme(),
        };
        var p = IsDark ? Tokens.Dark : Tokens.Light;
        r[Background] = Brush(p.Background);
        r[Text] = Brush(p.Text);
        r[SecondaryText] = Brush(p.SecondaryText);
        r[DisabledText] = Brush(p.DisabledText); // 비활성 글자(대비 요구 예외)
        r[Chrome] = Brush(p.Chrome);
        r[ChromeText] = Brush(p.Text);
        r[Sidebar] = Brush(p.Sidebar);
        r[Border] = Brush(p.Border);
        r[Accent] = Brush(p.Accent);
        r[AccentHover] = Brush(p.AccentHover);
        r[OnAccent] = Brush(p.OnAccent);
        r[Selection] = Brush(p.Selection);
        r[OnSelection] = Brush(p.OnSelection);
        r[Hover] = Brush(p.Hover);
        r[OnHover] = Brush(p.Text);
        r[Error] = Brush(p.Error);
        r[Focus] = Brush(p.Accent);
        // 트리·목록의 기본 선택색을 토큰 선택색으로(강조색 계열 한 가지).
        r[SystemColors.HighlightBrushKey] = r[Selection];
        r[SystemColors.HighlightTextBrushKey] = r[OnSelection];
        r[SystemColors.InactiveSelectionHighlightBrushKey] = r[Selection];
        r[SystemColors.InactiveSelectionHighlightTextBrushKey] = r[OnSelection];
    }

    /// <summary>HKCU\...\Themes\Personalize AppsUseLightTheme. 값이 없으면 라이트.</summary>
    public static bool AppsUseLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int v || v != 0;
        }
        catch
        {
            return true;
        }
    }

    private static SolidColorBrush Brush(string hex)
    {
        var (r, g, b) = Tokens.Parse(hex);
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
