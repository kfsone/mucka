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

        aggregator.BeginEncounter(start);
        aggregator.ObserveStamina(100);
        aggregator.Observe(new CombatEvent(start.AddSeconds(1), CombatEventKind.HitByNpc, CombatActor.Npc, "rat0", null, 94, 100, ""));
        aggregator.Observe(new CombatEvent(start.AddSeconds(2), CombatEventKind.HitByNpc, CombatActor.Npc, "rat0", null, 92, 100, ""));
        aggregator.ObserveStamina(95);
        aggregator.Observe(new CombatEvent(start.AddSeconds(3), CombatEventKind.HitByNpc, CombatActor.Npc, "rat0", null, 91, 100, ""));
        aggregator.Observe(new CombatEvent(start.AddSeconds(4), CombatEventKind.HitByNpc, CombatActor.Npc, "rat0", null, 93, 100, ""));

        var snapshot = aggregator.Snapshot(start.AddSeconds(4));

        Assert.Equal(4, snapshot.TheyHits);
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
