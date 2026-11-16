using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Windows;
using Microsoft.Win32;
using OhMyZip.App.Settings;
using OhMyZip.App.Text;
using OhMyZip.App.Theme;
using OhMyZip.Core;

namespace OhMyZip.App;

public partial class App : Application
{
    private const string RegisterFileTypesArg = "--register-file-types";
    private const string UnregisterFileTypesArg = "--unregister-file-types";
    private const string RegisterShellMenuArg = "--register-shell-menu";
    private const string UnregisterShellMenuArg = "--unregister-shell-menu";

    private static readonly Lazy<HttpClient> SharedHttp = new(CreateHttp);

    /// <summary>창 모드로 시작했는지(명령줄 모드에서는 업데이트를 정리·설치하지 않는다).</summary>
    private bool _windowMode;

    /// <summary>앱 수명 동안 하나만 쓰는 HTTP 클라이언트(업데이트 확인·내려받기).</summary>
    internal static HttpClient Http => SharedHttp.Value;

    /// <summary>업데이트를 내려받고 푸는 폴더: %LOCALAPPDATA%\ohmyZip\updates.</summary>
    internal static string UpdateWorkDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ohmyZip", "updates");

    /// <summary>실행 중인 앱 버전(major.minor.patch). 릴리스 태그 v{major}.{minor}.{patch} 와 같은 자리수로 비교한다.</summary>
    internal static Version CurrentVersion { get; } = ToReleaseVersion(typeof(App).Assembly.GetName().Version);

    /// <summary>시작할 때의 실행 파일 경로·인자·작업 폴더(업데이트를 설치한 뒤 같은 인자로 다시 실행).</summary>
    internal static string? ExecutablePath { get; private set; }
    internal static IReadOnlyList<string> StartupArgs { get; private set; } = [];
    internal static string StartupDirectory { get; private set; } = "";

    /// <summary>자동 업데이트가 받아 둔 준비 폴더와 그 버전. 프로그램을 닫을 때 설치한다.</summary>
    internal static (string StagedDir, Version Version)? PendingUpdate { get; set; }

