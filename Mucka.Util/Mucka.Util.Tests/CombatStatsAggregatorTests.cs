using MudSharp.Combat;
using Mucka.Combat;

namespace Mucka.Util.Tests;

public sealed class CombatStatsAggregatorTests
{
    [Fact]
    public void Snapshot_ComputesHitRatesDamageDoneDurationAndDps()
    {
        var start = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        var aggregator = new CombatStatsAggregator();

        aggregator.BeginEncounter(start);
        aggregator.Observe(new CombatEvent(start, CombatEventKind.FightStart, CombatActor.Player, "rat0", "dagger0", null, null, ""));
        aggregator.Observe(new CombatEvent(start.AddSeconds(2), CombatEventKind.Hit, CombatActor.Player, "rat0", null, 5, 9, ""));
        aggregator.Observe(new CombatEvent(start.AddSeconds(3), CombatEventKind.Miss, CombatActor.Player, "rat0", null, null, null, ""));

        var snapshot = aggregator.Snapshot(start.AddSeconds(4));

        Assert.True(snapshot.HasEncounter);
        Assert.True(snapshot.InCombat);
        Assert.Equal("dagger0", snapshot.CurrentWeapon);
        Assert.Equal(["rat0"], snapshot.ActiveNpcs);
        Assert.Equal(1, snapshot.YouHits);
        Assert.Equal(1, snapshot.YouMisses);
        Assert.Equal(0.5, snapshot.YouHitRate, 3);
        Assert.Equal(7.0, snapshot.ApproxDamageDone, 3);
        Assert.Equal(TimeSpan.FromSeconds(4), snapshot.Duration);
        Assert.Equal(1.75, snapshot.ApproxDps, 3);
    }

    [Fact]
    public void Snapshot_ComputesDamageTakenFromFallingStaminaOnly()
    {
        var start = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        var aggregator = new CombatStatsAggregator();

        // Pre-fight stamina must be known BEFORE BeginEncounter - that's the only reading
        // BeginEncounter can safely seed its combat baseline from (see the test below for why
        // mid-fight ObserveStamina calls must NOT be used for this).
        aggregator.ObserveStamina(100);
        aggregator.BeginEncounter(start);
        aggregator.Observe(new CombatEvent(start.AddSeconds(1), CombatEventKind.HitByNpc, CombatActor.Npc, "rat0", null, 94, 100, ""));
        aggregator.Observe(new CombatEvent(start.AddSeconds(2), CombatEventKind.HitByNpc, CombatActor.Npc, "rat0", null, 92, 100, ""));
        // Simulates MudStreamParser's real firing order: GameLineAnalyzer's own stamina scan
        // fires StatsUpdated -> ObserveStamina with a hit line's OWN embedded (cur/max) BEFORE
        // CombatTracker's matching HitByNpc event reaches here for that same line. Must have
        // zero effect on the delta chain (see the next test, which exercises this ordering's
        // failure mode directly).
        aggregator.ObserveStamina(91);
        aggregator.Observe(new CombatEvent(start.AddSeconds(3), CombatEventKind.HitByNpc, CombatActor.Npc, "rat0", null, 91, 100, ""));
        aggregator.ObserveStamina(93);
        aggregator.Observe(new CombatEvent(start.AddSeconds(4), CombatEventKind.HitByNpc, CombatActor.Npc, "rat0", null, 93, 100, ""));

        var snapshot = aggregator.Snapshot(start.AddSeconds(4));

        Assert.Equal(4, snapshot.TheyHits);
        // 100->94 (6) + 94->92 (2) + 92->91 (1) + 91->93 (regen, discarded) = 9
        Assert.Equal(9.0, snapshot.ApproxDamageTaken, 3);
    }

