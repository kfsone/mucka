using MudSharp.Combat;
using Mucka.ViewModels;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// Pins <see cref="CombatLiveView"/>'s equality contract - the property
/// <c>Mucka.Rendering.CombatRailView.Live</c>'s setter relies on to decide whether to repaint.
///
/// <para><b>Why this file exists (2026-09-02 review finding).</b> That setter used to guard with
/// <c>ReferenceEquals</c> alone, and <c>SidePanelViewModel.RefreshCombatSignals</c> allocates a
/// fresh <see cref="CombatLiveView"/> on every refresh - a <c>with</c>-expression in the idle
/// branch, <c>new</c> in the two in-combat branches - including the 1 Hz anti-idle tick that runs
/// whether or not anything actually changed. A reference check alone therefore never matched, and
/// the rail repainted once a second for as long as a combat summary stayed on screen, contradicting
/// <c>CombatRailView</c>'s own Invariant #1.</para>
///
/// <para><b>Why that fix was only half of one (2026-09-03).</b> Comparing by value fixed the IDLE
/// branch and nothing else. <c>RosterPlan.Rows</c> is an <c>IReadOnlyList&lt;RosterRow&gt;</c>, and
/// the synthesized record equality compares a reference-typed member of that shape with
/// <c>EqualityComparer&lt;T&gt;.Default</c> - i.e. by reference, since neither <c>List&lt;T&gt;</c>
/// nor an array overrides <c>Object.Equals</c>. <c>ParticipantRoster.Build</c> allocates a fresh
/// list on every refresh, so on the IN-COMBAT branch - the only branch reached while a fight is
/// open, and the branch where a repaint actually costs something - the new comparison decided
/// "different" every single time and the rail went on repainting at 1 Hz.
/// <c>RosterPlan</c> now declares a hand-written element-wise <c>Equals</c>; the tests below that
/// rebuild a roster from the same facts are what prove it.</para>
///
/// <para><b>A test used to assert the bug.</b> An earlier version of this file carried
/// <c>ARosterRebuiltWithIdenticalRowValues_StillDoesNotCompareEqual</c>, which pinned the
/// reference-equality behaviour as intentional on the reasoning that "fields like
/// <c>RosterRow.HealthAgeSeconds</c> genuinely advance every second" so an unequal verdict was
/// wanted anyway. That reasoning does not hold: those fields advance only while an encounter is
/// live, the same rebuild happens on every refresh of a FINISHED encounter and every idle second
/// with a dead strip on screen, and a comparison that answers "different" without looking is not a
/// comparison. It is replaced by
/// <c>ARosterRebuiltWithIdenticalRowValues_ComparesEqual_SoTheRailDoesNotRepaint</c> below, with
/// <c>ARowFieldThatOnlyTheSealDraws_StillCountsAsAChange</c> covering the real-change direction it
/// was worried about.</para>
///
/// <para><b>Why this pins the record rather than the setter.</b> <c>CombatRailView</c> is an
/// <c>SKCanvasView</c> in the MAUI-only Mucka project and is not reachable from mudsharp.Tests, so
/// there is no way to call its setter directly from here. <c>CombatContracts.cs</c> - which holds
/// <see cref="CombatLiveView"/> and is linked into this project precisely so its pure pieces stay
/// testable - is reachable, and the setter's whole decision is
/// <c>ReferenceEquals(_live, value) || _live == value</c>, so pinning that <c>==</c> directly is the
/// reachable proxy for "does the setter now decide correctly whether to repaint".</para>
/// </summary>
public sealed class CombatLiveViewEqualityTests
{
    private static CombatLiveView Frame(RosterPlan roster, IReadOnlyList<CombatEnding> deadStrip)
        => new(
            InCombat: false, HasEncounter: true, WeaponText: string.Empty, IsUnarmed: false,
            Roster: roster, StaminaCurrent: 40, StaminaMax: 40, ObjectsCarried: 3, Score: 1_000,
            DeadStripHistory: deadStrip);

    [Fact]
    public void TwoFramesBuiltFromTheSameRosterAndHistoryReferences_CompareEqual()
    {
        // Mirrors the actual idle-with-a-dead-strip publish: SidePanelViewModel's out-of-combat
        // branch is `CombatLiveView.Idle with { HasEncounter = true, DeadStripHistory =
        // _archiveSnapshot }`, run fresh every refresh - a NEW CombatLiveView instance every time,
        // but its Roster and DeadStripHistory both come from the SAME underlying references
        // (RosterPlan.Empty, _archiveSnapshot) on every tick, since neither is rebuilt while the
        // encounter stays idle. Two such frames must compare equal, or the 1 Hz anti-idle tick
        // forces a repaint forever with nothing on screen actually changing.
        var deadStrip = new[] { new CombatEnding("rat0", FightOutcome.Kill, DateTime.UtcNow) };

        var first = Frame(RosterPlan.Empty, deadStrip);
        var second = Frame(RosterPlan.Empty, deadStrip);

        Assert.Equal(first, second);
        Assert.True(first == second);
    }

