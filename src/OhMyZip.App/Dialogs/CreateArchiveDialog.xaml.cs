using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OhMyZip.App.Settings;
using OhMyZip.App.Text;
using OhMyZip.Core;

namespace OhMyZip.App.Dialogs;

/// <summary>
/// 새 압축 대화상자(OZ-F024). 형식별 옵션은 <see cref="Archives.CapabilitiesOf"/> 의 Create/Encrypt/Split 로만 켜고 끈다.
/// 암호는 <see cref="Options"/> 안에만 두고 화면 밖으로 내보내지 않는다.
/// </summary>
public partial class CreateArchiveDialog : Window
{
    public static readonly IReadOnlyList<(ArchiveFormat Format, string Label)> Formats =
    [
        (ArchiveFormat.Zip, "ZIP"),
        (ArchiveFormat.SevenZip, "7Z"),
        (ArchiveFormat.Tar, "TAR"),
        (ArchiveFormat.TarGz, "TAR.GZ"),
        (ArchiveFormat.GZip, "GZ"),
    ];

    public static readonly IReadOnlyList<string> LevelLabels =
    [
        DialogText.Create_Level0, DialogText.Create_Level1, "2", DialogText.Create_Level3, "4", DialogText.Create_Level5, "6", DialogText.Create_Level7, "8", DialogText.Create_Level9,
    ];

    private const long Mb = 1024L * 1024;

    /// <summary>분할 크기 선택지. null = 분할 안 함, -1 = 직접 입력.</summary>
    private static readonly (string Label, long? Bytes)[] SplitPresets =
    [
        (DialogText.Create_SplitNone, null),
        ("10 MB", 10 * Mb),
        ("100 MB", 100 * Mb),
        ("700 MB (CD)", 700 * Mb),
        (DialogText.Create_SplitFat32, 4000 * Mb),
        (DialogText.Create_SplitCustom, -1),
    ];

    private readonly List<string> _sources = [];
    private ArchiveFormat _format;

    private CreateArchiveDialog(IEnumerable<string> sources, AppSettings settings)
    {
        InitializeComponent();
        foreach (var s in sources) AddSource(s);
        FormatBox.ItemsSource = Formats.Select(f => f.Label).ToList();
        LevelBox.ItemsSource = LevelLabels;
        LevelBox.SelectedIndex = Math.Clamp(settings.CreateLevel, 0, 9);
        SplitBox.ItemsSource = SplitPresets.Select(p => p.Label).ToList();
        SplitBox.SelectedIndex = 0;

        _format = Formats.Any(f => f.Format == settings.CreateFormat) ? settings.CreateFormat : ArchiveFormat.Zip;
        TargetBox.Text = SuggestTarget(_format);
        FormatBox.SelectedIndex = IndexOf(_format);
        Loaded += (_, _) => { TargetBox.Focus(); TargetBox.SelectAll(); };
        SourceInitialized += (_, _) => NativeMethods.ApplyDarkTitleBar(this);
    }

    public IReadOnlyList<string> Sources => _sources;
    public string Target { get; private set; } = "";
    public CreateOptions Options { get; private set; } = new();
    /// <summary>대상이 이미 있고 덮어쓰기에 동의함.</summary>
    public bool ReplaceExisting { get; private set; }

    private static int IndexOf(ArchiveFormat format) => Formats.Select((f, i) => (f, i)).First(x => x.f.Format == format).i;

