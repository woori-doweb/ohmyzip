namespace OhMyPic.Core;

/// <summary>Order of images in a slideshow (and next/previous in the viewer).</summary>
public sealed class SlideshowSequence
{
    public const int MinIntervalSeconds = 1;
    public const int MaxIntervalSeconds = 60;
    public const int DefaultIntervalSeconds = 3;

    private readonly IReadOnlyList<string> _items;

    /// <exception cref="ArgumentException">The list is empty.</exception>
    public SlideshowSequence(IReadOnlyList<string> items, int startIndex, bool loop)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
            throw new ArgumentException("A slideshow needs at least one item.", nameof(items));
        _items = items;
        Index = Math.Clamp(startIndex, 0, items.Count - 1);
        Loop = loop;
    }

    public bool Loop { get; set; }

    public int Index { get; private set; }

    public int Count => _items.Count;

    public string Current => _items[Index];

    /// <summary>Advances; at the last item wraps to the first when <see cref="Loop"/> is set, otherwise returns false.</summary>
    public bool MoveNext()
    {
        if (Index < _items.Count - 1)
        {
            Index++;
            return true;
        }
        if (!Loop || _items.Count == 1)
            return false;
        Index = 0;
        return true;
    }

    /// <summary>Goes back; at the first item wraps to the last when <see cref="Loop"/> is set, otherwise returns false.</summary>
    public bool MovePrevious()
    {
        if (Index > 0)
        {
            Index--;
            return true;
        }
        if (!Loop || _items.Count == 1)
            return false;
        Index = _items.Count - 1;
        return true;
    }

    /// <summary>Clamps a slideshow interval to 1..60 seconds.</summary>
    public static int IntervalSeconds(int seconds) => Math.Clamp(seconds, MinIntervalSeconds, MaxIntervalSeconds);
}
