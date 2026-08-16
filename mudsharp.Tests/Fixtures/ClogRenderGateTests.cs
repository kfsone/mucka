using Mucka.ViewModels;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// Coverage for the coalescing gate behind the clog window's pack-fight fix (see
/// SidePanelViewModel.OnCombatEvent/OnStatsUpdated/TickCombatDisplay). The gate is what stops a
/// burst of combat events from rebuilding the native FormattedString (ClogPage.Render) far faster
/// than anyone can read it, while guaranteeing the eventual state is never lost.
/// </summary>
public sealed class ClogRenderGateTests
{
    private static readonly DateTime T0 = new(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(200);

    // -- basic throttle behaviour ------------------------------------------------

    [Fact]
    public void RequestRender_FirstCallAlwaysRendersImmediately()
    {
        // No prior render to measure a window against, so the very first event must not be held
        // back - an idle-to-combat transition should show something at once.
        var gate = new ClogRenderGate(Interval);

        Assert.True(gate.RequestRender(T0));
        Assert.False(gate.IsDirty);
    }

    [Fact]
    public void RequestRender_WithinTheWindow_MarksDirtyInsteadOfRenderingAgain()
    {
        var gate = new ClogRenderGate(Interval);
        gate.RequestRender(T0);

        var renderedAgain = gate.RequestRender(T0 + TimeSpan.FromMilliseconds(50));

        Assert.False(renderedAgain);
        Assert.True(gate.IsDirty);
    }

    [Fact]
    public void RequestRender_PastTheWindow_RendersAgainAndClearsDirty()
    {
        var gate = new ClogRenderGate(Interval);
        gate.RequestRender(T0);
        gate.RequestRender(T0 + TimeSpan.FromMilliseconds(50));   // throttled, now dirty

        var rendered = gate.RequestRender(T0 + TimeSpan.FromMilliseconds(250));

        Assert.True(rendered);
        Assert.False(gate.IsDirty);
    }

    [Fact]
    public void RequestRender_ExactlyAtTheWindowBoundary_IsAllowed()
    {
        // The comparison is "< interval", not "<= interval" - a render exactly Interval later is
        // not still inside the throttle window.
        var gate = new ClogRenderGate(Interval);
        gate.RequestRender(T0);

        Assert.True(gate.RequestRender(T0 + Interval));
    }

    // -- rate bounding under a burst ---------------------------------------------

    [Fact]
    public void RequestRender_ABurstOfEventsCollapsesToTheRateCap()
    {
        // Simulates roughly what an 11-participant pack fight can do: one event every 30 ms for a
        // full second (~33 events). Rendering must never exceed the gate's own interval, so over a
        // 1 s burst at a 200 ms window the render count must stay near 1000/200 = 5, not 33.
        var gate = new ClogRenderGate(Interval);
        var rendersTriggered = 0;
        for (var ms = 0; ms <= 1000; ms += 30)
        {
            if (gate.RequestRender(T0 + TimeSpan.FromMilliseconds(ms)))
                rendersTriggered++;
        }

        Assert.InRange(rendersTriggered, 1, 7);   // generous margin either side of the ~5 expected
    }

    [Fact]
    public void RequestRender_EventsSpacedWiderThanTheWindow_AllRenderImmediately()
    {
        // An isolated event (nothing else happening around it) must never be held back just
        // because the gate exists - only genuine bursts should be coalesced.
        var gate = new ClogRenderGate(Interval);

        Assert.True(gate.RequestRender(T0));
        Assert.True(gate.RequestRender(T0 + TimeSpan.FromSeconds(1)));
        Assert.True(gate.RequestRender(T0 + TimeSpan.FromSeconds(2)));
    }

    // -- no lost render -----------------------------------------------------------

    [Fact]
    public void MarkRendered_ClearsAPendingDirtyFlagEvenInsideTheThrottleWindow()
    {
        // Models a combat-end transition arriving while an event is still throttled: the direct,
        // unconditional render (MarkRendered) must be able to flush the final state regardless of
        // where the throttle window currently sits, and must leave nothing dirty behind it.
        var gate = new ClogRenderGate(Interval);
        gate.RequestRender(T0);
        gate.RequestRender(T0 + TimeSpan.FromMilliseconds(10));   // throttled -> dirty
        Assert.True(gate.IsDirty);

        gate.MarkRendered(T0 + TimeSpan.FromMilliseconds(15));

        Assert.False(gate.IsDirty);
    }

    [Fact]
    public void MarkRendered_ResetsTheWindowSoTheNextEventIsThrottledFromThatMoment()
    {
        var gate = new ClogRenderGate(Interval);
        gate.MarkRendered(T0);

        // Still inside the window measured from the MarkRendered call, not from any earlier state.
        Assert.False(gate.RequestRender(T0 + TimeSpan.FromMilliseconds(50)));
        Assert.True(gate.IsDirty);
    }

    [Fact]
    public void Dirty_StaysFalseWhenEveryEventAlreadyRenderedOnItsOwn()
    {
        // The common (non-burst) case: nothing should ever be marked dirty if every event landed
        // outside the throttle window on its own.
        var gate = new ClogRenderGate(Interval);
        gate.RequestRender(T0);
        gate.RequestRender(T0 + TimeSpan.FromSeconds(1));

        Assert.False(gate.IsDirty);
    }
}
