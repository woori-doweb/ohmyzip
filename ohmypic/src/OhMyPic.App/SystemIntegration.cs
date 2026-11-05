using System.IO;
using Microsoft.Win32;
using OhMyPic.App.Text;
using OhMyPic.Core;

namespace OhMyPic.App;

/// <summary>
/// App side of the current-user file associations and Explorer menu: localized texts, the real HKCU root and the
/// shell notification. Shared by the Settings pages and the command-line switches.
/// </summary>
internal static class SystemIntegration
{
    /// <summary>Running executable, or null when the process path is unknown.</summary>
    public static string? ExePath => Environment.ProcessPath;

    /// <summary>Image types this machine can open (built-in codecs plus installed optional ones).</summary>
    public static IReadOnlyList<string> Extensions => FileAssociations.AvailableExtensions();

    /// <summary>Explorer submenu text in the current UI language.</summary>
    public static string ShellLabel(ShellMenuCommand command) => command switch
    {
        ShellMenuCommand.Open => DialogText.Sys_ShellMenu_Open,
        ShellMenuCommand.Slideshow => DialogText.Sys_ShellMenu_Slideshow,
        ShellMenuCommand.Rotate => DialogText.Sys_ShellMenu_Rotate,
        ShellMenuCommand.Resize => DialogText.Sys_ShellMenu_Resize,
        ShellMenuCommand.Convert => DialogText.Sys_ShellMenu_Convert,
        ShellMenuCommand.Batch => DialogText.Sys_ShellMenu_Batch,
        _ => throw new ArgumentOutOfRangeException(nameof(command)),
    };

    public static void RegisterAssociations(string exePath, IEnumerable<string> extensions)
    {
        FileAssociations.Register(Registry.CurrentUser, exePath, extensions, DialogText.Sys_TypeName, DialogText.Sys_AppDescription);
        FileAssociations.NotifyShell();
    }

    public static void UnregisterAssociations()
    {
        FileAssociations.Unregister(Registry.CurrentUser);
        FileAssociations.NotifyShell();
    }

    public static void RegisterShellMenu(string exePath)
    {
        ShellMenu.Register(Registry.CurrentUser, exePath, Extensions, ShellLabel);
        FileAssociations.NotifyShell();
    }

    public static void UnregisterShellMenu()
    {
        ShellMenu.Unregister(Registry.CurrentUser);
        FileAssociations.NotifyShell();
    }

    /// <summary>Exceptions a registry write or read can raise (shown to the user, never fatal).</summary>
    public static bool IsRegistryError(Exception ex) =>
        ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException or InvalidOperationException;
}
