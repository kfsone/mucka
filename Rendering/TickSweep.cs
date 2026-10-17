#if WINDOWS
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace Mucka.Rendering;

/// <summary>
/// The Combat Rail's tick meter, animated on WinUI Composition - a bar that counts down one MUD2
/// combat tick, refills, and counts down again for as long as the fight lasts.
///
/// <para><b>Linear, and it has to be said out loud.</b> Composition applies a cubic ease-in-out to
/// keyframes that carry no easing function of their own, which makes a constant-rate countdown crawl
/// at both ends and race through the middle - in play the bar visibly slows toward the right instead
/// of ticking evenly. A clock that does not tick evenly is worse than no clock, because it is read as
/// information. Every keyframe here takes an explicit linear easing and must keep doing so.</para>
///
/// <para><b>Why this is not drawn by the canvas.</b> A timer repainting the rail's canvas 30 times a
/// second would compete directly with typing (Invariant #1) - and a 2-second progress bar is the
/// single most repaint-hungry thing on the panel. The whole motion therefore lives on the
/// compositor, where it costs the UI thread nothing at all: one <c>Vector3KeyFrameAnimation</c> on a
/// Visual's Scale, started once per anchor and left alone. The canvas draws only the still parts (the
/// track, the opponent count over it); this element is the fill inside that track, sitting behind the
/// transparent canvas exactly as <see cref="PulseLayer"/>'s glow does.</para>
///
/// <para><b>One animation, phase in its keyframes.</b> The loop is a single endless cycle whose
/// keyframes begin at the fraction of the tick already elapsed, so there is no partial animation to
/// hand off from. A handoff runs from a callback on the UI thread, and its lateness became the bar's
/// permanent offset: measured at 30 to 95 ms late on every restart, with nothing to correct it.</para>
///
/// <para><b>The start is where the phase is won or lost.</b> The loop keeps its own time once begun, so
/// whatever lies between "time left was computed" and "the compositor began" is the bar's error for the
/// rest of the fight. Two things are measured: the compositor itself takes 32 to 44 ms to begin, and a
/// UI-thread stall between the two instants added about 570 ms once. So the time left is computed as the
/// LAST step before starting, after every object is built, and advanced by the measured start lag
/// (<see cref="_startLagMs"/>). A 1 ms probe animation in the same batch then reports when the
/// compositor really began, and a start more than <see cref="ToleranceMs"/> off is redone.</para>
///
/// <para>Alignment is to an ANCHOR INSTANT, not to the moment <see cref="Restart"/> happens to be
/// called: the click is armed from the same anchor, which makes the two one clock rather than two
/// that were merely started near each other.</para>
///
/// <para>The sweep is not a prediction of the next swing: it is never labelled or coloured by
/// judgement on those grounds. A timer is not a verdict.</para>
///
/// <para><b>Teardown is not optional</b> - see PulseLayer's remarks for the RO_E_CLOSED crash class
/// this shares. <see cref="Stop"/> must run from the host's <c>OnHandlerChanged</c> when
/// <c>Handler is null</c>.</para>
/// </summary>
internal sealed class TickSweep
{
    /// <summary>One MUD2 combat tick - shared with <see cref="Mucka.Audio.CombatMetronome"/> via
    /// <see cref="Mucka.Combat.CombatTiming.TickMilliseconds"/> so the bar and the click can never
    /// independently drift apart.</summary>
    private const double TickMilliseconds = Mucka.Combat.CombatTiming.TickMilliseconds;

    /// <summary>How long the bar takes to jump from empty back to full at the rollover, as a fraction of
    /// a tick. One frame at 60 Hz is about 0.8% of a tick; this is 1 ms.</summary>
    private const float RefillFraction = (float)(1.0 / TickMilliseconds);

    /// <summary>A start whose observed lag differs from the assumed lag by more than this is redone.</summary>
    private const double ToleranceMs = 15.0;

    /// <summary>Starts attempted for one <see cref="Restart"/>. A third failure is left standing: the bar
    /// is then off by a stall that keeps recurring, and chasing it would only make the bar jump.</summary>
    private const int MaxStartAttempts = 3;

    /// <summary>Observed lags above this are one-off stalls, not the compositor's start latency, and are
    /// not learned from.</summary>
    private const double MaxLearnableLagMs = 100.0;

