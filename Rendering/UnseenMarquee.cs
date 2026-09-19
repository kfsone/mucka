#if WINDOWS
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Mucka.Combat;
using Mucka.Terminal;

namespace Mucka.Rendering;

/// <summary>
/// The unknown badge's marquee, on WinUI Composition: dots stepping along the badge's edge, yellow
/// and orange alternating, just outside the still dashed frame the canvas draws
/// (<see cref="CombatRailView.DrawUnseenFrame"/>). The operator's cue for "the client cannot say who
/// this is": motion that reads as a search, on a shape that is otherwise a slot like any other.
///
/// <para><b>Why this is not drawn by the canvas.</b> Same reason as <see cref="TickSweep"/>,
/// <see cref="FleePulse"/> and <see cref="RailFloatLayer"/>: continuous motion repainted on the UI
/// thread competes with typing (Invariant #1). A <see cref="ShapeVisual"/> is handed to the compositor
/// once per badge; what moves is one animated property per stroke, and the UI thread pays nothing
/// after the start.</para>
///
/// <para><b>One host element, positioned by the visual, not by layout.</b> The host is a transparent
/// element arranged once at the panel's top-left; the shape visual is its child visual, given the
/// badge's rectangle as its own Offset and Size, and a child visual is not clipped to its host. So a
/// badge growing by a slot moves nothing in the XAML tree - the visual's size changes and the geometry
/// with it. Steps, not a slide: a step easing with one step per <see cref="StepMilliseconds"/> moves
/// the dash offset by <see cref="StepDp"/> at a time, which is what makes it a marquee rather than a
/// crawl.</para>
///
/// <para><b>Teardown is not optional</b> - see <see cref="PulseLayer"/> for the RO_E_CLOSED crash class
/// a live animation on a destroyed visual belongs to. <see cref="Stop"/> must run from the host's
/// <c>HandlerChanged</c> when <c>Handler is null</c>, and from page teardown.</para>
/// </summary>
internal sealed class UnseenMarquee
{
    /// <summary>The stroke the dots are drawn with, and how far outside the tile they sit: centred one
    /// unit outside the tile's edge, so they ride in the slot gap rather than over the canvas's own
    /// frame.</summary>
    public const float StrokeDp = 2f;

    /// <summary>One dot and the gap after it, along the edge. Two strokes share this pattern half a
    /// period apart, so the eye sees a yellow dot, a gap, an orange dot, a gap.</summary>
    public const float DotDp = 3f;
    public const float PeriodDp = 16f;

    /// <summary>How far the dots move at each step, and how often.</summary>
    public const float StepDp = 2f;
    public const int StepMilliseconds = 250;

    private readonly FrameworkElement _host;
    private ShapeVisual? _visual;
    private CompositionRoundedRectangleGeometry? _geometry;
    private CompositionSpriteShape? _yellow;
    private CompositionSpriteShape? _orange;
    private RailRect _shown;
    private bool _running;
    private bool _detached;

    private UnseenMarquee(FrameworkElement host)
    {
        _host = host;
        _host.Unloaded += (_, _) => Hide();   // rests, never detaches - see RailFloatLayer's remarks
        try
        {
            var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
            _geometry = compositor.CreateRoundedRectangleGeometry();
            _geometry.CornerRadius = new Vector2(6f, 6f);
            _yellow = Stroke(compositor, _geometry, TerminalTheme.Palette[11], 0f);
            _orange = Stroke(compositor, _geometry, CombatRailView.NoveltyUnfought, PeriodDp / 2f);
            _visual = compositor.CreateShapeVisual();
            _visual.Shapes.Add(_yellow);
            _visual.Shapes.Add(_orange);
            _visual.IsVisible = false;
            ElementCompositionPreview.SetElementChildVisual(host, _visual);
        }
        catch (Exception ex)
        {
            _detached = true;
            Mucka.Core.CrashLog.Write("UnseenMarquee.Attach", ex);
        }
    }

