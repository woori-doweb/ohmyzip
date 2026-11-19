using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace OhMyPic.Core.Tests;

public sealed class FontTests : TestBase
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo Korean = CultureInfo.GetCultureInfo("ko-KR");

    /// <summary>Independent copy of the catalog's Hangul sample (code points of common syllables).</summary>
    private static readonly int[] HangulCodePoints =
        [0xAC00, 0xB098, 0xB2E4, 0xD55C, 0xAE00, 0xC0AC, 0xC544, 0xC790, 0xB77C, 0xB9C8, 0xBC14, 0xD558, 0xAD6D, 0xC758, 0xB294, 0xC744];

    [Fact]
    public void OP_E43_Load_Returns_Unique_Entries_Sorted_By_Display_Name()
    {
        foreach (CultureInfo culture in new[] { English, Korean, CultureInfo.InvariantCulture })
        {
            IReadOnlyList<FontEntry> entries = FontCatalog.Load(culture);
            Assert.NotEmpty(entries);
            Assert.Equal(entries.Count, entries.Select(e => e.FamilyName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(entries, e =>
            {
                Assert.False(string.IsNullOrWhiteSpace(e.FamilyName));
                Assert.False(string.IsNullOrWhiteSpace(e.DisplayName));
            });
            StringComparer byDisplay = StringComparer.Create(culture, ignoreCase: true);
            for (int i = 1; i < entries.Count; i++)
                Assert.True(byDisplay.Compare(entries[i - 1].DisplayName, entries[i].DisplayName) <= 0,
                    $"'{entries[i - 1].DisplayName}' sorted before '{entries[i].DisplayName}' ({culture.Name})");
            // Every Windows install has at least one usable Latin text font.
            Assert.Contains(entries, e => e.SupportsLatin);
        }

        // No localized names exist for the invariant culture: the display name falls back to the family name.
        Assert.All(FontCatalog.Load(CultureInfo.InvariantCulture), e => Assert.Equal(e.FamilyName, e.DisplayName));
    }

    [Fact]
    public void OP_E43_Coverage_Matches_Direct_Glyph_Map_Check()
    {
        Assert.Equal(HangulCodePoints, FontCatalog.HangulSample.Select(c => (int)c));

        int evaluated = 0;
        foreach (FontFamily family in System.Windows.Media.Fonts.SystemFontFamilies)
        {
            FontEntry? entry = FontCatalog.Evaluate(family, English);
            if (entry is null)
                continue;
            var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            Assert.True(typeface.TryGetGlyphTypeface(out GlyphTypeface? glyphs));
            IDictionary<int, ushort> map = glyphs!.CharacterToGlyphMap;

            bool hangul = HangulCodePoints.All(map.ContainsKey);
            bool latin = Enumerable.Range('A', 26).All(c => map.ContainsKey(c) && map.ContainsKey(c + ('a' - 'A')));
            Assert.True(hangul == entry.SupportsHangul, $"Hangul coverage of {entry.FamilyName}");
            Assert.True(latin == entry.SupportsLatin, $"Latin coverage of {entry.FamilyName}");
            Assert.True(glyphs.Symbol == entry.IsSymbol, $"Symbol flag of {entry.FamilyName}");
            evaluated++;
        }
        Assert.True(evaluated > 0, "No installed family produced a glyph typeface.");
    }

    [Fact]
    public void OP_E43_Known_Hangul_And_Latin_Only_Fonts()
    {
        IReadOnlyList<FontEntry> korean = FontCatalog.Load(Korean);
        Assert.NotEmpty(korean);

        FontEntry? malgun = Find(korean, "Malgun Gothic");
        if (malgun is not null)
        {
            Assert.True(malgun.SupportsHangul);
            Assert.True(malgun.SupportsLatin);
            Assert.False(malgun.IsSymbol);
            // The Korean UI shows the font's Korean family name; the English UI shows the family name.
            Assert.True(malgun.DisplayName.Any(c => c >= '\uAC00' && c <= '\uD7A3'), $"Korean display name expected, got '{malgun.DisplayName}'");
            Assert.Equal("Malgun Gothic", Find(FontCatalog.Load(English), "Malgun Gothic")!.DisplayName);
        }

        FontEntry? consolas = Find(korean, "Consolas");
        if (consolas is not null)
        {
            Assert.False(consolas.SupportsHangul);
            Assert.True(consolas.SupportsLatin);
            Assert.False(consolas.IsSymbol);
        }
    }

    [Fact]
    public void OP_E43_Filter_By_Name_And_Hangul()
    {
        FontEntry[] entries =
        [
            new("Alpha Sans", "Alpha Sans", false, true, false),
            new("Beta Gothic", "Display Beta", true, true, false),
            new("Gamma Mono", "Gamma Mono", false, true, false),
            new("Delta Symbols", "Delta Symbols", false, false, true),
            new("Epsilon Serif", "\uAC00\uB098 Serif", true, false, false),
        ];

        Assert.Equal(entries.Select(e => e.FamilyName), Names(FontCatalog.Filter(entries, null, false)));
        Assert.Equal(entries.Select(e => e.FamilyName), Names(FontCatalog.Filter(entries, "   ", false)));
        Assert.Equal(new[] { "Beta Gothic" }, Names(FontCatalog.Filter(entries, "GOTHIC", false)));
        Assert.Equal(new[] { "Beta Gothic" }, Names(FontCatalog.Filter(entries, "display", false)));
        Assert.Equal(new[] { "Epsilon Serif" }, Names(FontCatalog.Filter(entries, "\uAC00\uB098", false)));
        Assert.Equal(new[] { "Beta Gothic", "Delta Symbols" }, Names(FontCatalog.Filter(entries, " TA ", false)));
        Assert.Equal(new[] { "Beta Gothic", "Epsilon Serif" }, Names(FontCatalog.Filter(entries, "", true)));
        Assert.Equal(new[] { "Epsilon Serif" }, Names(FontCatalog.Filter(entries, "serif", true)));
        Assert.Empty(FontCatalog.Filter(entries, "mono", true));
        Assert.Empty(FontCatalog.Filter(entries, "zeta", false));
    }

    [Fact]
    public void OP_E44_Favorites_RoundTrip_Keeps_Order_Without_Temp_Files()
    {
        string folder = Temp.Combine("data");
        string path = Path.Combine(folder, "font-favorites.json");
        var favorites = new FontFavorites(path);
        Assert.Empty(favorites.Items);
        Assert.Equal(path, favorites.FilePath);

        Assert.True(favorites.Add("Consolas"));
        Assert.True(favorites.Add("Malgun Gothic"));
        Assert.True(favorites.Add("Arial"));
        Assert.False(favorites.Add("  consolas "));
        Assert.True(favorites.Remove("MALGUN GOTHIC"));
        Assert.False(favorites.Remove("Malgun Gothic"));
        Assert.True(favorites.Add("Segoe UI"));
        Assert.True(favorites.Add("Malgun Gothic"));
        Assert.Equal(new[] { "Consolas", "Arial", "Segoe UI", "Malgun Gothic" }, favorites.Items);
        favorites.Save();

        Assert.Equal(["font-favorites.json"], Util.FileNames(folder));
        Assert.Empty(Util.TempFilesUnder(Temp.Path));
        FontFavorites loaded = FontFavorites.Load(path);
        Assert.Equal(new[] { "Consolas", "Arial", "Segoe UI", "Malgun Gothic" }, loaded.Items);
        Assert.True(loaded.Contains("arial"));
        Assert.False(loaded.Contains("Calibri"));

        // Saving again replaces the file in place.
        Assert.True(loaded.Remove("Arial"));
        loaded.Save();
        Assert.Equal(new[] { "Consolas", "Segoe UI", "Malgun Gothic" }, FontFavorites.Load(path).Items);
        Assert.Equal(["font-favorites.json"], Util.FileNames(folder));
        Assert.Empty(Util.TempFilesUnder(Temp.Path));
    }

    [Fact]
    public void OP_E44_Favorites_Missing_Or_Corrupt_File_Loads_Empty()
    {
        Assert.Empty(FontFavorites.Load(Temp.Combine("missing", "font-favorites.json")).Items);

        string path = Temp.Combine("font-favorites.json");
        foreach (string content in new[] { "{ not json", "{\"Arial\": true}", "[1, 2]", "null", "", "[\"Arial\"" })
        {
            File.WriteAllText(path, content);
            FontFavorites loaded = FontFavorites.Load(path);
            Assert.Empty(loaded.Items);
            Assert.Equal(path, loaded.FilePath);
        }

        // Blank and duplicate names in an otherwise valid file are dropped.
        File.WriteAllText(path, JsonSerializer.Serialize(new[] { "Arial", "", "  ", "arial", "Consolas" }));
        Assert.Equal(new[] { "Arial", "Consolas" }, FontFavorites.Load(path).Items);

        // A corrupt file is replaced by the next save.
        File.WriteAllText(path, "{ not json");
        FontFavorites recovered = FontFavorites.Load(path);
        Assert.True(recovered.Add("Segoe UI"));
        recovered.Save();
        Assert.Equal(new[] { "Segoe UI" }, FontFavorites.Load(path).Items);
        Assert.Empty(Util.TempFilesUnder(Temp.Path));
    }

    private static FontEntry? Find(IEnumerable<FontEntry> entries, string familyName) =>
        entries.FirstOrDefault(e => string.Equals(e.FamilyName, familyName, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> Names(IEnumerable<FontEntry> entries) => entries.Select(e => e.FamilyName);
}
