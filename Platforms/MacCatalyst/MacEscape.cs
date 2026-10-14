using Foundation;
using Microsoft.Maui.Platform;
using UIKit;

namespace Mucka;

/// <summary>
/// Escape on a page presented over the game: a UIKeyCommand on the page's view controller, added in
/// OnAppearing and removed in OnDisappearing. Ignored while something is presented over that page.
/// </summary>
internal static class MacEscape
{
    internal const string Action = "muckaEscape:";

    private static Registration? _current;

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
        return _current = new Registration { Controller = controller, Command = command, OnEscape = onEscape };
    }

    /// <summary>Detach in OnDisappearing; safe with null and safe to call twice.</summary>
    public static void Detach(Registration? registration)
    {
        if (registration is null)
            return;
        registration.Controller.RemoveKeyCommand(registration.Command);
        if (ReferenceEquals(_current, registration))
            _current = null;
    }

    public static void Fire()
    {
        if (_current is not { } registration || registration.Controller.PresentedViewController is not null)
            return;
        registration.OnEscape();
    }
}
