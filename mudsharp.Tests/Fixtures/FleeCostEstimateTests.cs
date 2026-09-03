using Mucka.ViewModels;
using MudSharp.Combat;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// Covers the flee pill's price: the fraction-of-maximum bands, their boundaries, the free band, the
/// unpriced region above anything measured, and the owner's display format.
///
/// <para>These tests pin the ARITHMETIC, not the mechanic. The bands come from 40 flee events with
/// score captured either side (with a second pass over 38 of them finding a modal 2.25% of score on
/// 36), so a failure here means the code changed; it does not mean the game was re-measured.</para>
///
/// <para>This file previously tested an ABSOLUTE-stamina model with a 6.5 free boundary, a 10% flat
/// maximum above 20 stamina and a "cliff" between 19 and 20. That model is deleted: the boundary is a
/// fraction of maximum stamina, 6.5 was simply 6% of the one 105-maximum persona it was ever observed
/// on, and the 10% maximum was never measured at all. The one genuine measurement it contained
/// survives and is still asserted below.</para>
/// </summary>
public sealed class FleeCostEstimateTests
{
    /// <summary>The score the one exact before/after measurement was taken at.</summary>
    private const int MeasuredScore = 46_416;

    /// <summary>The persona every absolute-stamina figure in the old corpus was recorded on. Its
    /// maximum is what makes those figures convertible to fractions at all.</summary>
    private const int MeasuredMax = 105;

    [Fact]
    public void ReproducesTheOneExactMeasurement()
    {
        // Score 46,416 -> 44,337 at 19/105 stamina: exactly -2,079, the only flee with both ends
        // captured to the point. 19/105 is 18.1% of maximum, which is the dear band, which charges
        // 4.5%. If this drifts, the bands have stopped agreeing with the one thing measured exactly.
        var points = FleeCostEstimate.Points(stamina: 19, staminaMax: MeasuredMax, score: MeasuredScore);
        Assert.NotNull(points);
        Assert.InRange(points!.Value, 2_070, 2_095);
    }

    [Fact]
    public void TheOneExactMeasurementFallsInsideTheDearBandsObservedSpread()
    {
        // The band is 4.1-5.0% of score, not the 4.5% mode alone, and the reported spread has to
        // contain the measurement or the band is mis-stated.
        var range = FleeCostEstimate.PointsRange(19, MeasuredMax, MeasuredScore);
        Assert.NotNull(range);
        Assert.InRange(2_079, range!.Value.Low, range.Value.High);
    }

    // ── The bands, at the fractions of maximum they were measured at ─────────────

    [Theory]
    // Free: below 6% of maximum. 6/105 = 5.7%.
    [InlineData(6, 105, FleeCostBand.Free)]
    [InlineData(1, 105, FleeCostBand.Free)]
    [InlineData(1, 30, FleeCostBand.Free)]      // 3.3% on a small character - free, where an absolute
                                                // 6.5 threshold would also have said free by accident
    // Cheap: 6% to 15.2%. 7/105 = 6.7%, the lowest paying observation.
    [InlineData(7, 105, FleeCostBand.Cheap)]
    [InlineData(15, 105, FleeCostBand.Cheap)]   // 14.3%
    // Dear: above 15.2%, up to 18.1%.
    [InlineData(17, 105, FleeCostBand.Dear)]    // 16.2%
    [InlineData(19, 105, FleeCostBand.Dear)]    // 18.1%, the top of the evidence
    // Above anything measured.
    [InlineData(20, 105, FleeCostBand.AboveEvidence)]   // 19.0%
    [InlineData(105, 105, FleeCostBand.AboveEvidence)]
    public void BandsFollowTheFractionOfMaximum(int stamina, int max, FleeCostBand expected)
        => Assert.Equal(expected, FleeCostEstimate.Band(stamina, max));

    [Fact]
    public void TheSameAbsoluteStaminaBandsDifferentlyOnDifferentCharacters()
    {
        // The whole correction, in one assertion. 6 stamina is free on the 105-maximum persona the old
        // model was tuned to (5.7%) and squarely in the paying band on a 30-maximum novice (20%) - the
        // old absolute threshold called both of them free.
        Assert.Equal(FleeCostBand.Free, FleeCostEstimate.Band(6, 105));
        Assert.NotEqual(FleeCostBand.Free, FleeCostEstimate.Band(6, 30));
    }

