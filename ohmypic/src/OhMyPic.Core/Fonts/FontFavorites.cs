using System.Text.Json;

namespace OhMyPic.Core;

/// <summary>
/// Favourite font family names in the order they were added (no duplicates, compared case-insensitively like Windows family names),
/// stored as a JSON array of strings. <see cref="Load(string)"/> never throws; <see cref="Save"/> writes atomically.
/// </summary>
public sealed class FontFavorites
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly List<string> _items = [];

    /// <summary>font-favorites.json in <see cref="AppSettings.DataDirectory"/>.</summary>
    public static string DefaultPath => Path.Combine(AppSettings.DataDirectory, "font-favorites.json");

    /// <summary>Empty list bound to <see cref="DefaultPath"/>.</summary>
    public FontFavorites()
        : this(DefaultPath)
    {
    }

    /// <summary>Empty list bound to <paramref name="filePath"/> (nothing is read).</summary>
    public FontFavorites(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = filePath;
    }

    /// <summary>Where <see cref="Save"/> writes.</summary>
    public string FilePath { get; }

    public IReadOnlyList<string> Items => _items;

    /// <summary>Reads <see cref="DefaultPath"/>.</summary>
    public static FontFavorites Load() => Load(DefaultPath);

    /// <summary>Favourites stored at <paramref name="filePath"/>; empty when the file is missing, unreadable or not a JSON array of strings.
    /// Blank and duplicate names in the file are dropped.</summary>
    public static FontFavorites Load(string filePath)
    {
        var favorites = new FontFavorites(filePath);
        try
        {
            if (!File.Exists(filePath))
                return favorites;
            string[]? names = JsonSerializer.Deserialize<string[]>(File.ReadAllText(filePath), Json);
            if (names is null)
                return favorites;
            foreach (string? name in names)
            {
                if (!string.IsNullOrWhiteSpace(name))
                    favorites.Add(name);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            favorites._items.Clear();
        }
        return favorites;
    }

    public bool Contains(string familyName) => IndexOf(familyName) >= 0;

    /// <summary>Appends the name; false when it is already a favourite (the existing position is kept).</summary>
    public bool Add(string familyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(familyName);
        string name = familyName.Trim();
        if (IndexOf(name) >= 0)
            return false;
        _items.Add(name);
        return true;
    }

    /// <summary>False when the name was not a favourite.</summary>
    public bool Remove(string familyName)
    {
        int index = IndexOf(familyName);
        if (index < 0)
            return false;
        _items.RemoveAt(index);
        return true;
    }

    /// <summary>Writes the list to <see cref="FilePath"/> (creating its folder) through a temp file and an atomic replace.</summary>
    public void Save()
    {
        string? folder = Path.GetDirectoryName(Path.GetFullPath(FilePath));
        if (folder is not null)
            Directory.CreateDirectory(folder);
        AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(_items, Json));
    }

    private int IndexOf(string? familyName)
    {
        if (string.IsNullOrWhiteSpace(familyName))
            return -1;
        string name = familyName.Trim();
        return _items.FindIndex(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));
    }
}
