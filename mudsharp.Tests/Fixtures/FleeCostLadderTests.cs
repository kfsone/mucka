using MudSharp.Combat;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// Covers DESIGN_FINAL.md section 5's 3-anchor flee-cost model: the two owner-given points (sta &gt;
/// 20 = 10%, sta = 6.5 = 2.5%), the free floor below 6.5, and the explicitly-labelled linear GUESS
/// in between that must never be presented with the same weight as a real anchor.
/// </summary>
public sealed class FleeCostLadderTests
{
    [Theory]
    [InlineData(20.0)]
    [InlineData(25.0)]
    [InlineData(1000.0)]
    public void CostFraction_AtOrAboveCeiling_IsTheFlatAnchor(double stamina)
    {
        var fraction = FleeCostLadder.CostFraction(stamina, out var isAnchor);
        Assert.Equal(0.10, fraction, 6);
        Assert.True(isAnchor);
    }

    [Theory]
    [InlineData(6.5)]
    [InlineData(3.0)]
    [InlineData(0.0)]
    public void CostFraction_AtOrBelowFreeThreshold_IsZeroAndAnAnchor(double stamina)
    {
        var fraction = FleeCostLadder.CostFraction(stamina, out var isAnchor);
        Assert.Equal(0.0, fraction, 6);
        Assert.True(isAnchor);
    }

    [Fact]
    public void CostFraction_ExactMidpointBetweenAnchors_InterpolatesAndIsNotAnAnchor()
    {
        // Midpoint of [6.5, 20] is 13.25 — halfway from 2.5% to 10% is 6.25%.
        var fraction = FleeCostLadder.CostFraction(13.25, out var isAnchor);
        Assert.Equal(0.0625, fraction, 4);
        Assert.False(isAnchor);
    }

    [Fact]
    public void BuildLadder_AlwaysHasExactlyFourRowsInFixedOrder()
    {
        var rows = FleeCostLadder.BuildLadder(50, scoreTotal: 10000);
        Assert.Equal(4, rows.Count);
        Assert.Equal("now", rows[0].Label);
        Assert.Equal("at 20 sta", rows[1].Label);
        Assert.Equal("at 6.5 sta", rows[2].Label);
        Assert.Equal("below 6.5 sta", rows[3].Label);
    }

    [Fact]
    public void BuildLadder_BelowFreeThreshold_NowRowIsFreeWithNoCostFigure()
    {
        var rows = FleeCostLadder.BuildLadder(4.0, scoreTotal: 10000);
        Assert.True(rows[0].IsFree);
        Assert.Null(rows[0].CostFraction);
        Assert.Null(rows[0].CostPoints);
        // The floor row itself is always free too, independent of "now".
        Assert.True(rows[3].IsFree);
    }

    [Fact]
    public void BuildLadder_MidBandNowRow_IsAGuessNotAnAnchor()
    {
        var rows = FleeCostLadder.BuildLadder(13.25, scoreTotal: 10000);
        Assert.False(rows[0].IsAnchor);
        Assert.NotNull(rows[0].CostFraction);
    }

    [Fact]
    public void BuildLadder_ComputesPointsFromTheGivenScore()
    {
        var rows = FleeCostLadder.BuildLadder(25, scoreTotal: 26370);
        // "at 20 sta" is the flat 10% anchor.
        Assert.Equal(2637.0, rows[1].CostPoints!.Value, 3);
    }

    [Fact]
    public void BuildLadder_NullScore_LeavesPointsNullButKeepsFractions()
    {
        var rows = FleeCostLadder.BuildLadder(25, scoreTotal: null);
        Assert.Null(rows[1].CostPoints);
        Assert.NotNull(rows[1].CostFraction);
    }

    [Fact]
    public void HitsToNextBand_SuppressedWhenTheOpponentHasLandedTooFewHitsToTrustARate()
    {
        var result = FleeCostLadder.HitsToNextBand(15, incomingDamagePerHit: 5, opponentLandedHitsThisFight: 1);
        Assert.True(result.Suppressed);
    }

    [Fact]
    public void HitsToNextBand_SuppressedOnceAlreadyFree()
    {
        // 4.4/D7: never reintroduce cost-framing once fleeing is already free.
        var result = FleeCostLadder.HitsToNextBand(5, incomingDamagePerHit: 5, opponentLandedHitsThisFight: 4);
        Assert.True(result.Suppressed);
    }

    [Fact]
    public void HitsToNextBand_SuppressedWithNoIncomingRate()
    {
        var result = FleeCostLadder.HitsToNextBand(15, incomingDamagePerHit: null, opponentLandedHitsThisFight: 4);
        Assert.True(result.Suppressed);
    }

    [Fact]
    public void HitsToNextBand_MidBand_TargetsTheFreeAnchorAndCostGenuinelyChanges()
    {
        // 15 stamina, losing 5/hit: (15 - 6.5) / 5 = 1.7 -> ceil 2 hits to reach the free anchor.
        var result = FleeCostLadder.HitsToNextBand(15, incomingDamagePerHit: 5, opponentLandedHitsThisFight: 4);
        Assert.False(result.Suppressed);
        Assert.Equal(2, result.Hits);
        Assert.Equal(FleeCostLadder.FreeThreshold, result.TargetStamina);
        Assert.True(result.CostChangesAtTarget);
    }

    [Fact]
    public void HitsToNextBand_AboveCeiling_TargetsTheCeilingAndCostDoesNotActuallyChangeThere()
    {
        // 30 stamina, losing 5/hit: (30 - 20) / 5 = 2 hits to reach 20 sta, where the cost is still
        // the same flat 10% (the curve is continuous at sta=20) — honest that nothing is saved yet.
        var result = FleeCostLadder.HitsToNextBand(30, incomingDamagePerHit: 5, opponentLandedHitsThisFight: 4);
        Assert.False(result.Suppressed);
        Assert.Equal(2, result.Hits);
        Assert.Equal(FleeCostLadder.CeilingThreshold, result.TargetStamina);
        Assert.False(result.CostChangesAtTarget);
    }
}
