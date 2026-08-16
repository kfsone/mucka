#if WINDOWS
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace Mucka.Rendering;

/// <summary>
/// The Combat Rail's tick meter, animated on WinUI Composition - a bar that fills left to right over
/// one MUD2 combat tick, empties, and fills again for as long as the fight lasts.
///
/// <para><b>Linear, and it has to be said out loud.</b> Composition applies a cubic ease-in-out to
/// keyframes that carry no easing function of their own, which makes a constant-rate countdown crawl
/// at both ends and race through the middle. The owner caught it in play before this comment existed -
/// "combat tick bar is not smooth - it seems to slow down towards the right" - and they are right that
/// it disqualifies the thing: a clock that does not tick evenly is worse than no clock, because it is
/// read as information. Every keyframe here takes an explicit linear easing and must keep doing so.</para>
///
/// <para><b>Why this is not drawn by the canvas.</b> The rail's <c>SKCanvasView</c> paints ON the UI
/// thread on WinUI, so a timer repainting it 30 times a second would compete directly with typing
/// (Invariant #1) - and a 2-second progress bar is the single most repaint-hungry thing on the panel.
/// The whole motion therefore lives on the compositor, where it costs the UI thread nothing at all:
/// one <c>Vector3KeyFrameAnimation</c> on a Visual's Scale, started once per fight and left alone.
/// The canvas draws only the still parts (the track, the opponent count over it); this element is the
/// fill inside that track, sitting behind the transparent canvas exactly as <see cref="PulseLayer"/>'s
/// glow does.</para>
///
/// <para><b>Phase.</b> MUD2's combat tick is exactly 2.000 s and phase-locked - measured gaps are
/// exact multiples of 2000 ms - so a sweep aligned to the fight's first swing stays in phase with the
/// game for the whole fight without ever being resynced.</para>
///
/// <para>Alignment is to an ANCHOR INSTANT, not to the moment <see cref="Restart"/> happens to be
/// called. This is the whole reason the bar and the metronome ever disagreed: the click has always
/// been armed from the swing's feed-thread timestamp, while this class used to start its animation
/// wherever the call landed - after a <c>BeginInvokeOnMainThread</c> hop, a PropertyChanged fan-out
/// and a wait for the next compositor frame. That latency is variable and is WORST during a swing
/// burst, which is exactly when both instruments are being read, so the bar trailed the click by a
/// different amount every fight. Taking the anchor makes the two genuinely one clock rather than two
/// that were merely started near each other.</para>
///
/// <para>The sweep is still not a prediction of the next swing - the spec forbids labelling it or
/// colouring it by judgement on those grounds, and that has not changed. A timer is not a verdict.</para>
///
/// <para><b>Teardown is not optional</b> - see PulseLayer's remarks for the RO_E_CLOSED crash class
/// this shares. <see cref="Stop"/> must run from the host's <c>OnHandlerChanged</c> when
/// <c>Handler is null</c>.</para>
/// </summary>
internal sealed class TickSweep
{
    /// <summary>One MUD2 combat tick - shared with <see cref="Mucka.Audio.CombatMetronome"/> via
    /// <see cref="Mucka.Core.CombatTiming.TickMilliseconds"/> so the bar and the click can never
    /// independently drift apart.</summary>
    private const double TickMilliseconds = Mucka.Core.CombatTiming.TickMilliseconds;

    private readonly FrameworkElement _host;
    private readonly Visual _visual;

