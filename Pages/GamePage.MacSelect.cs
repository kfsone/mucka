#if MACCATALYST
using UIKit;

namespace Mucka.Pages;

// Selecting and copying terminal text with the mouse on a Mac. The Windows build drives
// TerminalView.PointerPress/PointerDrag/PointerRelease from WinUI pointer events; here the same
// calls come from gesture recognizers that accept only the indirect pointer, so the wheel and
// trackpad scroll (a separate recognizer, see GamePage.MacScroll.cs) never start a selection.
public partial class GamePage
{
    private readonly List<UIGestureRecognizer> _macSelectGestures = [];
    private UIView? _macSelectView;
    private bool _macSelectHooked;

    private void SetUpMacSelect()
    {
        if (!_macSelectHooked)
        {
            _macSelectHooked = true;
            Terminal.HandlerChanged += (_, _) => InstallMacSelect();
        }
        InstallMacSelect();
    }

    private void InstallMacSelect()
    {
        foreach (var gesture in _macSelectGestures)
            _macSelectView?.RemoveGestureRecognizer(gesture);
        _macSelectGestures.Clear();
        _macSelectView = Terminal.Handler?.PlatformView as UIView;
        if (_macSelectView is null) return;

        // A press with no minimum duration, so the selection anchors where the button went down
        // rather than where a pan would have recognised; movement is unbounded so the drag keeps
        // reporting however far it goes.
        var drag = new UILongPressGestureRecognizer(OnMacSelectDrag)
        {
            MinimumPressDuration = 0,
            AllowableMovement = 100000,
        };
        var word = new UITapGestureRecognizer(g => OnMacSelectMultiClick(g, lineWise: false)) { NumberOfTapsRequired = 2 };
        var line = new UITapGestureRecognizer(g => OnMacSelectMultiClick(g, lineWise: true)) { NumberOfTapsRequired = 3 };
        // A right click copies the selection, as it does on Windows.
        var copy = new UITapGestureRecognizer(_ =>
        {
            if (Terminal.CopySelectionToClipboard())
                ShowToast("* Copied to clipboard");
            RefocusAfterMacSelect();
        }) { ButtonMaskRequired = UIEventButtonMask.Secondary };

        foreach (UIGestureRecognizer gesture in new UIGestureRecognizer[] { drag, word, line, copy })
        {
            gesture.AllowedTouchTypes = [Foundation.NSNumber.FromInt32((int)UITouchType.IndirectPointer)];
            gesture.CancelsTouchesInView = false;
            gesture.ShouldRecognizeSimultaneously = (_, _) => true;
            if (!ReferenceEquals(gesture, copy))
                gesture.ShouldReceiveEvent = (_, uiEvent) => uiEvent.ButtonMask != UIEventButtonMask.Secondary;
            _macSelectView.AddGestureRecognizer(gesture);
            _macSelectGestures.Add(gesture);
        }
    }

    private void OnMacSelectDrag(UILongPressGestureRecognizer gesture)
    {
        var at = gesture.LocationInView(gesture.View);
        switch (gesture.State)
        {
            case UIGestureRecognizerState.Began:
                // A clickable name inserts its text and is not the start of a selection.
                if (Terminal.TryActivateSpanInsert((float)at.X, (float)at.Y)) return;
                Terminal.PointerPress((float)at.X, (float)at.Y);
                break;
            case UIGestureRecognizerState.Changed:
                Terminal.PointerDrag((float)at.X, (float)at.Y);
                break;
            case UIGestureRecognizerState.Ended:
            case UIGestureRecognizerState.Cancelled:
            case UIGestureRecognizerState.Failed:
                Terminal.PointerRelease();
                RefocusAfterMacSelect();
                break;
        }
    }

    private void OnMacSelectMultiClick(UIGestureRecognizer gesture, bool lineWise)
    {
        if (gesture.State != UIGestureRecognizerState.Ended) return;
        var at = gesture.LocationInView(gesture.View);
        if (lineWise) Terminal.SelectLineAt((float)at.X, (float)at.Y);
        else Terminal.SelectWordAt((float)at.X, (float)at.Y);
        RefocusAfterMacSelect();
    }

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