    [Fact]
    public void Snapshot_SingleHitFight_StillComputesDamageDespiteSameLineStatsRace()
    {
        // A hit line like "The zombie0 hits you (95/100)." is parsed TWICE - once generically by
        // GameLineAnalyzer (which fires StatsUpdated -> ObserveStamina(95)) and once by
        // CombatTracker's HitByNpc regex (RangeLow=95) - and MudStreamParser fires StatsUpdated
        // for a line strictly BEFORE LineReady/_combat.Observe for that SAME line.
        // ObserveDamageTaken must diff against the pre-hit baseline, not the value this exact
        // hit's own line just wrote, or every delta on a single-hit fight computes as 0 - most
        // visible on a single-hit fight, the common case since most NPC swings miss.
        var start = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        var aggregator = new CombatStatsAggregator();

        aggregator.ObserveStamina(100);   // known pre-fight stamina, e.g. from an earlier qs
        aggregator.BeginEncounter(start);
        aggregator.Observe(new CombatEvent(start.AddSeconds(1), CombatEventKind.FightStart, CombatActor.Player, "zombie0", "falchion", null, null, ""));
        aggregator.Observe(new CombatEvent(start.AddSeconds(2), CombatEventKind.Miss, CombatActor.Player, "zombie0", null, null, null, ""));
        aggregator.Observe(new CombatEvent(start.AddSeconds(2), CombatEventKind.MissByNpc, CombatActor.Npc, "zombie0", null, null, null, ""));
        // Same-line race: GameLineAnalyzer's generic scan fires first with the hit's own value...
        aggregator.ObserveStamina(95);
        // ...then CombatTracker's HitByNpc for that identical line.
        aggregator.Observe(new CombatEvent(start.AddSeconds(3), CombatEventKind.HitByNpc, CombatActor.Npc, "zombie0", null, 95, 100, ""));
        aggregator.Observe(new CombatEvent(start.AddSeconds(4), CombatEventKind.Kill, CombatActor.Player, "zombie0", null, null, null, ""));

        var snapshot = aggregator.Snapshot(start.AddSeconds(4));

        Assert.Equal(1, snapshot.TheyHits);
        Assert.Equal(5.0, snapshot.ApproxDamageTaken, 3);   // 100 -> 95, NOT 0
    }

    [Fact]
    public void Snapshot_RegenerationBetweenHits_RevisesBaselineSoNextHitIsNotOverOrUnderCounted()
    {
        // Stamina can rise mid-fight (natural 1-point regen ticks, the dreamword's stamina
        // recovery, the temporary-heal spell, eating a wafer) via a line
        // that carries NO accompanying combat event of its own - basing damage-taken on a fixed
        // pre-fight baseline would misattribute that recovery as "the NPC hit for less" on the
        // NEXT blow. The running _lastKnownStamina chain must instead revise the baseline as each
        // regen/heal reading arrives, so a later hit's delta is diffed against the truly-current
        // pre-hit value, not a stale one from several ticks earlier.
        var start = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        var aggregator = new CombatStatsAggregator();

        aggregator.ObserveStamina(100);
        aggregator.BeginEncounter(start);
        aggregator.Observe(new CombatEvent(start, CombatEventKind.FightStart, CombatActor.Player, "rat0", "dagger0", null, null, ""));

        aggregator.ObserveStamina(95);   // same-line relay ahead of the matching hit
        aggregator.Observe(new CombatEvent(start.AddSeconds(1), CombatEventKind.HitByNpc, CombatActor.Npc, "rat0", null, 95, 100, ""));

        // A natural regen tick (or heal/wafer/dreamword) recovers 2 points - no combat event at
        // all accompanies this line, just a bare stat update.
        aggregator.ObserveStamina(97);

        aggregator.ObserveStamina(90);   // same-line relay ahead of the second hit
        aggregator.Observe(new CombatEvent(start.AddSeconds(3), CombatEventKind.HitByNpc, CombatActor.Npc, "rat0", null, 90, 100, ""));

        var snapshot = aggregator.Snapshot(start.AddSeconds(3));

        Assert.Equal(2, snapshot.TheyHits);
        // hit1: 100 -> 95 = 5. regen: 95 -> 97 (not damage). hit2: 97 -> 90 = 7. Total = 12, NOT
        // 100 -> 90 = 10 (which would silently swallow the regen into the tally) and NOT the
        // naive "always diff against the fixed pre-fight 100" answer of 5 + 10 = 15 either.
        Assert.Equal(12.0, snapshot.ApproxDamageTaken, 3);
    }

