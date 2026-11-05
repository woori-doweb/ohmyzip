using System.Globalization;
using System.Windows.Media.Imaging;

namespace OhMyPic.Core;

/// <summary>EXIF reading and copying through WIC metadata queries.</summary>
internal static class MetadataTools
{
    private const ushort OrientationTag = 274;

    private sealed record Tag(string Key, bool InExif, ushort Id, string Policy);

    // Tags copied between JPEG and TIFF. DateTaken = DateTimeOriginal.
    private static readonly Tag[] CameraTags =
    [
        new(MetadataKeys.CameraMake, false, 271, "System.Photo.CameraManufacturer"),
        new(MetadataKeys.CameraModel, false, 272, "System.Photo.CameraModel"),
        new(MetadataKeys.DateTaken, true, 36867, "System.Photo.DateTaken"),
        new(MetadataKeys.ExposureTime, true, 33434, "System.Photo.ExposureTime"),
        new(MetadataKeys.FNumber, true, 33437, "System.Photo.FNumber"),
        new(MetadataKeys.IsoSpeed, true, 34855, "System.Photo.ISOSpeed"),
        new(MetadataKeys.FocalLength, true, 37386, "System.Photo.FocalLength"),
    ];

    /// <summary>IFD0 root query for a WIC metadata container format, or null when the container has no TIFF IFD.</summary>
    private static string? IfdRoot(string? format) => format switch
    {
        "jpg" => "/app1/ifd",
        "tiff" => "/ifd",
        _ => null,
    };

    private static string TagQuery(string root, Tag tag) => tag.InExif ? $"{root}/exif/{{ushort={tag.Id}}}" : $"{root}/{{ushort={tag.Id}}}";

