using Microsoft.Win32;

namespace OhMyPic.Core;

/// <summary>Which file extensions can be read (built-in WIC + installed WIC decoders) and written.</summary>
public static class ImageFormats
{
    /// <summary>Extensions WIC decodes out of the box on every supported Windows version.</summary>
    public static readonly IReadOnlyList<string> BuiltInReadableExtensions =
    [
        ".bmp", ".dib", ".gif", ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".tif", ".tiff", ".ico", ".jxr", ".hdp", ".wdp", ".dds",
    ];

    /// <summary>WIC component category "decoders" (CATID_WICBitmapDecoders).</summary>
    private const string DecoderCategory = "{7ED96837-96F0-4812-B211-F13C24117ED3}";

    private static readonly Lazy<IReadOnlyList<string>> Installed = new(DiscoverInstalledDecoderExtensions);
    private static readonly Lazy<HashSet<string>> ReadableSet = new(() =>
        new HashSet<string>(BuiltInReadableExtensions.Concat(Installed.Value), StringComparer.OrdinalIgnoreCase));
    private static readonly Lazy<IReadOnlyList<string>> ReadableList = new(() =>
        ReadableSet.Value.Select(e => e.ToLowerInvariant()).Distinct().OrderBy(e => e, StringComparer.Ordinal).ToList());

    /// <summary>Extensions (lower case, with dot) of the WIC decoders registered on this machine.</summary>
    public static IReadOnlyList<string> InstalledDecoderExtensions => Installed.Value;

    /// <summary>All readable extensions (lower case, with dot, sorted).</summary>
    public static IReadOnlyList<string> ReadableExtensions => ReadableList.Value;

    /// <summary>Writable formats in the order the UI offers them.</summary>
    public static readonly IReadOnlyList<ImageFormatKind> WritableFormats =
        [ImageFormatKind.Bmp, ImageFormatKind.Gif, ImageFormatKind.Jpeg, ImageFormatKind.Tiff, ImageFormatKind.Png];

    public static bool IsReadable(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Length > 1 && ReadableSet.Value.Contains(ext);
    }

    /// <summary>Writable format for an extension ("jpg" or ".jpg"), or null when the extension is not writable.</summary>
    public static ImageFormatKind? FromExtension(string extension)
    {
        string ext = extension.StartsWith('.') ? extension[1..] : extension;
        return ext.ToLowerInvariant() switch
        {
            "bmp" or "dib" => ImageFormatKind.Bmp,
            "gif" => ImageFormatKind.Gif,
            "jpg" or "jpeg" or "jpe" or "jfif" => ImageFormatKind.Jpeg,
            "tif" or "tiff" => ImageFormatKind.Tiff,
            "png" => ImageFormatKind.Png,
            _ => null,
        };
    }

    public static string DefaultExtension(ImageFormatKind kind) => kind switch
    {
        ImageFormatKind.Bmp => ".bmp",
        ImageFormatKind.Gif => ".gif",
        ImageFormatKind.Jpeg => ".jpg",
        ImageFormatKind.Tiff => ".tif",
        ImageFormatKind.Png => ".png",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>True when the format can hold more than one frame/page in one file.</summary>
    public static bool SupportsMultipleFrames(ImageFormatKind kind) => kind is ImageFormatKind.Tiff or ImageFormatKind.Gif;

    /// <summary>
    /// Reads HKCR\CLSID\{decoder category}\Instance\* → HKCR\CLSID\{codec}\FileExtensions.
    /// Never throws: any registry problem yields an empty list (built-in extensions still apply).
    /// </summary>
    private static IReadOnlyList<string> DiscoverInstalledDecoderExtensions()
    {
        var result = new SortedSet<string>(StringComparer.Ordinal);
        try
        {
            using RegistryKey? instances = Registry.ClassesRoot.OpenSubKey($@"CLSID\{DecoderCategory}\Instance");
            if (instances is null)
                return [];
            foreach (string name in instances.GetSubKeyNames())
            {
                try
                {
                    using RegistryKey? instance = instances.OpenSubKey(name);
                    string clsid = instance?.GetValue("CLSID") as string ?? name;
                    using RegistryKey? codec = Registry.ClassesRoot.OpenSubKey($@"CLSID\{clsid}");
                    if (codec?.GetValue("FileExtensions") is not string list)
                        continue;
                    foreach (string raw in list.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        string ext = raw.StartsWith('.') ? raw : "." + raw;
                        if (ext.Length > 1 && ext.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && ext.IndexOf('*') < 0)
                            result.Add(ext.ToLowerInvariant());
                    }
                }
                catch (Exception)
                {
                    // A broken codec registration must not hide the others.
                }
            }
        }
        catch (Exception)
        {
            // Discovery is best effort; the built-in list is always available.
            return [];
        }
        return result.ToList();
    }
}
