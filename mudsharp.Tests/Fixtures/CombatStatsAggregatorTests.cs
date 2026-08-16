using Mucka.ViewModels;
using MudSharp.Combat;

namespace MudSharp.Tests.Fixtures;

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

        // Pre-fight stamina must be known BEFORE BeginEncounter — that's the only reading
        // BeginEncounter can safely seed its combat baseline from (see the regression test
        // below for why mid-fight ObserveStamina calls must NOT be used for this).
        aggregator.ObserveStamina(100);
        aggregator.BeginEncounter(start);
        aggregator.Observe(new CombatEvent(start.AddSeconds(1), CombatEventKind.HitByNpc, CombatActor.Npc, "rat0", null, 94, 100, ""));
        aggregator.Observe(new CombatEvent(start.AddSeconds(2), CombatEventKind.HitByNpc, CombatActor.Npc, "rat0", null, 92, 100, ""));
        // Simulates MudStreamParser's real firing order: GameLineAnalyzer's own stamina scan
        // fires StatsUpdated -> ObserveStamina with a hit line's OWN embedded (cur/max) BEFORE
        // CombatTracker's matching HitByNpc event reaches here for that same line. Must have
        // zero effect on the delta chain (see next test for the bug this used to cause).
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
        // Regression: reported live as "damage taken always shows 0.0". Root cause: a hit line
        // like "The zombie0 hits you (95/100)." is parsed TWICE — once generically by
        // GameLineAnalyzer (which fires StatsUpdated -> ObserveStamina(95)) and once by
        // CombatTracker's HitByNpc regex (RangeLow=95) — and MudStreamParser fires StatsUpdated
        // for a line strictly BEFORE LineReady/_combat.Observe for that SAME line. The old code
        // read _lastKnownStamina directly inside ObserveDamageTaken, so it had ALREADY been
        // overwritten with this exact hit's OWN value by the time the delta was computed,
        // making every delta exactly 0 — most visible on a single-hit fight (the common case:
        // most NPC swings miss), which is exactly what a real live zombie fight looked like.
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
        // Player-requested behaviour: stamina can rise mid-fight (natural 1-point regen ticks,
        // the dreamword's stamina recovery, the temporary-heal spell, eating a wafer) via a line
        // that carries NO accompanying combat event of its own — basing damage-taken on a fixed
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

        // A natural regen tick (or heal/wafer/dreamword) recovers 2 points — no combat event at
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
        // Regression: mirrors the exact CombatTracker fix — "You can fight it no longer." is a
        // trailing acknowledgment (or, for aquatic NPCs, a dive/submerge re-engagement cycle),
        // never an authoritative close. In a multi-NPC fight it must not drop OTHER still-active
        // participants from the live HUD's target list.
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
}
