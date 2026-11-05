namespace OhMyPic.Core;

/// <summary>Lists readable images in one folder (not recursive).</summary>
public static class FolderScanner
{
    /// <summary>
    /// Readable images sorted by <paramref name="key"/>. Ties (and the Name key) use natural, case-insensitive name
    /// order, so <c>file2</c> comes before <c>file10</c>. Hidden/system files are left out unless requested.
    /// </summary>
    public static IReadOnlyList<ImageEntry> List(string folder, SortKey key, bool descending, bool includeHidden)
    {
        var entries = new List<ImageEntry>();
        foreach (FileInfo file in new DirectoryInfo(folder).EnumerateFiles())
        {
            if (!ImageFormats.IsReadable(file.Name))
                continue;
            if (!includeHidden && (file.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                continue;
            entries.Add(new ImageEntry(file.FullName, file.Name, file.Extension.ToLowerInvariant(), file.Length, file.LastWriteTime));
        }
        entries.Sort((a, b) => Compare(a, b, key, descending));
        return entries;
    }

    public static int Compare(ImageEntry a, ImageEntry b, SortKey key, bool descending)
    {
        int primary = key switch
        {
            SortKey.Name => NaturalStringComparer.Instance.Compare(a.Name, b.Name),
            SortKey.Type => string.Compare(a.Extension, b.Extension, StringComparison.OrdinalIgnoreCase),
            SortKey.Size => a.Size.CompareTo(b.Size),
            SortKey.Date => a.Modified.CompareTo(b.Modified),
            _ => 0,
        };
        if (descending)
            primary = -primary;
        if (primary != 0)
            return primary;
        int byName = NaturalStringComparer.Instance.Compare(a.Name, b.Name);
        if (key == SortKey.Name && descending)
            byName = -byName;
        return byName != 0 ? byName : string.CompareOrdinal(a.Name, b.Name);
    }
}

/// <summary>Case-insensitive comparison where digit runs compare by numeric value ("file2" &lt; "file10").</summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        if (x is null)
            return -1;
        if (y is null)
            return 1;
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                int si = i, sj = j;
                while (i < x.Length && char.IsAsciiDigit(x[i]))
                    i++;
                while (j < y.Length && char.IsAsciiDigit(y[j]))
                    j++;
                ReadOnlySpan<char> a = x.AsSpan(si, i - si).TrimStart('0');
                ReadOnlySpan<char> b = y.AsSpan(sj, j - sj).TrimStart('0');
                if (a.Length != b.Length)
                    return a.Length.CompareTo(b.Length);
                int digits = a.SequenceCompareTo(b);
                if (digits != 0)
                    return Math.Sign(digits);
                // Equal value: fewer leading zeros first.
                int zeros = (i - si).CompareTo(j - sj);
                if (zeros != 0)
                    return zeros;
                continue;
            }
            int c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
            if (c != 0)
                return c;
            i++;
            j++;
        }
        return (x.Length - i).CompareTo(y.Length - j);
    }
}
