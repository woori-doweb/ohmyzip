using System.Collections;
using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using OhMyZip.App.Settings;
using AppText = OhMyZip.App.Text;
using CoreText = OhMyZip.Core.Resources;

namespace OhMyZip.Core.Tests;

/// <summary>
/// 시험 프로세스의 화면 언어를 한국어로 고정한다. 기존 시험은 한국어 문구를 기대하고,
/// CI(영어 Windows)에서도 같은 결과여야 한다. 영어 문구는 아래 시험이 문화권을 직접 지정해 읽는다.
/// </summary>
internal static class KoreanTestCulture
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        CultureInfo.DefaultThreadCurrentUICulture = UiLanguage.Korean;
        CultureInfo.CurrentUICulture = UiLanguage.Korean;
    }
}

public sealed class LocalizationTests
{
    private static readonly CultureInfo Ko = CultureInfo.GetCultureInfo("ko");

    /// <summary>영어 기본 리소스와 한국어 번역본 쌍(이름, 관리자).</summary>
    private static readonly string[] SetNames = ["EngineText", "FormatText", "ServiceText", "MenuText", "MainText", "DialogText"];

    public static readonly TheoryData<string> Sets = new(SetNames);

    private static ResourceManager Manager(string set) => set switch
    {
        "EngineText" => CoreText.EngineText.ResourceManager,
        "FormatText" => CoreText.FormatText.ResourceManager,
        "ServiceText" => CoreText.ServiceText.ResourceManager,
        "MenuText" => AppText.MenuText.ResourceManager,
        "MainText" => AppText.MainText.ResourceManager,
        "DialogText" => AppText.DialogText.ResourceManager,
        _ => throw new ArgumentOutOfRangeException(nameof(set)),
    };

