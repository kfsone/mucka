namespace Mucka.Core;

/// <summary>
/// Where MUD2's combat-tick boundaries fall, estimated from the swings the client actually sees and
/// kept across the whole play session.
///
/// <para><b>Why a running estimate rather than a single sample.</b> Anchoring on one instant - the
/// timestamp of an encounter's first swing, re-derived at the start of every fight - is fine at the
/// median but has a long tail, measured against a session-wide best-fit lattice (742 encounters):</para>
///
/// <list type="bullet">
/// <item>median 35 ms, p75 118 ms, p90 250 ms, p99 846 ms, worst 963 ms</item>
/// <item><b>18.9% of encounters off by more than 150 ms, 6.5% by more than 500 ms</b></item>
/// </list>
///
/// <para>963 ms is essentially half a tick - maximally wrong. Most fights were fine, which is exactly
/// why the tail is easy to miss: the median hides it and only the tail is visible in play.</para>
///
/// <para>One 2000 ms phase fits a whole session: median mean-residual 26.5 ms across 65 sessions. One
/// confirmed cause of the bad single-sample anchors: when the first swing arrives in the same frame
/// as the player's own <c>kill</c> reply, its timestamp carries the KEYSTROKE's phase instead - 48
/// such encounters land over 100 ms out <b>52.1%</b> of the time, against <b>18.4%</b> for openers
/// that arrive more than a second after the fight starts.</para>
///
/// <para><b>Circular statistics, because the quantity is an angle.</b> A residual of +990 ms and one of
/// -1010 ms describe the same phase, so an ordinary mean or median of folded residuals is wrong near
/// the wrap and can average two identical readings into a phase half a tick away. Each residual is
/// therefore accumulated as a unit vector and the estimate is the direction of their sum, which has no
/// wrap to get wrong. It is also O(1) in memory - two running sums, no sample buffer.</para>
///
/// <para><b>Exponential forgetting, because the lattice does drift.</b> At roughly 4 ppm a day
/// is about 350 ms, so an estimate that weighted a week-old swing equally with this one would slowly
/// go wrong. <see cref="Decay"/> gives an effective window of a few hundred swings - long enough to
/// average out the ~12% of frames that arrive late, short enough to follow real drift.</para>
///
/// <para><b>Not thread-safe.</b> Called from the UI thread only, on the combat-event dispatch, which is
/// where the anchor is published from.</para>
/// </summary>
internal sealed class TickPhase
{
    private const double TickMs = CombatTiming.TickMilliseconds;

    /// <summary>Per-sample forgetting factor. 0.995 is an effective window of ~200 swings, which at one
    /// swing every couple of seconds is a few minutes of fighting - and, more to the point, several
    /// fights, which is the whole reason this survives an encounter boundary.</summary>
    private const double Decay = 0.995;

    /// <summary>How far the estimate must move before the reference is re-based and the anchor
    /// republished. Every republish restarts the bar's Composition animation, so this is a
    /// noise gate rather than a precision limit: early in a session corrections are large and this
    /// fires often, and once converged the mean barely moves and it stops firing. Below the ~26 ms the
    /// session lattice itself fits to, so it never limits accuracy.</summary>
    private const double RebaseThresholdMs = 15.0;

    /// <summary>
    /// Samples before the estimate is offered at all - the BAR's gate.
    ///
    /// <para>Two, not one: one swing is exactly the old single-sample behaviour, tail and all. Two is
    /// enough to have a lattice rather than a point, and the spec is explicit that a briefly-wrong
    /// timer which visibly corrects itself is honest - so the bar gets the estimate early and takes the
    /// correction.</para>
    ///
    /// <para><b>Deliberately low.</b> MUD2 has silent pass ticks - a starfish encounter went 90 seconds
    /// with no swing text at all - so a higher gate can leave the bar dark for many seconds, and a dark
    /// bar during a fight reads as a broken client rather than as an honest unknown.</para>
    /// </summary>
    private const int MinimumSamples = 2;

