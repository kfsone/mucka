using System.Runtime.InteropServices;
using Mucka.ViewModels;

namespace Mucka.Pages;

public partial class GamePage : ContentPage
{
    private readonly GameViewModel _vm;
    private readonly bool _exitOnDisconnect;
    private IDispatcherTimer?      _flushTimer;
    private IDispatcherTimer?      _toastTimer;

#if ANDROID
    // Set while this page is active so MainActivity can route hardware key events.
    private static Action<int>? _androidFkeyHandler;
    private static Action? _androidCtrlDHandler;
    private static Action? _androidCtrlLHandler;

    public static bool TryFireFkeyHandler(int absoluteIndex)
    {
        if (_androidFkeyHandler is null) return false;
        _androidFkeyHandler(absoluteIndex);
        return true;
    }

    public static bool TryFireCtrlD()
    {
        if (_androidCtrlDHandler is null) return false;
        _androidCtrlDHandler();
        return true;
    }

    public static bool TryFireCtrlL()
    {
        if (_androidCtrlLHandler is null) return false;
        _androidCtrlLHandler();
        return true;
    }
#endif

    private bool _isFkeyEditorOpen;
    private bool _eventsSubscribed;
#if WINDOWS
    private Window? _rawConsoleWindow;
    private Microsoft.UI.Xaml.Controls.TextBox? _inputTextBox;
    private Microsoft.UI.Xaml.UIElement? _terminalElement;   // SKXamlCanvas, for wheel scrollback
    private Microsoft.UI.Xaml.UIElement? _fnButtonElement;   // Fn button, for right-tap → settings
    private Microsoft.UI.Xaml.Input.PointerEventHandler? _rootPointerHandler;
    private int _wheelAccum;   // accumulates wheel delta so touchpad drift doesn't trip scrollback
    // ── Window minimum-size enforcement ─────────────────────────────────────
    // Must match the WidthRequest of the side-panel Border in GamePage.xaml.
    private const double SidePanelWidthDp = 260.0;
    // Default terminal-view width (in characters) used to size the window on first appearance.
    // Two columns wider than the 80-column wrap so the rightmost text isn't flush against the panel.
    private const double DefaultViewColumns = 82.0;
    // Left gutter the terminal renderer pads text with — must match TerminalView.LeftPadDip.
    private const double TerminalGutterDp = 4.0;
    // Horizontal window chrome (resize borders) not part of the client area; small fudge so the
    // client area still fits DefaultViewColumns after WinUI subtracts the frame.
    private const double WindowChromeDp = 16.0;

    /// <summary>
    /// Window width (in DIPs) that fits <paramref name="viewColumns"/> terminal columns plus the
    /// renderer's left gutter, the side panel when expanded, and the window frame. Shared by the
    /// app-launch default (<see cref="App.CreateWindow"/>) and the first-appearance resize here.
    /// </summary>
    internal static double PreferredWindowWidthDp(
        double charWidthDp, bool panelExpanded, double viewColumns = DefaultViewColumns)
        => viewColumns * charWidthDp + TerminalGutterDp
         + (panelExpanded ? SidePanelWidthDp : 0.0) + WindowChromeDp;
    private int              _minWindowWidthPx;
    private IntPtr           _hwnd = IntPtr.Zero;
    private WndProcDelegate? _wndProcDelegate;
#endif

    public GamePage(GameViewModel vm, bool exitOnDisconnect = false)
    {
        InitializeComponent();
        _vm = vm;
        _exitOnDisconnect = exitOnDisconnect;
        BindingContext = vm;
    }

