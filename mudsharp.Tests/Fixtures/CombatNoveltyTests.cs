using MudSharp.Combat;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// The novelty marks the Combat Rail lights a creature's name and the player's weapon with.
///
/// <para>Two things are pinned here that a reader would otherwise be free to "fix":</para>
/// <list type="bullet">
/// <item>The severity order. Orange is "no evidence"; red is "evidence, and you could not finish
/// it". Red therefore outranks orange in the weapon rollup, which is the owner's own reading and
/// the opposite of a conventional unknown-is-scariest ramp.</item>
/// <item>The bucketing. Novelty is per pool key - species plus the adjectives the game printed -
/// so "large rat0" is a different creature from "rat0" and rat3 is the same creature as rat7.</item>
/// </list>
/// </summary>
public sealed class CombatNoveltyTests
{
    private static FightRecord Fight(
        string npcName,
        FightOutcome outcome,
        string? weapon = "dagger0")
        => new()
        {
            NpcName = npcName,
            NpcGroup = NpcGroups.Normalize(npcName),
            WeaponUsed = weapon,
            Outcome = outcome.ToString(),
            YouHits = 3,
            YouMisses = 1,
            TheyHits = 1,
            TheyMisses = 3,
            ApproxDamageDone = 30,
            ApproxDamageTaken = 6,
            DurationMs = 60_000,
        };

    // ── Classify ──────────────────────────────────────────────────────────────

    [Fact]
    public void Classify_NeverFought_IsUnfought()
        => Assert.Equal(NoveltyMark.Unfought, CombatNovelty.Classify(fought: false, defeated: false));

    [Fact]
    public void Classify_FoughtNeverKilled_IsUndefeated()
        => Assert.Equal(NoveltyMark.Undefeated, CombatNovelty.Classify(fought: true, defeated: false));

    [Fact]
    public void Classify_Killed_IsNone()
        => Assert.Equal(NoveltyMark.None, CombatNovelty.Classify(fought: true, defeated: true));

    // ── WeaponRollup: the precedence rule, over the engaged only ──────────────

    private static ParticipantFact Live(NoveltyMark weaponMark)
        => new("rat0", IsResolved: false, FightOutcome.Unresolved, WeaponNovelty: weaponMark);

    private static ParticipantFact Resolved(NoveltyMark weaponMark)
        => new("rat0", IsResolved: true, FightOutcome.Kill, WeaponNovelty: weaponMark);

    [Fact]
    public void WeaponRollup_IgnoresResolvedFights()
    {
        // The one thing still swinging is a known quantity for this weapon; the loud mark belongs to
        // a fight that is already over and must not colour the weapon.
        var marks = CombatNovelty.WeaponRollup([Resolved(NoveltyMark.Undefeated), Live(NoveltyMark.None)]);
        Assert.Equal(NoveltyMark.None, marks);
    }

    [Fact]
    public void WeaponRollup_UndefeatedBeatsUnfought_WhateverTheOrder()
    {
        // The precedence rule itself: red outranks orange. Orange is "no evidence", red is "evidence,
        // and it says you could not finish this" - see NoveltyMark on why that is not a danger ramp.
        Assert.Equal(NoveltyMark.Undefeated, CombatNovelty.WeaponRollup(
            [Live(NoveltyMark.Unfought), Live(NoveltyMark.Undefeated), Live(NoveltyMark.None)]));
        Assert.Equal(NoveltyMark.Undefeated, CombatNovelty.WeaponRollup(
            [Live(NoveltyMark.Undefeated), Live(NoveltyMark.Unfought)]));
    }

    [Fact]
    public void WeaponRollup_UnfoughtBeatsNone()
        => Assert.Equal(NoveltyMark.Unfought, CombatNovelty.WeaponRollup(
            [Live(NoveltyMark.None), Live(NoveltyMark.Unfought), Live(NoveltyMark.None)]));

    [Fact]
    public void WeaponRollup_NothingAtAll_IsNone()
        => Assert.Equal(NoveltyMark.None, CombatNovelty.WeaponRollup([]));

    [Fact]
    public void WeaponRollup_NoLiveParticipants_IsNone()
        => Assert.Equal(NoveltyMark.None,
            CombatNovelty.WeaponRollup([Resolved(NoveltyMark.Unfought), Resolved(NoveltyMark.Undefeated)]));

    // ── HistoryIndex: what the corpus actually answers ────────────────────────

