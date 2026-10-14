using Foundation;
using Microsoft.Maui.Platform;
using UIKit;

namespace Mucka;

/// <summary>
/// Escape on a page that is presented over the game, as the Windows pages' PreviewKeyDown handler
/// does. The key command is added to the page's own view controller, so it exists only while that
/// page is the one showing and is looked up before the text field's own handling (priority over
/// system behavior), which makes Escape act even in the middle of editing a field. The game's
/// menu Escape is disabled while a view controller is presented (<see cref="MacHotkeys.IsActive"/>),
/// so the two never both fire. The selector is exported on the app delegate, the last stop of the
/// responder chain, because a key command's action needs a responder to receive it.
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

    public static void Fire() => _current?.OnEscape();
}
