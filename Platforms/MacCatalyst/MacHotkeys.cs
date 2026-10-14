using Foundation;
using UIKit;

namespace Mucka;

/// <summary>
/// The game's hotkeys as main-menu key commands. The Mac routes a key equivalent through the menu
/// before the focused text field sees it, so these fire while the command box has the keyboard and
/// plain typing never reaches this code. Control, not Command: this is a MUD client and the
/// operator's keys are Ctrl-based.
///
/// <para>The game page sets <see cref="Handler"/> while it is showing; with none set a key
/// equivalent does nothing.</para>
/// </summary>
internal static class MacHotkeys
{
    /// <summary>Receives a hotkey id (see <see cref="Build"/>) while the game page is showing.</summary>
    public static Action<string>? Handler { get; set; }

    /// <summary>True while the terminal holds a selection that Copy should take.</summary>
    public static Func<bool>? CanCopyTerminal { get; set; }

    /// <summary>Copies the terminal's selection to the pasteboard.</summary>
    public static Action? CopyTerminal { get; set; }

    /// <summary>The 36 Fkey texts of the profile in play; an empty one gets no menu entry.</summary>
    public static Func<string[]>? Fkeys { get; set; }

    internal const string Action = "muckaHotkey:";

    /// <summary>True while the game page is showing and nothing is presented over it. A key command
    /// that is disabled is not consumed, so Esc, the arrows and Ctrl-D/F keep their ordinary meaning
    /// on the profile page, in the settings editor, in prompts and under alerts.</summary>
    public static bool IsActive()
    {
        if (Handler is null)
            return false;
        foreach (var scene in UIApplication.SharedApplication.ConnectedScenes)
        {
            if (scene is UIWindowScene { KeyWindow.RootViewController: { } root }
                && root.PresentedViewController is not null)
                return false;
        }
        return true;
    }

    public static void Fire(UIKeyCommand command)
    {
        if (command.PropertyList is NSString id && Handler is { } handler)
            handler(id.ToString());
    }

    public static UIMenu Build()
    {
        var fkeyItems = new List<UIMenuElement>();
        var texts = Fkeys?.Invoke();
        (string Prefix, string Label, UIKeyModifierFlags Mods)[] banks =
        [
            ("", "F", 0),
            ("s", "Shift+F", UIKeyModifierFlags.Shift),
            ("c", "Ctrl+F", UIKeyModifierFlags.Control),
        ];
        NSString[] fkeys =
        [
            UIKeyCommand.F1, UIKeyCommand.F2, UIKeyCommand.F3, UIKeyCommand.F4, UIKeyCommand.F5, UIKeyCommand.F6,
            UIKeyCommand.F7, UIKeyCommand.F8, UIKeyCommand.F9, UIKeyCommand.F10, UIKeyCommand.F11, UIKeyCommand.F12,
        ];
        foreach (var (prefix, label, mods) in banks)
            for (var n = 0; n < fkeys.Length; n++)
            {
                var slot = (prefix switch { "s" => 12, "c" => 24, _ => 0 }) + n;
                if (texts is null || slot >= texts.Length || string.IsNullOrWhiteSpace(texts[slot]))
                    continue;
                fkeyItems.Add(Key($"{label}{n + 1}", fkeys[n], mods, $"{prefix}f{n + 1}"));
            }

        var game = new List<UIMenuElement>
        {
            Key("Speak Dreamword", "d", UIKeyModifierFlags.Control, "ctrl-d"),
            Key("Speak Dreamword, Then Command", "d", UIKeyModifierFlags.Control | UIKeyModifierFlags.Shift, "ctrl-shift-d"),
            Key("Flee", "f", UIKeyModifierFlags.Control, "ctrl-f"),
            Key("Flee in Typed Direction", "f", UIKeyModifierFlags.Control | UIKeyModifierFlags.Shift, "ctrl-shift-f"),
            Key("Wield Alternate Weapon", "w", UIKeyModifierFlags.Control, "ctrl-w"),
            Key("Ctrl-1", "1", UIKeyModifierFlags.Control, "ctrl-1"),
            Key("Ctrl-2", "2", UIKeyModifierFlags.Control, "ctrl-2"),
            Key("Ctrl-3", "3", UIKeyModifierFlags.Control, "ctrl-3"),
            Key("Reply to Last Tell", "r", UIKeyModifierFlags.Control, "ctrl-r"),
            Key("Scroll Back a Page", UIKeyCommand.PageUp, 0, "pgup"),
            Key("Scroll Forward a Page", UIKeyCommand.PageDown, 0, "pgdn"),
            Key("Previous Command", UIKeyCommand.UpArrow, 0, "history-up"),
            Key("Next Command", UIKeyCommand.DownArrow, 0, "history-down"),
            Key("Clear Input", UIKeyCommand.Escape, 0, "escape"),
            UIMenu.Create("Fkeys", null, UIMenuIdentifier.None, UIMenuOptions.DisplayInline, fkeyItems.ToArray()),
        };
        return UIMenu.Create("Game", null, new NSString("org.kfs.mucka.game"), (UIMenuOptions)0, game.ToArray());
    }

    private static UIKeyCommand Key(string title, string input, UIKeyModifierFlags mods, string id)
        => Key(title, new NSString(input), mods, id);

    private static UIKeyCommand Key(string title, NSString input, UIKeyModifierFlags mods, string id)
    {
        var command = UIKeyCommand.Create(title, null, new ObjCRuntime.Selector(Action), input, mods, new NSString(id));
        // Without this the focused text field's own bindings win: Ctrl+D deletes forward and Ctrl+F
        // moves the caret.
        command.WantsPriorityOverSystemBehavior = true;
        return command;
    }
}
