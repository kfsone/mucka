#if UIKIT
using Foundation;
using Microsoft.Maui.Platform;
using UIKit;

namespace Mucka;

/// <summary>
/// Escape on a page presented over the game, or a window of its own: a UIKeyCommand on the page's view
/// controller, added in OnAppearing and removed in OnDisappearing. Ignored while something is presented
/// over that page.
/// </summary>
internal static class MacEscape
{
    internal const string Action = "muckaEscape:";

    // Every page showing and closing on Escape, in the order they were attached. A page that is a window of
    // its own is showing at the same time as the page in the main window.
    private static readonly List<Registration> Registrations = [];

    // The page Escape goes to: the one attached last in the key window of the active scene, else the one
    // attached last. Each scene has a key window of its own, so the key window alone is not enough.
    private static Registration? Current
    {
        get
        {
            for (var i = Registrations.Count - 1; i >= 0; i--)
                if (Registrations[i].Controller.View?.Window is { IsKeyWindow: true } window
                    && window.WindowScene?.ActivationState == UISceneActivationState.ForegroundActive)
                    return Registrations[i];
            return Registrations.Count > 0 ? Registrations[^1] : null;
        }
    }

    internal sealed class Registration
    {
        internal required UIViewController Controller { get; init; }
        internal required UIKeyCommand Command { get; init; }
        internal required Action OnEscape { get; init; }
    }

    /// <summary>Attach in OnAppearing; null when the page has no view controller yet.</summary>
    public static Registration? Attach(Page page, Action onEscape)
    {
        if (page.Handler is not IPlatformViewHandler { ViewController: { } controller })
            return null;
        var command = UIKeyCommand.Create(UIKeyCommand.Escape, 0, new ObjCRuntime.Selector(Action));
        command.WantsPriorityOverSystemBehavior = true;
        controller.AddKeyCommand(command);
        var registration = new Registration { Controller = controller, Command = command, OnEscape = onEscape };
        Registrations.Add(registration);
        return registration;
    }

    /// <summary>Detach in OnDisappearing; safe with null and safe to call twice.</summary>
    public static void Detach(Registration? registration)
    {
        if (registration is null)
            return;
        registration.Controller.RemoveKeyCommand(registration.Command);
        Registrations.Remove(registration);
    }

    /// <summary>True while a page that closes on Escape is showing with nothing presented over it.</summary>
    public static bool HasPage => Current is { } r && r.Controller.PresentedViewController is null;

    private static long _lastFireTicks;

    /// <summary>Closes the showing page. Escape can arrive twice for one press - as the page's key
    /// command and as the Game menu's - so a second call within a moment is ignored.</summary>
    public static void Fire()
    {
        if (Current is not { } registration || registration.Controller.PresentedViewController is not null)
            return;
        var now = Environment.TickCount64;
        if (now - _lastFireTicks < 250)
            return;
        _lastFireTicks = now;
        registration.OnEscape();
    }
}
#endif
