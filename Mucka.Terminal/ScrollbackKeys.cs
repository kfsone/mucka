namespace Mucka.Terminal;

/// <summary>What a key press does while the terminal is in scrollback review.</summary>
public enum ScrollbackKeyAction
{
    /// <summary>Leave it unhandled, so the window's hotkey for it runs - every hotkey works in
    /// scrollback, and each one exits scrollback itself where its action needs the live view.</summary>
    PassToHotkey,
    ScrollToTop,
    ScrollToBottom,
    CopySelection,
    /// <summary>Mark it handled and do nothing: the command box is hidden in scrollback, so there is
    /// nowhere for typing to go.</summary>
    Swallow,
}

/// <summary>
/// The scrollback key table. A key that is a registered window hotkey always passes through to it,
/// so the hotkey table is the one list of hotkeys and a new one works in scrollback without anyone
/// remembering to add it here. Only the keys that exist for scrollback itself are handled here.
/// </summary>
public static class ScrollbackKeys
{
    // Windows virtual-key codes: the values the platform hands the page.
    public const int Home = 0x24;
    public const int End = 0x23;
    public const int Escape = 0x1B;
    public const int C = 0x43;

    /// <param name="virtualKey">The key's platform code.</param>
    /// <param name="control">Whether Ctrl is held.</param>
    /// <param name="isHotkey">Whether this key, with the modifiers held, is a registered window
    /// hotkey.</param>
    public static ScrollbackKeyAction Route(int virtualKey, bool control, bool isHotkey)
    {
        if (isHotkey)
            return ScrollbackKeyAction.PassToHotkey;
        if (control && virtualKey == C)
            return ScrollbackKeyAction.CopySelection;
        return virtualKey switch
        {
            Home => ScrollbackKeyAction.ScrollToTop,
            End or Escape => ScrollbackKeyAction.ScrollToBottom,
            _ => ScrollbackKeyAction.Swallow,
        };
    }
}
