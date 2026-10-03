#if WINDOWS
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Mucka.Combat;

namespace Mucka.Rendering;

/// <summary>
/// WinUI Composition glow helper. Animates the OPACITY of a layer positioned BEHIND the Skia canvas
/// (a transparent-background `Border`/`Rectangle` in the same grid cell), never the canvas's own
/// drawn text colour - Invariant #1 forbids driving continuous motion through anything that costs
/// UI-thread time, so the canvas must never be the thing animating.
///
/// <para><b>Teardown is not optional.</b> A live Composition animation on a visual whose underlying
/// native object has been torn down throws the `RO_E_CLOSED` family (0x80000013) and can take the
/// whole process down with it. <see cref="Stop"/> MUST be called from the host's own
/// `OnHandlerChanged` when <c>Handler is null</c>.</para>
/// </summary>
internal sealed class PulseLayer
{
    /// <summary>The client's one pulse period. Shared with <see cref="FleePulse"/> so the panel glow and
    /// the flee pill beat together rather than at two rates: several pulsing elements on their own
    /// phases read as noise rather than as urgency, and two that share a period and are armed in the
    /// same synchronous block are the nearest thing to one heartbeat that two visuals can be.</summary>
    internal const double PeriodMilliseconds = Blink.PulsePeriodMilliseconds;

    private Visual? _visual;
    private readonly FrameworkElement _host;

    private PulseLayer(FrameworkElement host)
    {
        _host = host;
        _host.Unloaded += (_, _) => Stop();   // belt-and-braces alongside OnHandlerChanged
        // The rest state, asserted here and NOT as an Opacity="0" on the MAUI element. XAML owns
        // UIElement.Opacity on the same visual this class animates, and at 0 it wins outright - that
        // is measured, not inferred: it is what made the Combat Rail's damage floats run their full
        // animation over an element nothing ever drew. So the glow's own element carries no opacity
        // in XAML, which leaves an untouched visual sitting at FULL opacity - a solid red panel the
        // instant the rail opens, with no fight anywhere - unless it is zeroed before it can be
        // shown. Stop() cannot do it: it returns early while _visual is still null.
        _visual = ElementCompositionPreview.GetElementVisual(host);
        _visual.Opacity = 0f;
    }

    public static PulseLayer Attach(FrameworkElement host) => new(host);

    /// <summary>One pulse: the opacity it swings between, peak to trough and back, and its
    /// period.</summary>
    public readonly record struct Pulse(float Peak, float Trough, double PeriodMilliseconds);

    private Pulse? _running;

    /// <summary>Pulses the layer's opacity (its colour belongs to the host element), or stops for
    /// null. Restarts only when the pulse changes: the view model republishes on every refresh, and a
    /// restart would snap the pulse back to its first keyframe each time.</summary>
    public void SetPulse(Pulse? pulse)
    {
        if (pulse is not { } p)
        {
            Stop();
            return;
        }
        if (_running == p && _visual is not null)
            return;

        _visual ??= ElementCompositionPreview.GetElementVisual(_host);
        var anim = _visual.Compositor.CreateScalarKeyFrameAnimation();
        anim.InsertKeyFrame(0.0f, p.Peak);
        anim.InsertKeyFrame(0.5f, p.Trough);
        anim.InsertKeyFrame(1.0f, p.Peak);
        anim.Duration = TimeSpan.FromMilliseconds(p.PeriodMilliseconds);
        anim.IterationBehavior = AnimationIterationBehavior.Forever;
        _visual.StartAnimation("Opacity", anim);
        _running = p;
    }

    /// <summary>Stops and detaches the animation. MUST be called from the host page's
    /// <c>OnHandlerChanged</c> when <c>Handler is null</c> - never left to GC, since a live
    /// Composition animation on a destroyed visual is exactly the RO_E_CLOSED crash class described
    /// above.</summary>
    public void Stop()
    {
        if (_visual is null)
            return;

        // Guarded for the reason FleePulse.Stop sets out at length: this is called from the host's
        // HandlerChanged, which fires while the native peer is being replaced or destroyed, so the
        // compositor behind this cached Visual may already be closed and touching it throws the
        // RO_E_CLOSED family. Unhandled, that would skip the caller's re-attach and leave the glow dead
        // for the session.
        try
        {
            _visual.StopAnimation("Opacity");
            // StopAnimation freezes the property at whatever value the animation last produced, not at
            // a defined rest state - without explicitly zeroing it here, a glow stopped mid-dip could
            // stick at partial opacity instead of going fully invisible.
            _visual.Opacity = 0f;
        }
        catch (Exception ex)
        {
            Mucka.Core.CrashLog.Write("PulseLayer.Stop", ex);
        }
        finally
        {
            _visual = null;
            _running = null;
        }
    }
}
#endif
