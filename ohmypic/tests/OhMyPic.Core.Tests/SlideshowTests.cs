namespace OhMyPic.Core.Tests;

public sealed class SlideshowTests
{
    private static readonly string[] Items = ["fixture-01.png", "fixture-02.png", "fixture-03.png"];

    [Fact]
    public void OP_E18_Next_Previous_With_Loop()
    {
        var s = new SlideshowSequence(Items, 0, loop: true);
        Assert.Equal("fixture-01.png", s.Current);
        Assert.True(s.MoveNext());
        Assert.Equal("fixture-02.png", s.Current);
        Assert.True(s.MoveNext());
        Assert.Equal("fixture-03.png", s.Current);
        Assert.True(s.MoveNext());
        Assert.Equal(0, s.Index);
        Assert.True(s.MovePrevious());
        Assert.Equal("fixture-03.png", s.Current);
        Assert.True(s.MovePrevious());
        Assert.Equal(1, s.Index);
    }

    [Fact]
    public void OP_E18_Stops_At_Ends_Without_Loop()
    {
        var s = new SlideshowSequence(Items, 2, loop: false);
        Assert.False(s.MoveNext());
        Assert.Equal(2, s.Index);
        Assert.True(s.MovePrevious());
        Assert.True(s.MovePrevious());
        Assert.False(s.MovePrevious());
        Assert.Equal(0, s.Index);

        // Toggling the setting at runtime takes effect immediately.
        s.Loop = true;
        Assert.True(s.MovePrevious());
        Assert.Equal(2, s.Index);
    }

    [Fact]
    public void OP_E18_Start_Index_Clamped_Single_Item_And_Empty()
    {
        Assert.Equal(2, new SlideshowSequence(Items, 99, true).Index);
        Assert.Equal(0, new SlideshowSequence(Items, -5, true).Index);
        var single = new SlideshowSequence(["only.png"], 0, loop: true);
        Assert.False(single.MoveNext());
        Assert.False(single.MovePrevious());
        Assert.Throws<ArgumentException>(() => new SlideshowSequence([], 0, true));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-10, 1)]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(60, 60)]
    [InlineData(61, 60)]
    [InlineData(int.MaxValue, 60)]
    public void OP_E18_Interval_Range(int requested, int expected)
    {
        Assert.Equal(expected, SlideshowSequence.IntervalSeconds(requested));
        Assert.Equal(3, SlideshowSequence.DefaultIntervalSeconds);
    }
}
