using MudSharp.Combat;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// Covers the flee pill's price: the anchors it interpolates, the cliff at 19/20 that is the one
/// observed feature of the curve, the free band, and the owner's display format.
///
/// <para>These tests pin the ARITHMETIC, not the mechanic. The curve rests on four anchors of which
/// exactly one is measured (-2,079 at 19/105 stamina), so a test failing here means the code changed;
/// it does not mean the game was re-measured. See FleeCostEstimate's own remarks.</para>
/// </summary>
public sealed class FleeCostEstimateTests
{
    /// <summary>The owner's character, and the score the one real measurement was taken at.</summary>
    private const int MeasuredScore = 46_416;

    [Fact]
    public void ReproducesTheOneMeasurement()
    {
        // Score 46,416 -> 44,337 at 19/105 stamina: exactly -2,079, the only flee in the corpus. 4.48%
        // of score. If this drifts, the curve has stopped agreeing with the sole thing it is anchored to.
        var points = FleeCostEstimate.Points(stamina: 19, score: MeasuredScore);
        Assert.NotNull(points);
        Assert.InRange(points!.Value, 2_070, 2_090);
    }

    [Fact]
    public void MaximumIsTenPercentAndFlatFromTheSurvivalThresholdUpwards()
    {
        var at20 = FleeCostEstimate.Points(20, MeasuredScore);
        var atFull = FleeCostEstimate.Points(105, MeasuredScore);
        Assert.Equal(4_642, at20);
        Assert.Equal(at20, atFull);
    }

    [Fact]
    public void TheCliffAtTheSurvivalThresholdIsPreserved()
    {
        // "Fleeing at 20 costs more than twice what fleeing at 19 costs" - the perverse incentive that
        // makes 20 the threshold that matters, since it is also where one blow can kill. Smoothing this
        // would erase the only feature of the curve anyone has observed.
        var at20 = FleeCostEstimate.Points(20, MeasuredScore)!.Value;
        var at19 = FleeCostEstimate.Points(19, MeasuredScore)!.Value;
        Assert.True(at20 > at19 * 2, $"expected a cliff, got {at19} -> {at20}");
    }

    [Fact]
    public void FallsAwayBelowTheSurvivalThreshold()
    {
        // Monotonic down the paying band. Not a claim that the real curve is linear - only that this
        // implementation does not wobble between its anchors.
        var previous = int.MaxValue;
        for (var sta = 19; sta >= 7; sta--)
        {
            var points = FleeCostEstimate.Points(sta, MeasuredScore)!.Value;
            Assert.True(points <= previous, $"cost rose as stamina fell at {sta}: {previous} -> {points}");
            previous = points;
        }
    }

    [Fact]
    public void AroundSevenStaminaTheLossIsSmall()
    {
        // The owner's second anchor: 500-600 points on a ~46,000 score.
        var points = FleeCostEstimate.Points(7, MeasuredScore);
        Assert.InRange(points!.Value, 450, 650);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(3)]
    [InlineData(1)]
    public void FreeBandReturnsNullNotZero(int stamina)
    {
        // Null, so the pill draws no parenthetical. A rendered "(-0)" would be a claim, and it would be
        // indistinguishable from "we do not know your score" - which is the reading that gets a
        // character killed.
        Assert.Null(FleeCostEstimate.Points(stamina, MeasuredScore));
    }

    [Fact]
    public void UnknownStaminaOrScoreIsNull()
    {
        Assert.Null(FleeCostEstimate.Points(null, MeasuredScore));
        Assert.Null(FleeCostEstimate.Points(19, null));
        Assert.Null(FleeCostEstimate.Points(19, 0));
    }

    [Fact]
    public void APayingFleeNeverRoundsToNothing()
    {
        // A brand-new character inside the paying band: 1.2% of 20 points rounds to zero, and zero here
        // would render as the free band - a different fact about the game.
        var points = FleeCostEstimate.Points(8, 20);
        Assert.NotNull(points);
        Assert.True(points!.Value >= 1);
    }

    // ── The owner's display format ──────────────────────────────────────────────

    [Theory]
    [InlineData(1, "1")]
    [InlineData(999, "999")]
    [InlineData(1000, "1.0k")]
    [InlineData(2079, "2.1k")]
    [InlineData(4999, "5.0k")]
    [InlineData(5000, "5k")]
    [InlineData(12345, "12k")]
    public void FormatMatchesTheSpecifiedShape(int points, string expected)
        => Assert.Equal(expected, FleeCostEstimate.Format(points));

    [Fact]
    public void FormatIsCultureInvariant()
    {
        // The decimal separator is a dot on a panel drawn in a fixed-width slot; a comma from a European
        // locale would be both wrong here and a different width.
        var previous = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture =
                new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("2.1k", FleeCostEstimate.Format(2079));
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = previous;
        }
    }
}
