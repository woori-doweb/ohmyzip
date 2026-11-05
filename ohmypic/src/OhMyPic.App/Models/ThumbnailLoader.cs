using System.Windows.Threading;
using OhMyPic.Core;

namespace OhMyPic.App.Models;

/// <summary>
/// Background decoder for one folder listing. Requests are served newest first (the tiles the user scrolled to),
/// on two worker tasks off the UI thread. <see cref="Dispose"/> (folder change) cancels everything still queued.
/// Results are posted back to the UI dispatcher.
/// </summary>
public sealed class ThumbnailLoader : IDisposable
{
    private const int Workers = 2;

    private readonly Dispatcher _dispatcher;
    private readonly int _decodeSide;
    private readonly bool _autoRotate;
    private readonly CancellationTokenSource _cts = new();
    private readonly Stack<(ImageItem Item, bool Thumbnail)> _pending = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly object _gate = new();

    public ThumbnailLoader(Dispatcher dispatcher, int decodeSide, bool autoRotate)
    {
        _dispatcher = dispatcher;
        _decodeSide = Math.Max(16, decodeSide);
        _autoRotate = autoRotate;
        for (int i = 0; i < Workers; i++) _ = Task.Run(WorkAsync);
    }

    public void RequestThumbnail(ImageItem item) => Enqueue(item, thumbnail: true);

    public void RequestInfo(ImageItem item) => Enqueue(item, thumbnail: false);

    private void Enqueue(ImageItem item, bool thumbnail)
    {
        if (_cts.IsCancellationRequested) return;
        lock (_gate) _pending.Push((item, thumbnail));
        _signal.Release();
    }

    private async Task WorkAsync()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            (ImageItem Item, bool Thumbnail) job;
            lock (_gate)
            {
                if (_pending.Count == 0) continue;
                job = _pending.Pop();
            }
            if (token.IsCancellationRequested) return;
            if (job.Thumbnail)
            {
                System.Windows.Media.Imaging.BitmapSource? bitmap;
                try
                {
                    bitmap = ImageLoader.LoadThumbnail(job.Item.Path, _decodeSide, _autoRotate);
                }
                catch (Exception)
                {
                    // Unreadable file: the tile keeps its placeholder glyph.
                    bitmap = null;
                }
                if (!token.IsCancellationRequested)
                    _ = _dispatcher.InvokeAsync(() => job.Item.SetThumbnail(bitmap), DispatcherPriority.Background);
            }
            else
            {
                ImageInfo? info;
                try
                {
                    info = ImageLoader.ReadInfo(job.Item.Path);
                }
                catch (Exception)
                {
                    info = null;
                }
                if (!token.IsCancellationRequested)
                    _ = _dispatcher.InvokeAsync(() => job.Item.SetInfo(info), DispatcherPriority.Background);
            }
        }
    }

    public void Dispose()
    {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();
        lock (_gate) _pending.Clear();
    }
}
