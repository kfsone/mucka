using Mucka.ViewModels;
using MudSharp.Combat;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// Covers the per-NPC fight layer added to <see cref="CombatStatsAggregator"/>. The encounter-wide
/// totals it already produced could not answer "how did this rat fight compare to previous rat
/// fights" whenever a second NPC was in the room, because every counter was lumped together.
/// </summary>
public sealed class CombatPerFightTests
{
    private static readonly DateTime Start = new(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);

    private static CombatEvent Event(
        CombatEventKind kind,
        string? npc = null,
        string? weapon = null,
        int? rangeLow = null,
        int? rangeHigh = null,
        int atSecond = 0)
        => new(Start.AddSeconds(atSecond), kind, CombatActor.Player, npc, weapon, rangeLow, rangeHigh, "");

    [Fact]
    public void Fights_AttributeHitsAndDamageToTheNamedNpcNotTheEncounterLump()
    {
        var aggregator = new CombatStatsAggregator();
        aggregator.BeginEncounter(Start);

        aggregator.Observe(Event(CombatEventKind.FightStart, "goat0", weapon: "broadsword0"));
        aggregator.Observe(Event(CombatEventKind.Hit, "goat0", rangeLow: 4, rangeHigh: 8, atSecond: 1));
        aggregator.Observe(Event(CombatEventKind.FightStart, "ram1", atSecond: 2));
        aggregator.Observe(Event(CombatEventKind.Hit, "ram1", rangeLow: 2, rangeHigh: 4, atSecond: 3));
        aggregator.Observe(Event(CombatEventKind.Miss, "ram1", atSecond: 4));

        var snapshot = aggregator.Snapshot(Start.AddSeconds(5));

        Assert.Equal(2, snapshot.Fights.Count);
        Assert.Equal(2, snapshot.YouHits);      // encounter totals unchanged by the split
        Assert.Equal(1, snapshot.YouMisses);

        var goat = snapshot.Fights[0];
        Assert.Equal("goat0", goat.NpcName);
        Assert.Equal("goats", goat.NpcGroup);
        Assert.Equal(1, goat.YouHits);
        Assert.Equal(0, goat.YouMisses);
        Assert.Equal(6.0, goat.ApproxDamageDone, 3);

        var ram = snapshot.Fights[1];
        Assert.Equal("ram1", ram.NpcName);
        Assert.Equal(1, ram.YouHits);
        Assert.Equal(1, ram.YouMisses);
        Assert.Equal(3.0, ram.ApproxDamageDone, 3);
    }

    [Fact]
    public void Fights_RecordsWhenTheNpcsOwnWeaponWasConfirmed()
    {
        var aggregator = new CombatStatsAggregator();
        aggregator.BeginEncounter(Start);

        aggregator.Observe(Event(CombatEventKind.FightStart, "zombie4"));
        aggregator.Observe(Event(CombatEventKind.NpcWeaponEquip, "zombie4", weapon: "fork", atSecond: 12));

        var fight = aggregator.Snapshot(Start.AddSeconds(20)).Fights[0];
        Assert.Equal("fork", fight.NpcWeapon);
    }

    [Fact]
    public void Fights_EncounterTotalsStillMatchTheSumOfTheFights()
    {
        // The per-fight split must not change what the existing HUD numbers mean — the user is
        // actively reading those.
        var aggregator = new CombatStatsAggregator();
        aggregator.BeginEncounter(Start);

        aggregator.Observe(Event(CombatEventKind.FightStart, "goat0", weapon: "broadsword0"));
        aggregator.Observe(Event(CombatEventKind.Hit, "goat0", rangeLow: 4, rangeHigh: 8, atSecond: 1));
        aggregator.Observe(Event(CombatEventKind.Miss, "goat0", atSecond: 2));
        aggregator.Observe(Event(CombatEventKind.FightStart, "ram1", atSecond: 3));
        aggregator.Observe(Event(CombatEventKind.Hit, "ram1", rangeLow: 2, rangeHigh: 4, atSecond: 4));

        var snapshot = aggregator.Snapshot(Start.AddSeconds(5));

        Assert.Equal(snapshot.YouHits, snapshot.Fights.Sum(f => f.YouHits));
        Assert.Equal(snapshot.YouMisses, snapshot.Fights.Sum(f => f.YouMisses));
        Assert.Equal(snapshot.ApproxDamageDone, snapshot.Fights.Sum(f => f.ApproxDamageDone), 3);
    }

    [Fact]
    public void Fights_AJoinerInheritsTheWeaponAlreadyInUse()
    {
        // MUD2 does not re-arm you for a second attacker: the weapon you are already wielding
        // silently extends to the new fight, and NO equip line is emitted for it. Leaving the
        // joiner's weapon null would strand its row in a "(none)" bucket and quietly corrupt every
        // per-weapon comparison.
        var aggregator = new CombatStatsAggregator();
        aggregator.BeginEncounter(Start);

        aggregator.Observe(Event(CombatEventKind.FightStart, "goat0", weapon: "broadsword0"));
        aggregator.Observe(Event(CombatEventKind.FightStart, "ram1", atSecond: 5));

        var snapshot = aggregator.Snapshot(Start.AddSeconds(6));

        Assert.Equal("broadsword0", snapshot.Fights[0].Weapon);
        Assert.Equal("broadsword0", snapshot.Fights[1].Weapon);
    }

