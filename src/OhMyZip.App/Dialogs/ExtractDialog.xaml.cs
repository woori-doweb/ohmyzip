using System.IO;
using System.Windows;
using Microsoft.Win32;
using OhMyZip.App.Settings;

namespace OhMyZip.App.Dialogs;

/// <summary>풀기 대화상자(OZ-F025): 풀 위치·대상 범위·충돌 정책·풀고 나서 폴더 열기.</summary>
public partial class ExtractDialog : Window
{
    private static readonly (ConflictChoice Value, string Label)[] Conflicts =
    [
        (ConflictChoice.Ask, "묻기"),
        (ConflictChoice.Overwrite, "덮어쓰기"),
        (ConflictChoice.Skip, "건너뛰기"),
        (ConflictChoice.Rename, "이름 바꾸기"),
    ];

    private ExtractDialog(string archivePath, int selectedCount, AppSettings settings)
    {
        InitializeComponent();
        HeadingText.Text = Path.GetFileName(archivePath);
        DestinationBox.Text = DefaultDestination(archivePath, settings);
        ScopeSelected.Content = selectedCount > 0 ? $"선택한 항목 ({selectedCount:N0}개)" : "선택한 항목";
        ScopeSelected.IsEnabled = selectedCount > 0;
        // 선택이 없으면 전체, 선택이 있으면 선택한 항목이 기본.
        if (selectedCount > 0) ScopeSelected.IsChecked = true;
        else ScopeAll.IsChecked = true;
        ConflictBox.ItemsSource = Conflicts.Select(c => c.Label).ToList();
        ConflictBox.SelectedIndex = Array.FindIndex(Conflicts, c => c.Value == settings.ExtractConflict);
        OpenAfterBox.IsChecked = settings.OpenFolderAfterExtract;
        Loaded += (_, _) => { DestinationBox.Focus(); DestinationBox.SelectAll(); };
        SourceInitialized += (_, _) => NativeMethods.ApplyDarkTitleBar(this);
    }

    public string Destination { get; private set; } = "";
    public bool SelectedOnly => ScopeSelected.IsChecked == true;
    public ConflictChoice Conflict => Conflicts[Math.Max(0, ConflictBox.SelectedIndex)].Value;
    public bool OpenAfter => OpenAfterBox.IsChecked == true;

    /// <summary>설정의 기본 해제 위치: 압축파일 폴더 / 압축파일 이름 폴더 / 마지막 위치(없으면 압축파일 폴더).</summary>
    public static string DefaultDestination(string archivePath, AppSettings settings)
    {
        var dir = Path.GetDirectoryName(archivePath) ?? "";
        return settings.ExtractDestination switch
        {
            ExtractDestination.ArchiveNameFolder => Path.Combine(dir, ArchiveBaseName(archivePath)),
            ExtractDestination.LastUsed when !string.IsNullOrWhiteSpace(settings.LastExtractFolder) => settings.LastExtractFolder!,
            _ => dir,
        };
    }

    /// <summary>"a.zip" → "a", "a.tar.gz" → "a", "a.7z.001" → "a".</summary>
    public static string ArchiveBaseName(string archivePath)
    {
        var name = Path.GetFileName(archivePath);
        var ext = Path.GetExtension(name);
        if (ext.Length > 1 && ext[1..].All(char.IsAsciiDigit)) name = Path.GetFileNameWithoutExtension(name);
        name = Path.GetFileNameWithoutExtension(name);
        if (name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.Length > 0 ? name : "압축 풀기";
    }

    private void OnDestinationChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var text = DestinationBox.Text.Trim();
        bool exists;
        try { exists = text.Length > 0 && Directory.Exists(text); }
        catch (Exception) { exists = false; }
        DestinationHint.Text = text.Length == 0 || exists ? "" : "이 폴더는 아직 없습니다 — 풀 때 새로 만듭니다.";
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var current = DestinationBox.Text.Trim();
        var picker = new OpenFolderDialog { Title = "압축을 풀 폴더 선택" };
        try
        {
            // 없는 폴더면 가장 가까운 기존 상위 폴더에서 연다.
            var probe = current;
            while (probe.Length > 0 && !Directory.Exists(probe)) probe = Path.GetDirectoryName(probe) ?? "";
            if (probe.Length > 0) picker.InitialDirectory = probe;
        }
        catch (ArgumentException) { }
        if (picker.ShowDialog(this) == true) DestinationBox.Text = picker.FolderName;
    }

    private void OnNewFolder(object sender, RoutedEventArgs e)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = InputDialog.Ask(this, "새 폴더 만들기", "풀 위치 안에 만들 새 폴더 이름:", "새 폴더", value =>
        {
            if (value.Length == 0) return "이름을 입력하세요.";
            if (value is "." or "..") return "이 이름은 쓸 수 없습니다.";
            if (value.IndexOfAny(invalid) >= 0) return "이름에 \\ / : * ? \" < > | 를 쓸 수 없습니다.";
            return null;
        });
        if (name is null) return;
        var baseDir = DestinationBox.Text.Trim();
        DestinationBox.Text = baseDir.Length == 0 ? name : Path.Combine(baseDir, name);
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var text = DestinationBox.Text.Trim();
        string? error = null;
        string full = "";
        if (text.Length == 0) error = "풀 위치를 입력하세요.";
        else
        {
            try
            {
                full = Path.GetFullPath(text);
                if (!Path.IsPathFullyQualified(text)) error = "풀 위치는 드라이브부터 시작하는 전체 경로로 입력하세요.";
                else if (File.Exists(full)) error = "같은 이름의 파일이 있어 폴더로 쓸 수 없습니다.";
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                error = "풀 위치 경로가 올바르지 않습니다.";
            }
        }
        if (error is not null)
        {
            ErrorText.Text = "\u26A0 " + error;
            ErrorText.Visibility = Visibility.Visible;
            DestinationBox.Focus();
            return;
        }
        Destination = full;
        DialogResult = true;
    }

    public static ExtractDialog? Ask(Window owner, string archivePath, int selectedCount, AppSettings settings)
    {
        var dlg = new ExtractDialog(archivePath, selectedCount, settings) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg : null;
    }
}
