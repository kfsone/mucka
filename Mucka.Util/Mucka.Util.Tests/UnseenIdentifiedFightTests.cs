using Mucka.Combat;
using MudSharp.Combat;
using MudSharp.Models;

namespace Mucka.Util.Tests;

/// <summary>
/// An invisible player who becomes visible mid-fight is ONE fight on the live display: the blows
/// thrown at "someone" belong to the name that replaces it, which is drawn with a trailing mark.
/// Lines are verbatim from the operator's fight with Kickflip.
/// </summary>
public sealed class UnseenIdentifiedFightTests
{
    private static readonly DateTime T0 = new(2026, 10, 15, 22, 50, 56, DateTimeKind.Utc);

    private static StyledLine Line(string text) => new([new StyledSpan(text, TextStyle.Default)]);

    private static StyledLine FightStartLine(string text) =>
        new([new StyledSpan(text, TextStyle.Default)], kind: LineKind.FightStart);

    private static (CombatTracker Tracker, CombatStatsAggregator Aggregator, List<CombatEvent> Seen) Wired()
    {
        var tracker = new CombatTracker();
        var aggregator = new CombatStatsAggregator();
        var seen = new List<CombatEvent>();
        aggregator.BeginEncounter(T0);
        tracker.EventOccurred += e =>
        {
            seen.Add(e);
            aggregator.Observe(e);
        };
        return (tracker, aggregator, seen);
    }

    private static RosterPlan RosterOf(CombatStatsAggregator aggregator, CombatTracker tracker)
    {
        var fights = aggregator.Snapshot(T0.AddMinutes(1)).Fights;
        var facts = ParticipantFacts.ToParticipantFacts(
            fights, T0.AddMinutes(1), null, null, null, null, null, null, null);
        return ParticipantRoster.Build(facts, tracker.Unseen);
    }

    [Fact]
    public void ASomeoneNamedWhileSighted_IsOneFightUnderTheirName_WithEveryBlowCarriedOver()
    {
        var (tracker, aggregator, seen) = Wired();
        tracker.Observe(FightStartLine("Someone is about to attack you."), T0);
        tracker.Observe(Line("Someone hits you (81/105)."), T0.AddSeconds(1));
        tracker.Observe(Line("You miss someone."), T0.AddSeconds(1));
        tracker.Observe(Line("You hit someone (15-19)."), T0.AddSeconds(2));

        tracker.Observe(Line("Kickflip the necromancer hits you (20/105)."), T0.AddSeconds(5));
        tracker.Observe(Line("You hit Kickflip the necromancer (10-14)."), T0.AddSeconds(5));

        Assert.Equal("Kickflip", Assert.Single(seen, e => e.Kind == CombatEventKind.UnseenNamed).IdentifiedAs);
        var fight = Assert.Single(aggregator.Snapshot(T0.AddSeconds(6)).Fights);
        Assert.Equal("Kickflip", fight.NpcName);
        Assert.Equal("kickflips", fight.NpcGroup);
        Assert.True(fight.WasUnseen);
        Assert.False(fight.IsResolved);
        Assert.Equal(2, fight.YouHits);
        Assert.Equal(1, fight.YouMisses);
        Assert.Equal(2, fight.TheyHits);

        var row = Assert.Single(RosterOf(aggregator, tracker).Rows);
        Assert.Equal("Kickflip", row.Name);
        Assert.Equal("Kickflip*", row.Label);
        Assert.False(row.IsUnseen);
    }

    [Fact]
    public void TheRenamedFight_EndsAsOneRow_WithTheMarkOnTheEnding()
    {
        var (tracker, aggregator, _) = Wired();
        tracker.Observe(FightStartLine("Someone is about to attack you."), T0);
        tracker.Observe(Line("Someone hits you (81/105)."), T0.AddSeconds(1));
        tracker.Observe(Line("Kickflip the necromancer hits you (20/105)."), T0.AddSeconds(5));

        tracker.Observe(Line("You have fled by trying to go out."), T0.AddSeconds(6));

        var fight = Assert.Single(aggregator.Snapshot(T0.AddSeconds(7)).Fights);
        Assert.Equal("Kickflip", fight.NpcName);
        Assert.True(fight.IsResolved);
        Assert.NotEqual(FightOutcome.Named, fight.Outcome);
    }

    [Fact]
    public void AWordThatStoodForTwoOpponents_IsNotRenamed_AndTheNameOpensItsOwnFight()
    {
        var (tracker, aggregator, seen) = Wired();
        tracker.NoteCannotSee(true, T0, "blinded");
        tracker.Observe(FightStartLine("Someone is about to attack you."), T0.AddSeconds(1));
        tracker.Observe(FightStartLine("Someone is about to attack you."), T0.AddSeconds(2));
        tracker.Observe(Line("Someone hits you (81/105)."), T0.AddSeconds(3));
        tracker.Observe(Line("You have killed someone."), T0.AddSeconds(4));
        tracker.NoteCannotSee(false, T0.AddSeconds(5), "sight returned");

        tracker.Observe(Line("Kickflip the necromancer hits you (20/105)."), T0.AddSeconds(6));

        var named = Assert.Single(seen, e => e.Kind == CombatEventKind.UnseenNamed);
        Assert.Null(named.IdentifiedAs);
        var fights = aggregator.Snapshot(T0.AddSeconds(7)).Fights;
        Assert.Equal(2, fights.Count);
        Assert.False(fights.Single(f => f.NpcName == "someone").WasUnseen);
        Assert.False(fights.Single(f => f.NpcName == "Kickflip").WasUnseen);
    }
}