    protected override void OnAppearing()
    {
        _isFkeyEditorOpen = false;
        base.OnAppearing();

        try { DeviceDisplay.Current.KeepScreenOn = _vm.KeepScreenOn; }
        catch (Exception ex) { LogCrash("KeepScreenOn", ex); }

        if (_flushTimer == null)
        {
            // Unsubscribe before subscribing to guard against any double-subscribe scenario.
            if (!_eventsSubscribed)
            {
                _vm.Disconnected        += OnDisconnected;
                _vm.RequestFocus        += FocusInput;
                _vm.SidePanel.RequestFocus += FocusInput;
                _vm.ConfigRequested     += OnConfigRequested;
                _vm.ClearScreenRequested += OnClearScreenRequested;
                _vm.SettingsSaved       += OnSettingsSaved;
                _vm.PropertyChanged     += OnVmPropertyChanged;
                Terminal.HistoryModeChanged += OnHistoryModeChanged;
                Terminal.FocusInputRequested += OnFocusInputRequested;
                _eventsSubscribed = true;
            }

            Terminal.SetFontSize(_vm.FontSize);
            Terminal.Columns = _vm.EffCols;

            _flushTimer = Dispatcher.CreateTimer();
            _flushTimer.Interval = TimeSpan.FromMilliseconds(50);
            _flushTimer.Tick += OnFlushTick;
            _flushTimer.Start();
            _vm.SidePanel.InitializeFadeTimer(Dispatcher);

            if (Window is not null)
                Window.Activated += OnWindowActivated;

#if WINDOWS
            try
            {
                // Hook window root for F1-F12 physical key events (fires regardless of focus)
                // and to keep keyboard focus pinned to the input box (GettingFocus bounce).
                if (Window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window fwin &&
                    fwin.Content is Microsoft.UI.Xaml.UIElement froot)
                {
                    froot.PreviewKeyDown += OnRootPreviewKeyDown;
                    froot.GettingFocus += OnRootGettingFocus;
                    froot.LosingFocus += OnRootLosingFocus;
                    // Catch-all: ANY click on the game page hands focus back to the input box
                    // (handledEventsToo so gesture-handled taps — chips, toggles — still count).
                    // Skipped in scrollback / while the config editor is open.
                    _rootPointerHandler = new Microsoft.UI.Xaml.Input.PointerEventHandler(OnRootPointerReleased);
                    froot.AddHandler(Microsoft.UI.Xaml.UIElement.PointerReleasedEvent, _rootPointerHandler, handledEventsToo: true);
                }
                // Chrome never takes pointer focus: clicks on these still fire their
                // commands/gestures, but keyboard focus stays wherever it was (the input box).
                // AllowFocusOnInteraction propagates to children, covering the chips, icons,
                // and fkey buttons. The input row's Entry is deliberately NOT covered.
                DisableFocusOnInteraction(StatusBar, SidePanelBorder, FkeyBar, FnButton, SendButton, ScrollbackBar);
#if FOCUS_DIAG
                Microsoft.UI.Xaml.Input.FocusManager.GettingFocus += OnFmGettingFocus;
                Microsoft.UI.Xaml.Input.FocusManager.LosingFocus += OnFmLosingFocus;
                Microsoft.UI.Xaml.Input.FocusManager.GotFocus += OnFmGotFocus;
                Microsoft.UI.Xaml.Input.FocusManager.LostFocus += OnFmLostFocus;
                InputEntry.Focused += (_, _) => FocusDiag("maui.Entry Focused");
                InputEntry.Unfocused += (_, _) => FocusDiag("maui.Entry Unfocused");
#endif
                // Hook the native TextBox so Up/Down/Esc keys work in the entry.
                InputEntry.HandlerChanged += OnInputHandlerChanged;
                // Right-click on Fn opens the settings page (left-click toggles the fkey bar).
                // Apply now and on handler change, since platform views can be recreated.
                FnButton.HandlerChanged += OnFnButtonHandlerChanged;
                OnFnButtonHandlerChanged(FnButton, EventArgs.Empty);
                // Hook the terminal canvas for mouse-wheel scrollback.
                Terminal.HandlerChanged += OnTerminalHandlerChanged;
                _vm.OpenRawConsoleRequested += OnOpenRawConsoleRequested;
                // Enforce minimum window width based on the configured terminal columns.
                _vm.SidePanel.PropertyChanged += OnSidePanelPropertyChanged;
                SetupWindowMinimumSize();
                // Size the window once so the terminal view fits ~82 columns + the side panel,
                // rather than inheriting WinUI's oversized default window width.
                SetPreferredInitialWindowSize();
            }
            catch (Exception ex) { LogCrash("OnAppearing/Windows", ex); }
#endif
        }
        else
        {
            // Returning from FkeyEditor: events and platform hooks are still active; just resume the timer.
            _flushTimer.Start();
        }

        FocusInput();

#if ANDROID
        _androidFkeyHandler = _vm.SendFkeyAbsolute;
        _androidCtrlDHandler = _vm.SpeakDreamword;
        _androidCtrlLHandler = _vm.ClearScreen;
#endif
    }

    protected override void OnNavigatedTo(NavigatedToEventArgs args)
    {
        base.OnNavigatedTo(args);
        // Navigation is complete at this point — the shell Back button has given up focus,
        // so focusing the entry here wins reliably.
        FocusInput();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
#if ANDROID
        _androidFkeyHandler = null;
        _androidCtrlDHandler = null;
        _androidCtrlLHandler = null;
#endif
        if (_isFkeyEditorOpen)
        {
            // Pause the timer while the modal is open. Keep it non-null so OnAppearing
            // knows not to reinitialize the terminal or re-hook events on return.
            _flushTimer?.Stop();
            return;
        }

        DeviceDisplay.Current.KeepScreenOn = false;
        _flushTimer?.Stop();
        _flushTimer = null;
        _toastTimer?.Stop();
        _vm.Disconnected        -= OnDisconnected;
        _vm.RequestFocus        -= FocusInput;
        _vm.SidePanel.RequestFocus -= FocusInput;
        _vm.ConfigRequested     -= OnConfigRequested;
        _vm.ClearScreenRequested -= OnClearScreenRequested;
        _vm.SettingsSaved       -= OnSettingsSaved;
        _vm.PropertyChanged     -= OnVmPropertyChanged;
        Terminal.HistoryModeChanged -= OnHistoryModeChanged;
        Terminal.FocusInputRequested -= OnFocusInputRequested;
        _eventsSubscribed = false;
        if (Window is not null)
            Window.Activated -= OnWindowActivated;
#if WINDOWS
        if (Window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window fwin &&
            fwin.Content is Microsoft.UI.Xaml.UIElement froot)
        {
            froot.PreviewKeyDown -= OnRootPreviewKeyDown;
            froot.GettingFocus -= OnRootGettingFocus;
            froot.LosingFocus -= OnRootLosingFocus;
            if (_rootPointerHandler is not null)
            {
                froot.RemoveHandler(Microsoft.UI.Xaml.UIElement.PointerReleasedEvent, _rootPointerHandler);
                _rootPointerHandler = null;
            }
        }
#if FOCUS_DIAG
        Microsoft.UI.Xaml.Input.FocusManager.GettingFocus -= OnFmGettingFocus;
        Microsoft.UI.Xaml.Input.FocusManager.LosingFocus -= OnFmLosingFocus;
        Microsoft.UI.Xaml.Input.FocusManager.GotFocus -= OnFmGotFocus;
        Microsoft.UI.Xaml.Input.FocusManager.LostFocus -= OnFmLostFocus;
#endif
        if (_inputTextBox != null)
        {
            _inputTextBox.PreviewKeyDown -= OnInputPreviewKeyDown;
            _inputTextBox = null;
        }
        InputEntry.HandlerChanged -= OnInputHandlerChanged;
        FnButton.HandlerChanged -= OnFnButtonHandlerChanged;
        if (_fnButtonElement != null)
        {
            _fnButtonElement.RightTapped -= OnFnButtonRightTapped;
            _fnButtonElement = null;
        }
        Terminal.HandlerChanged -= OnTerminalHandlerChanged;
        if (_terminalElement != null)
        {
            _terminalElement.PointerWheelChanged -= OnTerminalPointerWheel;
            _terminalElement.PointerPressed  -= OnTerminalPointerPressed;
            _terminalElement.PointerMoved    -= OnTerminalPointerMoved;
            _terminalElement.PointerReleased -= OnTerminalPointerReleased;
            _terminalElement = null;
        }
        _vm.OpenRawConsoleRequested -= OnOpenRawConsoleRequested;
        _vm.SidePanel.PropertyChanged -= OnSidePanelPropertyChanged;
        TeardownWindowMinimumSize();
#endif
        _ = _vm.DisposeAsync();
    }

