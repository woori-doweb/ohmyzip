using System.Runtime.Versioning;
using Microsoft.Win32;
using OhMyZip.Core.Resources;

namespace OhMyZip.Core;

/// <summary>탐색기 오른쪽 클릭 'ohmyZip' 하위 메뉴의 명령. 선언 순서가 압축 파일 메뉴의 순서다.</summary>
public enum ShellMenuCommand
{
    Open,
    Extract,
    ExtractHere,
    ExtractToFolder,
    SmartExtract,
    Compress,
    CompressToZip,
}

public enum ShellMenuStatus
{
    /// <summary>ohmyZip 메뉴 키도 명령 처리기(CLSID) 키도 없다.</summary>
    NotRegistered,
    /// <summary>모든 메뉴의 모든 명령과 처리기 키가 지금 실행 파일을 가리킨다.</summary>
    Registered,
    /// <summary>일부만 있거나 다른 실행 파일을 가리킨다(다시 등록해야 한다).</summary>
    RegisteredElsewhere,
}

/// <summary>
/// 탐색기 오른쪽 클릭 'ohmyZip' 하위 메뉴의 사용자 범위 등록.
/// 하위 명령은 정적 verb 의 ExplorerCommandHandler 로 실행 파일 옆 처리기 DLL(<see cref="HandlerDllName"/>)을 부른다.
/// 처리기는 선택 전체를 목록 파일 하나로 모아 실행 파일을 한 번 띄운다(<c>"exe" 인자 --list "목록 파일"</c>).
/// 압축 파일은 형식별 키(SystemFileAssociations\&lt;확장자&gt;), 그 밖의 파일·폴더는 AllFilesystemObjects 에 쓰므로
/// 확장자 연결 기본값·ProgId 와 <see cref="FileAssociations"/> 의 키는 건드리지 않는다.
/// 모든 경로는 root 기준 상대 경로다(보통 HKCU, 시험에서는 임시 하위 키).
/// </summary>
[SupportedOSPlatform("windows")]
public static class ShellMenu
{
    public const string VerbName = "ohmyZip";

    /// <summary>실행 파일과 같은 폴더에 두는 명령 처리기 DLL.</summary>
    public const string HandlerDllName = "ShellCommand.dll";

    /// <summary>목록 모드 인자: <c>ohmyZip.exe 명령인자 --list "목록 파일"</c>.</summary>
    public const string ListArgument = "--list";

    private const string SystemFileAssociationsPath = FileAssociations.ClassesPath + @"\SystemFileAssociations";
    private const string AllFilesystemObjectsPath = FileAssociations.ClassesPath + @"\AllFilesystemObjects";
    private const string ClassIdRootPath = FileAssociations.ClassesPath + @"\CLSID";

    /// <summary>셸이 명령 처리기를 처음 쓸 때 기록하는 캐시. 값 이름이 "{CLSID} " 로 시작한다.</summary>
    public const string ShellExtensionsCachedPath = @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Cached";

    /// <summary>메뉴를 붙이는 압축 파일 확장자(파일 연결과 같은 형식).</summary>
    public static IReadOnlyList<string> Extensions => FileAssociations.Extensions;

    /// <summary>압축 파일 하위 메뉴 명령(표시 순서). 모든 명령이 여기에 있다.</summary>
    public static IReadOnlyList<ShellMenuCommand> Commands { get; } = Enum.GetValues<ShellMenuCommand>();

    /// <summary>그 밖의 파일·폴더 하위 메뉴 명령(표시 순서).</summary>
    public static IReadOnlyList<ShellMenuCommand> FileSystemObjectCommands { get; } =
        [ShellMenuCommand.Compress, ShellMenuCommand.CompressToZip];

    public static string AssociationPath(string extension) => SystemFileAssociationsPath + @"\" + extension;

    /// <summary>압축 파일 형식 하나의 메뉴 키.</summary>
    public static string MenuPath(string extension) => AssociationPath(extension) + @"\shell\" + VerbName;

    /// <summary>그 밖의 파일·폴더 메뉴 키.</summary>
    public const string FileSystemObjectMenuPath = AllFilesystemObjectsPath + @"\shell\" + VerbName;

