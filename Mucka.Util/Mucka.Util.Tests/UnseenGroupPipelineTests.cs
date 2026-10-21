using Mucka.Combat;
using Mucka.Store;
using MudSharp.Combat;
using MudSharp.Models;

namespace Mucka.Util.Tests;

/// <summary>
/// The unseen group from the tracker's lines to what the rail, the dead strip and the fight records
/// get: <see cref="CombatTracker"/> feeding <see cref="CombatStatsAggregator"/>,
/// <see cref="ParticipantRoster"/>, <see cref="UnseenEndings"/> with <see cref="KillAwardLedger"/>,
/// and <see cref="FightHistoryRecorder"/>, against the operator's examples in
/// docs/unseen-presentation.md. The Kickflip lines are verbatim from the operator's fight with him.
/// </summary>
public sealed class UnseenGroupPipelineTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 15, 22, 50, 56, DateTimeKind.Utc);

    private static StyledLine Line(string text) => new([new StyledSpan(text, TextStyle.Default)]);

    private static StyledLine Start(string text) =>
        new([new StyledSpan(text, TextStyle.Default)], kind: LineKind.FightStart);

    private sealed class Wired
    {
        public readonly CombatTracker Tracker = new();
        public readonly CombatStatsAggregator Aggregator = new();
        public readonly KillAwardLedger Awards = new();
        public readonly List<CombatEvent> Seen = [];
        private int _second;

        public Wired()
        {
            Aggregator.BeginEncounter(T0);
            Tracker.EventOccurred += e =>
            {
                Seen.Add(e);
                Aggregator.Observe(e);
                if (KillAwardLedger.Awards(e.Kind) && e.NpcName is { } ended)
                    Awards.NoteEnding(0, ended, e.TimestampUtc);
            };
        }

        public DateTime Now => T0.AddSeconds(_second);

        public Wired Say(string text) { Tracker.Observe(Line(text), T0.AddSeconds(++_second)); return this; }
        public Wired Starts(string text) { Tracker.Observe(Start(text), T0.AddSeconds(++_second)); return this; }
        public Wired Scored(int points) { Awards.NoteScoreRise(points); Awards.NoteFrameClosed(); return this; }
        public Wired Coded(string text, LineKind kind)
        {
            Tracker.Observe(new StyledLine([new StyledSpan(text, TextStyle.Default)], kind: kind), T0.AddSeconds(++_second));
            return this;
        }
        public Wired Stamina(int current) { Aggregator.ObserveStamina(current); return this; }
        public Wired Dark() { Tracker.NoteCannotSee(true, T0.AddSeconds(++_second), "test"); return this; }
        public Wired Light() { Tracker.NoteCannotSee(false, T0.AddSeconds(++_second), "test"); return this; }

        public CombatEncounterSnapshot Snapshot() => Aggregator.Snapshot(T0.AddSeconds(_second + 1));

        public RosterPlan Roster()
        {
            var snapshot = Snapshot();
            var facts = ParticipantFacts.ToParticipantFacts(
                snapshot.Fights, T0.AddSeconds(_second + 1), null, null, null, null, null, null);
            return ParticipantRoster.Build(facts, snapshot.Unseen);
        }

        /// <summary>The dead strip's rows for this encounter, as SidePanelViewModel folds them at the
        /// close, awards filled the way its FillAwards does.</summary>
        public List<string> History()
        {
            var snapshot = Snapshot();
            var rows = snapshot.Fights
                .Where(f => f.IsResolved && !UnseenEndings.ShownByEntries(f))
                .Select(f => new CombatEnding(f.NpcName, f.Outcome, f.EndedUtc,
                    Dealt: ExchangeLines.DealtLine(f), Taken: ExchangeLines.TakenLine(f),
                    ScoreAwarded: Awards.AwardFor(0, f.NpcName, f.EndedUtc)))
                .Concat(UnseenEndings.For(snapshot.Unseen, snapshot.Fights, 0, 0, null)
                    .Select(e => e with { ScoreAwarded = UnseenEndings.Score(e.Awards!, a => Awards.AwardFor(0, a.Name, a.At)) }));
            return CombatEndingOrder.Sorted(rows.ToList())
                .Select(e => $"[{e.Label}{(e.ScoreAwarded is { } s ? $" +{s}" : "")}{(RailReadout.OutcomeWord(e.Outcome) is { Length: > 0 } w ? " " + w : "")}"
                    + $"{(e.Dealt.HasSamples || e.Taken.HasSamples ? " stats" : "")}]")
                .ToList();
        }
    }

    [Fact]
    public void ASomeoneNamedWhileSighted_KeepsTheAnonymousBlowsInTheGroup()
    {
        var w = new Wired()
            .Starts("Someone is about to attack you.")
            .Say("Someone hits you (81/105).")
            .Say("You miss someone.")
            .Say("You hit someone (15-19).");
        Assert.Equal(1, w.Snapshot().Unseen!.Count);
        Assert.Equal(AnonymousOpponent.Group, Assert.Single(w.Roster().Rows).Name);

        w.Say("Kickflip the necromancer hits you (20/105).")
         .Say("You hit Kickflip the necromancer (10-14).");

        var fights = w.Snapshot().Fights;
        var group = fights.Single(f => f.NpcName == AnonymousOpponent.Group);
        var kickflip = fights.Single(f => f.NpcName == "Kickflip");
        Assert.Equal((1, 1, 1), (group.YouHits, group.YouMisses, group.TheyHits));
        Assert.Equal((1, 0, 1), (kickflip.YouHits, kickflip.YouMisses, kickflip.TheyHits));
        // The named swing spent the slot: nobody is unseen, and the group draws nothing.
        Assert.Equal(0, w.Snapshot().Unseen!.Count);
        Assert.Equal(["Kickflip"], w.Roster().Rows.Select(r => r.Name));
    }

    [Fact]
    public void TheRailWalkThrough_ParksAndPromotes()
    {
        // The operator's ten steps as docs/unseen-presentation.md writes them: a rat and an
        // invisible old man.
        var w = new Wired()
            .Starts("The rat is moving towards you ferociously.")
            .Say("The rat hits you (90/100).");
        Assert.Equal(["rat"], w.Roster().Rows.Select(r => r.Name));                               // 1

        w.Starts("Someone is about to attack you.");
        Assert.Equal(["rat", AnonymousOpponent.Group], w.Roster().Rows.Select(r => r.Name));      // 2

        w.Say("Your spell worked!").Say("The rat has become invisible!");
        var parked = Assert.Single(w.Roster().Rows);                                               // 3
        Assert.Equal("rat, ???", parked.UnseenLabel);
        Assert.Equal(2, parked.SlotSpan);

        w.Say("The rat has regained his visibleness!");
        Assert.Single(w.Roster().Rows);                                  // the visibility line alone
        w.Say("The rat misses you.");                                                              // 4
        Assert.Equal(["rat", AnonymousOpponent.Group], w.Roster().Rows.Select(r => r.Name));
        Assert.Equal(2, w.Snapshot().Fights.Single(f => f.NpcName == "rat").TheyHits + w.Snapshot().Fights.Single(f => f.NpcName == "rat").TheyMisses);

        w.Dark();
        Assert.Equal("rat, ???", Assert.Single(w.Roster().Rows).UnseenLabel);                      // 5
        w.Light();
        Assert.Equal("rat, ???", Assert.Single(w.Roster().Rows).UnseenLabel);                      // 6, 7

        // 8: a swing at the old man by name spends the someone slot: a new row, first attributed. The
        // count does not conclude that the remaining unknown is the rat.
        w.Say("You hit the old man (5-9).");
        Assert.Equal(["old man", AnonymousOpponent.Group], w.Roster().Rows.Select(r => r.Name));
        Assert.Equal("rat", Assert.Single(w.Roster().Rows, r => r.IsUnseen).UnseenLabel);

        // 9: the rat swings by name and comes back with the blows it held; a hit from Someone is the
        // group's and parks the old man.
        w.Say("The rat hits you (80/100).").Say("Someone hits you (70/100).");
        Assert.Equal(["rat", AnonymousOpponent.Group], w.Roster().Rows.Select(r => r.Name));
        Assert.Equal("old man", Assert.Single(w.Roster().Rows, r => r.IsUnseen).UnseenLabel);
        Assert.Equal(2, w.Snapshot().Fights.Single(f => f.NpcName == "rat").TheyHits);
        Assert.Equal(1, w.Snapshot().Fights.Single(f => f.NpcName == AnonymousOpponent.Group).TheyHits);

        // 10: the old man hits again by name, resuming his figures: two rows, nobody unseen.
        w.Say("The old man hits you (60/100).");
        Assert.Equal(["rat", "old man"], w.Roster().Rows.Select(r => r.Name));
        Assert.Equal(1, w.Snapshot().Fights.Single(f => f.NpcName == "old man").YouHits);
    }

    [Fact]
    public void ThreeRatsInTheDark_CountingHandsTheRowsTheirOutcome_AndTheGroupTheScores()
    {
        // The operator's worked example: three named rats start, the torch is dropped, a someone
        // starts, and every line after is anonymous.
        var w = new Wired()
            .Starts("The rat0 is moving towards you ferociously.")
            .Starts("The rat1 is moving towards you ferociously.")
            .Starts("The rat2 is moving towards you ferociously.")
            .Dark()
            .Starts("Someone is about to attack you.");
        Assert.Equal(4, w.Snapshot().Unseen!.Count);
        for (var i = 0; i < 3; i++)
            w.Say("You hit something (2-3).").Say("You have killed something.").Scored(5);
        Assert.True(w.Tracker.InCombat);
        w.Say("Someone hits you (90/100).").Say("You hit someone (4-6).").Say("You have killed someone.").Scored(10);

        Assert.False(w.Tracker.InCombat);
        Assert.Equal(["[unseen +15 stats]", "[unseen +10 killed]", "[rat0 killed]", "[rat1 killed]", "[rat2 killed]"],
            w.History());
        Assert.All(w.Snapshot().Fights.Where(f => f.NpcName.StartsWith("rat", StringComparison.Ordinal)),
            f => Assert.True(f is { ByCounting: true, Outcome: FightOutcome.Kill }));
    }

    [Fact]
    public void OneUnseenParticipantKilledInTheDark_FoldsIntoOneBadge()
    {
        var w = new Wired()
            .Dark()
            .Starts("Something is about to attack you.")
            .Say("You hit something (9-14).")
            .Say("Something hits you (90/100).")
            .Say("You have killed something.").Scored(86);

        Assert.False(w.Tracker.InCombat);
        Assert.Equal(["[unseen +86 killed stats]"], w.History());
    }

    [Fact]
    public void TwoUnseens_TheSecondNamedBeforeItDies()
    {
        var w = new Wired()
            .Starts("Someone is about to attack you.")
            .Starts("Something is about to attack you.")
            .Starts("The rat0 is moving towards you ferociously.")
            .Say("You hit someone (12-19).")
            .Say("You hit something (13-20).")
            .Say("You hit the rat0 (7-8).")
            .Say("You have killed someone.").Scored(16)
            .Say("You have killed the rat0.").Scored(32)
            .Say("You hit the badger (10-15).")
            .Say("You have killed the badger.").Scored(22);

        Assert.False(w.Tracker.InCombat);
        Assert.Equal(["[unseen stats]", "[unseen +16 killed]", "[rat0 +32 killed stats]", "[badger +22 killed stats]"], w.History());
    }

    [Fact]
    public void ANamedKillOfACreatureWithNoRow_ConcludesTheSlotUnderItsName()
    {
        var w = new Wired()
            .Dark()
            .Starts("Something is about to attack you.")
            .Starts("Something is about to attack you.")
            .Say("Something hits you (65/90).")
            .Say("You hit something (10-14).")
            .Say("You have killed the rat18.").Scored(7)
            .Say("You have killed the rat19.").Scored(9);

        Assert.False(w.Tracker.InCombat);
        Assert.Equal(["[unseen stats]", "[rat18 +7 killed]", "[rat19 +9 killed]"], w.History());
        Assert.DoesNotContain(w.Snapshot().Fights, f => f.NpcName.StartsWith("rat", StringComparison.Ordinal));
    }

    /// <summary>A shape seen in the operator's wire corpus: one unseen opponent in the dark, named only by the line that killed
    /// it. The whole fight is one badge titled by the name in parentheses.</summary>
    [Fact]
    public void OneUnseenNamedOnlyByItsKill_IsOneBadge_TheNameInParentheses()
    {
        var w = new Wired()
            .Dark()
            .Starts("Something is about to attack you.")
            .Say("You hit something (10-14).")
            .Say("Something hits you (80/100).")
            .Say("You have killed the basilisk.").Scored(80);

        Assert.False(w.Tracker.InCombat);
        Assert.Equal(["[(basilisk) +80 killed stats]"], w.History());
    }

    /// <summary>A shape seen in the operator's wire corpus: three unseen opponents, each named only by the line that killed it.
    /// The group keeps the blows and no points; each kill is the Creature's own badge.</summary>
    [Fact]
    public void ThreeUnseensNamedOnlyByTheirKills_AreTheGroupAndThreeNamedBadges()
    {
        var w = new Wired().Dark();
        for (var i = 0; i < 3; i++)
            w.Starts("Something is about to attack you.");
        w.Say("You hit something (5-9).");
        foreach (var rat in new[] { "rat18", "rat19", "rat21" })
            w.Say($"You have killed the {rat}.").Scored(22);

        Assert.False(w.Tracker.InCombat);
        Assert.Equal(["[unseen stats]", "[rat18 +22 killed]", "[rat19 +22 killed]", "[rat21 +22 killed]"], w.History());
    }

    [Fact]
    public void SixUnseenBadges_LumpIntoOne()
    {
        var w = new Wired();
        for (var i = 0; i < 6; i++)
            w.Starts("Someone is about to attack you.");
        for (var i = 0; i < 6; i++)
            w.Say("You hit someone (2-3).").Say("You have killed someone.").Scored(10);

        Assert.False(w.Tracker.InCombat);
        Assert.Equal(["[unseen x6 ()x0 +60 stats]"], w.History());
    }

    [Fact]
    public void AnonymousEndingsThatDoNotAddUp_LeaveTheParkedRowFateUnseen()
    {
        // The rat is parked by the dark and a someone is open; two "something" deaths cannot be the
        // someone's, and cannot both be the rat's, so nobody is assigned and the rat's fate is unseen.
        var w = new Wired()
            .Starts("The rat is moving towards you ferociously.")
            .Dark()
            .Starts("Someone is about to attack you.")
            .Say("You have killed something.").Scored(3)
            .Say("You have killed something.").Scored(4);

        Assert.False(w.Tracker.InCombat);
        Assert.Equal(FightOutcome.FateUnseen, w.Snapshot().Fights.Single(f => f.NpcName == "rat").Outcome);
        Assert.Equal(["[unseen]", "[unseen +3 killed]", "[unseen +4 killed]", "[rat fate unseen]"], w.History());
    }

    [Fact]
    public void TheGroupsFight_IsLiveOnlyWhileSomebodyIsUnseen_AndStaysOneFight()
    {
        var w = new Wired()
            .Starts("Something is about to attack you.")
            .Starts("Something is about to attack you.")
            .Say("Something hits you (90/100).")
            .Say("You have killed something.")
            .Say("Something hits you (80/100).");

        // One still unseen: the group is a live opponent, every blow in one fight.
        var group = Assert.Single(w.Snapshot().Fights, f => f.NpcName == AnonymousOpponent.Group);
        Assert.False(group.IsResolved);
        Assert.Equal(2, group.TheyHits);
        Assert.Equal(1, w.Snapshot().Unseen!.Count);

        // A thief keeps the encounter open after the last unseen one dies: the group is over.
        w.Starts("The thief is moving towards you ferociously.").Say("You have killed something.");
        Assert.True(w.Tracker.InCombat);
        Assert.Equal(0, w.Snapshot().Unseen!.Count);
        Assert.True(Assert.Single(w.Snapshot().Fights, f => f.NpcName == AnonymousOpponent.Group).IsResolved);

        // A blow from an opponent nobody announced: unseen again, the same fight live again.
        w.Say("Something hits you (70/100).");
        group = Assert.Single(w.Snapshot().Fights, f => f.NpcName == AnonymousOpponent.Group);
        Assert.False(group.IsResolved);
        Assert.Equal(3, group.TheyHits);
    }

    /// <summary>The reviewer's probe: the dead group must not stand in for a live opponent in the
    /// primary fight, the incoming tempo or the largest live blow.</summary>
    [Fact]
    public void WithNobodyUnseen_TheLiveAggregatesDescribeTheNamedCreatureOnly()
    {
        var w = new Wired()
            .Stamina(100)
            .Starts("Something is about to attack you.")
            .Starts("The thief is moving towards you ferociously.")
            .Say("Something hits you (40/100).")
            .Say("The thief hits you (30/100).")
            .Say("You have killed something.");

        var snapshot = w.Snapshot();
        Assert.Equal(0, snapshot.Unseen!.Count);
        Assert.Equal("thief", CombatComposition.PrimaryFight(snapshot)!.NpcName);
        Assert.Equal(new SwingTempo(1, 0), CombatFrameComposer.IncomingTempoOf(snapshot));
        Assert.Equal(10, CombatFrameComposer.LargestLiveBlowTaken(snapshot));
    }

    [Fact]
    public void AnonymousKills_AreSessionKills()
    {
        var w = new Wired()
            .Dark()
            .Starts("Something is about to attack you.")
            .Starts("Something is about to attack you.")
            .Starts("Something is about to attack you.");
        for (var i = 0; i < 3; i++)
            w.Say("You hit something (2-3).").Say("You have killed something.");

        Assert.False(w.Tracker.InCombat);
        var totals = SessionCombatTotals.Empty.Accumulate(w.Snapshot());
        Assert.Equal((3, 3), (totals.Kills, totals.Fights));
    }

    [Fact]
    public void RowsCountedKilled_AndTheSlotsOwnKill_AreAllSessionKills()
    {
        var w = new Wired()
            .Starts("The rat0 is moving towards you ferociously.")
            .Starts("The rat1 is moving towards you ferociously.")
            .Dark()
            .Starts("Someone is about to attack you.");
        w.Say("You have killed something.").Say("You have killed something.").Say("You have killed someone.");

        Assert.False(w.Tracker.InCombat);
        var totals = SessionCombatTotals.Empty.Accumulate(w.Snapshot());
        Assert.Equal((3, 3), (totals.Kills, totals.Fights));
    }

    [Fact]
    public void ACountedRow_KeepsItsNameAndStats_TheGroupTakesTheScore()
    {
        var w = new Wired().Starts("The rat0 is moving towards you ferociously.");
        for (var i = 0; i < 10; i++)
            w.Say("You hit the rat0 (2-3).");
        w.Say("You hit something (2-3).")
         .Say("You have killed something.").Scored(9);

        Assert.False(w.Tracker.InCombat);
        Assert.Equal(["[unseen +9 stats]", "[rat0 killed stats]"], w.History());
        Assert.Equal(10, w.Snapshot().Fights.Single(f => f.NpcName == "rat0").YouHits);
    }

    [Fact]
    public void AnAnonymousSwing_ParksARowThatCouldThrowIt_InsteadOfOpeningASlot()
    {
        var parks = new Wired()
            .Starts("The rat0 is moving towards you ferociously.")
            .Say("Something hits you (80/100).");
        Assert.Equal(1, parks.Snapshot().Unseen!.Count);
        Assert.Equal(["rat0"], parks.Snapshot().Unseen!.Parked);

        // The thief cannot be "something": nothing open could have thrown it, so it is a missed start.
        var opens = new Wired()
            .Starts("The thief is moving towards you ferociously.")
            .Say("Something hits you (80/100).");
        Assert.Equal(1, opens.Snapshot().Unseen!.Count);
        Assert.Empty(opens.Snapshot().Unseen!.Parked);
        Assert.Equal(["thief", AnonymousOpponent.Group], opens.Roster().Rows.Select(r => r.Name));

        // A someone slot is open, but nothing open can say "something": a second missed start.
        var second = new Wired()
            .Starts("Someone is about to attack you.")
            .Starts("The thief is moving towards you ferociously.")
            .Say("Something hits you (80/100).");
        Assert.Equal(2, second.Snapshot().Unseen!.Count);
    }

    [Fact]
    public void PlainNamedEndingsOfEngagedCreatures_ConcludeTheirRows()
    {
        var w = new Wired()
            .Starts("The rat0 is moving towards you ferociously.")
            .Starts("The rat1 is moving towards you ferociously.")
            .Say("The rat0 has fled by going north.")
            .Say("The rat1 has just passed on.");

        Assert.False(w.Tracker.InCombat);
        Assert.Equal(["[rat0 fled]", "[rat1 no more]"], w.History());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnOgreDyingOfPoisonUnseen_IsClosedByTheCodedEnd(bool plainLines)
    {
        var w = new Wired()
            .Dark()
            .Starts("Something is about to attack you.")
            .Say("You hit something (2-3).")
            .Say("Something hits you (80/100).");
        if (plainLines)
            w.Say("The ogre0 drops dead, poisoned...").Say("The ogre0 has just passed on.");
        Assert.True(w.Tracker.InCombat);

        w.Coded("You can fight it no longer.", LineKind.FightEnd);

        Assert.False(w.Tracker.InCombat);
        Assert.Equal(["[unseen ended stats]"], w.History());
    }

    [Fact]
    public void ACreatureFirstNamedByItsWeapon_HasARow_SoAnonymousKillsCannotCloseAroundIt()
    {
        var w = new Wired()
            .Say("The zombie9 has started to use the fork to fight!")
            .Starts("Something is about to attack you.")
            .Starts("Something is about to attack you.")
            .Say("You have killed something.")
            .Say("You have killed something.");

        // Three participants, two endings: one is left, and it may be the zombie.
        Assert.True(w.Tracker.InCombat);
        Assert.Equal(1, w.Snapshot().Unseen!.Count);
    }

    [Fact]
    public void GroupEndingsInOneClockTick_StillPairTheirOwnAwards()
    {
        var w = new Wired();
        w.Tracker.Observe(Start("Something is about to attack you."), T0);
        w.Tracker.Observe(Start("Something is about to attack you."), T0);
        w.Tracker.Observe(Line("You have killed something."), T0);
        w.Scored(5);
        w.Tracker.Observe(Line("You have killed something."), T0);
        w.Scored(6);

        var kills = w.Seen.Where(e => e.Kind == CombatEventKind.Kill).ToList();
        Assert.Equal(2, kills.Select(e => e.TimestampUtc).Distinct().Count());
        Assert.Equal(["[unseen]", "[unseen +5 killed]", "[unseen +6 killed]"], w.History());
    }

    [Fact]
    public void BlowsThatChangeNothing_PublishNothing()
    {
        // The start publishes the count; the first blow gives the group something to show.
        var w = new Wired()
            .Starts("Someone is about to attack you.")
            .Say("Someone hits you (90/100).");
        var published = w.Seen.Count(e => e.Kind == CombatEventKind.UnseenChanged);
        Assert.Equal(2, published);

        for (var i = 0; i < 5; i++)
            w.Say("Someone hits you (90/100).").Say("You miss someone.");

        Assert.Equal(published, w.Seen.Count(e => e.Kind == CombatEventKind.UnseenChanged));
    }

    /// <summary>The counting failure a wake-up guards against: the thief's start is missed, the man
    /// starts, and "You have killed someone." is the thief's.</summary>
    [Fact]
    public void AWakeUpLine_StopsCountingHandingAnEndingToTheMan()
    {
        var w = new Wired()
            .Say("You have just been woken up!")
            .Starts("The man is moving towards you ferociously.")
            .Say("You have killed someone.");

        Assert.DoesNotContain(w.Seen, e => e.Kind == CombatEventKind.UnseenRowEnded && e.Outcome != FightOutcome.FateUnseen);
    }

    [Fact]
    public void WithoutAWakeUpLine_TheSameScriptCountsTheManKilled()
    {
        var w = new Wired()
            .Starts("The man is moving towards you ferociously.")
            .Say("You have killed someone.");

        Assert.Contains(w.Seen, e => e.Kind == CombatEventKind.UnseenRowEnded && e.NpcName == "man");
    }

    /// <summary>Verbatim lines from the operator's wire corpus: after a wake-up the man, attacked
    /// unseen, is named; his name spends the slot, so his flight closes the encounter and he is one
    /// fight.</summary>
    [Fact]
    public void AfterAWakeUp_TheNamedManSpendsHisSlot_AndHisFlightCloses()
    {
        var w = new Wired()
            .Starts("You attack someone.")
            .Say("The man has woken up!")
            .Say("The man has regained his visibleness!")
            .Say("You hit the man (30-39).");
        Assert.Equal(0, w.Snapshot().Unseen!.Count);

        w.Coded("The man has fled by going north.", LineKind.FightEnd);
        Assert.False(w.Tracker.InCombat);
        Assert.Single(w.Snapshot().Fights, f => f.NpcName == "man");
        Assert.Equal((1, 0), (SessionCombatTotals.Empty.Accumulate(w.Snapshot()).Fights, SessionCombatTotals.Empty.Accumulate(w.Snapshot()).Kills));
    }

    // -- a slot a pending ending already concluded is not available ----------------------------

    [Fact]
    public void ANamedKill_CannotTakeASlotAPendingEndingConcluded_TheThiefStaysOpen()
    {
        var w = new Wired()
            .Starts("The thief is moving towards you ferociously.")
            .Starts("Something is about to attack you.")
            .Say("You have killed something.")
            .Say("You have killed the rat18.");

        Assert.True(w.Tracker.InCombat);
        Assert.DoesNotContain(w.Seen, e => e.Kind == CombatEventKind.UnseenRowEnded);
    }

    [Fact]
    public void ANamedKillOfAnUnknownClass_CannotTakeTheConcludedSlot_TheThiefIsNotCounted()
    {
        var w = new Wired()
            .Starts("The thief is moving towards you ferociously.")
            .Starts("Someone is about to attack you.")
            .Say("You have killed someone.")
            .Say("You have killed the ogre0.");

        Assert.True(w.Tracker.InCombat);
        Assert.DoesNotContain(w.Seen, e => e.Kind == CombatEventKind.UnseenRowEnded && e.NpcName == "thief");
    }

    [Fact]
    public void ANamedSwing_CannotSpendTheConcludedSlot_TheBadgerHasItsOwnRow()
    {
        var w = new Wired()
            .Starts("The thief is moving towards you ferociously.")
            .Starts("Someone is about to attack you.")
            .Say("You have killed someone.")
            .Say("The badger hits you (80/100).")
            .Say("You have killed the badger.");

        Assert.True(w.Tracker.InCombat);
        Assert.Equal(FightOutcome.Kill, w.Snapshot().Fights.Single(f => f.NpcName == "badger").Outcome);
        Assert.False(w.Snapshot().Fights.Single(f => f.NpcName == "badger").ByCounting);
        Assert.DoesNotContain(w.Seen, e => e.Kind == CombatEventKind.UnseenRowEnded);
    }

    // -- the player's death ---------------------------------------------------------------------

    [Fact]
    public void DeathByAnUnseen_IsTheGroupsKilledYou_AndASessionDeath()
    {
        var w = new Wired()
            .Stamina(100)
            .Dark()
            .Starts("Someone is about to attack you.")
            .Say("Someone hits you (50/100).")
            .Say("Someone has killed you.");

        Assert.False(w.Tracker.InCombat);
        Assert.Equal(["[unseen KILLED YOU stats]"], w.History());
        var totals = SessionCombatTotals.Empty.Accumulate(w.Snapshot());
        Assert.Equal((1, 1), (totals.Deaths, totals.Fights));
    }

    [Fact]
    public void DeathByANamedKiller_IsItsRowsKilledYou_AndASessionDeath()
    {
        var w = new Wired()
            .Stamina(100)
            .Starts("The thief is moving towards you ferociously.")
            .Say("The thief hits you (50/100).")
            .Say("The thief has killed you.");

        Assert.False(w.Tracker.InCombat);
        Assert.Equal(["[thief KILLED YOU stats]"], w.History());
        var totals = SessionCombatTotals.Empty.Accumulate(w.Snapshot());
        Assert.Equal((1, 1), (totals.Deaths, totals.Fights));
    }

    [Fact]
    public void APackDeath_IsOneSessionDeath()
    {
        // One "The rat11 has killed you." with five fights open is one death, not five.
        var w = new Wired();
        foreach (var rat in new[] { "rat11", "rat12", "rat13", "rat14", "rat15" })
            w.Starts($"The {rat} is moving towards you ferociously.");
        w.Say("The rat11 has killed you.");

        var totals = SessionCombatTotals.Empty.Accumulate(w.Snapshot());
        Assert.Equal((1, 5), (totals.Deaths, totals.Fights));
    }

    [Fact]
    public void DeathByAnUnseenWithANamedRowOpen_IsOneSessionDeath_NotTwo()
    {
        var w = new Wired()
            .Starts("The thief is moving towards you ferociously.")
            .Starts("Someone is about to attack you.")
            .Say("Someone has killed you.");

        Assert.False(w.Tracker.InCombat);
        var totals = SessionCombatTotals.Empty.Accumulate(w.Snapshot());
        Assert.Equal((1, 2), (totals.Deaths, totals.Fights));
    }

    [Fact]
    public void ARowParkedForAMomentWithNothingAnonymous_LeavesNoEmptyUnseenBadge()
    {
        var w = new Wired()
            .Starts("The thief is moving towards you ferociously.")
            .Say("You hit the thief (5-9).")
            .Dark();
        Assert.Equal(["thief"], w.Snapshot().Unseen!.Parked);
        w.Light().Say("You hit the thief (5-9).").Say("You have killed the thief.");

        Assert.Equal(["[thief killed stats]"], w.History());
    }

    /// <summary>A shape seen in the operator's wire corpus: a player fades, and "You can fight
    /// someone no longer." is counted onto him. The group holds no blow and no score: only his row is drawn.</summary>
    [Fact]
    public void ARowCountedAnUnscoredEnding_WithAnEmptyGroup_LeavesNoUnseenBadge()
    {
        var w = new Wired()
            .Starts("Kickflip the swordsman is rushing at you fiercely.")
            .Say("You hit Kickflip the swordsman (5-9).")
            .Say("Kickflip the swordsman fades from view.")
            .Coded("You can fight someone no longer.", LineKind.FightEnd);

        Assert.False(w.Tracker.InCombat);
        Assert.Equal(["[Kickflip ended stats]"], w.History());
    }

    [Theory]
    [InlineData("You have fled by going north.", "you fled")]
    [InlineData("You have fled by trying to go north.", "flee failed")]
    public void ThePlayersFlight_IsTheGroupBadgesOutcome(string line, string word)
    {
        var w = new Wired()
            .Starts("Someone is about to attack you.")
            .Say("You hit someone (2-3).")
            .Say(line);

        Assert.Equal([$"[unseen {word} stats]"], w.History());
    }

    [Fact]
    public void ANamedKillersDeath_LeavesTheGroupCutShort()
    {
        var w = new Wired()
            .Starts("Someone is about to attack you.")
            .Say("You hit someone (2-3).")
            .Starts("The thief is moving towards you ferociously.")
            .Say("The thief has killed you.");

        Assert.Equal(["[unseen cut short stats]", "[thief KILLED YOU]"], w.History());
    }

    /// <summary>Operator's rule: KILLED YOU only under the one the MUD says killed the player.</summary>
    [Fact]
    public void APackDeath_ShowsKilledYouOnlyUnderTheKiller()
    {
        var w = new Wired()
            .Starts("The rat0 is moving towards you ferociously.")
            .Starts("The rat10 is moving towards you ferociously.")
            .Starts("The rat11 is moving towards you ferociously.")
            .Say("The rat11 has killed you.");

        Assert.Equal(["[rat0 cut short]", "[rat10 cut short]", "[rat11 KILLED YOU]"], w.History());
    }

    [Fact]
    public void AnUnseenKillerWithANamedRowOpen_IsTheOnlyKilledYou()
    {
        var w = new Wired()
            .Starts("The thief is moving towards you ferociously.")
            .Starts("Someone is about to attack you.")
            .Say("You hit someone (2-3).")
            .Say("Someone has killed you.");

        Assert.Equal(["[unseen stats]", "[thief cut short]", "[unseen KILLED YOU]"], w.History());
    }

    [Fact]
    public void APackDeath_RecordsOnlyTheKillerAsDied()
    {
        var db = TestStore.Open(Path.Combine(_directory, MuckaDb.DefaultFileName));
        _db.Add(db);
        var store = new FightHistoryStore(db);
        var recorder = new FightHistoryRecorder(store);
        var tracker = new CombatTracker();
        tracker.InCombatChanged += inCombat => recorder.OnInCombatChanged(inCombat);
        tracker.EventOccurred += recorder.OnCombatEvent;
        var at = 0;
        foreach (var rat in new[] { "rat0", "rat10", "rat11" })
            tracker.Observe(new StyledLine([new StyledSpan($"The {rat} is moving towards you ferociously.", TextStyle.Default)],
                kind: LineKind.FightStart), T0.AddSeconds(++at));
        tracker.Observe(new StyledLine([new StyledSpan("The rat11 has killed you.", TextStyle.Default)]), T0.AddSeconds(++at));

        var rows = store.Snapshot();
        Assert.Equal(nameof(FightOutcome.Died), rows.Single(r => r.NpcName == "rat11").Outcome);
        Assert.All(rows.Where(r => r.NpcName != "rat11"), r => Assert.Equal(nameof(FightOutcome.Unresolved), r.Outcome));
    }

    // -- the primary fight ----------------------------------------------------------------------

    [Fact]
    public void ANamedLiveCreature_IsThePrimaryFight_AndTheRostersTarget_AheadOfTheGroup()
    {
        var w = new Wired()
            .Starts("Someone is about to attack you.")
            .Say("Someone hits you (90/100).")
            .Starts("The thief is moving towards you ferociously.")
            .Say("The thief hits you (80/100).");

        Assert.Equal("thief", CombatComposition.PrimaryFight(w.Snapshot())!.NpcName);
        Assert.Equal("thief", Assert.Single(w.Roster().Rows, r => r.IsCurrentTarget).Name);

        // With no named Creature live, the group is both.
        var alone = new Wired().Starts("Someone is about to attack you.").Say("Someone hits you (90/100).");
        Assert.Equal(AnonymousOpponent.Group, CombatComposition.PrimaryFight(alone.Snapshot())!.NpcName);
        Assert.True(Assert.Single(alone.Roster().Rows).IsCurrentTarget);
    }

    // -- named endings for names with no row ------------------------------------------------------

    [Fact]
    public void ANamedWithdrawForANameWithNoRow_ConcludesTheSlot_WithTheName()
    {
        var w = new Wired()
            .Dark()
            .Starts("Someone is about to attack you.")
            .Say("The ogre withdraws from your fight, and so do you.");

        Assert.False(w.Tracker.InCombat);
        Assert.Equal(["[(ogre) withdrew]"], w.History());
    }

    [Fact]
    public void ACodedNamedNoLonger_ForANameWithNoRow_KeepsItsName_AndNeverParks()
    {
        var withSlot = new Wired()
            .Starts("The thief is moving towards you ferociously.")
            .Starts("Something is about to attack you.")
            .Coded("You can fight the bee1 no longer.", LineKind.FightEnd);
        Assert.True(withSlot.Tracker.InCombat);
        Assert.Equal(0, withSlot.Snapshot().Unseen!.Count);
        Assert.Empty(withSlot.Snapshot().Unseen!.Parked);
        Assert.Contains(withSlot.Snapshot().Unseen!.Entries, e => e.Name == "bee1");

        var noSlot = new Wired()
            .Starts("The thief is moving towards you ferociously.")
            .Coded("You can fight the bee1 no longer.", LineKind.FightEnd);
        Assert.True(noSlot.Tracker.InCombat);
        Assert.Null(Assert.Single(noSlot.Seen, e => e.Kind == CombatEventKind.FightEndOther).NpcName);
        Assert.DoesNotContain(noSlot.Seen, e => e.Kind == CombatEventKind.UnseenChanged);
    }

    // -- slots left open ----------------------------------------------------------------------

    [Fact]
    public void ASlotStillOpenWhenThePlayerFlees_IsASessionFight()
    {
        var w = new Wired()
            .Starts("Someone is about to attack you.")
            .Starts("The thief is moving towards you ferociously.")
            .Say("You have fled by going north.");

        Assert.False(w.Tracker.InCombat);
        var totals = SessionCombatTotals.Empty.Accumulate(w.Snapshot());
        Assert.Equal((2, 0), (totals.Fights, totals.Kills));
    }

    // -- the fight records ---------------------------------------------------------------------

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mucka-unseen-pipeline-tests", Guid.NewGuid().ToString("N"));

    private readonly List<MuckaStore> _db = [];

    public void Dispose()
    {
        foreach (var db in _db) db.Dispose();
        TestStore.ReleasePools(_directory);
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void TheRecords_HoldTheGroupsBlows_AndOneSwinglessRowPerUnseenConclusion()
    {
        var db = TestStore.Open(Path.Combine(_directory, MuckaDb.DefaultFileName));
        _db.Add(db);
        var store = new FightHistoryStore(db);
        var recorder = new FightHistoryRecorder(store);
        var tracker = new CombatTracker();
        tracker.InCombatChanged += inCombat => recorder.OnInCombatChanged(inCombat);
        tracker.EventOccurred += recorder.OnCombatEvent;

        var at = 0;
        void Say(string text, LineKind kind = LineKind.Normal)
            => tracker.Observe(new StyledLine([new StyledSpan(text, TextStyle.Default)], kind: kind), T0.AddSeconds(++at));
        Say("The rat0 is moving towards you ferociously.", LineKind.FightStart);
        tracker.NoteCannotSee(true, T0.AddSeconds(++at), "test");
        Say("Someone is about to attack you.", LineKind.FightStart);
        Say("You hit something (2-3).");
        Say("Someone hits you (90/100).");
        Say("You have killed something.");
        Say("You hit someone (4-6).");
        Say("You have killed someone.");

        Assert.False(tracker.InCombat);
        var rows = store.Snapshot();
        var group = rows.Where(r => r.NpcName == AnonymousOpponent.Group).ToList();
        // The group's row with every anonymous blow, and one row per conclusion.
        var blows = Assert.Single(group, r => r.YouHits + r.TheyHits > 0);
        Assert.Equal((2, 1), (blows.YouHits, blows.TheyHits));
        Assert.Equal(nameof(FightOutcome.Unresolved), blows.Outcome);
        Assert.Equal(2, group.Count(r => r.Outcome == nameof(FightOutcome.Kill) && r.YouHits + r.TheyHits == 0));
        // The rat's outcome came by counting: the record never saw it end.
        Assert.Equal(nameof(FightOutcome.Unresolved), rows.Single(r => r.NpcName == "rat0").Outcome);
    }

    [Fact]
    public void ASlotConcludedByANamedLine_IsRecordedUnderTheName_WithNoSwings()
    {
        var db = TestStore.Open(Path.Combine(_directory, MuckaDb.DefaultFileName));
        _db.Add(db);
        var store = new FightHistoryStore(db);
        var recorder = new FightHistoryRecorder(store);
        var tracker = new CombatTracker();
        tracker.InCombatChanged += inCombat => recorder.OnInCombatChanged(inCombat);
        tracker.EventOccurred += recorder.OnCombatEvent;

        var at = 0;
        void Say(string text, LineKind kind = LineKind.Normal)
            => tracker.Observe(new StyledLine([new StyledSpan(text, TextStyle.Default)], kind: kind), T0.AddSeconds(++at));
        tracker.NoteCannotSee(true, T0, "test");
        Say("Something is about to attack you.", LineKind.FightStart);
        Say("You hit something (10-14).");
        Say("You have killed the basilisk.");

        Assert.False(tracker.InCombat);
        var rows = store.Snapshot();
        var basilisk = Assert.Single(rows, r => r.NpcName == "basilisk");
        Assert.Equal((nameof(FightOutcome.Kill), 0, 0), (basilisk.Outcome, basilisk.YouHits, basilisk.TheyHits));
        // Swing-less by construction, so not the fightbrief signature.
        Assert.False(basilisk.NarrativeMode);
        // The blows stay the group's.
        Assert.Equal(1, Assert.Single(rows, r => r.NpcName == AnonymousOpponent.Group).YouHits);
    }

    /// <summary>Both slots are matched by the two anonymous endings, so the thief9's coded flight
    /// has no slot to conclude: it is a bystander's, and makes no fight.</summary>
    [Fact]
    public void ACodedFlightOfANameWithNoAvailableSlot_MakesNoFight()
    {
        var w = new Wired()
            .Starts("The rat0 is about to attack you.")
            .Starts("Someone is about to attack you.")
            .Starts("Someone is about to attack you.")
            .Coded("Someone has fled by going north.", LineKind.FightEnd)
            .Say("You have killed someone.")
            .Coded("The thief9 has fled by going north.", LineKind.FightEnd);

        Assert.DoesNotContain(w.Seen, e => e.NpcName == "thief9" || e.ConcludedName == "thief9");
        Assert.DoesNotContain(w.Snapshot().Fights, f => f.NpcName == "thief9");
        Assert.True(w.Tracker.InCombat);
    }

    /// <summary>A blow to an open row that has swung before changes nothing the snapshot carries, so
    /// nothing is built for it.</summary>
    [Fact]
    public void ARepeatBlowToAnOpenRow_BuildsNoSnapshot()
    {
        var w = new Wired()
            .Starts("The rat0 is about to attack you.")
            .Say("You hit the rat0 (4-6).");
        var built = w.Tracker.SnapshotsBuilt;
        w.Say("You hit the rat0 (4-6).")
         .Say("The rat0 misses you.");
        Assert.Equal(built, w.Tracker.SnapshotsBuilt);
        w.Say("Something hits you (80/105).");
        Assert.Equal(built + 1, w.Tracker.SnapshotsBuilt);
    }
}
