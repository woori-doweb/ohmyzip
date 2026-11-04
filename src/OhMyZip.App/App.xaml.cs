using System.Windows;
using Microsoft.Win32;
using OhMyZip.App.Settings;
using OhMyZip.App.Theme;
using OhMyZip.Core;

namespace OhMyZip.App;

public partial class App : Application
{
    private const string RegisterFileTypesArg = "--register-file-types";
    private const string UnregisterFileTypesArg = "--unregister-file-types";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // 파일 연결 명령줄: 창 없이 현재 사용자(HKCU) 범위에 등록/해제하고 끝낸다(성공 0, 실패 1).
        if (e.Args.Length > 0 && (IsArg(e.Args[0], RegisterFileTypesArg) || IsArg(e.Args[0], UnregisterFileTypesArg)))
        {
            Shutdown(RunFileTypesCommand(IsArg(e.Args[0], RegisterFileTypesArg)));
            return;
        }
        // 설정은 시작할 때 한 번 읽는다(테마 재정의가 첫 화면부터 적용되도록 테마보다 먼저).
        var settingsError = AppSettings.Load();
        ThemeManager.Override = AppSettings.Current.Theme;
        ThemeManager.ReduceMotion = AppSettings.Current.ReduceMotion;
        ThemeManager.Initialize(this);
        // 첫 번째 명령줄 인자 = 열 압축 파일.
        var window = new MainWindow(e.Args.Length > 0 ? e.Args[0] : null, settingsError);
        MainWindow = window;
        window.Show();
    }

    private static bool IsArg(string arg, string name) => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase);

    private static int RunFileTypesCommand(bool register)
    {
        try
        {
            if (register)
            {
                string exePath = Environment.ProcessPath ?? throw new InvalidOperationException("실행 파일 경로를 알 수 없습니다.");
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
            Console.Error.WriteLine($"ohmyZip: 파일 연결 {(register ? "등록" : "해제")} 실패 — {ex.Message}");
            return 1;
        }
    }
}