    internal static AppUpdater CreateUpdater() => new(Http, AppContext.BaseDirectory, UpdateWorkDir);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // 설정은 시작할 때 한 번 읽는다. 화면 언어는 어떤 문구나 창보다도 먼저 정한다(명령줄 모드도 같은 언어).
        // 읽기 실패 문장은 기본값(Windows 표시 언어)으로 만들어지고, 그때 설정도 기본값이라 언어가 같다.
        var settingsError = AppSettings.Load();
        UiLanguage.Apply(AppSettings.Current.Language);
        // 파일 연결·탐색기 메뉴 명령줄: 창 없이 현재 사용자(HKCU) 범위에 등록/해제하고 끝낸다(성공 0, 실패 1).
        if (e.Args.Length > 0 && RunRegistryCommand(e.Args[0]) is { } exitCode)
        {
            Shutdown(exitCode);
            return;
        }
        // 탐색기 메뉴 처리기가 남긴 목록 파일 중 하루 넘은 것을 지운다(실패는 무시).
        SelectionList.CleanupStale(SelectionList.DefaultDirectory, TimeSpan.FromDays(1), DateTime.UtcNow);
        // 목록 모드에서 남은 경로가 없으면 창 없이 끝낸다.
        if (!TryParseStartup(e.Args, out var startupCommand, out var startupPaths))
        {
            Shutdown(1);
            return;
        }
        _windowMode = true;
        ExecutablePath = Environment.ProcessPath;
        // 업데이트 뒤 다시 시작할 인자: 이 창에 연 압축 파일만(목록 파일은 이미 지웠고, 메뉴 명령은 다시 실행하지 않는다).
        StartupArgs = (startupCommand is null or ShellMenuCommand.Open) && startupPaths.Count > 0 ? [startupPaths[0]] : [];
        StartupDirectory = Environment.CurrentDirectory;
        // 지난 업데이트 설치에서 이름을 바꿔 둔 옛 파일을 지운다(아직 쓰는 중이면 다음 실행에서 다시).
        try
        {
            AppUpdater.CleanupLeftovers(AppContext.BaseDirectory);
        }
        catch (Exception) { }
        ThemeManager.Override = AppSettings.Current.Theme;
        ThemeManager.ReduceMotion = AppSettings.Current.ReduceMotion;
        ThemeManager.Initialize(this);
        var window = new MainWindow(startupCommand, startupPaths, settingsError);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_windowMode) InstallPendingUpdate();
        base.OnExit(e);
    }

    /// <summary>
    /// 받아 둔 업데이트를 설치한다. 실패해도 종료는 막지 않는다(설치 폴더는 Apply 가 되돌리고, 다음 실행에서 다시 받는다).
    /// 다른 창(다른 프로세스)이 먼저 같은 버전 이상을 설치했으면 건너뛴다.
    /// </summary>
    internal static void InstallPendingUpdate()
    {
        if (PendingUpdate is not { } pending || !AppSettings.Current.AutoUpdate) return;
        PendingUpdate = null;
        if (InstalledVersion() is { } installed && installed >= pending.Version) return;
        try
        {
            CreateUpdater().Apply(pending.StagedDir);
        }
        catch (Exception) { }
    }

    /// <summary>설치 폴더에 지금 놓인 앱 어셈블리의 버전. 읽을 수 없으면 null.</summary>
    private static Version? InstalledVersion()
    {
        try
        {
            var name = typeof(App).Assembly.GetName().Name + ".dll";
            return ToReleaseVersion(AssemblyName.GetAssemblyName(Path.Combine(AppContext.BaseDirectory, name)).Version);
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or ArgumentException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static Version ToReleaseVersion(Version? v) =>
        v is null ? new Version(0, 0, 0) : new Version(v.Major, v.Minor, Math.Max(0, v.Build));

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ohmyZip", CurrentVersion.ToString(3)));
        return http;
    }

    /// <summary>
    /// 등록 명령줄 인자면 실행하고 종료 코드(성공 0, 실패 1 + stderr 문장)를 돌려준다. 등록 명령줄이 아니면 null.
    /// 인자는 대소문자를 가리지 않는다(상수는 모두 소문자 ASCII).
    /// </summary>
    private static int? RunRegistryCommand(string arg)
    {
        Action run;
        string failed;
        switch (arg.ToLowerInvariant())
        {
            case RegisterFileTypesArg:
                run = () => FileAssociations.Register(Registry.CurrentUser, RequireExePath());
                failed = MainText.FileTypes_RegisterFailed;
                break;
            case UnregisterFileTypesArg:
                run = () => FileAssociations.Unregister(Registry.CurrentUser);
                failed = MainText.FileTypes_UnregisterFailed;
                break;
            case RegisterShellMenuArg:
                run = () => ShellMenu.Register(Registry.CurrentUser, RequireExePath());
                failed = MainText.ShellMenu_RegisterFailed;
                break;
            case UnregisterShellMenuArg:
                run = () => ShellMenu.Unregister(Registry.CurrentUser);
                failed = MainText.ShellMenu_UnregisterFailed;
                break;
            default:
                return null;
        }
        try
        {
            run();
            FileAssociations.NotifyShell();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(string.Format(failed, ex.Message));
            return 1;
        }
    }

    /// <summary>
    /// 시작 인자를 메뉴 명령과 경로로 바꾼다.
    /// 첫 인자가 메뉴 명령이면 그 뒤는 「--list 목록 파일」 또는 경로 하나 이상이고, 경로 없이 명령만 있으면 평소처럼 시작한다(명령 없음).
    /// 첫 인자가 명령이 아니면 그 인자가 열 압축 파일이다(명령 없음 = 열기).
    /// 목록 모드에서 남은 경로가 없으면(목록을 못 읽은 경우 포함) false.
    /// </summary>
    private static bool TryParseStartup(string[] args, out ShellMenuCommand? command, out IReadOnlyList<string> paths)
    {
        command = null;
        paths = [];
        if (args.Length == 0) return true;
        if (!ShellMenu.TryParseArgument(args[0], out var parsed))
        {
            paths = [args[0]];
            return true;
        }
        if (args.Length >= 2 && string.Equals(args[1], ShellMenu.ListArgument, StringComparison.OrdinalIgnoreCase))
        {
            IReadOnlyList<string> listed = [];
            try
            {
                if (args.Length == 3) listed = SelectionList.Read(args[2]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
            if (listed.Count == 0) return false;
            command = parsed;
            paths = listed;
            return true;
        }
        if (args.Length >= 2)
        {
            command = parsed;
            paths = args[1..];
        }
        return true;
    }

    private static string RequireExePath() => Environment.ProcessPath ?? throw new InvalidOperationException(MainText.ExePathUnknown);
}
