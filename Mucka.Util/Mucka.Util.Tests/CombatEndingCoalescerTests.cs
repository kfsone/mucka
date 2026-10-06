using MudSharp.Combat;
using Mucka.Combat;

namespace Mucka.Util.Tests;

/// <summary>
/// The dead strip folds an ending into the row directly above it
/// (<see cref="CombatEndingCoalescer"/>) when that row ended in a flight, the new ending is a flight or
/// a kill, both carry the same creature id and it is not an anonymous word, both belong to one known
/// persona session with no reset line between them, and the two are at most
/// <see cref="CombatEndingCoalescer.WindowSeconds"/> apart. Nothing else decides it.
/// </summary>
public sealed class CombatEndingCoalescerTests
{
    private static readonly DateTime T0 = new(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);

    private const string Npc1 = "water-snake1";
    private const string Npc2 = "rat9";

    private static readonly string[] AnonymousWords =
    [
        AnonymousOpponent.Person, AnonymousOpponent.Thing,
        AnonymousOpponent.PersonAsSubject, AnonymousOpponent.ThingAsSubject,
    ];

    private static readonly FightOutcome[] Outcomes = Enum.GetValues<FightOutcome>();

    private static readonly int Window = (int)CombatEndingCoalescer.WindowSeconds;

    private static TimeSpan Ticks(double n) => TimeSpan.FromMilliseconds(CombatTiming.TickMilliseconds * n);

    private static ExchangeLine Taken(int samples, double min, double max, double sum)
        => new(samples, min, max, sum / samples, 0, new DamageBracket(sum, sum));

    private static CombatEnding Ending(
        string name, FightOutcome outcome, double second, int encounter = 0, int reset = 0,
        ExchangeLine dealt = default, ExchangeLine taken = default, int? award = null, double ticks = 0,
        long? persona = 1)
        => new(name, outcome, T0.AddSeconds(second), encounter, reset, dealt, taken, award, Ticks(ticks),
            PersonaSessionId: persona);

    private static bool IsFlight(FightOutcome o)
        => o is FightOutcome.CFled or FightOutcome.CFledFail or FightOutcome.UFled or FightOutcome.UFledFail;

    /// <summary>The rule, written out independently of the code under test.</summary>
    private static bool Expected(FightOutcome previous, FightOutcome next, int gapSeconds)
        => IsFlight(previous) && (IsFlight(next) || next == FightOutcome.Kill)
            && gapSeconds >= 0 && gapSeconds <= Window;

    // -- The rule, exhaustively ----------------------------------------------------

    [Theory]
    [InlineData(Npc1)]
    [InlineData(Npc2)]
    public void SameName_FollowsTheRule_ForEveryOutcomePairAndEverySecondToTheWindowAndOneBeyond(string name)
    {
        foreach (var previous in Outcomes)
            foreach (var next in Outcomes)
                for (var gap = 0; gap <= Window + 1; gap++)
                {
                    var actual = CombatEndingCoalescer.ShouldCoalesce(
                        Ending(name, previous, 100), Ending(name, next, 100 + gap));
                    Assert.True(Expected(previous, next, gap) == actual,
                        $"{name}: {previous} then {next} {gap}s later -> {actual}");
                }
    }

    [Theory]
    [InlineData(Npc1, Npc2)]
    [InlineData(Npc2, Npc1)]
    public void DifferentNames_NeverCoalesce(string previousName, string nextName)
    {
        foreach (var previous in Outcomes)
            foreach (var next in Outcomes)
                for (var gap = 0; gap <= Window + 1; gap++)
                    Assert.False(CombatEndingCoalescer.ShouldCoalesce(
                        Ending(previousName, previous, 100), Ending(nextName, next, 100 + gap)),
                        $"{previousName} {previous} then {nextName} {next} {gap}s later");
    }

