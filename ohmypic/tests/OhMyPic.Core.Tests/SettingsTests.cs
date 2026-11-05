namespace OhMyPic.Core.Tests;

public sealed class SettingsTests : TestBase
{
    [Fact]
    public void OP_E19_Defaults()
    {
        var s = new AppSettings();
        Assert.False(s.DeleteWithoutAsking);
        Assert.False(s.ShowHiddenFiles);
        Assert.True(s.UseRecycleBin);
        Assert.Equal(WheelAction.NextPrevious, s.WheelAction);
        Assert.Equal(RotateAutoSaveMode.Ask, s.RotateAutoSave);
        Assert.False(s.ApplyLastSaveQuality);
        Assert.True(s.LoopAtEnds);
        Assert.False(s.AlwaysOnTop);
        Assert.True(s.AutoRotateByExif);
        Assert.True(s.SmoothScaling);
        Assert.False(s.ShowFileNameInFullScreen);
        Assert.True(s.ShowNavigator);
        Assert.Equal(FitMode.ShrinkLargeOnly, s.FitMode);
        Assert.Equal("System", s.BackgroundColor);
        Assert.Equal(3, s.SlideIntervalSeconds);
        Assert.True(s.SlideFade);
        Assert.Equal(150, s.ThumbnailSize);
        Assert.False(s.ShowResolutionOnThumbnail);
        Assert.Equal("system", s.Language);
        Assert.Equal(ViewFormat.Thumbnails, s.ViewFormat);
        Assert.Equal(SortKey.Name, s.SortKey);
        Assert.False(s.SortDescending);
        Assert.Null(s.LastFolder);
        Assert.Equal(85, s.JpegQuality);
        Assert.Null(s.LoadError);
        Assert.EndsWith(Path.Combine("ohMypic", "settings.json"), AppSettings.DefaultPath);
    }

    [Fact]
    public void OP_E19_RoundTrip_All_Fields()
    {
        string path = Temp.Combine("cfg", "settings.json");
        var s = new AppSettings
        {
            DeleteWithoutAsking = true,
            ShowHiddenFiles = true,
            UseRecycleBin = false,
            WheelAction = WheelAction.Zoom,
            RotateAutoSave = RotateAutoSaveMode.Overwrite,
            ApplyLastSaveQuality = true,
            LoopAtEnds = false,
            AlwaysOnTop = true,
            AutoRotateByExif = false,
            SmoothScaling = false,
            ShowFileNameInFullScreen = true,
            ShowNavigator = false,
            FitMode = FitMode.FitHeight,
            BackgroundColor = "#202124",
            SlideIntervalSeconds = 12,
            SlideFade = false,
            ThumbnailSize = 220,
            ShowResolutionOnThumbnail = true,
            Language = "ko",
            ViewFormat = ViewFormat.Filmstrip,
            SortKey = SortKey.Date,
            SortDescending = true,
            LastFolder = @"D:\Pictures\Trip",
            JpegQuality = 70,
        };
        s.Save(path);

        var loaded = AppSettings.Load(path);
        Assert.Null(loaded.LoadError);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(s), System.Text.Json.JsonSerializer.Serialize(loaded));
        Assert.Equal(FitMode.FitHeight, loaded.FitMode);
        Assert.Equal(@"D:\Pictures\Trip", loaded.LastFolder);
        // Enums are stored by name.
        Assert.Contains("\"Filmstrip\"", File.ReadAllText(path));
        Assert.Empty(Util.TempFilesUnder(Temp.Path));
    }

    [Fact]
    public void OP_E19_Corrupt_Or_Missing_File_Gives_Defaults_And_Error()
    {
        string corrupt = Temp.Combine("corrupt.json");
        File.WriteAllText(corrupt, "{ \"ThumbnailSize\": 200, ");
        var fromCorrupt = AppSettings.Load(corrupt);
        Assert.NotNull(fromCorrupt.LoadError);
        Assert.Equal(150, fromCorrupt.ThumbnailSize);

        string wrongType = Temp.Combine("wrong.json");
        File.WriteAllText(wrongType, "{ \"ThumbnailSize\": \"big\" }");
        Assert.NotNull(AppSettings.Load(wrongType).LoadError);

        string nullJson = Temp.Combine("null.json");
        File.WriteAllText(nullJson, "null");
        Assert.NotNull(AppSettings.Load(nullJson).LoadError);

        var missing = AppSettings.Load(Temp.Combine("nope", "settings.json"));
        Assert.NotNull(missing.LoadError);
        Assert.True(missing.UseRecycleBin);
    }

    [Fact]
    public void OP_E19_Out_Of_Range_Values_Normalised()
    {
        string path = Temp.Combine("range.json");
        File.WriteAllText(path, "{ \"ThumbnailSize\": 9999, \"SlideIntervalSeconds\": 0, \"JpegQuality\": 300, \"Language\": \"fr\", \"BackgroundColor\": \"red\", \"FitMode\": 42 }");
        var s = AppSettings.Load(path);
        Assert.Null(s.LoadError);
        Assert.Equal(320, s.ThumbnailSize);
        Assert.Equal(1, s.SlideIntervalSeconds);
        Assert.Equal(100, s.JpegQuality);
        Assert.Equal("system", s.Language);
        Assert.Equal("System", s.BackgroundColor);
        Assert.Equal(FitMode.ShrinkLargeOnly, s.FitMode);

        s.ThumbnailSize = 10;
        s.Save(path);
        Assert.Equal(64, AppSettings.Load(path).ThumbnailSize);
    }

    [Fact]
    public void OP_E19_Save_Is_Atomic_Replace()
    {
        string path = Temp.Combine("settings.json");
        new AppSettings { JpegQuality = 50 }.Save(path);
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            // While another process holds the file without delete sharing, the replace fails and the old content stays whole.
            Assert.ThrowsAny<Exception>(() => new AppSettings { JpegQuality = 60 }.Save(path));
        }
        Assert.Equal(50, AppSettings.Load(path).JpegQuality);
        Assert.Empty(Util.TempFilesUnder(Temp.Path));

        new AppSettings { JpegQuality = 60 }.Save(path);
        Assert.Equal(60, AppSettings.Load(path).JpegQuality);
        Assert.Equal(["settings.json"], Util.FileNames(Temp.Path));
    }

    [Fact]
    public void OP_E19_Data_Dir_Variable_Moves_Settings()
    {
        string? old = Environment.GetEnvironmentVariable(AppSettings.DataDirVariable);
        try
        {
            Environment.SetEnvironmentVariable(AppSettings.DataDirVariable, Temp.Path);
            Assert.Equal(Path.Combine(Path.GetFullPath(Temp.Path), "settings.json"), AppSettings.DefaultPath);
            Environment.SetEnvironmentVariable(AppSettings.DataDirVariable, null);
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            Assert.Equal(Path.Combine(roaming, "ohMypic", "settings.json"), AppSettings.DefaultPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppSettings.DataDirVariable, old);
        }
    }
}
