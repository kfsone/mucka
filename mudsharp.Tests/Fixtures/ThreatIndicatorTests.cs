using MudSharp.Combat;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// Covers the Combat Rail's headline threat indicator (DESIGN_FINAL.md section 4, amended): the
/// owner's "DEATH IN &lt;n&gt;S, bold, gently glowing at first, angrier as it gets likelier" request.
/// Deliberately mirrors <see cref="CombatTierResolverTests"/>' own scenarios wherever the tier table
/// is the thing actually deciding, so a change to one and not the other would be caught by both.
/// </summary>
public sealed class ThreatIndicatorTests
{
    [Fact]
    public void Resolve_NotInCombat_IsIdleWithNoLabel()
    {
        var reading = ThreatIndicator.Resolve(
            inCombat: false, CombatTier.T3, OutlookVerdict.Losing, secondsToDie: 5, hitsLeft: 1,
            staminaCurrent: 10, staminaMax: 100);

        Assert.Equal(ThreatLevel.Idle, reading.Level);
        Assert.Equal(string.Empty, reading.Label);
    }

    [Fact]
    public void Resolve_StaminaTierT3_IsCriticalAndPrefersTheSecondsToDieFigure()
    {
        var reading = ThreatIndicator.Resolve(
            inCombat: true, CombatTier.T3, OutlookVerdict.Losing, secondsToDie: 14.4, hitsLeft: 3,
            staminaCurrent: 8, staminaMax: 100);

        Assert.Equal(ThreatLevel.Critical, reading.Level);
        Assert.Equal("DEATH IN 14S", reading.Label);
    }

    [Fact]
    public void Resolve_CriticalWithNoTimeProjection_FallsBackToHitsLeft()
    {
        var reading = ThreatIndicator.Resolve(
            inCombat: true, CombatTier.T3, OutlookVerdict.Losing, secondsToDie: null, hitsLeft: 2,
            staminaCurrent: 8, staminaMax: 100);

        Assert.Equal(ThreatLevel.Critical, reading.Level);
        Assert.Equal("DEATH IN ~2 HITS", reading.Label);
    }

    [Fact]
    public void Resolve_CriticalWithOneHitLeft_SaysHitSingularNotHits()
    {
        var reading = ThreatIndicator.Resolve(
            inCombat: true, CombatTier.T3, OutlookVerdict.Losing, secondsToDie: null, hitsLeft: 1,
            staminaCurrent: 4, staminaMax: 100);

        Assert.Equal("DEATH IN ~1 HIT", reading.Label);
    }

    [Fact]
    public void Resolve_CriticalWithNeitherFigure_FallsBackToAPlainLabelRatherThanThrowing()
    {
        var reading = ThreatIndicator.Resolve(
            inCombat: true, CombatTier.T3, OutlookVerdict.Unknown, secondsToDie: null, hitsLeft: null,
            staminaCurrent: null, staminaMax: null);

        Assert.Equal(ThreatLevel.Critical, reading.Level);
        Assert.Equal("DEATH IMMINENT", reading.Label);
    }

    [Fact]
    public void Resolve_StaminaTierT2_IsDangerAndNamesHitsLeftWhenKnown()
    {
        var reading = ThreatIndicator.Resolve(
            inCombat: true, CombatTier.T2, OutlookVerdict.Losing, secondsToDie: null, hitsLeft: 4,
            staminaCurrent: 20, staminaMax: 100);

        Assert.Equal(ThreatLevel.Danger, reading.Level);
        Assert.Equal("~4 HITS FROM DEATH", reading.Label);
    }

    [Fact]
    public void Resolve_StaminaTierT2_WithNoHitsFigure_SaysStaminaLow()
    {
        var reading = ThreatIndicator.Resolve(
            inCombat: true, CombatTier.T2, OutlookVerdict.Unknown, secondsToDie: null, hitsLeft: null,
            staminaCurrent: 20, staminaMax: 100);

        Assert.Equal(ThreatLevel.Danger, reading.Level);
        Assert.Equal("STAMINA LOW", reading.Label);
    }

    [Fact]
    public void Resolve_StaminaTierT1_IsCautionAndSaysStaminaDropping()
    {
        var reading = ThreatIndicator.Resolve(
            inCombat: true, CombatTier.T1, OutlookVerdict.Unknown, secondsToDie: null, hitsLeft: null,
            staminaCurrent: 40, staminaMax: 100);

        Assert.Equal(ThreatLevel.Caution, reading.Level);
        Assert.Equal("STAMINA DROPPING", reading.Label);
    }

    [Fact]
    public void Resolve_HealthyStaminaButLosingOnOutlookAlone_IsCautionNotSafe()
    {
        // The one decision this class adds on top of the shared tier table: stamina says nothing is
        // wrong yet, but the fight already reads as losing on the outlook projection - worth a calm
        // nudge, never higher, since the tier table (the thing that can actually end in death) has
        // not said so.
        var reading = ThreatIndicator.Resolve(
            inCombat: true, CombatTier.None, OutlookVerdict.Losing, secondsToDie: null, hitsLeft: null,
            staminaCurrent: 90, staminaMax: 100);

        Assert.Equal(ThreatLevel.Caution, reading.Level);
        Assert.Equal("LOSING", reading.Label);
    }

    [Fact]
    public void Resolve_HealthyStaminaAndWinning_IsSafeAndSaysWinning()
    {
        var reading = ThreatIndicator.Resolve(
            inCombat: true, CombatTier.None, OutlookVerdict.Winning, secondsToDie: null, hitsLeft: null,
            staminaCurrent: 90, staminaMax: 100);

        Assert.Equal(ThreatLevel.Safe, reading.Level);
        Assert.Equal("WINNING", reading.Label);
    }

    [Theory]
    [InlineData(OutlookVerdict.Unknown)]
    [InlineData(OutlookVerdict.Unhurt)]
    [InlineData(OutlookVerdict.Even)]
    public void Resolve_HealthyStaminaAndNotClearlyLosing_IsSafeAndSaysSteady(OutlookVerdict verdict)
    {
        var reading = ThreatIndicator.Resolve(
            inCombat: true, CombatTier.None, verdict, secondsToDie: null, hitsLeft: null,
            staminaCurrent: 90, staminaMax: 100);

        Assert.Equal(ThreatLevel.Safe, reading.Level);
        Assert.Equal("STEADY", reading.Label);
    }

    [Fact]
    public void Resolve_CriticalAlwaysOutranksCaution_EvenIfOutlookDisagrees()
    {
        // The tier table is the single source of truth for urgency - the outlook-only Caution
        // fallback must never override a T3 stamina reading, even if the outlook verdict somehow
        // reads calmer (e.g. a fresh joiner mid-projection-reset).
        var reading = ThreatIndicator.Resolve(
            inCombat: true, CombatTier.T3, OutlookVerdict.Winning, secondsToDie: 5, hitsLeft: null,
            staminaCurrent: 5, staminaMax: 100);

        Assert.Equal(ThreatLevel.Critical, reading.Level);
    }
}
