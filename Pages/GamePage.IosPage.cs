#if IOS
namespace Mucka.Pages;

// The game page on iOS: the Mac's UIKit command box, with none of the Mac's window rules - the
// window is the screen. The side panel opens beside the terminal without resizing anything, so the
// terminal pane's own size changes are what re-count the columns.
public partial class GamePage
{
    private bool _iosPageHooked;

    private void SetUpIosPage()
    {
        if (_iosPageHooked) TearDownIosPage();
        _iosPageHooked = true;
        Terminal.SizeChanged += OnIosTerminalSizeChanged;
        DockedWhoList.SizeChanged += OnIosWhoListSizeChanged;
        FloatingWhoList.SizeChanged += OnIosWhoListSizeChanged;
        SetUpMacInput();
    }

    private void TearDownIosPage()
    {
        _iosPageHooked = false;
        Terminal.SizeChanged -= OnIosTerminalSizeChanged;
        DockedWhoList.SizeChanged -= OnIosWhoListSizeChanged;
        FloatingWhoList.SizeChanged -= OnIosWhoListSizeChanged;
    }

    private void OnIosTerminalSizeChanged(object? sender, EventArgs e) => RecalculateColumns(Width);

    // The online list was observed empty on the iPhone simulator while the wire log held the
    // server's FEW replies. Each list sits in a ScrollView sized by its content (at most 180); the
    // ScrollView's height is set from the list's own here.
    private void OnIosWhoListSizeChanged(object? sender, EventArgs e)
    {
        var (list, scroll) = ReferenceEquals(sender, DockedWhoList)
            ? (DockedWhoList, DockedWhoScroll)
            : (FloatingWhoList, FloatingWhoScroll);
        if (list.Height >= 0)
            scroll.HeightRequest = Math.Min(list.Height, scroll.MaximumHeightRequest);
    }
}
#endif
