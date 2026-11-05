using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OhMyPic.App.Text;
using OhMyPic.Core;

namespace OhMyPic.App.Dialogs.Edit;

/// <summary>
/// Shared "where and how to save" block of every edit dialog: output folder, text added to the file name,
/// duplicate handling and deleting the original. Produces <see cref="OutputOptions"/>.
/// </summary>
public partial class OutputOptionsControl : UserControl
{
    private static readonly (ConflictPolicy Value, string Label)[] Conflicts =
    [
        (ConflictPolicy.Ask, EditText.Output_ConflictAsk),
        (ConflictPolicy.Skip, EditText.Output_ConflictSkip),
        (ConflictPolicy.Overwrite, EditText.Output_ConflictOverwrite),
    ];

    private bool _ready;

    public OutputOptionsControl()
    {
        InitializeComponent();
        ConflictBox.ItemsSource = Conflicts.Select(c => c.Label).ToList();
        ConflictBox.SelectedIndex = 0;
        SubFolderBox.Text = EditText.Output_DefaultSubFolder;
        SameFolderRadio.IsChecked = true;
        _ready = true;
        Refresh();
    }

    /// <summary>Raised whenever any field changes (after validation ran).</summary>
    public event EventHandler? Changed;

    /// <summary>False while a field holds a value that cannot be used; the dialog disables its confirm button.</summary>
    public bool IsValid { get; private set; } = true;

    /// <summary>Text placed before the original name, or empty when "Add text" is off.</summary>
    public string Prefix => AddTextBox.IsChecked == true ? PrefixBox.Text : "";

    /// <summary>Text placed after the original name, or empty when "Add text" is off.</summary>
    public string Suffix => AddTextBox.IsChecked == true ? SuffixBox.Text : "";

    /// <summary>Fills the fields. <paramref name="defaultSuffix"/> is used when the options carry no suffix.</summary>
    public void Load(OutputOptions options, string defaultSuffix = "")
    {
        _ready = false;
        switch (options.Folder)
        {
            case OutputFolderMode.SubFolder: SubFolderRadio.IsChecked = true; break;
            case OutputFolderMode.Specified: SpecifiedRadio.IsChecked = true; break;
            default: SameFolderRadio.IsChecked = true; break;
        }
        SubFolderBox.Text = string.IsNullOrWhiteSpace(options.SubFolderName) ? EditText.Output_DefaultSubFolder : options.SubFolderName;
        FolderBox.Text = options.FolderPath ?? "";
        bool hasText = !string.IsNullOrEmpty(options.Prefix) || !string.IsNullOrEmpty(options.Suffix);
        AddTextBox.IsChecked = hasText;
        PrefixBox.Text = options.Prefix ?? "";
        SuffixBox.Text = hasText ? options.Suffix ?? "" : defaultSuffix;
        ConflictBox.SelectedIndex = Math.Max(0, Array.FindIndex(Conflicts, c => c.Value == options.Conflict));
        DeleteOriginalBox.IsChecked = options.DeleteOriginal;
        _ready = true;
        Refresh();
    }

    /// <summary>Current values as engine options. Only meaningful while <see cref="IsValid"/> is true.</summary>
    public OutputOptions BuildOptions()
    {
        var folder = SubFolderRadio.IsChecked == true ? OutputFolderMode.SubFolder
            : SpecifiedRadio.IsChecked == true ? OutputFolderMode.Specified
            : OutputFolderMode.SameFolder;
        string? folderPath = null;
        if (folder == OutputFolderMode.Specified)
        {
            try { folderPath = Path.GetFullPath(FolderBox.Text.Trim()); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { folderPath = FolderBox.Text.Trim(); }
        }
        string subFolder = SubFolderBox.Text.Trim();
        return new OutputOptions(
            Folder: folder,
            FolderPath: folderPath,
            SubFolderName: subFolder.Length > 0 ? subFolder : EditText.Output_DefaultSubFolder,
            Prefix: Prefix.Length > 0 ? Prefix : null,
            Suffix: Suffix.Length > 0 ? Suffix : null,
            Conflict: Conflicts[Math.Max(0, ConflictBox.SelectedIndex)].Value,
            DeleteOriginal: DeleteOriginalBox.IsChecked == true);
    }

    private void OnInputChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        Refresh();
    }

    private void Refresh()
    {
        bool sub = SubFolderRadio.IsChecked == true;
        bool specified = SpecifiedRadio.IsChecked == true;
        bool addText = AddTextBox.IsChecked == true;
        SubFolderBox.IsEnabled = sub;
        FolderBox.IsEnabled = specified;
        BrowseButton.IsEnabled = specified;
        PrefixBox.IsEnabled = addText;
        SuffixBox.IsEnabled = addText;
        PrefixPlaceholder.Visibility = PrefixBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SuffixPlaceholder.Visibility = SuffixBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        DeleteWarning.Visibility = DeleteOriginalBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        bool ok = true;
        ok &= ShowError(SubFolderError, sub ? ValidateFolderName(SubFolderBox.Text.Trim()) : null);
        ok &= ShowError(FolderError, specified ? ValidateFolderPath(FolderBox.Text.Trim()) : null);
        ok &= ShowError(AddTextError, addText ? ValidateNameText(PrefixBox.Text + SuffixBox.Text) : null);
        IsValid = ok;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shows or hides an inline error line under a field. Returns true when there is no error.</summary>
    internal static bool ShowError(TextBlock target, string? error)
    {
        if (error is null)
        {
            target.Text = "";
            target.Visibility = Visibility.Collapsed;
            return true;
        }
        target.Text = "\u26A0 " + error;
        target.Visibility = Visibility.Visible;
        return false;
    }

    private static string? ValidateFolderName(string name)
    {
        if (name.Length == 0) return EditText.Error_SubFolderRequired;
        if (name is "." or "..") return EditText.Error_NameDots;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return EditText.Error_NameInvalidChars;
        return null;
    }

    private static string? ValidateFolderPath(string path)
    {
        if (path.Length == 0) return EditText.Error_FolderRequired;
        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return EditText.Error_FolderInvalid;
        try
        {
            if (!Path.IsPathFullyQualified(path)) return EditText.Error_FolderNotFull;
            Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return EditText.Error_FolderInvalid;
        }
        return null;
    }

    /// <summary>Text that becomes part of a file name must not contain characters Windows forbids in names.</summary>
    internal static string? ValidateNameText(string text) =>
        text.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? EditText.Error_NameInvalidChars : null;

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = EditText.Output_BrowseTitle };
        try
        {
            // Start in the closest existing parent of the typed folder.
            var probe = FolderBox.Text.Trim();
            while (probe.Length > 0 && !Directory.Exists(probe)) probe = Path.GetDirectoryName(probe) ?? "";
            if (probe.Length > 0) picker.InitialDirectory = probe;
        }
        catch (ArgumentException) { }
        var owner = Window.GetWindow(this);
        bool? picked = owner is null ? picker.ShowDialog() : picker.ShowDialog(owner);
        if (picked == true)
        {
            FolderBox.Text = picker.FolderName;
            FolderBox.Focus();
        }
    }
}
