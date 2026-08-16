using MudSharp.Combat;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// Covers DESIGN_FINAL.md 4.2-4.4's tier table: the stamina/strength/dexterity/unarmed tiers, the
/// bracket-crossing hysteresis latches, the stamina tie-break, and the critical-stamina hard floor.
/// </summary>
public sealed class CombatTierResolverTests
{
    // ── Stamina tier (4.3) ──────────────────────────────────────────────────────

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
        // Under 15s to die, but the kill lands first — 4.3 requires BOTH.
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

    // ── Strength / dexterity / unarmed tiers (4.3) ──────────────────────────────

    [Theory]
    [InlineData(80, 100, CombatTier.None)]   // 80% — above the 75% brief threshold
    [InlineData(70, 100, CombatTier.T1)]     // 70% — below 75%, at/above 50%
    [InlineData(40, 100, CombatTier.T2)]     // 40% — below 50%, intensifies
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

    // ── Pulse tie-break (4.2) ────────────────────────────────────────────────────

    [Fact]
    public void ResolvePulseTier_StaminaT3AlwaysWins()
        => Assert.Equal(CombatTier.T3, CombatTierResolver.ResolvePulseTier(CombatTier.T3, CombatTier.T2));

    [Fact]
    public void ResolvePulseTier_TieBetweenTwoT3CandidatesGoesToStamina()
        // Stamina is the only T3-eligible signal that can directly end the encounter in death — no
        // other signal in this design reaches T3 today, but the tie-break itself must still resolve
        // in stamina's favour if it ever does.
        => Assert.Equal(CombatTier.T3, CombatTierResolver.ResolvePulseTier(CombatTier.T3, CombatTier.T3));

    [Fact]
    public void ResolvePulseTier_NoT3AnywhereFallsBackToTheHigherStaticTier()
        => Assert.Equal(CombatTier.T2, CombatTierResolver.ResolvePulseTier(CombatTier.T1, CombatTier.T2));

    // ── Critical-stamina hard floor (4.4) ───────────────────────────────────────

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

    // ── Bracket-crossing hysteresis (4.2's last bullet) ─────────────────────────

    [Fact]
    public void ObserveStaminaForCrossings_FiresOnceOnTheFirstDownwardCrossing()
    {
        var resolver = new CombatTierResolver();
        var t0 = DateTime.UtcNow;

        resolver.ObserveStaminaForCrossings(10.0, t0);   // above 6.5 — no crossing yet
        Assert.Null(resolver.Below6_5CrossedUtc);

        resolver.ObserveStaminaForCrossings(5.0, t0.AddSeconds(1));   // crosses below 6.5
        Assert.Equal(t0.AddSeconds(1), resolver.Below6_5CrossedUtc);
    }

    [Fact]
    public void ObserveStaminaForCrossings_DoesNotRefireWhileOscillatingBelowTheLine()
    {
        // The exact failure mode 4.2 calls out: small hits/regen ticks straddling 6.5 must not
        // re-flash on every tick — only the FIRST downward crossing counts until it re-arms.
        var resolver = new CombatTierResolver();
        var t0 = DateTime.UtcNow;

        resolver.ObserveStaminaForCrossings(5.0, t0);
        var firstCrossing = resolver.Below6_5CrossedUtc;
        Assert.NotNull(firstCrossing);

        resolver.ObserveStaminaForCrossings(6.4, t0.AddSeconds(1));   // still below 6.5
        resolver.ObserveStaminaForCrossings(5.9, t0.AddSeconds(2));   // still below 6.5
        Assert.Equal(firstCrossing, resolver.Below6_5CrossedUtc);   // unchanged — no re-fire
    }

    [Fact]
    public void ObserveStaminaForCrossings_RearmsOnlyAfterRisingStrictlyAboveTheBoundary()
    {
        var resolver = new CombatTierResolver();
        var t0 = DateTime.UtcNow;

        resolver.ObserveStaminaForCrossings(5.0, t0);              // first crossing
        resolver.ObserveStaminaForCrossings(6.5, t0.AddSeconds(1)); // AT the boundary — not "above"
        resolver.ObserveStaminaForCrossings(4.0, t0.AddSeconds(2)); // still below — should NOT re-fire
        Assert.Equal(t0, resolver.Below6_5CrossedUtc);

        resolver.ObserveStaminaForCrossings(7.0, t0.AddSeconds(3));  // strictly above — re-arms
        resolver.ObserveStaminaForCrossings(3.0, t0.AddSeconds(4));  // crosses again — fires again
        Assert.Equal(t0.AddSeconds(4), resolver.Below6_5CrossedUtc);
    }

    [Fact]
    public void Reset_ClearsBothLatchesAndTimestamps()
    {
        var resolver = new CombatTierResolver();
        var t0 = DateTime.UtcNow;
        resolver.ObserveStaminaForCrossings(3.0, t0);
        Assert.NotNull(resolver.Below6_5CrossedUtc);

        resolver.Reset();
        Assert.Null(resolver.Below6_5CrossedUtc);
        Assert.Null(resolver.Below20CrossedUtc);

        // And the latch is re-armed: a fresh downward crossing fires again immediately.
        resolver.ObserveStaminaForCrossings(3.0, t0.AddSeconds(5));
        Assert.Equal(t0.AddSeconds(5), resolver.Below6_5CrossedUtc);
    }
}
