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
        var anim = compositor.CreateScalarKeyFrameAnimation();
        anim.InsertKeyFrame(0.0f, 1.0f);
        anim.InsertKeyFrame(0.5f, 0.25f);
        anim.InsertKeyFrame(1.0f, 1.0f);
        anim.Duration = TimeSpan.FromMilliseconds(1200);
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
        _visual?.StopAnimation("Opacity");
        _anim = null;
        // StopAnimation freezes the property at whatever value the animation last produced, not at
        // a defined rest state - without explicitly zeroing it here, a glow stopped mid-dip could
        // stick at partial opacity instead of going fully invisible.
        if (_visual is not null)
            _visual.Opacity = 0f;
    }
}
#endif
