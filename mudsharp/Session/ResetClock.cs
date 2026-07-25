using System.Diagnostics;

namespace MudSharp.Session;

/// <summary>The engine's current activity — surfaced for display/diagnostics.</summary>
public enum ResetPhase
{
    /// <summary>Not in game / no reading yet.</summary>
    Idle,
    /// <summary>Folding routine heartbeat readings; not yet locked, may arm discovery.</summary>
    Coarse,
    /// <summary>Owning the channel and sampling ~2/s to bracket a minute-decrement edge.</summary>
    Discovering,
    /// <summary>Locked to the success target; all probing stopped for the session.</summary>
    Locked,
    /// <summary>Locked earlier, then the reset fired (time went up); coarse-tracking the new cycle.</summary>
    CoarseOnly,
}

/// <summary>Immutable projection snapshot read by the UI countdown.</summary>
public readonly record struct ResetEstimate(DateTime? TargetUtc, double UncertaintySec, ResetPhase Phase);

/// <summary>One folded observation, for optional diagnostic logging.</summary>
public readonly record struct ResetObservation(
    double RttMs, int Minutes, bool Sample,
    double WindowLoSecFromNow, double WindowHiSecFromNow, double UncertaintySec, ResetPhase Phase);

/// <summary>
/// Projects the absolute next-reset instant from the minute-granular reset value FES reports. The
/// server FLOORS seconds→minutes, so a reading <c>v</c> observed at <c>t</c> means the reset instant
/// <c>R ∈ [t + v·60, t + (v+1)·60)</c>. Routine 5 s heartbeats, intersected, converge this to ~half the
/// heartbeat cadence. To reach sub-second we run a ONE-TIME edge search near a predicted decrement.
///
/// <para>EDGE SEARCH (rate-limit-aware): the server rate-limits FES to roughly one answered probe per
/// ~500 ms and returns a bare prompt (no stats) if we ask faster or while a compound heartbeat reply is
/// still enroute. So discovery OWNS THE CHANNEL: it suspends the routine heartbeat, waits a
/// clear-channel lead (nothing regular enroute), then samples one-in-flight at ≥ ~501 ms. That brackets
/// a 60 s edge to ≈ ±250 ms in a single pass — inside the success target — then it LOCKS and never
/// probes again for the session. A pass stepped on by the player (unanswered samples / timeouts) simply
/// retries at the next minute boundary.</para>
///
/// <para>RE-ANCHORING: a normal countdown only decreases, so an UPWARD jump past the window is a
/// server reset event (main counter → finish-up, or → new cycle) and re-anchors. A reading that
/// contradicts a lock DOWNWARD (the reset came sooner than the lock said — e.g. an early decrement) is
/// NOT ignored: it is annotated to the capture, re-anchored, and re-verified. We never silently trust a
/// stale lock and never panic to coarse on an ordinary reading.</para>
///
/// <para>C06 C04 ("Auto reset initiated, you have 120 seconds…") gives an EXACT anchor for the final
/// countdown — see <see cref="NoteAutoResetInitiated"/>.</para>
///
/// <para>THREADING: <see cref="Observe"/> runs on the read-loop thread; the pacing timer fires on a
/// ThreadPool thread. All state is guarded by <c>_lock</c>. Lock order engine→<c>_fesLock</c>: the only
/// outward calls under the lock are the send / discovery-hold callbacks.</para>
/// </summary>
public sealed class ResetClock : IDisposable
{
    private const double MinuteUncertaintySec = 30.0;
    private const long PausedRetryMs = 400;

    private readonly ResetClockOptions _o;
    private readonly Func<bool> _sendFesProbe;        // sends a lone FES probe; false if it couldn't
    private readonly Func<bool> _canProbe;            // in game && !held && heartbeat enabled
    private readonly Action<bool> _setDiscoveryHold;  // true suspends the routine heartbeat (reserve channel)
    private readonly Func<long> _monoNow;             // monotonic milliseconds
    private readonly object _lock = new();
    private readonly Timer? _timer;
    private bool _disposed;

    // Window on R in monotonic ms: R ∈ [_loMs, _hiMs). Null when unheld.
    private long? _loMs;
    private long? _hiMs;

