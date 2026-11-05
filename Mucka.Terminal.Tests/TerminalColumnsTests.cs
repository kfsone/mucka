using Mucka.Terminal;

namespace Mucka.Terminal.Tests;

/// <summary>
/// <see cref="TerminalColumns"/>: the width the pane wraps at. The case that mattered is a width not
/// measured yet: a phone login used 20 columns at connect, because 0 was clamped rather than
/// replaced, and 44 only once the game page had laid out.
/// </summary>
public class TerminalColumnsTests
{
    [Fact]
    public void Effective_IsEveryColumnThatFits()
        => Assert.Equal(44, TerminalColumns.Effective(44));

    [Fact]
    public void WithNoWidthYet_IsTheUnmeasuredDefault_NotTheMinimum()
        => Assert.Equal(TerminalColumns.Unmeasured, TerminalColumns.Effective(0));

    [Theory]
    [InlineData(5, TerminalColumns.Min)]
    [InlineData(500, 500)]
    public void TheResult_IsNeverBelowTheMinimum_AndNeverCappedAbove(int displayable, int expected)
        => Assert.Equal(expected, TerminalColumns.Effective(displayable));

    [Fact]
    public void Displayable_IsWholeCellsOfTheFont()
    {
        // A 15px cell is 15 * 1200/2048 = 8.789dp wide: 392dp holds 44 of them and not 45.
        Assert.Equal(44, TerminalColumns.Displayable(392, 15));
        Assert.Equal(0, TerminalColumns.Displayable(0, 15));
    }

    [Theory]
    [InlineData(406, 80, 8)]     // 80 columns in 406dp: 8px cells (4.69dp) fit, 9px (5.27dp) do not
    [InlineData(2000, 80, 15)]   // room to spare: never larger than the maximum
    [InlineData(100, 80, 6)]     // too narrow for anything: never smaller than the minimum
    [InlineData(0, 80, 15)]      // no width yet: the maximum
    [InlineData(406, 0, 15)]     // no lines yet: the maximum
    public void FittingFontPx_IsTheLargestThatFitsTheColumns(double width, int columns, int expected)
        => Assert.Equal(expected, TerminalColumns.FittingFontPx(width, columns, 15, 6));

    [Fact]
    public void FittingFontPx_TheChosenSizeFits_AndTheNextDoesNot()
    {
        var px = TerminalColumns.FittingFontPx(406, 80, 15, 6);
        Assert.True(TerminalColumns.Displayable(406, px) >= 80);
        Assert.True(TerminalColumns.Displayable(406, px + 1) < 80);
    }
}
