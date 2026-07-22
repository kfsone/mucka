namespace MudSharp.Session;

/// <summary>
/// Projects the absolute next-reset instant from the minute-granular reset value FES reports
/// (field 13). The server FLOORS seconds→minutes, so a reading of value <c>v</c> at time <c>t</c>
/// means the true reset instant <c>R</c> satisfies <c>remaining = R - t ∈ [v·60, v·60+60)</c>, i.e.
///
/// <code>R ∈ [t + v·60, t + (v+1)·60)</code>
///
/// — a 60-second-wide constraint. The estimate is simply the INTERSECTION of these intervals over
/// recent fresh readings: <c>lo = max(t + v·60)</c>, <c>hi = min(t + (v+1)·60)</c>, with the target
/// at the midpoint and uncertainty at the half-width. Each reading can only tighten the window; a
/// reading whose interval is disjoint from the current one means a reset fired (or a long stall /
/// clock drift) and re-bootstraps the window to that reading alone.
///
/// <para>Routine heartbeat readings converge this to about half the heartbeat cadence on their own
/// (the two beats straddling a minute boundary pin it to their gap). To go below that you need a
/// reading nearer the boundary, which is what a precision probe buys — see
/// <see cref="TryGetPrecisionProbeDue"/>. Because probes cost the player a game turn, we only ask
/// for one once routine convergence has plateaued (<see cref="ProbeStartUncertaintySec"/>), at most
/// one per minute boundary, and we stop once we are within <see cref="SubSecondTargetSec"/> or have
/// hit the RTT/server-tick floor (<see cref="FloorBackoffTries"/> probe replies with no improvement).
/// Hitting the floor only pauses SCHEDULING; a later reset re-bootstraps and re-arms — we never give
/// up. "Can't measure" (asleep / stale / held) simply yields no fresh reading, so nothing shrinks,
/// nothing backs off, and nothing is lost.</para>
///
/// <para>Not internally locked: driven entirely from the UI thread (GameViewModel marshals
/// StatsUpdated there and calls <see cref="Tick"/> from the 1 Hz UI tick).</para>
/// </summary>
public sealed class ResetProjection
{
    /// <summary>Below this ± (seconds) we consider the projection dialled in and stop probing.</summary>
    public const double SubSecondTargetSec = 1.0;
    /// <summary>Don't spend a game turn until routine readings have brought us at least this tight —
    /// the coarse work (a whole minute down to ~half the heartbeat cadence) comes free.</summary>
    public const double ProbeStartUncertaintySec = 15.0;
    /// <summary>Uncertainty of a lone first sighting (midpoint of the 60 s minute).</summary>
    private const double MinuteUncertaintySec = 30.0;
    /// <summary>Consecutive precision-probe replies that fail to shrink the window before we accept
    /// we've hit the accuracy floor and pause scheduling.</summary>
    private const int FloorBackoffTries = 3;
    /// <summary>How close (seconds) to a predicted boundary crossing a 1 Hz tick must be to fire a
    /// probe. One tick's worth, so we always catch the crossing within a second before it.</summary>
    private const double ProbeLeadSec = 1.0;

    // Window on the true reset instant R: R ∈ [_lo, _hi). Null when no projection is held.
    private DateTime? _lo;
    private DateTime? _hi;

    // Precision-probe bookkeeping.
    private DateTime _pendingCrossingUtc;                 // crossing TryGetPrecisionProbeDue picked
    private DateTime _probedBoundaryUtc = DateTime.MinValue;   // boundary we last actually probed
    private bool _awaitingProbeReply;
    private int _floorStreak;
    private bool _floorReached;

    /// <summary>Projected reset instant (UTC), or null when nothing is projected / it has lapsed.</summary>
    public DateTime? TargetUtc =>
        _lo is DateTime lo && _hi is DateTime hi
            ? lo + TimeSpan.FromTicks((hi - lo).Ticks / 2)
            : null;

    /// <summary>Current ± uncertainty (seconds) in <see cref="TargetUtc"/>. Coarse default when the
    /// window is unknown so callers never imply precision we lack.</summary>
    public double UncertaintySec =>
        _lo is DateTime lo && _hi is DateTime hi
            ? (hi - lo).TotalSeconds / 2
            : MinuteUncertaintySec;

