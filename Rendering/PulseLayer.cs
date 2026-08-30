#if WINDOWS
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace Mucka.Rendering;

/// <summary>Tier passed to <see cref="PulseLayer.SetTier"/>. Mirrors DESIGN_FINAL.md 7.1's sketch
/// exactly (kept as its own small enum, distinct from <see cref="MudSharp.Combat.CombatTier"/>,
/// because the two answer different questions: CombatTier is "how urgent is this signal", PulseTier
/// is "what should the shared glow layer physically do about it").</summary>
public enum PulseTier
{
    /// <summary>No glow.</summary>
    None,
    /// <summary>Act now - continuous glow pulse, 1.2s period, forever until the tier changes.</summary>
    T3,
}

/// <summary>
/// WinUI Composition glow helper - DESIGN_FINAL.md 7.1's `PulseLayer` sketch, ported essentially
/// verbatim. Animates the OPACITY of a layer positioned BEHIND the Skia canvas (a transparent-
/// background `Border`/`Rectangle` in the same grid cell), never the canvas's own drawn text colour
/// - Invariant #1 forbids driving continuous motion through anything that costs UI-thread time, and
/// `SKXamlCanvas` paints ON the UI thread on WinUI, so it must never be the thing animating.
///
/// <para><b>Teardown is not optional.</b> This codebase has a live crash precedent for exactly this
/// failure shape: `ClogPage`'s own remarks (see its git history / DESIGN_FINAL.md 7.1) describe a
/// page that stayed subscribed after its hosting window closed, so the next combat line rendered
/// into already-destroyed WinUI objects and took the whole process down with `RO_E_CLOSED`
/// (0x80000013). A leaked, still-running Composition animation on a torn-down visual is the
/// identical shape - a live animation handle referencing a Visual whose underlying native object is
/// gone. <see cref="Stop"/> MUST be called from the host's own `OnHandlerChanged` when
/// <c>Handler is null</c>, exactly where `ClogPage.Detach()` used to run from.</para>
/// </summary>
internal sealed class PulseLayer
{
    /// <summary>The client's one pulse period. Shared with <see cref="FleePulse"/> so the panel glow and
    /// the flee pill beat together rather than at two rates - DESIGN_FINAL.md 4.2 allows one pulsing
    /// element at a time precisely because several on their own phases read as noise rather than as
    /// urgency, and two that share a period and are armed in the same synchronous block are the nearest
    /// thing to one heartbeat that two visuals can be.</summary>
    internal const double PeriodMilliseconds = 1200.0;

    private CompositionAnimation? _anim;
    private Visual? _visual;
    private readonly FrameworkElement _host;

    private PulseLayer(FrameworkElement host)
    {
        _host = host;
        _host.Unloaded += (_, _) => Stop();   // belt-and-braces alongside OnHandlerChanged
    }

    public static PulseLayer Attach(FrameworkElement host) => new(host);

    /// <summary>Starts (or restarts) the glow for <see cref="PulseTier.T3"/>. <see cref="PulseTier.None"/>
    /// just stops.</summary>
    public void SetTier(PulseTier tier)
    {
        if (tier is not PulseTier.T3)
        {
            Stop();
            return;
        }

        _visual ??= ElementCompositionPreview.GetElementVisual(_host);
        var compositor = _visual.Compositor;
        // Halved, 2026-08-28 (owner). It ran 1.0 -> 0.25 -> 1.0, and in play that flash dominated the
        // panel so completely that the flee pill - the one element with something actionable on it -
        // did not draw the eye at all, in the exact fight it was built for (23 stamina against a
        // banshee). Both ends are halved rather than just the trough raised: raising the trough would
        // shrink the SWING while leaving the panel brighter on average, which is the opposite of what
        // is wanted. This way the colour change is half what it was and the glow is dimmer throughout,
        // so the pill has somewhere to stand out from.
        //
        // The glow is still the loudest thing the client owns; it is just no longer the ONLY thing
        // visible while it runs.
        var anim = compositor.CreateScalarKeyFrameAnimation();
        anim.InsertKeyFrame(0.0f, 0.5f);
        anim.InsertKeyFrame(0.5f, 0.125f);
        anim.InsertKeyFrame(1.0f, 0.5f);
        anim.Duration = TimeSpan.FromMilliseconds(PeriodMilliseconds);
        anim.IterationBehavior = AnimationIterationBehavior.Forever;
        _anim = anim;
        _visual.StartAnimation("Opacity", anim);
    }

    /// <summary>Stops and detaches the animation. MUST be called from the host page's
    /// <c>OnHandlerChanged</c> when <c>Handler is null</c> - never left to GC, since a live
    /// Composition animation on a destroyed visual is exactly the RO_E_CLOSED crash class described
    /// above.</summary>
    public void Stop()
    {
        _anim = null;
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
        }
    }
}
#endif
