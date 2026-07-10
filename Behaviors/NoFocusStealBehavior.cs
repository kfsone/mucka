namespace Mucka.Behaviors;

/// <summary>
/// Marks an element so pointer interaction never moves keyboard focus to it — its commands and
/// gesture recognizers still fire, but the command box keeps the keyboard (Invariant #0).
///
/// This is the attachable twin of <c>GamePage.DisableFocusOnInteraction</c>: that helper takes
/// named, statically-declared elements, but rows created inside a <c>BindableLayout</c> data
/// template (e.g. the Recent who-list) are built lazily and can't be passed to it. The page's
/// <c>SidePanelBorder</c> sets <c>AllowFocusOnInteraction=false</c> for its static children, but
/// that does not reach these late-created, tappable rows — so they must opt in explicitly.
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
