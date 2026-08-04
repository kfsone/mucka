using Mucka.ViewModels;
using MudSharp.Combat;

namespace mudsharp.Tests.Fixtures;

public sealed class CombatHistoryFormatterTests
{
    private static FightSnapshot Snap(
        string npcName = "rat0",
        string? weapon = "axe0",
        int youHits = 3,
        int youMisses = 1,
        int theyHits = 1,
        int theyMisses = 3,
        double damageDone = 30,
        double damageTaken = 6,
        int durationSeconds = 52,
        FightOutcome outcome = FightOutcome.Unresolved)
        => new(npcName, NpcGroups.Normalize(npcName), weapon, youHits, youMisses, theyHits, theyMisses,
            damageDone, damageTaken, TimeSpan.FromSeconds(durationSeconds), outcome,
            outcome != FightOutcome.Unresolved);

    private static FightRecord Record(
        string npcName = "rat0",
        string? weapon = "axe0",
        FightOutcome outcome = FightOutcome.Killed,
        double damageDone = 32,
        int youHits = 4)
        => new()
        {
            NpcName = npcName,
            NpcGroup = NpcGroups.Normalize(npcName),
            WeaponUsed = weapon,
            Outcome = outcome.ToString(),
            YouHits = youHits,
            YouMisses = 2,
            TheyHits = 2,
            TheyMisses = 4,
            ApproxDamageDone = damageDone,
            ApproxDamageTaken = 9,
            DurationMs = 64_000,
        };

    [Fact]
    public void FormatFightRows_IsEmptyForASingleNpcEncounter()
    {
        // The totals above the block already describe a one-target fight completely; repeating them
        // as a single "per fight" row would be pure noise.
        Assert.Equal(string.Empty, CombatHistoryFormatter.FormatFightRows([Snap()]));
        Assert.Equal(string.Empty, CombatHistoryFormatter.FormatFightRows([]));
    }

    [Fact]
    public void FormatFightRows_ShowsOneAlignedRowPerNpcWithItsOutcome()
    {
        var rows = CombatHistoryFormatter.FormatFightRows(
        [
            Snap("goat0", youHits: 5, youMisses: 2, damageDone: 28, damageTaken: 11, outcome: FightOutcome.Killed),
            Snap("ram1", youHits: 2, youMisses: 2, damageDone: 13.5, damageTaken: 4),
        ]);

        var lines = rows.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Contains("goat0", lines[0]);
        Assert.Contains("5h/2m", lines[0]);
        Assert.Contains("28.0 dealt", lines[0]);
        Assert.Contains("11.0 taken", lines[0]);
        Assert.EndsWith("kill", lines[0]);
        Assert.Contains("ram1", lines[1]);
        Assert.EndsWith("live", lines[1]);   // still going
        // Names are padded to a common width so the columns line up in the monospace label.
        Assert.Equal(lines[0].IndexOf("5h/2m", StringComparison.Ordinal), lines[1].IndexOf("2h/2m", StringComparison.Ordinal));
    }

    [Fact]
    public void FormatHistoryHeader_AlwaysLeadsWithTheSampleSize()
    {
        // The count is the reader's only cue for how much weight the medians deserve, so it is never
        // omitted or buried.
        var summary = FightHistory.Summarize([Record(), Record(), Record()], "rats");

        Assert.Equal("rats: 3 prior fights", CombatHistoryFormatter.FormatHistoryHeader("rats", summary));
    }

    [Fact]
    public void FormatHistoryHeader_CallsOutWhenOnlySomeRowsCarryDetail()
    {
        // Narrative-mode rows count as fights but cannot inform a median; saying so prevents the
        // reader trusting a median that rests on fewer samples than the headline count implies.
        var records = new[]
        {
            Record(),
            new FightRecord { NpcGroup = "rats", Outcome = nameof(FightOutcome.Killed), NarrativeMode = true },
        };

        var header = CombatHistoryFormatter.FormatHistoryHeader("rats", FightHistory.Summarize(records, "rats"));

        Assert.Equal("rats: 2 prior fights, 1 with detail", header);
    }

    [Fact]
    public void FormatHistoryHeader_SaysSoWhenThereIsNoHistoryAtAll()
    {
        var header = CombatHistoryFormatter.FormatHistoryHeader("rats", FightHistorySummary.Empty);
        Assert.Equal("rats: no prior fights on record", header);
    }

