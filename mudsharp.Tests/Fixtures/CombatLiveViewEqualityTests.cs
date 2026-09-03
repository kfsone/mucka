using MudSharp.Combat;
using Mucka.ViewModels;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// Pins <see cref="CombatLiveView"/>'s equality contract - the property
/// <c>Mucka.Rendering.CombatRailView.Live</c>'s setter now relies on to decide whether to repaint.
///
/// <para><b>Why this file exists (2026-09-02 review finding).</b> That setter used to guard with
/// <c>ReferenceEquals</c> alone, and <c>SidePanelViewModel.RefreshCombatSignals</c> allocates a
/// fresh <see cref="CombatLiveView"/> on every refresh - a <c>with</c>-expression in the idle
/// branch, <c>new</c> in the two in-combat branches - including the 1 Hz anti-idle tick that runs
/// whether or not anything actually changed. A reference check alone therefore never matched, and
/// the rail repainted once a second for as long as a combat summary stayed on screen, contradicting
/// <c>CombatRailView</c>'s own Invariant #1. The fix compares by value first (both
/// <see cref="CombatLiveView"/>, a sealed record, and <c>RosterPlan</c>, a record struct, already
/// have member-wise equality), keeping <c>ReferenceEquals</c> only as a fast path.</para>
///
/// <para><b>Why this pins the record rather than the setter.</b> <c>CombatRailView</c> is an
/// <c>SKCanvasView</c> in the MAUI-only Mucka project and is not reachable from mudsharp.Tests, so
/// there is no way to call its setter directly from here. <c>CombatContracts.cs</c> - which holds
/// <see cref="CombatLiveView"/> and is linked into this project precisely so its pure pieces stay
/// testable - is reachable, and the setter's whole fix is "compare with <c>==</c> instead of only
/// <c>ReferenceEquals</c>", so pinning that <c>==</c>/<c>Equals</c> behaviour directly is the
/// reachable proxy for "does the setter now decide correctly whether to repaint".</para>
/// </summary>
public sealed class CombatLiveViewEqualityTests
{
    private static CombatLiveView Frame(RosterPlan roster, IReadOnlyList<CombatEnding> deadStrip)
        => new(
            InCombat: false, HasEncounter: true, WeaponText: string.Empty, IsUnarmed: false,
            Roster: roster, StaminaCurrent: 40, StaminaMax: 40, ObjectsCarried: 3,
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
        // forces a repaint forever with nothing on screen actually changing - the bug this test
        // exists to catch a regression of.
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
    public void ARosterRebuiltWithIdenticalRowValues_StillDoesNotCompareEqual()
    {
        // The other half of the in-combat story, and the reason the fix does not need to (and must
        // not try to) walk RosterPlan.Rows for a deep comparison: RosterPlan.Rows is an
        // IReadOnlyList<RosterRow>, a reference type, and the record-generated equality for a
        // reference-typed member compares it by reference (EqualityComparer<T>.Default for an
        // interface type with no IEquatable<T> falls back to Object.Equals, which List<T> and
        // arrays never override). So two roster builds with byte-for-byte identical rows still
        // compare unequal here purely because ParticipantRoster.Build allocates a new list each
        // call - which is exactly what keeps every genuine in-combat tick repainting (fields like
        // RosterRow.HealthAgeSeconds genuinely advance every second) without this equality check
        // ever needing to walk the roster's contents to prove it.
        var deadStrip = Array.Empty<CombatEnding>();
        var fact = new ParticipantFact("rat0", IsResolved: false, FightOutcome.Unresolved);

        var first = Frame(ParticipantRoster.Build([fact]), deadStrip);
        var second = Frame(ParticipantRoster.Build([fact]), deadStrip);

        Assert.NotEqual(first, second);
    }
}
