using Mucka.Terminal;

namespace Mucka.Terminal.Tests;

public class WheelNotchesTests
{
    /// <summary>The rule the accumulator exists for: a touchpad's drift, many small deltas summing
    /// to less than a notch, never moves the pane.</summary>
    [Fact]
    public void SubNotchDrift_ScrollsNothing()
    {
        var wheel = new WheelNotches();
        var rows = 0;
        for (var i = 0; i < 17; i++)
            rows += wheel.RowsFor(7);      // 119 in all
        Assert.Equal(0, rows);
    }

    [Fact]
    public void OneMouseNotch_ScrollsRowsPerNotch()
    {
        var wheel = new WheelNotches();
        Assert.Equal(WheelNotches.RowsPerNotch, wheel.RowsFor(WheelNotches.NotchDelta));
        Assert.Equal(-WheelNotches.RowsPerNotch, wheel.RowsFor(-WheelNotches.NotchDelta));
    }

    /// <summary>The remainder carries: drift that completes a notch scrolls exactly once, at the
    /// delta that completes it.</summary>
    [Fact]
    public void Remainder_CarriesToTheNextDelta()
    {
        var wheel = new WheelNotches();
        Assert.Equal(0, wheel.RowsFor(100));
        Assert.Equal(WheelNotches.RowsPerNotch, wheel.RowsFor(30));   // 130: one notch, 10 left
        Assert.Equal(0, wheel.RowsFor(100));                          // 110
        Assert.Equal(WheelNotches.RowsPerNotch, wheel.RowsFor(10));   // 120
    }

    [Fact]
    public void SeveralNotchesInOneDelta_ScrollTogether()
    {
        var wheel = new WheelNotches();
        Assert.Equal(3 * WheelNotches.RowsPerNotch, wheel.RowsFor(3 * WheelNotches.NotchDelta));
    }

    /// <summary>Reversing direction mid-notch cancels the drift rather than banking it.</summary>
    [Fact]
    public void OppositeDrift_Cancels()
    {
        var wheel = new WheelNotches();
        Assert.Equal(0, wheel.RowsFor(100));
        Assert.Equal(0, wheel.RowsFor(-100));
        Assert.Equal(0, wheel.RowsFor(100));
    }
}
