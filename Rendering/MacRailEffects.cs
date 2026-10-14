#if MACCATALYST
using CoreAnimation;
using CoreGraphics;
using Foundation;
using Mucka.Combat;
using UIKit;

namespace Mucka.Rendering;

// The Combat Rail's damage floats and unseen-tile marquee on a Mac, in Core Animation: the render
// server runs them, so they cost the typing path nothing (Invariant #1). The counterparts of
// RailFloatLayer and UnseenMarquee, whose behaviour they keep.

/// <summary>One pooled damage float: its view rises and fades once, then rests invisible.</summary>
internal sealed class MacRailFloat
{
    public const double RiseDp = 22.0;
    private const double FadeStartMs = 1000.0;
    private const double OvertravelDp = 4.0;
    private const string Key = "mucka.float";

    private readonly MacHostRef _host;
    private int _generation;

    private MacRailFloat(MacHostRef host)
    {
        _host = host;
        Rest();
    }

    public static MacRailFloat Attach(Microsoft.Maui.IElementHandler? handler, UIView fallback) => new(new MacHostRef(handler, fallback));

    /// <summary>Plays one float from (<paramref name="xDp"/>, <paramref name="yDp"/>), calling
    /// <paramref name="onCompleted"/> when it ends. A float cancelled part-way never calls it.</summary>
    public void Play(double xDp, double yDp, TimeSpan duration, Action onCompleted)
    {
        Sync();
        var generation = ++_generation;
        var layer = _host.View.Layer;
        layer.RemoveAnimation(Key);

        // Rises on a decelerating curve, starting below the origin and ending above the nominal
        // rise, so it arrives already moving and leaves still moving.
        var seconds = duration.TotalSeconds;
        var moveX = CABasicAnimation.FromKeyPath("transform.translation.x");
        moveX.From = NSNumber.FromDouble(xDp);
        moveX.To = NSNumber.FromDouble(xDp);
        var moveY = CABasicAnimation.FromKeyPath("transform.translation.y");
        moveY.From = NSNumber.FromDouble(yDp + OvertravelDp);
        moveY.To = NSNumber.FromDouble(yDp - RiseDp - OvertravelDp);
        moveY.TimingFunction = CAMediaTimingFunction.FromControlPoints(0.33f, 1f, 0.68f, 1f);

        // In at once, full for the first second, then a long fade out.
        var hold = (float)Math.Clamp(FadeStartMs / duration.TotalMilliseconds, 0.1, 0.9);
        var fade = CAKeyFrameAnimation.FromKeyPath("opacity");
        fade.Values = [NSNumber.FromFloat(0f), NSNumber.FromFloat(1f), NSNumber.FromFloat(1f), NSNumber.FromFloat(0f)];
        fade.KeyTimes = [NSNumber.FromFloat(0f), NSNumber.FromFloat(0.04f), NSNumber.FromFloat(hold), NSNumber.FromFloat(1f)];

        var group = new CAAnimationGroup
        {
            Animations = [moveX, moveY, fade],
            Duration = seconds,
            TimingFunction = CAMediaTimingFunction.FromName(CAMediaTimingFunction.Linear),
        };
        // Visible at the start: the model value, which a finished animation falls back to, is rest.
        group.AnimationStopped += (_, e) =>
        {
            if (generation != _generation || !e.Finished)
                return;
            Rest();
            onCompleted();
        };
        layer.AddAnimation(group, Key);
    }

    /// <summary>Gives the view up: no animation, fully opaque.</summary>
    public void Release()
    {
        _generation++;
        _host.View.Layer.RemoveAnimation(Key);
        _host.View.Layer.Opacity = 1f;
    }

    private void Sync()
    {
        if (!_host.Refresh(out var old))
            return;
        MacHostRef.Neutralise(old, Key);
        _host.View.Layer.Opacity = 0f;
    }

    /// <summary>Cancels any float in flight and hides the view.</summary>
    public void Rest()
    {
        Sync();
        _generation++;
        _host.View.Layer.RemoveAnimation(Key);
        _host.View.Layer.Opacity = 0f;
    }
}