    /// <summary>문화권 하나의 문구만(부모 대체 없이) 키 → 값.</summary>
    private static Dictionary<string, string> Read(string set, CultureInfo culture)
    {
        var rs = Manager(set).GetResourceSet(culture, createIfNotExists: true, tryParents: false)
                 ?? throw new InvalidOperationException($"{set}: {culture.Name} 리소스가 없습니다(위성 어셈블리 누락).");
        return rs.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!, StringComparer.Ordinal);
    }

    private static Dictionary<string, string> English(string set) => Read(set, CultureInfo.InvariantCulture);
    private static Dictionary<string, string> Korean(string set) => Read(set, Ko);

    [Theory]
    [MemberData(nameof(Sets))]
    public void OZ_L01_Every_key_has_English_and_Korean_text(string set)
    {
        var en = English(set);
        var ko = Korean(set);
        Assert.NotEmpty(en);
        Assert.Empty(en.Keys.Except(ko.Keys).Order());
        Assert.Empty(ko.Keys.Except(en.Keys).Order());
        Assert.Empty(en.Where(p => string.IsNullOrWhiteSpace(p.Value)).Select(p => "en:" + p.Key)
            .Concat(ko.Where(p => string.IsNullOrWhiteSpace(p.Value)).Select(p => "ko:" + p.Key)));
    }

    private static readonly Regex Placeholder = new(@"(?<!\{)\{(\d+)(?:[,:][^}]*)?\}(?!\})", RegexOptions.CultureInvariant);

    private static string Holes(string s) => string.Join(",", Placeholder.Matches(s).Select(m => int.Parse(m.Groups[1].Value)).Distinct().Order());

    [Theory]
    [MemberData(nameof(Sets))]
    public void OZ_L02_Format_placeholders_match_between_languages(string set)
    {
        var en = English(set);
        var ko = Korean(set);
        var mismatched = en.Where(p => ko.TryGetValue(p.Key, out var k) && Holes(p.Value) != Holes(k))
            .Select(p => $"{p.Key}: en[{Holes(p.Value)}] ko[{Holes(ko[p.Key])}]").ToList();
        Assert.Empty(mismatched);
        // 자리 표시자는 0 부터 빈틈없이(string.Format 인자 순서와 맞물리게).
        var gaps = en.Where(p => Holes(p.Value) is { Length: > 0 } h && h != string.Join(",", Enumerable.Range(0, h.Split(',').Length)))
            .Select(p => p.Key).ToList();
        Assert.Empty(gaps);
    }

    /// <summary>영어 문구 안에 한글을 둘 수 있는 키: 언어 이름을 그 언어로 보여 주는 항목.</summary>
    private static readonly HashSet<string> NativeLanguageNames = new(StringComparer.Ordinal) { "Pref_Language_Korean" };

    [Theory]
    [MemberData(nameof(Sets))]
    public void OZ_L03_English_text_has_no_Hangul_and_uses_one_ellipsis_character(string set)
    {
        var bad = English(set)
            .Where(p => (!NativeLanguageNames.Contains(p.Key) && Regex.IsMatch(p.Value, "[가-힣]")) || p.Value.Contains("..."))
            .Select(p => $"{p.Key}: {p.Value}").ToList();
        Assert.Empty(bad);
    }

    [Theory]
    [MemberData(nameof(Sets))]
    public void OZ_L04_Korean_text_keeps_terms_and_ellipsis(string set)
    {
        var bad = Korean(set)
            .Where(p => p.Value.Contains("압축파일") || p.Value.Contains("새창") || p.Value.Contains("..."))
            .Select(p => $"{p.Key}: {p.Value}").ToList();
        Assert.Empty(bad);
    }

    private static readonly HashSet<string> MinorWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "but", "or", "nor", "for", "so", "yet",
        "as", "at", "by", "in", "of", "off", "on", "per", "to", "up", "via", "from", "into", "with",
    };

    /// <summary>제목식 대문자: 첫 단어·마지막 단어·관사/짧은 전치사/등위 접속사가 아닌 단어는 대문자로 시작.</summary>
    internal static bool IsTitleCase(string label)
    {
        var words = label.TrimEnd('…').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
        {
            string w = words[i].Trim('(', ')', '"', '“', '”', '\'');
            if (w.Length == 0 || !char.IsLetter(w[0]) || w.Contains("ohmyZip", StringComparison.Ordinal)) continue;
            bool edge = i == 0 || i == words.Length - 1;
            string core = w.TrimEnd(':', ',', '.', '!', '?');
            if (!edge && MinorWords.Contains(core)) continue;
            if (!char.IsUpper(w[0])) return false;
        }
        return true;
    }

    [Theory]
    [InlineData("Open Archive…", true)]
    [InlineData("Extract to a New Folder", true)]
    [InlineData("Check for Updates…", true)]
    [InlineData("About ohmyZip", true)]
    [InlineData("Open archive…", false)]
    [InlineData("for Updates", false)]
    public void OZ_L05a_Title_case_rule(string label, bool expected) => Assert.Equal(expected, IsTitleCase(label));

    /// <summary>
    /// 메뉴 항목·메뉴 이름·단추·창 제목은 제목식 대문자(HIG menus·alerts).
    /// 키 규칙: MenuText 의 Menu_*·Label_*, 모든 집합의 *_Button·*_Title.
    /// </summary>
    [Theory]
    [MemberData(nameof(Sets))]
    public void OZ_L05_English_menus_buttons_and_titles_use_title_case(string set)
    {
        var bad = English(set)
            .Where(p => (set == "MenuText" && (p.Key.StartsWith("Menu_", StringComparison.Ordinal) || p.Key.StartsWith("Label_", StringComparison.Ordinal)))
                        || p.Key.EndsWith("_Button", StringComparison.Ordinal) || p.Key.EndsWith("_Title", StringComparison.Ordinal))
            .Where(p => !IsTitleCase(p.Value))
            .Select(p => $"{p.Key}: {p.Value}").ToList();
        Assert.Empty(bad);
    }

    [Theory]
    [InlineData(AppLanguage.System, "ko-KR", "ko-KR")]
    [InlineData(AppLanguage.System, "ko", "ko-KR")]
    [InlineData(AppLanguage.System, "en-GB", "en-US")]
    [InlineData(AppLanguage.System, "ja-JP", "en-US")]
    [InlineData(AppLanguage.Korean, "en-US", "ko-KR")]
    [InlineData(AppLanguage.English, "ko-KR", "en-US")]
    public void OZ_L06_Language_follows_Windows_unless_chosen(AppLanguage choice, string os, string expected) =>
        Assert.Equal(expected, UiLanguage.Resolve(choice, CultureInfo.GetCultureInfo(os)).Name);

    [Fact]
    public void OZ_L07_Satellite_assemblies_resolve_Korean_and_fall_back_to_English()
    {
        var rm = AppText.DialogText.ResourceManager;
        Assert.Equal("바꾼 언어는 ohmyZip 을 다시 시작하면 적용됩니다.", rm.GetString("Pref_LanguageNote", UiLanguage.Korean));
        Assert.Equal("The new language takes effect the next time you start ohmyZip.", rm.GetString("Pref_LanguageNote", UiLanguage.English));
        // 번역본이 없는 언어는 영어(기본 리소스)로.
        Assert.Equal(rm.GetString("Pref_LanguageNote", UiLanguage.English), rm.GetString("Pref_LanguageNote", CultureInfo.GetCultureInfo("ja-JP")));
        foreach (var set in SetNames)
            Assert.NotEmpty(Korean(set));
    }

    /// <summary>이 스레드의 UI 문화권을 잠시 바꾼다(동기 시험 안에서만).</summary>
    private sealed class UiCulture : IDisposable
    {
        private readonly CultureInfo _saved = CultureInfo.CurrentUICulture;
        public UiCulture(CultureInfo culture) => CultureInfo.CurrentUICulture = culture;
        public void Dispose() => CultureInfo.CurrentUICulture = _saved;
    }

    [Fact]
    public void OZ_L09_Engine_and_app_text_follow_the_UI_culture()
    {
        using var temp = new TempDir();
        string missing = temp.Combine("missing.bin");
        string english = CoreText.ServiceText.ResourceManager.GetString("Shred_FileMissing", UiLanguage.English)!;
        string korean = CoreText.ServiceText.ResourceManager.GetString("Shred_FileMissing", UiLanguage.Korean)!;
        Assert.NotEqual(english, korean);
        using (new UiCulture(UiLanguage.English))
        {
            OperationResult result = SecureDelete.Shred(missing);
            Assert.Equal(OperationStatus.Failed, result.Status);
            Assert.Contains(english, result.Message);
            Assert.DoesNotMatch("[가-힣]", result.Message);
            Assert.Equal(AppText.DialogText.ResourceManager.GetString("Pref_LanguageNote", UiLanguage.English), AppText.DialogText.Pref_LanguageNote);
        }
        using (new UiCulture(UiLanguage.Korean))
        {
            Assert.Contains(korean, SecureDelete.Shred(missing).Message);
            Assert.Equal("바꾼 언어는 ohmyZip 을 다시 시작하면 적용됩니다.", AppText.DialogText.Pref_LanguageNote);
        }
    }

    /// <summary>XAML 의 {x:Static res:클래스.키} 가 모두 리소스 속성으로 있는지(없으면 창을 열 때 실패).</summary>
    [Fact]
    public void OZ_L10_Xaml_resource_references_exist()
    {
        var classes = new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            ["MenuText"] = typeof(AppText.MenuText), ["MainText"] = typeof(AppText.MainText), ["DialogText"] = typeof(AppText.DialogText),
        };
        var missing = new List<string>();
        int count = 0;
        foreach (var path in Directory.EnumerateFiles(SourceRoot(), "*.xaml", SearchOption.AllDirectories))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(path), @"\{x:Static\s+res:(\w+)\.(\w+)\}"))
            {
                count++;
                if (!classes.TryGetValue(m.Groups[1].Value, out var type) || type.GetProperty(m.Groups[2].Value) is null)
                    missing.Add($"{Path.GetFileName(path)}: {m.Value}");
            }
        }
        Assert.True(count > 0);
        Assert.Empty(missing);
    }

    // ── 코드에 화면 문구(한글 리터럴)가 남지 않았는지 ──

    private static string SourceRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "OhMyZip.sln")))
                return Path.Combine(dir.FullName, "src");
        throw new DirectoryNotFoundException("OhMyZip.sln 을 찾지 못했습니다.");
    }

    /// <summary>화면에 보이지 않는 데이터로 쓰는 한글 리터럴(파일 → 이유). 화면 문구는 넣지 않는다.</summary>
    private static readonly Dictionary<string, string> AllowedKoreanLiterals = new(StringComparer.Ordinal)
    {
    };

    private static readonly Regex Literal = new("@?\\$?\"(?:[^\"\\\\\\n]|\\\\.)*\"", RegexOptions.CultureInvariant);

    internal static string StripComments(string text, bool xaml)
    {
        if (xaml) return Regex.Replace(text, "<!--.*?-->", "", RegexOptions.Singleline);
        text = Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline);
        var lines = text.Split('\n');
        for (int n = 0; n < lines.Length; n++)
        {
            string line = lines[n];
            bool inString = false;
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '"' && (i == 0 || line[i - 1] != '\\')) inString = !inString;
                else if (!inString && i + 1 < line.Length && line[i] == '/' && line[i + 1] == '/') { lines[n] = line[..i]; break; }
            }
        }
        return string.Join('\n', lines);
    }

    [Fact]
    public void OZ_L08_Source_has_no_Korean_UI_literals()
    {
        string root = SourceRoot();
        var hits = new List<string>();
        foreach (var path in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
        {
            bool xaml = path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase);
            if (!xaml && !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
            string rel = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (rel.Split('/').Any(part => part is "bin" or "obj") || AllowedKoreanLiterals.ContainsKey(rel)) continue;
            var lines = StripComments(File.ReadAllText(path), xaml).Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                IEnumerable<string> candidates = xaml ? [lines[i]] : Literal.Matches(lines[i]).Select(m => m.Value);
                hits.AddRange(candidates.Where(c => Regex.IsMatch(c, "[가-힣]")).Select(c => $"{rel}:{i + 1}: {c.Trim()}"));
            }
        }
        Assert.True(hits.Count == 0, $"{hits.Count} Korean literals:\n" + string.Join("\n", hits.Take(40)));
    }
}
