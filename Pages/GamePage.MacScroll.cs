#if MACCATALYST
using CoreGraphics;
using UIKit;

namespace Mucka.Pages;

// Mouse-wheel and trackpad scrolling of the terminal on a Mac. The Windows build hooks the native
// pointer-wheel event; here the scroll events arrive as a pan gesture that takes no touches.
public partial class GamePage
{
    private UIPanGestureRecognizer? _macScrollPan;
    private UIView? _macScrollView;
    private double _macScrollRemainderRows;

    private void SetUpMacScroll()
    {
        Terminal.HandlerChanged += (_, _) => InstallMacScroll();
        InstallMacScroll();
    }

    private void InstallMacScroll()
    {
        if (_macScrollPan is not null)
            _macScrollView?.RemoveGestureRecognizer(_macScrollPan);
        _macScrollPan = null;
        _macScrollView = Terminal.Handler?.PlatformView as UIView;
        if (_macScrollView is null) return;

        _macScrollPan = new UIPanGestureRecognizer(OnMacScroll)
        {
            AllowedScrollTypesMask = UIScrollTypeMask.All,
            AllowedTouchTypes = [],   // wheel and trackpad only; a mouse drag stays the terminal's own
        };
        _macScrollView.AddGestureRecognizer(_macScrollPan);
    }

    private void OnMacScroll(UIPanGestureRecognizer gesture)
    {
        var delta = gesture.TranslationInView(gesture.View).Y;
        gesture.SetTranslation(CGPoint.Empty, gesture.View);

        // Rows of the terminal's own line height, so one notch moves the same text at any font size.
        var rowDp = Terminal.RowHeightDp;
        if (rowDp <= 0) return;
        _macScrollRemainderRows += delta / rowDp;
        var rows = (int)_macScrollRemainderRows;
        if (rows == 0) return;
        _macScrollRemainderRows -= rows;
        Terminal.ScrollByRows(rows);
    }
}
#endif
