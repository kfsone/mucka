using MudSharp.Session;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// A clock a fixture moves by hand, plus the timers hanging off it. <see cref="Attach"/> gives a
/// <see cref="MudSession"/> this clock for every probe-path instant it reads, this clock's
/// monotonic counterpart for the reset clock, and timers whose deadlines are measured on it - so
/// a probe fires because <see cref="Advance"/> crossed its deadline, never because real time
/// passed. Everything a step is due lands synchronously, before <see cref="Advance"/> returns.
///
/// <para>A fixture that uses this asserts the probe machinery's own arithmetic - what was armed,
/// what replaced it, what was still pending when the step crossed it - rather than what a loaded
/// machine got round to within a margin.</para>
/// </summary>
internal sealed class VirtualSessionClock
{
    /// <summary>Guard against a timer that re-arms with no delay turning a step into a spin.</summary>
    private const int MaxCallbacksPerStep = 10_000;

    private readonly object _gate = new();
    private readonly List<VirtualTimer> _timers = new();
    private readonly DateTime _start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private DateTime _now;

    public VirtualSessionClock() => _now = _start;

    /// <summary>The current virtual instant - what the session reads as its probe clock.</summary>
    public DateTime UtcNow
    {
        get { lock (_gate) return _now; }
    }

    /// <summary>
    /// A machine that has been up an hour when the fixture starts. The production monotonic clock
    /// counts from boot, and the reset clock measures "how long since a fresh reply" against it
    /// with a never-set stamp of zero - so a clock starting at zero would make a session that has
    /// never had a reply read as freshly answered, which it does not in production.
    /// </summary>
    private static readonly long MonoBaseMs = 3_600_000;

    /// <summary>The same instant as monotonic milliseconds.</summary>
    public long MonoMs
    {
        get { lock (_gate) return MonoBaseMs + (long)(_now - _start).TotalMilliseconds; }
    }

    /// <summary>Point a session's three timing seams at this clock. Call before feeding it anything.</summary>
    public void Attach(MudSession session)
    {
        session.ProbeClock = () => UtcNow;
        session.MonoClock = () => MonoMs;
        session.SessionTimerFactory = Create;
    }

    private VirtualTimer Create(Action callback)
    {
        var timer = new VirtualTimer(this, callback);
        lock (_gate) _timers.Add(timer);
        return timer;
    }

    /// <summary>
    /// Move the clock forward, firing each deadline the step crosses. The clock is set to each
    /// deadline's own instant before its callback runs, not to the end of the step: the callbacks
    /// compute the next beat's phase and the probe-spacing floor from the clock, and a callback
    /// that read the far end of the step would place them wrong.
    /// </summary>
    public void Advance(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);
        DateTime target;
        lock (_gate) target = _now + by;

        for (int fired = 0; ; fired++)
        {
            if (fired > MaxCallbacksPerStep)
                throw new InvalidOperationException(
                    $"virtual clock step fired {MaxCallbacksPerStep} callbacks - a timer is re-arming with no delay");

            VirtualTimer? due = null;
            lock (_gate)
            {
                DateTime at = default;
                foreach (var t in _timers)
                {
                    if (t.DueAt is not { } d || d > target) continue;
                    if (due is null || d < at) { due = t; at = d; }
                }
                if (due is null)
                {
                    _now = target;
                    return;
                }
                _now = at;
                due.ArmNextLocked();
            }
            // Outside the gate: the callback takes the session's probe lock and sends bytes, and
            // may arm further timers through Change.
            due.Fire();
        }
    }

    /// <summary>
    /// One deadline on the enclosing clock. Holding the deadline rather than a mere "armed" flag is
    /// what makes a missing re-arm visible - a flag would already be set, so a timer the session
    /// failed to replace would look identical to one it did.
    /// </summary>
    private sealed class VirtualTimer : ISessionTimer
    {
        private readonly VirtualSessionClock _clock;
        private readonly Action _callback;
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public VirtualTimer(VirtualSessionClock clock, Action callback)
        {
            _clock = clock;
            _callback = callback;
        }

        /// <summary>Guarded by the clock's gate.</summary>
        public DateTime? DueAt { get; private set; }

        public void Change(TimeSpan due, TimeSpan period)
        {
            lock (_clock._gate)
            {
                DueAt = _clock._now + due;
                _period = period;
            }
        }

        public void Stop()
        {
            lock (_clock._gate)
            {
                DueAt = null;
                _period = Timeout.InfiniteTimeSpan;
            }
        }

        public void Dispose()
        {
            lock (_clock._gate)
            {
                DueAt = null;
                _clock._timers.Remove(this);
            }
        }

        /// <summary>Re-arm (periodic) or clear (one-shot) before the callback runs, so a callback
        /// that arms its own next deadline is not overwritten afterwards. Caller holds the gate.</summary>
        public void ArmNextLocked()
            => DueAt = _period > TimeSpan.Zero ? _clock._now + _period : null;

        public void Fire() => _callback();
    }
}