    private void OnFlushTick(object? sender, EventArgs e) => DoFlushWork();

    private void DoFlushWork()
    {
        _vm.AntiIdleTick();

        // Drain the ViewModel queue straight into the Skia terminal. The partial/complete/merge/
        // clear semantics live in TerminalBuffer (inside TerminalView); a paint of one screenful
        // is sub-millisecond, so there is no need to defer this off the keyboard's priority lane.
        var newLines = _vm.FlushPendingLines();
        if (newLines is { Count: > 0 })
            Terminal.AppendLines(newLines);
    }

    // Character width in MAUI logical pixels — delegates to the view-model so both
    // the column-count calculation and the minimum-width enforcement use one formula.
    private double CharWidthDp => _vm.CharWidthDp;

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        if (width <= 0) return;
        var displayableCols = (int)Math.Floor(width / CharWidthDp);
        _vm.NotifyWindowSize(width, displayableCols);
        // Keep the terminal's wrap width in sync with the negotiated column count.
        Terminal.Columns = _vm.EffCols;
    }

    private Task OpenConfigAsync(int initialTab)
    {
        Func<Mucka.Core.ClientSettings, string[], Task>? onSave = _vm.CanSaveSettings
            ? (settings, fkeys) => _vm.SaveSettingsAsync(settings, fkeys)
            : null;
        var editorVm = new FkeyEditorViewModel(
            _vm.GetAllFkeys(),
            _vm.CurrentSettings,
            _vm.ApplyClientSettings,
            onSave)
        {
            ActiveTab = initialTab
        };
        _isFkeyEditorOpen = true;
        return Navigation.PushModalAsync(new FkeyEditorPage(editorVm));
    }

    private async void OnConfigRequested() => await OpenConfigAsync(initialTab: 0);

    // Deferred a dispatcher tick: when invoked from a tap/click handler, WinUI settles pointer
    // focus on the clicked control AFTER the handler returns, which would clobber an immediate
    // Focus() call. Posting the focus wins that race.
    private void FocusInput() => Dispatcher.Dispatch(() =>
    {
#if WINDOWS
        FocusDiag($"FocusInput dispatch: IsFocused={InputEntry.IsFocused}");
        // Focus via the platform view: MAUI's IsFocused can be stale mid-click, and platform
        // Focus() on an already-focused control is a no-op, so unconditional is safe.
        _inputTextBox?.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
#else
        if (!InputEntry.IsFocused) InputEntry.Focus();
#endif
    });

    // On window activation, record the moment (so a click that activated the app focuses the input
    // box rather than entering scrollback) and re-focus the typing box when not in scrollback.
    private void OnWindowActivated(object? sender, EventArgs e)
    {
        Terminal.NotifyWindowActivated();
        if (!Terminal.IsHistoryMode) FocusInput();
    }

    private void OnClearScreenRequested() => Terminal.Clear();

    // Reacts to settings applied from the config dialog: font size changes re-style the
    // terminal; column changes re-fit the window (Windows) and re-wrap the view. The window
    // re-fit hangs off MaxColumns only: ApplyClientSettings raises it (unconditionally) after
    // FontSize, so one resize sees both new values instead of resizing twice.
    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GameViewModel.FontSize))
        {
            Terminal.SetFontSize(_vm.FontSize);
        }
        else if (e.PropertyName == nameof(GameViewModel.MaxColumns))
        {
            Terminal.Columns = _vm.EffCols;
#if WINDOWS
            UpdateWindowMinimumWidth();
            ResizeWindowToFitColumns();
#endif
        }
    }

    private void OnSettingsSaved() => ShowToast("* Settings saved");

    /// <summary>Closes the About overlay if it is open. Returns true when it was.</summary>
    private bool TryCloseAbout()
    {
        if (!_vm.SidePanel.IsAboutVisible) return false;
        _vm.SidePanel.CloseAboutCommand.Execute(null);
        return true;
    }

    // Android hardware/gesture back: dismiss the About overlay before leaving the page.
    protected override bool OnBackButtonPressed()
        => TryCloseAbout() || base.OnBackButtonPressed();

    // Scrollback is an explicit mode: swap the input row for the yellow "SCROLLBACK" indicator,
    // and restore + re-focus the input box on return to live.
    private void OnHistoryModeChanged(object? sender, EventArgs e)
    {
        // Overlay the indicator ON TOP of the (still-present) input controls rather than hiding
        // them, so the input row's measured height never changes — the text view above must not
        // reflow when entering/leaving scrollback.
        ScrollbackBar.IsVisible = Terminal.IsHistoryMode;
        if (!Terminal.IsHistoryMode) FocusInput();
    }

    // An activation click (the click that brought the app to the foreground) just focuses input.
    private void OnFocusInputRequested(object? sender, EventArgs e) => FocusInput();

    // Tapping the SCROLLBACK indicator returns to live.
    private void OnScrollbackBarTapped(object? sender, TappedEventArgs e) => Terminal.ScrollToBottom();

    private void ShowCopiedToast() => ShowToast("* Copied to clipboard");

    // Briefly flash a confirmation toast (3.3s); re-arms on each call.
    private void ShowToast(string message)
    {
        ToastLabel.Text = message;
        CopiedToast.IsVisible = true;
        if (_toastTimer is null)
        {
            _toastTimer = Dispatcher.CreateTimer();
            _toastTimer.Interval = TimeSpan.FromSeconds(3.3);
            _toastTimer.IsRepeating = false;
            _toastTimer.Tick += (_, _) => CopiedToast.IsVisible = false;
        }
        _toastTimer.Stop();
        _toastTimer.Start();
    }

