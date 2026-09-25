using MudSharp.Combat;
using Mucka.Combat;

namespace Mucka.Util.Tests;

/// <summary>
/// The dead strip folds a creature's flights into the kill that ends it
/// (<see cref="CombatEndingCoalescer"/>): one row, the kill's identity, the engagements' figures
/// combined, one retry per flight.
/// </summary>
public sealed class CombatEndingCoalescerTests
{
    private static readonly DateTime T0 = new(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);

    private static TimeSpan Ticks(double n) => TimeSpan.FromMilliseconds(CombatTiming.TickMilliseconds * n);

    private static ExchangeLine Taken(int samples, double min, double max, double sum)
        => new(samples, min, max, sum / samples, 0, new DamageBracket(sum, sum));

    private static CombatEnding Ending(
        string name, FightOutcome outcome, int second, int encounter = 0, int reset = 0,
        ExchangeLine dealt = default, ExchangeLine taken = default, int? award = null, double ticks = 0)
        => new(name, outcome, T0.AddSeconds(second), encounter, reset, dealt, taken, award, Ticks(ticks));

    [Fact]
    public void AFlightThenAKill_IsOneKillRowWithOneRetry()
    {
        var endings = new[]
        {
            Ending("water-snake1", FightOutcome.CFledFail, 10, encounter: 1),
            Ending("water-snake1", FightOutcome.Kill, 30, encounter: 2, award: 84),
        };

        var row = Assert.Single(CombatEndingCoalescer.Coalesce(endings));

        Assert.Equal(FightOutcome.Kill, row.Outcome);
        Assert.Equal(T0.AddSeconds(30), row.EndedUtc);
        Assert.Equal(2, row.EncounterOrdinal);
        Assert.Equal(1, row.Retries);
        Assert.Equal(84, row.ScoreAwarded);
    }

    [Fact]
    public void EveryFlightInTheRunIsARetry()
    {
        var endings = new[]
        {
            Ending("ram", FightOutcome.Kill, 0),
            Ending("water-snake0", FightOutcome.CFledFail, 10),
            Ending("water-snake0", FightOutcome.CFledFail, 20),
            Ending("water-snake0", FightOutcome.CFled, 30),
            Ending("water-snake0", FightOutcome.CFledFail, 40),
            Ending("water-snake0", FightOutcome.Kill, 50),
        };

        var rows = CombatEndingCoalescer.Coalesce(endings);

        Assert.Equal(2, rows.Count);
        Assert.Equal("ram", rows[0].Name);
        Assert.Equal(0, rows[0].Retries);
        Assert.Equal("water-snake0", rows[1].Name);
        Assert.Equal(4, rows[1].Retries);
    }

    [Fact]
    public void AnotherCreatureBetween_StopsTheRun()
    {
        var endings = new[]
        {
            Ending("water-snake1", FightOutcome.CFledFail, 10),
            Ending("water-snake2", FightOutcome.CFledFail, 20),
            Ending("water-snake1", FightOutcome.Kill, 30),
        };

        var rows = CombatEndingCoalescer.Coalesce(endings);

        Assert.Equal(3, rows.Count);
        Assert.Equal(0, rows[2].Retries);
    }

    [Fact]
    public void APlayerFlightBetween_StopsTheRun()
    {
        var endings = new[]
        {
            Ending("rat9", FightOutcome.CFledFail, 10),
            Ending("rat9", FightOutcome.UFled, 20),
            Ending("rat9", FightOutcome.Kill, 30),
        };

        Assert.Equal(3, CombatEndingCoalescer.Coalesce(endings).Count);
    }

    [Fact]
    public void AResetBetween_StopsTheRun_TheNameMayBeADifferentCreature()
    {
        var endings = new[]
        {
            Ending("rat9", FightOutcome.CFledFail, 10, reset: 0),
            Ending("rat9", FightOutcome.Kill, 30, reset: 1),
        };

        Assert.Equal(2, CombatEndingCoalescer.Coalesce(endings).Count);
    }

    [Fact]
    public void AFlightWithNoKillYet_StaysItsOwnRow()
    {
        var endings = new[]
        {
            Ending("rat9", FightOutcome.Kill, 0),
            Ending("rat8", FightOutcome.CFledFail, 10),
        };

        Assert.Same(endings, CombatEndingCoalescer.Coalesce(endings));
    }

    [Fact]
    public void OnlyAKillAbsorbs()
    {
        var endings = new[]
        {
            Ending("rat9", FightOutcome.CFledFail, 10),
            Ending("rat9", FightOutcome.NoMore, 30),
        };

        Assert.Equal(2, CombatEndingCoalescer.Coalesce(endings).Count);
    }

    [Fact]
    public void FiguresAndPointsCombine()
    {
        var endings = new[]
        {
            Ending("water-snake2", FightOutcome.CFledFail, 10,
                dealt: new ExchangeLine(3, 9, 14, 0, 0, new DamageBracket(15, 30)),
                taken: Taken(2, 6, 15, 21), award: 5, ticks: 4),
            Ending("water-snake2", FightOutcome.Kill, 30,
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
    public void TheRateIsOverTheSummedEngagements_IncludingOnesWhereThatSideLandedNothing()
    {
        var endings = new[]
        {
            // The creature landed nothing in the first engagement; its six ticks still count.
            Ending("rat9", FightOutcome.CFledFail, 10, ticks: 6),
            Ending("rat9", FightOutcome.Kill, 30, taken: Taken(2, 5, 7, 12), ticks: 4),
        };

        var row = Assert.Single(CombatEndingCoalescer.Coalesce(endings));

        Assert.Equal(12 / 10.0, row.Taken.PerTick, 6);
    }

    [Fact]
    public void AnEmptySide_DoesNotDragTheExtremesToZero()
    {
        var endings = new[]
        {
            Ending("rat9", FightOutcome.CFledFail, 10, ticks: 2),
            Ending("rat9", FightOutcome.Kill, 30, taken: Taken(2, 5, 7, 12), ticks: 2),
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
            Ending("rat9", FightOutcome.CFledFail, 10),
            Ending("rat9", FightOutcome.Kill, 30),
        };

        Assert.Null(Assert.Single(CombatEndingCoalescer.Coalesce(endings)).ScoreAwarded);
    }
}
