using Mucka.Combat;
using MudSharp.Combat;

namespace Mucka.Util.Tests;

/// <summary>
/// The two functions behind the Combat Insights panel's verdict: which fight the readout is about,
/// and what it says about whether the player is winning it.
///
/// <para>Both are EXECUTED by <see cref="CombatFrameComposerTests"/> on its in-combat path, so
/// coverage reports them as reached. Nothing there names them or asserts their choice, which is the
/// gap these tests close - line coverage cannot see an unasserted decision.</para>
/// </summary>
public sealed class CombatCompositionTests
{
    private static readonly DateTime Now = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

    private static FightSnapshot Fight(
        string npcName,
        bool resolved = false,
        int youHits = 4,
        int theyHits = 2,
        double damageDone = 40,
        double damageTaken = 20,
        double durationSeconds = 20)
        => new(
            npcName, NpcGroups.Normalize(npcName), Weapon: "axe0", NpcWeapon: null,
            YouHits: youHits, YouMisses: 0, TheyHits: theyHits, TheyMisses: 0,
            ApproxDamageDone: damageDone, ApproxDamageTaken: damageTaken,
            Duration: TimeSpan.FromSeconds(durationSeconds),
            Outcome: resolved ? FightOutcome.Kill : FightOutcome.Unresolved,
            IsResolved: resolved,
            EndedUtc: resolved ? Now : null);

    private static CombatEncounterSnapshot Encounter(bool inCombat, params FightSnapshot[] fights)
        => new(
            HasEncounter: true, InCombat: inCombat, StartedUtc: Now - TimeSpan.FromSeconds(20),
            CurrentWeapon: "axe0", ActiveNpcs: [],
            YouHits: 0, YouMisses: 0, TheyHits: 0, TheyMisses: 0, YouHitRate: 0, TheyHitRate: 0,
            ApproxDamageDone: 0, ApproxDamageTaken: 0, Duration: TimeSpan.FromSeconds(20),
            ApproxDps: 0, TheirApproxDps: 0, Fights: fights);

    /// <summary>A species band whose top and middle are far apart, so a projection that reads the
    /// wrong end of it is visible in the verdict rather than only in the decimals.</summary>
    private static CombatHistoryContext HistoryWithBand(double above, double atMost)
        => CombatHistoryContext.Empty with
        {
            Pool = StaminaPoolEstimate.None with { Interval = new StaminaInterval(above, atMost) },
        };

    // ---- PrimaryFight -------------------------------------------------------------------------

    /// <summary>
    /// The fight the panel is about is the first one still going, not the first one in the list.
    ///
    /// <para>This is the assertion that catches <c>if (!fight.IsResolved) return fight;</c> being
    /// inverted: the panel would then report on the goat it has already killed while the ram is
    /// still swinging at it.</para>
    /// </summary>
    [Fact]
    public void PrimaryFight_IsTheFirstStillUnresolvedFight_NotTheFirstListed()
    {
        var snapshot = Encounter(inCombat: true, Fight("goat", resolved: true), Fight("ram"));

        var primary = CombatComposition.PrimaryFight(snapshot);

        Assert.NotNull(primary);
        Assert.Equal("ram", primary!.NpcName);
    }

    /// <summary>Once everything has resolved the block must not go blank mid-grace-window, so it
    /// falls back to the encounter's FIRST fight - not its last, and not null.</summary>
    [Fact]
    public void PrimaryFight_FallsBackToTheFirstFight_WhenEveryFightHasResolved()
    {
        var snapshot = Encounter(
            inCombat: true, Fight("goat", resolved: true), Fight("ram", resolved: true));

        var primary = CombatComposition.PrimaryFight(snapshot);

        Assert.NotNull(primary);
        Assert.Equal("goat", primary!.NpcName);
    }

    /// <summary>No fights, no primary. There is nothing to fall back to and nothing to invent.</summary>
    [Fact]
    public void PrimaryFight_IsNull_WhenTheEncounterHoldsNoFights()
    {
        Assert.Null(CombatComposition.PrimaryFight(Encounter(inCombat: true)));
    }