    public static TheoryData<string, string> AnonymousPairs()
    {
        var data = new TheoryData<string, string>();
        foreach (var word in AnonymousWords)
        {
            data.Add(word, Npc1);
            data.Add(Npc1, word);
            foreach (var other in AnonymousWords)
                data.Add(word, other);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(AnonymousPairs))]
    public void AnAnonymousWordInEitherPlace_NeverCoalesces(string previousName, string nextName)
    {
        foreach (var previous in Outcomes)
            foreach (var next in Outcomes)
                for (var gap = 0; gap <= Window + 1; gap++)
                    Assert.False(CombatEndingCoalescer.ShouldCoalesce(
                        Ending(previousName, previous, 100), Ending(nextName, next, 100 + gap)),
                        $"{previousName} {previous} then {nextName} {next} {gap}s later");
    }

    [Fact]
    public void TheIdMustMatchExactly()
        => Assert.False(CombatEndingCoalescer.ShouldCoalesce(
            Ending("Water-snake1", FightOutcome.CFled, 0), Ending("water-snake1", FightOutcome.Kill, 10)));

    [Fact]
    public void TheWindowIsInclusiveToTheSecondAndNotAMillisecondMore()
    {
        var flight = Ending(Npc1, FightOutcome.CFled, 0);
        Assert.True(CombatEndingCoalescer.ShouldCoalesce(flight, Ending(Npc1, FightOutcome.Kill, Window)));
        Assert.False(CombatEndingCoalescer.ShouldCoalesce(flight, Ending(Npc1, FightOutcome.Kill, Window + 0.001)));
    }

    [Fact]
    public void ANextEndingEarlierThanThePrevious_DoesNotCoalesce()
        => Assert.False(CombatEndingCoalescer.ShouldCoalesce(
            Ending(Npc1, FightOutcome.CFled, 10), Ending(Npc1, FightOutcome.Kill, 9)));

    [Fact]
    public void AResetLineBetween_NeverCoalesces()
    {
        foreach (var previous in Outcomes)
            foreach (var next in Outcomes)
                Assert.False(CombatEndingCoalescer.ShouldCoalesce(
                    Ending(Npc1, previous, 0, reset: 3), Ending(Npc1, next, 10, reset: 4)),
                    $"{previous} then {next}");
    }

    [Fact]
    public void AnEncounterLineBetween_DoesNotStopIt()
        => Assert.True(CombatEndingCoalescer.ShouldCoalesce(
            Ending(Npc1, FightOutcome.CFled, 0, encounter: 1), Ending(Npc1, FightOutcome.Kill, 10, encounter: 2)));

    [Fact]
    public void AnotherPersonaSession_NeverCoalesces()
    {
        foreach (var previous in Outcomes)
            foreach (var next in Outcomes)
            {
                Assert.False(CombatEndingCoalescer.ShouldCoalesce(
                    Ending(Npc1, previous, 0, persona: 1), Ending(Npc1, next, 10, persona: 2)),
                    $"{previous} then {next}");
                Assert.False(CombatEndingCoalescer.ShouldCoalesce(
                    Ending(Npc1, previous, 0, persona: 1), Ending(Npc1, next, 10, persona: null)),
                    $"{previous} then {next}, no session");
                Assert.False(CombatEndingCoalescer.ShouldCoalesce(
                    Ending(Npc1, previous, 0, persona: null), Ending(Npc1, next, 10, persona: null)),
                    $"{previous} then {next}, neither session known");
            }
    }

    [Fact]
    public void AnEndingWithNoTimestamp_NeverCoalesces()
    {
        var flight = Ending(Npc1, FightOutcome.CFled, 0);
        var kill = Ending(Npc1, FightOutcome.Kill, 10);
        Assert.False(CombatEndingCoalescer.ShouldCoalesce(flight with { EndedUtc = null }, kill));
        Assert.False(CombatEndingCoalescer.ShouldCoalesce(flight, kill with { EndedUtc = null }));
    }