    [Fact]
    public void Novelty_NothingOnFile_IsUnfought()
        => Assert.Equal(NoveltyMark.Unfought, new HistoryIndex().GetNovelty("rat0"));

    [Fact]
    public void Novelty_BlankName_IsNone_NotUnfought()
    {
        // "No key could be formed" is a different claim from "you have never fought this", and the
        // rail must not light a name it could not even bucket.
        var index = new HistoryIndex();
        Assert.Equal(NoveltyMark.None, index.GetNovelty(null));
        Assert.Equal(NoveltyMark.None, index.GetNovelty("   "));
        Assert.Equal(NoveltyMark.None, index.GetNovelty("42"));
    }

    [Fact]
    public void Novelty_FledFrom_IsUndefeated()
    {
        var index = new HistoryIndex();
        index.Insert(Fight("rat0", FightOutcome.UFled));
        Assert.Equal(NoveltyMark.Undefeated, index.GetNovelty("rat0"));
    }

    [Theory]
    // Every way a fight can end with the creature still alive, or with us unable to say it died.
    // All of these leave the mark red. Enumerated rather than sampled so a new FightOutcome member
    // cannot be added without this test being updated to say which side of the line it falls on.
    [InlineData(FightOutcome.Died)]
    [InlineData(FightOutcome.CFled)]
    [InlineData(FightOutcome.CFledFail)]
    [InlineData(FightOutcome.UFled)]
    [InlineData(FightOutcome.UFledFail)]
    [InlineData(FightOutcome.Withdraw)]
    [InlineData(FightOutcome.EndOther)]
    [InlineData(FightOutcome.Unresolved)]
    // Interrupted is the client's own force-end (reset/logout/room change/app exit) and the creature
    // is certainly still alive. Pinned here for completeness rather than because it can happen: the
    // history persists force-ended fights as Unresolved, so this outcome never reaches an index row.
    // See FightOutcome.Interrupted.
    [InlineData(FightOutcome.Interrupted)]
    public void Novelty_OutcomesThatLeaveItStanding_AreUndefeated(FightOutcome outcome)
    {
        var index = new HistoryIndex();
        index.Insert(Fight("rat0", outcome));
        Assert.Equal(NoveltyMark.Undefeated, index.GetNovelty("rat0"));
    }

    [Theory]
    [InlineData(FightOutcome.Kill)]
    // NoMore is a DEAD creature - "The X drops dead, poisoned...", or the dragon that dies from the
    // coal you fed it ten minutes ago (operator, 2026-09-02). The red mark claims "you fought this
    // and could not finish it", which is false of something lying dead in front of you, so this is
    // deliberately NOT record.IsKill. See CombatNovelty.CountsAsDefeated - and note the pool
    // estimator reads the same row and must reach the OPPOSITE conclusion, because it is asking
    // whether we watched what killed it.
    [InlineData(FightOutcome.NoMore)]
    public void Novelty_OutcomesThatLeaveItDead_ClearTheMark(FightOutcome outcome)
    {
        var index = new HistoryIndex();
        index.Insert(Fight("rat0", outcome));
        Assert.Equal(NoveltyMark.None, index.GetNovelty("rat0"));
    }

    [Fact]
    public void Novelty_EveryFightOutcomeIsClassifiedOneWayOrTheOther()
    {
        // The two theories above must between them name every member of the enum. A new outcome that
        // nobody classified would silently fall into "undefeated" and light a red mark for a reason
        // no one chose.
        var classified = new HashSet<FightOutcome>
        {
            FightOutcome.Kill, FightOutcome.NoMore,
            FightOutcome.Died, FightOutcome.CFled, FightOutcome.CFledFail, FightOutcome.UFled,
            FightOutcome.UFledFail, FightOutcome.Withdraw, FightOutcome.EndOther, FightOutcome.Unresolved,
            FightOutcome.Interrupted,
        };
        Assert.Equal(Enum.GetValues<FightOutcome>().ToHashSet(), classified);
    }

    [Fact]
    public void CountsAsDefeated_IsKillAndNoMoreOnly()
    {
        Assert.True(CombatNovelty.CountsAsDefeated(nameof(FightOutcome.Kill)));
        Assert.True(CombatNovelty.CountsAsDefeated(nameof(FightOutcome.NoMore)));
        Assert.False(CombatNovelty.CountsAsDefeated(nameof(FightOutcome.CFled)));
        // An outcome string nothing wrote, and a null one: neither says the creature died.
        Assert.False(CombatNovelty.CountsAsDefeated("SomethingNobodyHasWrittenYet"));
        Assert.False(CombatNovelty.CountsAsDefeated(null));
    }

