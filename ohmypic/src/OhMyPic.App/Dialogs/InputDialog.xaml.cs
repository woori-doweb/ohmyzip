using System.Windows;

namespace OhMyPic.App.Dialogs;

/// <summary>
/// One-line input (rename). An optional fixed suffix (the file extension) is shown next to the box and is not editable.
/// When validate returns an error sentence the dialog stays open and shows it with an icon.
/// </summary>
public partial class InputDialog : Window
{
    private readonly Func<string, string?> _validate;

    private InputDialog(string title, string prompt, string initial, string suffix, Func<string, string?> validate, string okLabel)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        ValueBox.Text = initial;
        SuffixText.Text = suffix;
        SuffixText.Visibility = string.IsNullOrEmpty(suffix) ? Visibility.Collapsed : Visibility.Visible;
        OkButton.Content = okLabel;
        _validate = validate;
        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
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

    /// <summary>okLabel is the confirming verb (for example "Rename"). Returns null when cancelled.</summary>
    public static string? Ask(Window owner, string title, string prompt, string initial, string suffix, Func<string, string?> validate, string okLabel)
    {
        var dlg = new InputDialog(title, prompt, initial, suffix, validate, okLabel) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.Value : null;
    }
}
