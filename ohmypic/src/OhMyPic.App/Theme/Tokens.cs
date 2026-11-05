namespace OhMyPic.App.Theme;

/// <summary>
/// One set of colour tokens. Every value is opaque #RRGGBB. Contrast checks (OP-A02) read <see cref="Tokens.ContrastPairs"/>.
/// </summary>
public sealed record Palette(
    string Name,
    string Background,     // thumbnail area and dialog bodies (opaque)
    string Text,           // body text
    string SecondaryText,  // secondary text (12px)
    string DisabledText,   // disabled menu/button text. Disabled UI components are exempt from WCAG 1.4.3, so not in ContrastPairs
    string Chrome,         // toolbar, menu and status bar surface (solid)
    string Sidebar,        // folder tree surface (solid, slightly tinted)
    string Border,         // separators (decorative, not text)
    string Accent,         // primary action and focus
    string AccentHover,    // primary action on hover
    string OnAccent,       // text on accent
    string Selection,      // selected item background
    string OnSelection,    // selected item text
    string Hover,          // hover background
    string Error);         // error icon and text (never colour alone: icon + text)

public static class Tokens
{
    public static readonly Palette Light = new(
        Name: "Light",
        Background: "#FFFFFF",
        Text: "#1B1B1F",
        SecondaryText: "#5E5E66",
        DisabledText: "#9A9AA2",
        Chrome: "#F3F4F6",
        Sidebar: "#EEF0F3",
        Border: "#D4D6DB",
        Accent: "#005FB8",
        AccentHover: "#004C93",
        OnAccent: "#FFFFFF",
        Selection: "#CFE3F7",
        OnSelection: "#1B1B1F",
        Hover: "#E6E8EC",
        Error: "#C42B1C");

    public static readonly Palette Dark = new(
        Name: "Dark",
        Background: "#1C1C1E",
        Text: "#F2F2F5",
        SecondaryText: "#A9A9B2",
        DisabledText: "#6E6E76",
        Chrome: "#2A2A2D",
        Sidebar: "#252528",
        Border: "#3E3E43",
        Accent: "#4CB8F5",
        AccentHover: "#7ACBF8",
        OnAccent: "#0B0B0C",
        Selection: "#0F4C75",
        OnSelection: "#F2F2F5",
        Hover: "#36363A",
        Error: "#FF9C94");

    /// <summary>
    /// (foreground, background) pairs used for text and glyphs. Each must reach 4.5:1.
    /// DisabledText is excluded (disabled components are exempt from the WCAG contrast requirement).
    /// </summary>
    public static IEnumerable<(string Label, string Foreground, string Background)> ContrastPairs(Palette p)
    {
        yield return ("Text/Background", p.Text, p.Background);
        yield return ("SecondaryText/Background", p.SecondaryText, p.Background);
        yield return ("Text/Chrome", p.Text, p.Chrome);
        yield return ("SecondaryText/Chrome", p.SecondaryText, p.Chrome);
        yield return ("Text/Sidebar", p.Text, p.Sidebar);
        yield return ("SecondaryText/Sidebar", p.SecondaryText, p.Sidebar);
        yield return ("Text/Hover", p.Text, p.Hover);
        yield return ("SecondaryText/Hover", p.SecondaryText, p.Hover);
        yield return ("OnAccent/Accent", p.OnAccent, p.Accent);
        yield return ("OnAccent/AccentHover", p.OnAccent, p.AccentHover);
        yield return ("OnSelection/Selection", p.OnSelection, p.Selection);
        yield return ("Error/Background", p.Error, p.Background);
        yield return ("Accent/Background", p.Accent, p.Background); // keyboard focus ring, progress bar
        yield return ("Accent/Chrome", p.Accent, p.Chrome);
    }

    /// <summary>WCAG 2.x contrast ratio.</summary>
    public static double ContrastRatio(string foregroundHex, string backgroundHex)
    {
        double a = RelativeLuminance(foregroundHex), b = RelativeLuminance(backgroundHex);
        double hi = Math.Max(a, b), lo = Math.Min(a, b);
        return (hi + 0.05) / (lo + 0.05);
    }

    public static double RelativeLuminance(string hex)
    {
        var (r, g, b) = Parse(hex);
        static double Lin(byte c)
        {
            double s = c / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Lin(r) + 0.7152 * Lin(g) + 0.0722 * Lin(b);
    }

    public static (byte R, byte G, byte B) Parse(string hex)
    {
        if (!TryParse(hex, out var rgb)) throw new FormatException(string.Format(OhMyPic.App.Text.MainText.Token_InvalidHex, hex));
        return rgb;
    }

    /// <summary>Parses "#RRGGBB" (the leading # is optional).</summary>
    public static bool TryParse(string? hex, out (byte R, byte G, byte B) rgb)
    {
        rgb = default;
        if (hex is null) return false;
        var h = hex.Trim().TrimStart('#');
        if (h.Length != 6) return false;
        if (!byte.TryParse(h[..2], System.Globalization.NumberStyles.HexNumber, null, out var r)
            || !byte.TryParse(h[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g)
            || !byte.TryParse(h[4..6], System.Globalization.NumberStyles.HexNumber, null, out var b))
            return false;
        rgb = (r, g, b);
        return true;
    }
}
