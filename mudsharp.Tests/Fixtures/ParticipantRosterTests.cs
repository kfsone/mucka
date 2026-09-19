using MudSharp.Combat;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// The opposition roster. When participants are hidden by the row cap, the hidden-tail breakdown
/// must still distinguish "9 more, all down" from "9 more, all still up" - a live/dead count, not
/// just a total. <see cref="ParticipantRoster.Build"/> provides it: counts that survive the row cap,
/// plus that breakdown.
/// </summary>
public sealed class ParticipantRosterTests
{
    private static ParticipantFact Live(string name) => new(name, IsResolved: false, FightOutcome.Unresolved);
    private static ParticipantFact Dead(string name, FightOutcome outcome = FightOutcome.Kill)
        => new(name, IsResolved: true, outcome);

    [Fact]
    public void Build_Empty_ReturnsEmptyPlan()
    {
        var plan = ParticipantRoster.Build([]);
        Assert.Empty(plan.Rows);
        Assert.Equal(0, plan.TotalCount);
        Assert.False(plan.HasHidden);
    }

    [Fact]
    public void Build_UnderTheCap_ShowsEveryRowWithNoHiddenTail()
    {
        var plan = ParticipantRoster.Build([Live("rat0"), Live("rat1"), Dead("rat2")]);

        Assert.Equal(3, plan.Rows.Count);
        Assert.Equal(2, plan.LiveCount);
        Assert.Equal(1, plan.ResolvedCount);
        Assert.Equal(3, plan.TotalCount);
        Assert.False(plan.HasHidden);
    }

    [Fact]
    public void Build_LiveParticipantsSortBeforeResolvedOnes()
    {
        var plan = ParticipantRoster.Build([Dead("dead0"), Live("live0"), Dead("dead1"), Live("live1")]);

        Assert.Equal(["live0", "live1", "dead0", "dead1"], plan.Rows.Select(r => r.Name).ToArray());
    }

    [Fact]
    public void Build_FirstLiveRowIsMarkedAsTheCurrentTarget()
    {
        var plan = ParticipantRoster.Build([Dead("dead0"), Live("live0"), Live("live1")]);

        var live0 = plan.Rows.Single(r => r.Name == "live0");
        var live1 = plan.Rows.Single(r => r.Name == "live1");
        Assert.True(live0.IsCurrentTarget);
        Assert.False(live1.IsCurrentTarget);
    }

    [Fact]
    public void Build_WhenEverythingHasResolved_NothingIsMarkedAsTheCurrentTarget()
    {
        var plan = ParticipantRoster.Build([Dead("dead0"), Dead("dead1")]);

        Assert.All(plan.Rows, r => Assert.False(r.IsCurrentTarget));
    }

    [Fact]
    public void Build_TheReportedFourteenRatCase_CountsAndHidesCorrectly()
    {
        // 5 already dead, 9 more still alive and swinging. Expressed against MaxRows rather than a
        // literal cap, since the distinction under test is not about the cap's value.
        const int dead = 5;
        const int live = 9;
        var fights = Enumerable.Range(0, dead).Select(i => Dead($"dead{i}"))
            .Concat(Enumerable.Range(0, live).Select(i => Live($"live{i}")))
            .ToArray();

        var plan = ParticipantRoster.Build(fights);

        Assert.Equal(live, plan.LiveCount);
        Assert.Equal(dead, plan.ResolvedCount);
        Assert.Equal(dead + live, plan.TotalCount);
        Assert.Equal(ParticipantRoster.MaxRows, plan.Rows.Count);
        // Every shown row is a live one (live sorts first) - every single dead rat is hidden.
        Assert.All(plan.Rows, r => Assert.True(r.IsLive));
        Assert.Equal(dead + live - ParticipantRoster.MaxRows, plan.HiddenCount);
        // Critically: some of the hidden ones are STILL LIVE, not already dead.
        Assert.Equal(live - ParticipantRoster.MaxRows, plan.HiddenLiveCount);
    }

