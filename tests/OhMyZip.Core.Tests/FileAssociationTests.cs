using System.Runtime.Versioning;
using Microsoft.Win32;

namespace OhMyZip.Core.Tests;

/// <summary>
/// 파일 연결 등록 시험. 실제 HKCU\Software\Classes 를 건드리지 않도록 HKCU\Software\OhMyZipTests\{guid} 를 root 로 쓰고
/// 끝나면 그 트리를 지운다. NotifyShell 은 부르지 않는다.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FileAssociationTests : IDisposable
{
    private const string TestsParentPath = @"Software\OhMyZipTests";
    private const string ExePath = @"C:\Program Files\ohmyZip\ohmyZip.exe";
    private const string MovedExePath = @"D:\Tools\ohmyZip\ohmyZip.exe";
    private const string OtherProgId = "OtherApp.zip";

    private readonly string _rootPath = TestsParentPath + @"\" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _root;

    public FileAssociationTests() => _root = Registry.CurrentUser.CreateSubKey(_rootPath, writable: true);

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
    }

    [Fact]
    public void OZ_E69_Register_writes_progid_applications_capabilities_and_open_with_values()
    {
        FileAssociations.Register(_root, ExePath);
        string command = "\"" + ExePath + "\" \"%1\"";
        string icon = "\"" + ExePath + "\",0";

        using (var progId = _root.OpenSubKey(@"Software\Classes\ohmyZip.Archive"))
        {
            Assert.NotNull(progId);
            Assert.Equal("ohmyZip 압축파일", progId.GetValue(""));
            Assert.Equal("ohmyZip 압축파일", progId.GetValue("FriendlyTypeName"));
        }
        Assert.Equal(icon, ReadDefault(@"Software\Classes\ohmyZip.Archive\DefaultIcon"));
        Assert.Equal(command, ReadDefault(@"Software\Classes\ohmyZip.Archive\shell\open\command"));

        foreach (string ext in FileAssociations.Extensions)
        {
            using var openWith = _root.OpenSubKey($@"Software\Classes\{ext}\OpenWithProgids");
            Assert.NotNull(openWith);
            Assert.Contains("ohmyZip.Archive", openWith.GetValueNames());
            // 확장자 키의 기본값(현재 연결)은 쓰지 않는다.
            using var extKey = _root.OpenSubKey($@"Software\Classes\{ext}");
            Assert.Null(extKey!.GetValue(""));
        }

        using (var app = _root.OpenSubKey(@"Software\Classes\Applications\ohmyZip.exe"))
        {
            Assert.NotNull(app);
            Assert.Equal("ohmyZip", app.GetValue("FriendlyAppName"));
            using var types = app.OpenSubKey("SupportedTypes");
            Assert.NotNull(types);
            foreach (string ext in FileAssociations.Extensions)
                Assert.Equal("", types.GetValue(ext));
        }
        Assert.Equal(command, ReadDefault(@"Software\Classes\Applications\ohmyZip.exe\shell\open\command"));

        using (var caps = _root.OpenSubKey(@"Software\ohmyZip\Capabilities"))
        {
            Assert.NotNull(caps);
            Assert.Equal("ohmyZip", caps.GetValue("ApplicationName"));
            Assert.False(string.IsNullOrWhiteSpace(caps.GetValue("ApplicationDescription") as string));
            using var assoc = caps.OpenSubKey("FileAssociations");
            Assert.NotNull(assoc);
            Assert.Equal(FileAssociations.Extensions.Count, assoc.ValueCount);
            foreach (string ext in FileAssociations.Extensions)
                Assert.Equal("ohmyZip.Archive", assoc.GetValue(ext));
        }

        using (var registered = _root.OpenSubKey(@"Software\RegisteredApplications"))
        {
            Assert.NotNull(registered);
            Assert.Equal(@"Software\ohmyZip\Capabilities", registered.GetValue("ohmyZip"));
        }
    }

    [Fact]
    public void OZ_E69_Register_and_unregister_preserve_existing_extension_default_and_other_values()
    {
        using (var zip = _root.CreateSubKey(@"Software\Classes\.zip", writable: true))
        {
            zip.SetValue("", OtherProgId);
            using var openWith = zip.CreateSubKey("OpenWithProgids", writable: true);
            openWith.SetValue(OtherProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }
        using (var registered = _root.CreateSubKey(@"Software\RegisteredApplications", writable: true))
            registered.SetValue("OtherApp", @"Software\OtherApp\Capabilities");

        FileAssociations.Register(_root, ExePath);

        Assert.Equal(OtherProgId, ReadDefault(@"Software\Classes\.zip"));
        using (var openWith = _root.OpenSubKey(@"Software\Classes\.zip\OpenWithProgids"))
        {
            Assert.NotNull(openWith);
            Assert.Contains(OtherProgId, openWith.GetValueNames());
            Assert.Contains("ohmyZip.Archive", openWith.GetValueNames());
        }

        FileAssociations.Unregister(_root);

        Assert.Equal(OtherProgId, ReadDefault(@"Software\Classes\.zip"));
        using (var openWith = _root.OpenSubKey(@"Software\Classes\.zip\OpenWithProgids"))
        {
            Assert.NotNull(openWith);
            Assert.Equal(new[] { OtherProgId }, openWith.GetValueNames());
        }
        using (var registered = _root.OpenSubKey(@"Software\RegisteredApplications"))
        {
            Assert.NotNull(registered);
            Assert.Equal(@"Software\OtherApp\Capabilities", registered.GetValue("OtherApp"));
            Assert.DoesNotContain("ohmyZip", registered.GetValueNames());
        }
    }

    [Fact]
    public void OZ_E69_GetStatus_reports_not_registered_registered_and_registered_elsewhere()
    {
        Assert.Equal(FileAssociationStatus.NotRegistered, FileAssociations.GetStatus(_root, ExePath));

        FileAssociations.Register(_root, ExePath);
        Assert.Equal(FileAssociationStatus.Registered, FileAssociations.GetStatus(_root, ExePath));
        Assert.Equal(FileAssociationStatus.Registered, FileAssociations.GetStatus(_root, ExePath.ToUpperInvariant()));
        Assert.Equal(FileAssociationStatus.RegisteredElsewhere, FileAssociations.GetStatus(_root, MovedExePath));

        // 옮긴 위치에서 다시 등록하면 그 위치로 바뀐다.
        FileAssociations.Register(_root, MovedExePath);
        Assert.Equal(FileAssociationStatus.Registered, FileAssociations.GetStatus(_root, MovedExePath));
        Assert.Equal(FileAssociationStatus.RegisteredElsewhere, FileAssociations.GetStatus(_root, ExePath));
        Assert.Equal("\"" + MovedExePath + "\" \"%1\"", ReadDefault(@"Software\Classes\Applications\ohmyZip.exe\shell\open\command"));

        FileAssociations.Unregister(_root);
        Assert.Equal(FileAssociationStatus.NotRegistered, FileAssociations.GetStatus(_root, MovedExePath));
    }

    [Fact]
    public void OZ_E69_Unregister_removes_every_trace_and_empty_extension_keys_and_is_safe_to_repeat()
    {
        // 내용이 있는 확장자 키(.7z 기본값)는 남고, 원래 없던 확장자 키는 빈 채로 남지 않는다.
        using (var sevenZip = _root.CreateSubKey(@"Software\Classes\.7z", writable: true))
            sevenZip.SetValue("", "OtherApp.7z");

        // 등록한 적이 없어도 조용히 넘어간다.
        FileAssociations.Unregister(_root);

        FileAssociations.Register(_root, ExePath);
        FileAssociations.Unregister(_root);
        FileAssociations.Unregister(_root);

        Assert.Null(_root.OpenSubKey(@"Software\Classes\ohmyZip.Archive"));
        Assert.Null(_root.OpenSubKey(@"Software\Classes\Applications\ohmyZip.exe"));
        Assert.Null(_root.OpenSubKey(@"Software\ohmyZip"));
        using (var registered = _root.OpenSubKey(@"Software\RegisteredApplications"))
        {
            if (registered is not null)
                Assert.DoesNotContain("ohmyZip", registered.GetValueNames());
        }
        foreach (string ext in FileAssociations.Extensions)
        {
            using var extKey = _root.OpenSubKey($@"Software\Classes\{ext}");
            if (ext == ".7z")
            {
                Assert.NotNull(extKey);
                Assert.Equal("OtherApp.7z", extKey.GetValue(""));
                Assert.Null(extKey.OpenSubKey("OpenWithProgids"));
            }
            else
            {
                Assert.Null(extKey);
            }
        }
        Assert.Equal(FileAssociationStatus.NotRegistered, FileAssociations.GetStatus(_root, ExePath));
    }

    [Fact]
    public void OZ_E69_Register_rejects_relative_or_quoted_exe_path_without_writing()
    {
        Assert.Throws<ArgumentException>(() => FileAssociations.Register(_root, "ohmyZip.exe"));
        Assert.Throws<ArgumentException>(() => FileAssociations.Register(_root, "C:\\a\"b\\ohmyZip.exe"));
        Assert.Null(_root.OpenSubKey("Software"));
    }

    private string? ReadDefault(string path)
    {
        using var key = _root.OpenSubKey(path);
        return key?.GetValue("") as string;
    }
}
