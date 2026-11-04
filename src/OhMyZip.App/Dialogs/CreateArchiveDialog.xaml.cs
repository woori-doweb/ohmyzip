using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OhMyZip.App.Settings;
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
        "0 - 저장(압축 안 함)", "1 - 가장 빠름", "2", "3 - 빠름", "4", "5 - 보통", "6", "7 - 높음", "8", "9 - 최고",
    ];

    private const long Mb = 1024L * 1024;

    /// <summary>분할 크기 선택지. null = 분할 안 함, -1 = 직접 입력.</summary>
    private static readonly (string Label, long? Bytes)[] SplitPresets =
    [
        ("분할 안 함", null),
        ("10 MB", 10 * Mb),
        ("100 MB", 100 * Mb),
        ("700 MB (CD)", 700 * Mb),
        ("4,000 MB (FAT32 한도 이하)", 4000 * Mb),
        ("직접 입력", -1),
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
            : (Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } d ? d : "새 압축");
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
        if (!caps.Create) notes.Add($"\u26A0 {label} 형식으로 만들기는 아직 지원하지 않습니다(엔진 준비 중).");
        else
        {
            var missing = new List<string>();
            if (!caps.Encrypt) missing.Add("암호");
            if (!caps.Split) missing.Add("분할");
            if (missing.Count > 0) notes.Add($"{label} 형식은 아직 {string.Join("·", missing)} 기능을 지원하지 않아 해당 옵션을 껐습니다.");
        }
        if (_format == ArchiveFormat.Tar) notes.Add("TAR 는 압축하지 않고 묶기만 합니다(레벨 없음).");
        if (_format == ArchiveFormat.GZip) notes.Add("GZ 는 파일 하나만 담을 수 있습니다.");
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
        PasswordError.Text = mismatch ? "\u26A0 암호와 암호 확인이 다릅니다. 두 칸에 같은 암호를 입력하세요." : "";
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
        var pick = new OpenFileDialog { Title = "압축할 파일 추가", Multiselect = true, Filter = "모든 파일 (*.*)|*.*", CheckFileExists = true };
        if (pick.ShowDialog(this) != true) return;
        bool wasEmpty = _sources.Count == 0;
        foreach (var f in pick.FileNames) AddSource(f);
        if (wasEmpty) TargetBox.Text = SuggestTarget(_format);
    }

    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var pick = new OpenFolderDialog { Title = "압축할 폴더 추가", Multiselect = true };
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
            Title = "새 압축 파일 저장",
            Filter = $"{label} 압축 파일 (*{ext})|*{ext}",
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
        if (!caps.Create) { ShowError("이 형식으로는 아직 만들 수 없습니다. 형식에서 다른 항목을 고르세요."); return; }
        if (_sources.Count == 0) { ShowError("압축할 파일이나 폴더를 추가하세요."); return; }

        var ext = Archives.DefaultExtension(_format);
        var text = TargetBox.Text.Trim();
        if (text.Length == 0) { ShowError("압축 파일 경로를 입력하세요.", TargetBox); return; }
        if (!text.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) text += ext;
        string target;
        try
        {
            if (!Path.IsPathFullyQualified(text)) { ShowError("압축 파일 경로는 드라이브부터 시작하는 전체 경로로 입력하세요.", TargetBox); return; }
            target = Path.GetFullPath(text);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            ShowError("압축 파일 경로가 올바르지 않습니다.", TargetBox);
            return;
        }
        var targetDir = Path.GetDirectoryName(target);
        if (string.IsNullOrEmpty(targetDir) || !Directory.Exists(targetDir)) { ShowError("저장할 폴더가 없습니다.", TargetBox); return; }
        if (Directory.Exists(target)) { ShowError("같은 이름의 폴더가 있습니다.", TargetBox); return; }
        foreach (var s in _sources)
        {
            if (string.Equals(s, target, StringComparison.OrdinalIgnoreCase)) { ShowError("압축할 파일과 같은 경로에는 저장할 수 없습니다. 다른 이름이나 폴더를 고르세요.", TargetBox); return; }
            if (Directory.Exists(s) && target.StartsWith(s.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                ShowError("압축할 폴더 안에는 압축 파일을 저장할 수 없습니다. 그 폴더 밖의 위치를 고르세요.", TargetBox);
                return;
            }
        }

        string? password = null;
        if (EncryptGroup.IsEnabled && Password.Length > 0)
        {
            if (Password != Confirm) { UpdatePasswordError(); ShowError("암호와 암호 확인이 다릅니다. 두 칸에 같은 암호를 입력하세요."); return; }
            password = Password;
        }
        else if (EncryptGroup.IsEnabled && Confirm.Length > 0)
        {
            ShowError("암호를 입력하세요.");
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
                    ShowError("분할 크기는 0.0625(64KB) 이상 1,000,000 이하의 MB 값으로 입력하세요.", SplitCustomBox);
                    return;
                }
                volume = (long)(mb * Mb);
            }
        }

        bool replace = File.Exists(target);
        if (replace)
        {
            int answer = MessageDialog.Show(this, DialogKind.Warning, "같은 이름의 압축 파일이 이미 있습니다",
                $"{target}\n새 압축이 성공하면 기존 파일을 바꿉니다. 실패하거나 취소하면 기존 파일은 그대로입니다.", null,
                ["바꾸기", "취소"], defaultIndex: -1, cancelIndex: 1);
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