    private ResetPhase _phase = ResetPhase.Idle;
    private bool _locked;
    private bool _inGame;
    private int _lastValue = -1;

    // Discovery bookkeeping.
    private long _nextSampleAtMs;
    private int _discoveryArmValue = int.MaxValue;
    private int _discoverySamples;
    private bool _discoveryCrossed;

    private volatile bool _sampleInFlight;
    private long _sampleSendMs;
    private int _budgetLeft;
    private int _timeoutStreak;
    private double _rttEstMs = 100;
    private long _lastFreshReplyMs;

    private ResetEstimate _snapshot = new(null, MinuteUncertaintySec, ResetPhase.Idle);

    /// <summary>Optional one-shot UI refresh hint on any estimate change.</summary>
    public event Action? EstimateChanged;
    /// <summary>Per folded reading — for diagnostic logging only.</summary>
    public event Action<ResetObservation>? ObservationRecorded;
    /// <summary>Notable incident text (unanswered sample, lock contradiction, auto-reset anchor) for
    /// the capture log. Fires off the UI thread.</summary>
    public event Action<string>? DiagnosticNote;

    public ResetClock(ResetClockOptions options, Func<bool> sendFesProbe, Func<bool> canProbe,
                      Action<bool> setDiscoveryHold, Func<long>? monoNow = null)
    {
        _o = options;
        _sendFesProbe = sendFesProbe;
        _canProbe = canProbe;
        _setDiscoveryHold = setDiscoveryHold;
        _monoNow = monoNow ?? DefaultMonoNow;
        _budgetLeft = _o.ProbeBudget;
        if (_o.SelfSchedule)
            _timer = new Timer(_ => OnTimerFired(), null, Timeout.Infinite, Timeout.Infinite);
    }

    private static long DefaultMonoNow() => Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency;

    /// <summary>Current monotonic timestamp (ms). Callers stamp reply arrival with this.</summary>
    public long NowMono => _monoNow();

    /// <summary>Latest projection snapshot for the 1 Hz UI countdown.</summary>
    public ResetEstimate Snapshot()
    {
        lock (_lock)
            return _snapshot;
    }

    /// <summary>True while a discovery sample is outstanding — <see cref="MudSession"/> defers routine
    /// probes so the next reply is unambiguously the sample's.</summary>
    public bool IsSamplingInFlight => _sampleInFlight;

    // Lock-free cadence hint for the heartbeat composer. Read under MudSession._fesLock, which must
    // NEVER take _lock (the established order is engine→_fesLock), hence a volatile bool rather
    // than Snapshot(). Maintained by PublishLocked.
    private volatile bool _fesCadenceRelaxed;

    /// <summary>True once FES readings are only needed at the slow sweep cadence: the projection is
    /// Locked, or post-reset (CoarseOnly) and re-converged to ≤ <see cref="ResetClockOptions.RelaxedUncertaintySec"/>.
    /// While false the heartbeat keeps FES at beat cadence so coarse readings can converge and the
    /// discovery pass can arm (its freshness gate needs a reply within MaxReplyAgeToArm).</summary>
    public bool FesCadenceRelaxed => _fesCadenceRelaxed;

    public void OnGameModeEntered()
    {
        bool wasDiscovering;
        lock (_lock)
        {
            wasDiscovering = _phase == ResetPhase.Discovering;   // double-enter without an exit
            ResetStateLocked();
            _inGame = true;
            PublishLocked();
        }
        if (wasDiscovering) _setDiscoveryHold(false);
        EstimateChanged?.Invoke();
    }

    public void OnGameModeExited()
    {
        bool wasDiscovering;
        lock (_lock)
        {
            wasDiscovering = _phase == ResetPhase.Discovering;
            ResetStateLocked();
            CancelTimerLocked();
            PublishLocked();
        }
        if (wasDiscovering) _setDiscoveryHold(false);
        EstimateChanged?.Invoke();
    }

