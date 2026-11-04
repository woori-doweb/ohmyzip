using System.IO;
using System.Windows;
using Microsoft.Win32;
using OhMyZip.App.Settings;
using OhMyZip.App.Text;

namespace OhMyZip.App.Dialogs;

/// <summary>풀기 대화상자(OZ-F025): 풀 위치·대상 범위·충돌 정책·풀고 나서 폴더 열기.</summary>
public partial class ExtractDialog : Window
{
    private static readonly (ConflictChoice Value, string Label)[] Conflicts =
    [
        (ConflictChoice.Ask, DialogText.Conflict_Ask),
        (ConflictChoice.Overwrite, DialogText.Conflict_Overwrite),
        (ConflictChoice.Skip, DialogText.Conflict_Skip),
        (ConflictChoice.Rename, DialogText.Conflict_Rename),
    ];

    private ExtractDialog(string archivePath, int selectedCount, AppSettings settings)
    {
        InitializeComponent();
        HeadingText.Text = Path.GetFileName(archivePath);
        DestinationBox.Text = DefaultDestination(archivePath, settings);
        ScopeSelected.Content = selectedCount > 0 ? string.Format(DialogText.Extract_ScopeSelectedCount, selectedCount) : DialogText.Extract_ScopeSelected;
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

    /// <summary>설정의 기본 해제 위치: 압축 파일 폴더 / 압축 파일 이름 폴더 / 마지막 위치(없으면 압축 파일 폴더).</summary>
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
        return name.Length > 0 ? name : DialogText.Extract_DefaultFolderName;
    }

    private void OnDestinationChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var text = DestinationBox.Text.Trim();
        bool exists;
        try { exists = text.Length > 0 && Directory.Exists(text); }
        catch (Exception) { exists = false; }
        DestinationHint.Text = text.Length == 0 || exists ? "" : DialogText.Extract_FolderMissingHint;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var current = DestinationBox.Text.Trim();
        var picker = new OpenFolderDialog { Title = DialogText.Extract_Browse_Title };
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
        var name = InputDialog.Ask(this, DialogText.Extract_NewFolder_Title, DialogText.Extract_NewFolderPrompt, DialogText.Extract_NewFolderDefault, value =>
        {
            if (value.Length == 0) return DialogText.Name_Required;
            if (value is "." or "..") return DialogText.Name_Dots;
            if (value.IndexOfAny(invalid) >= 0) return DialogText.Name_InvalidChars;
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
        if (text.Length == 0) error = DialogText.Extract_DestinationRequired;
        else
        {
            try
            {
                full = Path.GetFullPath(text);
                if (!Path.IsPathFullyQualified(text)) error = DialogText.Extract_DestinationNotFull;
                else if (File.Exists(full)) error = DialogText.Extract_DestinationIsFile;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                error = DialogText.Extract_DestinationInvalid;
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
