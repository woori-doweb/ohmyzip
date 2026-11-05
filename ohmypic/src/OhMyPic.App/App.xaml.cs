using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using OhMyPic.App.Dialogs;
using OhMyPic.App.Dialogs.Edit;
using OhMyPic.App.Settings;
using OhMyPic.App.Text;
using OhMyPic.App.Theme;
using OhMyPic.Core;

namespace OhMyPic.App;

public partial class App : Application
{
    private const string RegisterAssociationsArg = "--register-associations";
    private const string UnregisterAssociationsArg = "--unregister-associations";
    private const string RegisterShellMenuArg = "--register-shell-menu";
    private const string UnregisterShellMenuArg = "--unregister-shell-menu";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Settings are read once at start. The UI language is chosen before any string or window is created.
        // Load never throws: a missing or damaged file yields defaults plus LoadError (shown in the status bar).
        var settings = AppSettings.Load(AppSettings.DefaultPath);
        UiLanguage.Apply(settings.Language);
        string? firstArg = e.Args.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a))?.Trim();
        bool isSwitch = firstArg is not null && firstArg.StartsWith("--", StringComparison.Ordinal);
        // Registration switches: no window, current user (HKCU) only, exit code 0 = success, 1 = failure (message on stderr).
        if (isSwitch && RunRegistryCommand(firstArg!) is { } registryExit)
        {
            Shutdown(registryExit);
            return;
        }
        ThemeManager.Initialize(this);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        if (isSwitch)
        {
            // Explorer menu switches. Exit code null = the main window now runs (--slideshow).
            if (RunSwitch(e.Args.SkipWhile(string.IsNullOrWhiteSpace).ToList(), settings) is { } switchExit)
                Shutdown(switchExit);
            return;
        }
        StartUpdateServices(settings);

        var window = new MainWindow(settings, ParseStartupPath(e.Args));
        MainWindow = window;
        window.Show();
    }

    /// <summary>First non-empty argument as a full path (file = open in the viewer, folder = browse). Null when absent or malformed.</summary>
    internal static string? ParseStartupPath(IReadOnlyList<string> args)
    {
        var raw = args.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
        if (raw is null) return null;
        try
        {
            return Path.GetFullPath(raw.Trim().Trim('"'));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// Runs a registration switch (case-insensitive) and returns its exit code: 0 on success, 1 with a stderr message on
    /// failure. Null when the argument is not a registration switch.
    /// </summary>
    private static int? RunRegistryCommand(string arg)
    {
        Action run;
        string failed;
        switch (arg.ToLowerInvariant())
        {
            case RegisterAssociationsArg:
                run = () => SystemIntegration.RegisterAssociations(RequireExePath(), SystemIntegration.Extensions);
                failed = DialogText.Sys_RegisterAssocFailed;
                break;
            case UnregisterAssociationsArg:
                run = SystemIntegration.UnregisterAssociations;
                failed = DialogText.Sys_UnregisterAssocFailed;
                break;
            case RegisterShellMenuArg:
                run = () => SystemIntegration.RegisterShellMenu(RequireExePath());
                failed = DialogText.Sys_RegisterShellFailed;
                break;
            case UnregisterShellMenuArg:
                run = SystemIntegration.UnregisterShellMenu;
                failed = DialogText.Sys_UnregisterShellFailed;
                break;
            default:
                return null;
        }
        try
        {
            run();
            return 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine(string.Format(failed, ex.Message));
            return 1;
        }
    }

    private static string RequireExePath() => SystemIntegration.ExePath ?? throw new InvalidOperationException(DialogText.Sys_ExePathUnknown);

    /// <summary>
    /// Explorer menu switches (&lt;switch&gt; &lt;file&gt;): --slideshow opens the main window on the file's folder and starts the
    /// slideshow from it (returns null); --rotate / --resize / --convert / --batch show only that edit dialog and return the
    /// exit code when it closes. An unknown switch or a missing file is reported and returns 1.
    /// </summary>
    private int? RunSwitch(IReadOnlyList<string> args, AppSettings settings)
    {
        // Dialog-only runs end explicitly; otherwise the first (helper) window would count as the main window.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string arg = args[0].Trim();
        if (!ShellMenu.TryParseArgument(arg, out var command))
            return Fail(DialogText.Sys_CommandLine_Title, string.Format(DialogText.Sys_UnknownSwitch, arg) + "\n\n" + DialogText.Sys_Usage);
        string? file = ParseStartupPath(args.Skip(1).ToList());
        if (file is null || !File.Exists(file))
            return Fail(DialogText.Sys_CommandLine_Title, string.Format(DialogText.Sys_SwitchNeedsFile, arg));

        if (command == ShellMenuCommand.Slideshow)
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            var window = new MainWindow(settings, file, startSlideshow: true);
            MainWindow = window;
            window.Show();
            return null;
        }
        try
        {
            RunEditDialog(command, file, settings);
            return 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Fail(MainText.Unexpected_Title, string.Format(MainText.Unexpected_Body, ex.Message));
        }
    }

    /// <summary>Shows one edit dialog for one file under an invisible owner (no taskbar button) and waits until it closes.</summary>
    private static void RunEditDialog(ShellMenuCommand command, string file, AppSettings settings)
    {
        // The edit dialogs need an owner that has been shown.
        var owner = new Window
        {
            Title = DialogText.App_Name,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            ResizeMode = System.Windows.ResizeMode.NoResize,
            Width = 1,
            Height = 1,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        owner.Show();
        int quality = settings.JpegQuality;
        try
        {
            IReadOnlyList<string> files = [file];
            switch (command)
            {
                case ShellMenuCommand.Rotate: EditDialogs.ShowRotate(owner, files, settings); break;
                case ShellMenuCommand.Resize: EditDialogs.ShowResize(owner, files, settings); break;
                case ShellMenuCommand.Convert: EditDialogs.ShowConvert(owner, files, settings); break;
                case ShellMenuCommand.Batch: EditDialogs.ShowBatch(owner, files, settings); break;
                default: throw new ArgumentOutOfRangeException(nameof(command));
            }
        }
        finally
        {
            owner.Close();
        }
        // The convert dialogs may remember the JPEG quality ("apply last save quality"); keep it as the main window would.
        if (settings.JpegQuality != quality)
        {
            try
            {
                settings.Save(AppSettings.DefaultPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Not worth an alert after the edit itself finished; the next change saves again.
            }
        }
    }

    /// <summary>Reports a command-line problem on stderr and in a dialog, and returns exit code 1.</summary>
    private static int Fail(string heading, string message)
    {
        Console.Error.WriteLine(message);
        MessageDialog.Error(null, heading, message);
        return 1;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Last line of defence: keep the window alive and explain in plain words (the raw message is only the detail).
        e.Handled = true;
        MessageDialog.Error(MainWindow, MainText.Unexpected_Title, string.Format(MainText.Unexpected_Body, e.Exception.Message));
    }
}
