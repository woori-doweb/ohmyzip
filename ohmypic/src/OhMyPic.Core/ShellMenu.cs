using Microsoft.Win32;

namespace OhMyPic.Core;

/// <summary>Commands of the Explorer right-click "ohMypic" submenu. Declaration order is menu order.</summary>
public enum ShellMenuCommand
{
    Open,
    Slideshow,
    Rotate,
    Resize,
    Convert,
    Batch,
}

public enum ShellMenuStatus
{
    /// <summary>No candidate extension has the ohMypic menu key.</summary>
    NotRegistered,
    /// <summary>Every expected extension has every command, all pointing to this executable.</summary>
    Registered,
    /// <summary>Partly registered or pointing to another executable (register again to repair).</summary>
    RegisteredElsewhere,
}

/// <summary>
/// Current-user Explorer right-click menu (static shell verb with a cascading submenu) for image files.
/// Writes under SystemFileAssociations\&lt;ext&gt;, so the extension's default value, ProgIDs and the
/// <see cref="FileAssociations"/> keys are not touched. All paths are relative to the given root (HKCU in the app,
/// a temporary subkey in tests).
/// </summary>
public static class ShellMenu
{
    public const string VerbName = "ohMypic";

    public const string SystemFileAssociationsPath = FileAssociations.ClassesPath + @"\SystemFileAssociations";

    /// <summary>Submenu commands in display order.</summary>
    public static IReadOnlyList<ShellMenuCommand> Commands { get; } = Enum.GetValues<ShellMenuCommand>();

    public static string AssociationPath(string extension) => SystemFileAssociationsPath + @"\" + extension;

    public static string MenuPath(string extension) => AssociationPath(extension) + @"\shell\" + VerbName;

    /// <summary>Command-line switch of a command. Open passes the path only.</summary>
    public static string? Argument(ShellMenuCommand command) => command switch
    {
        ShellMenuCommand.Open => null,
        ShellMenuCommand.Slideshow => "--slideshow",
        ShellMenuCommand.Rotate => "--rotate",
        ShellMenuCommand.Resize => "--resize",
        ShellMenuCommand.Convert => "--convert",
        ShellMenuCommand.Batch => "--batch",
        _ => throw new ArgumentOutOfRangeException(nameof(command)),
    };

    /// <summary>Maps a switch to its command (case-insensitive). Open has no switch and cannot be parsed.</summary>
    public static bool TryParseArgument(string arg, out ShellMenuCommand command)
    {
        foreach (var candidate in Commands)
        {
            if (Argument(candidate) is { } known && string.Equals(arg, known, StringComparison.OrdinalIgnoreCase))
            {
                command = candidate;
                return true;
            }
        }
        command = default;
        return false;
    }

    /// <summary>Command string: "exe" [switch] "%1".</summary>
    public static string CommandLine(string exePath, ShellMenuCommand command) =>
        Argument(command) is { } arg ? $"\"{exePath}\" {arg} \"%1\"" : $"\"{exePath}\" \"%1\"";

    /// <summary>Subcommand key name. Explorer sorts by key name, so a sequence number (01..) comes first.</summary>
    public static string CommandKeyName(ShellMenuCommand command)
    {
        for (int i = 0; i < Commands.Count; i++)
        {
            if (Commands[i] == command)
                return $"{i + 1:00}{command}";
        }
        throw new ArgumentOutOfRangeException(nameof(command));
    }

    private static string CommandPath(ShellMenuCommand command) => @"shell\" + CommandKeyName(command) + @"\command";