    [Fact]
    public void Fights_ASwitchedWeaponAppliesToEveryStillActiveFight()
    {
        var aggregator = new CombatStatsAggregator();
        aggregator.BeginEncounter(Start);

        aggregator.Observe(Event(CombatEventKind.FightStart, "goat0", weapon: "dagger0"));
        aggregator.Observe(Event(CombatEventKind.FightStart, "ram1", atSecond: 1));
        aggregator.Observe(Event(CombatEventKind.WeaponEquip, weapon: "axe0", atSecond: 2));

        var snapshot = aggregator.Snapshot(Start.AddSeconds(3));

        Assert.All(snapshot.Fights, fight => Assert.Equal("axe0", fight.Weapon));
    }

    [Fact]
    public void Fights_AResolvedFightKeepsItsOwnWeaponWhenTheNextOneSwitches()
    {
        // A finished fight is an immutable record of what actually killed the thing; a later switch
        // must not retroactively rewrite it.
        var aggregator = new CombatStatsAggregator();
        aggregator.BeginEncounter(Start);

        aggregator.Observe(Event(CombatEventKind.FightStart, "goat0", weapon: "dagger0"));
        aggregator.Observe(Event(CombatEventKind.Kill, "goat0", atSecond: 1));
        aggregator.Observe(Event(CombatEventKind.FightStart, "ram1", atSecond: 2));
        aggregator.Observe(Event(CombatEventKind.WeaponEquip, weapon: "axe0", atSecond: 3));

        var snapshot = aggregator.Snapshot(Start.AddSeconds(4));

        Assert.Equal("dagger0", snapshot.Fights[0].Weapon);
        Assert.Equal("axe0", snapshot.Fights[1].Weapon);
    }

    [Fact]
    public void Fights_RetainResolvedFightsSoAMultiNpcEncounterShowsHowEachEnded()
    {
        var aggregator = new CombatStatsAggregator();
        aggregator.BeginEncounter(Start);

        aggregator.Observe(Event(CombatEventKind.FightStart, "goat0", weapon: "axe0"));
        aggregator.Observe(Event(CombatEventKind.FightStart, "ram1", atSecond: 1));
        aggregator.Observe(Event(CombatEventKind.Kill, "goat0", atSecond: 2));

        var snapshot = aggregator.Snapshot(Start.AddSeconds(3));

        Assert.Equal(2, snapshot.Fights.Count);
        Assert.Equal(FightOutcome.Kill, snapshot.Fights[0].Outcome);
        Assert.True(snapshot.Fights[0].IsResolved);
        Assert.Equal(FightOutcome.Unresolved, snapshot.Fights[1].Outcome);
        Assert.False(snapshot.Fights[1].IsResolved);
        // The killed goat drops out of the ACTIVE list but keeps its fight row.
        Assert.Single(snapshot.ActiveNpcs);
    }

    [Fact]
    public void Fights_PlayerDeathResolvesEveryOpenFightNotJustTheKillers()
    {
        // CombatTracker emits KilledByNpc once, naming only the killer, then calls EndAll() — no
        // other fight gets a close event of its own. Leaving the others Unresolved would understate
        // how badly a pile-on went.
        var aggregator = new CombatStatsAggregator();
        aggregator.BeginEncounter(Start);

        aggregator.Observe(Event(CombatEventKind.FightStart, "goat0", weapon: "axe0"));
        aggregator.Observe(Event(CombatEventKind.FightStart, "ram1", atSecond: 1));
        aggregator.Observe(Event(CombatEventKind.KilledByNpc, "ram1", atSecond: 2));

        var snapshot = aggregator.Snapshot(Start.AddSeconds(3));

        Assert.Equal(2, snapshot.Fights.Count);
        Assert.All(snapshot.Fights, fight => Assert.Equal(FightOutcome.Died, fight.Outcome));
    }

    [Fact]
    public void Fights_FleeingResolvesEveryOpenFight()
    {
        var aggregator = new CombatStatsAggregator();
        aggregator.BeginEncounter(Start);

        aggregator.Observe(Event(CombatEventKind.FightStart, "goat0", weapon: "axe0"));
        aggregator.Observe(Event(CombatEventKind.FightStart, "ram1", atSecond: 1));
        aggregator.Observe(Event(CombatEventKind.YouFled, atSecond: 2));

        var snapshot = aggregator.Snapshot(Start.AddSeconds(3));

        Assert.All(snapshot.Fights, fight => Assert.Equal(FightOutcome.UFled, fight.Outcome));
        Assert.Empty(snapshot.ActiveNpcs);
    }