    private void ResetStateLocked()
    {
        _inGame = false;
        _loMs = null;
        _hiMs = null;
        _phase = ResetPhase.Idle;
        _locked = false;
        _lastValue = -1;
        _discoveryArmValue = int.MaxValue;
        _discoverySamples = 0;
        _discoveryCrossed = false;
        _sampleInFlight = false;
        _budgetLeft = _o.ProbeBudget;
        _timeoutStreak = 0;
        _lastFreshReplyMs = 0;
    }

    /// <summary>
    /// Fold one stats update. <paramref name="fresh"/> is true only for a genuine FES reply. A bare
    /// prompt (rate-limited empty) never reaches here, so an outstanding sample simply times out.
    /// </summary>
    public void Observe(int? serverMinutes, bool fresh, long replyMs)
    {
        ResetObservation? rec = null;
        string? note = null;
        lock (_lock)
        {
            if (!_inGame || !fresh || serverMinutes is not int v || v < 0)
                return;

            _lastFreshReplyMs = replyMs;
            _lastValue = v;

            long obsMs;
            bool wasSample = _sampleInFlight;
            if (wasSample)
            {
                _sampleInFlight = false;
                CancelTimerLocked();
                _rttEstMs = Math.Max(1, replyMs - _sampleSendMs);
                obsMs = _sampleSendMs + (long)(_rttEstMs / 2);   // RTT/2-corrected observation instant
                _timeoutStreak = 0;
                if (v < _discoveryArmValue)
                    _discoveryCrossed = true;
            }
            else
            {
                obsMs = replyMs;
            }

            long cLo = obsMs + (long)v * 60_000;
            long cHi = obsMs + (long)(v + 1) * 60_000;

            if (_loMs is not long lo || _hiMs is not long hi)
            {
                _loMs = cLo;
                _hiMs = cHi;
                _phase = _locked ? ResetPhase.CoarseOnly : ResetPhase.Coarse;
            }
            else if (cLo >= hi)
            {
                // TIME WENT UP — a server reset event (main → finish-up, or → new cycle). Re-anchor.
                ReAnchorLocked(cLo, cHi);
                _phase = _locked ? ResetPhase.CoarseOnly : ResetPhase.Coarse;
            }
            else if (cHi <= lo)
            {
                // Reset came SOONER than the window said — a genuine early decrement, or a routine
                // reading's reply-time bias inflating lo past an RTT/2-corrected sample's cHi.
                // Never treated as a firing; but if we were locked it means the lock is wrong —
                // annotate, reopen, re-verify.
                if (_phase == ResetPhase.Locked || _locked)
                {
                    note = $"reset-lock contradicted (early decrement?): reading v={v} is below the locked window";
                    _locked = false;
                }
                else if (_phase == ResetPhase.Discovering)
                {
                    note = $"early decrement mid-discovery: reading v={v} is below the window; pass ended";
                }
                // ALWAYS end an active pass here (like the upward-jump re-anchor does). This branch
                // used to flip Discovering→Coarse directly, orphaning the channel hold — the routine
                // heartbeat stayed suppressed for the rest of the session (live 2026-07-25).
                EndDiscoveryLocked();
                _loMs = cLo;
                _hiMs = cHi;
                if (_phase != ResetPhase.CoarseOnly) _phase = ResetPhase.Coarse;
            }
            else if (_phase == ResetPhase.Locked)
            {
                // Consistent reading — trust the lock, don't move a pinned window.
            }
            else
            {
                _loMs = Math.Max(lo, cLo);
                _hiMs = Math.Min(hi, cHi);
            }

            PumpLocked(replyMs);
            rec = BuildObservationLocked(v, wasSample);
            PublishLocked();
        }

        if (note is not null) DiagnosticNote?.Invoke(note);
        if (rec is ResetObservation r) ObservationRecorded?.Invoke(r);
        EstimateChanged?.Invoke();
    }

    private void ReAnchorLocked(long cLo, long cHi)
    {
        _loMs = cLo;
        _hiMs = cHi;
        _sampleInFlight = false;
        _timeoutStreak = 0;
        _discoveryCrossed = false;
        // If a reset event lands mid-pass, drop the pass and release the channel.
        if (_phase == ResetPhase.Discovering)
            EndDiscoveryLocked();
    }

