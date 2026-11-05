using System.Windows;
using OhMyPic.Core;
using CoreResizeMode = OhMyPic.Core.ResizeMode;

namespace OhMyPic.App.Dialogs.Edit;

/// <summary>Resize dialog: four resize modes, live result size of the first file and output options. Confirm verb: Resize.</summary>
public partial class ResizeDialog : Window
{
    internal ResizeDialog(IReadOnlyList<string> files)
    {
        InitializeComponent();
        HeadingText.Text = EditDialogs.HeadingFor(files);
        if (EditSession.Resize is { } last) OptionsPanel.Load(last);
        OptionsPanel.SetPreviewFile(files.Count > 0 ? files[0] : null);
        OutputPanel.Load(EditSession.ResizeOutput ?? EditSession.DefaultOutput());
        OptionsPanel.Changed += (_, _) => UpdateConfirm();
        OutputPanel.Changed += (_, _) => UpdateConfirm();
        UpdateConfirm();
        SourceInitialized += (_, _) => NativeMethods.ApplyDarkTitleBar(this);
    }

    public ResizeOptions Options { get; private set; } = new(CoreResizeMode.Percent);

    public OutputOptions Output { get; private set; } = EditSession.DefaultOutput();

    private void UpdateConfirm() => ConfirmButton.IsEnabled = OptionsPanel.IsValid && OutputPanel.IsValid;

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (!OptionsPanel.IsValid || !OutputPanel.IsValid) return;
        Options = OptionsPanel.BuildOptions();
        Output = OutputPanel.BuildOptions();
        EditSession.Resize = Options;
        EditSession.ResizeOutput = Output;
        DialogResult = true;
    }
}
