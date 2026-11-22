using Mucka.Terminal;

namespace Mucka.Terminal.Tests;

public class ScrollbarThumbTests
{
    private const float Track = 600f;
    private const int Viewport = 30;
    private const int Content = 3000;
    private const int MaxOffset = Content - Viewport;

    private static float ThumbHeight => ScrollbarThumb.Height(Track, Viewport, Content, minimum: 12f);

    [Fact]
    public void TheThumbIsTheViewportsShareOfTheTrack_AndNeverBelowTheMinimum()
    {
        Assert.Equal(12f, ThumbHeight);
        Assert.Equal(300f, ScrollbarThumb.Height(Track, 30, 60, minimum: 12f));
    }

    [Fact]
    public void BottomIsTheLiveEdge_TopIsTheOldestRow()
    {
        Assert.Equal(Track - ThumbHeight, ScrollbarThumb.Top(Track, ThumbHeight, 0, MaxOffset));
        Assert.Equal(0f, ScrollbarThumb.Top(Track, ThumbHeight, MaxOffset, MaxOffset));
    }

    /// <summary>The thumb a press takes hold of, dragged to where it is drawn, must mean the offset it was
    /// drawn for, or the view jumps on the first move.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1234)]
    [InlineData(MaxOffset - 1)]
    [InlineData(MaxOffset)]
    public void ADrawnThumb_MeansTheOffsetItWasDrawnFor(int offset)
    {
        var top = ScrollbarThumb.Top(Track, ThumbHeight, offset, MaxOffset);
        Assert.Equal(offset, ScrollbarThumb.Offset(Track, ThumbHeight, top, MaxOffset));
    }

    [Fact]
    public void AThumbDraggedPastEitherEnd_StopsAtTheEnd()
    {
        Assert.Equal(MaxOffset, ScrollbarThumb.Offset(Track, ThumbHeight, -50f, MaxOffset));
        Assert.Equal(0, ScrollbarThumb.Offset(Track, ThumbHeight, Track + 50f, MaxOffset));
    }

    [Fact]
    public void WithNothingToScroll_TheOffsetIsZero()
    {
        Assert.Equal(0, ScrollbarThumb.Offset(Track, ThumbHeight, 100f, maxOffset: 0));
        Assert.Equal(0, ScrollbarThumb.Offset(Track, Track, 0f, MaxOffset));
    }
}