    [Fact]
    public void Fights_FirstResolutionWinsSoATrailingFightEndCannotOverwriteAKill()
    {
        // "You can fight it no longer." commonly trails a real resolution; it must not downgrade it.
        var aggregator = new CombatStatsAggregator();
        aggregator.BeginEncounter(Start);

        aggregator.Observe(Event(CombatEventKind.FightStart, "goat0", weapon: "axe0"));
        aggregator.Observe(Event(CombatEventKind.Kill, "goat0", atSecond: 1));
        aggregator.Observe(Event(CombatEventKind.NpcFled, "goat0", atSecond: 2));

        var snapshot = aggregator.Snapshot(Start.AddSeconds(3));

        Assert.Equal(FightOutcome.Kill, snapshot.Fights[0].Outcome);
    }

    [Fact]
    public void Fights_IncomingDamageIsAttributedToTheAttackerThatLandedIt()
    {
        // Each hit's delta comes off the same continuously-revised baseline the encounter total uses,
        // so the two cannot disagree — see CombatStatsAggregator.ObserveDamageTaken.
        var aggregator = new CombatStatsAggregator();
        aggregator.ObserveStamina(100);
        aggregator.BeginEncounter(Start);

        aggregator.Observe(Event(CombatEventKind.FightStart, "goat0", weapon: "axe0"));
        aggregator.Observe(Event(CombatEventKind.FightStart, "ram1", atSecond: 1));

        aggregator.ObserveStamina(94);   // same-line relay ahead of the goat's hit
        aggregator.Observe(Event(CombatEventKind.HitByNpc, "goat0", rangeLow: 94, rangeHigh: 100, atSecond: 2));

        aggregator.ObserveStamina(90);   // and ahead of the ram's
        aggregator.Observe(Event(CombatEventKind.HitByNpc, "ram1", rangeLow: 90, rangeHigh: 100, atSecond: 3));

        var snapshot = aggregator.Snapshot(Start.AddSeconds(4));

        Assert.Equal(6.0, snapshot.Fights[0].ApproxDamageTaken, 3);
        Assert.Equal(4.0, snapshot.Fights[1].ApproxDamageTaken, 3);
        Assert.Equal(10.0, snapshot.ApproxDamageTaken, 3);
        Assert.Equal(snapshot.ApproxDamageTaken, snapshot.Fights.Sum(f => f.ApproxDamageTaken), 3);
    }

    [Fact]
    public void Fights_ARejoiningNpcContinuesItsExistingTallyRatherThanResetting()
    {
        var aggregator = new CombatStatsAggregator();
        aggregator.BeginEncounter(Start);

        aggregator.Observe(Event(CombatEventKind.FightStart, "goat0", weapon: "axe0"));
        aggregator.Observe(Event(CombatEventKind.Hit, "goat0", rangeLow: 4, rangeHigh: 6, atSecond: 1));
        aggregator.Observe(Event(CombatEventKind.FightStart, "goat0", atSecond: 2));
        aggregator.Observe(Event(CombatEventKind.Hit, "goat0", rangeLow: 4, rangeHigh: 6, atSecond: 3));

        var snapshot = aggregator.Snapshot(Start.AddSeconds(4));

        Assert.Single(snapshot.Fights);
        Assert.Equal(2, snapshot.Fights[0].YouHits);
        Assert.Equal(10.0, snapshot.Fights[0].ApproxDamageDone, 3);
    }

    [Fact]
    public void Fights_RecentSwingsStayBoundedToTheRingCapacity()
    {
        // A fight can run to hundreds of swings; the recent-hits strip only ever wants the last
        // handful, so the ring must never grow past its capacity no matter how long the fight runs
        // (Invariant #1 - AddYouHit runs on every combat line, so an unbounded list here would be
        // pure churn on a hot path).
        var aggregator = new CombatStatsAggregator();
        aggregator.BeginEncounter(Start);
        aggregator.Observe(Event(CombatEventKind.FightStart, "goat0", weapon: "axe0"));

        for (var i = 0; i < 20; i++)
            aggregator.Observe(Event(CombatEventKind.Hit, "goat0", rangeLow: 4, rangeHigh: 4, atSecond: i + 1));

        var snapshot = aggregator.Snapshot(Start.AddSeconds(21));

        Assert.Equal(FightAccumulator.RecentSwingCapacity, snapshot.Fights[0].RecentYourSwings.Count);
    }