    [Fact]
    public void BoundariesArePlacedSoAnUnobservedGapIsNeverPricedTooCheaply()
    {
        // Free is stated at 6% and the cheapest paying observation is at 6.7%; the gap between must be
        // priced as PAYING, because telling a player a flee is free when it might not be is the error
        // that costs points unexpectedly. 0.063 of 1000 = 63.
        Assert.Equal(FleeCostBand.Cheap, FleeCostEstimate.Band(63, 1000));

        // Cheap ends at 15.2% and dear begins at 16%; the gap must be priced as DEAR, same reason.
        Assert.Equal(FleeCostBand.Dear, FleeCostEstimate.Band(155, 1000));
    }

    [Fact]
    public void TheDearBandIsExactlyTwiceTheCheapOne()
    {
        // 36 of 38 events sit at a modal 2.25% of score and the remainder at exactly 2x. The doubling
        // is the finding; a drift here means someone tuned one band without the other.
        var cheap = FleeCostEstimate.Points(7, 105, 100_000)!.Value;
        var dear = FleeCostEstimate.Points(19, 105, 100_000)!.Value;
        Assert.Equal(cheap * 2, dear);
    }

    [Fact]
    public void TheChargeIsAConstantFractionOfScoreAcrossTheMeasuredRange()
    {
        // Constant from a score of 7,873 to 88,308 across two personas and levels 5-9. Not a ladder,
        // not level-scaled: the same percentage.
        foreach (var score in new[] { 7_873, 20_000, 46_416, 88_308 })
        {
            var points = FleeCostEstimate.Points(10, 105, score)!.Value;
            var fraction = points / (double)score;
            Assert.InRange(fraction, 0.0220, 0.0230);
        }
    }

    // ── The three no-price cases, which must stay distinguishable ────────────────

    [Fact]
    public void FreeBandReturnsNullNotZero()
    {
        // Null, so the pill draws no parenthetical. A rendered "(-0)" would be a claim, and it would be
        // indistinguishable from "we do not know your score".
        Assert.Null(FleeCostEstimate.Points(3, 105, MeasuredScore));
        Assert.Equal(FleeCostBand.Free, FleeCostEstimate.Band(3, 105));
    }

    [Fact]
    public void AboveTheEvidenceReturnsNullRatherThanAnExtrapolation()
    {
        // No flee has ever been measured above 18.1% of maximum. The old model claimed a flat 10%
        // ceiling up here and said in its own comment that nothing measured it. Showing nothing is the
        // honest answer, and it must be distinguishable from the free band.
        Assert.Null(FleeCostEstimate.Points(105, 105, MeasuredScore));
        Assert.Equal(FleeCostBand.AboveEvidence, FleeCostEstimate.Band(105, 105));
        Assert.Null(FleeCostEstimate.PointsRange(105, 105, MeasuredScore));
    }

    [Fact]
    public void MissingInputsAreUnknownRatherThanFree()
    {
        Assert.Equal(FleeCostBand.Unknown, FleeCostEstimate.Band(null, 105));
        Assert.Equal(FleeCostBand.Unknown, FleeCostEstimate.Band(19, null));
        // A maximum of zero cannot produce a fraction, and dividing by it would produce a band from a
        // NaN comparison rather than an error.
        Assert.Equal(FleeCostBand.Unknown, FleeCostEstimate.Band(19, 0));

        Assert.Null(FleeCostEstimate.Points(null, 105, MeasuredScore));
        Assert.Null(FleeCostEstimate.Points(19, null, MeasuredScore));
        Assert.Null(FleeCostEstimate.Points(19, 105, null));
        Assert.Null(FleeCostEstimate.Points(19, 105, 0));
    }

    [Fact]
    public void APayingFleeNeverRoundsToNothing()
    {
        // A brand-new character inside the paying band: 2.25% of 20 points rounds to zero, and zero
        // here would render as the free band - a different fact about the game.
        var points = FleeCostEstimate.Points(8, 105, 20);
        Assert.NotNull(points);
        Assert.True(points!.Value >= 1);

        var range = FleeCostEstimate.PointsRange(8, 105, 20);
        Assert.NotNull(range);
        Assert.True(range!.Value.Low >= 1);
    }

    [Fact]
    public void ThePriceNeverRisesAsStaminaFalls()
    {
        // Monotonic down the whole priced range. Not a claim that the real curve is a staircase - only
        // that this implementation does not wobble between its bands.
        var previous = int.MaxValue;
        for (var sta = 19; sta >= 7; sta--)
        {
            var points = FleeCostEstimate.Points(sta, 105, MeasuredScore)!.Value;
            Assert.True(points <= previous, $"cost rose as stamina fell at {sta}: {previous} -> {points}");
            previous = points;
        }
    }

