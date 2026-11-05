using System.IO;
using System.Text.Json;
using OhMyPic.Core;

namespace OhMyPic.App.Settings;

/// <summary>
/// Window placement and pane toggles. These are app-only state (not part of the engine's AppSettings), kept next to
/// settings.json as window.json. Reading never throws (defaults on any problem); writing is temp file + atomic replace.
/// </summary>
public sealed class UiState
{
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 1100;
    public double Height { get; set; } = 720;
    public bool Maximized { get; set; }
    public bool ShowFolderTree { get; set; } = true;
    public double FolderTreeWidth { get; set; } = 240;
    public bool ShowPreviewPane { get; set; }
    public bool ShowCameraPane { get; set; }

    /// <summary>When the automatic update check last finished without finding a new version (checks run at most once a day).</summary>
    public DateTimeOffset? LastUpdateCheck { get; set; }

    // Left/Top are NaN until the window was placed once; NaN is not plain JSON, so allow the named literals.
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static string DefaultPath => Path.Combine(AppSettings.DataDirectory, "window.json");

    public static UiState Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new UiState();
            return JsonSerializer.Deserialize<UiState>(File.ReadAllText(path), Json) ?? new UiState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new UiState();
        }
    }

    /// <summary>Best effort: window state is not worth an alert, so IO errors are swallowed here.</summary>
    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or JsonException or ArgumentException)
        {
        }
    }
}
