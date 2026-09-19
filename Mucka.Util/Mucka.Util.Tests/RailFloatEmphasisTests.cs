using Mucka.Combat;

namespace Mucka.Util.Tests;

/// <summary>
/// The damage float's emphasis ladder - how much bigger a bigger number is drawn. Pinned here
/// because it is the only part of the float feature a test can reach at all: the placement, the
/// animation and the elements themselves are WinUI and unreachable from any suite.
/// </summary>
public class RailFloatEmphasisTests
{
    [Theory]
    // Below and at the first threshold, which is strictly greater-than: a 5 is not emphasised.
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(5, 0)]
    // First step.
    [InlineData(6, 1)]
    [InlineData(9, 1)]
    // Second, at ten inclusive.
    [InlineData(10, 2)]
    [InlineData(19, 2)]
    // Third, at twenty inclusive.
    [InlineData(20, 3)]
    [InlineData(29, 3)]
    // Fourth, at thirty inclusive, and nothing above it - the ladder has a top.
    [InlineData(30, 4)]
    [InlineData(120, 4)]
    public void StepsFor_ClimbsOncePerThresholdAndStops(int magnitude, int expected)
        => Assert.Equal(expected, RailFloatEmphasis.StepsFor(magnitude));

    [Fact]
    public void FontSizeFor_IsTheBaseSizePlusItsSteps()
    {
        Assert.Equal(RailFloatEmphasis.BaseFontSize, RailFloatEmphasis.FontSizeFor(5));
        Assert.Equal(RailFloatEmphasis.BaseFontSize + 1, RailFloatEmphasis.FontSizeFor(6));
        Assert.Equal(RailFloatEmphasis.BaseFontSize + 4, RailFloatEmphasis.FontSizeFor(30));
    }

    [Fact]
    public void IsHeavy_OnlyAtTheTopOfTheLadder()
    {
        Assert.False(RailFloatEmphasis.IsHeavy(RailFloatEmphasis.HeavyThreshold - 1));
        Assert.True(RailFloatEmphasis.IsHeavy(RailFloatEmphasis.HeavyThreshold));
        Assert.True(RailFloatEmphasis.IsHeavy(RailFloatEmphasis.HeavyThreshold + 50));
    }

    /// <summary>
    /// A negative magnitude earns nothing. It is not a case the rail produces - incoming damage is
    /// carried as a positive "how much was taken" and the text adds the sign - but a ladder that
    /// climbed on one would be reporting a big blow for a number that went the other way.
    /// </summary>
    [Fact]
    public void NegativeMagnitude_EarnsNoEmphasis()
    {
        Assert.Equal(0, RailFloatEmphasis.StepsFor(-14));
        Assert.False(RailFloatEmphasis.IsHeavy(-40));
    }
}