    /// <summary>
    /// Fold one stats update into the window. <paramref name="fresh"/> must be true only for a real
    /// FES reading (<c>HasFesStats</c>) — carried-forward values (combat/text lines echo the last
    /// reset value) are not observations of the counter and are ignored. <paramref name="serverMinutes"/>
    /// null is treated as no reading.
    /// </summary>
    public void Observe(int? serverMinutes, bool fresh, DateTime nowUtc)
    {
        if (!fresh || serverMinutes is not int mins || mins < 0)
            return;

        // The reading's 60 s constraint on R.
        var cLo = nowUtc + TimeSpan.FromSeconds(mins * 60);
        var cHi = nowUtc + TimeSpan.FromSeconds((mins + 1) * 60);

        // No window yet, or the reading is disjoint from what we hold (reset fired / long stall /
        // drift) → re-bootstrap to this reading alone and re-arm probing.
        if (_lo is not DateTime lo || _hi is not DateTime hi || cHi <= lo || cLo >= hi)
        {
            _lo = cLo;
            _hi = cHi;
            _floorStreak = 0;
            _floorReached = false;
            _awaitingProbeReply = false;
            return;
        }

        var beforeUnc = (hi - lo).TotalSeconds / 2;
        var newLo = cLo > lo ? cLo : lo;
        var newHi = cHi < hi ? cHi : hi;
        _lo = newLo;
        _hi = newHi;
        var afterUnc = (newHi - newLo).TotalSeconds / 2;

        // Floor accounting: only a reading we deliberately probed for counts. If it failed to
        // meaningfully shrink the window, the accuracy floor (RTT + server tick) is above our
        // target; after a few such replies, pause scheduling. A reply that does shrink resets the
        // streak. Routine (unprobed) readings never touch this, so a quiet stretch can't trip it.
        if (_awaitingProbeReply)
        {
            _awaitingProbeReply = false;
            if (afterUnc >= beforeUnc - 0.05)
            {
                if (++_floorStreak >= FloorBackoffTries)
                    _floorReached = true;
            }
            else
            {
                _floorStreak = 0;
            }
        }
    }

    /// <summary>
    /// True when a precision probe is worth spending a turn on right now: we're converging (routine
    /// readings have plateaued but we're not yet sub-second), the accuracy floor hasn't been hit,
    /// and a predicted minute-boundary crossing falls within this tick. At most one per boundary.
    /// The caller sends the probe (which may still be refused for spacing/sleep) and, only if it
    /// actually fired, calls <see cref="NotePrecisionProbeSent"/>.
    /// </summary>
    public bool TryGetPrecisionProbeDue(DateTime nowUtc)
    {
        if (_floorReached)
            return false;
        var unc = UncertaintySec;
        if (unc <= SubSecondTargetSec || unc > ProbeStartUncertaintySec)
            return false;
        if (TargetUtc is not DateTime target)
            return false;

        var secsToReset = (target - nowUtc).TotalSeconds;
        if (secsToReset <= 0)
            return false;

        // Time to the next whole-minute boundary of the current estimate. Boundaries recur every
        // 60 s at R's phase; the most informative reading sits right at one.
        var secsToBoundary = secsToReset - Math.Floor(secsToReset / 60.0) * 60.0;
        if (secsToBoundary >= ProbeLeadSec)
            return false;

        var crossing = nowUtc + TimeSpan.FromSeconds(secsToBoundary);
        // One probe per boundary — successive boundaries are 60 s apart.
        if ((crossing - _probedBoundaryUtc).Duration() < TimeSpan.FromSeconds(30))
            return false;

        _pendingCrossingUtc = crossing;
        return true;
    }

    /// <summary>Call after a probe requested via <see cref="TryGetPrecisionProbeDue"/> actually went
    /// out. Arms floor accounting for its reply and marks this boundary as probed.</summary>
    public void NotePrecisionProbeSent(DateTime nowUtc)
    {
        _awaitingProbeReply = true;
        _probedBoundaryUtc = _pendingCrossingUtc;
    }

    /// <summary>Advance the projection; clears a fully-lapsed window so a stale target stops ticking.
    /// Called from the 1 Hz UI tick.</summary>
    public void Tick(DateTime nowUtc)
    {
        if (_hi is DateTime hi && hi <= nowUtc)
            Clear();
    }

    /// <summary>Drop all projection state (back at the option menu, or disconnected).</summary>
    public void Clear()
    {
        _lo = null;
        _hi = null;
        _probedBoundaryUtc = DateTime.MinValue;
        _awaitingProbeReply = false;
        _floorStreak = 0;
        _floorReached = false;
    }
}
