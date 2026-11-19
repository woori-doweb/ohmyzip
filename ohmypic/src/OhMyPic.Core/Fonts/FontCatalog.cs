using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

// Namespace stays OhMyPic.Core: a namespace named "Fonts" would hide System.Windows.Media.Fonts for every caller in OhMyPic.Core.*.
namespace OhMyPic.Core;

/// <param name="FamilyName">Name WPF enumerated the family under (the invariant, usually en-US, name); use it to create a <see cref="FontFamily"/> and to store favourites.</param>
/// <param name="DisplayName">Family name localized for the requested culture; equals <paramref name="FamilyName"/> when the font has no name in that language.</param>
public sealed record FontEntry(string FamilyName, string DisplayName, bool SupportsHangul, bool SupportsLatin, bool IsSymbol);

/// <summary>Installed font families with script coverage. Never throws because of a broken font: such families are skipped.</summary>
public static class FontCatalog
{
    /// <summary>
    /// Common Hangul syllables (GA, NA, DA, HAN, GEUL, SA, A, JA, RA, MA, BA, HA, GUK, UI, NEUN, EUL).
    /// A family supports Hangul when every one of them is in the cmap (<see cref="GlyphTypeface.CharacterToGlyphMap"/>)
    /// of its regular typeface. A sample instead of the whole U+AC00..U+D7A3 block keeps the check fast and still rejects
    /// fonts that only carry a handful of Hangul glyphs (or only Jamo).
    /// </summary>
    public static IReadOnlyList<char> HangulSample { get; } =
    [
        '\uAC00', '\uB098', '\uB2E4', '\uD55C', '\uAE00', '\uC0AC', '\uC544', '\uC790',
        '\uB77C', '\uB9C8', '\uBC14', '\uD558', '\uAD6D', '\uC758', '\uB294', '\uC744',
    ];

    private static readonly XmlLanguage EnglishUs = XmlLanguage.GetLanguage("en-us");

    /// <summary>All installed families that have a usable regular typeface, deduplicated by family name and sorted by display name for <paramref name="uiCulture"/>.</summary>
    public static IReadOnlyList<FontEntry> Load(CultureInfo uiCulture)
    {
        ArgumentNullException.ThrowIfNull(uiCulture);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<FontEntry>();
        foreach (FontFamily family in System.Windows.Media.Fonts.SystemFontFamilies)
        {
            FontEntry? entry = Evaluate(family, uiCulture);
            if (entry is not null && seen.Add(entry.FamilyName))
                entries.Add(entry);
        }
        StringComparer byDisplay = StringComparer.Create(uiCulture, ignoreCase: true);
        entries.Sort((a, b) =>
        {
            int c = byDisplay.Compare(a.DisplayName, b.DisplayName);
            return c != 0 ? c : StringComparer.Ordinal.Compare(a.FamilyName, b.FamilyName);
        });
        return entries;
    }

    /// <summary>Coverage and names of one family; null when it has no name or its regular typeface cannot produce a <see cref="GlyphTypeface"/>
    /// (composite families, damaged or unreadable font files).</summary>
    public static FontEntry? Evaluate(FontFamily family, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(culture);
        try
        {
            string? familyName = InvariantName(family);
            if (familyName is null)
                return null;
            var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            if (!typeface.TryGetGlyphTypeface(out GlyphTypeface? glyphs) || glyphs is null)
                return null;
            IDictionary<int, ushort> map = glyphs.CharacterToGlyphMap;
            bool hangul = HangulSample.All(c => map.ContainsKey(c));
            bool latin = true;
            for (char c = 'A'; c <= 'Z' && latin; c++)
                latin = map.ContainsKey(c) && map.ContainsKey(char.ToLowerInvariant(c));
            string display = LocalizedName(family.FamilyNames, culture) ?? familyName;
            return new FontEntry(familyName, display, hangul, latin, glyphs.Symbol);
        }
        catch (Exception)
        {
            // A single broken installed font (bad file, access denied, unexpected table data) must never break the list.
            return null;
        }
    }

    /// <summary>Entries whose family or display name contains <paramref name="query"/> (case-insensitive; blank matches all),
    /// optionally only those that support Hangul. Keeps the input order.</summary>
    public static IReadOnlyList<FontEntry> Filter(IEnumerable<FontEntry> entries, string? query, bool hangulOnly)
    {
        ArgumentNullException.ThrowIfNull(entries);
        string q = query?.Trim() ?? "";
        return entries
            .Where(e => !hangulOnly || e.SupportsHangul)
            .Where(e => q.Length == 0
                || e.FamilyName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || e.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>The name WPF enumerated the family under (the part after '#' when Source is a location), else its en-US name.</summary>
    private static string? InvariantName(FontFamily family)
    {
        string? source = family.Source;
        if (!string.IsNullOrWhiteSpace(source))
        {
            int hash = source.LastIndexOf('#');
            string name = (hash >= 0 ? source[(hash + 1)..] : source).Trim();
            if (name.Length > 0)
                return name;
        }
        return family.FamilyNames.TryGetValue(EnglishUs, out string? en) && !string.IsNullOrWhiteSpace(en) ? en.Trim() : null;
    }

    /// <summary>Exact language-tag match first, then any name in the same primary language (ko matches ko-kr and the reverse).</summary>
    private static string? LocalizedName(LanguageSpecificStringDictionary names, CultureInfo culture)
    {
        string tag = culture.IetfLanguageTag;
        if (tag.Length == 0)
            return null;
        string primary = PrimaryLanguage(tag);
        string? samePrimary = null;
        foreach (KeyValuePair<XmlLanguage, string> pair in names)
        {
            if (string.IsNullOrWhiteSpace(pair.Value))
                continue;
            string key = pair.Key.IetfLanguageTag;
            if (string.Equals(key, tag, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
            if (samePrimary is null && string.Equals(PrimaryLanguage(key), primary, StringComparison.OrdinalIgnoreCase))
                samePrimary = pair.Value;
        }
        return samePrimary;
    }

    private static string PrimaryLanguage(string tag)
    {
        int dash = tag.IndexOf('-');
        return dash >= 0 ? tag[..dash] : tag;
    }
}
