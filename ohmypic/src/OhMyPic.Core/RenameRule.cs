using System.Globalization;
using System.Text.RegularExpressions;

namespace OhMyPic.Core;

/// <summary>
/// File-name template. Tokens (case-insensitive): <c>[%N]</c> = StartNumber + index padded to <see cref="Digits"/>,
/// <c>[%F]</c> = original name, <c>[%D]</c> = date as yyyyMMdd. Applied in order: tokens → Find/Replace (ordinal) → Case.
/// </summary>
public sealed partial record RenameRule(
    string Template = "[%N]",
    int StartNumber = 1,
    int Digits = 0,
    NameCase Case = NameCase.Keep,
    string? Find = null,
    string? Replace = null)
{
    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    /// <summary>New name (without extension) for the item at 0-based <paramref name="index"/>.</summary>
    /// <exception cref="ArgumentException">The result is empty, a reserved name, or contains invalid file-name characters.</exception>
    public string Apply(string originalNameWithoutExt, int index, DateTime date)
    {
        ArgumentNullException.ThrowIfNull(originalNameWithoutExt);
        ArgumentOutOfRangeException.ThrowIfNegative(Digits);
        long number = (long)StartNumber + index;
        string numberText = Digits > 0
            ? (number < 0 ? "-" + Math.Abs(number).ToString("D" + Digits, CultureInfo.InvariantCulture) : number.ToString("D" + Digits, CultureInfo.InvariantCulture))
            : number.ToString(CultureInfo.InvariantCulture);
        string name = TokenPattern().Replace(Template ?? "", m => char.ToUpperInvariant(m.Groups[1].Value[0]) switch
        {
            'N' => numberText,
            'F' => originalNameWithoutExt,
            _ => date.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
        });
        if (!string.IsNullOrEmpty(Find))
            name = name.Replace(Find, Replace ?? "", StringComparison.Ordinal);
        name = Case switch
        {
            NameCase.Lower => name.ToLowerInvariant(),
            NameCase.Upper => name.ToUpperInvariant(),
            _ => name,
        };
        Validate(name);
        return name;
    }

    /// <summary>Throws <see cref="ArgumentException"/> unless <paramref name="name"/> is usable as a file name.</summary>
    public static void Validate(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("The file name is empty.", nameof(name));
        int bad = name.IndexOfAny(InvalidChars);
        if (bad >= 0)
            throw new ArgumentException($"The file name contains an invalid character '{name[bad]}'.", nameof(name));
        if (name is "." or ".." || name.EndsWith('.') || name.EndsWith(' '))
            throw new ArgumentException("The file name must not end with a dot or a space.", nameof(name));
        string stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || ReservedDevice().IsMatch(stem))
            throw new ArgumentException($"'{name}' is a reserved device name.", nameof(name));
    }

    [GeneratedRegex(@"\[%([NFDnfd])\]")]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"^(COM|LPT)[1-9]$")]
    private static partial Regex ReservedDevice();
}
