using Mucka.ViewModels;
using MudSharp.Combat;
using MudSharp.Models;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// The health descriptor's route from a line of game text to a drawable rung: tracker recognises it,
/// aggregator attributes it to the right NPC, roster ages it, renderer's staleness rules decide whether
/// it is still evidence.
/// </summary>
public sealed class NpcHealthTrackingTests
{
    private static readonly DateTime T0 = new(2026, 8, 10, 3, 0, 0, DateTimeKind.Utc);

    private static StyledLine Line(string text) => new([new StyledSpan(text, TextStyle.Default)]);

    private static List<CombatEvent> Observe(params string[] lines)
    {
        var tracker = new CombatTracker();
        var seen = new List<CombatEvent>();
        tracker.EventOccurred += seen.Add;
        for (var i = 0; i < lines.Length; i++)
            tracker.Observe(Line(lines[i]), T0.AddSeconds(i));
        return seen;
    }

    [Fact]
    public void Tracker_EmitsNpcHealthForAnEngagedOpponent()
    {
        var events = Observe(
            "You attack the zombie2, using the axe0 as a weapon.",
            "You hit the zombie2 (20-29).",
            "The zombie2 looks moderately damaged.");

        var health = Assert.Single(events, e => e.Kind == CombatEventKind.NpcHealth);
        Assert.Equal("zombie2", health.NpcName);
        Assert.Equal(4, health.HealthRung);
        Assert.Equal("moderately damaged", health.HealthPhrase);
    }

    /// <summary>The same sentence appears in room descriptions. A wounded creature the player has never
    /// touched must not open an encounter or appear on the panel - in a permadeath game a phantom
    /// opponent is worse than a missing one.</summary>
    [Fact]
    public void Tracker_IgnoresHealthLinesForCreaturesNotBeingFought()
    {
        var events = Observe("The rat7 looks critically injured.");

        Assert.Empty(events);
    }

    [Fact]
    public void Tracker_IgnoresHealthLinesForAnOpponentAlreadyDead()
    {
        var events = Observe(
            "You attack the rat7, using the axe0 as a weapon.",
            "You have killed the rat7.",
            "The rat7 looks critically injured.");

        Assert.DoesNotContain(events, e => e.Kind == CombatEventKind.NpcHealth);
    }

    [Fact]
    public void Aggregator_AttributesReadingsToTheRightNpcInAPackFight()
    {
        var aggregator = new CombatStatsAggregator();
        var tracker = new CombatTracker();
        tracker.EventOccurred += aggregator.Observe;

        var lines = new[]
        {
            "You attack the rat1, using the axe0 as a weapon.",
            "The rat2 attacks you.",
            "You hit the rat1 (5-9).",
            "The rat1 looks seriously injured.",
            "You hit the rat2 (1-4).",
            "The rat2 looks fit.",
        };
        for (var i = 0; i < lines.Length; i++)
            tracker.Observe(Line(lines[i]), T0.AddSeconds(i));

        var snapshot = aggregator.Snapshot(T0.AddSeconds(lines.Length));
        var rat1 = snapshot.Fights.Single(f => f.NpcName == "rat1");
        var rat2 = snapshot.Fights.Single(f => f.NpcName == "rat2");

        Assert.Equal(3, rat1.HealthRung);
        Assert.Equal("seriously injured", rat1.HealthPhrase);
        Assert.Equal(7, rat2.HealthRung);
        Assert.NotNull(rat1.HealthReadUtc);
    }

    /// <summary>Creatures regenerate: the corpus has a thief climbing back from "seriously injured" to
    /// "superficially injured" mid-fight. The panel must report the LATEST reading - latching to the
    /// worst seen would keep promising a kill that is no longer one swing away.</summary>
    [Fact]
    public void Accumulator_ReportsTheLatestReadingNotTheWorst()
    {
        var fight = new FightAccumulator("thief", T0, weaponAtStart: null);

        fight.NoteHealth(3, "seriously injured", T0);
        fight.NoteHealth(6, "superficially injured", T0.AddSeconds(20));

        Assert.Equal(6, fight.HealthRung);
        Assert.Equal("superficially injured", fight.HealthPhrase);
        Assert.Equal(T0.AddSeconds(20), fight.HealthReadUtc);
    }

    // ---- Staleness: the rules that stop an old reading being drawn as a current one ----------

    private static RosterRow Row(int? rung, double? ageSeconds)
        => new("rat1", IsLive: true, IsCurrentTarget: true, FightOutcome.Unresolved,
            rung, "seriously injured", ageSeconds);

    [Fact]
    public void RosterRow_FreshReadingIsUsableAndNotStale()
    {
        var row = Row(3, 1.0);

        Assert.Equal(3, row.UsableHealthRung);
        Assert.False(row.IsHealthStale);
    }

    /// <summary>One missed tick is ordinary - 68% of miss-streaks in the corpus are exactly one - so
    /// the ladder must not start fading at the first gap or it would flicker through every fight.</summary>
    [Fact]
    public void RosterRow_SurvivesOneMissedTickWithoutFading()
    {
        Assert.False(Row(3, 2.5).IsHealthStale);
    }

    [Fact]
    public void RosterRow_FadesAtThreeTicks()
    {
        var row = Row(3, RosterRow.StaleAfterSeconds);

        Assert.Equal(3, row.UsableHealthRung);
        Assert.True(row.IsHealthStale);
    }

    [Fact]
    public void RosterRow_DiscardsTheReadingAtFiveTicks()
    {
        var row = Row(3, RosterRow.UnknownAfterSeconds);

        Assert.Null(row.UsableHealthRung);
        Assert.False(row.IsHealthStale);   // nothing to fade - it reads as unknown instead
    }

    [Fact]
    public void RosterRow_NeverReportedIsUnknownNotFull()
    {
        var row = new RosterRow("rat1", IsLive: true, IsCurrentTarget: true, FightOutcome.Unresolved);

        Assert.Null(row.UsableHealthRung);
    }

    [Fact]
    public void Roster_CarriesHealthAndDamageThroughToTheRows()
    {
        var plan = ParticipantRoster.Build(
        [
            new ParticipantFact("rat1", false, FightOutcome.Unresolved, 3, "seriously injured", 1.0, 12.0),
            new ParticipantFact("rat2", false, FightOutcome.Unresolved, 7, "fit", 4.0, 30.0),
        ]);

        Assert.Equal(3, plan.Rows[0].UsableHealthRung);
        Assert.Equal("seriously injured", plan.Rows[0].HealthPhrase);
        Assert.Equal(12.0, plan.Rows[0].DamageTakenFrom);
        Assert.Equal(30.0, plan.Rows[1].DamageTakenFrom);
    }
}
