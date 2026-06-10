#if WINDOWS
using Microsoft.UI.Xaml.Hosting;
#endif
using Mucka.ViewModels;

namespace Mucka.Behaviors;

/// <summary>
/// Fades a who-list entry IN when it is added and OUT when it leaves, via WinUI
/// <c>ElementCompositionPreview</c> compositor animations (render/GPU thread — never touches the
/// UI thread or typing).
///
/// Departure is explicit, NOT WinUI's implicit hide animation: MAUI's BindableLayout tears the
/// item out of the native panel on removal before WinUI can play an implicit-hide, so departed
/// entries just vanished. Instead the view-model sets <see cref="WhoEntry.IsDeparting"/> and keeps
/// the entry in the list; we fade it out here, and the view-model removes it once the fade has
/// finished (and cancels both if the player reappears). No-op on non-Windows platforms.
/// </summary>
public sealed class WhoEntryFadeBehavior : Behavior<Label>
{
    private Label? _label;
    private WhoEntry? _entry;
#if WINDOWS
    private Microsoft.UI.Composition.Visual? _visual;
    private const double FadeInMs  = 2000;   // arrival
    private const double FadeOutMs = 3000;   // departure — slower, easier to notice someone left
#endif

    protected override void OnAttachedTo(Label label)
    {
        base.OnAttachedTo(label);
        _label = label;
#if WINDOWS
        label.Loaded += OnLoaded;
        label.Unloaded += OnUnloaded;
        label.BindingContextChanged += OnBindingContextChanged;
#endif
    }

    protected override void OnDetachingFrom(Label label)
    {
#if WINDOWS
        label.Loaded -= OnLoaded;
        label.Unloaded -= OnUnloaded;
        label.BindingContextChanged -= OnBindingContextChanged;
        HookEntry(null);
        _visual = null;
#endif
        _label = null;
        base.OnDetachingFrom(label);
    }

#if WINDOWS
    private void OnLoaded(object? sender, EventArgs e)
    {
        if (_label?.Handler?.PlatformView is not Microsoft.UI.Xaml.UIElement el) return;
        _visual = ElementCompositionPreview.GetElementVisual(el);
        HookEntry(_label.BindingContext as WhoEntry);
        // Fade in from transparent (or stay hidden if this entry is already on its way out).
        FadeTo(_entry?.IsDeparting == true ? 0f : 1f, fromZero: true);
    }

    private void OnUnloaded(object? sender, EventArgs e) => HookEntry(null);

    private void OnBindingContextChanged(object? sender, EventArgs e)
        => HookEntry(_label?.BindingContext as WhoEntry);

    private void HookEntry(WhoEntry? entry)
    {
        if (ReferenceEquals(_entry, entry)) return;
        if (_entry is not null) _entry.PropertyChanged -= OnEntryPropertyChanged;
        _entry = entry;
        if (_entry is not null) _entry.PropertyChanged += OnEntryPropertyChanged;
    }

    private void OnEntryPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WhoEntry.IsDeparting) && _entry is not null)
            FadeTo(_entry.IsDeparting ? 0f : 1f, fromZero: false);   // leaving → fade out; returned → fade back in
    }

    private void FadeTo(float target, bool fromZero)
    {
        if (_visual is null) return;
        var c = _visual.Compositor;
        if (fromZero) _visual.Opacity = 0f;
        var a = c.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(1f, target);
        a.Duration = TimeSpan.FromMilliseconds(target <= 0f ? FadeOutMs : FadeInMs);
        _visual.StartAnimation("Opacity", a);
    }
#endif
}