    [Fact]
    public void Snapshot_TracksParticipantsWithoutExplicitStartLines()
    {
        var start = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        var aggregator = new CombatStatsAggregator();

        aggregator.BeginEncounter(start);
        aggregator.Observe(new CombatEvent(start, CombatEventKind.FightStart, CombatActor.Player, "rat0", "dagger0", null, null, ""));
        aggregator.Observe(new CombatEvent(start.AddSeconds(1), CombatEventKind.HitByNpc, CombatActor.Npc, "rat6", null, 96, 100, ""));
        aggregator.Observe(new CombatEvent(start.AddSeconds(2), CombatEventKind.Kill, CombatActor.Player, "rat0", null, null, null, ""));

        var snapshot = aggregator.Snapshot(start.AddSeconds(3));

        Assert.Equal(["rat6"], snapshot.ActiveNpcs);
    }

    [Fact]
    public void NewEncounter_ResetsTalliesAndWeapon()
    {
        var start = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        var aggregator = new CombatStatsAggregator();

        aggregator.BeginEncounter(start);
        aggregator.Observe(new CombatEvent(start, CombatEventKind.FightStart, CombatActor.Player, "rat0", "dagger0", null, null, ""));
        aggregator.Observe(new CombatEvent(start.AddSeconds(1), CombatEventKind.Hit, CombatActor.Player, "rat0", null, 3, 5, ""));
        aggregator.EndEncounter();

        aggregator.BeginEncounter(start.AddMinutes(1));
        aggregator.Observe(new CombatEvent(start.AddMinutes(1), CombatEventKind.FightStart, CombatActor.Player, "wolf", "falchion", null, null, ""));

        var snapshot = aggregator.Snapshot(start.AddMinutes(1).AddSeconds(2));

        Assert.True(snapshot.InCombat);
        Assert.Equal("falchion", snapshot.CurrentWeapon);
        Assert.Equal(["wolf"], snapshot.ActiveNpcs);
        Assert.Equal(0, snapshot.YouHits);
        Assert.Equal(0.0, snapshot.ApproxDamageDone, 3);
        Assert.Equal(TimeSpan.FromSeconds(2), snapshot.Duration);
    }

    [Fact]
    public void FightEndOther_DoesNotClearOtherActiveParticipants()
    {
        // "You can fight it no longer." is a trailing acknowledgment (or, for aquatic NPCs, a
        // dive/submerge re-engagement cycle), never an authoritative close. In a multi-NPC fight
        // it must not drop OTHER still-active participants from the live HUD's target list.
        var start = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        var aggregator = new CombatStatsAggregator();

        aggregator.BeginEncounter(start);
        aggregator.Observe(new CombatEvent(start, CombatEventKind.FightStart, CombatActor.Npc, "billy goat", null, null, null, ""));
        aggregator.Observe(new CombatEvent(start.AddSeconds(1), CombatEventKind.FightStart, CombatActor.Npc, "ram", null, null, null, ""));
        aggregator.Observe(new CombatEvent(start.AddSeconds(2), CombatEventKind.FightEndOther, CombatActor.Player, null, null, null, null, ""));

        var snapshot = aggregator.Snapshot(start.AddSeconds(3));

        Assert.True(snapshot.InCombat);
        Assert.Equal(["billy goat", "ram"], snapshot.ActiveNpcs);
    }

    // ---- The exchange rings: the instance is the equality -----------------------------------

    private static (CombatStatsAggregator Aggregator, DateTime Start) Swinging()
    {
        var start = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        var aggregator = new CombatStatsAggregator();
        aggregator.BeginEncounter(start);
        aggregator.Observe(new CombatEvent(start, CombatEventKind.FightStart, CombatActor.Player, "rat0", "dagger0", null, null, ""));
        aggregator.Observe(new CombatEvent(start.AddSeconds(2), CombatEventKind.Hit, CombatActor.Player, "rat0", null, 5, 9, ""));
        return (aggregator, start);
    }

    /// <summary>The frame compares the exchange lists by reference, so two refreshes with no swing
    /// between them must hand out the same instances - the encounter's and each fight's.</summary>
    [Fact]
    public void Exchange_IsTheSameInstanceUntilASwingIsRecorded()
    {
        var (aggregator, start) = Swinging();

        var first = aggregator.Snapshot(start.AddSeconds(3));
        var second = aggregator.Snapshot(start.AddSeconds(4));

        Assert.NotEmpty(first.Exchange!);
        Assert.Same(first.Exchange, second.Exchange);
        Assert.Same(first.Fights[0].Exchange, second.Fights[0].Exchange);
    }

