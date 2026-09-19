#if WINDOWS
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace Mucka.Rendering;

/// <summary>
/// One pooled damage float, animated on WinUI Composition: a small word or number that appears over
/// a pane of the Combat Rail, drifts upward and fades out over <c>RailFloatBudget.Lifetime</c>.
///
/// <para><b>Why this is not drawn by the canvas.</b> The same Invariant #1 that put the tick
/// meter's fill and the flee pill's pulse on the compositor: a float is by definition continuous
/// motion - repainting the rail thirty times a second for a second and a half, several times per
/// two-second tick, is exactly the work that competes with typing. The canvas draws the still
/// panel; this element rides above it and costs the UI thread nothing once started. See
/// <see cref="TickSweep"/>, which says the same thing at greater length about a bar.</para>
///
/// <para><b>Translation, not Offset.</b> A XAML element's visual Offset is owned by layout and is
/// rewritten on every arrange, so animating it would fight the layout system for the property and
/// lose the moment anything reflowed. <c>Translation</c> is the property WinUI exposes for exactly
/// this - additive to the arranged position, untouched by layout - and it has to be switched on per
/// element with <c>SetIsTranslationEnabled</c> before the compositor will accept an animation for
/// it. The host therefore never moves the MAUI element at all: the element is arranged once at the
/// panel's top-left, at a fixed size, and every float's position is a Translation keyframe. That is
/// what keeps a combat event off the layout path entirely.</para>
///
/// <para><b>Teardown is not optional</b> - see <see cref="PulseLayer"/> and <see cref="TickSweep"/>
/// for the RO_E_CLOSED crash class a live animation on a destroyed visual belongs to.
/// <see cref="Stop"/> must run from the host's <c>HandlerChanged</c> when <c>Handler is null</c>.</para>
///
/// <para><b>Unloaded is not teardown.</b> WinUI raises it whenever the element leaves the live tree,
/// which for a pooled float means every time the combat panel's Border is collapsed - the panel is
/// hidden, a modal covers the page - and the platform view survives all of it. So the hook below
/// rests the layer (which is what actually answers the crash class: no animation is left running)
/// and does NOT mark it detached. A detached layer is permanent and nothing re-attaches it, because
/// the host only re-attaches on <c>HandlerChanged</c> and a collapse never recreates the handler:
/// one hidden panel would kill every float for the rest of the session, silently, with
/// <see cref="Play"/> returning false and no exception anywhere.</para>
/// </summary>
internal sealed class RailFloatLayer
{
    /// <summary>How far a float rises over its life, in dp. Roughly half an opponent slot: far
    /// enough that the motion reads as a rise rather than a flicker, short enough that the number
    /// never leaves the neighbourhood of the pane it is reporting on - a float that travelled the
    /// height of the panel would be a number the eye has to chase to attribute.</summary>
    public const double RiseDp = 22.0;

    /// <summary>How long a float holds full opacity before it starts going out. Absolute rather
    /// than a fraction, so it does not have to be re-derived by hand whenever the lifetime moves.</summary>
    private const double FadeStartMs = 1000.0;

    /// <summary>How far past each end of <see cref="RiseDp"/> the travel actually runs. The float
    /// is already moving when it becomes legible and still moving when it goes, which is what makes
    /// the rise read as motion rather than as a step.</summary>
    private const double OvertravelDp = 4.0;

    private readonly FrameworkElement _host;
    private readonly Visual _visual;

    /// <summary>Bumped by every <see cref="Play"/> and every <see cref="Rest"/>. A
    /// <c>CompositionScopedBatch</c> cannot be cancelled once ended, so its Completed WILL fire even
    /// after the slot was shed and re-let or the whole layer was reset; a stale completion would
    /// retire a float that is still in the air. Same hazard, same counter, as TickSweep's.</summary>
    private int _generation;

    /// <summary>Set once a Composition call has thrown, which means the compositor behind this
    /// visual is gone and every later call would throw the same way.</summary>
    private bool _detached;

    /// <summary>Whether an animation is actually attached. Kept so <see cref="Rest"/> does not call
    /// StopAnimation on a visual that has never been animated - the first Rest happens at attach,
    /// before the element has ever been shown, and a throw there would mark the layer detached and
    /// silently kill this pool slot for the rest of the session.</summary>
    private bool _playing;

    private RailFloatLayer(FrameworkElement host)
    {
        _host = host;
        // Must precede any animation of "Translation" - without it the property does not exist on
        // the visual and StartAnimation is a silent no-op.
        ElementCompositionPreview.SetIsTranslationEnabled(host, true);
        _visual = ElementCompositionPreview.GetElementVisual(host);
        // Rest, not Stop - see the class remarks. Stopping the animations is the whole of the crash
        // guard; the permanent flag Stop adds is what killed the feature.
        _host.Unloaded += (_, _) => Rest();
        Rest();
    }

