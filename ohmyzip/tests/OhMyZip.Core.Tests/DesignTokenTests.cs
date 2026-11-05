using System.Reflection;
using System.Text.RegularExpressions;
using OhMyZip.App.Commands;
using OhMyZip.App.Theme;

namespace OhMyZip.Core.Tests;

/// <summary>
/// 앱 색 토큰 대비(OZ-A02)와 툴바 명령 ⊆ 메뉴 명령 대조(OZ-A06).
/// 창을 띄우지 않고 앱 어셈블리의 공개 정적 표(Tokens, CommandCatalog)만 읽는다.
/// </summary>
public sealed class DesignTokenTests
{
    private const double MinimumTextContrast = 4.5;

    private static readonly Palette[] Palettes = [Tokens.Light, Tokens.Dark];

    private static readonly Regex OpaqueHex = new("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant);

    private static Palette PaletteNamed(string name) => Palettes.Single(p => p.Name == name);

    private static IEnumerable<PropertyInfo> ColorProperties() =>
        typeof(Palette).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(prop => prop.PropertyType == typeof(string) && prop.Name != nameof(Palette.Name));

    // ───────────────────────── OZ-A02 색 대비 ─────────────────────────

    /// <summary>(팔레트 이름, 쌍 이름). 시험 탐색기에서 쌍마다 한 줄로 보이도록 문자열만 넘긴다.</summary>
    public static TheoryData<string, string> ContrastCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var p in Palettes)
            foreach (var pair in Tokens.ContrastPairs(p))
                data.Add(p.Name, pair.Label);
        return data;
    }

    [Theory]
    [MemberData(nameof(ContrastCases))]
    public void OZ_A02_Text_pair_contrast_is_at_least_4_5_to_1(string palette, string label)
    {
        var (_, foreground, background) = Tokens.ContrastPairs(PaletteNamed(palette)).Single(x => x.Label == label);
        double ratio = Tokens.ContrastRatio(foreground, background);
        Assert.True(ratio >= MinimumTextContrast,
            $"{palette} 팔레트 {label}: {foreground} / {background} 대비 {ratio:F2}:1 (기준 {MinimumTextContrast}:1 미만)");
    }

    [Fact]
    public void OZ_A02_Contrast_pairs_exist_for_each_palette_with_unique_labels()
    {
        Assert.Equal(Palettes.Length, Palettes.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count());
        foreach (var p in Palettes)
        {
            var labels = Tokens.ContrastPairs(p).Select(x => x.Label).ToList();
            Assert.NotEmpty(labels);
            Assert.Equal(labels.Count, labels.Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Theory]
    [InlineData("#000000", "#FFFFFF")]
    [InlineData("#FFFFFF", "#000000")]
    public void OZ_A02_Contrast_ratio_of_black_and_white_is_21_to_1(string foreground, string background)
    {
        Assert.InRange(Tokens.ContrastRatio(foreground, background), 21.0 - 0.01, 21.0 + 0.01);
    }

    [Theory]
    [InlineData("#000000")]
    [InlineData("#FFFFFF")]
    [InlineData("#767676")]
    [InlineData("#005FB8")]
    [InlineData("#1C1C1E")]
    public void OZ_A02_Contrast_ratio_of_same_color_is_1_to_1(string hex)
    {
        Assert.InRange(Tokens.ContrastRatio(hex, hex), 1.0 - 0.01, 1.0 + 0.01);
    }

    [Fact]
    public void OZ_A02_Contrast_ratio_matches_wcag_reference_gray_on_white()
    {
        // #767676 / #FFFFFF 는 WCAG 4.5:1 경계 근처의 널리 쓰이는 기준값(4.54:1).
        Assert.InRange(Tokens.ContrastRatio("#767676", "#FFFFFF"), 4.54 - 0.01, 4.54 + 0.01);
    }

    /// <summary>(팔레트 이름, 색 필드 이름).</summary>
    public static TheoryData<string, string> PaletteColorFields()
    {
        var data = new TheoryData<string, string>();
        foreach (var p in Palettes)
            foreach (var prop in ColorProperties())
                data.Add(p.Name, prop.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(PaletteColorFields))]
    public void OZ_A02_Palette_color_is_opaque_rrggbb(string palette, string field)
    {
        var value = (string?)typeof(Palette).GetProperty(field)!.GetValue(PaletteNamed(palette));
        Assert.True(value is not null && OpaqueHex.IsMatch(value),
            $"{palette}.{field} = '{value}': 불투명 #RRGGBB 가 아님(알파·반투명 값 금지)");
    }

    [Theory]
    [InlineData("#80FFFFFF")]
    [InlineData("#FFF")]
    public void OZ_A02_Parse_rejects_values_other_than_rrggbb(string hex)
    {
        // 브러시는 Tokens.Parse 를 거쳐 만들어지므로 알파가 붙은 값은 화면에 닿기 전에 실패한다.
        Assert.Throws<FormatException>(() => Tokens.Parse(hex));
    }

    // ───────────────────────── OZ-A06 툴바 ⊆ 메뉴 ─────────────────────────

    /// <summary>MainWindow 메뉴 생성과 같은 규칙: 최상위 메뉴에 속한 Items, 그리고 그 항목 뒤에 붙는 FeatureItems.</summary>
    private static HashSet<string> MenuCommandKeys()
    {
        var menuNames = CommandCatalog.Menus.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var spec in CommandCatalog.Items.Where(i => menuNames.Contains(i.Menu)))
        {
            keys.Add(spec.Key);
            foreach (var (_, feature) in CommandCatalog.FeatureItems.Where(f => f.AfterKey == spec.Key))
                keys.Add(feature.Key);
        }
        return keys;
    }

    public static TheoryData<string> ToolbarCommandKeys()
    {
        var data = new TheoryData<string>();
        foreach (var key in CommandCatalog.ToolbarGroups.SelectMany(g => g).Select(t => t.Key).Distinct(StringComparer.Ordinal))
            data.Add(key);
        return data;
    }

    [Theory]
    [MemberData(nameof(ToolbarCommandKeys))]
    public void OZ_A06_Toolbar_command_exists_in_menu(string key)
    {
        bool inMenu = MenuCommandKeys().Contains(key);
        Assert.True(inMenu, $"툴바 명령 '{key}' 가 메뉴 정의(CommandCatalog.Items/FeatureItems)에 없음");
    }

    [Theory]
    [MemberData(nameof(ToolbarCommandKeys))]
    public void OZ_A06_Toolbar_command_shares_active_menu_command(string key)
    {
        // 메뉴 항목은 구현된 명령만 RoutedUICommand 를 받는다. 툴바 버튼과 메뉴 항목은 같은 키의 명령을 공유한다.
        Assert.True(CommandCatalog.IsImplemented(key), $"툴바 명령 '{key}' 가 구현 목록에 없어 메뉴에서 비활성");
        Assert.Equal(key, CommandCatalog.Command(key).Name);
        Assert.Same(CommandCatalog.Command(key), CommandCatalog.Command(key));
    }

    [Fact]
    public void OZ_A06_All_toolbar_commands_are_in_menu()
    {
        var toolbar = CommandCatalog.ToolbarGroups.SelectMany(g => g).Select(t => t.Key).ToList();
        Assert.NotEmpty(toolbar);
        Assert.Equal(toolbar.Count, toolbar.Distinct(StringComparer.Ordinal).Count());
        var menu = MenuCommandKeys();
        var missing = toolbar.Where(k => !menu.Contains(k)).ToList();
        Assert.True(missing.Count == 0, "메뉴에 없는 툴바 명령: " + string.Join(", ", missing));
    }

    [Fact]
    public void OZ_A06_Feature_menu_items_are_anchored_to_existing_menu_items()
    {
        // FeatureItems 는 AfterKey 항목 바로 뒤에만 붙으므로, 앵커가 없으면 메뉴에 나타나지 않는다.
        var itemKeys = CommandCatalog.Items.Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var (afterKey, spec) in CommandCatalog.FeatureItems)
        {
            bool anchored = itemKeys.Contains(afterKey);
            Assert.True(anchored, $"기능 메뉴 항목 '{spec.Key}' 의 앵커 '{afterKey}' 가 메뉴에 없음");
        }
    }
}
