using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace OhMyZip.App.Dialogs;

/// <summary>
/// 압축파일 암호 입력. 암호는 반환값으로만 넘기고 어디에도 기록하지 않는다.
/// wrong=true 면 '암호가 올바르지 않습니다' 를 경고 아이콘과 함께 보여 준다.
/// </summary>
public partial class PasswordDialog : Window
{
    private PasswordDialog(string archiveName, bool wrong)
    {
        InitializeComponent();
        MessageText.Text = wrong ? "암호가 올바르지 않습니다. 다시 입력하세요." : "암호가 필요합니다.";
        MessageGlyph.Text = wrong ? "\uE7BA" : "\uE72E";
        MessageGlyph.SetResourceReference(TextBlock.ForegroundProperty, wrong ? Theme.ThemeManager.Error : Theme.ThemeManager.Text);
        AutomationProperties.SetName(MessageGlyph, wrong ? "경고" : "암호");
        ArchiveText.Text = archiveName;
        Loaded += (_, _) => Masked.Focus();
        SourceInitialized += (_, _) => NativeMethods.ApplyDarkTitleBar(this);
    }

    private string Value => ShowBox.IsChecked == true ? Plain.Text : Masked.Password;

    private void OnShowChanged(object sender, RoutedEventArgs e)
    {
        if (ShowBox.IsChecked == true)
        {
            Plain.Text = Masked.Password;
            Masked.Visibility = Visibility.Collapsed;
            Plain.Visibility = Visibility.Visible;
            Plain.Focus();
            Plain.CaretIndex = Plain.Text.Length;
        }
        else
        {
            Masked.Password = Plain.Text;
            Plain.Text = "";
            Plain.Visibility = Visibility.Collapsed;
            Masked.Visibility = Visibility.Visible;
            Masked.Focus();
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (Value.Length == 0)
        {
            MessageText.Text = "암호를 입력하세요.";
            return;
        }
        DialogResult = true;
    }

    /// <summary>입력한 암호, 취소하면 null.</summary>
    public static string? Ask(Window owner, string archiveName, bool wrong)
    {
        var dlg = new PasswordDialog(archiveName, wrong) { Owner = owner };
        var ok = dlg.ShowDialog() == true;
        var value = ok ? dlg.Value : null;
        dlg.Masked.Clear();
        dlg.Plain.Text = "";
        return value;
    }
}
