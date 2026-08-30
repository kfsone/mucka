#if WINDOWS
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace Mucka.Rendering;

/// <summary>
/// The flee pill's pulse, on WinUI Composition - the border at
/// <see cref="MudSharp.Combat.FleePillStatus.Caution"/>, the border and a faint fill at
/// <see cref="MudSharp.Combat.FleePillStatus.EscapeNow"/>.
///
/// <para><b>Why this is not drawn by the canvas.</b> Same reason as <see cref="TickSweep"/> and
/// <see cref="PulseLayer"/>: <c>SKXamlCanvas</c> paints ON the UI thread on WinUI, so anything
/// repainting continuously there competes directly with typing (Invariant #1). The canvas draws the
/// pill's still parts; this animates the opacity of a MAUI <c>Border</c> sitting behind it, sized onto
/// the same rectangle by <see cref="CombatRailView.FleePillDp"/>.</para>
///
/// <para><b>One element carries both states, and the amplitude does the rest.</b> The border and the
/// fill live on the same visual and therefore share one opacity animation - which is what makes the
/// fill's pulse the "slight" one the design asks for without a second animation to keep in step: the
/// fill is a low-alpha colour, so the same relative swing is a small absolute change behind the text
/// while being a clearly visible one on a 1px stroke. The colours themselves are set on the MAUI
/// element (see <c>GamePage.UpdateCombatFleePill</c>), which costs nothing per frame; only the opacity
/// is animated, and only by the compositor.</para>
///
/// <para><b>Period is shared with the panel glow</b> (<see cref="PulseLayer.PeriodMilliseconds"/>).
/// At the stamina where this pill alarms, the whole-panel glow is already pulsing, and two rates would
/// be the noise DESIGN_FINAL.md 4.2 forbids rather than two facts.</para>
///
/// <para><b>Teardown is not optional</b> - see <see cref="PulseLayer"/>'s remarks for the RO_E_CLOSED
/// crash class a live animation on a destroyed visual belongs to. <see cref="Stop"/> must run from the
/// host page's <c>OnHandlerChanged</c> when <c>Handler is null</c>.</para>
/// </summary>
internal sealed class FleePulse
{
    private readonly FrameworkElement _host;
    private Visual? _visual;

    /// <summary>The trough of the pulse currently running, or null when stopped. Held so a state that
    /// republishes on every combat event, every heartbeat and every 1 Hz tick does not restart the
    /// animation each time - which would reset it to full opacity several times a second and read from
    /// the outside as a pill that does not pulse at all. Exactly the bug UpdateCombatTickSweep's own
    /// remarks record for the tick bar.</summary>
    private double? _trough;

    private FleePulse(FrameworkElement host)
    {
        _host = host;
        _host.Unloaded += (_, _) => Stop();   // belt-and-braces alongside OnHandlerChanged
        // Applied before the element is ever shown: an untouched visual sits at full opacity, which
        // would paint an alarming pill the instant the panel opens, with no fight anywhere.
        _visual = ElementCompositionPreview.GetElementVisual(host);
        _visual.Opacity = 0f;
    }

    public static FleePulse Attach(FrameworkElement host) => new(host);

    /// <summary>Starts a pulse dipping to <paramref name="trough"/> opacity, or leaves the running one
    /// alone if it is already this one.</summary>
    public void Pulse(double trough)
    {
        if (_trough == trough)
            return;

        _visual ??= ElementCompositionPreview.GetElementVisual(_host);
        var compositor = _visual.Compositor;
        var anim = compositor.CreateScalarKeyFrameAnimation();
        anim.InsertKeyFrame(0.0f, 1.0f);
        anim.InsertKeyFrame(0.5f, (float)trough);
        anim.InsertKeyFrame(1.0f, 1.0f);
        anim.Duration = TimeSpan.FromMilliseconds(PulseLayer.PeriodMilliseconds);
        anim.IterationBehavior = AnimationIterationBehavior.Forever;

        _trough = trough;
        _visual.StartAnimation("Opacity", anim);
    }

    /// <summary>Stops the pulse and hides the element. Nothing quiet is left behind: the pill's own
    /// non-alarm states are drawn entirely by the canvas, so a stopped layer must be invisible rather
    /// than resting at some readable opacity.</summary>
    public void Stop()
    {
        _trough = null;
        if (_visual is null)
            return;

        // Guarded, and the guard is the point rather than caution. Stop() is called from the host's
        // HandlerChanged, which fires when the native peer is being REPLACED OR DESTROYED - so by the
        // time this runs the compositor behind this cached Visual may already be closed, and touching it
        // throws the very RO_E_CLOSED family the call exists to prevent. Unhandled, that exception
        // propagates out of the handler and skips the re-attach below it, leaving the field pointing at a
        // defunct visual that every later update throws against: the pill would go quietly dead for the
        // rest of the session, on a surface whose whole job is to be noticed. Swallowing is correct here
        // specifically because there is nothing left to clean up - the visual we are trying to quiet has
        // already gone - but it is logged rather than silent.
        try
        {
            _visual.StopAnimation("Opacity");
            // StopAnimation freezes the property wherever the animation last left it, not at a defined
            // rest state - without zeroing it explicitly a pulse stopped mid-dip sticks at partial
            // opacity, leaving a dim alarm border around a pill that is no longer alarming.
            _visual.Opacity = 0f;
        }
        catch (Exception ex)
        {
            Mucka.Core.CrashLog.Write("FleePulse.Stop", ex);
        }
        finally
        {
            // Dropped either way: a Visual that threw here is not one to keep addressing.
            _visual = null;
        }
    }
}
#endif
