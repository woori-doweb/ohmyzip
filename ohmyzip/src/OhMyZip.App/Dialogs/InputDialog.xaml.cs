using System.IO;
using System.Windows;
using OhMyZip.App.Text;

namespace OhMyZip.App.Dialogs;

/// <summary>한 줄 입력(이름 바꾸기). validate 가 오류 문장을 돌려주면 닫지 않고 아이콘 문장으로 보여 준다.</summary>
public partial class InputDialog : Window
{
    private readonly Func<string, string?> _validate;

    private InputDialog(string title, string prompt, string initial, Func<string, string?> validate, string okLabel)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        ValueBox.Text = initial;
        OkButton.Content = okLabel;
        _validate = validate;
        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            // 확장자를 뺀 이름 부분만 선택(탐색기와 같은 동작).
            var stem = Path.GetFileNameWithoutExtension(initial);
            ValueBox.Select(0, stem.Length > 0 ? stem.Length : initial.Length);
        };
        SourceInitialized += (_, _) => NativeMethods.ApplyDarkTitleBar(this);
    }

    public string Value => ValueBox.Text.Trim();

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var error = _validate(Value);
        if (error is not null)
        {
            ErrorText.Text = "\u26A0 " + error;
            ErrorText.Visibility = Visibility.Visible;
            ValueBox.Focus();
            return;
        }
        DialogResult = true;
    }

    /// <summary>okLabel 은 확정 단추 문구(동사, 예: "이름 바꾸기"). 없으면 확인.</summary>
    public static string? Ask(Window owner, string title, string prompt, string initial, Func<string, string?> validate, string? okLabel = null)
    {
        var dlg = new InputDialog(title, prompt, initial, validate, okLabel ?? DialogText.Ok_Button) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.Value : null;
    }
}
