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
/// the inner view alone leaves that fill showing. The wrapper can appear after the handler does, so
/// every animation class calls <see cref="Refresh"/> first: it re-resolves the container-or-platform
/// view, and when that has changed the class drops its animation from the old view (opacity 1) and
/// carries on with the new one. One animation therefore always starts and stops on the same view.</summary>
internal sealed class MacHostRef
{
    private readonly Microsoft.Maui.IElementHandler? _handler;
    private UIView _view;

    public MacHostRef(Microsoft.Maui.IElementHandler? handler, UIView fallback)
    {
        _handler = handler;
        _view = MacAnimatedView.Of(handler) ?? fallback;
    }

    public UIView View => _view;

    /// <summary>True, with the view it replaces, when the container appeared or went away since the last call.</summary>
    public bool Refresh(out UIView old)
    {
        old = _view;
        var now = MacAnimatedView.Of(_handler);
        if (now is null || ReferenceEquals(now, _view))
            return false;
        _view = now;
        return true;
    }

    /// <summary>Removes the named animations from <paramref name="view"/> and leaves it fully opaque.</summary>
    public static void Neutralise(UIView view, params string[] keys)
    {
        foreach (var key in keys)
            view.Layer.RemoveAnimation(key);
        view.Layer.Opacity = 1f;
    }
}

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

    private readonly MacHostRef _host;
    private Pulse? _running;

    private MacPulseLayer(MacHostRef host)
    {
        _host = host;
        // The rest state. An untouched view is fully opaque, which would paint a solid panel the
        // moment the rail opens with no fight anywhere.
        _host.View.Layer.Opacity = 0f;
    }

    public static MacPulseLayer Attach(Microsoft.Maui.IElementHandler? handler, UIView fallback) => new(new MacHostRef(handler, fallback));

    /// <summary>Pulses, or stops for null. Restarts only when the pulse changes: it is republished on
    /// every refresh, and a restart would snap it back to its first value each time.</summary>
    public void SetPulse(Pulse? pulse)
    {
        if (pulse is not { } p)
        {
            Stop();
            return;
        }
        if (!Sync() && _running == p)
            return;
        _running = p;
        Apply(p);
    }

    /// <summary>Starts the running pulse again. Core Animation can drop a looping animation when the
    /// app is hidden or minimised, and the guard above would then never start it again.</summary>
    public void Reassert()
    {
        Sync();
        if (_running is { } p)
            Apply(p);
    }

    /// <summary>True when the host view changed: the old one is neutral and the new one at rest.</summary>
    private bool Sync()
    {
        if (!_host.Refresh(out var old))
            return false;
        MacHostRef.Neutralise(old, Key);
        _host.View.Layer.Opacity = 0f;
        return true;
    }

    private void Apply(Pulse p)
    {
        _host.View.Layer.RemoveAnimation(Key);
        _host.View.Layer.Opacity = p.Peak;
        var swing = CABasicAnimation.FromKeyPath("opacity");
        swing.From = NSNumber.FromFloat(p.Peak);
        swing.To = NSNumber.FromFloat(p.Trough);
        swing.Duration = p.PeriodMilliseconds / 2000.0;
        swing.AutoReverses = true;
        swing.RepeatCount = float.MaxValue;
        swing.TimingFunction = CAMediaTimingFunction.FromName(CAMediaTimingFunction.Linear);
        _host.View.Layer.AddAnimation(swing, Key);
    }

    public void Stop()
    {
        _running = null;
        Sync();
        _host.View.Layer.RemoveAnimation(Key);
        _host.View.Layer.Opacity = 0f;
    }

    /// <summary>Gives the view up: no animation, fully opaque.</summary>
    public void Release()
    {
        _running = null;
        _host.View.Layer.RemoveAnimation(Key);
        _host.View.Layer.Opacity = 1f;
    }
}

/// <summary>The flee pill's pulse: its border (and, when escape is urgent, a faint fill) dipping to a
/// trough opacity on the shared period.</summary>
internal sealed class MacFleePulse
{
    private const string Key = "mucka.fleepulse";

    private readonly MacHostRef _host;
    private double? _trough;

    private MacFleePulse(MacHostRef host)
    {
        _host = host;
        _host.View.Layer.Opacity = 0f;
    }

    public static MacFleePulse Attach(Microsoft.Maui.IElementHandler? handler, UIView fallback) => new(new MacHostRef(handler, fallback));

