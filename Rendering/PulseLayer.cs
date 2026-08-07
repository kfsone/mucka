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
    /// <summary>Bright colour, no motion - the caller paints this directly on the canvas; the glow
    /// layer itself has nothing to do, so this and <see cref="None"/> both map to <see cref="PulseLayer.Stop"/>.</summary>
    StaticBright,
    /// <summary>Act now - continuous glow pulse, 1.2s period, forever until the tier changes.</summary>
    T3,
    /// <summary>One-off "something changed, worth a look" pulse - a single 2.5s cycle, then settles.
    /// Wired but not currently requested by this implementation phase: with one shared glow layer
    /// behind the whole panel, a persistent T3 (survival) signal always wins the layer over a
    /// transient E2 event (see SidePanelViewModel.RefreshCombatSignals's remarks) - E2 events
    /// (NPC weapon pickup) are rendered today as a time-decayed static bright flash instead, with no
    /// motion. This tier is kept faithful to the design's own sketch for whichever later stage picks
    /// pursuit's "NPC fled, available" E2 back up and needs a real one-shot pulse.</summary>
    E2,
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

    /// <summary>Starts (or restarts, if the tier differs) the glow. <see cref="PulseTier.None"/> and
    /// <see cref="PulseTier.StaticBright"/> both just stop - this method is only for the tiers that
    /// actually pulse.</summary>
    public void SetTier(PulseTier tier)
    {
        if (tier is PulseTier.None or PulseTier.StaticBright)
        {
            Stop();
            return;
        }

        _visual ??= ElementCompositionPreview.GetElementVisual(_host);
        var compositor = _visual.Compositor;
        var anim = compositor.CreateScalarKeyFrameAnimation();
        anim.InsertKeyFrame(0.0f, 1.0f);
        anim.InsertKeyFrame(0.5f, tier == PulseTier.T3 ? 0.25f : 0.45f);
        anim.InsertKeyFrame(1.0f, 1.0f);
        anim.Duration = TimeSpan.FromMilliseconds(tier == PulseTier.T3 ? 1200 : 2500);
        anim.IterationBehavior = tier == PulseTier.T3
            ? AnimationIterationBehavior.Forever
            : AnimationIterationBehavior.Count;   // E2: "one pulse only" per 4.2's tier table
        if (tier != PulseTier.T3)
            anim.IterationCount = 1;
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