    /// <summary>
    /// 그 밖의 파일·폴더 메뉴를 압축 파일 확장자에는 보이지 않게 하는 조건.
    /// 같은 파일에 'ohmyZip' 하위 메뉴가 두 번 나오지 않는다.
    /// </summary>
    public static string AppliesTo { get; } =
        "NOT (" + string.Join(" OR ", FileAssociations.Extensions.Select(ext => "System.FileExtension:=" + ext)) + ")";

    /// <summary>메뉴 키 하나와 그 하위 명령(표시 순서). AppliesTo 가 null 이면 조건 없이 보인다.</summary>
    private sealed record MenuTarget(string Path, IReadOnlyList<ShellMenuCommand> Commands, string? AppliesTo);

    private static readonly MenuTarget[] Menus =
    [
        .. FileAssociations.Extensions.Select(ext => new MenuTarget(MenuPath(ext), Commands, null)),
        new MenuTarget(FileSystemObjectMenuPath, FileSystemObjectCommands, AppliesTo),
    ];

    /// <summary>등록이 쓰는 메뉴 키 전체(압축 파일 형식별 + 그 밖의 파일·폴더). 동의 대화상자에 보인다.</summary>
    public static IReadOnlyList<string> MenuPaths { get; } = [.. Menus.Select(m => m.Path)];

    /// <summary>등록이 쓰는 명령 처리기(CLSID) 키 전체(<see cref="Commands"/> 순서).</summary>
    public static IReadOnlyList<string> ClassIdPaths { get; } = [.. Commands.Select(ClassIdPath)];

    /// <summary>명령별 처리기 CLSID. 버전이 바뀌어도 바꾸지 않는다(처리기 DLL 이 이 키에서 실행 파일·인자를 읽는다).</summary>
    public static string ClassId(ShellMenuCommand command) => command switch
    {
        ShellMenuCommand.Open => "{5C1B7E0A-3F4D-4B8E-9A61-0D2E7F3A1B01}",
        ShellMenuCommand.Extract => "{5C1B7E0A-3F4D-4B8E-9A61-0D2E7F3A1B02}",
        ShellMenuCommand.ExtractHere => "{5C1B7E0A-3F4D-4B8E-9A61-0D2E7F3A1B03}",
        ShellMenuCommand.ExtractToFolder => "{5C1B7E0A-3F4D-4B8E-9A61-0D2E7F3A1B04}",
        ShellMenuCommand.SmartExtract => "{5C1B7E0A-3F4D-4B8E-9A61-0D2E7F3A1B05}",
        ShellMenuCommand.Compress => "{5C1B7E0A-3F4D-4B8E-9A61-0D2E7F3A1B06}",
        ShellMenuCommand.CompressToZip => "{5C1B7E0A-3F4D-4B8E-9A61-0D2E7F3A1B07}",
        _ => throw new ArgumentOutOfRangeException(nameof(command)),
    };

    public static string ClassIdPath(ShellMenuCommand command) => ClassIdRootPath + @"\" + ClassId(command);

    /// <summary>실행 파일 옆 처리기 DLL 의 전체 경로.</summary>
    public static string HandlerPath(string exePath) => Path.Combine(Path.GetDirectoryName(exePath) ?? "", HandlerDllName);

    /// <summary>
    /// 명령 인자. 목록 모드(<c>인자 --list "목록 파일"</c>)와 경로를 바로 주는 명령줄(<c>인자 "경로"…</c>)에 같이 쓴다.
    /// 열기는 인자 없이 경로만 주는 명령줄도 받는다(호출부가 처리한다).
    /// </summary>
    public static string Argument(ShellMenuCommand command) => command switch
    {
        ShellMenuCommand.Open => "--open",
        ShellMenuCommand.Extract => "--extract",
        ShellMenuCommand.ExtractHere => "--extract-here",
        ShellMenuCommand.ExtractToFolder => "--extract-to-folder",
        ShellMenuCommand.SmartExtract => "--extract-smart",
        ShellMenuCommand.Compress => "--compress",
        ShellMenuCommand.CompressToZip => "--compress-zip",
        _ => throw new ArgumentOutOfRangeException(nameof(command)),
    };