    [Fact]
    public void FormatHistoryRows_ContrastsTheLiveFigureAgainstTheMedian()
    {
        var summary = FightHistory.Summarize([Record(damageDone: 32), Record(damageDone: 32)], "rats");

        var rows = CombatHistoryFormatter.FormatHistoryRows(Snap(damageDone: 30), summary, []);

        Assert.Contains("dmg dealt", rows);
        Assert.Contains("30.0 now", rows);
        Assert.Contains("32.0 med", rows);
    }

    [Fact]
    public void FormatHistoryRows_StatesThePoolEstimateAndTheKillCountItRestsOn()
    {
        // The estimate is a derived guess, not an observation — MUD2 never reports NPC stamina — so
        // the number of kills behind it is shown alongside it rather than hidden.
        var summary = FightHistory.Summarize([Record(damageDone: 30), Record(damageDone: 34)], "rats");

        var rows = CombatHistoryFormatter.FormatHistoryRows(Snap(), summary, []);

        Assert.Contains("pool est   ~32.0 (from 2 kills)", rows);
    }

    [Fact]
    public void FormatHistoryRows_SaysWhyThePoolEstimateIsMissingInsteadOfShowingZero()
    {
        var summary = FightHistory.Summarize([Record(outcome: FightOutcome.YouFled)], "rats");

        var rows = CombatHistoryFormatter.FormatHistoryRows(Snap(), summary, []);

        Assert.Contains("pool est   -- (no kills on record)", rows);
        Assert.DoesNotContain("~0.0", rows);
    }

    [Fact]
    public void FormatHistoryRows_RendersAbsentMediansAsDashesNotZeros()
    {
        // "no samples" and "measured zero" must never look the same. A history of one narrative-mode
        // fight has an outcome but no medians at all.
        var records = new[]
        {
            new FightRecord { NpcGroup = "rats", Outcome = nameof(FightOutcome.Killed), NarrativeMode = true },
        };

        var rows = CombatHistoryFormatter.FormatHistoryRows(Snap(), FightHistory.Summarize(records, "rats"), []);

        Assert.Contains("-- med", rows);
        Assert.DoesNotContain("0.0 med", rows);
    }

    [Fact]
    public void FormatHistoryRows_ListsOutcomesAndOnlyTheOnesThatHappened()
    {
        var records = new[]
        {
            Record(outcome: FightOutcome.Killed),
            Record(outcome: FightOutcome.Killed),
            Record(outcome: FightOutcome.KilledByNpc),
        };

        var rows = CombatHistoryFormatter.FormatHistoryRows(Snap(), FightHistory.Summarize(records, "rats"), []);

        Assert.Contains("killed 2/3", rows);
        Assert.Contains("you died 1", rows);
        Assert.DoesNotContain("withdrew", rows);   // never happened, so never mentioned
        Assert.DoesNotContain("you fled", rows);
    }

    [Fact]
    public void FormatHistoryRows_IncludesThePerWeaponBreakdown()
    {
        var records = new[]
        {
            Record(weapon: "axe0", damageDone: 40, youHits: 4),
            Record(weapon: "axe0", damageDone: 40, youHits: 4),
            Record(weapon: "dagger0", damageDone: 12, youHits: 4),
        };

        var rows = CombatHistoryFormatter.FormatHistoryRows(
            Snap(), FightHistory.Summarize(records, "rats"), FightHistory.SummarizeByWeapon(records, "rats"));

        Assert.Contains("-- by weapon --", rows);
        Assert.Contains("axe0", rows);
        Assert.Contains("10.0/hit", rows);   // 40 damage over 4 hits
        Assert.Contains("dagger0", rows);
        Assert.Contains("3.0/hit", rows);
    }

    [Fact]
    public void FormatHistoryRows_IsEmptyWithNoHistory()
        => Assert.Equal(string.Empty, CombatHistoryFormatter.FormatHistoryRows(Snap(), FightHistorySummary.Empty, []));

    [Fact]
    public void FormatHistoryRows_StillShowsMediansWhenThereIsNoLiveFightYet()
    {
        // Reached during the post-kill grace window, when the encounter has closed but the block is
        // still on screen.
        var summary = FightHistory.Summarize([Record(damageDone: 32)], "rats");

        var rows = CombatHistoryFormatter.FormatHistoryRows(null, summary, []);

        Assert.Contains("median 32.0", rows);
        Assert.DoesNotContain("now vs", rows);
    }
}
