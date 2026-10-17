#if WINDOWS
using Microsoft.UI.Xaml.Hosting;
#endif
using Mucka.ViewModels;

namespace Mucka.Behaviors;

/// <summary>
/// Fades the gold, bold overlay label that sits over a list row, revealing the plain row beneath:
/// the "just arrived" highlight of the Carrying and Here lists. The fade is a compositor (Windows)
/// or Core Animation (Mac) opacity animation, so it runs off the UI thread and costs the typing path
/// nothing (Invariant #1); there is no timer.
///
/// The animation runs for whatever remains of <see cref="Mucka.Ui.ArrivalLedger.GlowFor"/> since the
/// row's arrival, not the full length: a list rebuild re-templates its rows, and a rebuilt row must
/// resume mid-fade rather than flash gold again. The row leaving the list ends the fade by removing
/// the view.
///
/// Android has no fade here: the overlay is hidden, and the row simply arrives in its new place.
/// </summary>
public sealed class ArrivalGlowBehavior : Behavior<View>
{
    private static readonly TimeSpan Length = Mucka.Ui.ArrivalLedger.GlowFor;

#if DESKTOP
    private View? _view;
#endif

    protected override void OnAttachedTo(View view)
    {
        base.OnAttachedTo(view);
#if DESKTOP
        _view = view;
        view.Loaded += OnLoaded;
#else
        view.IsVisible = false;
#endif
    }

    protected override void OnDetachingFrom(View view)
    {
#if DESKTOP
        view.Loaded -= OnLoaded;
        _view = null;
#endif
        base.OnDetachingFrom(view);
    }

#if DESKTOP
    private void OnLoaded(object? sender, EventArgs e)
    {
        if (_view is null) return;
        var started = (_view.BindingContext as RoomEntry)?.GlowStartedUtc
                      ?? (_view.BindingContext as InventoryEntry)?.GlowStartedUtc;
        if (started is null) return;

        var elapsed = DateTime.UtcNow - started.Value;
        var remaining = Length - elapsed;

        // The curve sampled over the time left, so a resumed row continues the same curve.
        const int Steps = 12;
        var samples = new float[Steps + 1];
        for (var i = 0; i <= Steps; i++)
            samples[i] = Mucka.Ui.ArrivalLedger.GlowOpacity(elapsed + remaining * i / Steps);

#if MACCATALYST
        if (Mucka.Rendering.MacAnimatedView.Of(_view.Handler) is not { } ui) return;
        if (remaining <= TimeSpan.Zero) MacLayerFade.Set(ui, 0f);
        else MacLayerFade.AnimateCurve(ui, samples, remaining.TotalSeconds);
#else
        if (_view.Handler?.PlatformView is not Microsoft.UI.Xaml.UIElement el) return;
        var visual = ElementCompositionPreview.GetElementVisual(el);
        if (remaining <= TimeSpan.Zero)
        {
            visual.StopAnimation("Opacity");
            visual.Opacity = 0f;
            return;
        }
        var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
        fade.Target = "Opacity";
        for (var i = 0; i <= Steps; i++)
            fade.InsertKeyFrame((float)i / Steps, samples[i]);
        fade.Duration = remaining;
        visual.StartAnimation("Opacity", fade);
#endif
    }
#endif
}
