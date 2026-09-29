using Mucka.Terminal;

namespace Mucka.Terminal.Tests;

/// <summary>
/// <see cref="TerminalColumns"/>: the width the server is told. The case that mattered is auto (a
/// maximum of 0): a phone login told the server 20 columns at connect, because 0 was clamped rather
/// than measured, and 44 only once the game page had laid out.
/// </summary>
public class TerminalColumnsTests
{
    [Fact]
    public void Auto_IsEveryColumnThatFits()
        => Assert.Equal(44, TerminalColumns.Effective(0, 44));

    [Fact]
    public void Auto_WithNoWidthYet_IsTheUnmeasuredDefault_NotTheMinimum()
        => Assert.Equal(TerminalColumns.Unmeasured, TerminalColumns.Effective(0, 0));

    [Theory]
    [InlineData(80, 44, 44)]    // a maximum wider than the screen is cut to what fits
    [InlineData(40, 44, 40)]    // a narrower one is kept
    [InlineData(80, 0, 80)]     // no width yet: the maximum stands
    public void AMaximum_IsNeverMoreThanFits(int max, int displayable, int expected)
        => Assert.Equal(expected, TerminalColumns.Effective(max, displayable));

    [Theory]
    [InlineData(0, 5, TerminalColumns.Min)]
    [InlineData(0, 500, TerminalColumns.Max)]
    public void TheResult_StaysInsideTheLimits(int max, int displayable, int expected)
        => Assert.Equal(expected, TerminalColumns.Effective(max, displayable));

    [Fact]
    public void Displayable_IsWholeCellsOfTheFont()
    {
        // A 15px cell is 15 * 1200/2048 = 8.789dp wide: 392dp holds 44 of them and not 45.
        Assert.Equal(44, TerminalColumns.Displayable(392, 15));
        Assert.Equal(0, TerminalColumns.Displayable(0, 15));
    }
}
