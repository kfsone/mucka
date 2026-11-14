#if MACCATALYST
namespace Mucka.Pages;

// Selecting and copying terminal text on a Mac. The mouse itself is Rendering/TerminalPointer.cs,
// shared with every pane that shows a terminal; what is the game page's own is here.
public partial class GamePage
{
    // Invariant #0: whatever the gesture did, the command box has the keyboard when it is done.
    private void RefocusAfterMacSelect()
    {
        if (!KeyboardOwnedElsewhere)
            FocusInput();
    }

    // Cmd+C and Edit > Copy reach the app delegate only when the focused text field declines them (it
    // has no selection of its own); see AppDelegate.Copy.
    private void SetUpMacCopy()
    {
        MacHotkeys.CanCopyTerminal = () => Terminal.HasSelection;
        MacHotkeys.CopyTerminal = () =>
        {
            if (Terminal.CopySelectionToClipboard())
                ShowToast("* Copied to clipboard");
        };
    }

    private static void TearDownMacCopy()
    {
        MacHotkeys.CanCopyTerminal = null;
        MacHotkeys.CopyTerminal = null;
    }
}
#endif
