using MudSharp.Combat;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// Covers the opposition roster (DESIGN_FINAL.md, amended): the owner's direct complaint that a
/// 14-rat fight rendered "5 dead rats and 9 more" with the 9 hidden participants' live/dead status
/// simply unknown. <see cref="ParticipantRoster.Build"/> is the fix - counts that survive the row cap,
/// and a hidden-tail breakdown that never collapses "9 more, all down" and "9 more, all still up" into
/// the same sentence.
/// </summary>
public sealed class ParticipantRosterTests
{
    private static ParticipantFact Live(string name) => new(name, IsResolved: false, FightOutcome.Unresolved);
    private static ParticipantFact Dead(string name, FightOutcome outcome = FightOutcome.Killed)
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
        // The exact failure case: 5 already dead, 9 more still alive and swinging - the old panel's
        // "and 9 more" line gave no way to tell which. Expressed against MaxRows rather than a literal
        // cap, because the cap has moved once already (5 -> 8, to stop it overruling the rail's own
        // height-derived slot count) and the distinction under test is not about its value.
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
        // Critically: some of the hidden ones are STILL LIVE, not already dead - the exact distinction
        // the old "and 9 more" line could never make.
        Assert.Equal(live - ParticipantRoster.MaxRows, plan.HiddenLiveCount);
        Assert.Equal(dead, plan.HiddenResolvedCount);
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
        Assert.Equal(total - ParticipantRoster.MaxRows, plan.HiddenResolvedCount);
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
        var plan = ParticipantRoster.Build([Dead("rat0", FightOutcome.NpcFled), Dead("rat1", FightOutcome.Killed)]);

        Assert.Equal(FightOutcome.NpcFled, plan.Rows.Single(r => r.Name == "rat0").Outcome);
        Assert.Equal(FightOutcome.Killed, plan.Rows.Single(r => r.Name == "rat1").Outcome);
    }
}
