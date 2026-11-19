using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using OhMyPic.App.Dialogs;
using OhMyPic.App.Text;
using OhMyPic.Core;
using OhMyPic.Core.Editing;

namespace OhMyPic.App.Editor;

/// <summary>
/// Editor export (OP-F60): PNG or JPG, JPG quality, scale 0.5×–3×, final pixel size and the exact file size (encoded in
/// memory on a background task, newest choice wins). Save asks for the target with the Windows dialog, asks before
/// replacing the original or another existing file (default: No), then writes through <see cref="EditExport.Save"/>.
/// A screenshot (capture mode) has no original: PNG is preselected and there is no "replace the original" question.
/// </summary>
public partial class ExportDialog : Window
{
    private static readonly double[] Scales = [0.5, 1, 2, 3];

    /// <summary>The file being edited; null for a screenshot.</summary>
    private readonly string? _sourcePath;

    private readonly string? _folder;
    private readonly string _baseName;
    private readonly int _width;
    private readonly int _height;

    /// <summary>Full-resolution result (adjustments applied), rendered once; estimates and Save wait for it.</summary>
    private readonly Task<RasterImage> _render;

    private readonly DispatcherTimer _estimateTimer;
    private readonly List<RadioButton> _scaleButtons = [];
    private bool _ready;
    private bool _estimating;
    private bool _saving;
    private bool _closed;
    private int _version;

    /// <param name="sourcePath">The file being edited, or null for a screenshot.</param>
    /// <param name="folder">Initial folder of the file dialog (used when it exists).</param>
    /// <param name="baseName">Suggested file name without extension.</param>
    internal ExportDialog(string? sourcePath, string? folder, string baseName, RasterImage source, AdjustmentSettings settings)
    {
        InitializeComponent();
        _sourcePath = sourcePath;
        _folder = folder;
        _baseName = baseName;
        _width = source.Width;
        _height = source.Height;
        _render = settings.IsIdentity ? Task.FromResult(source) : Task.Run(() => Adjustments.Apply(source, settings));

        foreach (var scale in Scales)
        {
            string label = string.Format(EditorText.Export_ScaleValue, scale.ToString(CultureInfo.CurrentCulture));
            var radio = new RadioButton { GroupName = "Scale", Content = label, Tag = scale };
            AutomationProperties.SetName(radio, label);
            AutomationProperties.SetAutomationId(radio, "OP-Export-Scale-" + ((int)Math.Round(scale * 100)).ToString(CultureInfo.InvariantCulture));
            radio.Checked += OnOptionChanged;
            ScalePanel.Children.Add(radio);
            _scaleButtons.Add(radio);
        }
        _scaleButtons[Array.IndexOf(Scales, 1.0)].IsChecked = true;
        bool jpeg = sourcePath is not null && ImageFormats.FromExtension(Path.GetExtension(sourcePath)) == ImageFormatKind.Jpeg;
        (jpeg ? JpegRadio : PngRadio).IsChecked = true;
        QualitySlider.Value = EditExport.DefaultJpegQuality;

        _estimateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _estimateTimer.Tick += OnEstimateTick;
        _ready = true;
        Refresh();

        SourceInitialized += (_, _) => NativeMethods.ApplyDarkTitleBar(this);
        Closing += (_, e) => { if (_saving) e.Cancel = true; };
        Closed += (_, _) =>
        {
            _closed = true;
            _estimateTimer.Stop();
        };
    }

    /// <summary>Full path of the written file after a successful Save.</summary>
    public string? SavedPath { get; private set; }

    private ExportFormat Format => JpegRadio.IsChecked == true ? ExportFormat.Jpeg : ExportFormat.Png;

    private int Quality => (int)Math.Round(QualitySlider.Value);

    private double Scale => _scaleButtons.FirstOrDefault(b => b.IsChecked == true)?.Tag is double s ? s : 1;

    private bool IsTooLarge(double scale)
    {
        var (w, h) = EditExport.ScaledSize(_width, _height, scale);
        return (long)w * h > ImageLoader.DefaultMaxPixels;
    }

    private void OnOptionChanged(object sender, RoutedEventArgs e) => Refresh();

    private void OnQualityChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => Refresh();