    private static CompositionSpriteShape Stroke(
        Compositor compositor, CompositionGeometry geometry, SkiaSharp.SKColor color, float phase)
    {
        var shape = compositor.CreateSpriteShape(geometry);
        shape.StrokeBrush = compositor.CreateColorBrush(
            Windows.UI.Color.FromArgb(color.Alpha, color.Red, color.Green, color.Blue));
        shape.StrokeThickness = StrokeDp;
        shape.StrokeDashArray.Add(DotDp);
        shape.StrokeDashArray.Add(PeriodDp - DotDp);
        shape.StrokeDashOffset = phase;
        shape.StrokeDashCap = CompositionStrokeCap.Flat;
        return shape;
    }

    public static UnseenMarquee Attach(FrameworkElement host) => new(host);

    /// <summary>Draws the marquee around <paramref name="rectDp"/> (the badge's tile, in dp from the
    /// panel content box's top-left) and runs it. Republished on every refresh, so an unchanged
    /// rectangle is a no-op and a running animation is never restarted - restarting it several times a
    /// second would hold the dots still, the tick bar's own old bug.</summary>
    public void Show(RailRect rectDp)
    {
        if (_detached || _visual is null || _geometry is null || _yellow is null || _orange is null)
            return;
        try
        {
            if (rectDp != _shown)
            {
                _shown = rectDp;
                _visual.Offset = new Vector3((float)rectDp.Left, (float)rectDp.Top, 0f);
                _visual.Size = new Vector2((float)rectDp.Width, (float)rectDp.Height);
                // Centred StrokeDp / 2 outside the tile's edge on every side.
                _geometry.Offset = new Vector2(-StrokeDp / 2f, -StrokeDp / 2f);
                _geometry.Size = new Vector2((float)rectDp.Width + StrokeDp, (float)rectDp.Height + StrokeDp);
            }
            _visual.IsVisible = true;
            if (_running)
                return;

            var compositor = _visual.Compositor;
            // One full period of travel per cycle, in PeriodDp / StepDp steps of StepMilliseconds
            // each, forever. A negative offset moves the dashes forward along the path.
            var steps = (int)(PeriodDp / StepDp);
            var easing = compositor.CreateStepEasingFunction(steps);
            _yellow.StartAnimation("StrokeDashOffset", Travel(compositor, easing, 0f, steps));
            _orange.StartAnimation("StrokeDashOffset", Travel(compositor, easing, PeriodDp / 2f, steps));
            _running = true;
        }
        catch (Exception ex)
        {
            _detached = true;
            Mucka.Core.CrashLog.Write("UnseenMarquee.Show", ex);
        }
    }

    private static ScalarKeyFrameAnimation Travel(
        Compositor compositor, CompositionEasingFunction easing, float phase, int steps)
    {
        var anim = compositor.CreateScalarKeyFrameAnimation();
        anim.InsertKeyFrame(0f, phase);
        anim.InsertKeyFrame(1f, phase - PeriodDp, easing);
        anim.Duration = TimeSpan.FromMilliseconds(StepMilliseconds * steps);
        anim.IterationBehavior = AnimationIterationBehavior.Forever;
        return anim;
    }

    /// <summary>Invisible and still. The badge is gone, or the panel is.</summary>
    public void Hide()
    {
        if (_detached || _visual is null)
            return;
        try
        {
            if (_running)
            {
                _yellow?.StopAnimation("StrokeDashOffset");
                _orange?.StopAnimation("StrokeDashOffset");
                _running = false;
            }
            _visual.IsVisible = false;
            _shown = default;
        }
        catch (Exception ex)
        {
            _detached = true;
            Mucka.Core.CrashLog.Write("UnseenMarquee.Hide", ex);
        }
    }

    /// <summary>Permanent teardown, for the host's HandlerChanged and page teardown only.</summary>
    public void Stop()
    {
        Hide();
        _detached = true;
    }
}
#endif
