using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using OhMyPic.App.Text;
using OhMyPic.Core;
using CoreResizeMode = OhMyPic.Core.ResizeMode;

namespace OhMyPic.App.Dialogs.Edit;

/// <summary>The four resize modes with inline validation and a live "old size → new size" line for the first file.</summary>
public partial class ResizePanel : UserControl
{
    internal const int MaxPercent = 1000;
    internal const int MaxSide = 65535;
    internal const int MaxKilobytes = 1048576;

    private bool _ready;
    private bool _syncing;
    private string? _previewPath;
    private ImageInfo? _info;
    private int _loadVersion;

    public ResizePanel()
    {
        InitializeComponent();
        Load(new ResizeOptions(CoreResizeMode.Percent, Percent: 50, Width: 800, Height: 600, KeepAspect: true, LongAxis: 1024, TargetKilobytes: 500));
    }

    /// <summary>Raised after every change; read <see cref="IsValid"/> afterwards.</summary>
    public event EventHandler? Changed;

    public bool IsValid { get; private set; } = true;

    public CoreResizeMode Mode =>
        PixelsRadio.IsChecked == true ? CoreResizeMode.Pixels
        : LongAxisRadio.IsChecked == true ? CoreResizeMode.LongAxis
        : FileSizeRadio.IsChecked == true ? CoreResizeMode.TargetFileSize
        : CoreResizeMode.Percent;

    public void Load(ResizeOptions options)
    {
        _ready = false;
        PercentBox.Text = Format(options.Percent > 0 ? (int)Math.Round(options.Percent) : 50);
        WidthBox.Text = Format(options.Width > 0 ? options.Width : 800);
        HeightBox.Text = Format(options.Height > 0 ? options.Height : 600);
        KeepAspectBox.IsChecked = options.KeepAspect;
        LongAxisBox.Text = Format(options.LongAxis > 0 ? options.LongAxis : 1024);
        FileSizeBox.Text = Format(options.TargetKilobytes > 0 ? options.TargetKilobytes : 500);
        var radio = options.Mode switch
        {
            CoreResizeMode.Pixels => PixelsRadio,
            CoreResizeMode.LongAxis => LongAxisRadio,
            CoreResizeMode.TargetFileSize => FileSizeRadio,
            _ => PercentRadio,
        };
        radio.IsChecked = true;
        _ready = true;
        Refresh();
    }

    /// <summary>Current values as engine options. Only meaningful while <see cref="IsValid"/> is true.</summary>
    public ResizeOptions BuildOptions()
    {
        TryParse(PercentBox.Text, 1, MaxPercent, out int percent);
        TryParse(WidthBox.Text, 1, MaxSide, out int width);
        TryParse(HeightBox.Text, 1, MaxSide, out int height);
        TryParse(LongAxisBox.Text, 1, MaxSide, out int longAxis);
        TryParse(FileSizeBox.Text, 1, MaxKilobytes, out int kilobytes);
        return new ResizeOptions(Mode, percent, width, height, KeepAspectBox.IsChecked == true, longAxis, kilobytes);
    }

    /// <summary>Reads the size of the first file in the background so the preview line can show the result size.</summary>
    public async void SetPreviewFile(string? path)
    {
        int version = ++_loadVersion;
        _previewPath = path;
        _info = null;
        Refresh();
        if (path is null) return;
        var info = await EditPreview.ReadInfoAsync(path);
        if (version != _loadVersion) return;
        _info = info;
        SyncFrom(width: true);
        Refresh();
    }

    private void OnInputChanged(object sender, RoutedEventArgs e)
    {
        if (_ready) Refresh();
    }