/// <summary>The dotted border round an unseen, live opponent's tile: two interleaved dashed outlines
/// whose dashes step round the tile.</summary>
internal sealed class MacUnseenMarquee
{
    public const float StrokeDp = 2f;
    public const float DotDp = 3f;
    public const float PeriodDp = 16f;
    public const float StepDp = 2f;
    public const int StepMilliseconds = 250;
    private const string Key = "mucka.marquee";

    private readonly CAShapeLayer _yellow;
    private readonly CAShapeLayer _orange;
    private RailRect _shown;
    private bool _running;

    private MacUnseenMarquee(UIView host)
    {
        _yellow = Outline(Colour(TerminalTheme.Palette[11]), 0f);
        _orange = Outline(Colour(CombatRailView.NoveltyUnfought), PeriodDp / 2f);
        _yellow.Hidden = _orange.Hidden = true;
        host.Layer.AddSublayer(_yellow);
        host.Layer.AddSublayer(_orange);
    }

    public static MacUnseenMarquee Attach(UIView host) => new(host);

    private static CGColor Colour(SkiaSharp.SKColor c)
        => UIColor.FromRGBA(c.Red / 255f, c.Green / 255f, c.Blue / 255f, c.Alpha / 255f).CGColor;

    private static CAShapeLayer Outline(CGColor colour, float phase) => new()
    {
        FillColor = null,
        StrokeColor = colour,
        LineWidth = StrokeDp,
        LineDashPattern = [NSNumber.FromFloat(DotDp), NSNumber.FromFloat(PeriodDp - DotDp)],
        LineDashPhase = phase,
        LineCap = CAShapeLayer.CapButt,
    };

    public void Show(RailRect rectDp)
    {
        if (rectDp != _shown)
        {
            _shown = rectDp;
            // Centred StrokeDp / 2 outside the tile's edge on every side.
            var frame = new CGRect(
                rectDp.Left - StrokeDp / 2, rectDp.Top - StrokeDp / 2,
                rectDp.Width + StrokeDp, rectDp.Height + StrokeDp);
            var path = UIBezierPath.FromRoundedRect(frame, 6f).CGPath;
            _yellow.Path = path;
            _orange.Path = path;
        }
        _yellow.Hidden = _orange.Hidden = false;
        if (_running)
            return;

        // One full period of travel per cycle, in PeriodDp / StepDp steps of StepMilliseconds each:
        // discrete values, not a glide.
        var steps = (int)(PeriodDp / StepDp);
        _yellow.AddAnimation(Travel(0f, steps), Key);
        _orange.AddAnimation(Travel(PeriodDp / 2f, steps), Key);
        _running = true;
    }

    private static CAKeyFrameAnimation Travel(float phase, int steps)
    {
        var values = new NSObject[steps + 1];
        var times = new NSNumber[steps + 1];
        for (var i = 0; i <= steps; i++)
        {
            values[i] = NSNumber.FromFloat(phase - i * StepDp);
            times[i] = NSNumber.FromFloat((float)i / steps);
        }
        return new CAKeyFrameAnimation
        {
            KeyPath = "lineDashPhase",
            Values = values,
            KeyTimes = times,
            CalculationMode = CAAnimation.AnimationDiscrete,
            Duration = StepMilliseconds * steps / 1000.0,
            RepeatCount = float.MaxValue,
        };
    }

    /// <summary>Starts the dashes moving again if they were running; see MacPulseLayer.Reassert.</summary>
    public void Reassert()
    {
        if (!_running)
            return;
        _running = false;
        Show(_shown);
    }

    public void Hide()
    {
        if (_running)
        {
            _yellow.RemoveAnimation(Key);
            _orange.RemoveAnimation(Key);
            _running = false;
        }
        _yellow.Hidden = _orange.Hidden = true;
        _shown = default;
    }

    public void Stop()
    {
        Hide();
        _yellow.RemoveFromSuperLayer();
        _orange.RemoveFromSuperLayer();
    }
}
#endif
