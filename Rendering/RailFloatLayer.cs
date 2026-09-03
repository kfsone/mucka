#if WINDOWS
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace Mucka.Rendering;

/// <summary>
/// One pooled damage float, animated on WinUI Composition: a small word or number that appears over
/// a pane of the Combat Rail, drifts upward and fades out over about a second and a half.
///
/// <para><b>Why this is not drawn by the canvas.</b> The same Invariant #1 that put the tick
/// meter's fill and the flee pill's pulse on the compositor. <c>SKXamlCanvas</c> paints ON the UI
/// thread on WinUI, and a float is by definition continuous motion - repainting the rail thirty
/// times a second for a second and a half, several times per two-second tick, is exactly the work
/// that competes with typing. The canvas draws the still panel; this element rides above it and
/// costs the UI thread nothing once started. See <see cref="TickSweep"/>, which says the same thing
/// at greater length about a bar.</para>
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
/// <see cref="Stop"/> must run from the host's <c>HandlerChanged</c> when <c>Handler is null</c>,
/// and the <c>Unloaded</c> hook below is belt-and-braces beside it.</para>
/// </summary>
internal sealed class RailFloatLayer
{
    /// <summary>How far a float rises over its life, in dp. Roughly half an opponent slot: far
    /// enough that the motion reads as a rise rather than a flicker, short enough that the number
    /// never leaves the neighbourhood of the pane it is reporting on - a float that travelled the
    /// height of the panel would be a number the eye has to chase to attribute.</summary>
    public const double RiseDp = 22.0;

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
        _host.Unloaded += (_, _) => Stop();
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
            var move = compositor.CreateVector3KeyFrameAnimation();
            move.InsertKeyFrame(0f, new Vector3((float)xDp, (float)yDp, 0f), linear);
            move.InsertKeyFrame(
                1f, new Vector3((float)xDp, (float)(yDp - RiseDp), 0f),
                compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f)));
            move.Duration = duration;

            // Full opacity for the first two thirds, then out. A float that starts fading
            // immediately is unreadable at exactly the moment it is worth reading; the fade is how
            // it leaves, not how it arrives.
            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0f, 0f, linear);
            fade.InsertKeyFrame(0.08f, 1f, linear);
            fade.InsertKeyFrame(0.62f, 1f, linear);
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

    /// <summary>Permanent teardown. Called from the host's HandlerChanged and from Unloaded; after
    /// this the instance is dead and the host creates a fresh one on re-attach.</summary>
    public void Stop()
    {
        Rest();
        _detached = true;
    }
}
#endif