    private readonly FrameworkElement _host;
    private readonly Visual _visual;

    /// <summary>Bumped by every <see cref="Restart"/> and every <see cref="Stop"/>. A scoped batch cannot
    /// be cancelled once ended, so its Completed fires even after the fight ended or a newer start
    /// replaced it; a stale completion must neither redo a start nor touch the log as if current.</summary>
    private int _generation;

    /// <summary>How long the compositor takes to begin an animation started now, as last observed. The
    /// first value is the middle of the 32 to 44 ms measured on the first run of the probe; each start
    /// then nudges it.</summary>
    private double _startLagMs = 38.0;

    /// <summary>Set once a Composition call against <see cref="_visual"/> has thrown - which means the
    /// compositor behind it is gone and every later call would throw the same way. The Visual is a
    /// readonly field (acquired eagerly so the rest state can be applied before the element is ever
    /// shown), so this flag is how the class stops addressing a dead one. See <see cref="Stop"/>.</summary>
    private bool _detached;

    private TickSweep(FrameworkElement host)
    {
        _host = host;
        // Acquired eagerly, unlike PulseLayer's lazy fetch, because the REST state has to be applied
        // before the element is ever shown: an untouched visual scales at 1, which would paint a
        // full-width bar the instant the panel opens - a tick meter reading "swing imminent" before
        // any fight exists.
        _visual = ElementCompositionPreview.GetElementVisual(host);
        _host.Unloaded += (_, _) => Stop();   // belt-and-braces alongside OnHandlerChanged
        Rest();
    }

    public static TickSweep Attach(FrameworkElement host) => new(host);

    /// <summary>Starts the sweep in phase with <paramref name="anchorUtc"/>, resetting it if it was
    /// already running. The metronome is armed from the same anchor, which is the entire point (see the
    /// class remarks).</summary>
    /// <param name="anchorUtc">A known tick boundary. The bar picks up the tick ALREADY IN PROGRESS at
    /// the fraction it has actually reached, rather than restarting the countdown from full: this is
    /// also called when the panel is toggled on mid-fight, where starting from full would put the bar
    /// up to a whole tick out.</param>
    public void Restart(DateTime anchorUtc)
    {
        // Nothing to drive. Reachable only after Stop() found the compositor already gone; the host
        // replaces this instance on re-attach, so refusing here is correct rather than degraded.
        if (_detached)
            return;

        Start(anchorUtc, ++_generation, attempt: 1);
    }

    private void Start(DateTime anchorUtc, int generation, int attempt)
    {
        // Scale about the left edge, so the bar's LEFT end stays pinned and its right end is what
        // moves - the bar drains leftward rather than growing or shrinking about its middle.
        _visual.CenterPoint = Vector3.Zero;

        // Everything that can take time is built BEFORE the clock is read; see the class remarks.
        var compositor = _visual.Compositor;
        var linear = compositor.CreateLinearEasingFunction();
        var sweep = compositor.CreateVector3KeyFrameAnimation();
        sweep.Duration = TimeSpan.FromMilliseconds(TickMilliseconds);
        sweep.IterationBehavior = AnimationIterationBehavior.Forever;
        var dummy = compositor.CreateContainerVisual();
        var probe = compositor.CreateScalarKeyFrameAnimation();
        probe.InsertKeyFrame(1.0f, 0f);
        probe.Duration = TimeSpan.FromMilliseconds(1);
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

        var assumedLagMs = _startLagMs;
        var calledAt = DateTime.UtcNow;
        // Shared with the metronome - see CombatTiming.MillisecondsToNextBoundary, which owns this
        // computation. Evaluated where the compositor will have begun, not where the call is made.
        var remaining = Mucka.Combat.CombatTiming.MillisecondsToNextBoundary(
            anchorUtc, calledAt.AddMilliseconds(assumedLagMs));
        // Clamped off the ends so the four keyframe times stay strictly increasing; costs at most 2 ms.
        var left = Math.Clamp((float)(remaining / TickMilliseconds), 2f * RefillFraction, 1f - (2f * RefillFraction));

        // One endless cycle of exactly one tick, starting mid-count: from what is left, down to empty on
        // the boundary, a one-millisecond refill, then down again to the same fraction at the cycle's
        // end - which is where it began, so the loop closes on itself with no seam. The last segment's
        // slope is the same as the first's: (1 - left) over (1 - left) of a tick.
        sweep.InsertKeyFrame(0.0f, new Vector3(left, 1f, 1f), linear);
        sweep.InsertKeyFrame(left, new Vector3(0f, 1f, 1f), linear);
        sweep.InsertKeyFrame(Math.Min(1f, left + RefillFraction), new Vector3(1f, 1f, 1f), linear);
        sweep.InsertKeyFrame(1.0f, new Vector3(left, 1f, 1f), linear);
        _visual.StartAnimation("Scale", sweep);
        dummy.StartAnimation("Opacity", probe);
        batch.End();
        batch.Completed += (_, _) =>
        {
            dummy.Dispose();
            OnStarted(anchorUtc, generation, attempt, calledAt, assumedLagMs, remaining);
        };

        if (Mucka.Core.TickDiag.Enabled)
            Mucka.Core.TickDiag.Log(
                $"bar restart  attempt {attempt}  from {left * 100,5:F1}% ({remaining,6:F0} ms left)  "
                + $"assumed lag {assumedLagMs:F0} ms  anchor {anchorUtc:HH:mm:ss.fff}");
    }