    /// <summary>And a swing must hand out new ones, or the spark would freeze on screen.</summary>
    [Fact]
    public void Exchange_IsANewInstanceCarryingTheSwingAfterOneIsRecorded()
    {
        var (aggregator, start) = Swinging();
        var before = aggregator.Snapshot(start.AddSeconds(3));

        aggregator.Observe(new CombatEvent(start.AddSeconds(4), CombatEventKind.Miss, CombatActor.Player, "rat0", null, null, null, ""));
        var after = aggregator.Snapshot(start.AddSeconds(5));

        Assert.NotSame(before.Exchange, after.Exchange);
        Assert.NotSame(before.Fights[0].Exchange, after.Fights[0].Exchange);
        Assert.Equal(before.Exchange!.Count + 1, after.Exchange!.Count);
        Assert.Equal(before.Fights[0].Exchange!.Count + 1, after.Fights[0].Exchange!.Count);
    }

    /// <summary>A new encounter starts with an empty spark, not the last encounter's cached one.</summary>
    [Fact]
    public void Exchange_IsEmptyAfterANewEncounterBegins()
    {
        var (aggregator, start) = Swinging();
        Assert.NotEmpty(aggregator.Snapshot(start.AddSeconds(3)).Exchange!);

        aggregator.BeginEncounter(start.AddMinutes(1));

        Assert.Empty(aggregator.Snapshot(start.AddMinutes(1).AddSeconds(1)).Exchange!);
    }

    [Fact]
    public void Exchange_IsEmptyAfterAReset()
    {
        var (aggregator, start) = Swinging();
        Assert.NotEmpty(aggregator.Snapshot(start.AddSeconds(3)).Exchange!);

        aggregator.Reset();

        Assert.Empty(aggregator.Snapshot(start.AddSeconds(4)).Exchange ?? []);
    }

    // ---- A creature's health across disengagement ----------------------------------------------

    private static readonly DateTime T0 = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

    private static CombatEvent At(double seconds, CombatEventKind kind, string npc = "zombie3",
        int? rung = null, string? phrase = null)
        => new(T0.AddSeconds(seconds), kind, CombatActor.Player, npc, null, null, null, "", rung, phrase);

    /// <summary>Fights zombie3 to "seriously injured" (rung 3), and it flees at 10 s.</summary>
    private static CombatStatsAggregator HurtThenFled()
    {
        var aggregator = new CombatStatsAggregator();
        aggregator.BeginEncounter(T0);
        aggregator.Observe(At(0, CombatEventKind.FightStart));
        aggregator.Observe(At(4, CombatEventKind.NpcHealth, rung: 3, phrase: "seriously injured"));
        aggregator.Observe(At(10, CombatEventKind.NpcFled));
        return aggregator;
    }

    private static FightSnapshot LiveFight(CombatStatsAggregator aggregator, double seconds)
        => aggregator.Snapshot(T0.AddSeconds(seconds)).Fights.Last(f => !f.IsResolved);

    /// <summary>Chased and re-attacked inside 30 s of breaking off: it is still as hurt as it looked.</summary>
    [Fact]
    public void Health_ReEngagedWithinTheWindow_CarriesTheLastReading()
    {
        var aggregator = HurtThenFled();
        aggregator.Observe(At(18, CombatEventKind.FightStart));

        var fight = LiveFight(aggregator, 19);
        Assert.Equal(3, fight.HealthRung);
        Assert.Equal("seriously injured", fight.HealthPhrase);
    }

    /// <summary>Re-attacked more than 30 s after breaking off: it may have healed, so it starts with no
    /// reading - which draws full.</summary>
    [Fact]
    public void Health_ReEngagedAfterTheWindow_StartsWithNoReading()
    {
        var aggregator = HurtThenFled();
        aggregator.Observe(At(10 + CombatStatsAggregator.HealthCarriesAcrossDisengagementSeconds + 1,
            CombatEventKind.FightStart));

        Assert.Null(LiveFight(aggregator, 60).HealthRung);
    }

