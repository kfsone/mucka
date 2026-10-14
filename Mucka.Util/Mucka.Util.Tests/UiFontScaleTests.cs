using Mucka.Ui;

namespace Mucka.Util.Tests;

public class UiFontScaleTests
{
    [Theory]
    [InlineData(8.0)]
    [InlineData(11.0)]
    [InlineData(13.5)]
    [InlineData(28.0)]
    public void Normal_ReturnsTheAuthoredSizeUntouched(double size)
    {
        Assert.Equal(size, UiFontScale.Apply(size, UiFontScale.Normal));
        Assert.Equal(size, UiFontScale.Apply(size, UiFontScale.Normal, tight: true));
    }

    [Fact]
    public void Steps_AreStrictlyIncreasing()
    {
        Assert.True(UiFontScale.ScaleFor(UiFontScale.Normal) < UiFontScale.ScaleFor(UiFontScale.Large));
        Assert.True(UiFontScale.ScaleFor(UiFontScale.Large) < UiFontScale.ScaleFor(UiFontScale.Larger));
    }

    [Theory]
    [InlineData(11.0, UiFontScale.Large, 12.5)]
    [InlineData(11.0, UiFontScale.Larger, 14.5)]
    [InlineData(14.0, UiFontScale.Large, 16.0)]
    [InlineData(14.0, UiFontScale.Larger, 18.0)]
    public void Scaled_RoundsToTheNearestHalfPoint(double size, int step, double expected)
        => Assert.Equal(expected, UiFontScale.Apply(size, step));

    [Fact]
    public void Tight_NeverExceedsTheTightCap()
    {
        Assert.Equal(UiFontScale.Apply(13.0, UiFontScale.Large), UiFontScale.Apply(13.0, UiFontScale.Larger, tight: true));
        Assert.True(UiFontScale.Apply(13.0, UiFontScale.Larger) > UiFontScale.Apply(13.0, UiFontScale.Larger, tight: true));
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(2, 2)]
    [InlineData(9, 2)]
    public void Clamp_KeepsTheStepInRange(int step, int expected)
        => Assert.Equal(expected, UiFontScale.Clamp(step));

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("junk", 0)]
    [InlineData("1", 1)]
    [InlineData("2", 2)]
    [InlineData("7", 2)]
    [InlineData("-1", 0)]
    public void Parse_FallsBackToNormal(string? text, int expected)
        => Assert.Equal(expected, UiFontScale.Parse(text));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Format_RoundTripsThroughParse(int step)
        => Assert.Equal(step, UiFontScale.Parse(UiFontScale.Format(step)));
}
