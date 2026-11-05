using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using OhMyPic.App.Text;
using OhMyPic.Core;

namespace OhMyPic.App.Dialogs.Edit;

/// <summary>Target format, informational bits per pixel, JPEG quality and multi-frame handling. Used by the Convert dialog and the batch wizard.</summary>
public partial class ConvertPanel : UserControl
{
    internal const int DefaultQuality = 85;

    private bool _ready;
    private bool _syncing;
    private bool _firstHasAlpha;

    public ConvertPanel()
    {
        InitializeComponent();
        FormatBox.ItemsSource = ImageFormats.WritableFormats.Select(FormatLabelOf).ToList();
        Load(new ConvertOptions(ImageFormatKind.Jpeg, DefaultQuality, FrameMode.FirstOnly));
    }

    /// <summary>Raised after every change; read <see cref="IsValid"/> afterwards.</summary>
    public event EventHandler? Changed;

    public bool IsValid { get; private set; } = true;

    public ImageFormatKind Format => ImageFormats.WritableFormats[Math.Max(0, FormatBox.SelectedIndex)];

    /// <summary>Short upper-case label the format combo shows (JPG, TIF, ...).</summary>
    public static string FormatLabelOf(ImageFormatKind kind) => ImageFormats.DefaultExtension(kind).TrimStart('.').ToUpperInvariant();

    /// <summary>Clamps a stored quality to 1..100; anything else falls back to 85.</summary>
    public static int NormalizeQuality(int quality) => quality is >= 1 and <= 100 ? quality : DefaultQuality;

    public void Load(ConvertOptions options)
    {
        _ready = false;
        int index = -1;
        for (int i = 0; i < ImageFormats.WritableFormats.Count; i++)
            if (ImageFormats.WritableFormats[i] == options.Format) index = i;
        FormatBox.SelectedIndex = index >= 0 ? index : 2;
        int quality = NormalizeQuality(options.JpegQuality);
        QualitySlider.Value = quality;
        QualityBox.Text = ResizePanel.Format(quality);
        switch (options.Frames)
        {
            case FrameMode.SplitEach:
                AllFramesRadio.IsChecked = true;
                SplitFramesRadio.IsChecked = true;
                break;
            case FrameMode.CombineOne:
                AllFramesRadio.IsChecked = true;
                CombineFramesRadio.IsChecked = true;
                break;
            default:
                FirstFrameRadio.IsChecked = true;
                SplitFramesRadio.IsChecked = true;
                break;
        }
        _ready = true;
        Refresh();
    }

    /// <summary>Current values as engine options. Only meaningful while <see cref="IsValid"/> is true.</summary>
    public ConvertOptions BuildOptions()
    {
        var format = Format;
        var frames = FirstFrameRadio.IsChecked == true ? FrameMode.FirstOnly
            : CombineFramesRadio.IsChecked == true && ImageFormats.SupportsMultipleFrames(format) ? FrameMode.CombineOne
            : FrameMode.SplitEach;
        return new ConvertOptions(format, (int)Math.Round(QualitySlider.Value), frames);
    }

    /// <summary>Reads whether the first file has transparency so the bits-per-pixel line can show 32 for PNG/TIF.</summary>
    public async void SetPreviewFile(string? path)
    {
        _firstHasAlpha = false;
        Refresh();
        if (path is null) return;
        var info = await EditPreview.ReadInfoAsync(path);
        _firstHasAlpha = info?.HasAlpha == true;
        Refresh();
    }

    private void OnFormatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready) Refresh();
    }

    private void OnInputChanged(object sender, RoutedEventArgs e)
    {
        if (_ready) Refresh();
    }

    private void OnQualitySliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _syncing) return;
        _syncing = true;
        try { QualityBox.Text = ResizePanel.Format((int)Math.Round(QualitySlider.Value)); }
        finally { _syncing = false; }
        Refresh();
    }

    private void OnQualityTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _syncing) return;
        if (ResizePanel.TryParse(QualityBox.Text, 1, 100, out int value))
        {
            _syncing = true;
            try { QualitySlider.Value = value; }
            finally { _syncing = false; }
        }
        Refresh();
    }

    private void Refresh()
    {
        if (!_ready) return;
        var format = Format;
        bool jpeg = format == ImageFormatKind.Jpeg;
        bool multi = ImageFormats.SupportsMultipleFrames(format);

        // Informational only: the engine picks the pixel format for the target.
        string bpp = format switch
        {
            ImageFormatKind.Gif => "8",
            ImageFormatKind.Png or ImageFormatKind.Tiff when _firstHasAlpha => "32",
            _ => "24",
        };
        BppBox.ItemsSource = new[] { bpp };
        BppBox.SelectedIndex = 0;

        QualitySlider.IsEnabled = jpeg;
        QualityBox.IsEnabled = jpeg;
        QualityNote.Text = jpeg ? EditText.Convert_QualityNoteJpeg : EditText.Convert_QualityNoteOther;

        bool all = AllFramesRadio.IsChecked == true;
        SplitFramesRadio.IsEnabled = all;
        CombineFramesRadio.IsEnabled = all && multi;
        if (CombineFramesRadio.IsChecked == true && !multi)
        {
            _ready = false;
            SplitFramesRadio.IsChecked = true;
            _ready = true;
        }

        IsValid = OutputOptionsControl.ShowError(QualityError, jpeg ? ResizePanel.RangeError(QualityBox.Text, 1, 100) : null);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
