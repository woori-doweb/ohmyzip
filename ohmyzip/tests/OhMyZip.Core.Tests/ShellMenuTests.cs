using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32;
using OhMyZip.App.Settings;
using CoreText = OhMyZip.Core.Resources;

namespace OhMyZip.Core.Tests;

/// <summary>
/// 탐색기 오른쪽 클릭 메뉴 등록 시험. 실제 HKCU\Software\Classes 를 건드리지 않도록 HKCU\Software\OhMyZipTests\{guid} 를 root 로 쓰고
/// 끝나면 그 트리를 지운다. 실행 파일과 처리기 DLL 은 임시 폴더의 가짜 파일이다(실행하지 않는다).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ShellMenuTests : IDisposable
{
    private const string TestsParentPath = @"Software\OhMyZipTests";
    private const string AssociationsPath = @"Software\Classes\SystemFileAssociations";
    private const string FileSystemObjectMenuPath = @"Software\Classes\AllFilesystemObjects\shell\ohmyZip";
    private const string ClassIdRootPath = @"Software\Classes\CLSID";

    private static readonly ShellMenuCommand[] AllCommands =
    [
        ShellMenuCommand.Open, ShellMenuCommand.Extract, ShellMenuCommand.ExtractHere, ShellMenuCommand.ExtractToFolder,
        ShellMenuCommand.SmartExtract, ShellMenuCommand.Compress, ShellMenuCommand.CompressToZip,
    ];

    private static readonly string[] ArchiveKeyNames =
        ["01Open", "02Extract", "03ExtractHere", "04ExtractToFolder", "05SmartExtract", "06Compress", "07CompressToZip"];

    private static readonly string[] FileSystemObjectKeyNames = ["01Compress", "02CompressToZip"];

    private static readonly string[] Verbs =
        ["--open", "--extract", "--extract-here", "--extract-to-folder", "--extract-smart", "--compress", "--compress-zip"];

    private static readonly string[] ClassIds =
    [
        "{5C1B7E0A-3F4D-4B8E-9A61-0D2E7F3A1B01}", "{5C1B7E0A-3F4D-4B8E-9A61-0D2E7F3A1B02}", "{5C1B7E0A-3F4D-4B8E-9A61-0D2E7F3A1B03}",
        "{5C1B7E0A-3F4D-4B8E-9A61-0D2E7F3A1B04}", "{5C1B7E0A-3F4D-4B8E-9A61-0D2E7F3A1B05}", "{5C1B7E0A-3F4D-4B8E-9A61-0D2E7F3A1B06}",
        "{5C1B7E0A-3F4D-4B8E-9A61-0D2E7F3A1B07}",
    ];

    private const string ExpectedAppliesTo =
        "NOT (System.FileExtension:=.zip OR System.FileExtension:=.7z OR System.FileExtension:=.rar"
        + " OR System.FileExtension:=.tar OR System.FileExtension:=.gz OR System.FileExtension:=.tgz)";

    private readonly string _rootPath = TestsParentPath + @"\" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _root;
    private readonly TempDir _temp = new();
    private readonly string _exePath;
    private readonly string _movedExePath;

    public ShellMenuTests()
    {
        _root = Registry.CurrentUser.CreateSubKey(_rootPath, writable: true);
        _exePath = FakeInstall(_temp.Combine("Program Files", "ohmyZip"), withHandler: true);
        _movedExePath = FakeInstall(_temp.Combine("Tools", "ohmyZip"), withHandler: true);
    }

    public void Dispose()
    {
        _root.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_rootPath, throwOnMissingSubKey: false);
        try
        {
            // 다른 시험이 아직 쓰는 중이면 하위 키가 남아 있으므로 지우지 못해도 된다.
            Registry.CurrentUser.DeleteSubKey(TestsParentPath, throwOnMissingSubKey: false);
        }
        catch (InvalidOperationException)
        {
        }
        _temp.Dispose();
    }

    /// <summary>폴더에 가짜 ohmyZip.exe(와 처리기 DLL)를 만들고 exe 경로를 돌려준다.</summary>
    private static string FakeInstall(string folder, bool withHandler)
    {
        Directory.CreateDirectory(folder);
        string exe = Path.Combine(folder, "ohmyZip.exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        if (withHandler)
            File.WriteAllBytes(Path.Combine(folder, "ShellCommand.dll"), [0x4D, 0x5A]);
        return exe;
    }

    private static string HandlerNextTo(string exePath) => Path.Combine(Path.GetDirectoryName(exePath)!, "ShellCommand.dll");

    [Fact]
    public void OZ_I02_Contract_commands_class_ids_labels_and_paths()
    {
        Assert.Same(FileAssociations.Extensions, ShellMenu.Extensions);
        Assert.Equal(AllCommands, ShellMenu.Commands.ToArray());
        Assert.Equal(new[] { ShellMenuCommand.Compress, ShellMenuCommand.CompressToZip }, ShellMenu.FileSystemObjectCommands.ToArray());
        Assert.Equal(ClassIds, AllCommands.Select(ShellMenu.ClassId).ToArray());
        Assert.Equal(ClassIds.Length, ClassIds.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(ClassIds.Select(id => ClassIdRootPath + @"\" + id).ToArray(), ShellMenu.ClassIdPaths.ToArray());
        Assert.Equal(@"Software\Classes\CLSID\{5C1B7E0A-3F4D-4B8E-9A61-0D2E7F3A1B07}", ShellMenu.ClassIdPath(ShellMenuCommand.CompressToZip));

        Assert.Equal(@"Software\Classes\SystemFileAssociations\.zip", ShellMenu.AssociationPath(".zip"));
        Assert.Equal(@"Software\Classes\SystemFileAssociations\.zip\shell\ohmyZip", ShellMenu.MenuPath(".zip"));
        Assert.Equal(FileSystemObjectMenuPath, ShellMenu.FileSystemObjectMenuPath);
        Assert.Equal(
            new[]
            {
                @"Software\Classes\SystemFileAssociations\.zip\shell\ohmyZip", @"Software\Classes\SystemFileAssociations\.7z\shell\ohmyZip",
                @"Software\Classes\SystemFileAssociations\.rar\shell\ohmyZip", @"Software\Classes\SystemFileAssociations\.tar\shell\ohmyZip",
                @"Software\Classes\SystemFileAssociations\.gz\shell\ohmyZip", @"Software\Classes\SystemFileAssociations\.tgz\shell\ohmyZip",
                FileSystemObjectMenuPath,
            },
            ShellMenu.MenuPaths.ToArray());

        // AppliesTo 는 파일 연결 확장자 목록에서 만든다.
        Assert.Equal(ExpectedAppliesTo, ShellMenu.AppliesTo);
        Assert.Equal("ShellCommand.dll", ShellMenu.HandlerDllName);
        Assert.Equal(@"C:\Program Files\ohmyZip\ShellCommand.dll", ShellMenu.HandlerPath(@"C:\Program Files\ohmyZip\ohmyZip.exe"));
        Assert.Equal("--list", ShellMenu.ListArgument);

        // 시험 프로세스는 한국어 화면이다.
        Assert.Equal("여기에 풀기", ShellMenu.Label(ShellMenuCommand.ExtractHere));
        Assert.Equal("압축 풀기…", ShellMenu.Label(ShellMenuCommand.Extract));
        Assert.Equal("압축하기…", ShellMenu.Label(ShellMenuCommand.Compress));
        Assert.Equal("ZIP으로 압축", ShellMenu.Label(ShellMenuCommand.CompressToZip));
        var rm = CoreText.ServiceText.ResourceManager;
        Assert.Equal("Compress…", rm.GetString("ShellMenu_Compress", UiLanguage.English));
        Assert.Equal("Compress to ZIP", rm.GetString("ShellMenu_CompressToZip", UiLanguage.English));
    }

    [Fact]
    public void OZ_E70_Argument_and_TryParseArgument_round_trip_ignoring_case()
    {
        Assert.Equal(Verbs, AllCommands.Select(ShellMenu.Argument).ToArray());

        foreach (var command in ShellMenu.Commands)
        {
            string arg = ShellMenu.Argument(command);
            Assert.True(ShellMenu.TryParseArgument(arg, out var parsed));
            Assert.Equal(command, parsed);
            Assert.True(ShellMenu.TryParseArgument(arg.ToUpperInvariant(), out parsed));
            Assert.Equal(command, parsed);
        }

        Assert.True(ShellMenu.TryParseArgument("--Open", out var open));
        Assert.Equal(ShellMenuCommand.Open, open);
        Assert.True(ShellMenu.TryParseArgument("--EXTRACT-HERE", out var here));
        Assert.Equal(ShellMenuCommand.ExtractHere, here);
        Assert.True(ShellMenu.TryParseArgument("--Compress-Zip", out var zip));
        Assert.Equal(ShellMenuCommand.CompressToZip, zip);

        Assert.False(ShellMenu.TryParseArgument("--extract-elsewhere", out _));
        Assert.False(ShellMenu.TryParseArgument("--extract-", out _));
        Assert.False(ShellMenu.TryParseArgument("--compress-", out _));
        Assert.False(ShellMenu.TryParseArgument("extract-here", out _));
        Assert.False(ShellMenu.TryParseArgument("--list", out _));
        Assert.False(ShellMenu.TryParseArgument("--register-file-types", out _));
        Assert.False(ShellMenu.TryParseArgument("", out _));
        Assert.False(ShellMenu.TryParseArgument(@"C:\a.zip", out _));
    }

    [Fact]
    public void OZ_I02_Register_writes_archive_menus_with_seven_handler_commands()
    {
        ShellMenu.Register(_root, _exePath);
        string icon = "\"" + _exePath + "\",0";

        foreach (string ext in ShellMenu.Extensions)
        {
            using var menu = _root.OpenSubKey($@"{AssociationsPath}\{ext}\shell\ohmyZip");
            Assert.NotNull(menu);
            Assert.Equal(new[] { "Icon", "MUIVerb", "MultiSelectModel", "SubCommands" }, menu.GetValueNames().Order(StringComparer.Ordinal).ToArray());
            Assert.Equal("Player", menu.GetValue("MultiSelectModel"));
            Assert.Equal("ohmyZip", menu.GetValue("MUIVerb"));
            Assert.Equal(icon, menu.GetValue("Icon"));
            Assert.Equal("", menu.GetValue("SubCommands"));
            Assert.Equal(new[] { "shell" }, menu.GetSubKeyNames());
            using var shell = menu.OpenSubKey("shell");
            Assert.NotNull(shell);
            Assert.Equal(ArchiveKeyNames, shell.GetSubKeyNames().Order(StringComparer.Ordinal).ToArray());

            for (int i = 0; i < AllCommands.Length; i++)
            {
                using var item = shell.OpenSubKey(ArchiveKeyNames[i]);
                Assert.NotNull(item);
                Assert.Equal(new[] { "ExplorerCommandHandler", "MUIVerb" }, item.GetValueNames().Order(StringComparer.Ordinal).ToArray());
                Assert.Equal(ShellMenu.Label(AllCommands[i]), item.GetValue("MUIVerb"));
                Assert.Equal(ClassIds[i], item.GetValue("ExplorerCommandHandler"));
                // 명령 문자열(command 하위 키)은 쓰지 않는다. 처리기가 실행한다.
                Assert.Empty(item.GetSubKeyNames());
            }

            // 확장자 연결 기본값과 파일 연결 키는 쓰지 않는다.
            Assert.Null(_root.OpenSubKey($@"Software\Classes\{ext}"));
        }

        Assert.Equal("{5C1B7E0A-3F4D-4B8E-9A61-0D2E7F3A1B03}",
            ReadValue($@"{AssociationsPath}\.zip\shell\ohmyZip\shell\03ExtractHere", "ExplorerCommandHandler"));
        Assert.Equal("ZIP으로 압축", ReadValue($@"{AssociationsPath}\.7z\shell\ohmyZip\shell\07CompressToZip", "MUIVerb"));
    }

    [Fact]
    public void OZ_I02_Register_writes_file_system_object_menu_with_applies_to_and_two_commands()
    {
        ShellMenu.Register(_root, _exePath);

        using var menu = _root.OpenSubKey(FileSystemObjectMenuPath);
        Assert.NotNull(menu);
        Assert.Equal(new[] { "AppliesTo", "Icon", "MUIVerb", "MultiSelectModel", "SubCommands" }, menu.GetValueNames().Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("Player", menu.GetValue("MultiSelectModel"));
        Assert.Equal("ohmyZip", menu.GetValue("MUIVerb"));
        Assert.Equal("\"" + _exePath + "\",0", menu.GetValue("Icon"));
        Assert.Equal("", menu.GetValue("SubCommands"));
        Assert.Equal(ExpectedAppliesTo, menu.GetValue("AppliesTo"));
        foreach (string ext in FileAssociations.Extensions)
            Assert.Contains("System.FileExtension:=" + ext, (string)menu.GetValue("AppliesTo")!);

        using var shell = menu.OpenSubKey("shell");
        Assert.NotNull(shell);
        Assert.Equal(FileSystemObjectKeyNames, shell.GetSubKeyNames().Order(StringComparer.Ordinal).ToArray());
        var expected = new[] { (ShellMenuCommand.Compress, ClassIds[5]), (ShellMenuCommand.CompressToZip, ClassIds[6]) };
        for (int i = 0; i < expected.Length; i++)
        {
            using var item = shell.OpenSubKey(FileSystemObjectKeyNames[i]);
            Assert.NotNull(item);
            Assert.Equal(new[] { "ExplorerCommandHandler", "MUIVerb" }, item.GetValueNames().Order(StringComparer.Ordinal).ToArray());
            Assert.Equal(ShellMenu.Label(expected[i].Item1), item.GetValue("MUIVerb"));
            Assert.Equal(expected[i].Item2, item.GetValue("ExplorerCommandHandler"));
            Assert.Empty(item.GetSubKeyNames());
        }
        Assert.Equal("압축하기…", ReadValue(FileSystemObjectMenuPath + @"\shell\01Compress", "MUIVerb"));
    }

    [Fact]
    public void OZ_I02_Register_writes_class_id_keys_with_handler_dll_and_command_values()
    {
        ShellMenu.Register(_root, _exePath);
        string handler = HandlerNextTo(_exePath);

        using (var clsidRoot = _root.OpenSubKey(ClassIdRootPath))
        {
            Assert.NotNull(clsidRoot);
            Assert.Equal(ClassIds.Order(StringComparer.OrdinalIgnoreCase), clsidRoot.GetSubKeyNames().Order(StringComparer.OrdinalIgnoreCase));
        }
        for (int i = 0; i < AllCommands.Length; i++)
        {
            using var clsid = _root.OpenSubKey(ClassIdRootPath + @"\" + ClassIds[i]);
            Assert.NotNull(clsid);
            Assert.Equal("ohmyZip " + AllCommands[i], clsid.GetValue(""));
            Assert.Equal(new[] { "Command", "InprocServer32" }, clsid.GetSubKeyNames().Order(StringComparer.Ordinal).ToArray());
            using (var server = clsid.OpenSubKey("InprocServer32"))
            {
                Assert.NotNull(server);
                Assert.Equal(handler, server.GetValue(""));
                Assert.Equal("Apartment", server.GetValue("ThreadingModel"));
            }
            using var command = clsid.OpenSubKey("Command");
            Assert.NotNull(command);
            Assert.Equal(new[] { "Exe", "Title", "Verb" }, command.GetValueNames().Order(StringComparer.Ordinal).ToArray());
            Assert.Equal(_exePath, command.GetValue("Exe"));
            Assert.Equal(Verbs[i], command.GetValue("Verb"));
            Assert.Equal(ShellMenu.Label(AllCommands[i]), command.GetValue("Title"));
        }

        string zipKey = ClassIdRootPath + @"\{5C1B7E0A-3F4D-4B8E-9A61-0D2E7F3A1B07}";
        Assert.Equal("ohmyZip CompressToZip", ReadValue(zipKey, ""));
        Assert.Equal("--compress-zip", ReadValue(zipKey + @"\Command", "Verb"));
        Assert.Equal("ZIP으로 압축", ReadValue(zipKey + @"\Command", "Title"));
        Assert.Equal(Path.Combine(_temp.Path, "Program Files", "ohmyZip", "ShellCommand.dll"), ReadValue(zipKey + @"\InprocServer32", ""));
    }

    [Fact]
    public void OZ_I02_Register_sets_multi_select_model_on_parent_menus_only()
    {
        ShellMenu.Register(_root, _exePath);

        Assert.Equal(7, ShellMenu.MenuPaths.Count);
        foreach (string path in ShellMenu.MenuPaths)
        {
            // 없으면 16개 이상 선택할 때 하위 메뉴가 사라진다.
            Assert.Equal("Player", ReadValue(path, "MultiSelectModel"));
            using var shell = _root.OpenSubKey(path + @"\shell");
            Assert.NotNull(shell);
            Assert.Null(shell.GetValue("MultiSelectModel"));
            foreach (string child in shell.GetSubKeyNames())
            {
                using var item = shell.OpenSubKey(child);
                Assert.NotNull(item);
                Assert.DoesNotContain("MultiSelectModel", item.GetValueNames());
            }
        }
        Assert.Equal("Player", ReadValue(FileSystemObjectMenuPath, "MultiSelectModel"));
        Assert.Equal("Player", ReadValue($@"{AssociationsPath}\.tgz\shell\ohmyZip", "MultiSelectModel"));
    }

    [Fact]
    public void OZ_I02_Unregister_deletes_only_our_shell_extension_cache_values()
    {
        const string CachedPath = @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Cached";
        const string Iid = "{A08CE4D0-FA25-44AB-B57C-C7B1C323E0B9}";
        Assert.Equal(CachedPath, ShellMenu.ShellExtensionsCachedPath);

        string[] foreign =
        [
            "{11111111-2222-3333-4444-555555555555} " + Iid + " 0xFFFF",
            // 우리 CLSID 와 한 글자만 다른 값, 우리 CLSID 가 이름 중간에 있는 값, 뒤에 공백이 없는 값은 우리 것이 아니다.
            "{5C1B7E0A-3F4D-4B8E-9A61-0D2E7F3A1B08} " + Iid + " 0xFFFF",
            "{11111111-2222-3333-4444-555555555555} " + ClassIds[0] + " 0xFFFF",
            ClassIds[1],
        ];
        string[] ours =
        [
            ClassIds[0] + " " + Iid + " 0xFFFF",
            ClassIds[6] + " " + Iid + " 0xFFFF",
            // 대소문자는 가리지 않는다.
            ClassIds[3].ToLowerInvariant() + " " + Iid + " 0xFFFF",
        ];

        using (var cached = _root.CreateSubKey(CachedPath, writable: true))
        {
            foreach (string name in foreign)
                cached.SetValue(name, new byte[] { 1, 2, 3 }, RegistryValueKind.Binary);
        }
        // 실제 HKCU 에는 늘 있는 상위 키다. 해제는 이 키들을 지우지 않는다.
        _root.CreateSubKey(ClassIdRootPath)!.Dispose();
        _root.CreateSubKey(AssociationsPath)!.Dispose();
        var before = Snapshot();

        ShellMenu.Register(_root, _exePath);
        // 셸이 메뉴를 처음 쓸 때 캐시에 기록한다.
        using (var cached = _root.OpenSubKey(CachedPath, writable: true)!)
        {
            foreach (string name in ours)
                cached.SetValue(name, new byte[] { 4, 5, 6 }, RegistryValueKind.Binary);
        }
        ShellMenu.Unregister(_root);

        Assert.Equal(before, Snapshot());
        using (var cached = _root.OpenSubKey(CachedPath))
        {
            Assert.NotNull(cached);
            Assert.Equal(foreign.Order(StringComparer.Ordinal), cached.GetValueNames().Order(StringComparer.Ordinal));
            Assert.Equal(new byte[] { 1, 2, 3 }, cached.GetValue(foreign[0]));
        }

        // 우리 값만 있던 캐시 키는 비어도 남는다.
        using (var cached = _root.OpenSubKey(CachedPath, writable: true)!)
        {
            foreach (string name in foreign)
                cached.DeleteValue(name);
            cached.SetValue(ours[1], new byte[] { 7 }, RegistryValueKind.Binary);
        }
        ShellMenu.Unregister(_root);
        using (var cached = _root.OpenSubKey(CachedPath))
        {
            Assert.NotNull(cached);
            Assert.Empty(cached.GetValueNames());
        }

        // 캐시 키가 없어도 오류 없이 끝난다.
        _root.DeleteSubKeyTree(@"Software\Microsoft");
        ShellMenu.Register(_root, _exePath);
        ShellMenu.Unregister(_root);
        Assert.Null(_root.OpenSubKey(@"Software\Microsoft"));
    }

    [Fact]
    public void OZ_I02_Register_fails_without_handler_dll_and_writes_nothing()
    {
        string exe = FakeInstall(_temp.Combine("NoHandler"), withHandler: false);
        string handler = HandlerNextTo(exe);

        var ex = Assert.Throws<FileNotFoundException>(() => ShellMenu.Register(_root, exe));
        Assert.Equal(handler, ex.FileName);
        Assert.Contains(handler, ex.Message);
        Assert.Equal(string.Format(CoreText.ServiceText.ShellMenu_HandlerMissing, handler), ex.Message);
        Assert.Null(_root.OpenSubKey("Software"));

        // 앱의 등록 오류 처리(IOException)로 잡힌다.
        Assert.IsAssignableFrom<IOException>(ex);

        // DLL 을 두면 같은 경로로 등록된다.
        File.WriteAllBytes(handler, [0x4D, 0x5A]);
        ShellMenu.Register(_root, exe);
        Assert.Equal(ShellMenuStatus.Registered, ShellMenu.GetStatus(_root, exe));
    }

    [Fact]
    public void OZ_E70_Register_rejects_relative_or_quoted_exe_path_without_writing()
    {
        Assert.Throws<ArgumentException>(() => ShellMenu.Register(_root, "ohmyZip.exe"));
        Assert.Throws<ArgumentException>(() => ShellMenu.Register(_root, "C:\\a\"b\\ohmyZip.exe"));
        Assert.Null(_root.OpenSubKey("Software"));
    }

    [Fact]
    public void OZ_I02_GetStatus_reports_not_registered_registered_and_registered_elsewhere()
    {
        Assert.Equal(ShellMenuStatus.NotRegistered, ShellMenu.GetStatus(_root, _exePath));

        ShellMenu.Register(_root, _exePath);
        Assert.Equal(ShellMenuStatus.Registered, ShellMenu.GetStatus(_root, _exePath));
        Assert.Equal(ShellMenuStatus.Registered, ShellMenu.GetStatus(_root, _exePath.ToUpperInvariant()));
        Assert.Equal(ShellMenuStatus.RegisteredElsewhere, ShellMenu.GetStatus(_root, _movedExePath));

        // 한 확장자의 메뉴가 없으면 다시 등록해야 한다.
        _root.DeleteSubKeyTree($@"{AssociationsPath}\.rar\shell\ohmyZip");
        Assert.Equal(ShellMenuStatus.RegisteredElsewhere, ShellMenu.GetStatus(_root, _exePath));

        // 그 밖의 파일·폴더 메뉴의 명령 하나만 빠져도 마찬가지다.
        ShellMenu.Register(_root, _exePath);
        _root.DeleteSubKeyTree(FileSystemObjectMenuPath + @"\shell\02CompressToZip");
        Assert.Equal(ShellMenuStatus.RegisteredElsewhere, ShellMenu.GetStatus(_root, _exePath));

        // 명령 키가 다른 처리기를 가리켜도.
        ShellMenu.Register(_root, _exePath);
        using (var item = _root.OpenSubKey($@"{AssociationsPath}\.tgz\shell\ohmyZip\shell\05SmartExtract", writable: true)!)
            item.SetValue("ExplorerCommandHandler", "{00000000-0000-0000-0000-000000000001}");
        Assert.Equal(ShellMenuStatus.RegisteredElsewhere, ShellMenu.GetStatus(_root, _exePath));

        // 처리기 키가 없거나 다른 DLL·실행 파일을 가리켜도.
        ShellMenu.Register(_root, _exePath);
        _root.DeleteSubKeyTree(ClassIdRootPath + @"\" + ClassIds[3]);
        Assert.Equal(ShellMenuStatus.RegisteredElsewhere, ShellMenu.GetStatus(_root, _exePath));
        ShellMenu.Register(_root, _exePath);
        using (var server = _root.OpenSubKey(ClassIdRootPath + @"\" + ClassIds[0] + @"\InprocServer32", writable: true)!)
            server.SetValue("", HandlerNextTo(_movedExePath));
        Assert.Equal(ShellMenuStatus.RegisteredElsewhere, ShellMenu.GetStatus(_root, _exePath));
        ShellMenu.Register(_root, _exePath);
        using (var command = _root.OpenSubKey(ClassIdRootPath + @"\" + ClassIds[6] + @"\Command", writable: true)!)
            command.SetValue("Exe", _movedExePath);
        Assert.Equal(ShellMenuStatus.RegisteredElsewhere, ShellMenu.GetStatus(_root, _exePath));

        // 옮긴 위치에서 다시 등록하면 그 위치로 바뀌고, 이전 형식의 하위 키도 남지 않는다.
        using (var stale = _root.CreateSubKey($@"{AssociationsPath}\.gz\shell\ohmyZip\shell\04ExtractToFolder\command", writable: true))
            stale.SetValue("", "\"" + _exePath + "\" --extract-to-folder \"%1\"");
        ShellMenu.Register(_root, _movedExePath);
        Assert.Equal(ShellMenuStatus.Registered, ShellMenu.GetStatus(_root, _movedExePath));
        Assert.Equal(ShellMenuStatus.RegisteredElsewhere, ShellMenu.GetStatus(_root, _exePath));
        Assert.Null(_root.OpenSubKey($@"{AssociationsPath}\.gz\shell\ohmyZip\shell\04ExtractToFolder\command"));
        Assert.Equal(_movedExePath, ReadValue(ClassIdRootPath + @"\" + ClassIds[3] + @"\Command", "Exe"));
        Assert.Equal(HandlerNextTo(_movedExePath), ReadValue(ClassIdRootPath + @"\" + ClassIds[3] + @"\InprocServer32", ""));

        ShellMenu.Unregister(_root);
        Assert.Equal(ShellMenuStatus.NotRegistered, ShellMenu.GetStatus(_root, _movedExePath));

        // 처리기 키만 남아도 등록이 덜 지워진 상태다.
        using (var leftover = _root.CreateSubKey(ClassIdRootPath + @"\" + ClassIds[1], writable: true))
            leftover.SetValue("", "ohmyZip Extract");
        Assert.Equal(ShellMenuStatus.RegisteredElsewhere, ShellMenu.GetStatus(_root, _movedExePath));
    }

    [Fact]
    public void OZ_I02_Unregister_keeps_other_programs_and_restores_snapshot()
    {
        using (var other = _root.CreateSubKey($@"{AssociationsPath}\.zip\shell\Other\command", writable: true))
            other.SetValue("", "\"C:\\Other\\other.exe\" \"%1\"");
        using (var sevenZip = _root.CreateSubKey($@"{AssociationsPath}\.7z", writable: true))
            sevenZip.SetValue("PerceivedType", "compressed");
        using (var otherAll = _root.CreateSubKey(@"Software\Classes\AllFilesystemObjects\shell\Other", writable: true))
            otherAll.SetValue("MUIVerb", "Other");
        using (var otherClsid = _root.CreateSubKey(ClassIdRootPath + @"\{11111111-2222-3333-4444-555555555555}\InprocServer32", writable: true))
        {
            otherClsid.SetValue("", @"C:\Other\other.dll");
            otherClsid.SetValue("ThreadingModel", "Both");
        }
        var before = Snapshot();

        ShellMenu.Register(_root, _exePath);
        Assert.NotEqual(before, Snapshot());
        ShellMenu.Unregister(_root);

        Assert.Equal(before, Snapshot());
        Assert.Equal("\"C:\\Other\\other.exe\" \"%1\"", ReadValue($@"{AssociationsPath}\.zip\shell\Other\command", ""));
        Assert.Equal("Other", ReadValue(@"Software\Classes\AllFilesystemObjects\shell\Other", "MUIVerb"));
        Assert.Equal(@"C:\Other\other.dll", ReadValue(ClassIdRootPath + @"\{11111111-2222-3333-4444-555555555555}\InprocServer32", ""));
        using (var sevenZip = _root.OpenSubKey($@"{AssociationsPath}\.7z"))
        {
            Assert.NotNull(sevenZip);
            Assert.Equal("compressed", sevenZip.GetValue("PerceivedType"));
            // 비어 버린 shell 키는 남지 않는다.
            Assert.Empty(sevenZip.GetSubKeyNames());
        }
        Assert.Equal(ShellMenuStatus.NotRegistered, ShellMenu.GetStatus(_root, _exePath));
    }

    [Fact]
    public void OZ_I02_Unregister_is_safe_without_registration_and_repeated()
    {
        ShellMenu.Unregister(_root);
        Assert.Null(_root.OpenSubKey("Software"));

        ShellMenu.Register(_root, _exePath);
        ShellMenu.Unregister(_root);
        ShellMenu.Unregister(_root);

        foreach (string path in ShellMenu.MenuPaths)
            Assert.Null(_root.OpenSubKey(path));
        foreach (string path in ShellMenu.ClassIdPaths)
            Assert.Null(_root.OpenSubKey(path));
        foreach (string ext in ShellMenu.Extensions)
            Assert.Null(_root.OpenSubKey(ShellMenu.AssociationPath(ext)));
        // 비어 버린 형식 키(AllFilesystemObjects)도 지우고, SystemFileAssociations·CLSID 자체는 남긴다.
        Assert.Null(_root.OpenSubKey(@"Software\Classes\AllFilesystemObjects"));
        using (var associations = _root.OpenSubKey(AssociationsPath))
        {
            Assert.NotNull(associations);
            Assert.Empty(associations.GetSubKeyNames());
        }
        using (var clsid = _root.OpenSubKey(ClassIdRootPath))
        {
            Assert.NotNull(clsid);
            Assert.Empty(clsid.GetSubKeyNames());
        }
    }

    [Fact]
    public void OZ_E70_Shell_menu_and_file_associations_are_independent()
    {
        FileAssociations.Register(_root, _exePath);
        ShellMenu.Register(_root, _exePath);

        ShellMenu.Unregister(_root);
        Assert.Equal(FileAssociationStatus.Registered, FileAssociations.GetStatus(_root, _exePath));
        Assert.NotNull(_root.OpenSubKey(@"Software\Classes\ohmyZip.Archive"));
        Assert.NotNull(_root.OpenSubKey(@"Software\Classes\Applications\ohmyZip.exe"));
        foreach (string ext in FileAssociations.Extensions)
        {
            using var openWith = _root.OpenSubKey($@"Software\Classes\{ext}\OpenWithProgids");
            Assert.NotNull(openWith);
            Assert.Contains("ohmyZip.Archive", openWith.GetValueNames());
        }

        ShellMenu.Register(_root, _exePath);
        FileAssociations.Unregister(_root);
        Assert.Equal(ShellMenuStatus.Registered, ShellMenu.GetStatus(_root, _exePath));
        Assert.Equal(FileAssociationStatus.NotRegistered, FileAssociations.GetStatus(_root, _exePath));
    }

    private string? ReadValue(string path, string name)
    {
        using var key = _root.OpenSubKey(path);
        return key?.GetValue(name) as string;
    }

    /// <summary>시험 root 아래 전체 트리(키·값 이름·종류·값)를 정렬된 줄 목록으로.</summary>
    private List<string> Snapshot()
    {
        var lines = new List<string>();
        Walk(_root, "");
        return lines;

        void Walk(RegistryKey key, string path)
        {
            foreach (string name in key.GetValueNames().Order(StringComparer.OrdinalIgnoreCase))
            {
                object? value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                string text = value switch
                {
                    null => "",
                    byte[] bytes => Convert.ToHexString(bytes),
                    string[] many => string.Join("\u0000", many),
                    _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
                };
                lines.Add($"{path}|{name}|{key.GetValueKind(name)}|{text}");
            }
            foreach (string sub in key.GetSubKeyNames().Order(StringComparer.OrdinalIgnoreCase))
            {
                string childPath = path + @"\" + sub;
                lines.Add(childPath);
                using var child = key.OpenSubKey(sub)!;
                Walk(child, childPath);
            }
        }
    }
}