    /// <summary>The probe completed, so the compositor has begun. Compares how long that took with how
    /// long it was assumed to take, learns from it, and redoes the start if it was off by more than
    /// <see cref="ToleranceMs"/>.</summary>
    private void OnStarted(
        DateTime anchorUtc, int generation, int attempt, DateTime calledAt, double assumedLagMs, double remaining)
    {
        if (generation != _generation || _detached)
            return;

        // Includes the probe's own 1 ms, and the time for this callback to be dispatched.
        var lag = (DateTime.UtcNow - calledAt).TotalMilliseconds - 1.0;
        var error = lag - assumedLagMs;
        var redo = Math.Abs(error) > ToleranceMs && attempt < MaxStartAttempts;

        if (Mucka.Core.TickDiag.Enabled)
        {
            var nowLeft = Mucka.Combat.CombatTiming.MillisecondsToNextBoundary(anchorUtc, DateTime.UtcNow);
            Mucka.Core.TickDiag.Log(
                $"bar probe    attempt {attempt}  start lag {lag,6:F1} ms vs assumed {assumedLagMs:F0} ms "
                + $"(error {error,6:F1} ms; told {remaining:F0} ms left, lattice now {nowLeft:F0})"
                + (redo ? "  - redoing" : string.Empty));
        }

        if (lag <= MaxLearnableLagMs)
            _startLagMs = (0.7 * _startLagMs) + (0.3 * Math.Max(0.0, lag));
        if (redo)
            Start(anchorUtc, generation, attempt + 1);
    }

    /// <summary>Stops the sweep and empties the bar. Out of combat there is no tick to show: the
    /// tick meter is a fight instrument, and one still ticking away in the tea room would read as
    /// a fight that never ended.</summary>
    public void Stop()
    {
        // Bumped first, and outside the guard below, so a start completing after this finds itself
        // stale instead of redoing itself a frame after the bar was emptied.
        _generation++;
        if (_detached)
            return;

        // Guarded for the reason FleePulse.Stop sets out at length: this is called from the host's
        // HandlerChanged, which fires while the native peer is being replaced or destroyed, so the
        // compositor behind this cached Visual may already be closed and touching it throws the
        // RO_E_CLOSED family. Unhandled, that would propagate out of the handler and skip the re-attach
        // below it, leaving the tick bar dead for the rest of the session.
        try
        {
            _visual.StopAnimation("Scale");
            // StopAnimation freezes the property wherever the animation last left it, so the rest state
            // has to be asserted explicitly - otherwise the bar sticks at whatever fraction it had
            // reached when the fight ended.
            Rest();
        }
        catch (Exception ex)
        {
            _detached = true;
            Mucka.Core.CrashLog.Write("TickSweep.Stop", ex);
        }
    }

    /// <summary>Empty and still. The rest state is zero width rather than full: out of combat there is
    /// no tick to count down, and a full bar sitting there reads as "a swing is due right now".</summary>
    private void Rest()
    {
        _visual.CenterPoint = Vector3.Zero;
        _visual.Scale = new Vector3(0f, 1f, 1f);
    }
}
#endif
