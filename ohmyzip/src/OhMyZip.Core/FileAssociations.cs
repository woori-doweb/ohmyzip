using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using OhMyZip.Core.Resources;

namespace OhMyZip.Core;

public enum FileAssociationStatus
{
    /// <summary>ohmyZip ProgId 의 열기 명령이 없다.</summary>
    NotRegistered,
    /// <summary>열기 명령이 지금 실행 파일을 가리킨다.</summary>
    Registered,
    /// <summary>등록돼 있지만 열기 명령이 다른 실행 파일을 가리킨다(프로그램 폴더를 옮긴 경우).</summary>
    RegisteredElsewhere,
}

/// <summary>
/// Windows '기본 앱' 에서 ohmyZip 을 압축 파일 기본 프로그램으로 고를 수 있게 하는 사용자 범위 등록.
/// Windows 10/11 은 프로그램이 UserChoice 를 직접 바꾸지 못하게 하므로, 여기서는 후보로 등록만 하고
/// 실제 선택은 사용자가 설정 화면(<see cref="SettingsUri"/>)에서 한다. 확장자 키의 기본값은 건드리지 않는다.
/// 모든 경로는 root 기준 상대 경로다(보통 HKCU, 시험에서는 임시 하위 키).
/// </summary>
[SupportedOSPlatform("windows")]
public static class FileAssociations
{
    public const string AppName = "ohmyZip";
    public const string ProgId = "ohmyZip.Archive";
    public const string ExeName = "ohmyZip.exe";
    public static string TypeName => ServiceText.Assoc_TypeName;
    public static string AppDescription => ServiceText.Assoc_AppDescription;
    public const string SettingsUri = "ms-settings:defaultapps?registeredAppUser=ohmyZip";

    public const string ClassesPath = @"Software\Classes";
    public const string ProgIdPath = ClassesPath + @"\" + ProgId;
    public const string ApplicationPath = ClassesPath + @"\Applications\" + ExeName;
    public const string AppRootPath = @"Software\" + AppName;
    public const string CapabilitiesPath = AppRootPath + @"\Capabilities";
    public const string RegisteredApplicationsPath = @"Software\RegisteredApplications";

    /// <summary>Archives 가 읽을 수 있는 형식의 확장자.</summary>
    public static IReadOnlyList<string> Extensions { get; } = [".zip", ".7z", ".rar", ".tar", ".gz", ".tgz"];

    public static string OpenWithProgidsPath(string extension) => $@"{ClassesPath}\{extension}\OpenWithProgids";

    /// <summary>열기 명령 문자열: "실행 파일" "%1".</summary>
    public static string OpenCommand(string exePath) => $"\"{exePath}\" \"%1\"";

    private static string IconValue(string exePath) => $"\"{exePath}\",0";

