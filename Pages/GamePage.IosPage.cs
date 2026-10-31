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
        _vm.PropertyChanged += OnIosSendMenuStateChanged;
        SendButton.HandlerChanged += OnIosSendButtonHandlerChanged;
        SetIosSendMenu();
    }

    private void TearDownIosPage()
    {
        _iosPageHooked = false;
        Terminal.SizeChanged -= OnIosTerminalSizeChanged;
        DockedWhoList.SizeChanged -= OnIosWhoListSizeChanged;
        FloatingWhoList.SizeChanged -= OnIosWhoListSizeChanged;
        _vm.PropertyChanged -= OnIosSendMenuStateChanged;
        SendButton.HandlerChanged -= OnIosSendButtonHandlerChanged;
    }

    // Send: a tap sends, and holding it offers Chat and Fn keys - the platform button's own menu,
    // which iOS opens on a long press when it is not the button's primary action. A phone's input
    // row has no room for Chat and Fn as buttons; this is the same pair, ticked while on.
    private void SetIosSendMenu()
    {
        if (SendButton.Handler?.PlatformView is not UIKit.UIButton button) return;
        var chat = UIKit.UIAction.Create("Chat", null, null, _ =>
        {
            _vm.ToggleChatModeCommand.Execute(null);
            FocusInput();
        });
        chat.State = _vm.ChatMode ? UIKit.UIMenuElementState.On : UIKit.UIMenuElementState.Off;
        var fkeys = UIKit.UIAction.Create("Fn keys", null, null, _ =>
        {
            _vm.ToggleFkeysCommand.Execute(null);
            FocusInput();
        });
        fkeys.State = _vm.FkeysVisible ? UIKit.UIMenuElementState.On : UIKit.UIMenuElementState.Off;
        button.Menu = UIKit.UIMenu.Create([chat, fkeys]);
        button.ShowsMenuAsPrimaryAction = false;
    }

    private void OnIosSendButtonHandlerChanged(object? sender, EventArgs e) => SetIosSendMenu();

    private void OnIosSendMenuStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Mucka.ViewModels.GameViewModel.ChatMode) or nameof(Mucka.ViewModels.GameViewModel.FkeysVisible))
            SetIosSendMenu();
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