    /// <summary>인자를 명령으로 바꾼다(대소문자 무시). 경로·<see cref="ListArgument"/> 는 명령이 아니다.</summary>
    public static bool TryParseArgument(string arg, out ShellMenuCommand command)
    {
        foreach (var candidate in Commands)
        {
            if (string.Equals(arg, Argument(candidate), StringComparison.OrdinalIgnoreCase))
            {
                command = candidate;
                return true;
            }
        }
        command = default;
        return false;
    }

    /// <summary>메뉴 문구(지금 화면 언어).</summary>
    public static string Label(ShellMenuCommand command) => command switch
    {
        ShellMenuCommand.Open => ServiceText.ShellMenu_Open,
        ShellMenuCommand.Extract => ServiceText.ShellMenu_Extract,
        ShellMenuCommand.ExtractHere => ServiceText.ShellMenu_ExtractHere,
        ShellMenuCommand.ExtractToFolder => ServiceText.ShellMenu_ExtractToFolder,
        ShellMenuCommand.SmartExtract => ServiceText.ShellMenu_SmartExtract,
        ShellMenuCommand.Compress => ServiceText.ShellMenu_Compress,
        ShellMenuCommand.CompressToZip => ServiceText.ShellMenu_CompressToZip,
        _ => throw new ArgumentOutOfRangeException(nameof(command)),
    };

    /// <summary>하위 명령 키 이름. 탐색기는 키 이름 순으로 보이므로 메뉴 안 순번(01~)을 앞에 붙인다.</summary>
    private static string CommandKeyName(IReadOnlyList<ShellMenuCommand> commands, int index) =>
        $"{index + 1:00}{commands[index]}";

