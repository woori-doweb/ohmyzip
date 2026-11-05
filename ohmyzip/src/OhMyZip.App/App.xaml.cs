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
        // 파일 연결 명령줄: 창 없이 현재 사용자(HKCU) 범위에 등록/해제하고 끝낸다(성공 0, 실패 1).
        if (e.Args.Length > 0 && (IsArg(e.Args[0], RegisterFileTypesArg) || IsArg(e.Args[0], UnregisterFileTypesArg)))
        {
            Shutdown(RunFileTypesCommand(IsArg(e.Args[0], RegisterFileTypesArg)));
            return;
        }
        _windowMode = true;
        ExecutablePath = Environment.ProcessPath;
        StartupArgs = e.Args;
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
        // 첫 번째 명령줄 인자 = 열 압축 파일.
        var window = new MainWindow(e.Args.Length > 0 ? e.Args[0] : null, settingsError);
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

    private static bool IsArg(string arg, string name) => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase);

    private static int RunFileTypesCommand(bool register)
    {
        try
        {
            if (register)
            {
                string exePath = Environment.ProcessPath ?? throw new InvalidOperationException(MainText.ExePathUnknown);
                FileAssociations.Register(Registry.CurrentUser, exePath);
            }
            else
            {
                FileAssociations.Unregister(Registry.CurrentUser);
            }
            FileAssociations.NotifyShell();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(string.Format(register ? MainText.FileTypes_RegisterFailed : MainText.FileTypes_UnregisterFailed, ex.Message));
            return 1;
        }
    }
}