    [Fact]
    public void Fights_RecentSwingsRecordMissesInTheirChronologicalSlot()
    {
        // The miss rhythm matters as much as the hit magnitude - a miss between two hits must show
        // up as a miss marker in the right position, not be silently dropped or reordered.
        var aggregator = new CombatStatsAggregator();
        aggregator.BeginEncounter(Start);
        aggregator.Observe(Event(CombatEventKind.FightStart, "goat0", weapon: "axe0"));

        aggregator.Observe(Event(CombatEventKind.Hit, "goat0", rangeLow: 8, rangeHigh: 12, atSecond: 1));   // 10.0
        aggregator.Observe(Event(CombatEventKind.Miss, "goat0", atSecond: 2));
        aggregator.Observe(Event(CombatEventKind.Hit, "goat0", rangeLow: 4, rangeHigh: 4, atSecond: 3));    // 4.0

        var snapshot = aggregator.Snapshot(Start.AddSeconds(4));
        var swings = snapshot.Fights[0].RecentYourSwings;

        Assert.Equal(3, swings.Count);
        Assert.True(swings[0].IsHit);
        Assert.Equal(10.0, swings[0].Damage, 3);
        Assert.False(swings[1].IsHit);
        Assert.True(swings[2].IsHit);
        Assert.Equal(4.0, swings[2].Damage, 3);
    }

    [Fact]
    public void Fights_RecentSwingsTrackTheIncomingSideIndependentlyOfTheOutgoingSide()
    {
        var aggregator = new CombatStatsAggregator();
        aggregator.ObserveStamina(100);
        aggregator.BeginEncounter(Start);
        aggregator.Observe(Event(CombatEventKind.FightStart, "goat0", weapon: "axe0"));

        aggregator.ObserveStamina(94);
        aggregator.Observe(Event(CombatEventKind.HitByNpc, "goat0", rangeLow: 94, rangeHigh: 100, atSecond: 1));
        aggregator.Observe(Event(CombatEventKind.MissByNpc, "goat0", atSecond: 2));

        var snapshot = aggregator.Snapshot(Start.AddSeconds(3));
        var theirs = snapshot.Fights[0].RecentTheirSwings;

        Assert.Equal(2, theirs.Count);
        Assert.True(theirs[0].IsHit);
        Assert.Equal(6.0, theirs[0].Damage, 3);   // 100 -> 94
        Assert.False(theirs[1].IsHit);
    }

    [Fact]
    public void Fights_RecentSwingsRingOverwritesOldestFirstOnceFull()
    {
        // Once the ring has filled, the NEXT write must overwrite the OLDEST slot, not some
        // arbitrary one - otherwise "newest on the right" would silently stop being true.
        var aggregator = new CombatStatsAggregator();
        aggregator.BeginEncounter(Start);
        aggregator.Observe(Event(CombatEventKind.FightStart, "goat0", weapon: "axe0"));

        // Fill the ring (capacity 6) with ascending damage 1..6, then land a 7th hit for 99 damage.
        for (var i = 1; i <= FightAccumulator.RecentSwingCapacity; i++)
            aggregator.Observe(Event(CombatEventKind.Hit, "goat0", rangeLow: i, rangeHigh: i, atSecond: i));
        aggregator.Observe(Event(
            CombatEventKind.Hit, "goat0", rangeLow: 99, rangeHigh: 99, atSecond: FightAccumulator.RecentSwingCapacity + 1));

        var swings = aggregator.Snapshot(Start.AddSeconds(FightAccumulator.RecentSwingCapacity + 2)).Fights[0].RecentYourSwings;

        Assert.Equal(FightAccumulator.RecentSwingCapacity, swings.Count);
        Assert.Equal(2.0, swings[0].Damage, 3);    // the original "1" fell off, "2" is now oldest
        Assert.Equal(99.0, swings[^1].Damage, 3);  // the newest swing is last, i.e. rightmost on screen
    }

    [Fact]
    public void Fights_PoisonDeathResolvesAsNoMoreNotKill()
    {
        // The wyvern frame (owner, 2026-08-26): the creature died of poison, so no kill line was ever
        // printed. Recorded as NoMore deliberately - the damage that finished it never crossed the
        // wire, and StaminaPoolEstimator reads Kill rows' damage brackets to infer a
        // creature's pool. See FightOutcome.NoMore.
        var aggregator = new CombatStatsAggregator();
        aggregator.BeginEncounter(Start);

        aggregator.Observe(Event(CombatEventKind.FightStart, "wyvern", weapon: "pitchfork"));
        aggregator.Observe(Event(CombatEventKind.Hit, "wyvern", rangeLow: 10, rangeHigh: 14, atSecond: 1));
        aggregator.Observe(Event(CombatEventKind.NpcDied, "wyvern", atSecond: 2));

        var snapshot = aggregator.Snapshot(Start.AddSeconds(3));

        Assert.Equal(FightOutcome.NoMore, snapshot.Fights[0].Outcome);
        Assert.Empty(snapshot.ActiveNpcs);   // a corpse is not a target
    }