    /// <summary>
    /// The server announced the auto-reset (C06 C04): the reset is in exactly the finish-up period.
    /// Anchor precisely from the message arrival (RTT/2-corrected) — the last two minutes are exact
    /// with no further probing.
    /// </summary>
    public void NoteAutoResetInitiated(long arrivalMs)
    {
        lock (_lock)
        {
            if (!_inGame) return;
            long reset = arrivalMs + (long)_o.FinishUpDuration.TotalMilliseconds - (long)(_rttEstMs / 2);
            _loMs = reset - 300;
            _hiMs = reset + 300;
            _locked = true;
            _phase = ResetPhase.Locked;
            _discoveryCrossed = false;
            if (_sampleInFlight) { _sampleInFlight = false; }
            CancelTimerLocked();
            PublishLocked();
        }
        _setDiscoveryHold(false);
        DiagnosticNote?.Invoke($"auto-reset initiated: reset anchored in {_o.FinishUpDuration.TotalSeconds:F0}s");
        EstimateChanged?.Invoke();
    }

    // Drive transitions and sampling. No-op once locked.
    private void PumpLocked(long nowMs)
    {
        if (!_inGame || _sampleInFlight || _locked)
            return;
        if (_loMs is not long lo || _hiMs is not long hi)
            return;

        double half = (hi - lo) / 2000.0;
        long target = (lo + hi) / 2;
        double secsToTarget = (target - nowMs) / 1000.0;

        if (_phase == ResetPhase.Discovering)
        {
            if (_discoveryCrossed)
            {
                if (half <= _o.SuccessTargetSec) LockSuccessLocked();
                else EndDiscoveryLocked();   // bracketed but not tight enough — retry next boundary
                return;
            }
            if (_discoverySamples >= _o.SampleCap || _budgetLeft <= 0)
            {
                EndDiscoveryLocked();        // edge not crossed within the pass budget — retry next boundary
                return;
            }
            if (nowMs >= _nextSampleAtMs) SendSampleLocked(nowMs);
            else ReArmTimerLocked(nowMs, _nextSampleAtMs);
            return;
        }

        // Coarse: arm discovery as a minute-decrement approaches.
        if (_phase == ResetPhase.Idle)
            _phase = ResetPhase.Coarse;
        if (secsToTarget <= 0 || !EligibleToDiscoverLocked(nowMs, half))
            return;
        if (half <= _o.SuccessTargetSec) { LockSuccessLocked(); return; }   // coarse already good enough
        double secsToDecrement = secsToTarget - Math.Floor(secsToTarget / 60.0) * 60.0;
        if (secsToDecrement > _o.ApproachSec)
            return;
        EnterDiscoveryLocked(nowMs);
    }

    private bool EligibleToDiscoverLocked(long nowMs, double half)
        => _canProbe()
           && _budgetLeft > 0
           && half <= _o.ArmUncertaintySec
           && nowMs - _lastFreshReplyMs <= (long)_o.MaxReplyAgeToArm.TotalMilliseconds;

    private void EnterDiscoveryLocked(long nowMs)
    {
        _phase = ResetPhase.Discovering;
        _discoveryArmValue = _lastValue;
        _discoverySamples = 0;
        _discoveryCrossed = false;
        _setDiscoveryHold(true);                              // reserve the channel
        _nextSampleAtMs = nowMs + (long)_o.ClearChannelLead.TotalMilliseconds;   // 1 s clear before the first probe
        ReArmTimerLocked(nowMs, _nextSampleAtMs);
    }

    private void SendSampleLocked(long nowMs)
    {
        if (!_canProbe())
        {
            ReArmTimerLocked(nowMs, nowMs + PausedRetryMs);   // held/asleep — pause, don't spend budget
            return;
        }
        _sampleSendMs = nowMs;
        _sampleInFlight = true;
        if (!_sendFesProbe())   // takes _fesLock (engine→_fesLock order)
        {
            _sampleInFlight = false;
            ReArmTimerLocked(nowMs, nowMs + PausedRetryMs);
            return;
        }
        _budgetLeft--;
        _discoverySamples++;
        _nextSampleAtMs = nowMs + (long)_o.SampleSpacing.TotalMilliseconds;
        ReArmTimerLocked(nowMs, nowMs + (long)_o.SampleTimeout.TotalMilliseconds);
    }

