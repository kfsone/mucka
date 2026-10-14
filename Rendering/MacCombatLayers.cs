#if MACCATALYST
using CoreAnimation;
using Foundation;
using Mucka.Combat;
using UIKit;

namespace Mucka.Rendering;

// The Combat Rail's animated layers on a Mac. Each one animates a native view's layer with Core
// Animation, which the render server carries out - nothing here repeats on the app's UI thread,
// which is what keeps the typing path clear (Invariant #1). They stand in for the Windows
// Composition classes PulseLayer, FleePulse and TickSweep, with the same behaviour.
//
// Every animation here changes only the PRESENTATION of the layer. The layer's own opacity and
// transform are left alone (apart from opacity as the rest state), because UIKit reads them back
// when it lays the view out - a layer left with a transform makes the next layout size it wrongly.

/// <summary>The view an animation acts on. A handler whose element needs a container (the Combat Rail's
/// BoxView and Border elements do) puts its platform view inside a Microsoft.Maui.Platform.WrapperView
/// that paints a background of its own - 0.78 grey in the rail's view-tree dump - so an animation on
/// the inner view alone leaves that fill showing. Callers resolve this once per Attach, hand the
/// result to the animation class, and the class acts on that one instance until it is released.</summary>
internal static class MacAnimatedView
{
    public static UIView? Of(Microsoft.Maui.IElementHandler? handler)
        => handler is Microsoft.Maui.IPlatformViewHandler platform ? platform.ContainerView ?? platform.PlatformView : null;
}

/// <summary>A layer's opacity swinging between two values on a loop: the panel glow and its trim.</summary>
internal sealed class MacPulseLayer
{
    private const string Key = "mucka.pulse";

    /// <summary>The opacity it swings between, peak to trough and back, and its period.</summary>
    public readonly record struct Pulse(float Peak, float Trough, double PeriodMilliseconds);

    private readonly UIView _view;
    private Pulse? _running;

    private MacPulseLayer(UIView view)
    {
        _view = view;
        // The rest state. An untouched view is fully opaque, which would paint a solid panel the
        // moment the rail opens with no fight anywhere.
        view.Layer.Opacity = 0f;
    }

    public static MacPulseLayer Attach(UIView view) => new(view);

    /// <summary>Pulses, or stops for null. Restarts only when the pulse changes: it is republished on
    /// every refresh, and a restart would snap it back to its first value each time.</summary>
    public void SetPulse(Pulse? pulse)
    {
        if (pulse is not { } p)
        {
            Stop();
            return;
        }
        if (_running == p)
            return;
        _running = p;
        Apply(p);
    }

    /// <summary>Starts the running pulse again. Core Animation can drop a looping animation when the
    /// app is hidden or minimised, and the guard above would then never start it again.</summary>
    public void Reassert()
    {
        if (_running is { } p)
            Apply(p);
    }

    private void Apply(Pulse p)
    {
        _view.Layer.RemoveAnimation(Key);
        _view.Layer.Opacity = p.Peak;
        var swing = CABasicAnimation.FromKeyPath("opacity");
        swing.From = NSNumber.FromFloat(p.Peak);
        swing.To = NSNumber.FromFloat(p.Trough);
        swing.Duration = p.PeriodMilliseconds / 2000.0;
        swing.AutoReverses = true;
        swing.RepeatCount = float.MaxValue;
        swing.TimingFunction = CAMediaTimingFunction.FromName(CAMediaTimingFunction.Linear);
        _view.Layer.AddAnimation(swing, Key);
    }

    public void Stop()
    {
        _running = null;
        _view.Layer.RemoveAnimation(Key);
        _view.Layer.Opacity = 0f;
    }

    /// <summary>Gives the view up: no animation, fully opaque.</summary>
    public void Release()
    {
        _running = null;
        _view.Layer.RemoveAnimation(Key);
        _view.Layer.Opacity = 1f;
    }
}

/// <summary>The flee pill's pulse: its border (and, when escape is urgent, a faint fill) dipping to a
/// trough opacity on the shared period.</summary>
internal sealed class MacFleePulse
{
    private const string Key = "mucka.fleepulse";

    private readonly UIView _view;
    private double? _trough;

    private MacFleePulse(UIView view)
    {
        _view = view;
        view.Layer.Opacity = 0f;
    }

    public static MacFleePulse Attach(UIView view) => new(view);

    /// <summary>Starts a pulse dipping to <paramref name="trough"/>, or leaves the running one alone
    /// if it is already this one - restarting on every republish would read as no pulse at all.</summary>
    public void Pulse(double trough)
    {
        if (_trough == trough)
            return;
        _trough = trough;
        Apply(trough);
    }

    /// <summary>Starts the running pulse again; see <see cref="MacPulseLayer.Reassert"/>.</summary>
    public void Reassert()
    {
        if (_trough is { } trough)
            Apply(trough);
    }