    [Fact]
    public void ARosterRebuiltWithADifferentRow_DoesNotCompareEqual()
    {
        // Mirrors the in-combat publish: SidePanelViewModel rebuilds the roster
        // (ParticipantRoster.Build) fresh on every refresh, so the published RosterPlan's Rows list
        // is a new instance every tick. A refresh where what is actually drawn has changed - here,
        // the one participant resolving from live to a kill - must not compare equal to the frame
        // before it, or a real change would stop repainting.
        var deadStrip = Array.Empty<CombatEnding>();
        var beforeKill = ParticipantRoster.Build(
            [new ParticipantFact("rat0", IsResolved: false, FightOutcome.Unresolved)]);
        var afterKill = ParticipantRoster.Build(
            [new ParticipantFact("rat0", IsResolved: true, FightOutcome.Kill)]);

        var first = Frame(beforeKill, deadStrip);
        var second = Frame(afterKill, deadStrip);

        Assert.NotEqual(first, second);
        Assert.False(first == second);
    }

    [Fact]
    public void TwoRosterBuildsFromTheSameFacts_ProduceDistinctListInstances()
    {
        // The premise the two tests below rest on. If Build ever started returning a cached list
        // they would go green for the wrong reason and stop covering anything.
        var fact = new ParticipantFact("rat0", IsResolved: false, FightOutcome.Unresolved);
        var a = ParticipantRoster.Build([fact]);
        var b = ParticipantRoster.Build([fact]);

        Assert.False(ReferenceEquals(a.Rows, b.Rows));
    }

    [Fact]
    public void ARosterRebuiltWithIdenticalRowValues_ComparesEqual_SoTheRailDoesNotRepaint()
    {
        // THE regression. This is the 1 Hz tick during a live fight in which nothing happened: the
        // roster is rebuilt from the same facts into a new list, and the frame is otherwise
        // identical. Before RosterPlan grew its own Equals this compared unequal and the rail
        // repainted, every second, for the whole fight.
        var deadStrip = Array.Empty<CombatEnding>();
        var fact = new ParticipantFact("rat0", IsResolved: false, FightOutcome.Unresolved);

        var first = Frame(ParticipantRoster.Build([fact]), deadStrip);
        var second = Frame(ParticipantRoster.Build([fact]), deadStrip);

        Assert.Equal(first, second);
        Assert.True(first == second);
    }

    [Fact]
    public void APackRebuiltUnchanged_ComparesEqual()
    {
        // Same again with several rows, so the element-wise loop is actually exercised rather than
        // a single-element short circuit.
        var deadStrip = Array.Empty<CombatEnding>();
        ParticipantFact[] facts =
        [
            new("zombie", IsResolved: false, FightOutcome.Unresolved),
            new("rat17",  IsResolved: true,  FightOutcome.Kill),
            new("rat18",  IsResolved: false, FightOutcome.Unresolved),
        ];

        Assert.True(Frame(ParticipantRoster.Build(facts), deadStrip)
                 == Frame(ParticipantRoster.Build(facts), deadStrip));
    }

    [Fact]
    public void ARowFieldThatOnlyTheSealDraws_StillCountsAsAChange()
    {
        // The direction the deleted test was right to worry about. The health rung moves often and
        // changes nothing else on the row, so if the comparison were made coarse - names and counts
        // only - this is what would go stale on screen.
        var deadStrip = Array.Empty<CombatEnding>();
        var before = ParticipantRoster.Build(
            [new ParticipantFact("rat0", IsResolved: false, FightOutcome.Unresolved) with { HealthRung = 6 }]);
        var after = ParticipantRoster.Build(
            [new ParticipantFact("rat0", IsResolved: false, FightOutcome.Unresolved) with { HealthRung = 4 }]);

        Assert.False(Frame(before, deadStrip) == Frame(after, deadStrip));
    }

    [Fact]
    public void ARosterWithADifferentNumberOfRows_DoesNotCompareEqual()
    {
        var deadStrip = Array.Empty<CombatEnding>();
        var one = ParticipantRoster.Build(
            [new ParticipantFact("rat0", IsResolved: false, FightOutcome.Unresolved)]);
        var two = ParticipantRoster.Build(
        [
            new ParticipantFact("rat0", IsResolved: false, FightOutcome.Unresolved),
            new ParticipantFact("rat1", IsResolved: false, FightOutcome.Unresolved),
        ]);

        Assert.False(Frame(one, deadStrip) == Frame(two, deadStrip));
    }

    [Fact]
    public void EqualRosterPlans_HashEqually()
    {
        // Equals without a consistent GetHashCode is a latent bug the moment anything dictionaries
        // or sets one of these.
        var fact = new ParticipantFact("rat0", IsResolved: false, FightOutcome.Unresolved);
        var a = ParticipantRoster.Build([fact]);
        var b = ParticipantRoster.Build([fact]);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }
}