    [Fact]
    public void Build_HiddenTailAllResolved_HiddenLiveCountIsZero()
    {
        const int total = ParticipantRoster.MaxRows + 4;
        var fights = new[] { Live("live0") }
            .Concat(Enumerable.Range(0, total - 1).Select(i => Dead($"dead{i}")))
            .ToArray();

        var plan = ParticipantRoster.Build(fights);

        Assert.Equal(total - ParticipantRoster.MaxRows, plan.HiddenCount);
        // The one live participant sorts first, so it is always shown - the whole hidden tail is dead.
        Assert.Equal(0, plan.HiddenLiveCount);
    }

    [Fact]
    public void Build_ExactlyAtTheCap_HasNoHiddenTail()
    {
        var fights = Enumerable.Range(0, ParticipantRoster.MaxRows).Select(i => Live($"rat{i}")).ToArray();

        var plan = ParticipantRoster.Build(fights);

        Assert.Equal(ParticipantRoster.MaxRows, plan.Rows.Count);
        Assert.False(plan.HasHidden);
    }

    [Fact]
    public void Build_RowsCarryTheirOwnOutcomeForTheDescriptiveWordBelow()
    {
        var plan = ParticipantRoster.Build([Dead("rat0", FightOutcome.CFled), Dead("rat1", FightOutcome.Kill)]);

        Assert.Equal(FightOutcome.CFled, plan.Rows.Single(r => r.Name == "rat0").Outcome);
        Assert.Equal(FightOutcome.Kill, plan.Rows.Single(r => r.Name == "rat1").Outcome);
    }

    /// <summary>The `value` probe's point figure rides through to the row - and 0 (the ox) must
    /// stay distinguishable from "never asked" the whole way, exactly as the rail's rule 5 (an
    /// unknown must never render as a measured state) requires of every other figure here.</summary>
    [Fact]
    public void Build_CarriesValueThrough_AndKeepsAbsentDistinctFromZero()
    {
        var known = Live("rat0") with { Value = 0 };          // the ox case - a real, legal zero
        var unknown = Live("rat1");                            // never probed - Value defaults to null

        var plan = ParticipantRoster.Build([known, unknown]);

        Assert.Equal(0, plan.Rows.Single(r => r.Name == "rat0").Value);
        Assert.Null(plan.Rows.Single(r => r.Name == "rat1").Value);
    }

    // -- the unknown badges: the operator's cases under made-up names ----------------------------

    private static UnseenState Blind(int someone = 0, int something = 0) => new(someone, something, CannotSee: true);
    private static UnseenState Sighted(int someone = 0, int something = 0) => new(someone, something, CannotSee: false);

    [Fact]
    public void Build_NoUnseenOpponents_MakesNoBadge_AndCountsOneSlotPerLiveRow()
    {
        var plan = ParticipantRoster.Build([Live("snurfle0"), Live("snurfle1"), Dead("snurfle2")], Sighted());

        Assert.All(plan.Rows, r => Assert.False(r.IsUnseen));
        Assert.Equal(2, plan.LiveCount);
    }

    [Fact]
    public void Build_BlindWithTwoUnseenAndAnUnknownKindCreature_FoldsItIntoOneBadgeThreeSlotsTall()
    {
        // "Something is about to attack you." twice while the snurfle is engaged and the player is
        // blind: three candidates for the word, so the snurfle cannot be told from the two newcomers
        // and folds into their badge, which is as tall as the three of them.
        var plan = ParticipantRoster.Build(
            [Live("snurfle3"), Live(AnonymousOpponent.Thing)], Blind(something: 2));

        var badge = Assert.Single(plan.Rows);
        Assert.True(badge.IsUnseen);
        Assert.Equal(AnonymousOpponent.Thing, badge.Name);
        Assert.Equal(3, badge.SlotSpan);
        Assert.Equal("snurfle3, ???, ???", badge.UnseenLabel);
        Assert.True(badge.IsLive);
        Assert.True(badge.IsCurrentTarget);
        Assert.Equal(3, plan.LiveCount);   // opponents, one slot each - not the aggregator's two rows
    }