#if WINDOWS
    // ── Win32 types and imports for minimum-window-size enforcement ──────────

    private delegate IntPtr WndProcDelegate(
        IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinPoint { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public WinPoint ptReserved;
        public WinPoint ptMaxSize;
        public WinPoint ptMaxPosition;
        public WinPoint ptMinTrackSize;
        public WinPoint ptMaxTrackSize;
    }

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hwnd, WndProcDelegate pfnSubclass, IntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(IntPtr hwnd, WndProcDelegate pfnSubclass, IntPtr uIdSubclass);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    // ── Window minimum-size methods ──────────────────────────────────────────

    /// <summary>
    /// Attaches a Win32 window subclass on first call so that WM_GETMINMAXINFO can be
    /// intercepted to enforce the minimum window width, then applies the initial constraint.
    /// Safe to call multiple times — the subclass is only registered once.
    /// </summary>
    private void SetupWindowMinimumSize()
    {
        if (_hwnd != IntPtr.Zero) return; // already set up
        var nativeWindow = Window?.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
        if (nativeWindow is null) return;
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow);
        if (_hwnd == IntPtr.Zero) return;
        _wndProcDelegate = GameWindowSubclassProc;
        if (!SetWindowSubclass(_hwnd, _wndProcDelegate, IntPtr.Zero, IntPtr.Zero))
        {
            // Subclass registration failed — minimum-size enforcement unavailable.
            _hwnd = IntPtr.Zero;
            _wndProcDelegate = null;
            return;
        }
        UpdateWindowMinimumWidth();
    }

    /// <summary>
    /// Recomputes the required minimum window width (in physical pixels) and, if the window
    /// is currently narrower, resizes it to the new minimum.
    /// </summary>
    private void UpdateWindowMinimumWidth()
    {
        if (_hwnd == IntPtr.Zero) return;
        var nativeWindow = Window?.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
        if (nativeWindow is null) return;

        var panelExpanded = _vm.SidePanel.IsPanelExpanded;
        var minDp  = PreferredWindowWidthDp(CharWidthDp, panelExpanded, _vm.MaxColumns);
        var dpi    = GetDpiForWindow(_hwnd);
        _minWindowWidthPx = (int)Math.Ceiling(minDp * dpi / 96.0);

        // Resize now if the window is already narrower than the new minimum.
        var appWindow = nativeWindow.AppWindow;
        if (appWindow.Size.Width < _minWindowWidthPx)
            appWindow.Resize(new Windows.Graphics.SizeInt32(_minWindowWidthPx, appWindow.Size.Height));
    }

    /// <summary>
    /// Sizes the window once (on first GamePage appearance) so the terminal view fits
    /// <see cref="DefaultViewColumns"/> characters plus the side panel when expanded, instead
    /// of inheriting WinUI's oversized default window width. Height is left untouched.
    /// Subsequent appearances (e.g. returning from the config editor) do not re-run this, so a
    /// user's manual resize is preserved.
    /// </summary>
    private void SetPreferredInitialWindowSize()
    {
        if (_hwnd == IntPtr.Zero) return;
        var nativeWindow = Window?.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
        if (nativeWindow is null) return;

        var panelExpanded = _vm.SidePanel.IsPanelExpanded;
        var contentDp = PreferredWindowWidthDp(CharWidthDp, panelExpanded);
        var dpi       = GetDpiForWindow(_hwnd);
        var targetPx  = (int)Math.Ceiling(contentDp * dpi / 96.0);

        // Never set the window narrower than the enforced minimum.
        if (targetPx < _minWindowWidthPx) targetPx = _minWindowWidthPx;

        var appWindow = nativeWindow.AppWindow;
        appWindow.Resize(new Windows.Graphics.SizeInt32(targetPx, appWindow.Size.Height));
    }

    /// <summary>
    /// Snaps the window width to fit the configured column count (+2 breathing columns, the
    /// same margin as the launch default) after a settings change to columns or font size —
    /// without this the view stays clamped to whatever the old window width could display.
    /// Height is left untouched.
    /// </summary>
    private void ResizeWindowToFitColumns()
    {
        if (_hwnd == IntPtr.Zero) return;
        var nativeWindow = Window?.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
        if (nativeWindow is null) return;

        var panelExpanded = _vm.SidePanel.IsPanelExpanded;
        var contentDp = PreferredWindowWidthDp(CharWidthDp, panelExpanded, _vm.MaxColumns + 2.0);
        var dpi       = GetDpiForWindow(_hwnd);
        var targetPx  = (int)Math.Ceiling(contentDp * dpi / 96.0);
        if (targetPx < _minWindowWidthPx) targetPx = _minWindowWidthPx;

        var appWindow = nativeWindow.AppWindow;
        if (appWindow.Size.Width != targetPx)
            appWindow.Resize(new Windows.Graphics.SizeInt32(targetPx, appWindow.Size.Height));
    }

    /// <summary>Removes the Win32 window subclass registered by SetupWindowMinimumSize.</summary>
    private void TeardownWindowMinimumSize()
    {
        if (_hwnd != IntPtr.Zero && _wndProcDelegate is not null)
        {
            RemoveWindowSubclass(_hwnd, _wndProcDelegate, IntPtr.Zero);
            _hwnd           = IntPtr.Zero;
            _wndProcDelegate = null;
        }
    }

    /// <summary>
    /// Win32 subclass procedure: intercepts WM_GETMINMAXINFO to prevent the user from
    /// resizing the window narrower than the configured terminal width (plus side panel
    /// when it is open).
    /// </summary>
    private IntPtr GameWindowSubclassProc(
        IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData)
    {
        const uint WM_GETMINMAXINFO = 0x0024;
        if (msg == WM_GETMINMAXINFO && lParam != IntPtr.Zero && _minWindowWidthPx > 0)
        {
            var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            if (info.ptMinTrackSize.x < _minWindowWidthPx)
            {
                info.ptMinTrackSize.x = _minWindowWidthPx;
                Marshal.StructureToPtr(info, lParam, false);
            }
        }
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>
    /// Responds to SidePanelViewModel property changes: updates the minimum window width
    /// (and resizes if needed) whenever the panel is expanded or collapsed.
    /// </summary>
    private void OnSidePanelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SidePanelViewModel.IsPanelExpanded))
            UpdateWindowMinimumWidth();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    private static bool IsModifierKey(Windows.System.VirtualKey k) => k is
        Windows.System.VirtualKey.Control or Windows.System.VirtualKey.LeftControl or Windows.System.VirtualKey.RightControl or
        Windows.System.VirtualKey.Shift   or Windows.System.VirtualKey.LeftShift   or Windows.System.VirtualKey.RightShift   or
        Windows.System.VirtualKey.Menu    or Windows.System.VirtualKey.LeftMenu    or Windows.System.VirtualKey.RightMenu    or
        Windows.System.VirtualKey.LeftWindows or Windows.System.VirtualKey.RightWindows or Windows.System.VirtualKey.CapitalLock;

    // ── Focus diagnostics — compiled out unless the FOCUS_DIAG symbol is defined
    // (add FOCUS_DIAG to DefineConstants in Mucka.csproj to enable). Logs every focus
    // transition (app-wide FocusManager events + our veto/redirect decisions) to
    // %TEMP%\mucka-focus.txt.
    [System.Diagnostics.Conditional("FOCUS_DIAG")]
    private static void FocusDiag(string msg)
    {
        try
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mucka-focus.txt");
            System.IO.File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff}  {msg}\n");
        }
        catch { /* diagnostics only */ }
    }

    // Referenced only from FocusDiag call arguments; must stay compiled for those to parse.
    private string FocusDesc(object? o) => o switch
    {
        null => "(null)",
        Microsoft.UI.Xaml.Controls.TextBox tb when ReferenceEquals(tb, _inputTextBox) => "INPUT",
        Microsoft.UI.Xaml.FrameworkElement fe => $"{fe.GetType().Name}'{fe.Name}'",
        _ => o.GetType().Name,
    };

