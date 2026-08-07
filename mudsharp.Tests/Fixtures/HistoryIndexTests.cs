using MudSharp.Combat;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// HistoryIndex is the incremental replacement for FightHistory's "filter then scan the whole
/// corpus" approach (DESIGN_FINAL.md section 7.3). These tests mirror FightHistoryTests' own
/// fixtures and assertions closely on purpose: the two must agree on every figure, since
/// IncrementalFightBucket.Insert is written to be a line-for-line match of FightHistory.Summarize's
/// per-record logic - any drift between them would mean the live incremental path and the
/// offline/test corpus-scan path silently disagree about the same data.
/// </summary>
public sealed class HistoryIndexTests
{
    private static FightRecord Fight(
        string npcName = "rat0",
        string? weapon = "dagger0",
        FightOutcome outcome = FightOutcome.Killed,
        int youHits = 3,
        int youMisses = 1,
        int theyHits = 1,
        int theyMisses = 3,
        double damageDone = 30,
        double damageTaken = 6,
        long durationMs = 60_000,
        bool narrative = false)
        => new()
        {
            NpcName = npcName,
            NpcGroup = NpcGroups.Normalize(npcName),
            WeaponUsed = weapon,
            Outcome = outcome.ToString(),
            YouHits = youHits,
            YouMisses = youMisses,
            TheyHits = theyHits,
            TheyMisses = theyMisses,
            ApproxDamageDone = damageDone,
            ApproxDamageTaken = damageTaken,
            DurationMs = durationMs,
            NarrativeMode = narrative,
        };

    [Fact]
    public void Insert_ThenGetGroupSummary_MatchesFightHistorySummarizeExactly()
    {
        var records = new[]
        {
            Fight(damageDone: 30),
            Fight(damageDone: 32),
            Fight(damageDone: 34),
            Fight(damageDone: 300),
        };

        var index = new HistoryIndex();
        foreach (var record in records)
            index.Insert(record);

        var incremental = index.GetGroupSummary("rats");
        var scanned = FightHistory.Summarize(records, "rats");

        Assert.Equal(scanned.SampleSize, incremental.SampleSize);
        Assert.Equal(scanned.FightCount, incremental.FightCount);
        Assert.Equal(scanned.MedianDamageDone, incremental.MedianDamageDone);
    }

    [Fact]
    public void Insert_OrderIndependent_MedianMatchesRegardlessOfInsertOrder()
    {
        // Binary-search insertion must keep the list sorted no matter what order fights arrive in -
        // unlike a scan (which sorts once at the end), an incremental structure that got this wrong
        // would only show up as a wrong median, never a crash.
        var records = new[]
        {
            Fight(damageDone: 300),
            Fight(damageDone: 30),
            Fight(damageDone: 34),
            Fight(damageDone: 32),
        };

        var index = new HistoryIndex();
        foreach (var record in records)
            index.Insert(record);

        Assert.Equal(33.0, index.GetGroupSummary("rats").MedianDamageDone!.Value, 3);
    }

    [Fact]
    public void GetInstanceSummary_IsScopedToTheExactNpcNameNotTheGroup()
    {
        var index = new HistoryIndex();
        index.Insert(Fight("rat0", damageDone: 30));
        index.Insert(Fight("rat1", damageDone: 40));

        Assert.Equal(1, index.GetInstanceSummary("rat0").FightCount);
        Assert.Equal(30.0, index.GetInstanceSummary("rat0").MedianDamageDone!.Value, 3);
        Assert.Equal(2, index.GetGroupSummary("rats").FightCount);
    }

    [Fact]
    public void GetInstanceSummary_UnseenInstanceReturnsEmptyNotAThrow()
    {
        var index = new HistoryIndex();
        var summary = index.GetInstanceSummary("dwarf99");

        Assert.Equal(0, summary.FightCount);
        Assert.Null(summary.MedianDamageDone);
    }

    [Fact]
    public void GetByWeapon_RanksByEvidenceAndBucketsUnarmedSeparately()
    {
        var records = new[]
        {
            Fight(weapon: "axe0"),
            Fight(weapon: "axe0"),
            Fight(weapon: "axe0"),
            Fight(weapon: "dagger0"),
            Fight(weapon: null),   // unarmed is real data, not dropped
        };

        var index = new HistoryIndex();
        foreach (var record in records)
            index.Insert(record);

        var byWeapon = index.GetByWeapon("rats");
        var scanned = FightHistory.SummarizeByWeapon(records, "rats");

        Assert.Equal(scanned.Count, byWeapon.Count);
        Assert.Equal("axe0", byWeapon[0].Weapon);
        Assert.Equal(3, byWeapon[0].Summary.FightCount);
        Assert.Contains(byWeapon, entry => entry.Weapon == FightHistory.NoWeaponKey);
    }