    // ── Ignorance has to have a shape ────────────────────────────────────────────

    [Fact]
    public void FreeAndAboveEvidenceDoNotRenderTheSame()
    {
        // The failure this exists to prevent. Points() returns null for BOTH, so a caller branching on
        // it draws "leaving is free" and "leaving costs an unknown amount" as the same empty space -
        // and a player reads a blank as free and stays in a fight that may cost 4.5% of their score.
        Assert.Null(FleeCostEstimate.Points(3, 105, MeasuredScore));
        Assert.Null(FleeCostEstimate.Points(105, 105, MeasuredScore));

        var free = FleeCostEstimate.Parenthetical(FleeCostBand.Free, null);
        var unmeasured = FleeCostEstimate.Parenthetical(FleeCostBand.AboveEvidence, null);

        Assert.Null(free);
        Assert.Equal(FleeCostEstimate.UnmeasuredMarker, unmeasured);
        Assert.NotEqual(free, unmeasured);
    }

    [Theory]
    [InlineData(FleeCostBand.Free, null, null)]
    [InlineData(FleeCostBand.Unknown, null, null)]
    [InlineData(FleeCostBand.AboveEvidence, null, "?")]
    [InlineData(FleeCostBand.AboveEvidence, 2079, "?")]   // a stale figure must not leak into this band
    [InlineData(FleeCostBand.Cheap, 500, "500")]
    [InlineData(FleeCostBand.Dear, 2079, "2.1k")]
    public void ParentheticalMapsEachBandToWhatThePillMustPrint(
        FleeCostBand band, int? points, string? expected)
        => Assert.Equal(expected, FleeCostEstimate.Parenthetical(band, points));

    [Fact]
    public void APayingBandWithNoFigureStillShowsTheUnmeasuredMarker()
    {
        // Score unknown inside a paying band: we know it costs something and cannot say how much, which
        // is exactly what AboveEvidence means and must render the same way - never as silence.
        Assert.Equal(
            FleeCostEstimate.UnmeasuredMarker,
            FleeCostEstimate.Parenthetical(FleeCostBand.Cheap, null));
    }

    // ── What the rail actually reads ─────────────────────────────────────────────

    private static CombatLiveView Live(int? stamina, int? max, int? points) =>
        CombatLiveView.Idle with { StaminaCurrent = stamina, StaminaMax = max, FleeCostPoints = points };

    [Fact]
    public void TheLiveViewDerivesTheBandFromTheStaminaItAlreadyCarries()
    {
        // Derived rather than passed, so the band and the points figure beside it cannot be set
        // inconsistently with each other.
        Assert.Equal(FleeCostBand.Free, Live(3, 105, null).FleeCostBand);
        Assert.Equal(FleeCostBand.Dear, Live(19, 105, 2079).FleeCostBand);
        Assert.Equal(FleeCostBand.AboveEvidence, Live(105, 105, null).FleeCostBand);
        Assert.Equal(FleeCostBand.Unknown, Live(null, null, null).FleeCostBand);
    }

    [Fact]
    public void TheLiveViewsParentheticalSeparatesFreeFromUnmeasured()
    {
        // The property the renderer must read instead of FleeCostPoints. These two rows are the whole
        // point: same null points figure, opposite meanings, different output.
        Assert.Null(Live(3, 105, null).FleeCostParenthetical);
        Assert.Equal("?", Live(105, 105, null).FleeCostParenthetical);

        Assert.Equal("2.1k", Live(19, 105, 2079).FleeCostParenthetical);
        Assert.Null(CombatLiveView.Idle.FleeCostParenthetical);
    }

    [Fact]
    public void OnAWeakCharacterMostOfTheBarIsUnmeasuredRatherThanFree()
    {
        // Not a corner case. The evidence tops out at 18.1% of maximum, which on a 30-maximum novice is
        // about 5 stamina - so a healthy novice is in the unmeasured band nearly always, and if that
        // rendered as silence the pill would tell them fleeing is free for almost the whole fight.
        foreach (var stamina in new[] { 6, 10, 20, 30 })
        {
            Assert.Equal(FleeCostBand.AboveEvidence, FleeCostEstimate.Band(stamina, 30));
            Assert.Equal("?", Live(stamina, 30, null).FleeCostParenthetical);
        }

        // And the genuinely free end still shows nothing.
        Assert.Null(Live(1, 30, null).FleeCostParenthetical);
    }

    // ── The owner's display format, unchanged by the model swap ──────────────────

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
