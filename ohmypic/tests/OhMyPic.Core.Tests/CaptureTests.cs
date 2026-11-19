using System.Globalization;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using OhMyPic.Core.Editing;

namespace OhMyPic.Core.Tests;

/// <summary>
/// Capture agent autostart and screenshot files (OP-E53). The real Run key is never touched:
/// HKCU\Software\ohMypicTests\{guid} is the root and the whole tree is deleted afterwards. The executable is a fake
/// .exe file in a temp folder.
/// </summary>
public sealed class CaptureTests : IDisposable
{
    private const string TestsParentPath = @"Software\ohMypicTests";
    private const string OtherValueName = "Other Program";
    private const string OtherValue = "\"C:\\Other\\other.exe\" --background";

    private static readonly DateTime ShotTime = new(2026, 11, 19, 15, 30, 5);

    private readonly string _rootPath = TestsParentPath + @"\" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _root;
    private readonly TempDir _temp = new();
    private readonly string _exe;
    private readonly string _movedExe;

    public CaptureTests()
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

    private object? RunValue(string name)
    {
        using var run = _root.OpenSubKey(CaptureAgentRegistration.RunKeyPath);
        return run?.GetValue(name);
    }

    // ───────────── Run value ─────────────

    [Fact]
    public void OP_E53_Run_Value_Register_Status_Unregister_Round_Trip_Keeps_Other_Values()
    {
        using (var run = _root.CreateSubKey(CaptureAgentRegistration.RunKeyPath, writable: true))
            run.SetValue(OtherValueName, OtherValue);
        Assert.False(CaptureAgentRegistration.IsRegistered(_root, _exe));

        CaptureAgentRegistration.Register(_root, _exe);

        string command = "\"" + _exe + "\" --capture-agent";
        Assert.Equal(command, CaptureAgentRegistration.Command(_exe));
        Assert.Equal(command, RunValue(CaptureAgentRegistration.ValueName));
        Assert.True(CaptureAgentRegistration.IsRegistered(_root, _exe));
        Assert.True(CaptureAgentRegistration.IsRegistered(_root, _exe.ToUpperInvariant()));
        Assert.False(CaptureAgentRegistration.IsRegistered(_root, _movedExe));
        Assert.Equal(OtherValue, RunValue(OtherValueName));

        // Registering another copy replaces the value instead of adding a second one.
        CaptureAgentRegistration.Register(_root, _movedExe);
        Assert.True(CaptureAgentRegistration.IsRegistered(_root, _movedExe));
        Assert.False(CaptureAgentRegistration.IsRegistered(_root, _exe));
        using (var run = _root.OpenSubKey(CaptureAgentRegistration.RunKeyPath))
            Assert.Equal(2, run!.ValueCount);

        CaptureAgentRegistration.Unregister(_root);
        Assert.Null(RunValue(CaptureAgentRegistration.ValueName));
        Assert.False(CaptureAgentRegistration.IsRegistered(_root, _movedExe));
        Assert.Equal(OtherValue, RunValue(OtherValueName));

        CaptureAgentRegistration.Unregister(_root);
        Assert.False(CaptureAgentRegistration.IsRegistered(_root, _exe));
        Assert.Equal(OtherValue, RunValue(OtherValueName));
    }

    [Fact]
    public void OP_E53_Unregister_Without_Run_Key_Creates_Nothing()
    {
        CaptureAgentRegistration.Unregister(_root);
        CaptureAgentRegistration.Unregister(_root);
        Assert.False(CaptureAgentRegistration.IsRegistered(_root, _exe));
        Assert.Empty(_root.GetSubKeyNames());
    }

    [Fact]
    public void OP_E53_Invalid_Executable_Paths_Are_Rejected_Without_Writing()
    {
        foreach (string bad in new[] { "", "  ", "ohMypic.exe", @"bin\ohMypic.exe", "C:\\a\"b\\ohMypic.exe", _temp.Combine("missing.exe") })
            Assert.ThrowsAny<ArgumentException>(() => CaptureAgentRegistration.Register(_root, bad));
        Assert.ThrowsAny<ArgumentException>(() => CaptureAgentRegistration.Command("ohMypic.exe"));
        Assert.ThrowsAny<ArgumentException>(() => CaptureAgentRegistration.IsRegistered(_root, ""));
        Assert.Empty(_root.GetSubKeyNames());
    }

