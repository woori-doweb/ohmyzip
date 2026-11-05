namespace OhMyPic.Core;

/// <summary>Where an edit result goes and what it is called.</summary>
public static class OutputPlanner
{
    /// <summary>Output folder for <paramref name="input"/> (not created here).</summary>
    /// <exception cref="ArgumentException">Specified mode without a folder path, or an invalid sub-folder name.</exception>
    public static string ResolveFolder(string input, OutputOptions output)
    {
        string inputFolder = Path.GetDirectoryName(Path.GetFullPath(input)) ?? throw new ArgumentException("Input has no folder.", nameof(input));
        switch (output.Folder)
        {
            case OutputFolderMode.SameFolder:
                return inputFolder;
            case OutputFolderMode.SubFolder:
                RenameRule.Validate(output.SubFolderName);
                return Path.Combine(inputFolder, output.SubFolderName);
            case OutputFolderMode.Specified:
                if (string.IsNullOrWhiteSpace(output.FolderPath))
                    throw new ArgumentException("An output folder must be specified.", nameof(output));
                return Path.GetFullPath(output.FolderPath);
            default:
                throw new ArgumentOutOfRangeException(nameof(output));
        }
    }

    /// <summary>
    /// File name (with extension) for the item at 0-based <paramref name="index"/>: rename rule (if any), then
    /// prefix/suffix, then extension. <paramref name="frameNumber"/> (1-based) adds <c>_n</c> for split frames.
    /// </summary>
    public static string BuildFileName(string input, int index, DateTime date, OutputOptions output, RenameRule? rename, string extension, int? frameNumber = null)
    {
        string stem = Path.GetFileNameWithoutExtension(input);
        if (rename is not null)
            stem = rename.Apply(stem, index, date);
        string name = (output.Prefix ?? "") + stem + (output.Suffix ?? "");
        if (frameNumber is { } n)
            name += "_" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        RenameRule.Validate(name);
        return name + extension;
    }

    /// <summary>
    /// Output extension: the converted format's default, else the input's own extension when that format is writable,
    /// else ".png".
    /// </summary>
    public static string ResolveExtension(string input, ConvertOptions? convert)
    {
        if (convert is not null)
            return ImageFormats.DefaultExtension(convert.Format);
        string ext = Path.GetExtension(input);
        return ImageFormats.FromExtension(ext) is not null ? ext : ImageFormats.DefaultExtension(ImageFormatKind.Png);
    }

    /// <summary>First free <c>name (n).ext</c> (n ≥ 2) that does not exist and is not in <paramref name="reserved"/>.</summary>
    public static string MakeUnique(string path, ISet<string>? reserved = null)
    {
        string folder = Path.GetDirectoryName(path) ?? "";
        string stem = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int n = 2; ; n++)
        {
            string candidate = Path.Combine(folder, $"{stem} ({n}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate) && (reserved is null || !reserved.Contains(candidate)))
                return candidate;
        }
    }
}