    private void LockSuccessLocked()
    {
        bool wasDiscovering = _phase == ResetPhase.Discovering;
        _locked = true;
        _phase = ResetPhase.Locked;
        _sampleInFlight = false;
        CancelTimerLocked();
        if (wasDiscovering) _setDiscoveryHold(false);
    }

    private void EndDiscoveryLocked()
    {
        bool wasDiscovering = _phase == ResetPhase.Discovering;
        if (!_locked && _phase != ResetPhase.CoarseOnly)
            _phase = ResetPhase.Coarse;
        _sampleInFlight = false;
        _discoveryCrossed = false;
        _timeoutStreak = 0;
        CancelTimerLocked();
        if (wasDiscovering) _setDiscoveryHold(false);
    }

    private void OnTimerFired()
    {
        string? note = null;
        lock (_lock)
        {
            if (_disposed || !_inGame)
                return;
            note = HandleTimeoutLocked();
            PumpLocked(_monoNow());
            PublishLocked();
        }
        if (note is not null) DiagnosticNote?.Invoke(note);
        EstimateChanged?.Invoke();
    }

    // Drop an overdue sample; note it (bare prompt / collision / asleep). Three in a row end the pass.
    private string? HandleTimeoutLocked()
    {
        long now = _monoNow();
        if (!_sampleInFlight)
            return null;
        long dueMs = _sampleSendMs + (long)_o.SampleTimeout.TotalMilliseconds;
        if (now < dueMs)
        {
            // The timer fired ahead of the sample's deadline (timer-clock vs monotonic-clock skew,
            // or a stale callback that raced a reply): KEEP the deadline armed. Returning without
            // re-arming left the sample in flight with no timer pending — and every probe path
            // gates on IsSamplingInFlight, so one early fire deadlocked the entire FES machinery
            // (no status updates at all) for the rest of the session.
            ReArmTimerLocked(now, dueMs);
            return null;
        }
        _sampleInFlight = false;
        string note = "reset sample unanswered (rate-limit / collision / asleep)";
        if (++_timeoutStreak >= 3)
            EndDiscoveryLocked();
        return note;
    }

    private ResetObservation BuildObservationLocked(int v, bool sample)
    {
        long now = _monoNow();
        double loSec = _loMs is long lo ? (lo - now) / 1000.0 : 0;
        double hiSec = _hiMs is long hi ? (hi - now) / 1000.0 : 0;
        double unc = _loMs is long l && _hiMs is long h ? (h - l) / 2000.0 : MinuteUncertaintySec;
        return new ResetObservation(_rttEstMs, v, sample, loSec, hiSec, unc, _phase);
    }

    private void PublishLocked()
    {
        DateTime? target = null;
        double unc = MinuteUncertaintySec;
        if (_loMs is long lo && _hiMs is long hi)
        {
            long now = _monoNow();
            long mid = (lo + hi) / 2;
            target = DateTime.UtcNow.AddSeconds((mid - now) / 1000.0);
            unc = (hi - lo) / 2000.0;
        }
        _snapshot = new ResetEstimate(target, unc, _phase);
        _fesCadenceRelaxed = _phase == ResetPhase.Locked
            || (_phase == ResetPhase.CoarseOnly && unc <= _o.RelaxedUncertaintySec);
    }

    private void ReArmTimerLocked(long nowMs, long atMs)
    {
        if (!_o.SelfSchedule || _timer is null)
            return;
        long delay = Math.Max(1, atMs - nowMs);
        _timer.Change(delay, Timeout.Infinite);
    }

    private void CancelTimerLocked()
    {
        if (_o.SelfSchedule)
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Test hook: run one scheduling pass at the injected clock's current time. Only
    /// meaningful with <see cref="ResetClockOptions.SelfSchedule"/> false.</summary>
    internal void PumpForTest()
    {
        lock (_lock)
        {
            if (!_inGame)
                return;
            HandleTimeoutLocked();
            PumpLocked(_monoNow());
            PublishLocked();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        }
        _timer?.Dispose();
    }
}