    /// <summary>
    /// Writes the submenu for exactly <paramref name="extensions"/> (each must be in
    /// <see cref="FileAssociations.CandidateExtensions"/>); other candidate extensions lose their ohMypic menu.
    /// <paramref name="label"/> gives the localized item text (the UI language at registration time).
    /// Calling again refreshes paths and texts.
    /// </summary>
    public static void Register(RegistryKey root, string exePath, IEnumerable<string> extensions, Func<ShellMenuCommand, string> label)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(label);
        FileAssociations.ValidateExePath(exePath);
        var selected = FileAssociations.NormalizeExtensions(extensions);
        if (selected.Count == 0)
            throw new ArgumentException("At least one extension is required.", nameof(extensions));
        var labels = Commands.ToDictionary(c => c, c => label(c));
        foreach (var (command, text) in labels)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException($"The label of {command} is empty.", nameof(label));
        }
        string icon = FileAssociations.IconValue(exePath);

        foreach (string ext in FileAssociations.CandidateExtensions.Where(e => !selected.Contains(e)))
            RemoveMenu(root, ext);
        foreach (string ext in selected)
        {
            // Rewrite from scratch so items from an older layout do not linger.
            root.DeleteSubKeyTree(MenuPath(ext), throwOnMissingSubKey: false);
            using var menu = root.CreateSubKey(MenuPath(ext), writable: true);
            menu.SetValue("MUIVerb", VerbName);
            menu.SetValue("Icon", icon);
            // An empty SubCommands value makes the "shell" subkeys below a cascading submenu.
            menu.SetValue("SubCommands", "");
            foreach (var command in Commands)
            {
                using (var item = menu.CreateSubKey(@"shell\" + CommandKeyName(command), writable: true))
                    item.SetValue("MUIVerb", labels[command]);
                using (var line = menu.CreateSubKey(CommandPath(command), writable: true))
                    line.SetValue("", CommandLine(exePath, command));
            }
        }
    }

    /// <summary>
    /// Deletes only the ohMypic menu keys of every candidate extension, then the shell / extension keys this left empty.
    /// Values and keys of other programs stay. Missing keys are skipped silently.
    /// </summary>
    public static void Unregister(RegistryKey root)
    {
        ArgumentNullException.ThrowIfNull(root);
        foreach (string ext in FileAssociations.CandidateExtensions)
            RemoveMenu(root, ext);
        FileAssociations.DeleteIfEmpty(root, SystemFileAssociationsPath);
        FileAssociations.DeleteIfEmpty(root, FileAssociations.ClassesPath);
        FileAssociations.DeleteIfEmpty(root, "Software");
    }

    /// <summary>
    /// Registered when every extension in <paramref name="extensions"/> has all commands pointing to this executable and no
    /// other candidate extension carries the menu. Path comparison ignores case.
    /// </summary>
    public static ShellMenuStatus GetStatus(RegistryKey root, string exePath, IEnumerable<string> extensions)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        var expected = new HashSet<string>(extensions ?? throw new ArgumentNullException(nameof(extensions)), StringComparer.OrdinalIgnoreCase);
        bool any = false;
        bool all = expected.Count > 0;
        foreach (string ext in FileAssociations.CandidateExtensions)
        {
            using var menu = root.OpenSubKey(MenuPath(ext));
            if (menu is null)
            {
                if (expected.Contains(ext))
                    all = false;
                continue;
            }
            any = true;
            if (!expected.Contains(ext))
            {
                all = false;
                continue;
            }
            foreach (var command in Commands)
            {
                using var line = menu.OpenSubKey(CommandPath(command));
                if (line?.GetValue("") is not string value
                    || !string.Equals(value, CommandLine(exePath, command), StringComparison.OrdinalIgnoreCase))
                    all = false;
            }
        }
        if (!any)
            return ShellMenuStatus.NotRegistered;
        return all ? ShellMenuStatus.Registered : ShellMenuStatus.RegisteredElsewhere;
    }

    /// <summary>True when <see cref="GetStatus"/> reports <see cref="ShellMenuStatus.Registered"/>.</summary>
    public static bool IsRegistered(RegistryKey root, string exePath, IEnumerable<string> extensions) =>
        GetStatus(root, exePath, extensions) == ShellMenuStatus.Registered;

    private static void RemoveMenu(RegistryKey root, string ext)
    {
        root.DeleteSubKeyTree(MenuPath(ext), throwOnMissingSubKey: false);
        FileAssociations.DeleteIfEmpty(root, AssociationPath(ext) + @"\shell");
        FileAssociations.DeleteIfEmpty(root, AssociationPath(ext));
    }
}