    /// <summary>Bumped by every <see cref="Restart"/> and every <see cref="Stop"/>, and captured by
    /// the pending partial-tick handoff. A CompositionScopedBatch cannot be cancelled once ended, so
    /// its Completed WILL fire even if the fight ended or a better anchor arrived while the partial
    /// tick was still running; a stale handoff would then either revive a bar Stop() had emptied or
    /// stamp the endless loop over an alignment the newer Restart had just established - putting the
    /// bar back out of phase with the click, which is the one thing this class must not do.</summary>
    private int _generation;

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
    /// already running. Call with the encounter's first-swing timestamp - the same instant the
    /// metronome is armed from, which is the entire point (see the class remarks on Phase).</summary>
    /// <param name="anchorUtc">A known tick boundary. The bar picks up the tick ALREADY IN PROGRESS at
    /// the fraction it has actually reached, rather than restarting the countdown from full: at the
    /// first swing we are typically a few tens of milliseconds past a boundary, but this is also
    /// called when the panel is toggled on mid-fight, where starting from full would put the bar up to
    /// a whole tick out and the click would be heard landing in the middle of the sweep.</param>
    public void Restart(DateTime anchorUtc)
    {
        // Scale about the left edge, so the bar's LEFT end stays pinned and its right end is what
        // moves - the bar drains leftward rather than growing or shrinking about its middle.
        _visual.CenterPoint = Vector3.Zero;
        var generation = ++_generation;

        var intoCycle = (DateTime.UtcNow - anchorUtc).TotalMilliseconds % TickMilliseconds;
        if (intoCycle < 0)
            intoCycle += TickMilliseconds;
        var remaining = TickMilliseconds - intoCycle;

        // Less than a couple of frames of this tick left. Animating it would be a flicker at the far
        // left edge and the handoff would land late; wait it out empty and begin cleanly on the
        // boundary instead.
        if (remaining < 40.0)
        {
            Rest();
            StartLoopAfter(remaining, generation);
            return;
        }

        var compositor = _visual.Compositor;
        var linear = compositor.CreateLinearEasingFunction();

        // The partial tick: from wherever this one has got to, down to empty exactly on the boundary.
        var partial = compositor.CreateVector3KeyFrameAnimation();
        partial.InsertKeyFrame(0.0f, new Vector3((float)(remaining / TickMilliseconds), 1f, 1f), linear);
        partial.InsertKeyFrame(1.0f, new Vector3(0f, 1f, 1f), linear);
        partial.Duration = TimeSpan.FromMilliseconds(remaining);

        // A scoped batch is how the handoff stays on the compositor's own clock. Timing it with a
        // Timer or a Task.Delay would reintroduce the very scheduling latency this class exists to
        // remove, and on the UI thread it would also be a repeating hop near the typing path
        // (Invariant #1). Completed fires ONCE PER FIGHT, not once per tick.
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        _visual.StartAnimation("Scale", partial);
        batch.End();
        batch.Completed += (_, _) => StartLoop(generation);
    }

    /// <summary>Begins the endless on-phase sweep after <paramref name="delayMs"/> of stillness, for
    /// the case where the current tick has too little left to be worth drawing.</summary>
    private void StartLoopAfter(double delayMs, int generation)
    {
        var compositor = _visual.Compositor;
        var hold = compositor.CreateVector3KeyFrameAnimation();
        hold.InsertKeyFrame(1.0f, new Vector3(0f, 1f, 1f), compositor.CreateLinearEasingFunction());
        hold.Duration = TimeSpan.FromMilliseconds(Math.Max(delayMs, 1.0));

        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        _visual.StartAnimation("Scale", hold);
        batch.End();
        batch.Completed += (_, _) => StartLoop(generation);
    }

    /// <summary>The steady state: full at the start of each tick, empty at the end - a countdown, not
    /// a progress bar. It shows how much time is LEFT before the next swing, which is the question
    /// being asked, and it matches the health pips (lit = remaining) rather than inverting between two
    /// gauges on one panel.</summary>
    private void StartLoop(int generation)
    {
        // See _generation: this handoff was scheduled a partial tick ago and cannot be cancelled, so
        // it has to check on arrival that it is still the current one.
        if (generation != _generation)
            return;

        var compositor = _visual.Compositor;
        var linear = compositor.CreateLinearEasingFunction();
        var animation = compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(0.0f, new Vector3(1f, 1f, 1f), linear);
        animation.InsertKeyFrame(1.0f, new Vector3(0f, 1f, 1f), linear);
        animation.Duration = TimeSpan.FromMilliseconds(TickMilliseconds);
        animation.IterationBehavior = AnimationIterationBehavior.Forever;

        _visual.StartAnimation("Scale", animation);
    }

    /// <summary>Stops the sweep and empties the bar. Out of combat there is no tick to show: the
    /// spec's tick meter is a fight instrument, and one still ticking away in the tea room would
    /// read as a fight that never ended.</summary>
    public void Stop()
    {
        // Bump BEFORE stopping, so a handoff already queued for this fight's partial tick finds
        // itself stale rather than restarting the sweep a frame after the bar was emptied.
        _generation++;
        _visual.StopAnimation("Scale");
        // StopAnimation freezes the property wherever the animation last left it, so the rest state
        // has to be asserted explicitly - otherwise the bar sticks at whatever fraction it had
        // reached when the fight ended.
        Rest();
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