    /// <summary>Every field the rule does not name, varied on either side.</summary>
    private static IEnumerable<Func<CombatEnding, CombatEnding>> IrrelevantChanges()
    {
        yield return e => e with { EncounterOrdinal = e.EncounterOrdinal + 7 };
        yield return e => e with { Dealt = new ExchangeLine(3, 9, 14, 0, 0, new DamageBracket(15, 30)) };
        yield return e => e with { Taken = Taken(2, 6, 15, 21) };
        yield return e => e with { ScoreAwarded = -102 };
        yield return e => e with { ScoreAwarded = 84 };
        yield return e => e with { Duration = Ticks(40) };
        yield return e => e with { Retries = 5 };
    }

    [Fact]
    public void FieldsTheRuleDoesNotName_ChangeNothing()
    {
        foreach (var previous in Outcomes)
            foreach (var next in Outcomes)
                foreach (var gap in new[] { 0, Window, Window + 1 })
                {
                    var before = Ending(Npc1, previous, 100);
                    var after = Ending(Npc1, next, 100 + gap);
                    var expected = Expected(previous, next, gap);
                    foreach (var change in IrrelevantChanges())
                    {
                        Assert.Equal(expected, CombatEndingCoalescer.ShouldCoalesce(change(before), after));
                        Assert.Equal(expected, CombatEndingCoalescer.ShouldCoalesce(before, change(after)));
                    }
                }
    }

    // -- Folding a list --------------------------------------------------------------

    [Fact]
    public void NothingFolds_TheInputComesBackAsIs()
    {
        var endings = new[]
        {
            Ending(Npc2, FightOutcome.Kill, 0),
            Ending(Npc1, FightOutcome.CFledFail, 10),
        };

        Assert.Same(endings, CombatEndingCoalescer.Coalesce(endings));
    }

    [Fact]
    public void TheMergedRowIsTheLaterEnding_WithARetryForTheRowItTookIn()
    {
        var endings = new[]
        {
            Ending(Npc1, FightOutcome.CFledFail, 10, encounter: 1),
            Ending(Npc1, FightOutcome.Kill, 30, encounter: 2, award: 84),
        };

        var row = Assert.Single(CombatEndingCoalescer.Coalesce(endings));

        Assert.Equal(FightOutcome.Kill, row.Outcome);
        Assert.Equal(T0.AddSeconds(30), row.EndedUtc);
        Assert.Equal(2, row.EncounterOrdinal);
        Assert.Equal(1, row.Retries);
        Assert.Equal(84, row.ScoreAwarded);
    }

    [Fact]
    public void ARunChains_EachStepInsideTheWindowThoughTheWholeIsNot()
    {
        var endings = new[]
        {
            Ending(Npc2, FightOutcome.Kill, 0),
            Ending(Npc1, FightOutcome.CFledFail, 10),
            Ending(Npc1, FightOutcome.UFledFail, 10 + Window),
            Ending(Npc1, FightOutcome.CFled, 10 + 2 * Window),
            Ending(Npc1, FightOutcome.UFled, 10 + 3 * Window),
            Ending(Npc1, FightOutcome.Kill, 10 + 4 * Window),
        };

        var rows = CombatEndingCoalescer.Coalesce(endings);

        Assert.Equal(2, rows.Count);
        Assert.Equal(Npc2, rows[0].Name);
        Assert.Equal(0, rows[0].Retries);
        Assert.Equal(Npc1, rows[1].Name);
        Assert.Equal(FightOutcome.Kill, rows[1].Outcome);
        Assert.Equal(4, rows[1].Retries);
    }

    [Fact]
    public void ARunOfFlightsWithNoKill_IsOneRowEndingInTheLastFlight()
    {
        var endings = new[]
        {
            Ending(Npc1, FightOutcome.CFledFail, 10),
            Ending(Npc1, FightOutcome.UFledFail, 20),
        };

        var row = Assert.Single(CombatEndingCoalescer.Coalesce(endings));

        Assert.Equal(FightOutcome.UFledFail, row.Outcome);
        Assert.Equal(1, row.Retries);
    }

