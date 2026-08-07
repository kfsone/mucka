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
        // DESIGN_FINAL.md 3.8's "why" line (priority 5) and the Combat Rail's E2 weapon-pickup
        // alert both need "how long ago did this NPC arm itself", not just "is it armed" - see
        // FightAccumulator.NpcWeaponEquippedUtc.
        var aggregator = new CombatStatsAggregator();
        aggregator.BeginEncounter(Start);

        aggregator.Observe(Event(CombatEventKind.FightStart, "zombie4"));
        aggregator.Observe(Event(CombatEventKind.NpcWeaponEquip, "zombie4", weapon: "fork", atSecond: 12));

        var fight = aggregator.Snapshot(Start.AddSeconds(20)).Fights[0];
        Assert.Equal("fork", fight.NpcWeapon);
        Assert.Equal(Start.AddSeconds(12), fight.NpcWeaponEquippedUtc);
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
        Assert.Equal(FightOutcome.Killed, snapshot.Fights[0].Outcome);
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
        Assert.All(snapshot.Fights, fight => Assert.Equal(FightOutcome.KilledByNpc, fight.Outcome));
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

        Assert.All(snapshot.Fights, fight => Assert.Equal(FightOutcome.YouFled, fight.Outcome));
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

        Assert.Equal(FightOutcome.Killed, snapshot.Fights[0].Outcome);
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
    public void BeginEncounter_ClearsThePreviousEncountersFights()
    {
        var aggregator = new CombatStatsAggregator();
        aggregator.BeginEncounter(Start);
        aggregator.Observe(Event(CombatEventKind.FightStart, "goat0", weapon: "axe0"));

        aggregator.BeginEncounter(Start.AddMinutes(5));

        Assert.Empty(aggregator.Snapshot(Start.AddMinutes(5)).Fights);
    }
}
