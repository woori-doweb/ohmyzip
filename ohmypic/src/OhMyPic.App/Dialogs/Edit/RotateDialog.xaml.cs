using System.Windows;
using OhMyPic.Core;

namespace OhMyPic.App.Dialogs.Edit;

/// <summary>Rotate dialog: rotate/flip choice, camera auto-rotate, preview and output options. Confirm verb: Rotate.</summary>
public partial class RotateDialog : Window
{
    internal RotateDialog(IReadOnlyList<string> files, AppSettings settings)
    {
        InitializeComponent();
        HeadingText.Text = EditDialogs.HeadingFor(files);
        OptionsPanel.Load(EditSession.Rotate ?? new RotateOptions(RotateFlip.Rotate90, settings.AutoRotateByExif), settings.AutoRotateByExif);
        OptionsPanel.SetPreviewFile(files.Count > 0 ? files[0] : null);
        OutputPanel.Load(EditSession.RotateOutput ?? EditSession.DefaultOutput());
        OutputPanel.Changed += (_, _) => UpdateConfirm();
        UpdateConfirm();
        SourceInitialized += (_, _) => NativeMethods.ApplyDarkTitleBar(this);
    }

    public RotateOptions Options { get; private set; } = new(RotateFlip.Rotate90);

    public OutputOptions Output { get; private set; } = EditSession.DefaultOutput();

    private void UpdateConfirm() => ConfirmButton.IsEnabled = OutputPanel.IsValid;

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (!OutputPanel.IsValid) return;
        Options = OptionsPanel.BuildOptions();
        Output = OutputPanel.BuildOptions();
        EditSession.Rotate = Options;
        EditSession.RotateOutput = Output;
        DialogResult = true;
    }
}