    [Fact]
    public void AKillEndsTheRun_WhatFollowsStartsANewRow()
    {
        var endings = new[]
        {
            Ending(Npc1, FightOutcome.CFled, 10),
            Ending(Npc1, FightOutcome.Kill, 20),
            Ending(Npc1, FightOutcome.CFled, 30),
            Ending(Npc1, FightOutcome.Kill, 40),
        };

        var rows = CombatEndingCoalescer.Coalesce(endings);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(1, r.Retries));
    }

    [Fact]
    public void OnlyTheRowDirectlyAboveIsConsidered()
    {
        var endings = new[]
        {
            Ending(Npc1, FightOutcome.CFled, 10),
            Ending(Npc2, FightOutcome.Kill, 20),
            Ending(Npc1, FightOutcome.Kill, 30),
        };

        var rows = CombatEndingCoalescer.Coalesce(endings);

        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal(0, r.Retries));
    }

    [Fact]
    public void FiguresAndPointsCombine()
    {
        var endings = new[]
        {
            Ending(Npc1, FightOutcome.CFledFail, 10,
                dealt: new ExchangeLine(3, 9, 14, 0, 0, new DamageBracket(15, 30)),
                taken: Taken(2, 6, 15, 21), award: 5, ticks: 4),
            Ending(Npc1, FightOutcome.Kill, 30,
                dealt: new ExchangeLine(2, 5, 20, 0, 0, new DamageBracket(20, 35)),
                taken: Taken(1, 4, 4, 4), award: 86, ticks: 6),
        };

        var row = Assert.Single(CombatEndingCoalescer.Coalesce(endings));

        Assert.Equal(5, row.Dealt.Samples);
        Assert.Equal(new DamageBracket(35, 65), row.Dealt.Total);
        Assert.Equal(5, row.Dealt.Min);
        Assert.Equal(20, row.Dealt.Max);
        Assert.Equal((35 + 65) / 10.0, row.Dealt.Mean);
        Assert.Equal(3, row.Taken.Samples);
        Assert.Equal(new DamageBracket(25, 25), row.Taken.Total);
        Assert.Equal(4, row.Taken.Min);
        Assert.Equal(15, row.Taken.Max);
        Assert.Equal(91, row.ScoreAwarded);
        Assert.Equal(Ticks(10), row.Duration);
    }

    [Fact]
    public void AFlightChargeAndAKillAwardSum()
    {
        var endings = new[]
        {
            Ending(Npc1, FightOutcome.UFledFail, 10, award: -102),
            Ending(Npc1, FightOutcome.Kill, 30, award: 84),
        };

        Assert.Equal(-18, Assert.Single(CombatEndingCoalescer.Coalesce(endings)).ScoreAwarded);
    }

    [Fact]
    public void TheRateIsOverTheSummedEngagements_IncludingOnesWhereThatSideLandedNothing()
    {
        var endings = new[]
        {
            // The creature landed nothing in the first engagement; its six ticks still count.
            Ending(Npc2, FightOutcome.CFledFail, 10, ticks: 6),
            Ending(Npc2, FightOutcome.Kill, 30, taken: Taken(2, 5, 7, 12), ticks: 4),
        };

        var row = Assert.Single(CombatEndingCoalescer.Coalesce(endings));

        Assert.Equal(12 / 10.0, row.Taken.PerTick, 6);
    }

    [Fact]
    public void AnEmptySide_DoesNotDragTheExtremesToZero()
    {
        var endings = new[]
        {
            Ending(Npc2, FightOutcome.CFledFail, 10, ticks: 2),
            Ending(Npc2, FightOutcome.Kill, 30, taken: Taken(2, 5, 7, 12), ticks: 2),
        };

        var row = Assert.Single(CombatEndingCoalescer.Coalesce(endings));

        Assert.Equal(5, row.Taken.Min);
        Assert.Equal(7, row.Taken.Max);
        Assert.False(row.Dealt.HasSamples);
    }

    [Fact]
    public void NoAwardAnywhere_StaysNull()
    {
        var endings = new[]
        {
            Ending(Npc2, FightOutcome.CFledFail, 10),
            Ending(Npc2, FightOutcome.Kill, 30),
        };

        Assert.Null(Assert.Single(CombatEndingCoalescer.Coalesce(endings)).ScoreAwarded);
    }
}