    // ───────────── screenshot files ─────────────

    [Fact]
    public void OP_E53_Screenshot_Name_Uses_Invariant_Timestamp_And_Counts_Up()
    {
        string folder = _temp.Combine("shots");
        Directory.CreateDirectory(folder);
        string first = Path.Combine(folder, "ohMypic-20261119-153005.png");
        CultureInfo culture = CultureInfo.CurrentCulture;
        try
        {
            // A non-Gregorian default calendar must not leak into the name.
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            Assert.Equal(first, ScreenshotFiles.NextPath(folder, ShotTime));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
        Assert.Equal(first, ScreenshotFiles.NextPath(folder, ShotTime));

        File.WriteAllBytes(first, [1]);
        Assert.Equal(Path.Combine(folder, "ohMypic-20261119-153005-2.png"), ScreenshotFiles.NextPath(folder, ShotTime));
        File.WriteAllBytes(Path.Combine(folder, "ohMypic-20261119-153005-2.png"), [2]);
        Assert.Equal(Path.Combine(folder, "ohMypic-20261119-153005-3.png"), ScreenshotFiles.NextPath(folder, ShotTime));
        Assert.Equal(Path.Combine(folder, "ohMypic-20261119-153006.png"), ScreenshotFiles.NextPath(folder, ShotTime.AddSeconds(1)));
    }

    [Fact]
    public void OP_E53_SavePng_Writes_Decodable_Identical_Pixels_And_No_Temp_Files()
    {
        string folder = _temp.Combine("new", "Screenshots");
        RasterImage image = RasterImage.FromBitmap(Img.Palette(37, 23));
        byte[] before = (byte[])image.Pixels.Clone();

        string path = ScreenshotFiles.SavePng(image, folder, ShotTime);

        Assert.Equal(Path.Combine(folder, "ohMypic-20261119-153005.png"), path);
        Assert.True(Path.IsPathFullyQualified(path));
        BitmapSource decoded = Img.Decode(path);
        Assert.Equal((image.Width, image.Height), (decoded.PixelWidth, decoded.PixelHeight));
        Assert.True(image.Pixels.AsSpan().SequenceEqual(Img.Bgra(decoded)), "decoded pixels differ");
        Assert.True(before.AsSpan().SequenceEqual(image.Pixels), "input image was modified");

        // Same second again: a second file, the first one untouched.
        byte[] firstBytes = File.ReadAllBytes(path);
        string second = ScreenshotFiles.SavePng(image, folder, ShotTime);
        Assert.Equal(Path.Combine(folder, "ohMypic-20261119-153005-2.png"), second);
        Assert.Equal(firstBytes, File.ReadAllBytes(path));
        Assert.Equal(new[] { "ohMypic-20261119-153005-2.png", "ohMypic-20261119-153005.png" }, Util.FileNames(folder));
        Assert.Empty(Util.TempFilesUnder(_temp.Path));
    }

    [Fact]
    public void OP_E53_DefaultFolder_Honors_Environment_Variable()
    {
        string? old = Environment.GetEnvironmentVariable(ScreenshotFiles.FolderVariable);
        string pictures = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
        try
        {
            string custom = _temp.Combine("custom shots");
            Environment.SetEnvironmentVariable(ScreenshotFiles.FolderVariable, custom);
            Assert.Equal(custom, ScreenshotFiles.DefaultFolder);
            Environment.SetEnvironmentVariable(ScreenshotFiles.FolderVariable, "");
            Assert.Equal(pictures, ScreenshotFiles.DefaultFolder);
            Environment.SetEnvironmentVariable(ScreenshotFiles.FolderVariable, null);
            Assert.Equal(pictures, ScreenshotFiles.DefaultFolder);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ScreenshotFiles.FolderVariable, old);
        }
    }
}
