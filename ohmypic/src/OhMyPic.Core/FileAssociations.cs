using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace OhMyPic.Core;

/// <summary>Registration state of one extension for the given executable.</summary>
public enum FileAssociationStatus
{
    /// <summary>The extension does not list the ohMypic ProgID (or the ProgID has no open command).</summary>
    NotRegistered,
    /// <summary>The extension lists the ohMypic ProgID and its open command points to this executable.</summary>
    Registered,
    /// <summary>The extension lists the ohMypic ProgID but the open command points to another executable (moved install).</summary>
    RegisteredElsewhere,
}

/// <summary>
/// Current-user registration that lets the user pick ohMypic as the default app for image files in Windows Settings
/// (Default apps). Windows 10/11 do not let a program change the user's choice (UserChoice), so this only registers
/// ohMypic as a candidate; the extension key's default value is never written. All paths are relative to the given root
/// (HKCU in the app, a temporary subkey in tests).
/// </summary>
public static class FileAssociations
{
    public const string AppName = "ohMypic";
    public const string ProgId = "ohMypic.Image";
    public const string ExeName = "ohMypic.exe";

    /// <summary>Windows Settings page where the user chooses default apps (opens on the ohMypic entry when supported).</summary>
    public const string SettingsUri = "ms-settings:defaultapps?registeredAppUser=ohMypic";

    public const string ClassesPath = @"Software\Classes";
    public const string ProgIdPath = ClassesPath + @"\" + ProgId;
    public const string ApplicationsPath = ClassesPath + @"\Applications";
    public const string ApplicationPath = ApplicationsPath + @"\" + ExeName;
    public const string AppRootPath = @"Software\" + AppName;
    public const string CapabilitiesPath = AppRootPath + @"\Capabilities";
    public const string RegisteredApplicationsPath = @"Software\RegisteredApplications";

    /// <summary>Extensions decoded by a codec that ships with every supported Windows version.</summary>
    public static IReadOnlyList<string> BuiltInExtensions { get; } =
        [".bmp", ".dib", ".gif", ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".tif", ".tiff", ".ico", ".wdp", ".jxr"];

    /// <summary>Extensions offered only when the matching optional Windows codec is installed.</summary>
    public static IReadOnlyList<string> OptionalExtensions { get; } = [".webp", ".heic", ".heif", ".avif"];

    /// <summary>Every extension ohMypic may ever register. Unregister cleans all of them.</summary>
    public static IReadOnlyList<string> CandidateExtensions { get; } = [.. BuiltInExtensions, .. OptionalExtensions];

    /// <summary>Built-in extensions plus the optional ones whose decoder is listed in <paramref name="installedDecoderExtensions"/>.</summary>
    public static IReadOnlyList<string> AvailableExtensions(IEnumerable<string> installedDecoderExtensions)
    {
        ArgumentNullException.ThrowIfNull(installedDecoderExtensions);
        var installed = new HashSet<string>(installedDecoderExtensions, StringComparer.OrdinalIgnoreCase);
        return [.. BuiltInExtensions, .. OptionalExtensions.Where(installed.Contains)];
    }

    /// <summary>Extensions this machine can open: built-in plus optional ones with an installed WIC decoder.</summary>
    public static IReadOnlyList<string> AvailableExtensions() => AvailableExtensions(ImageFormats.InstalledDecoderExtensions);

    public static string ExtensionPath(string extension) => $@"{ClassesPath}\{extension}";

    public static string OpenWithProgidsPath(string extension) => ExtensionPath(extension) + @"\OpenWithProgids";

    /// <summary>Open command: "exe" "%1".</summary>
    public static string OpenCommand(string exePath) => $"\"{exePath}\" \"%1\"";

    /// <summary>Icon reference: "exe",0.</summary>
    public static string IconValue(string exePath) => $"\"{exePath}\",0";

