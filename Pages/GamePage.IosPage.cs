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
        SetUpMacInput();
    }

    private void TearDownIosPage()
    {
        _iosPageHooked = false;
        Terminal.SizeChanged -= OnIosTerminalSizeChanged;
    }

    private void OnIosTerminalSizeChanged(object? sender, EventArgs e) => RecalculateColumns(Width);
}
#endif
