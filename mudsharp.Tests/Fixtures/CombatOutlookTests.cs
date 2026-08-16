using MudSharp.Combat;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// Covers the "am I going to die before it does" projection. The bulk of these assert that it stays
/// QUIET, which is the point: an early confident verdict is worse than none, because MUD2's silent
/// pass tick means a short observation window can imply a rate far above the real one.
/// </summary>
public sealed class CombatOutlookTests
{
    [Fact]
    public void Project_SaysNothingBeforeTheMinimumElapsedTime()
    {
        // 4 seconds of a fight tells you nothing: the pass tick emits no text, so a burst of luck at
        // the start is indistinguishable from a genuinely high rate.
        var outlook = CombatOutlook.Project(
            elapsedSeconds: 4, damageDealt: 40, damageTaken: 1,
            ownHits: 4, opponentHits: 1, playerStamina: 100, estimatedPool: 50);

        Assert.Equal(OutlookVerdict.Unknown, outlook.Verdict);
    }

    [Fact]
    public void Project_SaysNothingOnASingleLuckyBlow()
    {
        var outlook = CombatOutlook.Project(
            elapsedSeconds: 30, damageDealt: 45, damageTaken: 2,
            ownHits: 1, opponentHits: 1, playerStamina: 100, estimatedPool: 50);

        Assert.Equal(OutlookVerdict.Unknown, outlook.Verdict);
    }

    [Fact]
    public void Project_SaysNothingWithoutAKillEstimateToDivideInto()
    {
        // Never having killed one of these means there is no denominator at all — MUD2 does not report
        // NPC stamina, so there is nothing to project against.
        var outlook = CombatOutlook.Project(
            elapsedSeconds: 30, damageDealt: 20, damageTaken: 10,
            ownHits: 4, opponentHits: 3, playerStamina: 100, estimatedPool: null);

        Assert.Equal(OutlookVerdict.Unknown, outlook.Verdict);
    }

    [Fact]
    public void Project_SaysNothingWithoutAKnownPlayerStamina()
    {
        var outlook = CombatOutlook.Project(
            elapsedSeconds: 30, damageDealt: 20, damageTaken: 10,
            ownHits: 4, opponentHits: 3, playerStamina: null, estimatedPool: 50);

        Assert.Equal(OutlookVerdict.Unknown, outlook.Verdict);
    }

    [Fact]
    public void Project_ReportsUnhurtRatherThanWinningWhenNothingHasLandedOnYou()
    {
        // An opponent that has done no damage yields an infinite time-to-die, and calling that
        // "winning" would overstate what is known — the pass tick means it may simply not have acted.
        var outlook = CombatOutlook.Project(
            elapsedSeconds: 30, damageDealt: 20, damageTaken: 0,
            ownHits: 4, opponentHits: 0, playerStamina: 100, estimatedPool: 50);

        Assert.Equal(OutlookVerdict.Unhurt, outlook.Verdict);
        Assert.NotNull(outlook.SecondsToKill);
        Assert.Null(outlook.SecondsToDie);   // no incoming rate, so no projection, not "infinity"
    }

    [Fact]
    public void Project_CallsItWinningWhenTheOpponentRunsOutFirstByAClearMargin()
    {
        // Dealt 40 of a 50 pool in 20s => 2.0/s, 10 left => 5s to kill.
        // Taken 10 of 100 stamina in 20s => 0.5/s => 200s to die.
        var outlook = CombatOutlook.Project(
            elapsedSeconds: 20, damageDealt: 40, damageTaken: 10,
            ownHits: 4, opponentHits: 2, playerStamina: 100, estimatedPool: 50);

        Assert.Equal(OutlookVerdict.Winning, outlook.Verdict);
        Assert.Equal(5.0, outlook.SecondsToKill!.Value, 3);
        Assert.Equal(200.0, outlook.SecondsToDie!.Value, 3);
    }

    [Fact]
    public void Project_CallsItLosingWhenYouRunOutFirst()
    {
        // Dealt 10 of a 200 pool in 20s => 0.5/s, 190 left => 380s to kill.
        // Taken 40 of 50 stamina in 20s => 2.0/s => 25s to die.
        var outlook = CombatOutlook.Project(
            elapsedSeconds: 20, damageDealt: 10, damageTaken: 40,
            ownHits: 2, opponentHits: 8, playerStamina: 50, estimatedPool: 200);

        Assert.Equal(OutlookVerdict.Losing, outlook.Verdict);
        Assert.True(outlook.SecondsToKill > outlook.SecondsToDie);
    }

    [Fact]
    public void Project_RefusesToCallANarrowMargin()
    {
        // Both sides ~40s out. The estimate is a median over a handful of past kills against a
        // regenerating opponent, so resolving a near-tie would be false precision.
        var outlook = CombatOutlook.Project(
            elapsedSeconds: 20, damageDealt: 20, damageTaken: 20,
            ownHits: 4, opponentHits: 4, playerStamina: 40, estimatedPool: 60);

        Assert.Equal(OutlookVerdict.Even, outlook.Verdict);
    }

    [Fact]
    public void Project_TreatsAnAlreadyExceededEstimateAsImminentRatherThanNegative()
    {
        // Dealt more than the historical median already: this one is tougher than usual. Remaining
        // clamps at zero rather than going negative and inverting the verdict.
        var outlook = CombatOutlook.Project(
            elapsedSeconds: 30, damageDealt: 90, damageTaken: 10,
            ownHits: 9, opponentHits: 2, playerStamina: 100, estimatedPool: 50);

        Assert.Equal(0.0, outlook.SecondsToKill!.Value, 3);
        Assert.Equal(OutlookVerdict.Winning, outlook.Verdict);
    }

    [Fact]
    public void Project_UsesWallClockNotSwingCountForRates()
    {
        // Same swings and damage, different elapsed time => different verdict. This is what stops a
        // fight that has gone quiet (all passes, no text) from still reading as a certain win.
        var quick = CombatOutlook.Project(20, 40, 10, 4, 2, 100, 50);
        var dragging = CombatOutlook.Project(600, 40, 10, 4, 2, 100, 50);

        Assert.Equal(OutlookVerdict.Winning, quick.Verdict);
        Assert.True(dragging.SecondsToKill > quick.SecondsToKill,
            "a longer wall-clock for the same damage must project a slower kill");
    }
}