    [Fact]
    public void Build_BlindWithBothWordsOpen_FoldsAnUnknownKindCreatureIntoTheDefaultWordsBadge()
    {
        // An unknown-kind Creature is a candidate for either word. With both open it goes where
        // prose would already put it - SomeKinds.Default - and not wherever the loop looked first.
        var plan = ParticipantRoster.Build(
            [Live("snurfle0"), Live(AnonymousOpponent.Person), Live(AnonymousOpponent.Thing)],
            Blind(someone: 1, something: 1));

        Assert.Equal(2, plan.Rows.Count);
        var things = plan.Rows.Single(r => r.Name == SomeKinds.Word(SomeKinds.Default));
        var others = plan.Rows.Single(r => r.Name == SomeKinds.Word(SomeKinds.Other(SomeKinds.Default)));
        Assert.Equal("snurfle0, ???", things.UnseenLabel);
        Assert.Equal("???", others.UnseenLabel);
        Assert.Equal(3, plan.LiveCount);
    }

    [Fact]
    public void Build_ABadgeIsNeverWhatTheRowCapDrops()
    {
        // Eight named Creatures and a badge three deep: the badge keeps its row and the count is
        // exact, the eighth named Creature is the one that goes to the overflow row.
        var fights = Enumerable.Range(0, ParticipantRoster.MaxRows).Select(i => Live($"snurfle{i}"))
            .Append(Live(AnonymousOpponent.Thing))
            .ToArray();

        var plan = ParticipantRoster.Build(fights, Sighted(something: 3));

        Assert.Equal(ParticipantRoster.MaxRows, plan.Rows.Count);
        var badge = Assert.Single(plan.Rows, r => r.IsUnseen);
        Assert.Equal(3, badge.SlotSpan);
        Assert.Equal(1, plan.HiddenLiveCount);
        Assert.Equal(ParticipantRoster.MaxRows + 3, plan.LiveCount);
    }

    [Fact]
    public void Build_SightedWithOneAnnouncedSomeone_KeepsTheNamedCreatureOnItsOwnRow()
    {
        // Run 49's shape: the blorf is named on every line, an invisible player announced itself.
        // Sighted, the blorf is self-evidently not the unknown, so nothing folds.
        var plan = ParticipantRoster.Build(
            [Live("blorf5"), Live(AnonymousOpponent.Person)], Sighted(someone: 1));

        Assert.Equal(["blorf5", AnonymousOpponent.Person], plan.Rows.Select(r => r.Name));
        var badge = plan.Rows[1];
        Assert.True(badge.IsUnseen);
        Assert.Equal(1, badge.SlotSpan);
        Assert.Equal("???", badge.UnseenLabel);
        Assert.True(plan.Rows[0].IsCurrentTarget);
        Assert.False(badge.IsCurrentTarget);
        Assert.Equal(2, plan.LiveCount);
    }

    [Fact]
    public void Build_BlindWithKnownKinds_FoldsOnlyTheCreatureOfTheBadgesWord()
    {
        // The quazzle is a known "someone" on this install, the snurfle a known "something". One
        // unseen something announced: the snurfle is a candidate for it, the quazzle is not.
        var quazzle = Live("quazzle") with { Kind = SomeKind.Someone };
        var snurfle = Live("snurfle0") with { Kind = SomeKind.Something };
        var plan = ParticipantRoster.Build(
            [quazzle, snurfle, Live(AnonymousOpponent.Thing)], Blind(something: 1));

        Assert.Equal(["quazzle", AnonymousOpponent.Thing], plan.Rows.Select(r => r.Name));
        Assert.Equal("snurfle0, ???", plan.Rows[1].UnseenLabel);
        Assert.Equal(2, plan.Rows[1].SlotSpan);
    }

