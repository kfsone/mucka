using MudSharp.Combat;
using Mucka.ViewModels;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// The Combat Rail dead strip's one hard invariant - an ending's row, once drawn, never moves - lives
/// or dies on <see cref="CombatEndingOrder.Sorted"/>. This class is unreachable from SidePanelViewModel
/// (MAUI-dependent) and CombatRailView (an SKCanvasView), so it is pinned here directly.
/// </summary>
public sealed class CombatEndingOrderTests
{
    private static readonly DateTime T0 = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void EngagementOrderIsIgnored_ResolutionOrderWins()
    {
        // rat0 engaged first, rat1 engaged second (so this is the ENGAGEMENT order
        // CombatStatsAggregator.Fights actually produces), but rat1 was killed at +10s and rat0 at
        // +30s. The dead strip must show rat1 above rat0 - oldest ending first - not the
        // engagement order, or rat1's row would move down a line the moment rat0 died.
        var engagementOrder = new[]
        {
            new CombatEnding("rat0", FightOutcome.Kill, T0.AddSeconds(30)),
            new CombatEnding("rat1", FightOutcome.Kill, T0.AddSeconds(10)),
        };

        var sorted = CombatEndingOrder.Sorted(engagementOrder);

        Assert.Equal("rat1", sorted[0].Name);
        Assert.Equal("rat0", sorted[1].Name);
    }

    [Fact]
    public void AlreadySortedInput_IsUnchanged()
    {
        var endings = new[]
        {
            new CombatEnding("rat1", FightOutcome.Kill, T0),
            new CombatEnding("rat0", FightOutcome.Kill, T0.AddSeconds(20)),
        };

        var sorted = CombatEndingOrder.Sorted(endings);

        Assert.Equal("rat1", sorted[0].Name);
        Assert.Equal("rat0", sorted[1].Name);
    }

    [Fact]
    public void ANullEndedUtc_SortsAsTheOldest()
    {
        var endings = new[]
        {
            new CombatEnding("dated", FightOutcome.Kill, T0),
            new CombatEnding("undated", FightOutcome.EndOther, null),
        };

        var sorted = CombatEndingOrder.Sorted(endings);

        Assert.Equal("undated", sorted[0].Name);
        Assert.Equal("dated", sorted[1].Name);
    }

    [Fact]
    public void TiedTimestamps_KeepTheirRelativeInputOrder_Stably()
    {
        // A single KilledByNpc force-resolves every open fight with the SAME timestamp - a real tie,
        // not a hypothetical. A stable sort must not shuffle these between calls: two runs over the
        // identical input must agree, or a row with nothing actually different about it could still
        // appear to move.
        var endings = new[]
        {
            new CombatEnding("first", FightOutcome.Died, T0),
            new CombatEnding("second", FightOutcome.Died, T0),
            new CombatEnding("third", FightOutcome.Died, T0),
        };

        var sortedOnce = CombatEndingOrder.Sorted(endings);
        var sortedAgain = CombatEndingOrder.Sorted(endings);

        Assert.Equal(["first", "second", "third"], sortedOnce.Select(e => e.Name));
        Assert.Equal(sortedOnce.Select(e => e.Name), sortedAgain.Select(e => e.Name));
    }

    [Fact]
    public void EmptyAndSingleElementInputs_AreHandled()
    {
        Assert.Empty(CombatEndingOrder.Sorted(Array.Empty<CombatEnding>()));

        var one = new[] { new CombatEnding("solo", FightOutcome.Kill, T0) };
        Assert.Equal("solo", CombatEndingOrder.Sorted(one)[0].Name);
    }
}
