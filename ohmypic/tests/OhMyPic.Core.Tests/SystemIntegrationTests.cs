using Microsoft.Win32;

namespace OhMyPic.Core.Tests;

/// <summary>
/// File association (OP-E21) and Explorer menu (OP-E22) registration. The real HKCU\Software\Classes is never touched:
/// HKCU\Software\ohMypicTests\{guid} is the root and the whole tree is deleted afterwards. The executable is a fake
/// .exe file in a temp folder.
/// </summary>
public sealed class SystemIntegrationTests : IDisposable
{
    private const string TestsParentPath = @"Software\ohMypicTests";
    private const string TypeName = "ohMypic image";
    private const string Description = "Image browser and viewer";

    private static readonly string[] CommandKeyNames = ["01Open", "02Slideshow", "03Rotate", "04Resize", "05Convert", "06Batch"];
    private static readonly string[] Extensions = [".jpg", ".png", ".gif"];

    private readonly string _rootPath = TestsParentPath + @"\" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _root;
    private readonly TempDir _temp = new();
    private readonly string _exe;
    private readonly string _movedExe;

    public SystemIntegrationTests()
    {
        _root = Registry.CurrentUser.CreateSubKey(_rootPath, writable: true);
        _exe = _temp.Combine("Program Files", "ohMypic", "ohMypic.exe");
        _movedExe = _temp.Combine("Moved", "ohMypic.exe");
        foreach (string exe in new[] { _exe, _movedExe })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
            File.WriteAllBytes(exe, [0x4D, 0x5A]);
        }
    }

    public void Dispose()
    {
        _root.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_rootPath, throwOnMissingSubKey: false);
        try
        {
            // Other test classes may still use the parent; leaving it is fine.
            Registry.CurrentUser.DeleteSubKey(TestsParentPath, throwOnMissingSubKey: false);
        }
        catch (InvalidOperationException)
        {
        }
        _temp.Dispose();
    }

    // ───────────── OP-E21 file associations ─────────────

    [Fact]
    public void OP_E21_Extension_lists_follow_installed_codecs()
    {
        Assert.Equal(
            new[] { ".bmp", ".dib", ".gif", ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".tif", ".tiff", ".ico", ".wdp", ".jxr" },
            FileAssociations.BuiltInExtensions.ToArray());
        Assert.Equal(FileAssociations.BuiltInExtensions.ToArray(), FileAssociations.AvailableExtensions(Array.Empty<string>()).ToArray());
        var withWebp = FileAssociations.AvailableExtensions(new[] { ".WEBP", ".avif", ".xyz" });
        Assert.Equal(FileAssociations.BuiltInExtensions.Concat(new[] { ".webp", ".avif" }).ToArray(), withWebp.ToArray());
        Assert.All(FileAssociations.AvailableExtensions(), ext => Assert.Contains(ext, FileAssociations.CandidateExtensions));
    }

    [Fact]
    public void OP_E21_Register_status_unregister_round_trip_leaves_root_empty()
    {
        foreach (string ext in Extensions)
            Assert.Equal(FileAssociationStatus.NotRegistered, FileAssociations.Status(_root, _exe, ext));

        FileAssociations.Register(_root, _exe, Extensions, TypeName, Description);

        string command = "\"" + _exe + "\" \"%1\"";
        string icon = "\"" + _exe + "\",0";
        Assert.Equal(TypeName, ReadValue(@"Software\Classes\ohMypic.Image", "FriendlyTypeName"));
        Assert.Equal(icon, ReadValue(@"Software\Classes\ohMypic.Image\DefaultIcon", ""));
        Assert.Equal(command, ReadValue(@"Software\Classes\ohMypic.Image\shell\open\command", ""));
        Assert.Equal("ohMypic", ReadValue(@"Software\Classes\Applications\ohMypic.exe", "FriendlyAppName"));
        Assert.Equal(command, ReadValue(@"Software\Classes\Applications\ohMypic.exe\shell\open\command", ""));
        Assert.Equal("ohMypic", ReadValue(@"Software\ohMypic\Capabilities", "ApplicationName"));
        Assert.Equal(Description, ReadValue(@"Software\ohMypic\Capabilities", "ApplicationDescription"));
        Assert.Equal(@"Software\ohMypic\Capabilities", ReadValue(@"Software\RegisteredApplications", "ohMypic"));
        foreach (string ext in Extensions)
        {
            using (var openWith = _root.OpenSubKey($@"Software\Classes\{ext}\OpenWithProgids"))
            {
                Assert.NotNull(openWith);
                Assert.Contains("ohMypic.Image", openWith.GetValueNames());
            }
            // The extension's default value (current association) is never written.
            using (var extKey = _root.OpenSubKey($@"Software\Classes\{ext}"))
                Assert.Null(extKey!.GetValue(""));
            Assert.Equal("", ReadValue(@"Software\Classes\Applications\ohMypic.exe\SupportedTypes", ext));
            Assert.Equal("ohMypic.Image", ReadValue(@"Software\ohMypic\Capabilities\FileAssociations", ext));
            Assert.Equal(FileAssociationStatus.Registered, FileAssociations.Status(_root, _exe, ext));
            Assert.Equal(FileAssociationStatus.Registered, FileAssociations.Status(_root, _exe.ToUpperInvariant(), ext));
            Assert.Equal(FileAssociationStatus.RegisteredElsewhere, FileAssociations.Status(_root, _movedExe, ext));
        }
        Assert.Equal(FileAssociationStatus.NotRegistered, FileAssociations.Status(_root, _exe, ".bmp"));
        var all = FileAssociations.Status(_root, _exe, new[] { ".jpg", ".bmp" });
        Assert.Equal(FileAssociationStatus.Registered, all[".jpg"]);
        Assert.Equal(FileAssociationStatus.NotRegistered, all[".bmp"]);

        // Registering another set replaces the selection.
        FileAssociations.Register(_root, _movedExe, [".png"], TypeName, Description);
        Assert.Equal(FileAssociationStatus.Registered, FileAssociations.Status(_root, _movedExe, ".png"));
        Assert.Equal(FileAssociationStatus.NotRegistered, FileAssociations.Status(_root, _movedExe, ".jpg"));
        Assert.Null(_root.OpenSubKey(@"Software\Classes\.jpg"));
        Assert.Null(ReadValue(@"Software\ohMypic\Capabilities\FileAssociations", ".jpg"));
        Assert.Null(ReadValue(@"Software\Classes\Applications\ohMypic.exe\SupportedTypes", ".jpg"));

        FileAssociations.Unregister(_root);
        foreach (string ext in Extensions)
            Assert.Equal(FileAssociationStatus.NotRegistered, FileAssociations.Status(_root, _movedExe, ext));
        Assert.Empty(_root.GetSubKeyNames());
        Assert.Empty(_root.GetValueNames());

        // Unregistering again is harmless.
        FileAssociations.Unregister(_root);
        Assert.Empty(_root.GetSubKeyNames());
    }

    [Fact]
    public void OP_E21_Does_not_overwrite_foreign_default_progid_and_keeps_other_programs()
    {
        using (var jpg = _root.CreateSubKey(@"Software\Classes\.jpg", writable: true))
        {
            jpg.SetValue("", "Other.Jpeg");
            using var openWith = jpg.CreateSubKey("OpenWithProgids", writable: true);
            openWith.SetValue("Other.Jpeg", Array.Empty<byte>(), RegistryValueKind.None);
        }
        using (var registered = _root.CreateSubKey(@"Software\RegisteredApplications", writable: true))
            registered.SetValue("Other", @"Software\Other\Capabilities");

        FileAssociations.Register(_root, _exe, Extensions, TypeName, Description);
        Assert.Equal("Other.Jpeg", ReadValue(@"Software\Classes\.jpg", ""));
        Assert.Equal(FileAssociationStatus.Registered, FileAssociations.Status(_root, _exe, ".jpg"));

        FileAssociations.Unregister(_root);
        Assert.Equal("Other.Jpeg", ReadValue(@"Software\Classes\.jpg", ""));
        using (var openWith = _root.OpenSubKey(@"Software\Classes\.jpg\OpenWithProgids"))
        {
            Assert.NotNull(openWith);
            Assert.Equal(new[] { "Other.Jpeg" }, openWith.GetValueNames());
        }
        Assert.Equal(@"Software\Other\Capabilities", ReadValue(@"Software\RegisteredApplications", "Other"));
        Assert.Null(ReadValue(@"Software\RegisteredApplications", "ohMypic"));
        Assert.Null(_root.OpenSubKey(@"Software\Classes\ohMypic.Image"));
        Assert.Null(_root.OpenSubKey(@"Software\Classes\.png"));
        Assert.Null(_root.OpenSubKey(@"Software\ohMypic"));
    }

    [Fact]
    public void OP_E21_Rejects_invalid_exe_paths_and_extensions_without_writing()
    {
        string notExe = _temp.Combine("ohMypic.txt");
        File.WriteAllText(notExe, "x");
        string[] invalid =
        [
            "",
            "ohMypic.exe",
            _temp.Combine("missing", "ohMypic.exe"),
            notExe,
            _temp.Combine("a\"b", "ohMypic.exe"),
        ];
        foreach (string exe in invalid)
        {
            Assert.ThrowsAny<ArgumentException>(() => FileAssociations.Register(_root, exe, Extensions, TypeName, Description));
            Assert.ThrowsAny<ArgumentException>(() => ShellMenu.Register(_root, exe, Extensions, Label));
        }
        Assert.Throws<ArgumentException>(() => FileAssociations.Register(_root, _exe, [".exe"], TypeName, Description));
        Assert.Throws<ArgumentException>(() => FileAssociations.Register(_root, _exe, [], TypeName, Description));
        Assert.Throws<ArgumentException>(() => ShellMenu.Register(_root, _exe, [".zip"], Label));
        Assert.Empty(_root.GetSubKeyNames());
    }

    // ───────────── OP-E22 Explorer menu ─────────────

    [Fact]
    public void OP_E22_Shell_menu_round_trip_writes_six_exact_commands_and_unregister_removes_them()
    {
        Assert.Equal(ShellMenuStatus.NotRegistered, ShellMenu.GetStatus(_root, _exe, Extensions));
        Assert.False(ShellMenu.IsRegistered(_root, _exe, Extensions));

        ShellMenu.Register(_root, _exe, Extensions, Label);

        string q = "\"" + _exe + "\"";
        var expected = new Dictionary<string, string>
        {
            ["01Open"] = q + " \"%1\"",
            ["02Slideshow"] = q + " --slideshow \"%1\"",
            ["03Rotate"] = q + " --rotate \"%1\"",
            ["04Resize"] = q + " --resize \"%1\"",
            ["05Convert"] = q + " --convert \"%1\"",
            ["06Batch"] = q + " --batch \"%1\"",
        };
        foreach (string ext in Extensions)
        {
            string menuPath = $@"Software\Classes\SystemFileAssociations\{ext}\shell\ohMypic";
            Assert.Equal(menuPath, ShellMenu.MenuPath(ext));
            using var menu = _root.OpenSubKey(menuPath);
            Assert.NotNull(menu);
            Assert.Equal("ohMypic", menu.GetValue("MUIVerb"));
            Assert.Equal(q + ",0", menu.GetValue("Icon"));
            Assert.Equal("", menu.GetValue("SubCommands"));
            using var shell = menu.OpenSubKey("shell");
            Assert.NotNull(shell);
            Assert.Equal(CommandKeyNames, shell.GetSubKeyNames().Order(StringComparer.Ordinal).ToArray());
            for (int i = 0; i < CommandKeyNames.Length; i++)
            {
                using var item = shell.OpenSubKey(CommandKeyNames[i]);
                Assert.NotNull(item);
                Assert.Equal(Label(ShellMenu.Commands[i]), item.GetValue("MUIVerb"));
                using var line = item.OpenSubKey("command");
                Assert.NotNull(line);
                Assert.Equal(expected[CommandKeyNames[i]], line.GetValue(""));
            }
            // The menu does not create file-association keys.
            Assert.Null(_root.OpenSubKey($@"Software\Classes\{ext}"));
        }
        Assert.Null(_root.OpenSubKey(ShellMenu.MenuPath(".bmp")));
        Assert.Equal(ShellMenuStatus.Registered, ShellMenu.GetStatus(_root, _exe, Extensions));
        Assert.True(ShellMenu.IsRegistered(_root, _exe.ToUpperInvariant(), Extensions));
        Assert.Equal(ShellMenuStatus.RegisteredElsewhere, ShellMenu.GetStatus(_root, _movedExe, Extensions));
        Assert.Equal(ShellMenuStatus.RegisteredElsewhere, ShellMenu.GetStatus(_root, _exe, [.. Extensions, ".bmp"]));

        // A missing command needs a new registration.
        _root.DeleteSubKeyTree(ShellMenu.MenuPath(".png") + @"\shell\06Batch");
        Assert.Equal(ShellMenuStatus.RegisteredElsewhere, ShellMenu.GetStatus(_root, _exe, Extensions));

        ShellMenu.Unregister(_root);
        Assert.Equal(ShellMenuStatus.NotRegistered, ShellMenu.GetStatus(_root, _exe, Extensions));
        Assert.Empty(_root.GetSubKeyNames());
        ShellMenu.Unregister(_root);
        Assert.Empty(_root.GetSubKeyNames());
    }

    [Fact]
    public void OP_E22_Switches_parse_ignoring_case_and_open_has_none()
    {
        Assert.Equal(
            new[] { ShellMenuCommand.Open, ShellMenuCommand.Slideshow, ShellMenuCommand.Rotate, ShellMenuCommand.Resize, ShellMenuCommand.Convert, ShellMenuCommand.Batch },
            ShellMenu.Commands.ToArray());
        Assert.Null(ShellMenu.Argument(ShellMenuCommand.Open));
        foreach (var (arg, command) in new[]
                 {
                     ("--slideshow", ShellMenuCommand.Slideshow), ("--ROTATE", ShellMenuCommand.Rotate), ("--Resize", ShellMenuCommand.Resize),
                     ("--convert", ShellMenuCommand.Convert), ("--batch", ShellMenuCommand.Batch),
                 })
        {
            Assert.True(ShellMenu.TryParseArgument(arg, out var parsed));
            Assert.Equal(command, parsed);
        }
        Assert.False(ShellMenu.TryParseArgument("--open", out _));
        Assert.False(ShellMenu.TryParseArgument("--register-shell-menu", out _));
        Assert.False(ShellMenu.TryParseArgument("rotate", out _));
        Assert.False(ShellMenu.TryParseArgument("", out _));
    }

    [Fact]
    public void OP_E22_Unregister_keeps_other_programs_and_file_associations()
    {
        string other = @"Software\Classes\SystemFileAssociations\.jpg\shell\Other\command";
        using (var key = _root.CreateSubKey(other, writable: true))
            key.SetValue("", "\"C:\\Other\\other.exe\" \"%1\"");

        FileAssociations.Register(_root, _exe, Extensions, TypeName, Description);
        ShellMenu.Register(_root, _exe, Extensions, Label);
        ShellMenu.Unregister(_root);

        Assert.Equal("\"C:\\Other\\other.exe\" \"%1\"", ReadValue(other, ""));
        Assert.Null(_root.OpenSubKey(ShellMenu.AssociationPath(".png")));
        foreach (string ext in Extensions)
            Assert.Equal(FileAssociationStatus.Registered, FileAssociations.Status(_root, _exe, ext));

        ShellMenu.Register(_root, _exe, Extensions, Label);
        FileAssociations.Unregister(_root);
        Assert.Equal(ShellMenuStatus.Registered, ShellMenu.GetStatus(_root, _exe, Extensions));
    }

    private static string Label(ShellMenuCommand command) => "Item " + command;

    private object? ReadValue(string path, string name)
    {
        using var key = _root.OpenSubKey(path);
        return key?.GetValue(name);
    }
}
