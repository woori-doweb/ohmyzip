using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OhMyPic.Core;

public enum WheelAction
{
    NextPrevious,
    Zoom,
}

public enum RotateAutoSaveMode
{
    Ask,
    Overwrite,
}

public enum FitMode
{
    ShrinkLargeOnly,
    FitWindow,
    FitWidth,
    FitHeight,
    ActualSize,
    KeepZoom,
}

public enum ViewFormat
{
    Thumbnails,
    LargeIcons,
    Icons,
    List,
    Details,
    Filmstrip,
}

/// <summary>User settings stored as JSON. <see cref="Load"/> never throws; <see cref="Save"/> writes atomically.</summary>
public sealed partial class AppSettings
{
    public const int MinThumbnailSize = 64;
    public const int MaxThumbnailSize = 320;
    public const string SystemColor = "System";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    // General
    public bool DeleteWithoutAsking { get; set; }
    public bool ShowHiddenFiles { get; set; }
    public bool UseRecycleBin { get; set; } = true;
    public WheelAction WheelAction { get; set; } = WheelAction.NextPrevious;
    public RotateAutoSaveMode RotateAutoSave { get; set; } = RotateAutoSaveMode.Ask;
    public bool ApplyLastSaveQuality { get; set; }

    /// <summary>Check GitHub for a new ohMypic release at most once a day and install it when the app closes.</summary>
    public bool CheckForUpdates { get; set; } = true;

    // View
    public bool LoopAtEnds { get; set; } = true;
    public bool AlwaysOnTop { get; set; }
    public bool AutoRotateByExif { get; set; } = true;
    public bool SmoothScaling { get; set; } = true;
    public bool ShowFileNameInFullScreen { get; set; }
    public bool ShowNavigator { get; set; } = true;
    public FitMode FitMode { get; set; } = FitMode.ShrinkLargeOnly;

    /// <summary>"System" or "#RRGGBB".</summary>
    public string BackgroundColor { get; set; } = SystemColor;

    // Slideshow
    public int SlideIntervalSeconds { get; set; } = SlideshowSequence.DefaultIntervalSeconds;
    public bool SlideFade { get; set; } = true;

    // Thumbnails
    public int ThumbnailSize { get; set; } = 150;
    public bool ShowResolutionOnThumbnail { get; set; }

    // App
    /// <summary>"system", "en" or "ko".</summary>
    public string Language { get; set; } = "system";
    public ViewFormat ViewFormat { get; set; } = ViewFormat.Thumbnails;
    public SortKey SortKey { get; set; } = SortKey.Name;
    public bool SortDescending { get; set; }
    public string? LastFolder { get; set; }
    public int JpegQuality { get; set; } = 85;

    /// <summary>Why the last <see cref="Load"/> fell back to defaults (missing or unreadable file); null on success.</summary>
    [JsonIgnore]
    public string? LoadError { get; private set; }

    /// <summary>Environment variable that moves all app data (settings, window state, updates) to another folder; used by tests.</summary>
    public const string DataDirVariable = "OHMYPIC_DATA_DIR";

    /// <summary>The OHMYPIC_DATA_DIR folder when set, otherwise %APPDATA%\ohMypic.</summary>
    public static string DataDirectory =>
        Environment.GetEnvironmentVariable(DataDirVariable) is { Length: > 0 } dir
            ? Path.GetFullPath(dir)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ohMypic");

    /// <summary>settings.json in <see cref="DataDirectory"/>.</summary>
    public static string DefaultPath => Path.Combine(DataDirectory, "settings.json");

    public static AppSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new AppSettings { LoadError = $"Settings file not found: {path}" };
            AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json);
            if (loaded is null)
                return new AppSettings { LoadError = "Settings file is empty." };
            loaded.Normalize();
            return loaded;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return new AppSettings { LoadError = $"Settings file could not be read: {ex.Message}" };
        }
    }

    public void Save(string path)
    {
        Normalize();
        string? folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (folder is not null)
            Directory.CreateDirectory(folder);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>Brings out-of-range values back into range.</summary>
    public void Normalize()
    {
        SlideIntervalSeconds = SlideshowSequence.IntervalSeconds(SlideIntervalSeconds);
        ThumbnailSize = Math.Clamp(ThumbnailSize, MinThumbnailSize, MaxThumbnailSize);
        JpegQuality = Math.Clamp(JpegQuality, 1, 100);
        Language = Language?.ToLowerInvariant() is "en" or "ko" ? Language.ToLowerInvariant() : "system";
        if (BackgroundColor is null || !(string.Equals(BackgroundColor, SystemColor, StringComparison.OrdinalIgnoreCase) || HexColor().IsMatch(BackgroundColor)))
            BackgroundColor = SystemColor;
        else if (string.Equals(BackgroundColor, SystemColor, StringComparison.OrdinalIgnoreCase))
            BackgroundColor = SystemColor;
        if (!Enum.IsDefined(WheelAction))
            WheelAction = WheelAction.NextPrevious;
        if (!Enum.IsDefined(RotateAutoSave))
            RotateAutoSave = RotateAutoSaveMode.Ask;
        if (!Enum.IsDefined(FitMode))
            FitMode = FitMode.ShrinkLargeOnly;
        if (!Enum.IsDefined(ViewFormat))
            ViewFormat = ViewFormat.Thumbnails;
        if (!Enum.IsDefined(SortKey))
            SortKey = SortKey.Name;
    }

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColor();
}
