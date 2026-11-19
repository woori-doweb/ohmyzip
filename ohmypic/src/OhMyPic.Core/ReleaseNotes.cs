using System.Globalization;

namespace OhMyPic.Core;

/// <summary>
/// Release notes in two languages: one Markdown text with an English and a Korean "## " section (packaging writes it
/// beside the executable as <see cref="FileName"/>; the GitHub release body uses the same text). Older notes without
/// those headings are used whole. Also decides when the app shows "What's new" once after an update.
/// </summary>
public static class ReleaseNotes
{
    /// <summary>Notes of the installed version, next to the executable (absent in development builds).</summary>
    public const string FileName = "WHATSNEW.md";

    private const string HeadingPrefix = "## ";
    private const string EnglishHeading = "English";
    /// <summary>The Korean name of the Korean language (escaped: no non-English literals in code).</summary>
    private const string KoreanHeading = "\uD55C\uAD6D\uC5B4";

    /// <summary>
    /// The section for the UI language: the Korean section for a Korean UI when present, else the English section (the
    /// Korean one when it is the only one); the whole text (trimmed) when neither heading exists. A section is the lines
    /// after its heading up to the next "## " heading, trimmed.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="notes"/> or <paramref name="uiCulture"/> is null.</exception>
    public static string Section(string notes, CultureInfo uiCulture)
    {
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(uiCulture);
        string[] lines = notes.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        string? english = null;
        string? korean = null;
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith(HeadingPrefix, StringComparison.Ordinal)) continue;
            string heading = lines[i][HeadingPrefix.Length..].Trim();
            if (heading == EnglishHeading) english ??= Body(lines, i);
            else if (heading == KoreanHeading) korean ??= Body(lines, i);
        }
        if (korean is not null && uiCulture.TwoLetterISOLanguageName == "ko") return korean;
        if (english is not null) return english;
        return korean ?? string.Join('\n', lines).Trim();
    }

    /// <summary>
    /// True when the notes of <paramref name="current"/> were not shown yet: the stored version key is missing,
    /// unreadable or names another major.minor.patch.
    /// </summary>
    public static bool ShouldShowWhatsNew(string? lastSeenVersion, Version current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (string.IsNullOrWhiteSpace(lastSeenVersion) || !Version.TryParse(lastSeenVersion.Trim(), out Version? seen)) return true;
        return seen.Major != current.Major || seen.Minor != current.Minor || Math.Max(seen.Build, 0) != Math.Max(current.Build, 0);
    }

    /// <summary>"major.minor.patch" (a missing patch counts as 0); stored as the last version whose notes were seen.</summary>
    public static string VersionKey(Version v)
    {
        ArgumentNullException.ThrowIfNull(v);
        return string.Create(CultureInfo.InvariantCulture, $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}");
    }

    /// <summary>Lines after the heading at <paramref name="heading"/> up to the next heading, trimmed.</summary>
    private static string Body(string[] lines, int heading)
    {
        int end = heading + 1;
        while (end < lines.Length && !lines[end].StartsWith(HeadingPrefix, StringComparison.Ordinal)) end++;
        return string.Join('\n', lines, heading + 1, end - heading - 1).Trim();
    }
}
