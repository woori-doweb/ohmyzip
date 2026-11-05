using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using OhMyPic.Core;

namespace OhMyPic.App;

/// <summary>
/// Update plumbing shared by the main window: one HttpClient, the work folder, the running version, and the update
/// downloaded in the background that is installed when the app exits.
/// </summary>
public partial class App
{
    /// <summary>The updater sets the User-Agent on every request, so the client needs no defaults.</summary>
    private static readonly Lazy<HttpClient> SharedHttp = new(() => new HttpClient());

    /// <summary>Settings of this run; null until the window mode started (command-line runs never install updates).</summary>
    private static AppSettings? _updateSettings;

    /// <summary>One client for the app's lifetime (update check and download).</summary>
    internal static HttpClient Http => SharedHttp.Value;

    /// <summary>Download and staging folder: %LOCALAPPDATA%\ohMypic\updates, or "updates" in OHMYPIC_DATA_DIR when that is set.</summary>
    internal static string UpdateWorkDir =>
        Environment.GetEnvironmentVariable(AppSettings.DataDirVariable) is { Length: > 0 }
            ? Path.Combine(AppSettings.DataDirectory, "updates")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ohMypic", "updates");

    /// <summary>Running version (major.minor.patch) from the informational version; compared with "ohmypic-vX.Y.Z" tags.</summary>
    internal static Version CurrentVersion { get; } = AppUpdater.VersionOf(Assembly.GetEntryAssembly() ?? typeof(App).Assembly);

    /// <summary>Staging folder and version downloaded by the automatic check; installed when the app exits.</summary>
    internal static (string StagedDir, Version Version)? PendingUpdate { get; set; }

    internal static AppUpdater CreateUpdater() => new(Http, AppContext.BaseDirectory, UpdateWorkDir);

    /// <summary>True in the normal window start; command-line runs neither download nor install in the background.</summary>
    internal static bool BackgroundUpdatesAllowed => _updateSettings is not null;

    /// <summary>
    /// Window mode start: remembers the settings for the exit-time install and removes what earlier updates left behind
    /// (renamed old files that are no longer in use, staging folders that are not newer than this version).
    /// </summary>
    private static void StartUpdateServices(AppSettings settings)
    {
        _updateSettings = settings;
        AppUpdater.CleanupLeftovers(AppContext.BaseDirectory);
        AppUpdater.CleanupWorkFolder(UpdateWorkDir, CurrentVersion);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        InstallPendingUpdate();
        base.OnExit(e);
    }

    /// <summary>
    /// Installs the update downloaded in the background. Never blocks the exit: Apply rolls back on failure and the next
    /// start downloads again. Skipped when automatic updates were turned off meanwhile, or when another window already
    /// installed the same or a newer version.
    /// </summary>
    internal static void InstallPendingUpdate()
    {
        if (PendingUpdate is not { } pending || _updateSettings is not { CheckForUpdates: true }) return;
        PendingUpdate = null;
        if (AppUpdater.InstalledVersion(AppContext.BaseDirectory) is { } installed && installed >= pending.Version) return;
        try
        {
            CreateUpdater().Apply(pending.StagedDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Exiting: nowhere to report. The files were put back; the next start checks again.
        }
    }
}
