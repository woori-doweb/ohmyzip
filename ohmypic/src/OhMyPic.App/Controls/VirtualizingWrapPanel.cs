using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace OhMyPic.App.Controls;

/// <summary>
/// Wrap panel for uniformly sized tiles that only realizes the containers in (and one row around) the viewport.
/// Scrolls vertically through <see cref="IScrollInfo"/>; the hosting ScrollViewer must have CanContentScroll=True.
/// </summary>
public sealed class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(150.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(150.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    private Size _extent;
    private Size _viewport;
    private double _offsetY;

    private double TileWidth => ItemWidth > 1 ? ItemWidth : 1;
    private double TileHeight => ItemHeight > 1 ? ItemHeight : 1;

    private int Columns(double width) => Math.Max(1, (int)Math.Floor(width / TileWidth));

    private int ItemCount => ItemsControl.GetItemsOwner(this) is { } owner ? owner.Items.Count : 0;

    protected override Size MeasureOverride(Size availableSize)
    {
        // Touch InternalChildren first: it makes sure the generator is hooked up.
        var children = InternalChildren;
        var generator = ItemContainerGenerator;
        int count = ItemCount;
        double width = double.IsInfinity(availableSize.Width) ? TileWidth : availableSize.Width;
        double height = double.IsInfinity(availableSize.Height) ? TileHeight * 4 : availableSize.Height;
        int cols = Columns(width);
        int rows = (count + cols - 1) / cols;
        UpdateScrollInfo(new Size(width, height), new Size(width, rows * TileHeight));

        int firstRow = Math.Max(0, (int)Math.Floor(_offsetY / TileHeight) - 1);
        int lastRow = (int)Math.Ceiling((_offsetY + height) / TileHeight);
        int first = firstRow * cols;
        int last = Math.Min(count - 1, (lastRow + 1) * cols - 1);

        if (count > 0 && first <= last && generator is not null)
        {
            var start = generator.GeneratorPositionFromIndex(first);
            int childIndex = start.Offset == 0 ? start.Index : start.Index + 1;
            using (generator.StartAt(start, GeneratorDirection.Forward, true))
            {
                for (int i = first; i <= last; i++, childIndex++)
                {
                    if (generator.GenerateNext(out bool newlyRealized) is not UIElement child) break;
                    if (newlyRealized)
                    {
                        if (childIndex >= children.Count) AddInternalChild(child);
                        else InsertInternalChild(childIndex, child);
                        generator.PrepareItemContainer(child);
                    }
                    child.Measure(new Size(TileWidth, TileHeight));
                }
            }
        }
        CleanUp(first, last);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int cols = Columns(finalSize.Width);
        var generator = ItemContainerGenerator;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            int index = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (index < 0) continue;
            int row = index / cols, col = index % cols;
            InternalChildren[i].Arrange(new Rect(col * TileWidth, row * TileHeight - _offsetY, TileWidth, TileHeight));
        }
        return finalSize;
    }

    /// <summary>Removes containers outside [first, last], except the one holding keyboard focus.</summary>
    private void CleanUp(int first, int last)
    {
        var children = InternalChildren;
        var generator = ItemContainerGenerator;
        if (generator is null) return;
        for (int i = children.Count - 1; i >= 0; i--)
        {
            var position = new GeneratorPosition(i, 0);
            int index = generator.IndexFromGeneratorPosition(position);
            if ((index < first || index > last) && !children[i].IsKeyboardFocusWithin)
            {
                generator.Remove(position, 1);
                RemoveInternalChildRange(i, 1);
            }
        }
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
            case NotifyCollectionChangedAction.Move:
                RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;
            case NotifyCollectionChangedAction.Reset:
                _offsetY = 0;
                ScrollOwner?.InvalidateScrollInfo();
                break;
        }
    }

    protected override void BringIndexIntoView(int index) => ScrollToIndex(index);

    private void ScrollToIndex(int index)
    {
        if (index < 0) return;
        int cols = Columns(_viewport.Width > 0 ? _viewport.Width : ActualWidth);
        double top = index / cols * TileHeight;
        if (top < _offsetY) SetVerticalOffset(top);
        else if (top + TileHeight > _offsetY + _viewport.Height) SetVerticalOffset(top + TileHeight - _viewport.Height);
    }

    private void UpdateScrollInfo(Size viewport, Size extent)
    {
        bool changed = viewport != _viewport || extent != _extent;
        _viewport = viewport;
        _extent = extent;
        double max = Math.Max(0, _extent.Height - _viewport.Height);
        if (_offsetY > max)
        {
            _offsetY = max;
            changed = true;
        }
        if (changed) ScrollOwner?.InvalidateScrollInfo();
    }

    // IScrollInfo (vertical only)

    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => 0;
    public double VerticalOffset => _offsetY;
    public ScrollViewer? ScrollOwner { get; set; }

    private double LineStep => Math.Max(16, TileHeight / 4);
    private double WheelStep => Math.Max(48, TileHeight / 2);

    public void LineUp() => SetVerticalOffset(_offsetY - LineStep);
    public void LineDown() => SetVerticalOffset(_offsetY + LineStep);
    public void PageUp() => SetVerticalOffset(_offsetY - _viewport.Height);
    public void PageDown() => SetVerticalOffset(_offsetY + _viewport.Height);
    public void MouseWheelUp() => SetVerticalOffset(_offsetY - WheelStep);
    public void MouseWheelDown() => SetVerticalOffset(_offsetY + WheelStep);
    public void LineLeft() { }
    public void LineRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void SetHorizontalOffset(double offset) { }

    public void SetVerticalOffset(double offset)
    {
        double max = Math.Max(0, _extent.Height - _viewport.Height);
        offset = Math.Max(0, Math.Min(offset, max));
        if (offset == _offsetY) return;
        _offsetY = offset;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        var generator = ItemContainerGenerator;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            if (child == visual || child.IsAncestorOf(visual))
            {
                ScrollToIndex(generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0)));
                break;
            }
        }
        return rectangle;
    }
}
