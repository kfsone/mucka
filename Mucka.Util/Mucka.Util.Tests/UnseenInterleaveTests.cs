using Mucka.Combat;
using MudSharp.Combat;
using MudSharp.Models;

namespace Mucka.Util.Tests;

/// <summary>
/// Interleavings of named and anonymous opponents through the tracker and the aggregator, under the
/// rules of docs/unseen-presentation.md: an anonymous line never credits a name and goes to the one
/// unseen group; a named line is its Creature's; anonymous lines and vision signals park named rows,
/// and only a named swing promotes one. Each script is a list of steps: "S text" is a line coded as
/// a fight start, "I text" a line coded as a creature turning invisible, "L text" an ordinary line,
/// "dark"/"light" the player losing and regaining sight. The expectation is the blows each fight
/// took ("name:theirHits"), in the order the fights opened, fights with no blows left out, then the
/// unseen count and the rows parked in the group.
/// </summary>
public sealed class UnseenInterleaveTests
{
    private static readonly DateTime T0 = new(2026, 10, 15, 22, 50, 56, DateTimeKind.Utc);

    private static StyledLine Line(string text, LineKind kind = LineKind.Normal) =>
        new([new StyledSpan(text, TextStyle.Default)], kind: kind);

    private static string Play(string[] steps)
    {
        var tracker = new CombatTracker();
        var aggregator = new CombatStatsAggregator();
        aggregator.BeginEncounter(T0);
        tracker.EventOccurred += aggregator.Observe;

        for (var i = 0; i < steps.Length; i++)
        {
            var at = T0.AddSeconds(i);
            var step = steps[i];
            if (step == "dark")
                tracker.NoteCannotSee(true, at, "test");
            else if (step == "light")
                tracker.NoteCannotSee(false, at, "test");
            else if (step.StartsWith("S ", StringComparison.Ordinal))
                tracker.Observe(Line(step[2..], LineKind.FightStart), at);
            else if (step.StartsWith("I ", StringComparison.Ordinal))
                tracker.Observe(Line(step[2..], LineKind.CreatureInvisible), at);
            else if (step.StartsWith("L ", StringComparison.Ordinal))
                tracker.Observe(Line(step[2..]), at);
            else
                throw new ArgumentException($"unknown step '{step}'");
        }

        var snapshot = aggregator.Snapshot(T0.AddSeconds(steps.Length));
        var rows = snapshot.Fights
            .Where(f => f.TheyHits > 0)
            .Select(f => $"{(AnonymousOpponent.IsGroup(f.NpcName) ? AnonymousOpponent.GroupLabel : f.NpcName)}:{f.TheyHits}");
        var unseen = snapshot.Unseen ?? UnseenSnapshot.Empty;
        var parked = unseen.Parked.Count > 0 ? " parked=" + string.Join(",", unseen.Parked) : "";
        return $"{string.Join(" ", rows)} | unseen={unseen.Count}{parked}";
    }

    public static TheoryData<string, string[], string> Scenarios => new()
    {
        {
            "Everyone visible: named blows stay on their names",
            ["S The man is moving towards you ferociously.", "S The rat1 is moving towards you ferociously.",
             "L The man hits you (90/100).", "L The rat1 hits you (85/100)."],
            "man:1 rat1:1 | unseen=0"
        },
        {
            "Dark falls on two announced opponents: every blow is the group's, an anonymous kill is placed on nobody",
            ["S The man is moving towards you ferociously.", "S The rat1 is moving towards you ferociously.",
             "dark", "L Someone hits you (90/100).", "L Something hits you (85/100).",
             "L You have killed something."],
            "unseen:2 | unseen=1 parked=man,rat1"
        },
        {
            "A second opponent announces itself as someone: the rat, a seeded something, is not parked; the man's own line promotes him",
            ["S The man is moving towards you ferociously.", "S The rat1 is moving towards you ferociously.",
             "S Someone is about to attack you.", "L Someone hits you (90/100).", "L The man hits you (80/100)."],
            "man:1 unseen:1 | unseen=1"
        },
        {
            "Two invisible opponents announce themselves: neither blow is anyone's",
            ["S The man is moving towards you ferociously.", "S Someone is about to attack you.",
             "L Someone hits you (90/100).", "S Someone is about to attack you.", "L Someone hits you (80/100)."],
            "unseen:2 | unseen=3 parked=man"
        },
        {
            "One invisible opponent, named on a swing: it spends the slot, and its earlier blow stays the group's",
            ["S Someone is about to attack you.", "L Someone hits you (90/100).", "L The man hits you (80/100)."],
            "unseen:1 man:1 | unseen=0"
        },
        {
            "Two invisible opponents, then a creature is named on a swing: one slot is spent",
            ["S Someone is about to attack you.", "L Someone hits you (90/100).", "S Someone is about to attack you.",
             "L The man hits you (80/100)."],
            "unseen:1 man:1 | unseen=1"
        },
        {
            "An announced creature that later hits by name spends no slot",
            ["S Someone is about to attack you.", "L Someone hits you (90/100).",
             "S The man is moving towards you ferociously.", "L The man hits you (80/100)."],
            "unseen:1 man:1 | unseen=1"
        },
        {
            "Hunted invisible man: attacked as someone, named on the first swing after",
            ["S You attack someone.", "L You miss someone.", "L Someone hits you (94/105).",
             "L You hit the man (10-14).", "L The man hits you (62/105)."],
            "unseen:1 man:1 | unseen=0"
        },
        {
            "One someone and one something open: a name of unknown class spends one of them",
            ["S Someone is about to attack you.", "S Something is about to attack you.",
             "L Someone hits you (90/100).", "L The rat1 hits you (80/100)."],
            "unseen:1 rat1:1 | unseen=1"
        },
        {
            "A creature that fades is parked; the anonymous blow after it is the group's",
            ["S The man is moving towards you ferociously.", "L The man hits you (90/100).",
             "I The man fades from view.", "L Someone hits you (80/100)."],
            "man:1 unseen:1 | unseen=1 parked=man"
        },
        {
            "A creature fades, then a second invisible one announces itself",
            ["S The man is moving towards you ferociously.", "L The man hits you (90/100).",
             "I The man fades from view.", "S Someone is about to attack you.", "L Someone hits you (80/100)."],
            "man:1 unseen:1 | unseen=2 parked=man"
        },
        {
            "A visibility line promotes nothing; the named swing after it spends the slot",
            ["S Someone is about to attack you.", "L Someone hits you (90/100).",
             "L The man has regained his visibleness!", "L The man hits you (80/100)."],
            "unseen:1 man:1 | unseen=0"
        },
        {
            "A known someone is not parked by a something: the thief keeps his row",
            ["S The thief is moving towards you ferociously.", "S Something is about to attack you.",
             "L Something hits you (90/100).", "L The thief hits you (80/100)."],
            "thief:1 unseen:1 | unseen=1"
        },
        {
            "A blow from nobody announced opens a slot for it",
            ["L Someone hits you (90/100)."],
            "unseen:1 | unseen=1"
        },
    };

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Scenario(string name, string[] steps, string expected)
    {
        var actual = Play(steps);
        Assert.True(expected == actual, $"{name}\n  expected: {expected}\n  actual:   {actual}");
    }
}