    /// <summary>ProgId·Applications·Capabilities·RegisteredApplications·확장자별 OpenWithProgids 를 쓴다. 다시 부르면 경로를 갱신한다.</summary>
    public static void Register(RegistryKey root, string exePath)
    {
        ArgumentNullException.ThrowIfNull(root);
        ValidateExePath(exePath);
        string command = OpenCommand(exePath);
        string icon = IconValue(exePath);

        using (var progId = root.CreateSubKey(ProgIdPath, writable: true))
        {
            progId.SetValue("", TypeName);
            progId.SetValue("FriendlyTypeName", TypeName);
            using (var defaultIcon = progId.CreateSubKey("DefaultIcon", writable: true))
                defaultIcon.SetValue("", icon);
            using (var open = progId.CreateSubKey(@"shell\open\command", writable: true))
                open.SetValue("", command);
        }

        foreach (string ext in Extensions)
        {
            // 확장자 키의 기본값(현재 연결)은 바꾸지 않고, '연결 프로그램' 후보 값만 더한다.
            using var openWith = root.CreateSubKey(OpenWithProgidsPath(ext), writable: true);
            openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        using (var app = root.CreateSubKey(ApplicationPath, writable: true))
        {
            app.SetValue("FriendlyAppName", AppName);
            using (var types = app.CreateSubKey("SupportedTypes", writable: true))
            {
                foreach (string ext in Extensions)
                    types.SetValue(ext, "");
            }
            using (var open = app.CreateSubKey(@"shell\open\command", writable: true))
                open.SetValue("", command);
        }

        using (var caps = root.CreateSubKey(CapabilitiesPath, writable: true))
        {
            caps.SetValue("ApplicationName", AppName);
            caps.SetValue("ApplicationDescription", AppDescription);
            caps.SetValue("ApplicationIcon", icon);
            using var assoc = caps.CreateSubKey("FileAssociations", writable: true);
            foreach (string ext in Extensions)
                assoc.SetValue(ext, ProgId);
        }

        using (var registered = root.CreateSubKey(RegisteredApplicationsPath, writable: true))
            registered.SetValue(AppName, CapabilitiesPath);
    }

    /// <summary>Register 가 만든 것만 지운다. 다른 프로그램의 값과 내용이 남은 확장자 키는 남기고, 완전히 빈 확장자 키만 지운다. 없는 키는 조용히 넘어간다.</summary>
    public static void Unregister(RegistryKey root)
    {
        ArgumentNullException.ThrowIfNull(root);
        root.DeleteSubKeyTree(ProgIdPath, throwOnMissingSubKey: false);
        root.DeleteSubKeyTree(ApplicationPath, throwOnMissingSubKey: false);
        root.DeleteSubKeyTree(AppRootPath, throwOnMissingSubKey: false);

        using (var registered = root.OpenSubKey(RegisteredApplicationsPath, writable: true))
            registered?.DeleteValue(AppName, throwOnMissingValue: false);

        foreach (string ext in Extensions)
        {
            string path = OpenWithProgidsPath(ext);
            bool empty;
            using (var openWith = root.OpenSubKey(path, writable: true))
            {
                if (openWith is null)
                    continue;
                openWith.DeleteValue(ProgId, throwOnMissingValue: false);
                empty = openWith.ValueCount == 0 && openWith.SubKeyCount == 0;
            }
            if (!empty)
                continue;
            // 비어 버린 OpenWithProgids 를 지우고, 그래서 확장자 키까지 값·하위 키가 하나도 없으면 그것도 지운다.
            // 기본값이나 다른 하위 키가 있는 확장자 키는 남긴다.
            root.DeleteSubKey(path, throwOnMissingSubKey: false);
            string extPath = $@"{ClassesPath}\{ext}";
            bool extEmpty;
            using (var extKey = root.OpenSubKey(extPath))
                extEmpty = extKey is not null && extKey.ValueCount == 0 && extKey.SubKeyCount == 0;
            if (extEmpty)
                root.DeleteSubKey(extPath, throwOnMissingSubKey: false);
        }
    }

    /// <summary>ProgId 의 열기 명령으로 상태를 판단한다. 경로 비교는 대소문자를 가리지 않는다.</summary>
    public static FileAssociationStatus GetStatus(RegistryKey root, string exePath)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        using var open = root.OpenSubKey(ProgIdPath + @"\shell\open\command");
        if (open?.GetValue("") is not string command || command.Length == 0)
            return FileAssociationStatus.NotRegistered;
        return string.Equals(command, OpenCommand(exePath), StringComparison.OrdinalIgnoreCase)
            ? FileAssociationStatus.Registered
            : FileAssociationStatus.RegisteredElsewhere;
    }

    /// <summary>탐색기에 파일 연결이 바뀌었다고 알린다. root 가 실제 HKCU 일 때만 호출부에서 부른다.</summary>
    public static void NotifyShell() => SHChangeNotify(ShcneAssocChanged, ShcnfIdList, IntPtr.Zero, IntPtr.Zero);

    private const int ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0;

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    private static void ValidateExePath(string exePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        if (!Path.IsPathFullyQualified(exePath))
            throw new ArgumentException(ServiceText.Assoc_ExePathNotFull, nameof(exePath));
        if (exePath.Contains('"'))
            throw new ArgumentException(ServiceText.Assoc_ExePathQuote, nameof(exePath));
    }
}