    /// <summary>The window holds across a new encounter, not only within one.</summary>
    [Fact]
    public void Health_ReEngagedInANewEncounterWithinTheWindow_CarriesTheLastReading()
    {
        var aggregator = HurtThenFled();
        aggregator.EndEncounter();
        aggregator.BeginEncounter(T0.AddSeconds(20));
        aggregator.Observe(At(20, CombatEventKind.FightStart));

        Assert.Equal(3, LiveFight(aggregator, 21).HealthRung);
    }

    /// <summary>A killed creature is not carried over: the next one by that name is another creature.</summary>
    [Fact]
    public void Health_AKilledCreatureIsNotCarriedOver()
    {
        var aggregator = new CombatStatsAggregator();
        aggregator.BeginEncounter(T0);
        aggregator.Observe(At(0, CombatEventKind.FightStart));
        aggregator.Observe(At(4, CombatEventKind.NpcHealth, rung: 2, phrase: "badly wounded"));
        aggregator.Observe(At(8, CombatEventKind.Kill));
        aggregator.Observe(At(12, CombatEventKind.FightStart));

        Assert.Null(LiveFight(aggregator, 13).HealthRung);
    }

    [Fact]
    public void Health_AResetForgetsEveryCreature()
    {
        var aggregator = HurtThenFled();
        aggregator.BeginEncounter(T0.AddSeconds(12));   // the encounter boundary remembers zombie3
        aggregator.Reset();
        aggregator.BeginEncounter(T0.AddSeconds(15));
        aggregator.Observe(At(15, CombatEventKind.FightStart));

        Assert.Null(LiveFight(aggregator, 16).HealthRung);
    }

    /// <summary>A diagnose carries over too, and still counts every blow since the probe: 3-5 landed
    /// after it before the creature fled, and 2-4 more in the new fight.</summary>
    [Fact]
    public void Health_ADiagnoseCarriesOverWithTheDamageSinceIt()
    {
        var aggregator = new CombatStatsAggregator();
        aggregator.BeginEncounter(T0);
        aggregator.Observe(At(0, CombatEventKind.FightStart));
        aggregator.Observe(new CombatEvent(T0.AddSeconds(2), CombatEventKind.NpcStaminaRead, CombatActor.Player,
            "zombie3", null, 12, 20, ""));
        aggregator.Observe(new CombatEvent(T0.AddSeconds(4), CombatEventKind.Hit, CombatActor.Player,
            "zombie3", null, 3, 5, ""));
        aggregator.Observe(At(10, CombatEventKind.NpcFled));
        aggregator.Observe(At(18, CombatEventKind.FightStart));
        aggregator.Observe(new CombatEvent(T0.AddSeconds(20), CombatEventKind.Hit, CombatActor.Player,
            "zombie3", null, 2, 4, ""));

        var read = LiveFight(aggregator, 21).StaminaReading!.Value;
        Assert.Equal((12, 20), (read.PrintedLow, read.PrintedHigh));
        Assert.Equal(new DamageBracket(5, 9), read.DealtSince);
    }

    /// <summary>The diagnose lapses with the rest after the window.</summary>
    [Fact]
    public void Health_ADiagnoseDoesNotCarryOverAfterTheWindow()
    {
        var aggregator = new CombatStatsAggregator();
        aggregator.BeginEncounter(T0);
        aggregator.Observe(At(0, CombatEventKind.FightStart));
        aggregator.Observe(new CombatEvent(T0.AddSeconds(2), CombatEventKind.NpcStaminaRead, CombatActor.Player,
            "zombie3", null, 12, 20, ""));
        aggregator.Observe(At(10, CombatEventKind.NpcFled));
        aggregator.Observe(At(10 + CombatStatsAggregator.HealthCarriesAcrossDisengagementSeconds + 1,
            CombatEventKind.FightStart));

        Assert.Null(LiveFight(aggregator, 60).StaminaReading);
    }

    /// <summary>The 30 is the operator's number, pinned literally so the constant cannot drift.</summary>
    [Fact]
    public void Health_TheWindowIsThirtySeconds()
        => Assert.Equal(30.0, CombatStatsAggregator.HealthCarriesAcrossDisengagementSeconds);
}