    [Fact]
    public void Fights_NamedFightEndOtherDropsOnlyThatParticipant()
    {
        // "You can fight the wyvern no longer." names its creature, so CombatTracker has just closed
        // that fight and the panel must stop listing it - without touching the ram, which is still
        // swinging. Recorded as EndOther, NOT left Unresolved: the game closed this fight and simply
        // gave no reason, which is a different fact from "we never saw it end" - the bucket that is
        // worth querying for the next unmatched wording. See FightOutcome.EndOther.
        var aggregator = new CombatStatsAggregator();
        aggregator.BeginEncounter(Start);

        aggregator.Observe(Event(CombatEventKind.FightStart, "wyvern", weapon: "pitchfork"));
        aggregator.Observe(Event(CombatEventKind.FightStart, "ram1", atSecond: 1));
        aggregator.Observe(Event(CombatEventKind.FightEndOther, "wyvern", atSecond: 2));

        var snapshot = aggregator.Snapshot(Start.AddSeconds(3));

        Assert.Equal(["ram1"], snapshot.ActiveNpcs);
        Assert.Equal(FightOutcome.EndOther, snapshot.Fights[0].Outcome);
        Assert.Equal(FightOutcome.Unresolved, snapshot.Fights[1].Outcome);   // the ram is still fighting
    }

    [Fact]
    public void Fights_NamedFightEndOtherCannotOverwriteAnEarlierKill()
    {
        // First resolution wins, pinned for THIS event kind rather than inferred from the NpcFled
        // version of the rule: the named "You can fight the X no longer." routinely trails a real
        // terminator, and downgrading a Kill to EndOther would quietly corrupt both the kill count
        // and the stamina-pool estimate that reads it.
        var aggregator = new CombatStatsAggregator();
        aggregator.BeginEncounter(Start);

        aggregator.Observe(Event(CombatEventKind.FightStart, "wyvern", weapon: "pitchfork"));
        aggregator.Observe(Event(CombatEventKind.Kill, "wyvern", atSecond: 1));
        aggregator.Observe(Event(CombatEventKind.FightEndOther, "wyvern", atSecond: 2));

        Assert.Equal(FightOutcome.Kill, aggregator.Snapshot(Start.AddSeconds(3)).Fights[0].Outcome);
    }

    [Fact]
    public void BeginEncounter_ClearsThePreviousEncountersFights()
    {
        var aggregator = new CombatStatsAggregator();
        aggregator.BeginEncounter(Start);
        aggregator.Observe(Event(CombatEventKind.FightStart, "goat0", weapon: "axe0"));

        aggregator.BeginEncounter(Start.AddMinutes(5));

        Assert.Empty(aggregator.Snapshot(Start.AddMinutes(5)).Fights);
    }

    // -- re-engagement against a name whose fight already closed ------------------

    /// <summary>
    /// A flee attempt ends combat whether or not it succeeds (owner, 2026-09-01), so "the rat17 attempts
    /// to flee, but fails" really does end the fight - the creature is still in the room but no longer
    /// fighting. Anything the player lands after that is a NEW engagement, and reusing the closed bucket
    /// folded its damage into a finished fight's totals.
    /// </summary>
    [Fact]
    public void ReEngagingAClosedFight_OpensAFreshOne_RatherThanFeedingTheClosedRecord()
    {
        var aggregator = new CombatStatsAggregator();
        aggregator.BeginEncounter(Start);

        aggregator.Observe(Event(CombatEventKind.FightStart, "rat17"));
        aggregator.Observe(Event(CombatEventKind.Hit, "rat17", rangeLow: 4, rangeHigh: 8, atSecond: 1));
        aggregator.Observe(Event(CombatEventKind.NpcFleeFailed, "rat17", atSecond: 2));

        // The player attacks again. The creature left combat at the failed flee, so this is a second
        // engagement rather than the first one continuing.
        aggregator.Observe(Event(CombatEventKind.FightStart, "rat17", atSecond: 3));
        aggregator.Observe(Event(CombatEventKind.Hit, "rat17", rangeLow: 5, rangeHigh: 9, atSecond: 3));
        aggregator.Observe(Event(CombatEventKind.Hit, "rat17", rangeLow: 6, rangeHigh: 10, atSecond: 4));

        var fights = aggregator.Fights;
        Assert.Equal(2, fights.Count);

        var closed = fights[0];
        Assert.Equal(FightOutcome.CFledFail, closed.Outcome);
        Assert.Equal(1, closed.YouHits);
        Assert.Equal(4, closed.DamageDealt.Low);
        Assert.Equal(8, closed.DamageDealt.High);

        var live = fights[1];
        Assert.False(live.IsResolved);
        Assert.Equal(2, live.YouHits);
        Assert.Equal(11, live.DamageDealt.Low);
        Assert.Equal(19, live.DamageDealt.High);
    }