    [Fact]
    public void Build_BlindWithOneUnseenAndOneCandidate_FoldsThem_TwoCandidatesIsAlreadyUnknown()
    {
        // Blind, the snurfle engaged, ONE "Something is about to attack you.": two candidates, so
        // the badge is "snurfle0, ???" and two slots tall - the unknown state begins at the second
        // candidate, not the third.
        var plan = ParticipantRoster.Build(
            [Live("snurfle0"), Live(AnonymousOpponent.Thing)], Blind(something: 1));

        var badge = Assert.Single(plan.Rows);
        Assert.Equal("snurfle0, ???", badge.UnseenLabel);
        Assert.Equal(2, badge.SlotSpan);
    }

    [Fact]
    public void Build_AWordRowWithNothingAnnounced_IsABadgeOfOne_WhenNobodyCanBeFoldedIn()
    {
        // Sighted, an unexplained "Someone hits you" opened the word's own row. One unknown.
        var plan = ParticipantRoster.Build([Live("blorf5"), Live(AnonymousOpponent.Person)], Sighted());

        var badge = plan.Rows.Single(r => r.IsUnseen);
        Assert.Equal("???", badge.UnseenLabel);
        Assert.Equal(1, badge.SlotSpan);
    }

    [Fact]
    public void Build_AWordRowWithNothingAnnounced_ListsTheCandidatesWithoutAQuestionMark_WhenBlind()
    {
        // Blind, two unknown-kind Creatures engaged, "Something hits you" that neither could be
        // ruled out of: the blow is one of theirs, so the badge lists the two of them and no "???" -
        // nothing announced itself, so there is no third opponent to claim.
        var plan = ParticipantRoster.Build(
            [Live("snurfle0"), Live("podipooper1"), Live(AnonymousOpponent.Thing)], Blind());

        var badge = Assert.Single(plan.Rows);
        Assert.Equal("snurfle0, podipooper1", badge.UnseenLabel);
        Assert.Equal(2, badge.SlotSpan);
        Assert.Equal(2, plan.LiveCount);
    }

    [Fact]
    public void Build_BothWordsOpen_MakeTwoBadges_AboveTheNamedRows()
    {
        var plan = ParticipantRoster.Build(
            [Live("blorf5") with { Kind = SomeKind.Someone }, Live(AnonymousOpponent.Person), Live(AnonymousOpponent.Thing)],
            Sighted(someone: 1, something: 2));

        // The Default word's badge first (see Build), so "something" sits under "someone".
        Assert.Equal(["blorf5", AnonymousOpponent.Thing, AnonymousOpponent.Person], plan.Rows.Select(r => r.Name));
        Assert.Equal(2, plan.Rows[1].SlotSpan);
        Assert.Equal("???, ???", plan.Rows[1].UnseenLabel);
        Assert.Equal(1, plan.Rows[2].SlotSpan);
        Assert.Equal(4, plan.LiveCount);
    }

    [Fact]
    public void Build_AResolvedWordRow_IsNotABadge_ItIsADeath()
    {
        // "You have killed something." closed the word's row; nothing is open. The dead strip gets it.
        var plan = ParticipantRoster.Build([Dead(AnonymousOpponent.Thing)], Sighted());

        var row = Assert.Single(plan.Rows);
        Assert.False(row.IsUnseen);
        Assert.False(row.IsLive);
        Assert.Equal(0, plan.LiveCount);
    }

    [Fact]
    public void Build_TwoPlansWithTheSameBadge_AreEqual_SoTheRailSkipsTheRepaint()
    {
        var a = ParticipantRoster.Build([Live("snurfle0"), Live(AnonymousOpponent.Thing)], Blind(something: 1));
        var b = ParticipantRoster.Build([Live("snurfle0"), Live(AnonymousOpponent.Thing)], Blind(something: 1));
        var c = ParticipantRoster.Build([Live("snurfle0"), Live(AnonymousOpponent.Thing)], Blind(something: 2));

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }
}
