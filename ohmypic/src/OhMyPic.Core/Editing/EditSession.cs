using System.Windows;
using CropOps = OhMyPic.Core.Editing.Crop;

namespace OhMyPic.Core.Editing;

/// <summary>
/// Single-image edit state: a base image (geometry and drawings applied) plus adjustment settings rendered on top, with
/// undo/redo. Adjustment steps store only settings; geometric and drawing steps keep a reference to the replaced base
/// image (images are never modified in place, so no copy is made). Image bytes held by the history are capped by a
/// budget; the oldest steps are dropped first.
/// </summary>
public sealed class EditSession
{
    public const long DefaultHistoryBudgetBytes = 512L * 1024 * 1024;

    /// <summary>Upper bound for <see cref="CanEdit"/>'s memory estimate (1.5 GB).</summary>
    public const long MaxEditBytes = 1536L * 1024 * 1024;

    /// <summary>Image-sized buffers reserved for history and rendering in the <see cref="CanEdit"/> estimate.</summary>
    public const int HistoryReserveImages = 2;

    private readonly List<Step> _undo = [];
    private readonly List<Step> _redo = [];

    /// <exception cref="ImageOperationException"><see cref="EditErrorCode.TooLarge"/> when <see cref="CanEdit"/> rejects the image.</exception>
    public EditSession(RasterImage image, long historyBudgetBytes = DefaultHistoryBudgetBytes)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfNegative(historyBudgetBytes);
        if (!CanEdit(image.Width, image.Height, out long estimate))
            throw new ImageOperationException(EditErrorCode.TooLarge,
                $"{image.Width}x{image.Height} needs about {estimate:N0} bytes to edit; the limit is {MaxEditBytes:N0}.");
        Base = image;
        HistoryBudgetBytes = historyBudgetBytes;
    }

    /// <summary>Current image with all geometric and drawing steps applied (adjustments not applied).</summary>
    public RasterImage Base { get; private set; }

    public AdjustmentSettings Settings { get; private set; } = AdjustmentSettings.Identity;

    public long HistoryBudgetBytes { get; }

    /// <summary>Image bytes currently held by undo and redo steps.</summary>
    public long HistoryBytes { get; private set; }

    /// <summary>Number of history steps dropped because the budget was exceeded.</summary>
    public int DroppedHistory { get; private set; }

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public int UndoCount => _undo.Count;

    public int RedoCount => _redo.Count;

    /// <summary>
    /// True when an image of this size may be opened in the editor: within the loader pixel limit
    /// (<see cref="ImageLoader.DefaultMaxPixels"/>) and an estimate of width·height·4·(1 + <see cref="HistoryReserveImages"/>)
    /// bytes not above <see cref="MaxEditBytes"/>.
    /// </summary>
    public static bool CanEdit(int width, int height, out long estimatedBytes)
    {
        long pixels = (long)Math.Max(0, width) * Math.Max(0, height);
        estimatedBytes = pixels * 4 * (1 + HistoryReserveImages);
        return pixels > 0 && pixels <= ImageLoader.DefaultMaxPixels && estimatedBytes <= MaxEditBytes;
    }

    public void Crop(Int32Rect rect) => ReplaceBase(CropOps.Apply(Base, rect));

    public void Rotate(RotateFlip operation)
    {
        if (operation != RotateFlip.None)
            ReplaceBase(CropOps.Rotate(Base, operation));
    }

    /// <summary>Paints an annotation onto the base image as one undo step.</summary>
    public void Draw(Annotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        ReplaceBase(Annotations.Apply(Base, annotation));
    }

    public void SetAdjustments(AdjustmentSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        if (settings == Settings)
            return;
        Push(new Step(null, Settings));
        Settings = settings;
    }

    public void Undo() => Move(_undo, _redo);

    public void Redo() => Move(_redo, _undo);

    /// <summary>Base image with the current adjustments applied (a new image).</summary>
    public RasterImage Render() => Adjustments.Apply(Base, Settings);

    private void ReplaceBase(RasterImage next)
    {
        Push(new Step(Base, Settings));
        Base = next;
    }

    private void Push(Step step)
    {
        foreach (Step s in _redo)
            HistoryBytes -= s.Bytes;
        _redo.Clear();
        _undo.Add(step);
        HistoryBytes += step.Bytes;
        Trim();
    }

    /// <summary>Restores the newest step of <paramref name="from"/> and records the current state in <paramref name="to"/>.</summary>
    private void Move(List<Step> from, List<Step> to)
    {
        if (from.Count == 0)
            throw new InvalidOperationException("Nothing to restore.");
        Step step = from[^1];
        from.RemoveAt(from.Count - 1);
        var current = new Step(step.Base is null ? null : Base, Settings);
        to.Add(current);
        HistoryBytes += current.Bytes - step.Bytes;
        Base = step.Base ?? Base;
        Settings = step.Settings;
        Trim();
    }

    /// <summary>Drops the oldest undo steps (then the furthest redo steps) until the history fits the budget.</summary>
    private void Trim()
    {
        while (HistoryBytes > HistoryBudgetBytes && (_undo.Count > 0 || _redo.Count > 0))
        {
            List<Step> list = _undo.Count > 0 ? _undo : _redo;
            HistoryBytes -= list[0].Bytes;
            list.RemoveAt(0);
            DroppedHistory++;
        }
    }

    /// <summary>State to restore. <see cref="Base"/> is null for adjustment-only steps (base image unchanged).</summary>
    private sealed record Step(RasterImage? Base, AdjustmentSettings Settings)
    {
        public long Bytes => Base?.ByteSize ?? 0;
    }
}