    /// <summary>
    /// The consequence that made this worth fixing rather than noting. FightAccumulator.Resolve keeps
    /// the FIRST outcome, so with one shared bucket a creature that broke off and was then killed stayed
    /// labelled "broke off" for ever and the kill was never recorded at all.
    ///
    /// <para>The sequence is the observed one: a failed flee then a fresh attack. In the clog corpus 100
    /// of 128 failed flees are followed by exactly this, at a median 1.8 seconds.</para>
    /// </summary>
    [Fact]
    public void AKillAfterAReEngagement_IsRecordedAsAKill()
    {
        var aggregator = new CombatStatsAggregator();
        aggregator.BeginEncounter(Start);

        aggregator.Observe(Event(CombatEventKind.FightStart, "rat17"));
        aggregator.Observe(Event(CombatEventKind.Hit, "rat17", rangeLow: 4, rangeHigh: 8, atSecond: 1));
        aggregator.Observe(Event(CombatEventKind.NpcFleeFailed, "rat17", atSecond: 2));
        aggregator.Observe(Event(CombatEventKind.FightStart, "rat17", atSecond: 3));
        aggregator.Observe(Event(CombatEventKind.Hit, "rat17", rangeLow: 5, rangeHigh: 9, atSecond: 3));
        aggregator.Observe(Event(CombatEventKind.Kill, "rat17", atSecond: 4));

        var fights = aggregator.Fights;
        Assert.Equal(2, fights.Count);
        Assert.Equal(FightOutcome.CFledFail, fights[0].Outcome);
        Assert.Equal(FightOutcome.Kill, fights[1].Outcome);

        // And the kill bracket - the estimator's only two-sided pool constraint - is the second
        // engagement's blows alone, not both engagements summed.
        Assert.Equal(5, fights[1].DamageDealt.Low);
        Assert.Equal(9, fights[1].DamageDealt.High);
    }

    /// <summary>
    /// A defensive guard, and honestly labelled as one: this ordering is NOT observed. A creature that
    /// attempted to flee has left combat, and across 128 NpcFleeFailed events in the clog corpus the
    /// next event naming it is a fresh FightStart (100) or nothing (28) - never a swing. The swing-level
    /// guard exists so that an unobserved ordering degrades into a fresh engagement rather than into a
    /// corrupted closed record; FightStart is what opens the bucket in every case actually seen.
    /// </summary>
    [Fact]
    public void AnNpcSwingingAfterItsFightClosed_WouldAlsoOpenTheFreshOne()
    {
        var aggregator = new CombatStatsAggregator();
        aggregator.BeginEncounter(Start);

        aggregator.Observe(Event(CombatEventKind.FightStart, "rat17"));
        aggregator.Observe(Event(CombatEventKind.NpcFleeFailed, "rat17", atSecond: 1));
        aggregator.Observe(Event(CombatEventKind.MissByNpc, "rat17", atSecond: 2));

        Assert.Equal(2, aggregator.Fights.Count);
        Assert.False(aggregator.Fights[1].IsResolved);
        Assert.Equal(1, aggregator.Fights[1].TheyMisses);
    }

    [Fact]
    public void AnOngoingFight_IsNeverSplit()
    {
        // The guard keys on the bucket being CLOSED, not on the event kind, so an ordinary fight with a
        // flurry of events stays exactly one fight.
        var aggregator = new CombatStatsAggregator();
        aggregator.BeginEncounter(Start);

        aggregator.Observe(Event(CombatEventKind.FightStart, "rat17"));
        for (var i = 1; i <= 6; i++)
        {
            aggregator.Observe(Event(CombatEventKind.Hit, "rat17", rangeLow: 1, rangeHigh: 2, atSecond: i));
            aggregator.Observe(Event(CombatEventKind.HitByNpc, "rat17", rangeLow: 40, atSecond: i));
        }

        Assert.Single(aggregator.Fights);
        Assert.Equal(6, aggregator.Fights[0].YouHits);
    }

    [Fact]
    public void ATrailingCloseForAnAlreadyClosedFight_DoesNotMintAnEmptyBucket()
    {
        // ResolveFight deliberately still uses the whatever-state lookup. If it re-minted the way the
        // engagement path does, every trailing end line would leave a zero-swing phantom on the roster.
        var aggregator = new CombatStatsAggregator();
        aggregator.BeginEncounter(Start);

        aggregator.Observe(Event(CombatEventKind.FightStart, "rat17"));
        aggregator.Observe(Event(CombatEventKind.Hit, "rat17", rangeLow: 4, rangeHigh: 8, atSecond: 1));
        aggregator.Observe(Event(CombatEventKind.Kill, "rat17", atSecond: 2));
        aggregator.Observe(Event(CombatEventKind.FightEndOther, "rat17", atSecond: 2));

        Assert.Single(aggregator.Fights);
        Assert.Equal(FightOutcome.Kill, aggregator.Fights[0].Outcome);
    }

    [Fact]
    public void AWeaponLineForAClosedFight_NeitherWritesToItNorOpensANewOne()
    {
        // A weapon line is not proof a fight has restarted, so it must not mint a bucket - that is the
        // phantom-opponent hazard - and it must not write into a finished record either.
        var aggregator = new CombatStatsAggregator();
        aggregator.BeginEncounter(Start);

        aggregator.Observe(Event(CombatEventKind.FightStart, "rat17"));
        aggregator.Observe(Event(CombatEventKind.Kill, "rat17", atSecond: 1));
        aggregator.Observe(Event(CombatEventKind.NpcWeaponEquip, "rat17", weapon: "club", atSecond: 2));

        Assert.Single(aggregator.Fights);
        Assert.Null(aggregator.Fights[0].NpcWeapon);
    }

