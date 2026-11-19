using System.Globalization;

namespace OhMyPic.Core.Tests;

/// <summary>Release notes language sections and the once-per-version "What's new" decision (OP-E54).</summary>
public sealed class ReleaseNotesTests
{
    // Korean text is escaped: no non-English literals in code. "\uD55C\uAD6D\uC5B4" is the Korean section heading.
    private const string Korean = "\uD55C\uAD6D\uC5B4";
    private const string KoreanLine = "- \uC0C8 \uAE30\uB2A5";

    private static readonly string Notes =
        "Intro that is not part of any section\n" +
        "\n" +
        "## English\n" +
        "\n" +
        "- New feature\n" +
        "- Fixed a bug\n" +
        "\n" +
        "## " + Korean + "\n" +
        KoreanLine + "\n" +
        "\n";

    private static readonly CultureInfo Ko = CultureInfo.GetCultureInfo("ko-KR");
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr");

    [Fact]
    public void OP_E54_Korean_UI_Picks_Korean_Section()
    {
        Assert.Equal(KoreanLine, ReleaseNotes.Section(Notes, Ko));
    }

    [Fact]
    public void OP_E54_English_And_Other_UI_Pick_English_Section()
    {
        Assert.Equal("- New feature\n- Fixed a bug", ReleaseNotes.Section(Notes, En));
        Assert.Equal("- New feature\n- Fixed a bug", ReleaseNotes.Section(Notes, Fr));
    }

    [Fact]
    public void OP_E54_Text_Before_First_Heading_Is_Ignored()
    {
        foreach (var culture in new[] { Ko, En, Fr })
            Assert.DoesNotContain("Intro", ReleaseNotes.Section(Notes, culture), StringComparison.Ordinal);
    }

    [Fact]
    public void OP_E54_Notes_Without_Language_Headings_Are_Used_Whole()
    {
        const string plain = "\n  * First change\n## Details\n* Second change\n\n";
        Assert.Equal("* First change\n## Details\n* Second change", ReleaseNotes.Section(plain, Ko));
        Assert.Equal("* First change\n## Details\n* Second change", ReleaseNotes.Section(plain, En));
        Assert.Equal("", ReleaseNotes.Section("", En));
        Assert.Equal("", ReleaseNotes.Section(" \r\n ", Ko));
    }

    [Fact]
    public void OP_E54_CRLF_And_CR_Give_The_Same_Sections_As_LF()
    {
        foreach (var culture in new[] { Ko, En })
        {
            string lf = ReleaseNotes.Section(Notes, culture);
            Assert.Equal(lf, ReleaseNotes.Section(Notes.Replace("\n", "\r\n"), culture));
            Assert.Equal(lf, ReleaseNotes.Section(Notes.Replace("\n", "\r"), culture));
        }
        Assert.DoesNotContain("\r", ReleaseNotes.Section("## English\r\n- a\r\n- b\r\n", En));
    }

    [Fact]
    public void OP_E54_Empty_Korean_Section_Is_Empty()
    {
        string notes = "## English\n- Change\n## " + Korean + "\n\n   \n";
        Assert.Equal("", ReleaseNotes.Section(notes, Ko));
        Assert.Equal("- Change", ReleaseNotes.Section(notes, En));
    }

    [Fact]
    public void OP_E54_Section_Ends_At_The_Next_Heading_In_Any_Order()
    {
        string notes = "## " + Korean + "\n" + KoreanLine + "\n## Other\n- other\n## English\n- Change\n## Footer\n- footer";
        Assert.Equal(KoreanLine, ReleaseNotes.Section(notes, Ko));
        Assert.Equal("- Change", ReleaseNotes.Section(notes, En));
    }

    [Fact]
    public void OP_E54_Null_Arguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => ReleaseNotes.Section(null!, En));
        Assert.Throws<ArgumentNullException>(() => ReleaseNotes.Section(Notes, null!));
    }

    [Fact]
    public void OP_E54_ShouldShowWhatsNew_When_Last_Seen_Is_Missing_Unreadable_Or_Different()
    {
        var current = new Version(0, 2, 7);
        Assert.True(ReleaseNotes.ShouldShowWhatsNew(null, current));
        Assert.True(ReleaseNotes.ShouldShowWhatsNew("", current));
        Assert.True(ReleaseNotes.ShouldShowWhatsNew("   ", current));
        Assert.True(ReleaseNotes.ShouldShowWhatsNew("garbage", current));
        Assert.True(ReleaseNotes.ShouldShowWhatsNew("0.2.6", current));
        Assert.True(ReleaseNotes.ShouldShowWhatsNew("0.3.7", current));
        Assert.True(ReleaseNotes.ShouldShowWhatsNew("1.2.7", current));
        Assert.True(ReleaseNotes.ShouldShowWhatsNew("0.2", current));
    }

    [Fact]
    public void OP_E54_ShouldShowWhatsNew_False_For_The_Same_Three_Part_Version()
    {
        Assert.False(ReleaseNotes.ShouldShowWhatsNew("0.2.7", new Version(0, 2, 7)));
        Assert.False(ReleaseNotes.ShouldShowWhatsNew(" 0.2.7 ", new Version(0, 2, 7, 0)));
        Assert.False(ReleaseNotes.ShouldShowWhatsNew("0.2.7.5", new Version(0, 2, 7)));
        // Build -1 (a two-part version) counts as patch 0.
        Assert.False(ReleaseNotes.ShouldShowWhatsNew("1.4.0", new Version(1, 4)));
        Assert.False(ReleaseNotes.ShouldShowWhatsNew("1.4", new Version(1, 4)));
        Assert.True(ReleaseNotes.ShouldShowWhatsNew("1.4.1", new Version(1, 4)));
    }

    [Fact]
    public void OP_E54_VersionKey_Is_Three_Parts_And_Round_Trips()
    {
        Assert.Equal("0.2.7", ReleaseNotes.VersionKey(new Version(0, 2, 7)));
        Assert.Equal("0.2.7", ReleaseNotes.VersionKey(new Version(0, 2, 7, 9)));
        Assert.Equal("1.4.0", ReleaseNotes.VersionKey(new Version(1, 4)));
        var current = new Version(3, 10, 12);
        Assert.False(ReleaseNotes.ShouldShowWhatsNew(ReleaseNotes.VersionKey(current), current));
    }
}
