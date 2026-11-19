using Microsoft.Win32;

namespace OhMyPic.Core;

/// <summary>
/// Sign-in autostart of the resident Print Screen capture agent: one value under the current user's Run key. The agent
/// must already be running to receive Print Screen before the system capture shortcut does, so it starts with Windows.
/// Only <see cref="ValueName"/> is written or removed; other Run values stay. All paths are relative to the given root
/// (HKCU in the app, a temporary subkey in tests).
/// </summary>
public static class CaptureAgentRegistration
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public const string ValueName = "ohMypic Capture";

    /// <summary>Command-line switch that starts the executable as the capture agent instead of a viewer window.</summary>
    public const string Switch = "--capture-agent";

    /// <summary>Run value for the executable: "exe" --capture-agent.</summary>
    /// <exception cref="ArgumentException">The path is empty, relative or contains quotes.</exception>
    public static string Command(string exePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        if (exePath.Contains('"'))
            throw new ArgumentException("The executable path must not contain quotes.", nameof(exePath));
        if (!Path.IsPathFullyQualified(exePath))
            throw new ArgumentException("The executable path must be absolute.", nameof(exePath));
        return $"\"{exePath}\" {Switch}";
    }

    /// <summary>Writes (or refreshes) the Run value. The executable must exist (<see cref="FileAssociations.ValidateExePath"/>).</summary>
    public static void Register(RegistryKey root, string exePath)
    {
        ArgumentNullException.ThrowIfNull(root);
        FileAssociations.ValidateExePath(exePath);
        string command = Command(exePath);
        using RegistryKey run = root.CreateSubKey(RunKeyPath, writable: true);
        run.SetValue(ValueName, command, RegistryValueKind.String);
    }

    /// <summary>Removes the Run value. A missing key or value is not an error; no key is created.</summary>
    public static void Unregister(RegistryKey root)
    {
        ArgumentNullException.ThrowIfNull(root);
        using RegistryKey? run = root.OpenSubKey(RunKeyPath, writable: true);
        run?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// True only when the Run value equals <see cref="Command"/> for this executable (ignoring case), so a value left by a
    /// moved or other copy counts as not registered and registering again repairs it.
    /// </summary>
    public static bool IsRegistered(RegistryKey root, string exePath)
    {
        ArgumentNullException.ThrowIfNull(root);
        string command = Command(exePath);
        using RegistryKey? run = root.OpenSubKey(RunKeyPath);
        return run?.GetValue(ValueName) is string value && string.Equals(value, command, StringComparison.OrdinalIgnoreCase);
    }
}
