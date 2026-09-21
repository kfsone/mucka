using MudSharp.Combat;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// Covers the alert tiers and trigger table in docs/combat-panel-design.md: the stamina/strength/dexterity/unarmed
/// tiers, the stamina tie-break, and the critical-stamina hard floor.
/// </summary>
public sealed class CombatTierResolverTests
{
    // -- Stamina tier (4.3) ------------------------------------------------------

    [Fact]
    public void StaminaTier_HitsLeftAtOrBelowTwo_IsT3()
    {
        var tier = CombatTierResolver.StaminaTier(
            staminaCurrent: 40, staminaMax: 100, hitsLeft: 2, secondsToDie: null, secondsToKill: null);
        Assert.Equal(CombatTier.T3, tier);
    }

    [Fact]
    public void StaminaTier_ProjectedDeathSoonerThanKillAndUnder15Seconds_IsT3()
    {
        var tier = CombatTierResolver.StaminaTier(
            staminaCurrent: 40, staminaMax: 100, hitsLeft: null, secondsToDie: 10, secondsToKill: 30);
        Assert.Equal(CombatTier.T3, tier);
    }

    [Fact]
    public void StaminaTier_DeathProjectedSoonButSlowerThanTheKill_IsNotT3FromThatAlone()
    {
        // Under 15s to die, but the kill lands first - 4.3 requires BOTH.
        var tier = CombatTierResolver.StaminaTier(
            staminaCurrent: 40, staminaMax: 100, hitsLeft: null, secondsToDie: 10, secondsToKill: 5);
        Assert.NotEqual(CombatTier.T3, tier);
    }

    [Fact]
    public void StaminaTier_HitsLeftAtOrBelowFour_IsT2()
    {
        var tier = CombatTierResolver.StaminaTier(
            staminaCurrent: 60, staminaMax: 100, hitsLeft: 4, secondsToDie: null, secondsToKill: null);
        Assert.Equal(CombatTier.T2, tier);
    }

    [Fact]
    public void StaminaTier_BelowAQuarterOfMax_IsT2()
    {
        var tier = CombatTierResolver.StaminaTier(
            staminaCurrent: 20, staminaMax: 100, hitsLeft: null, secondsToDie: null, secondsToKill: null);
        Assert.Equal(CombatTier.T2, tier);
    }

    [Fact]
    public void StaminaTier_BelowHalfOfMax_IsT1()
    {
        var tier = CombatTierResolver.StaminaTier(
            staminaCurrent: 40, staminaMax: 100, hitsLeft: null, secondsToDie: null, secondsToKill: null);
        Assert.Equal(CombatTier.T1, tier);
    }

    [Fact]
    public void StaminaTier_HealthyIsNone()
    {
        var tier = CombatTierResolver.StaminaTier(
            staminaCurrent: 90, staminaMax: 100, hitsLeft: null, secondsToDie: null, secondsToKill: null);
        Assert.Equal(CombatTier.None, tier);
    }

    [Fact]
    public void StaminaTier_UnknownInputs_IsNone()
    {
        var tier = CombatTierResolver.StaminaTier(null, null, null, null, null);
        Assert.Equal(CombatTier.None, tier);
    }

    // -- Strength / dexterity / unarmed tiers (4.3) ------------------------------

    [Theory]
    [InlineData(80, 100, CombatTier.None)]   // 80% - above the 75% brief threshold
    [InlineData(70, 100, CombatTier.T1)]     // 70% - below 75%, at/above 50%
    [InlineData(40, 100, CombatTier.T2)]     // 40% - below 50%, intensifies
    public void StrengthTier_MatchesTheFractionOfMaxThresholds(int effective, int max, CombatTier expected)
        => Assert.Equal(expected, CombatTierResolver.StrengthTier(effective, max));

    [Fact]
    public void StrengthTier_UnknownMax_IsNone()
        => Assert.Equal(CombatTier.None, CombatTierResolver.StrengthTier(50, null));

    [Fact]
    public void DexterityTier_NonzeroPenaltyInCombat_IsT1()
        => Assert.Equal(CombatTier.T1, CombatTierResolver.DexterityTier(-3, inCombat: true));

    [Fact]
    public void DexterityTier_NonzeroPenaltyOutOfCombat_IsNone()
        => Assert.Equal(CombatTier.None, CombatTierResolver.DexterityTier(-3, inCombat: false));

    [Fact]
    public void DexterityTier_ZeroDelta_IsNone()
        => Assert.Equal(CombatTier.None, CombatTierResolver.DexterityTier(0, inCombat: true));

    [Fact]
    public void DexterityTier_NeverEscalatesPastT1EvenForALargePenalty()
        => Assert.Equal(CombatTier.T1, CombatTierResolver.DexterityTier(-40, inCombat: true));

    [Fact]
    public void UnarmedTier_UnarmedAndLive_IsT2()
        => Assert.Equal(CombatTier.T2, CombatTierResolver.UnarmedTier(isUnarmed: true, fightLive: true));

    [Fact]
    public void UnarmedTier_ArmedOrNotLive_IsNone()
    {
        Assert.Equal(CombatTier.None, CombatTierResolver.UnarmedTier(isUnarmed: false, fightLive: true));
        Assert.Equal(CombatTier.None, CombatTierResolver.UnarmedTier(isUnarmed: true, fightLive: false));
    }

    // -- Pulse tie-break (4.2) ----------------------------------------------------

    [Fact]
    public void ResolvePulseTier_StaminaT3AlwaysWins()
        => Assert.Equal(CombatTier.T3, CombatTierResolver.ResolvePulseTier(CombatTier.T3, CombatTier.T2));

    // A T3/T3 tie is unobservable through this method: it returns a TIER, not which candidate won,
    // so every branch returns T3 and no assertion can tell them apart. The stamina-first line in
    // ResolvePulseTier is therefore pinned by nothing here, and its only production caller passes
    // CombatTier.None as the second argument, so it would not be exercised live either.

    [Fact]
    public void ResolvePulseTier_NoT3AnywhereFallsBackToTheHigherStaticTier()
        => Assert.Equal(CombatTier.T2, CombatTierResolver.ResolvePulseTier(CombatTier.T1, CombatTier.T2));

    // -- Critical-stamina hard floor (4.4) ---------------------------------------

    [Fact]
    public void CriticalStaminaFloorTier_AtOrBelowThreshold_NeverRendersBelowT2()
    {
        // A calm-looking T1/None reading from the stamina table alone must still be promoted.
        Assert.Equal(CombatTier.T2, CombatTierResolver.CriticalStaminaFloorTier(CombatTier.None, staminaCurrent: 6.0));
        Assert.Equal(CombatTier.T2, CombatTierResolver.CriticalStaminaFloorTier(CombatTier.T1, staminaCurrent: 4.0));
    }

    [Fact]
    public void CriticalStaminaFloorTier_HardFloorNeverDemotesAnAlreadyHigherTier()
        => Assert.Equal(CombatTier.T3, CombatTierResolver.CriticalStaminaFloorTier(CombatTier.T3, staminaCurrent: 4.0));

    [Fact]
    public void CriticalStaminaFloorTier_AboveTheThreshold_PassesTheStaminaTierThrough()
        => Assert.Equal(CombatTier.None, CombatTierResolver.CriticalStaminaFloorTier(CombatTier.None, staminaCurrent: 50));
}
