using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using OhMyPic.App.Text;
using OhMyPic.Core;

namespace OhMyPic.App.Dialogs.Edit;

/// <summary>Rotate/flip choice, camera auto-rotate and a preview of the first file. Used by the Rotate dialog and the batch wizard.</summary>
public partial class RotatePanel : UserControl
{
    private bool _ready;
    private bool _defaultAutoRotate = true;
    private BitmapSource? _raw;
    private int _orientation = 1;
    private int _loadVersion;

    public RotatePanel()
    {
        InitializeComponent();
        Right90Radio.IsChecked = true;
        AutoRotateBox.IsChecked = true;
        _ready = true;
    }

    public RotateFlip Operation =>
        Left90Radio.IsChecked == true ? RotateFlip.Rotate270
        : Rotate180Radio.IsChecked == true ? RotateFlip.Rotate180
        : FlipHorizontalRadio.IsChecked == true ? RotateFlip.FlipHorizontal
        : FlipVerticalRadio.IsChecked == true ? RotateFlip.FlipVertical
        : RotateFlip.Rotate90;

    public RotateOptions BuildOptions() => new(Operation, AutoRotateBox.IsChecked == true);

    /// <summary>Fills the fields; <paramref name="defaultAutoRotate"/> is what Reset restores (the app setting).</summary>
    public void Load(RotateOptions options, bool defaultAutoRotate)
    {
        _ready = false;
        _defaultAutoRotate = defaultAutoRotate;
        SetOperation(options.Operation);
        AutoRotateBox.IsChecked = options.AutoRotateByExif;
        _ready = true;
        UpdatePreview();
    }

    /// <summary>Loads a small preview of <paramref name="path"/> in the background; null clears the preview.</summary>
    public async void SetPreviewFile(string? path)
    {
        int version = ++_loadVersion;
        _raw = null;
        _orientation = 1;
        PreviewImage.Source = null;
        PreviewCaption.Text = path is null ? "" : Path.GetFileName(path);
        PreviewMessage.Text = path is null ? EditText.Preview_Unavailable : EditText.Preview_Loading;
        if (path is null) return;
        var loaded = await EditPreview.LoadAsync(path, EditPreview.PreviewSide);
        if (version != _loadVersion) return;
        if (loaded is null)
        {
            PreviewMessage.Text = EditText.Preview_Unavailable;
            return;
        }
        (_raw, _orientation) = loaded.Value;
        UpdatePreview();
    }

    private void SetOperation(RotateFlip op)
    {
        var radio = op switch
        {
            RotateFlip.Rotate270 => Left90Radio,
            RotateFlip.Rotate180 => Rotate180Radio,
            RotateFlip.FlipHorizontal => FlipHorizontalRadio,
            RotateFlip.FlipVertical => FlipVerticalRadio,
            _ => Right90Radio,
        };
        radio.IsChecked = true;
    }

    private void OnOperationChanged(object sender, RoutedEventArgs e)
    {
        if (_ready) UpdatePreview();
    }

    private void OnAutoRotateChanged(object sender, RoutedEventArgs e)
    {
        if (_ready) UpdatePreview();
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        _ready = false;
        Right90Radio.IsChecked = true;
        AutoRotateBox.IsChecked = _defaultAutoRotate;
        _ready = true;
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (_raw is null) return;
        try
        {
            var image = AutoRotateBox.IsChecked == true ? ImageTransforms.ApplyOrientation(_raw, _orientation) : _raw;
            PreviewImage.Source = ImageTransforms.Rotate(image, Operation);
            PreviewMessage.Text = "";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or OutOfMemoryException)
        {
            PreviewImage.Source = null;
            PreviewMessage.Text = EditText.Preview_Unavailable;
        }
    }
}
