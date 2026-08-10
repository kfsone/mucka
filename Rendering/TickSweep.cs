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
/// exact multiples of 2000 ms - so a sweep started at the fight's first swing stays in phase with the
/// game for the whole fight without ever being resynced. <see cref="Restart"/> exists for that one
/// moment. It does NOT track the tick across a fight the panel joined late; until the swing-timestamp
/// lock lands, treat the sweep as a metronome that happens to agree with the game, not as a
/// prediction of the next swing - which is also why the spec forbids labelling it or colouring it by
/// judgement. A timer is not a verdict.</para>
///
/// <para><b>Teardown is not optional</b> - see PulseLayer's remarks for the RO_E_CLOSED crash class
/// this shares. <see cref="Stop"/> must run from the host's <c>OnHandlerChanged</c> when
/// <c>Handler is null</c>.</para>
/// </summary>
internal sealed class TickSweep
{
    /// <summary>One MUD2 combat tick. Measured, not chosen: swing gaps across the whole capture
    /// corpus are exact multiples of this, with 76-94% of a session's swings landing in a single
    /// 20 ms bin.</summary>
    private const double TickMilliseconds = 2000.0;

    private readonly FrameworkElement _host;
    private readonly Visual _visual;
    private bool _running;

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

    /// <summary>Starts sweeping if it is not already. Idempotent on purpose: this is called from the
    /// combat-state handler, which fires far more often than combat actually starts, and restarting
    /// the animation on each of those calls would keep yanking the bar back to empty.</summary>
    public void Start()
    {
        if (_running)
            return;
        Restart();
    }

    /// <summary>Starts the sweep from empty, resetting its phase. Call at the fight's first swing -
    /// see the class remarks on why once per fight is enough.</summary>
    public void Restart()
    {
        // Scale about the left edge, so the bar grows rightward out of the track's left end rather
        // than outward from its middle.
        _visual.CenterPoint = Vector3.Zero;

        var animation = _visual.Compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(0.0f, new Vector3(0f, 1f, 1f));
        animation.InsertKeyFrame(1.0f, new Vector3(1f, 1f, 1f));
        animation.Duration = TimeSpan.FromMilliseconds(TickMilliseconds);
        animation.IterationBehavior = AnimationIterationBehavior.Forever;

        _visual.StartAnimation("Scale", animation);
        _running = true;
    }

    /// <summary>Stops the sweep and empties the bar. Out of combat there is no tick to show: the
    /// spec's tick meter is a fight instrument, and one still ticking away in the tea room would
    /// read as a fight that never ended.</summary>
    public void Stop()
    {
        _visual.StopAnimation("Scale");
        _running = false;
        // StopAnimation freezes the property wherever the animation last left it, so the rest state
        // has to be asserted explicitly - otherwise the bar sticks at whatever fraction it had
        // reached when the fight ended.
        Rest();
    }

    private void Rest()
    {
        _visual.CenterPoint = Vector3.Zero;
        _visual.Scale = new Vector3(0f, 1f, 1f);
    }
}
#endif