    [Fact]
    public void GetByWeapon_DoesNotLeakEntriesFromAnotherGroup()
    {
        var index = new HistoryIndex();
        index.Insert(Fight("rat0", weapon: "axe0"));
        index.Insert(Fight("goat0", weapon: "pick0"));

        var ratWeapons = index.GetByWeapon("rats");

        Assert.Single(ratWeapons);
        Assert.Equal("axe0", ratWeapons[0].Weapon);
    }

    [Fact]
    public void GetWeaponGlobalSummary_AggregatesAcrossEveryGroupForOneWeapon()
    {
        var records = new[]
        {
            Fight("rat0", weapon: "axe0", damageDone: 40, youHits: 4),    // rats: 10.0/hit
            Fight("goat0", weapon: "axe0", damageDone: 20, youHits: 4),   // goats: 5.0/hit
            Fight("rat1", weapon: "dagger0", damageDone: 8, youHits: 4),  // different weapon
        };

        var index = new HistoryIndex();
        foreach (var record in records)
            index.Insert(record);

        var summary = index.GetWeaponGlobalSummary("axe0");

        Assert.Equal(2, summary.FightCount);
        Assert.Equal(7.5, summary.MedianDamagePerHit!.Value, 3);
    }

    [Fact]
    public void GetWeaponGlobalSummary_TreatsNullAndMissingWeaponAsTheSameUnarmedBucket()
    {
        var index = new HistoryIndex();
        index.Insert(Fight(weapon: null));
        index.Insert(Fight(weapon: "axe0"));

        Assert.Equal(1, index.GetWeaponGlobalSummary(null).FightCount);
        Assert.Equal(1, index.GetWeaponGlobalSummary("").FightCount);
    }

    [Fact]
    public void GetByWeapon_AndGetWeaponGlobalSummary_UseDifferentNullWeaponConventionsLikeTheOriginalCode()
    {
        // FightHistory.SummarizeByWeapon uses "(none)" for unarmed; SummarizeWeaponGlobal uses "".
        // These two spellings already disagreed before this index existed - preserved here exactly,
        // not "fixed" to agree, so no caller's output changes.
        var index = new HistoryIndex();
        index.Insert(Fight("rat0", weapon: null));

        var byWeapon = index.GetByWeapon("rats");
        Assert.Equal(FightHistory.NoWeaponKey, byWeapon[0].Weapon);

        var global = index.GetWeaponGlobalSummary(null);
        Assert.Equal(1, global.FightCount);
    }

    [Fact]
    public void Insert_ExcludesNarrativeModeRowsFromMediansButKeepsTheirOutcomes()
    {
        var index = new HistoryIndex();
        index.Insert(Fight(damageDone: 30, youHits: 3, youMisses: 1));
        index.Insert(Fight(damageDone: 0, youHits: 0, youMisses: 0, theyHits: 0, theyMisses: 0, narrative: true));

        var summary = index.GetGroupSummary("rats");

        Assert.Equal(1, summary.SampleSize);
        Assert.Equal(2, summary.FightCount);
        Assert.Equal(2, summary.Kills);
        Assert.Equal(30.0, summary.MedianDamageDone!.Value, 3);
    }

    [Fact]
    public void Insert_EstimatesStaminaPoolFromKillsOnly()
    {
        var index = new HistoryIndex();
        index.Insert(Fight(outcome: FightOutcome.Killed, damageDone: 30));
        index.Insert(Fight(outcome: FightOutcome.Killed, damageDone: 36));
        index.Insert(Fight(outcome: FightOutcome.Withdrawn, damageDone: 5));
        index.Insert(Fight(outcome: FightOutcome.YouFled, damageDone: 2));

        var summary = index.GetGroupSummary("rats");

        Assert.Equal(33.0, summary.EstimatedStaminaPool!.Value, 3);
        Assert.Equal(2, summary.Kills);
        Assert.Equal(4, summary.FightCount);
    }
}