    // ── Creature-value probe: unnumbered-name collisions ──────────────────────────
    // Unnumbered mobs (thief, banshee, coot, fox - see NpcPoolKey's own remarks) have no instance
    // number and can share a live name, so ONE `value <name>` probe can legitimately draw a reply
    // from more than one live creature. Both replies land on the SAME name-keyed bucket here - see
    // MudSession.TryConsumeCreatureValueLine's own remarks on why the session layer does not (and
    // cannot) suppress the second reply itself.

    [Fact]
    public void ObserveCreatureValue_OneReply_IsAConfidentReading()
    {
        var aggregator = new CombatStatsAggregator();
        aggregator.BeginEncounter(Start);
        aggregator.Observe(Event(CombatEventKind.FightStart, "thief"));

        aggregator.ObserveCreatureValue("thief", 1419);

        var fight = aggregator.Snapshot(Start.AddSeconds(1)).Fights.Single(f => f.NpcName == "thief");
        Assert.Equal(1419, fight.Value);
    }

    [Fact]
    public void ObserveCreatureValue_TwoRepliesForTheSameNameThisEncounter_IsUnattributableNotLastWriterWins()
    {
        // Reviewer's executed reproduction: `value thief` drew two replies (two live thieves
        // sharing the name), and the roster ended up holding whichever value arrived LAST - a
        // coin-flip presented as a measurement. FightAccumulator.NoteValue now retracts the first
        // reading the instant a second one arrives for the same bucket, rather than overwriting it,
        // because there is no way to tell which live creature either value actually belongs to.
        var aggregator = new CombatStatsAggregator();
        aggregator.BeginEncounter(Start);
        aggregator.Observe(Event(CombatEventKind.FightStart, "thief"));

        aggregator.ObserveCreatureValue("thief", 1419);
        aggregator.ObserveCreatureValue("thief", 87);

        var fight = aggregator.Snapshot(Start.AddSeconds(1)).Fights.Single(f => f.NpcName == "thief");
        Assert.Null(fight.Value);   // honest absence, not 1419, not 87, and not their average
    }

    [Fact]
    public void ObserveCreatureValue_AThirdReplyAfterAmbiguity_StaysUnattributable()
    {
        var aggregator = new CombatStatsAggregator();
        aggregator.BeginEncounter(Start);
        aggregator.Observe(Event(CombatEventKind.FightStart, "banshee"));

        aggregator.ObserveCreatureValue("banshee", 102);
        aggregator.ObserveCreatureValue("banshee", 143);
        aggregator.ObserveCreatureValue("banshee", 129);   // does not un-flag it or pick a "real" one

        var fight = aggregator.Snapshot(Start.AddSeconds(1)).Fights.Single(f => f.NpcName == "banshee");
        Assert.Null(fight.Value);
    }

    [Fact]
    public void ObserveCreatureValue_TwoDifferentNumberedInstances_BothResolveConfidently()
    {
        // The ambiguity is specific to a SHARED name - two different numbered instances (which the
        // game itself keeps distinct, per NpcPoolKey) must not interfere with each other at all.
        var aggregator = new CombatStatsAggregator();
        aggregator.BeginEncounter(Start);
        aggregator.Observe(Event(CombatEventKind.FightStart, "gargoyle0"));
        aggregator.Observe(Event(CombatEventKind.FightStart, "gargoyle1"));

        aggregator.ObserveCreatureValue("gargoyle1", 300);
        aggregator.ObserveCreatureValue("gargoyle0", 150);

        var snapshot = aggregator.Snapshot(Start.AddSeconds(1));
        Assert.Equal(150, snapshot.Fights.Single(f => f.NpcName == "gargoyle0").Value);
        Assert.Equal(300, snapshot.Fights.Single(f => f.NpcName == "gargoyle1").Value);
    }

    // -- the client's own force-end vs MUD2's unnamed fight-end ---------------------
    //
    // These two events arrive with a null NpcName and used to arrive with the SAME event kind, which
    // is the whole bug: the pronoun form ("You can fight it no longer.") must close nothing, so the
    // force-end - which means the entire encounter is over - was swallowed by the same no-op and a
    // reset left every fight live and drawn on the rail. See CombatEventKind.EncounterForceEnded.

    /// <summary>The roster as SidePanelViewModel builds it, reduced to the two fields this bug is
    /// about. Asserting through ParticipantRoster rather than on the outcome alone is the point: the
    /// symptom the owner saw was rows still being drawn as opponents, and IsLive is derived from the
    /// outcome, so a fix that resolved the fights but left them live would still pass a bare
    /// outcome assertion.</summary>
    private static RosterPlan RosterOf(CombatEncounterSnapshot snapshot)
        => ParticipantRoster.Build(
            [.. snapshot.Fights.Select(f => new ParticipantFact(f.NpcName, f.IsResolved, f.Outcome))]);

