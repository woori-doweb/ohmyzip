using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using OhMyPic.App.Text;
using OhMyPic.Core;

namespace OhMyPic.App;

/// <summary>
/// App side of the current-user file associations, Explorer menu and Print Screen agent: localized texts, the real
/// HKCU root and the shell notification. Shared by the Settings pages, the command-line switches and the agent.
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

    /// <summary>True when this copy of ohMypic starts the Print Screen agent at sign-in (the HKCU Run value is the only state).</summary>
    public static bool IsCaptureEnabled => ExePath is { } exe && CaptureAgentRegistration.IsRegistered(Registry.CurrentUser, exe);

    /// <summary>
    /// Registers the agent for sign-in and starts it now. Starting it while one already runs is harmless: the new process
    /// finds the single-instance mutex taken and exits. Throws the registry errors, or Win32Exception when the start fails.
    /// </summary>
    public static void EnableCapture(string exePath)
    {
        CaptureAgentRegistration.Register(Registry.CurrentUser, exePath);
        var start = new ProcessStartInfo(exePath) { UseShellExecute = false };
        start.ArgumentList.Add(CaptureAgentRegistration.Switch);
        using var _ = Process.Start(start);
    }

    /// <summary>Removes the sign-in entry, then asks a running agent to exit.</summary>
    public static void DisableCapture()
    {
        CaptureAgentRegistration.Unregister(Registry.CurrentUser);
        CaptureAgent.SignalQuit();
    }

    /// <summary>Exceptions a registry write or read can raise (shown to the user, never fatal).</summary>
    public static bool IsRegistryError(Exception ex) =>
        ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException or InvalidOperationException;
}