    // ---- ComputeOutlook -----------------------------------------------------------------------

    /// <summary>
    /// Out of combat the verdict is Unknown, whatever the last fight's numbers were.
    ///
    /// <para>The fight handed in here is fully projectable, so the only thing suppressing the
    /// verdict is the <c>InCombat</c> gate - delete it and this reddens.</para>
    /// </summary>
    [Fact]
    public void ComputeOutlook_IsUnknown_OutOfCombat_EvenWithAProjectableFight()
    {
        var outlook = CombatComposition.ComputeOutlook(
            Encounter(inCombat: false, Fight("ram")),
            new CombatStatDeficits(StaminaCurrent: 60, StaminaMax: 100, ObjectsCarried: 0),
            HistoryWithBand(above: 20, atMost: 140));

        Assert.Equal(OutlookVerdict.Unknown, outlook.Verdict);
    }

    /// <summary>In combat but with no fight to be about: still Unknown. There is no fight to project
    /// from, and a verdict here would be a claim about nothing.</summary>
    [Fact]
    public void ComputeOutlook_IsUnknown_InCombatWithNoPrimaryFight()
    {
        var outlook = CombatComposition.ComputeOutlook(
            Encounter(inCombat: true),
            new CombatStatDeficits(StaminaCurrent: 60, StaminaMax: 100, ObjectsCarried: 0),
            HistoryWithBand(above: 20, atMost: 140));

        Assert.Equal(OutlookVerdict.Unknown, outlook.Verdict);
    }

    /// <summary>
    /// The projection reads the TOP of the species band, never its middle - the safe error in a
    /// permadeath game is "this is taking longer than you think".
    ///
    /// <para>Arranged so the two ends of the band disagree about the verdict, not merely about the
    /// decimals. The fight has dealt 40 over 20 s (2/s) and taken 20 over 20 s (1/s) against 60
    /// stamina, so the player has 60 s left. Against the band's top of 140 there are 100 points
    /// still to chew through - 50 s to kill, a ratio of 0.83, which is Even. Against the band's
    /// midpoint of 80 there would be 40 left - 20 s to kill, a ratio of 0.33, which reads as
    /// Winning. Swap <c>history.Pool.PessimisticPool</c> for <c>Interval.Midpoint</c> and the panel
    /// tells the operator he is winning a fight the evidence says is even.</para>
    /// </summary>
    [Fact]
    public void ComputeOutlook_ProjectsAgainstThePessimisticPool_NotTheBandMidpoint()
    {
        var outlook = CombatComposition.ComputeOutlook(
            Encounter(inCombat: true, Fight("ram")),
            new CombatStatDeficits(StaminaCurrent: 60, StaminaMax: 100, ObjectsCarried: 0),
            HistoryWithBand(above: 20, atMost: 140));

        Assert.Equal(OutlookVerdict.Even, outlook.Verdict);
        Assert.Equal(50.0, Assert.IsType<double>(outlook.SecondsToKill), 3);
        Assert.Equal(60.0, Assert.IsType<double>(outlook.SecondsToDie), 3);
    }

    /// <summary>The two halves joined up: with a resolved fight sitting in front of a live one, the
    /// verdict describes the LIVE one. The resolved goat's numbers are unprojectable (nothing has
    /// landed on the player in it), so reporting on it would silently downgrade the panel to
    /// Unknown while a ram is still swinging.</summary>
    [Fact]
    public void ComputeOutlook_ProjectsTheStillLiveFight_NotTheOneAlreadyWon()
    {
        var snapshot = Encounter(
            inCombat: true,
            Fight("goat", resolved: true, youHits: 1, theyHits: 0, damageTaken: 0, durationSeconds: 1),
            Fight("ram"));

        var outlook = CombatComposition.ComputeOutlook(
            snapshot,
            new CombatStatDeficits(StaminaCurrent: 60, StaminaMax: 100, ObjectsCarried: 0),
            HistoryWithBand(above: 20, atMost: 140));

        Assert.Equal(OutlookVerdict.Even, outlook.Verdict);
    }
}