    public static RailFloatLayer Attach(FrameworkElement host) => new(host);

    /// <summary>
    /// Runs one float from (<paramref name="xDp"/>, <paramref name="yDp"/>) upward, fading out.
    /// Returns false when the compositor is gone, in which case nothing was started and the caller
    /// must give the pool slot straight back rather than wait for a completion that will not come.
    /// </summary>
    /// <param name="onCompleted">Runs on the UI thread when the animation ends, and only for the
    /// most recent <see cref="Play"/> on this element.</param>
    public bool Play(double xDp, double yDp, TimeSpan duration, Action onCompleted)
    {
        if (_detached)
            return false;

        try
        {
            var generation = ++_generation;
            var compositor = _visual.Compositor;
            var linear = compositor.CreateLinearEasingFunction();

            // Decelerating rise: quick off the mark, easing to a near stop as it fades. The
            // acceleration is doing real work - it makes the float legible in its first frames,
            // which is when it is at full opacity, instead of spending them barely moving.
            // Starts BELOW the origin and ends above the nominal rise, so the travel is longer than
            // the distance the eye is meant to read the float moving through - it arrives already
            // moving and leaves still moving, instead of springing into existence at rest.
            var move = compositor.CreateVector3KeyFrameAnimation();
            move.InsertKeyFrame(0f, new Vector3((float)xDp, (float)(yDp + OvertravelDp), 0f), linear);
            move.InsertKeyFrame(
                1f, new Vector3((float)xDp, (float)(yDp - RiseDp - OvertravelDp), 0f),
                // Ease-out cubic. The previous curve put nearly all the travel in the first fifth
                // and then stopped dead, which read as a jump rather than a rise; this spreads the
                // motion and decelerates into the end.
                compositor.CreateCubicBezierEasingFunction(new Vector2(0.33f, 1f), new Vector2(0.68f, 1f)));
            move.Duration = duration;

            // In at once, hold full for the first second, then a long fade over the remaining two -
            // so the float is legible immediately, stays plainly readable while the blow it reports
            // is the current one, and spends most of its life on the way out rather than snapping
            // off. A float that starts fading immediately is unreadable at exactly the moment it is
            // worth reading; the fade is how it leaves, not how it arrives.
            //
            // The hold is derived from the duration actually being played, not from the budget's
            // constant: these keyframes are fractions of THIS animation, so reading the lifetime
            // instead would silently misplace the hold the moment the two stopped agreeing. Clamped
            // so a short duration cannot produce keyframes out of order.
            var holdFraction = (float)Math.Clamp(FadeStartMs / duration.TotalMilliseconds, 0.1, 0.9);
            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0f, 0f, linear);
            fade.InsertKeyFrame(0.04f, 1f, linear);
            fade.InsertKeyFrame(holdFraction, 1f, linear);
            fade.InsertKeyFrame(1f, 0f, linear);
            fade.Duration = duration;

            var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            _visual.StartAnimation("Translation", move);
            _visual.StartAnimation("Opacity", fade);
            _playing = true;
            batch.End();
            batch.Completed += (_, _) =>
            {
                if (generation != _generation)
                    return;
                Rest();
                onCompleted();
            };
            return true;
        }
        catch (Exception ex)
        {
            _detached = true;
            Mucka.Core.CrashLog.Write("RailFloatLayer.Play", ex);
            return false;
        }
    }

    /// <summary>Invisible and still, with no animation attached. The rest state is opacity zero:
    /// the element is a real MAUI Label sitting over the panel, and an untouched visual sits at full
    /// opacity, which would print the previous float's text permanently over the rail.</summary>
    public void Rest()
    {
        // Bumped outside the guard so a completion already queued for the float being cancelled
        // finds itself stale even if the Composition calls below fail.
        _generation++;
        if (_detached)
            return;

        try
        {
            if (_playing)
            {
                _visual.StopAnimation("Translation");
                _visual.StopAnimation("Opacity");
                _playing = false;
            }
            // StopAnimation freezes the property wherever the animation left it, so the rest state
            // has to be asserted rather than assumed.
            _visual.Properties.InsertVector3("Translation", Vector3.Zero);
            _visual.Opacity = 0f;
        }
        catch (Exception ex)
        {
            _detached = true;
            Mucka.Core.CrashLog.Write("RailFloatLayer.Rest", ex);
        }
    }

    /// <summary>Permanent teardown, for the host's HandlerChanged and page teardown only - both
    /// replace or null the instance straight after. NOT for Unloaded: see the class remarks.</summary>
    public void Stop()
    {
        Rest();
        _detached = true;
    }
}
#endif