    [Fact]
    public void Novelty_OneKillAmongManyFailures_ClearsTheMark()
    {
        var index = new HistoryIndex();
        index.Insert(Fight("rat0", FightOutcome.UFled));
        index.Insert(Fight("rat0", FightOutcome.Kill));
        index.Insert(Fight("rat0", FightOutcome.Died));
        Assert.Equal(NoveltyMark.None, index.GetNovelty("rat0"));
    }

    [Fact]
    public void Novelty_IsPerPoolKey_SoInstanceNumbersShare()
    {
        // rat3 and rat7 are measured to be statistically indistinguishable, so killing one says
        // something about the other. Keying on the instance name would call every unmet number new.
        var index = new HistoryIndex();
        index.Insert(Fight("rat3", FightOutcome.Kill));
        Assert.Equal(NoveltyMark.None, index.GetNovelty("rat7"));
    }

    [Fact]
    public void Novelty_IsPerPoolKey_SoTheSizeAdjectiveSplits()
    {
        // A "large rat" runs to roughly 100 stamina where a plain "rat" runs to roughly 25. Killing
        // one must not clear the mark on the other, which is exactly what npc_group ("rats") would do.
        var index = new HistoryIndex();
        index.Insert(Fight("rat0", FightOutcome.Kill));
        Assert.Equal(NoveltyMark.Unfought, index.GetNovelty("large rat0"));
        Assert.Equal(NoveltyMark.None, index.GetNovelty("rat0"));
    }

    // ── HistoryIndex: the per-weapon question ─────────────────────────────────

    [Fact]
    public void WeaponNovelty_NeverUsedAgainstThisKind_IsUnfought()
    {
        var index = new HistoryIndex();
        index.Insert(Fight("rat0", FightOutcome.Kill, weapon: "dagger0"));
        Assert.Equal(NoveltyMark.Unfought, index.GetWeaponNovelty("rat0", "axe0"));
    }

    [Fact]
    public void WeaponNovelty_UsedButNeverFinished_IsUndefeated()
    {
        var index = new HistoryIndex();
        index.Insert(Fight("rat0", FightOutcome.Kill, weapon: "dagger0"));
        index.Insert(Fight("rat0", FightOutcome.UFled, weapon: "axe0"));
        // The KIND is a solved problem - a dagger has killed one - but this particular axe has not.
        Assert.Equal(NoveltyMark.None, index.GetNovelty("rat0"));
        Assert.Equal(NoveltyMark.Undefeated, index.GetWeaponNovelty("rat0", "axe0"));
    }

    [Fact]
    public void WeaponNovelty_Unarmed_IsItsOwnBucket_NotAMissingAnswer()
    {
        // Fighting bare-handed is ordinary in MUD2, so it is a weapon choice with a record of its own
        // and null/blank must not collide with a named weapon's bucket.
        var index = new HistoryIndex();
        index.Insert(Fight("rat0", FightOutcome.Kill, weapon: null));
        Assert.Equal(NoveltyMark.None, index.GetWeaponNovelty("rat0", null));
        Assert.Equal(NoveltyMark.None, index.GetWeaponNovelty("rat0", "   "));
        Assert.Equal(NoveltyMark.Unfought, index.GetWeaponNovelty("rat0", "dagger0"));
    }

    [Fact]
    public void WeaponNovelty_BlankName_IsNone()
        => Assert.Equal(NoveltyMark.None, new HistoryIndex().GetWeaponNovelty("", "dagger0"));

    [Fact]
    public void Novelty_DoesNotDisturbTheSummaryBuckets()
    {
        // The novelty dictionaries are additions to Insert, not a replacement for anything: the
        // existing group/instance/weapon summaries must read exactly as they did before.
        var index = new HistoryIndex();
        index.Insert(Fight("rat0", FightOutcome.Kill));
        var group = index.GetGroupSummary("rats");
        Assert.Equal(1, group.FightCount);
        Assert.Equal(1, group.Kills);
        Assert.Equal(1, index.GetInstanceSummary("rat0").FightCount);
        Assert.True(index.IsKnownWeapon("dagger0"));
    }
}
