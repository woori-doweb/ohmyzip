using System.Globalization;
using System.Windows;
using System.Windows.Markup;

namespace OhMyZip.App.Settings;

/// <summary>
/// 화면 언어를 정한다. 문구는 Resources/*.resx(영어, 기본)와 *.ko.resx(한국어)에 있고,
/// 리소스 클래스는 CurrentUICulture 로 고른다. 시작할 때 한 번 정하며, 바꾼 설정은 다시 시작해야 적용된다.
/// </summary>
public static class UiLanguage
{
    public static readonly CultureInfo Korean = CultureInfo.GetCultureInfo("ko-KR");
    public static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    /// <summary>프로세스가 시작할 때의 Windows 표시 언어(Apply 가 바꾸기 전 값).</summary>
    private static readonly CultureInfo OsUiCulture = CultureInfo.CurrentUICulture;

    private static bool _xmlLanguageSet;

    /// <summary>이번 실행의 화면 언어(Apply 전에는 Windows 표시 언어로 고른 값).</summary>
    public static CultureInfo Current { get; private set; } = Resolve(AppLanguage.System, OsUiCulture);

    public static CultureInfo Resolve(AppLanguage language, CultureInfo osUiCulture) => language switch
    {
        AppLanguage.Korean => Korean,
        AppLanguage.English => English,
        _ => osUiCulture.TwoLetterISOLanguageName == "ko" ? Korean : English,
    };

    /// <summary>
    /// 모든 스레드의 UI 문화권과 WPF 요소의 xml:lang(글꼴 대체·입력기)을 정한다.
    /// 숫자·날짜 형식(CurrentCulture)은 Windows 지역 설정을 그대로 둔다.
    /// </summary>
    public static void Apply(AppLanguage language)
    {
        Current = Resolve(language, OsUiCulture);
        CultureInfo.DefaultThreadCurrentUICulture = Current;
        CultureInfo.CurrentUICulture = Current;
        if (_xmlLanguageSet) return;
        // 메타데이터 재정의는 프로세스에서 한 번만 할 수 있다.
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(Current.IetfLanguageTag)));
        _xmlLanguageSet = true;
    }
}