    /// <summary>
    /// Registers ohMypic for exactly <paramref name="extensions"/> (each must be in <see cref="CandidateExtensions"/>):
    /// ProgID, Applications entry, Capabilities, RegisteredApplications and OpenWithProgids per extension. Candidate
    /// extensions not in the set lose their ohMypic entries, so calling again with another set replaces the selection.
    /// <paramref name="typeName"/> and <paramref name="appDescription"/> are the localized texts shown by Windows.
    /// </summary>
    public static void Register(RegistryKey root, string exePath, IEnumerable<string> extensions, string typeName, string appDescription)
    {
        ArgumentNullException.ThrowIfNull(root);
        ValidateExePath(exePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(appDescription);
        var selected = NormalizeExtensions(extensions);
        if (selected.Count == 0)
            throw new ArgumentException("At least one extension is required.", nameof(extensions));

        string command = OpenCommand(exePath);
        string icon = IconValue(exePath);

        using (var progId = root.CreateSubKey(ProgIdPath, writable: true))
        {
            progId.SetValue("", typeName);
            progId.SetValue("FriendlyTypeName", typeName);
            using (var defaultIcon = progId.CreateSubKey("DefaultIcon", writable: true))
                defaultIcon.SetValue("", icon);
            using (var open = progId.CreateSubKey(@"shell\open\command", writable: true))
                open.SetValue("", command);
        }

        foreach (string ext in CandidateExtensions.Where(e => !selected.Contains(e)))
            RemoveOpenWith(root, ext);
        foreach (string ext in selected)
        {
            // Only the "Open with" candidate is added; the extension's default value (current association) is left alone.
            using var openWith = root.CreateSubKey(OpenWithProgidsPath(ext), writable: true);
            openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        using (var app = root.CreateSubKey(ApplicationPath, writable: true))
        {
            app.SetValue("FriendlyAppName", AppName);
            app.DeleteSubKeyTree("SupportedTypes", throwOnMissingSubKey: false);
            using (var types = app.CreateSubKey("SupportedTypes", writable: true))
            {
                foreach (string ext in selected)
                    types.SetValue(ext, "");
            }
            using (var open = app.CreateSubKey(@"shell\open\command", writable: true))
                open.SetValue("", command);
        }

        using (var caps = root.CreateSubKey(CapabilitiesPath, writable: true))
        {
            caps.SetValue("ApplicationName", AppName);
            caps.SetValue("ApplicationDescription", appDescription);
            caps.SetValue("ApplicationIcon", icon);
            caps.DeleteSubKeyTree("FileAssociations", throwOnMissingSubKey: false);
            using var assoc = caps.CreateSubKey("FileAssociations", writable: true);
            foreach (string ext in selected)
                assoc.SetValue(ext, ProgId);
        }

        using (var registered = root.CreateSubKey(RegisteredApplicationsPath, writable: true))
            registered.SetValue(AppName, CapabilitiesPath);
    }

    /// <summary>
    /// Removes only what <see cref="Register"/> wrote. Keys of other programs and extension keys with other content stay;
    /// keys left empty by the removal are deleted. Missing keys are skipped silently.
    /// </summary>
    public static void Unregister(RegistryKey root)
    {
        ArgumentNullException.ThrowIfNull(root);
        root.DeleteSubKeyTree(ProgIdPath, throwOnMissingSubKey: false);
        root.DeleteSubKeyTree(ApplicationPath, throwOnMissingSubKey: false);
        root.DeleteSubKeyTree(CapabilitiesPath, throwOnMissingSubKey: false);

        using (var registered = root.OpenSubKey(RegisteredApplicationsPath, writable: true))
            registered?.DeleteValue(AppName, throwOnMissingValue: false);

        foreach (string ext in CandidateExtensions)
            RemoveOpenWith(root, ext);

        DeleteIfEmpty(root, AppRootPath);
        DeleteIfEmpty(root, ApplicationsPath);
        DeleteIfEmpty(root, RegisteredApplicationsPath);
        DeleteIfEmpty(root, ClassesPath);
        DeleteIfEmpty(root, "Software");
    }

    /// <summary>State of one extension. Path comparison ignores case.</summary>
    public static FileAssociationStatus Status(RegistryKey root, string exePath, string extension)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        using (var openWith = root.OpenSubKey(OpenWithProgidsPath(extension)))
        {
            if (openWith is null || !openWith.GetValueNames().Contains(ProgId, StringComparer.OrdinalIgnoreCase))
                return FileAssociationStatus.NotRegistered;
        }
        using var open = root.OpenSubKey(ProgIdPath + @"\shell\open\command");
        if (open?.GetValue("") is not string command || command.Length == 0)
            return FileAssociationStatus.NotRegistered;
        return string.Equals(command, OpenCommand(exePath), StringComparison.OrdinalIgnoreCase)
            ? FileAssociationStatus.Registered
            : FileAssociationStatus.RegisteredElsewhere;
    }

    /// <summary>State of each extension, in the given order.</summary>
    public static IReadOnlyDictionary<string, FileAssociationStatus> Status(RegistryKey root, string exePath, IEnumerable<string> extensions)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        var result = new Dictionary<string, FileAssociationStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (string ext in extensions)
            result[ext] = Status(root, exePath, ext);
        return result;
    }

    /// <summary>Tells Explorer that associations changed. Callers invoke it only when root is the real HKCU.</summary>
    public static void NotifyShell() => SHChangeNotify(ShcneAssocChanged, ShcnfIdList, IntPtr.Zero, IntPtr.Zero);

    private const int ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0;

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    /// <summary>The executable must be an existing, fully qualified .exe path without quotes (it is embedded in quoted commands).</summary>
    public static void ValidateExePath(string exePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        if (exePath.Contains('"'))
            throw new ArgumentException("The executable path must not contain quotes.", nameof(exePath));
        if (!Path.IsPathFullyQualified(exePath))
            throw new ArgumentException("The executable path must be absolute.", nameof(exePath));
        if (!string.Equals(Path.GetExtension(exePath), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The executable path must end with .exe.", nameof(exePath));
        if (!File.Exists(exePath))
            throw new ArgumentException($"The executable '{exePath}' does not exist.", nameof(exePath));
    }

    /// <summary>Lower-case, de-duplicated extensions in candidate order. Unknown extensions are rejected.</summary>
    internal static IReadOnlyList<string> NormalizeExtensions(IEnumerable<string> extensions)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string ext in extensions)
        {
            if (ext is null || !CandidateExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException($"'{ext}' is not an extension ohMypic registers.", nameof(extensions));
            wanted.Add(ext);
        }
        return CandidateExtensions.Where(wanted.Contains).ToList();
    }

    /// <summary>Removes the ohMypic OpenWithProgids value of one extension, then the keys it left empty.</summary>
    private static void RemoveOpenWith(RegistryKey root, string ext)
    {
        using (var openWith = root.OpenSubKey(OpenWithProgidsPath(ext), writable: true))
        {
            if (openWith is null)
                return;
            openWith.DeleteValue(ProgId, throwOnMissingValue: false);
        }
        DeleteIfEmpty(root, OpenWithProgidsPath(ext));
        DeleteIfEmpty(root, ExtensionPath(ext));
    }

    /// <summary>Deletes the key when it has no values and no subkeys.</summary>
    internal static void DeleteIfEmpty(RegistryKey root, string path)
    {
        bool empty;
        using (var key = root.OpenSubKey(path))
            empty = key is not null && key.ValueCount == 0 && key.SubKeyCount == 0;
        if (empty)
            root.DeleteSubKey(path, throwOnMissingSubKey: false);
    }
}
