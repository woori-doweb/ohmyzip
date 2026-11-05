using System.IO;
using System.Windows;
using OhMyPic.App.Text;
using OhMyPic.Core;

namespace OhMyPic.App.Dialogs.Edit;

/// <summary>One row of the convert file table: original name → new name.</summary>
public sealed record ConvertRow(string Name, string TargetName);

/// <summary>Convert dialog: target format, bits per pixel, quality, frames, file table and output options. Confirm verb: Convert.</summary>
public partial class ConvertDialog : Window
{
    private readonly IReadOnlyList<string> _files;

    internal ConvertDialog(IReadOnlyList<string> files, AppSettings settings)
    {
        InitializeComponent();
        _files = files;
        HeadingText.Text = EditDialogs.HeadingFor(files);
        OptionsPanel.Load(EditSession.Convert ?? new ConvertOptions(ImageFormatKind.Jpeg, ConvertPanel.NormalizeQuality(settings.JpegQuality), FrameMode.FirstOnly));
        OptionsPanel.SetPreviewFile(files.Count > 0 ? files[0] : null);
        OutputPanel.Load(EditSession.ConvertOutput ?? EditSession.DefaultOutput(), EditText.Convert_DefaultSuffix);
        OptionsPanel.Changed += (_, _) => Refresh();
        OutputPanel.Changed += (_, _) => Refresh();
        Refresh();
        SourceInitialized += (_, _) => NativeMethods.ApplyDarkTitleBar(this);
    }

    public ConvertOptions Options { get; private set; } = new(ImageFormatKind.Jpeg);

    public OutputOptions Output { get; private set; } = EditSession.DefaultOutput();

    private void Refresh()
    {
        string extension = ImageFormats.DefaultExtension(OptionsPanel.Format);
        string prefix = OutputPanel.Prefix, suffix = OutputPanel.Suffix;
        FileTable.ItemsSource = _files
            .Select(f => new ConvertRow(Path.GetFileName(f), prefix + Path.GetFileNameWithoutExtension(f) + suffix + extension))
            .ToList();
        ConfirmButton.IsEnabled = OptionsPanel.IsValid && OutputPanel.IsValid;
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (!OptionsPanel.IsValid || !OutputPanel.IsValid) return;
        Options = OptionsPanel.BuildOptions();
        Output = OutputPanel.BuildOptions();
        EditSession.Convert = Options;
        EditSession.ConvertOutput = Output;
        DialogResult = true;
    }
}
