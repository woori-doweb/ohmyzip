using System.Globalization;
using System.Windows;
using System.Windows.Markup;

namespace OhMyPic.App.Settings;

/// <summary>
/// Chooses the UI language. Strings live in OhMyPic.App.Text (*.resx English neutral, *.ko.resx Korean) and the
/// resource classes pick them by CurrentUICulture. Decided once at start; a changed setting applies on the next start.
/// Setting values (AppSettings.Language): "system" | "en" | "ko".
/// </summary>
public static class UiLanguage
{
    public const string SystemValue = "system";
    public const string EnglishValue = "en";
    public const string KoreanValue = "ko";

    public static readonly CultureInfo Korean = CultureInfo.GetCultureInfo("ko-KR");
    public static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    /// <summary>Windows display language when the process started (before Apply changed it).</summary>
    private static readonly CultureInfo OsUiCulture = CultureInfo.CurrentUICulture;

    private static bool _xmlLanguageSet;

    /// <summary>UI language of this run (before Apply: chosen from the Windows display language).</summary>
    public static CultureInfo Current { get; private set; } = Resolve(SystemValue, OsUiCulture);

    public static bool IsKorean => Current.TwoLetterISOLanguageName == "ko";

    /// <summary>"ko" → Korean, "en" → English, anything else → Windows display language (ko → Korean, else English).</summary>
    public static CultureInfo Resolve(string? language, CultureInfo osUiCulture) => language?.Trim().ToLowerInvariant() switch
    {
        KoreanValue => Korean,
        EnglishValue => English,
        _ => osUiCulture.TwoLetterISOLanguageName == "ko" ? Korean : English,
    };

    /// <summary>True when the chosen language differs from the language of this run (visible only after a restart).</summary>
    public static bool NeedsRestart(string? chosen) => Resolve(chosen, OsUiCulture).Name != Current.Name;

    /// <summary>
    /// Sets the UI culture of every thread and the xml:lang of WPF elements (font fallback, IME).
    /// Number and date formats (CurrentCulture) keep the Windows regional settings.
    /// </summary>
    public static void Apply(string? language)
    {
        Current = Resolve(language, OsUiCulture);
        CultureInfo.DefaultThreadCurrentUICulture = Current;
        CultureInfo.CurrentUICulture = Current;
        if (_xmlLanguageSet) return;
        // Metadata can be overridden only once per process.
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(Current.IetfLanguageTag)));
        _xmlLanguageSet = true;
    }
}
