namespace Mucka.Core;

/// <summary>
/// Where MUD2's combat-tick boundaries fall, estimated from the swings the client actually sees and
/// kept across the whole play session.
///
/// <para><b>Why this replaced a single sample.</b> Both instruments - the bar and the click - used to
/// take their phase from ONE instant: the timestamp of an encounter's first swing, discarded and
/// re-derived at the start of every fight. Measured against a session-wide best-fit lattice over the
/// live clog corpus (<c>tools/combat/sessionlattice.py</c>, 742 encounters), that anchor is fine at the
/// median and has a long tail:</para>
///
/// <list type="bullet">
/// <item>median 35 ms, p75 118 ms, p90 250 ms, p99 846 ms, worst 963 ms</item>
/// <item><b>18.9% of encounters off by more than 150 ms, 6.5% by more than 500 ms</b></item>
/// </list>
///
/// <para>963 ms is essentially half a tick - maximally wrong. That tail is what the owner reported as
/// "the ticker didn't actually seem to coincide with the server's combat tick - I was receiving combat
/// messages about 3/5th of the way thru the slider". Most fights were fine, which is exactly why it
/// took so long to pin: the median hides it and only the tail is visible in play.</para>
///
/// <para><b>The spec's own justification argued for this change.</b> COMBAT-RAIL-SPEC.md section 6 set
/// the phase once per encounter because "one lattice fits a whole 40-minute session to ~4 ppm, so the
/// phase does not need chasing". The premise is true and now independently measured - one 2000 ms phase
/// fits a whole session with a median mean-residual of 26.5 ms across 65 sessions. The conclusion drawn
/// from it was backwards: if one lattice fits the entire session, then throwing away everything the
/// previous fight taught us and re-deriving from a single noisy sample is strictly worse than keeping a
/// running estimate. One confirmed cause of the bad samples: when the first swing arrives in the same
/// frame as the player's own <c>kill</c> reply its timestamp carries the KEYSTROKE's phase:
/// <c>tools/combat/opener_phase.py</c> puts those 48 encounters over 100 ms out <b>52.1%</b> of the
/// time, against <b>18.4%</b> for openers that arrive more than a second after the fight starts. The
/// same error the spec believed anchoring on a swing had escaped.</para>
///
/// <para>An earlier version of this paragraph said "over 150 ms out 52% of the time, against a ~20%
/// baseline". The 52% was measured at 100 ms, not 150; the "~20%" was the overall &gt;150 ms rate
/// (18.9%) quoted against a &gt;100 ms percentage, where the real overall figure is 26.8%. And the
/// script that produced it lived in a scratchpad, so none of it could be re-derived - it is now
/// <c>opener_phase.py</c>, which prints both thresholds side by side precisely so they cannot be
/// confused again. Recorded because this is the corpus rot CLAUDE.md describes, committed by the same
/// pass that was documenting an instance of it.</para>
///
/// <para><b>Circular statistics, because the quantity is an angle.</b> A residual of +990 ms and one of
/// -1010 ms describe the same phase, so an ordinary mean or median of folded residuals is wrong near
/// the wrap and can average two identical readings into a phase half a tick away. Each residual is
/// therefore accumulated as a unit vector and the estimate is the direction of their sum, which has no
/// wrap to get wrong. It is also O(1) in memory - two running sums, no sample buffer.</para>
///
/// <para><b>Exponential forgetting, because the lattice does drift.</b> At the spec's own ~4 ppm a day
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
    /// <para><b>Deliberately low, because setting it high broke the bar in play.</b> It was 5 for one
    /// round, to blunt a weighting problem (see <see cref="SettledSamples"/>). The owner's next session:
    /// "the combat ticker didn't start for a long time on the first fight, making it look like nothing
    /// was happening." MUD2 has silent pass ticks - a starfish went 90 seconds with no swing text at all
    /// - so a five-swing gate can be many seconds of dark bar, and a dark bar during a fight reads as a
    /// broken client rather than as an honest unknown.</para>
    /// </summary>
    private const int MinimumSamples = 2;

    /// <summary>
    /// Samples before the estimate is trusted enough to make a SOUND - see <see cref="IsSettled"/>.
    ///
    /// <para><b>The bar and the click get different gates on purpose, and it is the spec's own
    /// doctrine:</b> "a briefly-wrong timer that visibly corrects itself is honest in a way a
    /// confidently-wrong sound is not." A bar that jumps once early in a fight is self-explaining; a
    /// click bracketing a boundary that is not there is just wrong, twice a second, with nothing on
    /// screen to show why.</para>
    ///
    /// <para>Five, because of a weighting the first version of this class got wrong. The very first
    /// sample becomes the REFERENCE, so it sits at angle zero by construction and always votes for
    /// itself: at three samples it still carries about a third of the total weight, and a
    /// keystroke-phased opener needed only one more anomalous swing agreeing with it to be published as
    /// the lattice. A pack-fight opener, where several participants' swings land in one frame, is
    /// exactly where a correlated pair is plausible. At five its share is down to about a fifth.</para>
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
