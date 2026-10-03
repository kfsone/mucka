using Mucka.Combat;

namespace Mucka.Util.Tests;

public sealed class StaminaGlowTests
{
    private static readonly StaminaGlow.Bands Plain = StaminaGlow.BandsFor(null);

    [Theory]
    [InlineData(31, 0)]
    [InlineData(30, 1)]
    [InlineData(29, 1)]
    [InlineData(28, 2)]
    [InlineData(27, 2)]
    [InlineData(26, 3)]
    [InlineData(25, 3)]
    [InlineData(24, 4)]
    [InlineData(23, 4)]
    [InlineData(22, 5)]
    [InlineData(21, 5)]
    [InlineData(20, StaminaGlow.Red)]
    [InlineData(5, StaminaGlow.Red)]
    public void Level_RampsInStepsOfTwoFromThirtyToRedAtTwenty(int stamina, int level)
        => Assert.Equal(level, StaminaGlow.Level(stamina, Plain));

    [Fact]
    public void Level_IsZeroWithNoStaminaReading()
        => Assert.Equal(0, StaminaGlow.Level(null, Plain));

    /// <summary>Out of a fight, or with no measured blow, the bands are the plain 20 and 30.</summary>
    [Fact]
    public void Bands_WithNoBlow_AreTwentyAndThirty()
        => Assert.Equal(new StaminaGlow.Bands(20, 30), Plain);

    /// <summary>One of the largest blows from dead is red; two of them is amber - each only when it is
    /// higher than the plain band.</summary>
    [Theory]
    [InlineData(12.0, 20, 30)]
    [InlineData(14.0, 20, 30)]
    [InlineData(15.0, 20, 31)]
    [InlineData(19.0, 20, 39)]
    [InlineData(19.5, 21, 41)]
    [InlineData(39.0, 40, 79)]
    public void Bands_MoveUpWithTheLargestLiveBlow(double blow, int redAt, int amberFrom)
        => Assert.Equal(new StaminaGlow.Bands(redAt, amberFrom), StaminaGlow.BandsFor(blow));

    /// <summary>The five steps share out whatever span the bands leave: a 39 hitter's ramp runs 79 to
    /// 41, the top step at the top of it and the last step just above the red.</summary>
    [Fact]
    public void Level_SharesTheStepsAcrossAWideSpan()
    {
        var bands = StaminaGlow.BandsFor(39);
        Assert.Equal(0, StaminaGlow.Level(80, bands));
        Assert.Equal(1, StaminaGlow.Level(79, bands));
        Assert.Equal(StaminaGlow.Steps, StaminaGlow.Level(41, bands));
        Assert.Equal(StaminaGlow.Red, StaminaGlow.Level(40, bands));
    }

    [Fact]
    public void WholePanel_StartsMidRamp_AtTwentyFiveOrTwentySix()
    {
        Assert.True(StaminaGlow.Level(27, Plain) < StaminaGlow.WholePanelFromLevel);
        Assert.Equal(StaminaGlow.WholePanelFromLevel, StaminaGlow.Level(26, Plain));
        Assert.Equal(StaminaGlow.WholePanelFromLevel, StaminaGlow.Level(25, Plain));
    }

    /// <summary>The red is the whole-panel glow exactly as it was before the ramp existed.</summary>
    [Fact]
    public void Look_RedIsUnchanged()
    {
        var red = StaminaGlow.LookFor(StaminaGlow.Red)!.Value;
        Assert.Equal((0xC5, 0x0F, 0x1F), (red.R, red.G, red.B));
        Assert.Equal(0.5f, red.Peak);
        Assert.Equal(0.125f, red.Trough);
        Assert.Equal(Blink.PulsePeriodMilliseconds, red.PeriodMilliseconds);
    }

    /// <summary>Every step up the ramp is at least as bright, as fast and as red as the one below it,
    /// on the panel, on the trim and on the combat rules, so the alarm only ever grows as stamina
    /// falls.</summary>
    [Fact]
    public void Look_GrowsMonotonicallyTowardsTheRed()
    {
        for (var level = 1; level < StaminaGlow.Red; level++)
        {
            var lower = StaminaGlow.LookFor(level)!.Value;
            var upper = StaminaGlow.LookFor(level + 1)!.Value;
            Assert.True(upper.Peak > lower.Peak, $"peak at {level + 1}");
            Assert.True(upper.Trough > lower.Trough, $"trough at {level + 1}");
            Assert.True(upper.TrimPeak > lower.TrimPeak, $"trim peak at {level + 1}");
            Assert.True(upper.PeriodMilliseconds < lower.PeriodMilliseconds, $"period at {level + 1}");
            Assert.True(upper.G < lower.G, $"green falls (amber to red) at {level + 1}");
            Assert.True(StaminaGlow.EdgeColorFor(level + 1)!.Value.G < StaminaGlow.EdgeColorFor(level)!.Value.G,
                $"edge green falls at {level + 1}");
        }
    }

    [Fact]
    public void EdgeColor_RunsFromTheAmberToTheBrightRed()
    {
        Assert.Equal(StaminaGlow.Amber, StaminaGlow.EdgeColorFor(1));
        Assert.Equal(StaminaGlow.EdgeRed, StaminaGlow.EdgeColorFor(StaminaGlow.Red));
    }

    [Fact]
    public void NothingIsLitAtLevelZero()
    {
        Assert.Null(StaminaGlow.LookFor(0));
        Assert.Null(StaminaGlow.EdgeColorFor(0));
    }
}