    /// <summary>
    /// 명령 처리기 키와 메뉴 키를 쓴다. 우리 키는 지우고 새로 쓰므로 다시 부르면 경로·문구를 갱신하고 이전 형식의 값도 남지 않는다.
    /// 실행 파일 옆에 처리기 DLL 이 없으면 아무것도 쓰지 않고 <see cref="FileNotFoundException"/>.
    /// </summary>
    public static void Register(RegistryKey root, string exePath)
    {
        ArgumentNullException.ThrowIfNull(root);
        FileAssociations.ValidateExePath(exePath);
        string handler = HandlerPath(exePath);
        if (!File.Exists(handler))
            throw new FileNotFoundException(string.Format(ServiceText.ShellMenu_HandlerMissing, handler), handler);
        string icon = FileAssociations.IconValue(exePath);

        // 메뉴가 없는 처리기를 가리키지 않도록 처리기 키부터 쓴다.
        foreach (var command in Commands)
        {
            string path = ClassIdPath(command);
            root.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
            using var clsid = root.CreateSubKey(path, writable: true);
            clsid.SetValue("", $"{VerbName} {command}");
            using (var server = clsid.CreateSubKey("InprocServer32", writable: true))
            {
                server.SetValue("", handler);
                server.SetValue("ThreadingModel", "Apartment");
            }
            using var line = clsid.CreateSubKey("Command", writable: true);
            line.SetValue("Exe", exePath);
            line.SetValue("Verb", Argument(command));
            line.SetValue("Title", Label(command));
        }

        foreach (var target in Menus)
        {
            root.DeleteSubKeyTree(target.Path, throwOnMissingSubKey: false);
            using var menu = root.CreateSubKey(target.Path, writable: true);
            menu.SetValue("MUIVerb", VerbName);
            menu.SetValue("Icon", icon);
            // 빈 SubCommands = 아래 shell 하위 키를 하위 메뉴로 보인다.
            menu.SetValue("SubCommands", "");
            // 없으면 16개 이상 선택할 때 하위 메뉴 전체가 사라진다. 상위 메뉴 키에만 두면 된다.
            menu.SetValue("MultiSelectModel", "Player");
            if (target.AppliesTo is { } appliesTo)
                menu.SetValue("AppliesTo", appliesTo);
            for (int i = 0; i < target.Commands.Count; i++)
            {
                var command = target.Commands[i];
                using var item = menu.CreateSubKey(@"shell\" + CommandKeyName(target.Commands, i), writable: true);
                item.SetValue("MUIVerb", Label(command));
                item.SetValue("ExplorerCommandHandler", ClassId(command));
            }
        }
    }

    /// <summary>
    /// ohmyZip 메뉴 키와 처리기 키만 지운다. 그래서 비어 버린 shell 키와 형식 키(확장자·AllFilesystemObjects)만 지우고,
    /// 다른 프로그램의 값·키가 있으면 남긴다. SystemFileAssociations·CLSID 자체는 지우지 않는다.
    /// 셸 확장 캐시(<see cref="ShellExtensionsCachedPath"/>)에서는 우리 CLSID 로 시작하는 값만 지우고 키는 남긴다. 없는 키는 조용히 넘어간다.
    /// </summary>
    public static void Unregister(RegistryKey root)
    {
        ArgumentNullException.ThrowIfNull(root);
        foreach (var target in Menus)
        {
            root.DeleteSubKeyTree(target.Path, throwOnMissingSubKey: false);
            string shell = target.Path[..target.Path.LastIndexOf('\\')];
            DeleteIfEmpty(root, shell);
            DeleteIfEmpty(root, shell[..shell.LastIndexOf('\\')]);
        }
        foreach (string path in ClassIdPaths)
            root.DeleteSubKeyTree(path, throwOnMissingSubKey: false);

        using var cached = root.OpenSubKey(ShellExtensionsCachedPath, writable: true);
        if (cached is null)
            return;
        string[] prefixes = [.. Commands.Select(command => ClassId(command) + " ")];
        foreach (string name in cached.GetValueNames())
        {
            if (prefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                cached.DeleteValue(name, throwOnMissingValue: false);
        }
    }

    private static void DeleteIfEmpty(RegistryKey root, string path)
    {
        bool empty;
        using (var key = root.OpenSubKey(path))
            empty = key is not null && key.ValueCount == 0 && key.SubKeyCount == 0;
        if (empty)
            root.DeleteSubKey(path, throwOnMissingSubKey: false);
    }

    /// <summary>
    /// 모든 메뉴의 명령 키가 고정 CLSID 를 가리키고, 처리기 키의 DLL·실행 파일·인자가 지금 실행 파일과 맞으면 Registered.
    /// 문구(화면 언어)는 보지 않는다. 경로 비교는 대소문자를 가리지 않는다.
    /// </summary>
    public static ShellMenuStatus GetStatus(RegistryKey root, string exePath)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        bool any = false;
        bool all = true;
        foreach (var target in Menus)
        {
            using var menu = root.OpenSubKey(target.Path);
            if (menu is null)
            {
                all = false;
                continue;
            }
            any = true;
            for (int i = 0; i < target.Commands.Count; i++)
            {
                using var item = menu.OpenSubKey(@"shell\" + CommandKeyName(target.Commands, i));
                if (!SameText(item?.GetValue("ExplorerCommandHandler"), ClassId(target.Commands[i])))
                    all = false;
            }
        }

        string handler = HandlerPath(exePath);
        foreach (var command in Commands)
        {
            using var clsid = root.OpenSubKey(ClassIdPath(command));
            if (clsid is null)
            {
                all = false;
                continue;
            }
            any = true;
            using var server = clsid.OpenSubKey("InprocServer32");
            using var line = clsid.OpenSubKey("Command");
            if (!SameText(server?.GetValue(""), handler)
                || !SameText(line?.GetValue("Exe"), exePath)
                || !SameText(line?.GetValue("Verb"), Argument(command)))
                all = false;
        }

        if (!any)
            return ShellMenuStatus.NotRegistered;
        return all ? ShellMenuStatus.Registered : ShellMenuStatus.RegisteredElsewhere;
    }

    private static bool SameText(object? value, string expected) =>
        value is string text && string.Equals(text, expected, StringComparison.OrdinalIgnoreCase);
}