    private void OnKeepAspectChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        SyncFrom(width: true);
        Refresh();
    }

    private void OnWidthChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _syncing) return;
        SyncFrom(width: true);
        Refresh();
    }

    private void OnHeightChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _syncing) return;
        SyncFrom(width: false);
        Refresh();
    }

    /// <summary>With "keep proportions" on, typing one side fills in the other from the first file's aspect ratio.</summary>
    private void SyncFrom(bool width)
    {
        if (KeepAspectBox.IsChecked != true || _info is not { Width: > 0, Height: > 0 } info) return;
        var source = width ? WidthBox : HeightBox;
        if (!TryParse(source.Text, 1, MaxSide, out int value)) return;
        var options = width
            ? new ResizeOptions(CoreResizeMode.Pixels, Width: value, Height: 0, KeepAspect: true)
            : new ResizeOptions(CoreResizeMode.Pixels, Width: 0, Height: value, KeepAspect: true);
        var (w, h) = ImageTransforms.ComputeSize(info.Width, info.Height, options);
        _syncing = true;
        try
        {
            if (width) HeightBox.Text = Format(Math.Clamp(h, 1, MaxSide));
            else WidthBox.Text = Format(Math.Clamp(w, 1, MaxSide));
        }
        finally
        {
            _syncing = false;
        }
    }

    private void Refresh()
    {
        if (!_ready) return;
        var mode = Mode;
        PercentBox.IsEnabled = mode == CoreResizeMode.Percent;
        WidthBox.IsEnabled = mode == CoreResizeMode.Pixels;
        HeightBox.IsEnabled = mode == CoreResizeMode.Pixels;
        KeepAspectBox.IsEnabled = mode == CoreResizeMode.Pixels;
        LongAxisBox.IsEnabled = mode == CoreResizeMode.LongAxis;
        FileSizeBox.IsEnabled = mode == CoreResizeMode.TargetFileSize;

        bool ok = true;
        ok &= OutputOptionsControl.ShowError(PercentError, mode == CoreResizeMode.Percent ? RangeError(PercentBox.Text, 1, MaxPercent) : null);
        ok &= OutputOptionsControl.ShowError(PixelsError, mode == CoreResizeMode.Pixels
            ? RangeError(WidthBox.Text, 1, MaxSide) ?? RangeError(HeightBox.Text, 1, MaxSide)
            : null);
        ok &= OutputOptionsControl.ShowError(LongAxisError, mode == CoreResizeMode.LongAxis ? RangeError(LongAxisBox.Text, 1, MaxSide) : null);
        ok &= OutputOptionsControl.ShowError(FileSizeError, mode == CoreResizeMode.TargetFileSize ? RangeError(FileSizeBox.Text, 1, MaxKilobytes) : null);
        IsValid = ok;
        PreviewText.Text = BuildPreview(ok);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private string BuildPreview(bool valid)
    {
        if (_previewPath is null) return "";
        string name = Path.GetFileName(_previewPath);
        if (_info is not { Width: > 0, Height: > 0 } info) return string.Format(CultureInfo.CurrentCulture, EditText.Resize_PreviewUnknown, name);
        if (!valid) return string.Format(CultureInfo.CurrentCulture, EditText.Resize_PreviewInvalid, name, info.Width, info.Height);
        var options = BuildOptions();
        if (options.Mode == CoreResizeMode.TargetFileSize)
            return string.Format(CultureInfo.CurrentCulture, EditText.Resize_PreviewSize, name, info.Width, info.Height, options.TargetKilobytes);
        try
        {
            var (w, h) = ImageTransforms.ComputeSize(info.Width, info.Height, options);
            return string.Format(CultureInfo.CurrentCulture, EditText.Resize_PreviewPixels, name, info.Width, info.Height, w, h);
        }
        catch (ArgumentOutOfRangeException)
        {
            return string.Format(CultureInfo.CurrentCulture, EditText.Resize_PreviewInvalid, name, info.Width, info.Height);
        }
    }

    internal static string Format(int value) => value.ToString(CultureInfo.CurrentCulture);

    /// <summary>Whole number in [min, max]; group separators and surrounding spaces are accepted.</summary>
    internal static bool TryParse(string text, int min, int max, out int value) =>
        int.TryParse(text.Trim(), NumberStyles.Integer | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out value) && value >= min && value <= max;

    internal static string? RangeError(string text, int min, int max) =>
        TryParse(text, min, max, out _) ? null : string.Format(CultureInfo.CurrentCulture, EditText.Error_NumberRange, Format(min), Format(max));
}