    private void Apply(double trough)
    {
        _view.Layer.RemoveAnimation(Key);
        _view.Layer.Opacity = 1f;
        var dip = CABasicAnimation.FromKeyPath("opacity");
        dip.From = NSNumber.FromFloat(1f);
        dip.To = NSNumber.FromDouble(trough);
        dip.Duration = Blink.PulsePeriodMilliseconds / 2000.0;
        dip.AutoReverses = true;
        dip.RepeatCount = float.MaxValue;
        dip.TimingFunction = CAMediaTimingFunction.FromName(CAMediaTimingFunction.Linear);
        _view.Layer.AddAnimation(dip, Key);
    }

    /// <summary>Stops and hides: the pill's quiet states are drawn by the canvas alone.</summary>
    public void Stop()
    {
        _trough = null;
        _view.Layer.RemoveAnimation(Key);
        _view.Layer.Opacity = 0f;
    }

    /// <summary>Gives the view up: no animation, fully opaque.</summary>
    public void Release()
    {
        _trough = null;
        _view.Layer.RemoveAnimation(Key);
        _view.Layer.Opacity = 1f;
    }
}

/// <summary>The tick meter's fill: a bar that drains toward its left end once per tick, kept on the
/// combat tick's boundaries by timing it from the encounter's anchor.</summary>
internal sealed class MacTickSweep
{
    private const string PartialKey = "mucka.tick.partial";
    private const string LoopKey = "mucka.tick.loop";
    private const double TickMilliseconds = CombatTiming.TickMilliseconds;

    /// <summary>What the bar shows at while a fight runs: the <c>Opacity</c> the XAML gives the
    /// element (CombatTickSweep in GamePage.xaml).</summary>
    private const float RunningOpacity = 0.20f;

    private readonly UIView _view;

    private MacTickSweep(UIView view)
    {
        _view = view;
        // Hidden at rest: an untouched full-width bar would read as "swing imminent" before any fight.
        Rest();
    }

    public static MacTickSweep Attach(UIView view) => new(view);

    /// <summary>Begins draining from wherever this tick has got to, empty on the boundary, then
    /// sweeps full to empty every tick. Both are timed on the render server's clock, so the handoff
    /// between them cannot be late.</summary>
    public void Restart(DateTime anchorUtc)
    {
        var remaining = CombatTiming.MillisecondsToNextBoundary(anchorUtc, DateTime.UtcNow);
        Clear();
        _view.Layer.Opacity = RunningOpacity;

        var now = CAAnimation.CurrentMediaTime();
        // Less than a couple of frames left: hold empty and begin cleanly on the boundary instead.
        var startFraction = remaining < 40.0 ? 0.0 : remaining / TickMilliseconds;

        var partial = Sweep(startFraction, remaining / 1000.0);
        partial.FillMode = CAFillMode.Forwards;   // holds empty until the loop takes over
        partial.RemovedOnCompletion = false;
        _view.Layer.AddAnimation(partial, PartialKey);

        var loop = Sweep(1.0, TickMilliseconds / 1000.0);
        loop.BeginTime = now + remaining / 1000.0;
        loop.RepeatCount = float.MaxValue;
        _view.Layer.AddAnimation(loop, LoopKey);
    }

    /// <summary>Stops and empties the bar.</summary>
    public void Stop()
    {
        Clear();
        Rest();
    }

    /// <summary>One sweep from <paramref name="fromFraction"/> of the bar to empty, with the LEFT end
    /// pinned: a scale about the centre plus the shift that keeps the left edge where it is.</summary>
    private CAAnimationGroup Sweep(double fromFraction, double seconds)
    {
        var width = (double)_view.Bounds.Width;
        var centreX = (double)_view.Layer.Position.X;

        var scale = CABasicAnimation.FromKeyPath("transform.scale.x");
        scale.From = NSNumber.FromDouble(fromFraction);
        scale.To = NSNumber.FromDouble(0.0);

        var pin = CABasicAnimation.FromKeyPath("position.x");
        pin.From = NSNumber.FromDouble(centreX - (1.0 - fromFraction) * width / 2.0);
        pin.To = NSNumber.FromDouble(centreX - width / 2.0);

        var group = new CAAnimationGroup
        {
            Animations = [scale, pin],
            Duration = Math.Max(seconds, 0.001),
            TimingFunction = CAMediaTimingFunction.FromName(CAMediaTimingFunction.Linear),
        };
        return group;
    }

    private void Clear()
    {
        _view.Layer.RemoveAnimation(PartialKey);
        _view.Layer.RemoveAnimation(LoopKey);
    }

    private void Rest() => _view.Layer.Opacity = 0f;

    /// <summary>Gives the view up: no animation, fully opaque.</summary>
    public void Release()
    {
        Clear();
        _view.Layer.Opacity = 1f;
    }
}
#endif
