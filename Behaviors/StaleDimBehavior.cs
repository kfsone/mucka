#if WINDOWS
using Microsoft.UI.Xaml.Hosting;
#endif
using Mucka.ViewModels;

namespace Mucka.Behaviors;

/// <summary>
/// Dims a side-panel section toward 70% when its data goes stale, via a WinUI compositor opacity
/// animation (render/GPU thread — never touches the UI thread or typing). Restarted on every
/// refresh through <see cref="SidePanelViewModel"/>'s FewRefreshed/FeiRefreshed events.
///
/// The stale threshold is RELATIVE to the configured FES update interval, not a fixed time:
/// FEW/FEI refresh on that heartbeat, so a fixed delay would falsely dim every cycle at long
/// intervals. We hold full-bright for ~1.5× the interval — so a normal on-time refresh resets
/// before any dimming, and the dim only appears when an expected update is genuinely late. When
/// the heartbeat is disabled (interval 0) staleness is meaningless, so we never dim.
///
/// Set <see cref="Source"/> to "Few" (Online) or "Fei" (Here/Carrying). No-op on non-Windows.
/// </summary>
public sealed class StaleDimBehavior : Behavior<View>
{
    /// <summary>"Few" (Online list) or "Fei" (Here/Carrying lists).</summary>
    public string Source { get; set; } = "";

    private View? _view;
    private SidePanelViewModel? _sp;

    protected override void OnAttachedTo(View view)
    {
        base.OnAttachedTo(view);
        _view = view;
#if WINDOWS
        view.Loaded   += OnLoaded;
        view.Unloaded += OnUnloaded;
#endif
    }

    protected override void OnDetachingFrom(View view)
    {
#if WINDOWS
        view.Loaded   -= OnLoaded;
        view.Unloaded -= OnUnloaded;
        Unsubscribe();
#endif
        _view = null;
        base.OnDetachingFrom(view);
    }

#if WINDOWS
    private void OnLoaded(object? sender, EventArgs e)
    {
        if (_view?.BindingContext is GameViewModel vm)
        {
            _sp = vm.SidePanel;
            Unsubscribe();
            if (Source == "Few") _sp.FewRefreshed += RestartDim;
            else if (Source == "Fei") _sp.FeiRefreshed += RestartDim;
        }
        RestartDim();   // begin the countdown with the data we already have
    }

    private void OnUnloaded(object? sender, EventArgs e) => Unsubscribe();

    private void Unsubscribe()
    {
        if (_sp is null) return;
        _sp.FewRefreshed -= RestartDim;
        _sp.FeiRefreshed -= RestartDim;
    }

    private void RestartDim()
    {
        if (_view?.Handler?.PlatformView is not Microsoft.UI.Xaml.UIElement el) return;
        var visual = ElementCompositionPreview.GetElementVisual(el);

        // Interval drives the threshold; read it live so a config change takes effect next refresh.
        int freq = (_view.BindingContext as GameViewModel)?.StatUpdateFrequency ?? 0;
        if (freq <= 0)
        {
            // Heartbeat disabled → no staleness concept. Cancel any dim, stay full-bright.
            visual.StopAnimation("Opacity");
            visual.Opacity = 1.0f;
            return;
        }

        var c = visual.Compositor;
        var dim = c.CreateScalarKeyFrameAnimation();
        dim.Target = "Opacity";
        // Hold bright for ~1.5 intervals (a normal on-time refresh resets us before this elapses),
        // then ease to 70%. SetInitialValueBeforeDelay pins opacity to the first keyframe (1.0)
        // during the delay, so a refresh snaps the section back to full-bright immediately.
        dim.DelayTime = TimeSpan.FromSeconds(freq * 1.5);
        dim.DelayBehavior = Microsoft.UI.Composition.AnimationDelayBehavior.SetInitialValueBeforeDelay;
        dim.InsertKeyFrame(0f, 1.0f);
        dim.InsertKeyFrame(1f, 0.70f);
        dim.Duration = TimeSpan.FromSeconds(4);
        visual.StartAnimation("Opacity", dim);
    }
#endif
}
