namespace Mucka.Behaviors;

/// <summary>
/// Marks an element so pointer interaction never moves keyboard focus to it — its commands and
/// gesture recognizers still fire, but the command box keeps the keyboard (Invariant #0).
///
/// <b>You almost certainly do not need this.</b> <see cref="FocusGuard"/> covers every element
/// under the game page automatically, by tree position, including lazily created ones — adding a
/// widget requires wiring up nothing at all.
///
/// This remains for one narrow case: an element that can be interacted with in the very first
/// moments of its life, before the guard's hover pre-emption or its next sweep has reached it.
/// Attaching this marks it non-focusable as soon as its platform view exists, which is earlier
/// than anything the guard can do from outside. The Recent who-list rows (built inside a
/// <c>BindableLayout</c> data template, appearing whenever the server sends a who list, and
/// tappable the instant they do) are the case that earned it.
///
/// No-op off Windows.
/// </summary>
public sealed class NoFocusStealBehavior : Behavior<View>
{
    private View? _view;

    protected override void OnAttachedTo(View view)
    {
        base.OnAttachedTo(view);
        _view = view;
#if WINDOWS
        Apply();
        // Platform views can be recreated (virtualization/handler churn) — re-apply each time.
        view.HandlerChanged += OnHandlerChanged;
#endif
    }

    protected override void OnDetachingFrom(View view)
    {
#if WINDOWS
        view.HandlerChanged -= OnHandlerChanged;
#endif
        _view = null;
        base.OnDetachingFrom(view);
    }

#if WINDOWS
    private void OnHandlerChanged(object? sender, EventArgs e) => Apply();

    private void Apply()
    {
        if (_view?.Handler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement fe)
            fe.AllowFocusOnInteraction = false;
    }
#endif
}
