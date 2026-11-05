using System.ComponentModel;
using System.Globalization;
using System.Windows.Media.Imaging;
using OhMyPic.App.Text;
using OhMyPic.Core;

namespace OhMyPic.App.Models;

/// <summary>
/// One image in the thumbnail area. Thumbnail and dimensions load lazily: the first binding read of
/// <see cref="Thumbnail"/> or <see cref="DimensionsText"/> queues work on the folder's <see cref="ThumbnailLoader"/>,
/// so only realized (visible) tiles cost a decode.
/// </summary>
public sealed class ImageItem : INotifyPropertyChanged
{
    private ThumbnailLoader _loader;
    private BitmapSource? _thumbnail;
    private bool _thumbnailRequested;
    private bool _thumbnailFailed;
    private ImageInfo? _info;
    private bool _infoRequested;

    public ImageItem(ImageEntry entry, ThumbnailLoader loader)
    {
        Entry = entry;
        _loader = loader;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ImageEntry Entry { get; }
    public string Path => Entry.Path;
    public string Name => Entry.Name;
    public long Size => Entry.Size;
    public DateTime Modified => Entry.Modified;

    public string TypeName => string.Format(MainText.TypeName_Format, Entry.Extension.TrimStart('.').ToUpperInvariant());
    public string SizeText => FormatSize(Entry.Size);
    public string ModifiedText => Entry.Modified.ToString("g", CultureInfo.CurrentCulture);

    public BitmapSource? Thumbnail
    {
        get
        {
            if (_thumbnail is null && !_thumbnailRequested && !_thumbnailFailed)
            {
                _thumbnailRequested = true;
                _loader.RequestThumbnail(this);
            }
            return _thumbnail;
        }
    }

    /// <summary>True while no thumbnail is shown (the tile shows a generic picture glyph instead).</summary>
    public bool ShowPlaceholder => _thumbnail is null;

    public ImageInfo? Info => _info;

    /// <summary>"1280 × 800", or empty until the header was read (or when it could not be read).</summary>
    public string DimensionsText
    {
        get
        {
            if (_info is null && !_infoRequested)
            {
                _infoRequested = true;
                _loader.RequestInfo(this);
            }
            return _info is null ? "" : string.Format(MainText.Dimensions_Format, _info.Width, _info.Height);
        }
    }

    /// <summary>Same entry (path, size, date) seen again after a refresh: keep loaded data, re-arm pending requests on the new loader.</summary>
    internal bool SameFile(ImageEntry other) =>
        string.Equals(other.Path, Entry.Path, StringComparison.OrdinalIgnoreCase) && other.Size == Entry.Size && other.Modified == Entry.Modified;

    internal void Rebind(ThumbnailLoader loader)
    {
        _loader = loader;
        if (_thumbnail is null) _thumbnailRequested = false;
        if (_info is null) _infoRequested = false;
        Raise(nameof(Thumbnail));
        Raise(nameof(DimensionsText));
    }

    internal void SetThumbnail(BitmapSource? thumbnail)
    {
        _thumbnail = thumbnail;
        _thumbnailFailed = thumbnail is null;
        Raise(nameof(Thumbnail));
        Raise(nameof(ShowPlaceholder));
    }

    internal void SetInfo(ImageInfo? info)
    {
        _info = info;
        Raise(nameof(Info));
        Raise(nameof(DimensionsText));
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return string.Format(MainText.Size_Bytes, bytes);
        double kb = bytes / 1024.0;
        if (kb < 1024) return string.Format(MainText.Size_KB, kb.ToString("N0", CultureInfo.CurrentCulture));
        double mb = kb / 1024.0;
        if (mb < 1024) return string.Format(MainText.Size_MB, mb.ToString("N1", CultureInfo.CurrentCulture));
        return string.Format(MainText.Size_GB, (mb / 1024.0).ToString("N2", CultureInfo.CurrentCulture));
    }
}