    /// <summary>
    /// Samples before the estimate is trusted enough to make a SOUND - see <see cref="IsSettled"/>.
    ///
    /// <para><b>The bar and the click get different gates on purpose:</b> a briefly-wrong timer that
    /// visibly corrects itself is honest in a way a confidently-wrong sound is not. A bar that jumps
    /// once early in a fight is self-explaining; a click bracketing a boundary that is not there is
    /// just wrong, twice a second, with nothing on screen to show why.</para>
    ///
    /// <para>Five: the very first sample becomes the REFERENCE, so it sits at angle zero by
    /// construction and always votes for itself. At three samples it still carries about a third of
    /// the total weight, and a keystroke-phased opener would need only one more anomalous swing
    /// agreeing with it to be published as the lattice - and a pack-fight opener, where several
    /// participants' swings land in one frame, is exactly where a correlated pair is plausible. At
    /// five its share is down to about a fifth.</para>
    /// </summary>
    private const int SettledSamples = 5;

    private DateTime _reference;
    private double _sumCos;
    private double _sumSin;
    private double _weight;
    private int _samples;

    /// <summary>The current best estimate of a tick boundary, or null until
    /// <see cref="MinimumSamples"/> swings have been seen.
    ///
    /// <para>Null is honest rather than cautious: the bar draws nothing and the click stays silent
    /// without a phase, which for at most the first few swings of a session is the correct output. A
    /// confidently wrong beat is worse than a late one.</para></summary>
    public DateTime? Anchor => _samples >= MinimumSamples ? _reference : null;

    /// <summary>Swings folded into the estimate. Diagnostics, and the gate behind
    /// <see cref="Anchor"/>.</summary>
    public int Samples => _samples;

    /// <summary>True once the estimate has enough swings behind it to drive a SOUND rather than only a
    /// visual - see <see cref="SettledSamples"/> for why those are different thresholds.</summary>
    public bool IsSettled => _samples >= SettledSamples;

    /// <summary>How tightly the observed swings agree about the phase, 0 (no agreement) to 1 (all
    /// identical) - the resultant length of the accumulated unit vectors. Diagnostics only: nothing
    /// gates on it, but a value that stays low would mean the swings are not landing on a 2000 ms
    /// lattice at all, which would invalidate the whole instrument rather than just this class.</summary>
    public double Concentration => _weight <= 0 ? 0 : Math.Sqrt((_sumCos * _sumCos) + (_sumSin * _sumSin)) / _weight;

    /// <summary>
    /// Folds one swing into the estimate.
    /// </summary>
    /// <returns>True when <see cref="Anchor"/> has changed and should be republished - either because
    /// the estimate has just become available, or because it moved more than
    /// <see cref="RebaseThresholdMs"/>.</returns>
    public bool Observe(DateTime swingUtc)
    {
        if (_samples == 0)
        {
            _reference = swingUtc;
            _sumCos = 1.0;
            _sumSin = 0.0;
            _weight = 1.0;
            _samples = 1;
            return false;   // one sample is not yet an estimate - see MinimumSamples
        }

        var residual = Fold((swingUtc - _reference).TotalMilliseconds);
        var theta = residual / TickMs * 2.0 * Math.PI;

        _sumCos = (_sumCos * Decay) + Math.Cos(theta);
        _sumSin = (_sumSin * Decay) + Math.Sin(theta);
        _weight = (_weight * Decay) + 1.0;
        var becameAvailable = _samples < MinimumSamples && _samples + 1 >= MinimumSamples;
        _samples++;

        var offset = Math.Atan2(_sumSin, _sumCos) / (2.0 * Math.PI) * TickMs;
        if (Math.Abs(offset) < RebaseThresholdMs)
            return becameAvailable;

        // Re-base: move the reference onto the current estimate and rotate the accumulator so its mean
        // is zero again. Rotating rather than resetting keeps the weight and the concentration earned so
        // far, and keeping residuals near zero is what stops the wrap from ever mattering.
        _reference = _reference.AddMilliseconds(offset);
        var (sin, cos) = Math.SinCos(-offset / TickMs * 2.0 * Math.PI);
        (_sumCos, _sumSin) = ((_sumCos * cos) - (_sumSin * sin), (_sumCos * sin) + (_sumSin * cos));
        return true;
    }

    /// <summary>Discards everything. For a genuinely new lattice - a different server - not for a new
    /// encounter, which is the mistake this class was built to undo.</summary>
    public void Reset()
    {
        _reference = default;
        _sumCos = _sumSin = _weight = 0;
        _samples = 0;
    }

    /// <summary>A millisecond offset folded onto (-1000, +1000] - the signed distance to the nearest
    /// lattice point rather than to the next one.</summary>
    private static double Fold(double ms)
    {
        var r = ms % TickMs;
        if (r < 0) r += TickMs;
        return r > TickMs / 2 ? r - TickMs : r;
    }
}