    private void AddSource(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return; }
        if (_sources.Contains(full, StringComparer.OrdinalIgnoreCase)) return;
        _sources.Add(full);
        SourceList.Items.Add(Directory.Exists(full) ? full + Path.DirectorySeparatorChar : full);
    }

    private string SuggestTarget(ArchiveFormat format)
    {
        var ext = Archives.DefaultExtension(format);
        if (_sources.Count == 0) return "";
        var first = _sources[0];
        var dir = Path.GetDirectoryName(first.TrimEnd(Path.DirectorySeparatorChar)) ?? "";
        string stem = _sources.Count == 1
            ? (Directory.Exists(first) ? Path.GetFileName(first.TrimEnd(Path.DirectorySeparatorChar)) : Path.GetFileNameWithoutExtension(first))
            : (Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } d ? d : DialogText.Create_DefaultName);
        return Path.Combine(dir, stem + ext);
    }

    /// <summary>경로 끝의 알려진 압축 확장자를 새 형식 확장자로 바꾼다.</summary>
    private static string ReplaceExtension(string path, ArchiveFormat format)
    {
        var ext = Archives.DefaultExtension(format);
        foreach (var known in new[] { ".tar.gz", ".tgz", ".zip", ".7z", ".tar", ".gz" })
        {
            if (path.EndsWith(known, StringComparison.OrdinalIgnoreCase)) return path[..^known.Length] + ext;
        }
        return path.Length == 0 ? path : path + ext;
    }

    private void OnFormatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FormatBox.SelectedIndex < 0) return;
        _format = Formats[FormatBox.SelectedIndex].Format;
        TargetBox.Text = ReplaceExtension(TargetBox.Text.Trim(), _format);
        ApplyCapabilities();
    }

    private void ApplyCapabilities()
    {
        var caps = Archives.CapabilitiesOf(_format);
        var label = Formats[IndexOf(_format)].Label;
        bool encrypt = caps.Create && caps.Encrypt;
        bool split = caps.Create && caps.Split;
        OkButton.IsEnabled = caps.Create;
        LevelBox.IsEnabled = caps.Create && _format != ArchiveFormat.Tar;
        EncryptGroup.IsEnabled = encrypt;
        EncryptNamesBox.IsEnabled = encrypt && _format == ArchiveFormat.SevenZip;
        if (!EncryptNamesBox.IsEnabled) EncryptNamesBox.IsChecked = false;
        if (!encrypt) ClearPasswords();
        SplitPanel.IsEnabled = split;
        if (!split) SplitBox.SelectedIndex = 0;

        var notes = new List<string>();
        if (!caps.Create) notes.Add("\u26A0 " + string.Format(DialogText.Create_FormatNotSupported, label));
        else
        {
            var missing = new List<string>();
            if (!caps.Encrypt) missing.Add(DialogText.Create_FeaturePassword);
            if (!caps.Split) missing.Add(DialogText.Create_FeatureSplit);
            if (missing.Count > 0) notes.Add(string.Format(DialogText.Create_FeaturesOff, label, string.Join(DialogText.Create_FeatureSeparator, missing)));
        }
        if (_format == ArchiveFormat.Tar) notes.Add(DialogText.Create_TarNote);
        if (_format == ArchiveFormat.GZip) notes.Add(DialogText.Create_GzNote);
        FormatStatus.Text = string.Join(" ", notes);
        FormatStatus.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClearPasswords()
    {
        PasswordMasked.Clear();
        ConfirmMasked.Clear();
        PasswordPlain.Text = "";
        ConfirmPlain.Text = "";
    }

    private bool ShowingPassword => ShowPasswordBox.IsChecked == true;
    private string Password => ShowingPassword ? PasswordPlain.Text : PasswordMasked.Password;
    private string Confirm => ShowingPassword ? ConfirmPlain.Text : ConfirmMasked.Password;

    private void OnShowPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (ShowingPassword)
        {
            PasswordPlain.Text = PasswordMasked.Password;
            ConfirmPlain.Text = ConfirmMasked.Password;
            PasswordMasked.Clear();
            ConfirmMasked.Clear();
        }
        else
        {
            PasswordMasked.Password = PasswordPlain.Text;
            ConfirmMasked.Password = ConfirmPlain.Text;
            PasswordPlain.Text = "";
            ConfirmPlain.Text = "";
        }
        var plain = ShowingPassword ? Visibility.Visible : Visibility.Collapsed;
        var masked = ShowingPassword ? Visibility.Collapsed : Visibility.Visible;
        PasswordPlain.Visibility = ConfirmPlain.Visibility = plain;
        PasswordMasked.Visibility = ConfirmMasked.Visibility = masked;
        UpdatePasswordError();
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e) => UpdatePasswordError();

    /// <summary>색만이 아니라 아이콘 + 문장으로 불일치를 알린다.</summary>
    private void UpdatePasswordError()
    {
        bool mismatch = Confirm.Length > 0 && Password != Confirm;
        PasswordError.Text = mismatch ? "\u26A0 " + DialogText.Create_PasswordMismatch : "";
        PasswordError.Visibility = mismatch ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSplitChanged(object sender, SelectionChangedEventArgs e)
    {
        bool custom = SplitBox.SelectedIndex >= 0 && SplitPresets[SplitBox.SelectedIndex].Bytes == -1;
        SplitCustomBox.IsEnabled = custom;
        if (custom) SplitCustomBox.Focus();
    }

    private void OnSourceSelectionChanged(object sender, SelectionChangedEventArgs e) => RemoveButton.IsEnabled = SourceList.SelectedItems.Count > 0;

    private void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var pick = new OpenFileDialog { Title = DialogText.Create_AddFiles_Title, Multiselect = true, Filter = DialogText.Filter_AllFiles, CheckFileExists = true };
        if (pick.ShowDialog(this) != true) return;
        bool wasEmpty = _sources.Count == 0;
        foreach (var f in pick.FileNames) AddSource(f);
        if (wasEmpty) TargetBox.Text = SuggestTarget(_format);
    }

    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var pick = new OpenFolderDialog { Title = DialogText.Create_AddFolder_Title, Multiselect = true };
        if (pick.ShowDialog(this) != true) return;
        bool wasEmpty = _sources.Count == 0;
        foreach (var f in pick.FolderNames) AddSource(f);
        if (wasEmpty) TargetBox.Text = SuggestTarget(_format);
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        foreach (var index in SourceList.SelectedItems.Cast<object>().Select(o => SourceList.Items.IndexOf(o)).OrderByDescending(i => i).ToList())
        {
            _sources.RemoveAt(index);
            SourceList.Items.RemoveAt(index);
        }
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var ext = Archives.DefaultExtension(_format);
        var label = Formats[IndexOf(_format)].Label;
        var current = TargetBox.Text.Trim();
        var save = new SaveFileDialog
        {
            Title = DialogText.Create_Save_Title,
            Filter = string.Format(DialogText.Create_SaveFilter, label, ext),
            DefaultExt = ext,
            AddExtension = true,
            OverwritePrompt = false, // 덮어쓰기는 '압축' 을 누를 때 한 번만 묻는다.
            FileName = Path.GetFileName(current),
        };
        try
        {
            var dir = Path.GetDirectoryName(current);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) save.InitialDirectory = dir;
        }
        catch (ArgumentException) { }
        if (save.ShowDialog(this) == true) TargetBox.Text = save.FileName;
    }

    private void ShowError(string message, UIElement? focus = null)
    {
        ErrorText.Text = "\u26A0 " + message;
        ErrorText.Visibility = Visibility.Visible;
        focus?.Focus();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        var caps = Archives.CapabilitiesOf(_format);
        if (!caps.Create) { ShowError(DialogText.Create_FormatCannotCreate); return; }
        if (_sources.Count == 0) { ShowError(DialogText.Create_NoSources); return; }

        var ext = Archives.DefaultExtension(_format);
        var text = TargetBox.Text.Trim();
        if (text.Length == 0) { ShowError(DialogText.Create_TargetRequired, TargetBox); return; }
        if (!text.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) text += ext;
        string target;
        try
        {
            if (!Path.IsPathFullyQualified(text)) { ShowError(DialogText.Create_TargetNotFull, TargetBox); return; }
            target = Path.GetFullPath(text);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            ShowError(DialogText.Create_TargetInvalid, TargetBox);
            return;
        }
        var targetDir = Path.GetDirectoryName(target);
        if (string.IsNullOrEmpty(targetDir) || !Directory.Exists(targetDir)) { ShowError(DialogText.Create_TargetFolderMissing, TargetBox); return; }
        if (Directory.Exists(target)) { ShowError(DialogText.Create_TargetIsFolder, TargetBox); return; }
        foreach (var s in _sources)
        {
            if (string.Equals(s, target, StringComparison.OrdinalIgnoreCase)) { ShowError(DialogText.Create_TargetIsSource, TargetBox); return; }
            if (Directory.Exists(s) && target.StartsWith(s.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                ShowError(DialogText.Create_TargetInsideSource, TargetBox);
                return;
            }
        }

        string? password = null;
        if (EncryptGroup.IsEnabled && Password.Length > 0)
        {
            if (Password != Confirm) { UpdatePasswordError(); ShowError(DialogText.Create_PasswordMismatch); return; }
            password = Password;
        }
        else if (EncryptGroup.IsEnabled && Confirm.Length > 0)
        {
            ShowError(DialogText.Password_Enter);
            return;
        }

        long? volume = null;
        if (SplitPanel.IsEnabled && SplitBox.SelectedIndex > 0)
        {
            volume = SplitPresets[SplitBox.SelectedIndex].Bytes;
            if (volume == -1)
            {
                if (!double.TryParse(SplitCustomBox.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var mb) || mb < 0.0625 || mb > 1_000_000)
                {
                    ShowError(DialogText.Create_SplitSizeInvalid, SplitCustomBox);
                    return;
                }
                volume = (long)(mb * Mb);
            }
        }

        bool replace = File.Exists(target);
        if (replace)
        {
            int answer = MessageDialog.Show(this, DialogKind.Warning, DialogText.Create_Exists_Title,
                string.Format(DialogText.Create_ExistsBody, target), null,
                [DialogText.Replace_Button, DialogText.Cancel_Button], defaultIndex: -1, cancelIndex: 1);
            if (answer != 0) return;
        }

        Target = target;
        ReplaceExisting = replace;
        Options = new CreateOptions
        {
            Format = _format,
            // TAR 는 레벨이 없으므로 기본값을 그대로 둔다(엔진이 레벨을 해석하지 않음).
            Level = LevelBox.IsEnabled ? LevelBox.SelectedIndex : new CreateOptions().Level,
            Password = password,
            // 암호를 넣으면 방식도 함께 넣는다(암호만 있고 방식 None 이면 엔진이 NotSupported).
            Encryption = password is null ? EncryptionMethod.None : EncryptionMethod.Aes256,
            EncryptNames = password is not null && EncryptNamesBox.IsEnabled && EncryptNamesBox.IsChecked == true,
            VolumeSize = volume,
        };
        ClearPasswords();
        DialogResult = true;
    }

    public static CreateArchiveDialog? Ask(Window owner, IEnumerable<string> sources, AppSettings settings)
    {
        var dlg = new CreateArchiveDialog(sources, settings) { Owner = owner };
        var ok = dlg.ShowDialog() == true;
        if (!ok) dlg.ClearPasswords();
        return ok ? dlg : null;
    }
}
