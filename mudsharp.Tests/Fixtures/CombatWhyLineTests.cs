using MudSharp.Combat;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// Covers DESIGN_FINAL.md section 3.8's deterministic, priority-ordered "why" line: surfaces
/// CAUSES, never coefficients, and renders at most one sentence — the single highest-priority
/// active condition — silent when nothing qualifies.
/// </summary>
public sealed class CombatWhyLineTests
{
    // Every "healthy" parameter set used as the baseline for override tests below — nothing here
    // should independently trigger any rule.
    private static CombatWhyLine.Result? Resolve(
        bool hasWeapon = true,
        int? strengthDelta = 0,
        int? itemsCarried = 0,
        double? livePerHit = 10,
        double? historicalMedianPerHit = 10,
        int historicalSampleSize = 5,
        string? weaponDisplayName = "axe0",
        int? dexterityDelta = 0,
        double? liveHitRate = 0.5,
        double? historicalHitRate = 0.5,
        double? secondsSinceNpcWeaponEquip = null,
        string? npcName = "rat0",
        string? npcWeaponDisplayName = null)
        => CombatWhyLine.Resolve(
            hasWeapon, strengthDelta, itemsCarried, livePerHit, historicalMedianPerHit,
            historicalSampleSize, weaponDisplayName, dexterityDelta, liveHitRate, historicalHitRate,
            secondsSinceNpcWeaponEquip, npcName, npcWeaponDisplayName);

    [Fact]
    public void Resolve_NothingQualifies_IsSilent()
        => Assert.Null(Resolve());

    [Fact]
    public void Resolve_Priority1_UnarmedRegardlessOfEverythingElse()
    {
        var result = Resolve(hasWeapon: false, strengthDelta: -20);   // priority 2 also true
        Assert.NotNull(result);
        Assert.Equal(1, result!.Value.Priority);
        Assert.Equal("low dmg: fighting bare handed", result.Value.Text);
    }

    [Fact]
    public void Resolve_Priority2_StrengthDeltaAtOrBelowMinusTen()
    {
        var result = Resolve(strengthDelta: -10, itemsCarried: 7);
        Assert.NotNull(result);
        Assert.Equal(2, result!.Value.Priority);
        Assert.Contains("7 items", result.Value.Text);
        Assert.Contains("10 str", result.Value.Text);
    }

    [Fact]
    public void Resolve_Priority2_UnknownItemCount_NamesTheLoadWithoutCountingIt()
    {
        // The count is only live once the inventory probe has reported. Before that it must not be
        // treated as zero: "0 items cost you 12 str right now" is a flat contradiction, and the
        // stale-count version of this ("7 items" against a real inventory of 3) is the bug this
        // guards. The strength cost itself is live regardless, so the sentence still fires.
        var result = Resolve(strengthDelta: -12, itemsCarried: null);
        Assert.NotNull(result);
        Assert.Equal(2, result!.Value.Priority);
        Assert.Contains("12 str", result.Value.Text);
        Assert.DoesNotContain("0 items", result.Value.Text);
    }

    [Fact]
    public void Resolve_Priority4_UnknownItemCount_NamesTheLoadWithoutCountingIt()
    {
        var result = Resolve(dexterityDelta: -15, liveHitRate: 0.3, historicalHitRate: 0.5, itemsCarried: null);
        Assert.NotNull(result);
        Assert.Equal(4, result!.Value.Priority);
        Assert.Contains("dex", result.Value.Text);
        Assert.DoesNotContain("0 items", result.Value.Text);
    }

    [Fact]
    public void Resolve_Priority2_DoesNotFireAboveTheThreshold()
        => Assert.Null(Resolve(strengthDelta: -9));

    [Fact]
    public void Resolve_Priority3_WeaponHittingBelowSeventyPercentOfItsOwnHistoricalMedian()
    {
        var result = Resolve(livePerHit: 6, historicalMedianPerHit: 10, historicalSampleSize: 3, weaponDisplayName: "dagger0");
        Assert.NotNull(result);
        Assert.Equal(3, result!.Value.Priority);
        Assert.Contains("dagger0", result.Value.Text);
        Assert.Contains("6.0", result.Value.Text);
        Assert.Contains("10.0", result.Value.Text);
    }

    [Fact]
    public void Resolve_Priority3_SuppressedBelowTheMinimumSampleSize()
        => Assert.Null(Resolve(livePerHit: 6, historicalMedianPerHit: 10, historicalSampleSize: 2));

    [Fact]
    public void Resolve_Priority3_SuppressedWhenNotActuallyUnderperforming()
        => Assert.Null(Resolve(livePerHit: 9, historicalMedianPerHit: 10, historicalSampleSize: 5));

    [Fact]
    public void Resolve_Priority4_DexPenaltyWithADroppedHitRate()
    {
        var result = Resolve(dexterityDelta: -15, liveHitRate: 0.3, historicalHitRate: 0.5, itemsCarried: 4);
        Assert.NotNull(result);
        Assert.Equal(4, result!.Value.Priority);
        Assert.Contains("dex", result.Value.Text);
        Assert.Contains("hit rate", result.Value.Text);
    }

    [Fact]
    public void Resolve_Priority4_DoesNotFireIfHitRateHasNotActuallyDropped()
        => Assert.Null(Resolve(dexterityDelta: -20, liveHitRate: 0.6, historicalHitRate: 0.5));

    [Fact]
    public void Resolve_Priority5_NpcWeaponEquippedWithinTheLast20Seconds()
    {
        var result = Resolve(secondsSinceNpcWeaponEquip: 12, npcName: "zombie4", npcWeaponDisplayName: "fork");
        Assert.NotNull(result);
        Assert.Equal(5, result!.Value.Priority);
        Assert.Contains("zombie4", result.Value.Text);
        Assert.Contains("fork", result.Value.Text);
    }

    [Fact]
    public void Resolve_Priority5_SilentOnceTheWindowHasPassed()
        => Assert.Null(Resolve(secondsSinceNpcWeaponEquip: 21, npcName: "zombie4", npcWeaponDisplayName: "fork"));

    [Fact]
    public void Resolve_OnlyTheHighestPriorityActiveConditionRenders()
    {
        // Priorities 2 AND 5 are both true here; 2 must win.
        var result = Resolve(strengthDelta: -15, secondsSinceNpcWeaponEquip: 5,
            npcName: "zombie4", npcWeaponDisplayName: "fork");
        Assert.NotNull(result);
        Assert.Equal(2, result!.Value.Priority);
    }
}
