namespace Mucka.ViewModels;

/// <summary>
/// Bounds how often the clog readout is allowed to rebuild and republish.
///
/// <para>Rendering rebuilds a native FormattedString - one WinUI Run per styled span - which is a
/// full teardown-plus-remeasure on the UI thread (see ClogPage.Render). Combat events and the FES
/// stats heartbeat can fire many times per second, especially in a pack fight, but MUD2's own
/// combat tick is roughly 2 seconds (see tools/combat/NOTES.md's duration clustering), so
/// rendering faster than a few times a second is UI-thread work the player cannot perceive -
/// exactly what CLAUDE.md's Invariant #1 forbids.</para>
///
/// <para>Usage: every event that WOULD trigger a render calls <see cref="RequestRender"/>. If
/// enough time has passed since the last render it returns true (render now, and the gate
/// remembers that moment) so an isolated event still updates promptly; otherwise it marks the
/// display dirty and returns false, deferring to a later unconditional render - in
/// SidePanelViewModel that later render is the existing 1 Hz tick (TickCombatDisplay), which is
/// itself well under the rate cap here and so is never held back by it. A transition that must
/// never be missed (combat ending, the summary being cleared) should render directly and then
/// call <see cref="MarkRendered"/> to keep the gate's bookkeeping in step, rather than going
/// through <see cref="RequestRender"/> and risking a skip.</para>
///
/// <para>Pure and stateless of any MAUI type, so - like ClogDisplay.cs and
/// CombatHistoryFormatter.cs - it is linked directly into mudsharp.Tests.</para>
/// </summary>
public sealed class ClogRenderGate
{
    /// <summary>~4.5 Hz: comfortably above MUD2's own ~2 s combat tick (so no real update is ever
    /// delayed by this bound) and comfortably below the point where native span rebuilds start
    /// competing with typing.</summary>
    public static readonly TimeSpan DefaultMinInterval = TimeSpan.FromMilliseconds(220);

    private readonly TimeSpan _minInterval;
    private DateTime? _lastRenderUtc;
    private bool _dirty;

    public ClogRenderGate(TimeSpan? minInterval = null)
        => _minInterval = minInterval ?? DefaultMinInterval;

    /// <summary>True when an event was throttled and is waiting for the next unconditional render
    /// to carry its (already-current) state onto the screen.</summary>
    public bool IsDirty => _dirty;

    /// <summary>
    /// Call on every event that would otherwise trigger a render. Returns true when the caller
    /// should render right now - which also resets the window, so a burst of events collapses to
    /// at most one render per <see cref="DefaultMinInterval"/> - or false when the render was
    /// throttled, in which case the gate is left dirty for the next unconditional flush to pick up.
    /// </summary>
    public bool RequestRender(DateTime nowUtc)
    {
        if (_lastRenderUtc is DateTime last && nowUtc - last < _minInterval)
        {
            _dirty = true;
            return false;
        }

        MarkRendered(nowUtc);
        return true;
    }

    /// <summary>
    /// Records that a render happened at <paramref name="nowUtc"/> regardless of the throttle
    /// window, and clears the dirty flag. Call this after any unconditional render (the 1 Hz tick,
    /// a combat-end transition, an explicit clear) so subsequent <see cref="RequestRender"/> calls
    /// measure their window from the render that actually happened rather than from a stale
    /// timestamp.
    /// </summary>
    public void MarkRendered(DateTime nowUtc)
    {
        _lastRenderUtc = nowUtc;
        _dirty = false;
    }
}
