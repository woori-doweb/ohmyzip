using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using OhMyZip.Core;

namespace OhMyZip.App.Settings;

public enum AppTheme { System, Light, Dark }

public enum ExtractDestination { ArchiveFolder, ArchiveNameFolder, LastUsed }

/// <summary>Ask = 같은 이름이 있으면 충돌 대화상자로 묻는다. 나머지는 엔진 ConflictPolicy 로 바로 간다.</summary>
public enum ConflictChoice { Ask, Overwrite, Skip, Rename }

public enum ListMode { Details, List, Icons, LargeIcons }

public enum ToolbarMode { Large, Small, Classic, Hidden }

/// <summary>
/// 앱 설정 한 벌(JSON). 위치: %APPDATA%\ohmyZip\settings.json, 환경 변수 OHMYZIP_DATA_DIR 가 있으면 그 폴더.
/// 시작할 때 읽고, 환경 설정 확인·보기 메뉴 토글 때 저장한다. 암호는 여기에 넣지 않는다.
/// </summary>
public sealed class AppSettings
{
    // 일반
    public bool ShowStartScreen { get; set; } = true;
    public AppTheme Theme { get; set; } = AppTheme.System;
    public bool ReduceMotion { get; set; }

    // 압축 풀기
    public ExtractDestination ExtractDestination { get; set; } = ExtractDestination.ArchiveNameFolder;
    public string? LastExtractFolder { get; set; }
    public ConflictChoice ExtractConflict { get; set; } = ConflictChoice.Ask;
    public bool OpenFolderAfterExtract { get; set; }

    // 압축하기
    public ArchiveFormat CreateFormat { get; set; } = ArchiveFormat.Zip;
    public int CreateLevel { get; set; } = 5;

    // 보기/편집
    public bool ConfirmDelete { get; set; } = true;
    public ListMode ListMode { get; set; } = ListMode.Details;
    /// <summary>칼럼 키(<see cref="ColumnKeys"/>) → 표시 여부. 없는 키는 기본값.</summary>
    public Dictionary<string, bool> Columns { get; set; } = DefaultColumns();
    public ToolbarMode ToolbarMode { get; set; } = ToolbarMode.Large;
    /// <summary>숨긴 툴바 버튼의 명령 키.</summary>
    public List<string> HiddenToolbarButtons { get; set; } = [];
    public bool ShowSidebar { get; set; } = true;
    public bool ShowStatusBar { get; set; } = true;
    public bool ShowComment { get; set; }
    public bool Topmost { get; set; }

    /// <summary>보기 &gt; 칼럼 설정 10개(메뉴 순서). Id 는 traceability 행, Header 는 메뉴 문구 = 칼럼 머리글.</summary>
    public static readonly IReadOnlyList<(string Key, string Id, string Header, bool Default)> ColumnKeys =
    [
        ("compressed", "OZ-C040", "압축 크기", true),
        ("size", "OZ-C041", "원본 크기", true),
        ("type", "OZ-C042", "파일 종류", true),
        ("modified", "OZ-C043", "수정한 날짜", true),
        ("method", "OZ-C044", "압축 방식", false),
        ("encryption", "OZ-C045", "암호 방식", false),
        ("crc", "OZ-C046", "CRC", true),
        ("attributes", "OZ-C047", "속성", false),
        ("description", "OZ-C048", "파일 설명", false),
        ("extra", "OZ-C049", "부가 정보", false),
    ];

    public static Dictionary<string, bool> DefaultColumns() => ColumnKeys.ToDictionary(c => c.Key, c => c.Default, StringComparer.Ordinal);

    public bool IsColumnVisible(string key) =>
        Columns.TryGetValue(key, out var v) ? v : ColumnKeys.FirstOrDefault(c => c.Key == key).Default;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DataDirectory
    {
        get
        {
            var overrideDir = Environment.GetEnvironmentVariable("OHMYZIP_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(overrideDir)) return Path.GetFullPath(overrideDir);
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ohmyZip");
        }
    }

    public static string FilePath => Path.Combine(DataDirectory, "settings.json");

    /// <summary>현재 설정(앱 전체 하나). 시작 시 <see cref="Load"/> 로 채운다.</summary>
    public static AppSettings Current { get; private set; } = new();

    /// <summary>파일이 없거나 읽을 수 없으면 기본값. 깨진 파일은 덮어쓰지 않고(저장할 때까지) 기본값으로 시작한다.</summary>
    public static string? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) { Current = new AppSettings(); return null; }
            Current = (JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings()).Normalize();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            Current = new AppSettings();
            return $"설정 파일을 읽지 못해 기본값으로 시작합니다: {ex.Message}";
        }
    }

    /// <summary>임시 파일에 쓴 뒤 교체한다. 실패하면 오류 문장, 성공하면 null.</summary>
    public static string? Save(AppSettings settings)
    {
        Current = settings;
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json));
            File.Move(temp, FilePath, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return $"설정을 저장하지 못했습니다({FilePath}): {ex.Message}";
        }
    }

    public static string? SaveCurrent() => Save(Current);

    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, Json), Json)!.Normalize();

    /// <summary>범위를 벗어난 값(손으로 고친 파일 등)을 기본값 쪽으로 되돌린다.</summary>
    private AppSettings Normalize()
    {
        CreateLevel = Math.Clamp(CreateLevel, 0, 9);
        Columns ??= DefaultColumns();
        HiddenToolbarButtons ??= [];
        if (!Enum.IsDefined(Theme)) Theme = AppTheme.System;
        if (!Enum.IsDefined(ExtractDestination)) ExtractDestination = ExtractDestination.ArchiveNameFolder;
        if (!Enum.IsDefined(ExtractConflict)) ExtractConflict = ConflictChoice.Ask;
        if (!Enum.IsDefined(ListMode)) ListMode = ListMode.Details;
        if (!Enum.IsDefined(ToolbarMode)) ToolbarMode = ToolbarMode.Large;
        if (CreateFormat is ArchiveFormat.Unknown or ArchiveFormat.Rar || !Enum.IsDefined(CreateFormat)) CreateFormat = ArchiveFormat.Zip;
        return this;
    }
}
