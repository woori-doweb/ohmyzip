using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace OhMyPic.App.Theme;

/// <summary>
/// Reads the Windows app theme (light/dark) and high contrast at start and fills the brush resources.
/// Every screen references these keys through DynamicResource, so a system change only needs <see cref="Reapply"/>.
/// </summary>
public static class ThemeManager
{
    public const string Background = "Op.Background";
    public const string Text = "Op.Text";
    public const string SecondaryText = "Op.SecondaryText";
    public const string DisabledText = "Op.DisabledText";
    public const string Chrome = "Op.Chrome";
    public const string ChromeText = "Op.ChromeText";
    public const string Sidebar = "Op.Sidebar";
    public const string Border = "Op.Border";
    public const string Accent = "Op.Accent";
    public const string AccentHover = "Op.AccentHover";
    public const string OnAccent = "Op.OnAccent";
    public const string Selection = "Op.Selection";
    public const string OnSelection = "Op.OnSelection";
    public const string Hover = "Op.Hover";
    public const string OnHover = "Op.OnHover";
    public const string Error = "Op.Error";
    public const string Focus = "Op.Focus";

    public static bool IsDark { get; private set; }
    public static bool IsHighContrast { get; private set; }

    /// <summary>Raised after the brushes were refilled (theme or high contrast changed).</summary>
    public static event EventHandler? Changed;

    public static bool AnimationsEnabled => SystemParameters.ClientAreaAnimation;

    /// <summary>Refill the brushes and match the title bars of every open window.</summary>
    public static void Reapply(Application app)
    {
        Apply(app);
        foreach (Window w in app.Windows) NativeMethods.ApplyDarkTitleBar(w);
        Changed?.Invoke(null, EventArgs.Empty);
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
            // High contrast: system colours only, nothing translucent, no forced dark title bar.
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
            // Remove the selection overrides so controls use the high-contrast system colours.
            r.Remove(SystemColors.HighlightBrushKey);
            r.Remove(SystemColors.HighlightTextBrushKey);
            r.Remove(SystemColors.InactiveSelectionHighlightBrushKey);
            r.Remove(SystemColors.InactiveSelectionHighlightTextBrushKey);
            return;
        }

        IsDark = !AppsUseLightTheme();
        var p = IsDark ? Tokens.Dark : Tokens.Light;
        r[Background] = Brush(p.Background);
        r[Text] = Brush(p.Text);
        r[SecondaryText] = Brush(p.SecondaryText);
        r[DisabledText] = Brush(p.DisabledText);
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
        // Tree and list default selection colours follow the token selection colour.
        r[SystemColors.HighlightBrushKey] = r[Selection];
        r[SystemColors.HighlightTextBrushKey] = r[OnSelection];
        r[SystemColors.InactiveSelectionHighlightBrushKey] = r[Selection];
        r[SystemColors.InactiveSelectionHighlightTextBrushKey] = r[OnSelection];
    }

    /// <summary>HKCU\...\Themes\Personalize AppsUseLightTheme. Light when the value is missing.</summary>
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

    public static SolidColorBrush Brush(string hex)
    {
        var (r, g, b) = Tokens.Parse(hex);
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