    /// <summary>Updates the quality state and final size at once, then restarts the (debounced) estimate.</summary>
    private void Refresh()
    {
        if (!_ready) return;
        QualitySlider.IsEnabled = Format == ExportFormat.Jpeg && !_saving;
        QualityValue.Text = Quality.ToString(CultureInfo.CurrentCulture);
        double scale = Scale;
        var (w, h) = EditExport.ScaledSize(_width, _height, scale);
        FinalSizeText.Text = string.Format(EditorText.Export_FinalSizeValue,
            w.ToString("N0", CultureInfo.CurrentCulture), h.ToString("N0", CultureInfo.CurrentCulture));
        _version++;
        _estimateTimer.Stop();
        bool tooLarge = IsTooLarge(scale);
        SaveButton.IsEnabled = !_saving && !tooLarge;
        if (tooLarge)
        {
            EstimateText.Text = EditorText.Export_TooLarge;
            return;
        }
        EstimateText.Text = EditorText.Export_Calculating;
        _estimateTimer.Start();
    }

    private async void OnEstimateTick(object? sender, EventArgs e)
    {
        _estimateTimer.Stop();
        // A running estimate loops again when it sees newer options.
        if (_estimating || _closed) return;
        _estimating = true;
        try
        {
            while (!_closed)
            {
                int version = _version;
                var (format, quality, scale) = (Format, Quality, Scale);
                if (IsTooLarge(scale)) break;
                var image = await _render;
                long bytes = await Task.Run(() => EditExport.Estimate(image, format, quality, scale).Bytes);
                if (version != _version) continue;
                if (!_closed) EstimateText.Text = EditorWindow.FormatBytes(bytes);
                break;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_closed) EstimateText.Text = EditorText.Export_EstimateUnavailable;
        }
        finally
        {
            _estimating = false;
        }
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_saving || !SaveButton.IsEnabled) return;
        var format = Format;
        int quality = Quality;
        double scale = Scale;
        string ext = format == ExportFormat.Jpeg ? ".jpg" : ".png";
        var dialog = new SaveFileDialog
        {
            Title = EditorText.Export_FileDialogTitle,
            Filter = format == ExportFormat.Jpeg ? $"{EditorText.Export_FilterJpeg}|*.jpg;*.jpeg" : $"{EditorText.Export_FilterPng}|*.png",
            FileName = _baseName + ext,
            DefaultExt = ext,
            AddExtension = true,
            // Replacing is asked below, with the original file called out separately.
            OverwritePrompt = false,
        };
        if (_folder is not null && Directory.Exists(_folder)) dialog.InitialDirectory = _folder;
        if (dialog.ShowDialog(this) != true) return;

        string target = Path.GetFullPath(dialog.FileName);
        if (!HasExtension(target, format)) target += ext;
        string name = Path.GetFileName(target);
        if (_sourcePath is not null && string.Equals(target, Path.GetFullPath(_sourcePath), StringComparison.OrdinalIgnoreCase))
        {
            if (!ConfirmReplace(string.Format(EditorText.Export_OverwriteOriginalHeading, name), EditorText.Export_OverwriteOriginalBody)) return;
        }
        else if (File.Exists(target))
        {
            if (!ConfirmReplace(string.Format(EditorText.Export_OverwriteHeading, name), EditorText.Export_OverwriteBody)) return;
        }

        SetSaving(true);
        try
        {
            var image = await _render;
            await Task.Run(() => EditExport.Save(target, image, format, quality, scale));
            SavedPath = target;
            SetSaving(false);
            DialogResult = true;
        }
        catch (Exception ex) when (ex is ImageOperationException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            SetSaving(false);
            MessageDialog.Error(this, string.Format(EditorText.Export_FailedTitle, name), MainWindow.DescribeIoError(ex));
        }
    }

    private static bool HasExtension(string path, ExportFormat format)
    {
        string ext = Path.GetExtension(path);
        return format == ExportFormat.Jpeg
            ? ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            : ext.Equals(".png", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Replace + Cancel, Cancel is the default (Enter does not replace).</summary>
    private bool ConfirmReplace(string heading, string body) =>
        MessageDialog.Show(this, DialogKind.Warning, heading, body, null, [EditorText.Export_Replace, DialogText.Cancel_Button], defaultIndex: 1, cancelIndex: 1) == 0;

    private void SetSaving(bool saving)
    {
        _saving = saving;
        OptionsGrid.IsEnabled = !saving;
        CancelButton.IsEnabled = !saving;
        SaveButton.Content = saving ? EditorText.Export_Saving : EditorText.Export_Save;
        SaveButton.IsEnabled = !saving && !IsTooLarge(Scale);
        QualitySlider.IsEnabled = Format == ExportFormat.Jpeg && !saving;
    }
}