    [Fact]
    public void ForceEnd_ResolvesEveryOpenFightAndClearsTheRoster()
    {
        var aggregator = new CombatStatsAggregator();
        aggregator.BeginEncounter(Start);

        aggregator.Observe(Event(CombatEventKind.FightStart, "rat0", weapon: "dagger0"));
        aggregator.Observe(Event(CombatEventKind.FightStart, "rat1", atSecond: 1));
        aggregator.Observe(Event(CombatEventKind.FightStart, "rat2", atSecond: 2));

        aggregator.Observe(Event(CombatEventKind.EncounterForceEnded, atSecond: 3));

        var snapshot = aggregator.Snapshot(Start.AddSeconds(4));
        // Interrupted, not Unresolved: the fights were cut short, not lost track of, and Unresolved
        // renders as "still swinging". Not a win or a loss either - see FightOutcome.Interrupted.
        Assert.All(snapshot.Fights, f => Assert.Equal(FightOutcome.Interrupted, f.Outcome));
        Assert.All(snapshot.Fights, f => Assert.Equal(Start.AddSeconds(3), f.EndedUtc));
        Assert.Empty(snapshot.ActiveNpcs);

        var roster = RosterOf(snapshot);
        Assert.Equal(0, roster.LiveCount);
        Assert.Equal(3, roster.ResolvedCount);
        Assert.All(roster.Rows, r => Assert.False(r.IsLive));
        Assert.DoesNotContain(roster.Rows, r => r.IsCurrentTarget);
    }

    [Fact]
    public void ForceEnd_LeavesAnAlreadyResolvedFightAlone()
    {
        // First resolution wins. A reset landing after a kill must not downgrade the kill to
        // Interrupted - that would cost the kill count and the stamina-pool estimate that reads it.
        var aggregator = new CombatStatsAggregator();
        aggregator.BeginEncounter(Start);

        aggregator.Observe(Event(CombatEventKind.FightStart, "goat0", weapon: "axe0"));
        aggregator.Observe(Event(CombatEventKind.FightStart, "ram1", atSecond: 1));
        aggregator.Observe(Event(CombatEventKind.Kill, "goat0", atSecond: 2));
        aggregator.Observe(Event(CombatEventKind.EncounterForceEnded, atSecond: 3));

        var snapshot = aggregator.Snapshot(Start.AddSeconds(4));
        Assert.Equal(FightOutcome.Kill, snapshot.Fights[0].Outcome);
        Assert.Equal(FightOutcome.Interrupted, snapshot.Fights[1].Outcome);
    }

    [Fact]
    public void UnnamedFightEndOther_TheGamesPronounForm_StillClosesNothing()
    {
        // "You can fight it no longer." names nobody. It is a trailing acknowledgment of an end
        // already stated earlier in the same frame, so acting on it would close a pack's other
        // still-swinging participants. This is the behaviour the force-end fix had to preserve, and
        // the reason the two cannot share an event kind.
        var aggregator = new CombatStatsAggregator();
        aggregator.BeginEncounter(Start);

        aggregator.Observe(Event(CombatEventKind.FightStart, "goat0", weapon: "axe0"));
        aggregator.Observe(Event(CombatEventKind.FightStart, "ram1", atSecond: 1));
        aggregator.Observe(Event(CombatEventKind.FightEndOther, npc: null, atSecond: 2));

        var snapshot = aggregator.Snapshot(Start.AddSeconds(3));
        Assert.All(snapshot.Fights, f => Assert.Equal(FightOutcome.Unresolved, f.Outcome));
        Assert.Equal(["goat0", "ram1"], snapshot.ActiveNpcs);

        var roster = RosterOf(snapshot);
        Assert.Equal(2, roster.LiveCount);
        Assert.All(roster.Rows, r => Assert.True(r.IsLive));
    }

    [Fact]
    public void PackOfThree_OneNamedFightEndOther_LeavesTheOtherTwoLive()
    {
        var aggregator = new CombatStatsAggregator();
        aggregator.BeginEncounter(Start);

        aggregator.Observe(Event(CombatEventKind.FightStart, "rat0", weapon: "dagger0"));
        aggregator.Observe(Event(CombatEventKind.FightStart, "rat1", atSecond: 1));
        aggregator.Observe(Event(CombatEventKind.FightStart, "rat2", atSecond: 2));
        aggregator.Observe(Event(CombatEventKind.FightEndOther, "rat1", atSecond: 3));

        var snapshot = aggregator.Snapshot(Start.AddSeconds(4));
        Assert.Equal(FightOutcome.Unresolved, snapshot.Fights[0].Outcome);
        Assert.Equal(FightOutcome.EndOther, snapshot.Fights[1].Outcome);
        Assert.Equal(FightOutcome.Unresolved, snapshot.Fights[2].Outcome);
        Assert.Equal(["rat0", "rat2"], snapshot.ActiveNpcs);

        var roster = RosterOf(snapshot);
        Assert.Equal(2, roster.LiveCount);
        Assert.Equal(1, roster.ResolvedCount);
    }
}