    /// <summary>Starts a pulse dipping to <paramref name="trough"/>, or leaves the running one alone
    /// if it is already this one - restarting on every republish would read as no pulse at all.</summary>
    public void Pulse(double trough)
    {
        if (!Sync() && _trough == trough)
            return;
        _trough = trough;
        Apply(trough);
    }

    /// <summary>Starts the running pulse again; see <see cref="MacPulseLayer.Reassert"/>.</summary>
    public void Reassert()
    {
        Sync();
        if (_trough is { } trough)
            Apply(trough);
    }

    /// <summary>True when the host view changed: the old one is neutral and the new one at rest.</summary>
    private bool Sync()
    {
        if (!_host.Refresh(out var old))
            return false;
        MacHostRef.Neutralise(old, Key);
        _host.View.Layer.Opacity = 0f;
        return true;
    }

    private void Apply(double trough)
    {
        _host.View.Layer.RemoveAnimation(Key);
        _host.View.Layer.Opacity = 1f;
        var dip = CABasicAnimation.FromKeyPath("opacity");
        dip.From = NSNumber.FromFloat(1f);
        dip.To = NSNumber.FromDouble(trough);
        dip.Duration = Blink.PulsePeriodMilliseconds / 2000.0;
        dip.AutoReverses = true;
        dip.RepeatCount = float.MaxValue;
        dip.TimingFunction = CAMediaTimingFunction.FromName(CAMediaTimingFunction.Linear);
        _host.View.Layer.AddAnimation(dip, Key);
    }

    /// <summary>Stops and hides: the pill's quiet states are drawn by the canvas alone.</summary>
    public void Stop()
    {
        _trough = null;
        Sync();
        _host.View.Layer.RemoveAnimation(Key);
        _host.View.Layer.Opacity = 0f;
    }

    /// <summary>Gives the view up: no animation, fully opaque.</summary>
    public void Release()
    {
        _trough = null;
        _host.View.Layer.RemoveAnimation(Key);
        _host.View.Layer.Opacity = 1f;
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

    private readonly MacHostRef _host;

    private MacTickSweep(MacHostRef host)
    {
        _host = host;
        // Hidden at rest: an untouched full-width bar would read as "swing imminent" before any fight.
        Rest();
    }

    public static MacTickSweep Attach(Microsoft.Maui.IElementHandler? handler, UIView fallback) => new(new MacHostRef(handler, fallback));

    /// <summary>Begins draining from wherever this tick has got to, empty on the boundary, then
    /// sweeps full to empty every tick. Both are timed on the render server's clock, so the handoff
    /// between them cannot be late.</summary>
    public void Restart(DateTime anchorUtc)
    {
        var remaining = CombatTiming.MillisecondsToNextBoundary(anchorUtc, DateTime.UtcNow);
        Sync();
        Clear();
        _host.View.Layer.Opacity = RunningOpacity;

        var now = CAAnimation.CurrentMediaTime();
        // Less than a couple of frames left: hold empty and begin cleanly on the boundary instead.
        var startFraction = remaining < 40.0 ? 0.0 : remaining / TickMilliseconds;

        var partial = Sweep(startFraction, remaining / 1000.0);
        partial.FillMode = CAFillMode.Forwards;   // holds empty until the loop takes over
        partial.RemovedOnCompletion = false;
        _host.View.Layer.AddAnimation(partial, PartialKey);

        var loop = Sweep(1.0, TickMilliseconds / 1000.0);
        loop.BeginTime = now + remaining / 1000.0;
        loop.RepeatCount = float.MaxValue;
        _host.View.Layer.AddAnimation(loop, LoopKey);
    }

    /// <summary>Stops and empties the bar.</summary>
    public void Stop()
    {
        Sync();
        Clear();
        Rest();
    }

    /// <summary>One sweep from <paramref name="fromFraction"/> of the bar to empty, with the LEFT end
    /// pinned: a scale about the centre plus the shift that keeps the left edge where it is.</summary>
    private CAAnimationGroup Sweep(double fromFraction, double seconds)
    {
        var width = (double)_host.View.Bounds.Width;
        var centreX = (double)_host.View.Layer.Position.X;

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
        _host.View.Layer.RemoveAnimation(PartialKey);
        _host.View.Layer.RemoveAnimation(LoopKey);
    }

    private void Rest() => _host.View.Layer.Opacity = 0f;

    private void Sync()
    {
        if (!_host.Refresh(out var old))
            return;
        MacHostRef.Neutralise(old, PartialKey, LoopKey);
        Rest();
    }

    /// <summary>Gives the view up: no animation, fully opaque.</summary>
    public void Release()
    {
        Clear();
        _host.View.Layer.Opacity = 1f;
    }
}
#endif