#if FOCUS_DIAG
    private void OnFmGettingFocus(object? sender, Microsoft.UI.Xaml.Input.GettingFocusEventArgs e) =>
        FocusDiag($"FM.GettingFocus  old={FocusDesc(e.OldFocusedElement)} new={FocusDesc(e.NewFocusedElement)} state={e.FocusState} dir={e.Direction}");
    private void OnFmLosingFocus(object? sender, Microsoft.UI.Xaml.Input.LosingFocusEventArgs e) =>
        FocusDiag($"FM.LosingFocus   old={FocusDesc(e.OldFocusedElement)} new={FocusDesc(e.NewFocusedElement)} state={e.FocusState}");
    private void OnFmGotFocus(object? sender, Microsoft.UI.Xaml.Input.FocusManagerGotFocusEventArgs e) =>
        FocusDiag($"FM.GotFocus      new={FocusDesc(e.NewFocusedElement)}");
    private void OnFmLostFocus(object? sender, Microsoft.UI.Xaml.Input.FocusManagerLostFocusEventArgs e) =>
        FocusDiag($"FM.LostFocus     old={FocusDesc(e.OldFocusedElement)}");
#endif

    /// <summary>
    /// Marks chrome elements so pointer interaction never moves keyboard focus to them
    /// (commands and gestures still fire). Applied via the platform view; re-applied on
    /// handler changes since platform views can be recreated.
    /// </summary>
    private static void DisableFocusOnInteraction(params VisualElement[] elements)
    {
        foreach (var el in elements)
        {
            ApplyNoFocusOnInteraction(el);
            el.HandlerChanged += (s, _) => ApplyNoFocusOnInteraction((VisualElement)s!);
        }
    }

    private static void ApplyNoFocusOnInteraction(VisualElement el)
    {
        if (el.Handler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement fe)
            fe.AllowFocusOnInteraction = false;
    }

    // Any stray click on the game page lands focus back on the input box. Registered with
    // handledEventsToo so taps consumed by gesture recognizers (chips, toggles) still count.
    // Scrollback clicks are exempt (the terminal owns interaction there), as is the config
    // editor (modal page over the same window root).
    private void OnRootPointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_isFkeyEditorOpen || Terminal.IsHistoryMode) return;
        FocusDiag("root.PointerReleased → FocusInput");
        FocusInput();
    }

    // Keyboard belongs to the input box on the game page. If focus heads anywhere else — a panel
    // toggle, the gear, the rec/dreamword chips — keep it on the input box so typing is never
    // stranded. Skipped while reviewing scrollback (input is hidden) or while the config editor
    // is open. While the input box holds focus, veto the move outright (TryCancel) so a click
    // never steals focus even for a frame; otherwise redirect the incoming focus to the input.
    private void OnRootGettingFocus(Microsoft.UI.Xaml.UIElement sender, Microsoft.UI.Xaml.Input.GettingFocusEventArgs args)
    {
        if (_isFkeyEditorOpen || Terminal.IsHistoryMode || _inputTextBox is null)
        {
            FocusDiag($"root.GettingFocus SKIP (editor={_isFkeyEditorOpen} history={Terminal.IsHistoryMode} tb={_inputTextBox is not null})");
            return;
        }
        if (ReferenceEquals(args.NewFocusedElement, _inputTextBox)) return;
        if (ReferenceEquals(args.OldFocusedElement, _inputTextBox))
        {
            bool cancelled = args.TryCancel();
            FocusDiag($"root.GettingFocus VETO old=INPUT new={FocusDesc(args.NewFocusedElement)} cancelled={cancelled}");
            if (cancelled) return;
        }
        bool redirected = args.TrySetNewFocusedElement(_inputTextBox);
        FocusDiag($"root.GettingFocus REDIRECT old={FocusDesc(args.OldFocusedElement)} new={FocusDesc(args.NewFocusedElement)} redirected={redirected}");
    }

    // Companion to the GettingFocus veto: catches focus leaving the input box for a target the
    // GettingFocus bounce never sees (e.g. a non-XAML element or "nothing"), which is what a
    // click on the rec/dreamword chips produces.
    private void OnRootLosingFocus(Microsoft.UI.Xaml.UIElement sender, Microsoft.UI.Xaml.Input.LosingFocusEventArgs args)
    {
        if (_isFkeyEditorOpen || Terminal.IsHistoryMode || _inputTextBox is null) return;
        if (!ReferenceEquals(args.OldFocusedElement, _inputTextBox)) return;
        if (args.NewFocusedElement is null)
        {
            bool cancelled = args.TryCancel();
            FocusDiag($"root.LosingFocus VETO new=(null) cancelled={cancelled}");
        }
    }

    private void OnRootPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (_isFkeyEditorOpen) return;
        var key = e.Key;
        bool ctrl  = (GetKeyState((int)Windows.System.VirtualKey.Control) & 0x8000) != 0;
        bool shift = (GetKeyState((int)Windows.System.VirtualKey.Shift)   & 0x8000) != 0;

        // The About overlay sits over everything — Escape dismisses it first.
        if (key == Windows.System.VirtualKey.Escape && TryCloseAbout())
        {
            e.Handled = true;
            return;
        }

        // ── Scrollback navigation ────────────────────────────────────────────
        // PageUp/PageDown scroll history (PageUp enters it from live). While reviewing
        // history the input buffer is blocked: handle scroll/exit keys and swallow the rest.
        if (key == Windows.System.VirtualKey.PageUp)   { Terminal.ScrollByPages(1);  e.Handled = true; return; }
        if (key == Windows.System.VirtualKey.PageDown) { Terminal.ScrollByPages(-1); e.Handled = true; return; }
        if (Terminal.IsHistoryMode)
        {
            // Ctrl+C copies the selection without leaving scrollback.
            if (ctrl && key == Windows.System.VirtualKey.C)
            {
                if (Terminal.CopySelectionToClipboard()) ShowCopiedToast();
                e.Handled = true;
                return;
            }
            // Ctrl+D escapes scrollback and speaks the dreamword — dreamwords are
            // time-critical, so it must work mid-review.
            if (ctrl && key == Windows.System.VirtualKey.D)
            {
                Terminal.ScrollToBottom();
                _vm.SpeakDreamword();
                e.Handled = true;
                return;
            }
            switch (key)
            {
                case Windows.System.VirtualKey.Home:
                    Terminal.ScrollToTop();    e.Handled = true; return;
                case Windows.System.VirtualKey.End:
                case Windows.System.VirtualKey.Escape:
                    Terminal.ScrollToBottom(); e.Handled = true; return;
            }
            if (IsModifierKey(key)) return;   // lone modifiers pass through harmlessly
            e.Handled = true;                 // swallow all other keys — input box is hidden in scrollback
            return;
        }

        if (ctrl && key == Windows.System.VirtualKey.D)
        {
            _vm.SpeakDreamword();
            e.Handled = true;
            return;
        }
        if (ctrl && key == Windows.System.VirtualKey.L)
        {
            _vm.ClearScreen();
            e.Handled = true;
            return;
        }
        if (ctrl && (int)key == 0xC0)  // Ctrl+` (OEM_3 / backtick)
        {
            _ = TakeSelfieAsync();
            e.Handled = true;
            return;
        }

        if (key < Windows.System.VirtualKey.F1 || key > Windows.System.VirtualKey.F12)
            return;

        int fkeyNum = (int)key - (int)Windows.System.VirtualKey.F1; // 0-11
        int absoluteIndex = ctrl ? 24 + fkeyNum : shift ? 12 + fkeyNum : fkeyNum;

        _vm.SendFkeyAbsolute(absoluteIndex);
        e.Handled = true;
    }

    private void OnInputHandlerChanged(object? sender, EventArgs e)
    {
        if (_inputTextBox != null)
        {
            _inputTextBox.PreviewKeyDown -= OnInputPreviewKeyDown;
            _inputTextBox = null;
        }
        if (InputEntry.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.TextBox tb)
        {
            _inputTextBox = tb;
            tb.PreviewKeyDown += OnInputPreviewKeyDown;
            // Shadow the ReturnCommand with a local null so MAUI's KeyDown handler
            // (registered with handledEventsToo:true) sees null and skips execution.
            // Do NOT use RemoveBinding — that fires PropertyChanged which causes MAUI's
            // binding infrastructure to re-evaluate and re-apply the XAML binding,
            // restoring ReturnCommand=SendCommand and producing a second send.
            // A plain null SetValue shadows the live binding without disturbing it.
            InputEntry.ReturnCommand = null;
        }
    }

    // Right-click (mouse) on the Fn button opens the settings page; left-click still
    // toggles the fkey bar via the bound command (WinUI Click only fires for the
    // primary button, so the two don't overlap).
    private void OnFnButtonHandlerChanged(object? sender, EventArgs e)
    {
        if (_fnButtonElement != null)
        {
            _fnButtonElement.RightTapped -= OnFnButtonRightTapped;
            _fnButtonElement = null;
        }
        if (FnButton.Handler?.PlatformView is Microsoft.UI.Xaml.UIElement el)
        {
            _fnButtonElement = el;
            el.RightTapped += OnFnButtonRightTapped;
        }
    }

    private async void OnFnButtonRightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (_isFkeyEditorOpen) return;
        await OpenConfigAsync(initialTab: 1);   // straight to the Hotkeys tab

    }

    private void OnTerminalHandlerChanged(object? sender, EventArgs e)
    {
        if (_terminalElement != null)
        {
            _terminalElement.PointerWheelChanged -= OnTerminalPointerWheel;
            _terminalElement.PointerPressed  -= OnTerminalPointerPressed;
            _terminalElement.PointerMoved    -= OnTerminalPointerMoved;
            _terminalElement.PointerReleased -= OnTerminalPointerReleased;
            _terminalElement = null;
        }
        if (Terminal.Handler?.PlatformView is Microsoft.UI.Xaml.UIElement el)
        {
            _terminalElement = el;
            el.PointerWheelChanged += OnTerminalPointerWheel;
            // Drive mouse selection / click-to-enter from native pointer events (reliable on
            // Windows). Turn off SkiaSharp's own pointer→Touch handling so the two don't fight;
            // touch-pan via OnTouch is only needed on Android, where this hook never runs.
            Terminal.EnableTouchEvents = false;
            el.PointerPressed  += OnTerminalPointerPressed;
            el.PointerMoved    += OnTerminalPointerMoved;
            el.PointerReleased += OnTerminalPointerReleased;
            // Drag-selecting in the terminal must not steal keyboard focus from the input box.
            if (el is Microsoft.UI.Xaml.FrameworkElement fe)
                fe.AllowFocusOnInteraction = false;
            // WinUI elements with a NULL Background are not hit-test-visible, so pointer/wheel
            // events never fire over the canvas (Skia still paints it — hence "see text, can't
            // click"). A Transparent brush IS hit-testable; the canvas paints its own background.
            var hitBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            switch (el)
            {
                case Microsoft.UI.Xaml.Controls.Panel p when p.Background is null:   p.Background = hitBrush; break;
                case Microsoft.UI.Xaml.Controls.Control c when c.Background is null: c.Background = hitBrush; break;
            }
        }
    }

    private void OnTerminalPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var el = (Microsoft.UI.Xaml.UIElement)sender;
        var pt = e.GetCurrentPoint(el);
        if (pt.Properties.IsRightButtonPressed)           // right-click copies the selection
        {
            if (Terminal.CopySelectionToClipboard()) ShowCopiedToast();
            e.Handled = true;
            return;
        }
        if (!pt.Properties.IsLeftButtonPressed) return;   // selection / entry is the left button only
        el.CapturePointer(e.Pointer);                     // keep getting moves if the cursor leaves the pane
        Terminal.PointerPress((float)pt.Position.X, (float)pt.Position.Y);
        e.Handled = true;
    }

    private void OnTerminalPointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var el = (Microsoft.UI.Xaml.UIElement)sender;
        var pt = e.GetCurrentPoint(el);
        Terminal.PointerDrag((float)pt.Position.X, (float)pt.Position.Y);   // no-op unless a drag is active
    }

    private void OnTerminalPointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var el = (Microsoft.UI.Xaml.UIElement)sender;
        el.ReleasePointerCapture(e.Pointer);
        Terminal.PointerRelease();
        e.Handled = true;
    }

    private void OnTerminalPointerWheel(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        int delta = e.GetCurrentPoint((Microsoft.UI.Xaml.UIElement)sender).Properties.MouseWheelDelta;
        if (delta == 0) return;
        e.Handled = true;

        // Accumulate and only act on whole wheel notches (120). A mouse wheel sends one full
        // notch per click; a touchpad sends many tiny deltas — accumulating means it takes a
        // deliberate scroll to enter scrollback, rather than incidental drift.
        _wheelAccum += delta;
        int notches = _wheelAccum / 120;
        if (notches == 0) return;
        _wheelAccum -= notches * 120;
        Terminal.ScrollByRows(notches * 3);   // positive (wheel up) = toward older output
    }

    private void OnInputPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Up)
        {
            _vm.HistoryUpCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Down)
        {
            _vm.HistoryDownCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            // Close the About overlay if it is open; otherwise clear the input box.
            if (!TryCloseAbout())
                _vm.InputText = string.Empty;
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Enter)
        {
            // Sync from the native TextBox before capturing — the TwoWay binding propagation
            // for the last-typed character may not have reached the ViewModel yet, which would
            // drop that character ("hell" sent instead of "hello" on fast typing).
            if (_inputTextBox is not null)
                _vm.InputText = _inputTextBox.Text;
            _vm.SendCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Capture the current window content as a PNG (Ctrl+`).
    /// Saves to a timestamped file in TEMP and records the path in mucka-latest-selfie.txt
    /// so the ux-diligence skill can locate it without a file-system search.
    /// </summary>
    private async Task TakeSelfieAsync()
    {
        try
        {
            var nativeWindow = Window?.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
            if (nativeWindow?.Content is not Microsoft.UI.Xaml.UIElement root) return;

            var rtb = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
            await rtb.RenderAsync(root);
            var pixels = await rtb.GetPixelsAsync();

            var ts      = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var outPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mucka-selfie-{ts}.png");
            var ptrFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mucka-latest-selfie.txt");

            using (var fs = System.IO.File.OpenWrite(outPath))
            {
                var winrtStream = fs.AsRandomAccessStream();
                var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                    Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, winrtStream);
                encoder.SetPixelData(
                    Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                    Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                    (uint)rtb.PixelWidth, (uint)rtb.PixelHeight,
                    96, 96,
                    System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(pixels));
                await encoder.FlushAsync();
            }

            // Record the path so the ux-diligence skill can find it without a search.
            System.IO.File.WriteAllText(ptrFile, outPath);
            System.Diagnostics.Trace.WriteLine($"[selfie] {outPath}");

            // Flash the window title briefly so the user knows the selfie was taken.
            if (Application.Current?.Windows.FirstOrDefault() is Window win)
            {
                var origTitle = win.Title;
                win.Title = $"selfie \u2192 {System.IO.Path.GetFileName(outPath)}";
                await Task.Delay(3000);
                win.Title = origTitle;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[selfie] Failed: {ex.Message}");
        }
    }

    private void OnOpenRawConsoleRequested()
    {
        // Reuse existing window if it is still open.
        if (_rawConsoleWindow != null &&
            Application.Current?.Windows.Contains(_rawConsoleWindow) == true)
            return;
        _rawConsoleWindow = new Window(new RawConsolePage(_vm))
        {
            Title  = "Mucka — Raw Console",
            Width  = 900,
            Height = 550,
        };
        Application.Current?.OpenWindow(_rawConsoleWindow);
    }
#endif


    private static void LogCrash(string context, Exception ex)
    {
        try
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mucka-crash.txt");
            System.IO.File.AppendAllText(path, $"{DateTimeOffset.Now:o}  [{context}]\n{ex}\n\n");
            System.Diagnostics.Trace.WriteLine($"[Mucka] crash log: {path}");
        }
        catch { }
    }

    private void OnDisconnected()
    {
        _flushTimer?.Stop();
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            await DisplayAlertAsync("Disconnected", "The server closed the connection.", "OK");
            if (_exitOnDisconnect)
            {
                var window = Window ?? Application.Current?.Windows.FirstOrDefault();
                if (window != null)
                {
                    Application.Current?.CloseWindow(window);
                }
                return;
            }

            await Navigation.PopAsync();
        });
    }
}