    public static BitmapMetadata? TryGetMetadata(BitmapSource frame)
    {
        try
        {
            return frame.Metadata as BitmapMetadata;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    public static string? TryFormat(BitmapMetadata metadata)
    {
        try
        {
            return metadata.Format;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    public static object? TryQuery(BitmapMetadata metadata, string query)
    {
        try
        {
            // GetQuery returns null for a missing item; it also resolves System.Photo.* policy names.
            return metadata.GetQuery(query);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private static bool TrySet(BitmapMetadata metadata, string query, object value)
    {
        try
        {
            metadata.SetQuery(query, value);
            return true;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    public static int ReadOrientation(BitmapMetadata? metadata)
    {
        if (metadata is null)
            return 1;
        string? root = IfdRoot(TryFormat(metadata));
        object? value = root is not null ? TryQuery(metadata, $"{root}/{{ushort={OrientationTag}}}") : null;
        value ??= TryQuery(metadata, "System.Photo.Orientation");
        int orientation = value switch
        {
            ushort u => u,
            short s => s,
            uint ui => (int)ui,
            int i => i,
            ushort[] arr when arr.Length > 0 => arr[0],
            _ => 1,
        };
        return orientation is >= 1 and <= 8 ? orientation : 1;
    }

    public static IReadOnlyList<MetadataItem> ReadCamera(BitmapMetadata? metadata)
    {
        if (metadata is null)
            return [];
        string? root = IfdRoot(TryFormat(metadata));
        var items = new List<MetadataItem>();
        foreach (Tag tag in CameraTags)
        {
            object? raw = root is not null ? TryQuery(metadata, TagQuery(root, tag)) : null;
            string? text = raw is not null ? FormatTag(tag.Key, raw) : FormatPolicy(tag.Key, TryQuery(metadata, tag.Policy));
            if (!string.IsNullOrWhiteSpace(text))
                items.Add(new MetadataItem(tag.Key, text));
        }
        return items;
    }

    /// <summary>
    /// Metadata to embed in a <paramref name="target"/> file, or null when the format keeps no EXIF.
    /// JPEG → JPEG keeps the whole block (EXIF, XMP, IPTC); any other pairing copies the camera tags.
    /// </summary>
    public static BitmapMetadata? CopyFor(BitmapMetadata? source, ImageFormatKind target, bool resetOrientation, int width, int height)
    {
        if (source is null || target is not (ImageFormatKind.Jpeg or ImageFormatKind.Tiff))
            return null;
        string targetFormat = target == ImageFormatKind.Jpeg ? "jpg" : "tiff";
        string targetRoot = IfdRoot(targetFormat)!;
        string? sourceFormat = TryFormat(source);
        BitmapMetadata result;
        if (sourceFormat == "jpg" && target == ImageFormatKind.Jpeg)
        {
            result = source.Clone();
            UpdateDimensions(result, targetRoot, width, height);
            if (resetOrientation)
            {
                // The embedded EXIF thumbnail shows the old orientation; drop it rather than ship a wrong preview.
                try
                {
                    if (result.ContainsQuery("/app1/thumb"))
                        result.RemoveQuery("/app1/thumb");
                }
                catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException)
                {
                }
            }
        }
        else
        {
            result = new BitmapMetadata(targetFormat);
            string? sourceRoot = IfdRoot(sourceFormat);
            bool any = false;
            foreach (Tag tag in CameraTags)
            {
                object? value = sourceRoot is not null ? TryQuery(source, TagQuery(sourceRoot, tag)) : null;
                if (value is not null && TrySet(result, TagQuery(targetRoot, tag), value))
                    any = true;
            }
            int orientation = ReadOrientation(source);
            if (!resetOrientation && orientation != 1)
            {
                TrySet(result, $"{targetRoot}/{{ushort={OrientationTag}}}", (ushort)orientation);
                any = true;
            }
            if (!any)
                return null;
        }
        if (resetOrientation)
        {
            TrySet(result, $"{targetRoot}/{{ushort={OrientationTag}}}", (ushort)1);
            if (TryQuery(result, "/xmp/tiff:Orientation") is not null)
                TrySet(result, "/xmp/tiff:Orientation", "1");
        }
        result.Freeze();
        return result;
    }

    private static void UpdateDimensions(BitmapMetadata metadata, string root, int width, int height)
    {
        foreach (var (id, value) in new[] { (40962, width), (40963, height) })
        {
            string query = $"{root}/exif/{{ushort={id}}}";
            switch (TryQuery(metadata, query))
            {
                case ushort when value <= ushort.MaxValue:
                    TrySet(metadata, query, (ushort)value);
                    break;
                case ushort:
                case uint:
                    TrySet(metadata, query, (uint)value);
                    break;
            }
        }
    }

    private static string? FormatTag(string key, object raw)
    {
        switch (key)
        {
            case MetadataKeys.CameraMake:
            case MetadataKeys.CameraModel:
                return AsText(raw);
            case MetadataKeys.DateTaken:
            {
                string? text = AsText(raw);
                return text is not null && DateTime.TryParseExact(text, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
                    ? dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                    : text;
            }
            case MetadataKeys.ExposureTime:
                return Rational(raw) is { } exposure ? FormatExposure(exposure.Num, exposure.Den) : null;
            case MetadataKeys.FNumber:
                return Rational(raw) is { Den: > 0 } f ? "f/" + (f.Num / (double)f.Den).ToString("0.#", CultureInfo.InvariantCulture) : null;
            case MetadataKeys.FocalLength:
                return Rational(raw) is { Den: > 0 } fl ? (fl.Num / (double)fl.Den).ToString("0.#", CultureInfo.InvariantCulture) + " mm" : null;
            case MetadataKeys.IsoSpeed:
                return raw switch
                {
                    ushort u => u.ToString(CultureInfo.InvariantCulture),
                    ushort[] arr when arr.Length > 0 => arr[0].ToString(CultureInfo.InvariantCulture),
                    uint ui => ui.ToString(CultureInfo.InvariantCulture),
                    _ => null,
                };
            default:
                return null;
        }
    }

    private static string? FormatPolicy(string key, object? raw)
    {
        switch (raw)
        {
            case null:
                return null;
            case string s:
                return AsText(s);
            case System.Runtime.InteropServices.ComTypes.FILETIME ft:
            {
                long ticks = ((long)(uint)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;
                return DateTime.FromFileTime(ticks).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }
            case DateTime dt:
                return dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            case double d when key == MetadataKeys.ExposureTime:
                return d > 0 && d < 1 ? "1/" + Math.Round(1 / d).ToString(CultureInfo.InvariantCulture) : d.ToString("0.#", CultureInfo.InvariantCulture);
            case double d when key == MetadataKeys.FNumber:
                return "f/" + d.ToString("0.#", CultureInfo.InvariantCulture);
            case double d when key == MetadataKeys.FocalLength:
                return d.ToString("0.#", CultureInfo.InvariantCulture) + " mm";
            case IFormattable f:
                return f.ToString(null, CultureInfo.InvariantCulture);
            default:
                return null;
        }
    }

    private static string? AsText(object raw)
    {
        string? text = raw switch
        {
            string s => s,
            string[] arr when arr.Length > 0 => arr[0],
            _ => null,
        };
        text = text?.Trim('\0', ' ');
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>WIC returns TIFF RATIONAL as a 64-bit value: numerator in the low 32 bits, denominator in the high 32 bits.</summary>
    private static (uint Num, uint Den)? Rational(object raw) => raw switch
    {
        ulong v => ((uint)(v & 0xFFFFFFFF), (uint)(v >> 32)),
        long v => ((uint)(v & 0xFFFFFFFF), (uint)((ulong)v >> 32)),
        _ => null,
    };

    private static string? FormatExposure(uint num, uint den)
    {
        if (den == 0)
            return null;
        if (num > 0 && num < den)
            return "1/" + Math.Round(den / (double)num).ToString(CultureInfo.InvariantCulture);
        return (num / (double)den).ToString("0.#", CultureInfo.InvariantCulture);
    }
}
