using System.Runtime.InteropServices;
using Mucka.Core;
using Mucka.Rendering;
using Mucka.ViewModels;
using MudSharp.Models;   // StyledLine/StyledSpan/TextStyle — used by the chat placeholder (all targets) and the $f<n> annotation handler (Windows)

namespace Mucka.Pages;

public partial class GamePage : ContentPage
{
    private readonly GameViewModel _vm;
    private readonly bool _exitOnDisconnect;
    // Slow (1 s) timer for anti-idle only. Output is NOT polled here — it is drained
    // event-driven via _vm.OutputAvailable → OnOutputAvailable (see DoFlushWork). This field
    // also doubles as the page-initialised sentinel (null == not yet set up).
    private IDispatcherTimer?      _antiIdleTimer;
    private IDispatcherTimer?      _toastTimer;

#if ANDROID
    // Set while this page is active so MainActivity can route hardware key events.
    private static Action<int>? _androidFkeyHandler;
    private static Action? _androidCtrlDHandler;
    private static Action? _androidCtrlLHandler;
    private static Action? _androidHistoryUpHandler;
    private static Action? _androidHistoryDownHandler;
    private static Action? _androidEscapeHandler;

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

    public static bool TryFireHistoryUp()
    {
        if (_androidHistoryUpHandler is null) return false;
        _androidHistoryUpHandler();
        return true;
    }

    public static bool TryFireHistoryDown()
    {
        if (_androidHistoryDownHandler is null) return false;
        _androidHistoryDownHandler();
        return true;
    }

    public static bool TryFireEscape()
    {
        if (_androidEscapeHandler is null) return false;
        _androidEscapeHandler();
        return true;
    }
#endif

    private bool _isFkeyEditorOpen;
    private bool _isGuidedLoginOverlayOpen;
    private bool _guidedLoginOverlayRunning;
    // The config/f-key editor while it is up, so a guided-login reentry can close it. Being
    // dropped from the game outranks whatever the player was configuring: leaving the editor
    // sitting there hides the fact that they are no longer in the game at all.
    private FkeyEditorPage? _fkeyEditorPage;
    private bool _eventsSubscribed;
    private double _floatTransX;
    private double _floatTransY;
    private double _floatMapTransX;
    private double _floatMapTransY;
    private Size _onlineLastSize;
    private Size _mapLastSize;
#if WINDOWS
    // True once an auxiliary window (raw console, map) has been opened. WinUI's native
    // caret-follow breaks after focus leaves to another app window, so the UpdateLayout
    // workaround in OnInputSelectionChanged is only needed from that point on.
    private bool _auxiliaryWindowOpened;
    private Window? _rawConsoleWindow;
    private Window? _mapWindow;
    // The old floating clog window (ClogPage) is gone - DESIGN_FINAL.md D1/D2: the live readout now
    // lives in the docked CombatPanelBorder (GamePage.xaml) instead of a second window, so there is
    // nothing here to hold a Window/Page reference for any more.
    private PulseLayer? _combatPanelPulse;
    private TickSweep? _combatTickSweep;
    // Audible half of the same tick clock. Owned here rather than by the view model so it starts,
    // stops and is disposed on exactly the same transitions as the visual sweep - two renderings of
    // one clock, never two clocks.
    private readonly Mucka.Audio.CombatMetronome _combatMetronome = new();
    // Last combat state the tick sweep was told about, so the sweep is started/stopped only on a real
    // transition. SidePanelViewModel.Live changes on every combat event, every FES heartbeat and every
    // 1 Hz tick; restarting a Composition animation on each of those would yank the bar back to empty
    // several times a second.
    private bool _tickSweepRunning;
    // The instant the current fight's tick lattice was started from. Both the visual sweep and the
    // audible click are anchored to it, so they are two renderings of one clock - and so arming the
    // metronome mid-fight joins the existing beat rather than starting a new one from the button
    // press. MUD2's tick is exactly 2.000s and phase-locked, which is what makes a single anchor
    // hold good for the length of a fight.
    private DateTime? _tickPhaseAnchorUtc;
    // Whether the audible tick is currently running. Tracked separately from the visual sweep because
    // the two have different run conditions - see UpdateCombatMetronome.
    private bool _metronomeRunning;
    // The anchor the click is currently aligned to. Held separately so a LATER, better anchor (the
    // encounter's first swing, replacing "no idea yet") re-aligns the bracket instead of being
    // ignored because the run state itself did not change.
    private DateTime? _metronomeAnchorUtc;
    // Same reasoning for the sweep's colour: set only when the band actually changes, since assigning
    // BoxView.Color rebuilds a native brush.
    private bool _tickSweepAlarmed;
    private Microsoft.UI.Xaml.Controls.TextBox? _inputTextBox;
    private Microsoft.UI.Xaml.Controls.ScrollViewer? _inputScroller;   // _inputTextBox's inner ScrollViewer
    private Microsoft.UI.Xaml.UIElement? _terminalElement;   // SKXamlCanvas, for wheel scrollback
    private Microsoft.UI.Xaml.UIElement? _fnButtonElement;   // Fn button, for right-tap → settings
    private Microsoft.UI.Xaml.Input.PointerEventHandler? _rootPointerHandler;
    // Window root element — holds the keyboard accelerators (hotkeys) and, only while in
    // scrollback, the temporary key handler. Kept so both can be torn down on disappear.
    private Microsoft.UI.Xaml.UIElement? _rootElement;
    private readonly List<Microsoft.UI.Xaml.Input.KeyboardAccelerator> _accelerators = new();
    private int _wheelAccum;   // accumulates wheel delta so touchpad drift doesn't trip scrollback
    // ── Window minimum-size enforcement ─────────────────────────────────────
    // Must match the WidthRequest of SidePanelBorder (the LEFT panel: Online/Items/Map) in
    // GamePage.xaml. Deliberately unchanged and untouched by the Combat Rail work below: that panel
    // keeps the width the player already plays with. Disturbing a working layout in a PvP,
    // permadeath game is its own hazard - see DESIGN_FINAL.md D3, which corrects an earlier draft
    // that said to widen THIS constant to 300 and dock combat content in it. That was wrong; the
    // Combat Rail is a wholly separate, additional panel (see CombatPanelWidthDp below).
    private const double SidePanelWidthDp = 228.0;

    // - Combat Rail: the new, additional right-edge panel (DESIGN_FINAL.md D3/2.2) -
    // Own width constant, deliberately separate from SidePanelWidthDp above. Shown/hidden by the
    // overflow menu's "Combat Rail" entry, which flips SidePanelViewModel.IsCombatPanelVisible; the
    // resize by exactly this many DIPs rides that property change (see OnSidePanelPropertyChanged
    // and ResizeWindowForCombatPanel) - the ONLY place
    // this window resizes for combat-panel reasons. It is never included in
    // PreferredWindowWidthDp/UpdateWindowMinimumWidth below: those compute the LEFT panel + terminal
    // minimum only, so the combat panel's own show/hide never feeds back into "what is the smallest
    // this window may be" - it simply occupies whatever space the toggle already made for it.
    private const double CombatPanelWidthDp = 300.0;
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
#if INPUT_DIAG
    // UI-thread responsiveness probe: a 16ms low-overhead heartbeat. Each tick measures the gap
    // since the previous tick — if the gap balloons past the interval, the UI thread was blocked
    // (e.g. by a terminal repaint or a layout pass) for that long, which is exactly the stall that
    // reads as typing lag. Active only in INPUT_DIAG builds.
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _uiProbeTimer;
    private DateTime _uiProbeLast;
#endif
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
        // Whatever modal we pushed has handed control back, so neither is up any more.
        _isFkeyEditorOpen = false;
        _fkeyEditorPage = null;
        _isGuidedLoginOverlayOpen = false;
        base.OnAppearing();

        try { DeviceDisplay.Current.KeepScreenOn = _vm.KeepScreenOn; }
        catch (Exception ex) { CrashLog.Write("KeepScreenOn", ex); }

        if (_antiIdleTimer == null)
        {
            // Unsubscribe before subscribing to guard against any double-subscribe scenario.
            if (!_eventsSubscribed)
            {
                _vm.OutputAvailable     += OnOutputAvailable;
                _vm.Disconnected        += OnDisconnected;
                _vm.RequestFocus        += FocusInput;
                _vm.SidePanel.RequestFocus += FocusInput;
                _vm.SidePanel.FloatingOpenDisplaySettings += OnFloatingOpenDisplaySettings;
                _vm.ConfigRequested     += OnConfigRequested;
                _vm.ClearScreenRequested += OnClearScreenRequested;
                _vm.GuidedLoginReentryRequested += OnGuidedLoginReentryRequested;
                _vm.ChatModeChanged     += OnChatModeChanged;
                _vm.SettingsSaved       += OnSettingsSaved;
                _vm.ToastRequested      += ShowToast;
                _vm.PropertyChanged     += OnVmPropertyChanged;
                Terminal.HistoryModeChanged += OnHistoryModeChanged;
                Terminal.FocusInputRequested += OnFocusInputRequested;
                Terminal.SpanInsertTextRequested += OnTerminalSpanInsertTextRequested;
                // Re-negotiate columns when the display rotates (see OnMainDisplayInfoChanged).
                DeviceDisplay.Current.MainDisplayInfoChanged += OnMainDisplayInfoChanged;
#if WINDOWS
                _vm.AnnotationReady += OnAnnotationReady;
#endif
                _eventsSubscribed = true;
            }

            Terminal.SetFontSize(_vm.FontSize);
            Terminal.Columns = _vm.EffCols;

            _antiIdleTimer = Dispatcher.CreateTimer();
            _antiIdleTimer.Interval = TimeSpan.FromSeconds(1);
            _antiIdleTimer.Tick += OnAntiIdleTick;
            _antiIdleTimer.Start();
            _vm.SidePanel.InitializeFadeTimer(Dispatcher);

            // Catch-up drain: if any output arrived before OutputAvailable was subscribed above,
            // its wake-up fired into the void and left the VM's guard armed. This clears the guard
            // (via FlushPendingLines) and paints the backlog; a no-op when nothing is queued.
            Dispatcher.Dispatch(DoFlushWork);

            if (Window is not null)
            {
                Window.Activated += OnWindowActivated;
                Window.Title = _vm.WindowTitle;   // profile-only until a character is identified
            }

#if WINDOWS
            try
            {
                // Hook window root to keep keyboard focus pinned to the input box (GettingFocus
                // bounce) and to register hotkeys as KeyboardAccelerators. Hotkeys are NOT a
                // per-keystroke handler: the framework invokes our callback only when an
                // accelerator matches, so ordinary typing runs zero managed key code.
                if (Window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window fwin &&
                    fwin.Content is Microsoft.UI.Xaml.UIElement froot)
                {
                    _rootElement = froot;
                    // WinUI Activated fires synchronously before pointer events, so the
                    // activation timestamp is always set before PointerPress can run.
                    fwin.Activated += OnWinUIWindowActivated;
                    RegisterHotkeyAccelerators(froot);
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
                // RecChip is listed explicitly even though it lives inside StatusBar: a Border's
                // platform view is its own WinUI panel, and AllowFocusOnInteraction is a
                // per-element property -- setting it on the StatusBar panel does not reach it.
                // Clicking "rec" was taking keyboard focus off the input box and keeping it
                // (Invariant #0).
                DisableFocusOnInteraction(StatusBar, RecChip, SidePanelBorder, FkeyBar, FnButton, SendButton, ScrollbackBar, FloatingOnlinePanel);
#if FOCUS_DIAG
                Microsoft.UI.Xaml.Input.FocusManager.GettingFocus += OnFmGettingFocus;
                Microsoft.UI.Xaml.Input.FocusManager.LosingFocus += OnFmLosingFocus;
                Microsoft.UI.Xaml.Input.FocusManager.GotFocus += OnFmGotFocus;
                Microsoft.UI.Xaml.Input.FocusManager.LostFocus += OnFmLostFocus;
                InputEntry.Focused += (_, _) => FocusDiag("maui.Entry Focused");
                InputEntry.Unfocused += (_, _) => FocusDiag("maui.Entry Unfocused");
#endif
                // Hook the native TextBox so Up/Down/Esc keys work in the entry.
                // Apply now AND on handler change: if the platform view already exists when
                // this runs, HandlerChanged never fires again, and without the direct call the
                // TwoWay Text binding stays live (per-keystroke VM round-trips — the recurring
                // lag regression) and Up/Down/Esc/Enter handling silently degrades.
                InputEntry.HandlerChanged += OnInputHandlerChanged;
                OnInputHandlerChanged(InputEntry, EventArgs.Empty);
                // Right-click on Fn opens the settings page (left-click toggles the fkey bar).
                // Apply now and on handler change, since platform views can be recreated.
                FnButton.HandlerChanged += OnFnButtonHandlerChanged;
                OnFnButtonHandlerChanged(FnButton, EventArgs.Empty);
                // Hook the terminal canvas for mouse-wheel scrollback.
                Terminal.HandlerChanged += OnTerminalHandlerChanged;
                OnTerminalHandlerChanged(Terminal, EventArgs.Empty);
                // The ▶ button must read the native TextBox like the Enter path does (no Text
                // binding on Windows): shadow the XAML SendCommand and send via Clicked, or it
                // sends a blank line and strands the typed text in the box.
                SendButton.Command = null;
                SendButton.Clicked -= OnSendButtonClicked;
                SendButton.Clicked += OnSendButtonClicked;
                _vm.OpenRawConsoleRequested += OnOpenRawConsoleRequested;
                _vm.MapPanelRequested += OnMapPanelRequested;
                // Enforce minimum window width based on the configured terminal columns, and
                // (same handler) drive the Combat Rail's shared pulse layer on tier changes.
                _vm.SidePanel.PropertyChanged += OnSidePanelPropertyChanged;
                CombatPanelGlow.HandlerChanged += OnCombatPanelGlowHandlerChanged;
                OnCombatPanelGlowHandlerChanged(CombatPanelGlow, EventArgs.Empty);
                CombatTickSweep.HandlerChanged += OnCombatTickSweepHandlerChanged;
                OnCombatTickSweepHandlerChanged(CombatTickSweep, EventArgs.Empty);
                CombatMetronomeHit.HandlerChanged += OnCombatMetronomeHandlerChanged;
                OnCombatMetronomeHandlerChanged(CombatMetronomeHit, EventArgs.Empty);
                SetupWindowMinimumSize();
                // Size the window once so the terminal view fits ~82 columns + the side panel,
                // rather than inheriting WinUI's oversized default window width.
                SetPreferredInitialWindowSize();
#if INPUT_DIAG
                StartUiThreadProbe();
#endif
            }
            catch (Exception ex) { CrashLog.Write("OnAppearing/Windows", ex); }
#endif
        }
        else
        {
            // Returning from a modal we pushed ourselves (FkeyEditor, the Persona Login overlay):
            // events and platform hooks are still active; just resume the timer.
            _antiIdleTimer.Start();
        }

        FocusInput();

#if ANDROID
        _androidFkeyHandler = _vm.SendFkeyAbsolute;
        _androidCtrlDHandler = _vm.SpeakDreamword;
        _androidCtrlLHandler = _vm.ClearScreen;
        // History recall (Up/Down) is not a per-keystroke cost — wire the hardware arrows to the
        // same commands the Windows input box uses, then park the cursor at the end of the recalled
        // command so it can be edited immediately (the TwoWay binding has already pushed the text).
        _androidHistoryUpHandler = () =>
        {
            _vm.HistoryUpCommand.Execute(null);
            InputEntry.CursorPosition = _vm.InputText?.Length ?? 0;
        };
        _androidHistoryDownHandler = () =>
        {
            _vm.HistoryDownCommand.Execute(null);
            InputEntry.CursorPosition = _vm.InputText?.Length ?? 0;
        };
        // Escape closes the About overlay if it is open; otherwise it clears the input. The TwoWay
        // binding pushes the empty string to the Entry (no native-textbox sync needed as on Windows).
        _androidEscapeHandler = () =>
        {
            if (!TryCloseAbout())
                _vm.InputText = string.Empty;
        };
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
        _androidHistoryUpHandler = null;
        _androidHistoryDownHandler = null;
        _androidEscapeHandler = null;
#endif
        if (_isFkeyEditorOpen || _isGuidedLoginOverlayOpen)
        {
            // Pause the timer while the modal is open. Keep it non-null so OnAppearing
            // knows not to reinitialize the terminal or re-hook events on return.
            _antiIdleTimer?.Stop();
            return;
        }

        DeviceDisplay.Current.KeepScreenOn = false;
        _antiIdleTimer?.Stop();
        _antiIdleTimer = null;
        _toastTimer?.Stop();
        _vm.OutputAvailable     -= OnOutputAvailable;
        _vm.Disconnected        -= OnDisconnected;
        _vm.RequestFocus        -= FocusInput;
        _vm.SidePanel.RequestFocus -= FocusInput;
        _vm.SidePanel.FloatingOpenDisplaySettings -= OnFloatingOpenDisplaySettings;
        _vm.ConfigRequested     -= OnConfigRequested;
        _vm.ClearScreenRequested -= OnClearScreenRequested;
        _vm.GuidedLoginReentryRequested -= OnGuidedLoginReentryRequested;
        _vm.ChatModeChanged      -= OnChatModeChanged;
        _vm.SettingsSaved       -= OnSettingsSaved;
        _vm.ToastRequested      -= ShowToast;
        _vm.PropertyChanged     -= OnVmPropertyChanged;
        Terminal.HistoryModeChanged -= OnHistoryModeChanged;
        Terminal.FocusInputRequested -= OnFocusInputRequested;
        Terminal.SpanInsertTextRequested -= OnTerminalSpanInsertTextRequested;
        DeviceDisplay.Current.MainDisplayInfoChanged -= OnMainDisplayInfoChanged;
#if WINDOWS
        _vm.AnnotationReady -= OnAnnotationReady;
#endif
        _eventsSubscribed = false;
        if (Window is not null)
        {
            Window.Activated -= OnWindowActivated;
            // Backing out to the profile menu: drop the char@profile title back to the generic form.
            Window.Title = $"mucka {AppInfo.VersionString}";
        }
#if WINDOWS
        if (Window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window fwin &&
            fwin.Content is Microsoft.UI.Xaml.UIElement froot)
        {
            fwin.Activated -= OnWinUIWindowActivated;
            UnregisterHotkeyAccelerators();
            froot.PreviewKeyDown -= OnScrollbackKeyDown;   // no-op if not in scrollback
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
            _inputTextBox.SelectionChanged -= OnInputSelectionChanged;
            _inputTextBox = null;
            _inputScroller = null;
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
        _vm.MapPanelRequested -= OnMapPanelRequested;
        _vm.SidePanel.PropertyChanged -= OnSidePanelPropertyChanged;
        CombatPanelGlow.HandlerChanged -= OnCombatPanelGlowHandlerChanged;
        CombatTickSweep.HandlerChanged -= OnCombatTickSweepHandlerChanged;
        CombatMetronomeHit.HandlerChanged -= OnCombatMetronomeHandlerChanged;
        // A thread-pool timer outlives its page unless stopped; a metronome still clicking after the
        // window closed is the audible version of the RO_E_CLOSED crash class below.
        _combatMetronome.Dispose();
        // Belt-and-braces alongside PulseLayer's own host.Unloaded hook (see PulseLayer's remarks):
        // stop any running Composition animation before this page's own teardown proceeds, so a
        // torn-down visual can never still have a live animation referencing it (the RO_E_CLOSED
        // crash class the old ClogPage hit once already). The tick sweep is the same crash class.
        _combatPanelPulse?.Stop();
        _combatPanelPulse = null;
        _combatTickSweep?.Stop();
        _combatTickSweep = null;
        TeardownWindowMinimumSize();
#if INPUT_DIAG
        StopUiThreadProbe();
#endif
#endif
        _ = _vm.DisposeAsync();
    }

    // 1 s tick: anti-idle keep-alive plus the projected reset countdown. Output draining is
    // event-driven, not polled — see OnOutputAvailable.
    private void OnAntiIdleTick(object? sender, EventArgs e)
    {
        _vm.AntiIdleTick();
        _vm.TickResetCountdown();
        _vm.TickCombatDisplay();
    }

    // Server output arrived. Fires on the TCP thread (coalesced to one pending flush by the VM's
    // guard); marshal a single drain/paint to the UI thread. Replaces the old 50 ms poll so the
    // first line renders on the next dispatcher pump, while bursts still batch into one flush.
    private void OnOutputAvailable() => Dispatcher.Dispatch(DoFlushWork);

    private void DoFlushWork()
    {
        // Drain the ViewModel queue straight into the Skia terminal. The partial/complete/merge/
        // clear semantics live in TerminalBuffer (inside TerminalView); a paint of one screenful
        // is sub-millisecond, so there is no need to defer this off the keyboard's priority lane.
        var newLines = _vm.FlushPendingLines();
        if (newLines is { Count: > 0 })
        {
            InputDiag.Log($"FLUSH n={newLines.Count}");
            if (_vm.ChatMode)
            {
                // Chat filter on: paint only chat lines. Non-chat output is still captured in the
                // VM history buffers (so toggling off restores it) — it just does not draw here.
                // Any non-chat, non-partial line arriving is the "other stuff" the flash signals
                // (FES/stats never arrive as terminal lines; prompts are IsPartial).
                List<StyledLine>? chat = null;
                bool otherArrived = false;
                foreach (var l in newLines)
                {
                    if (l.Kind == LineKind.Chat) (chat ??= new List<StyledLine>()).Add(l);
                    else if (!l.IsPartial && !l.PlainText.Contains('\f')) otherArrived = true;
                }
                if (chat != null)
                {
                    if (_chatPlaceholderShown)
                    {
                        // First chat since the empty-state note — repaint from the buffer (which now
                        // holds these lines) so the "[no chat…]" placeholder is dropped.
                        Terminal.Clear();
                        Terminal.AppendLines(_vm.ChatSnapshot());
                        _chatPlaceholderShown = false;
                    }
                    else
                    {
                        Terminal.AppendLines(chat);
                    }
                }
                if (otherArrived) FlashChatButton();
            }
            else
            {
                Terminal.AppendLines(newLines);
            }
        }
    }

    // Character width in MAUI logical pixels — delegates to the view-model so both
    // the column-count calculation and the minimum-width enforcement use one formula.
    private double CharWidthDp => _vm.CharWidthDp;

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        RecalculateColumns(width);
    }

    // Recompute the negotiated column count from the current page width and keep the terminal's
    // wrap width in sync. Funnelled here so both the layout pass (OnSizeAllocated) and an
    // orientation flip (OnMainDisplayInfoChanged) drive the same recalculation — NotifyWindowSize
    // is a no-op when the effective column count is unchanged, so calling it twice is harmless.
    private void RecalculateColumns(double width)
    {
        if (width <= 0) return;
        var displayableCols = (int)Math.Floor(width / CharWidthDp);
        _vm.NotifyWindowSize(width, displayableCols);
        Terminal.Columns = _vm.EffCols;
    }

    // A rotation on Android reconfigures the activity in place (see MainActivity's
    // ConfigurationChanges) instead of recreating it, so OnSizeAllocated does not reliably fire
    // with the new width — the terminal would stay wrapped to the old orientation's columns.
    // Recompute off the page's post-rotation width once the layout has settled.
    private void OnMainDisplayInfoChanged(object? sender, DisplayInfoChangedEventArgs e)
        => Dispatcher.Dispatch(() => RecalculateColumns(Width));

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
        _fkeyEditorPage = new FkeyEditorPage(editorVm);
        return Navigation.PushModalAsync(_fkeyEditorPage);
    }

    private async void OnConfigRequested() => await OpenConfigAsync(initialTab: 0);
    private async void OnFloatingOpenDisplaySettings() => await OpenConfigAsync(initialTab: 1);

    // Fired when the shell drops us back to the Option menu (quit, permadeath, or a game reset):
    // re-run the persona dance rather than stranding the player at a bare menu prompt.
    private void OnGuidedLoginReentryRequested(
        Mucka.Core.GuidedLogin.GuidedLoginOptions options,
        Mucka.Core.GuidedLogin.SessionDropContext drop)
        => _ = RunGuidedLoginOverlayAsync(options, drop);

    private async Task RunGuidedLoginOverlayAsync(
        Mucka.Core.GuidedLogin.GuidedLoginOptions options,
        Mucka.Core.GuidedLogin.SessionDropContext drop)
    {
        // Fire-and-forget from the VM event, so nothing above us observes a faulted task.
        try { await RunGuidedLoginOverlayCoreAsync(options, drop); }
        catch (Exception ex) { CrashLog.Write("GuidedLoginReentry", ex); }
    }

    private async Task RunGuidedLoginOverlayCoreAsync(
        Mucka.Core.GuidedLogin.GuidedLoginOptions options,
        Mucka.Core.GuidedLogin.SessionDropContext drop)
    {
        // Both guards run on the UI thread with no await in between, so this is a real latch.
        if (_guidedLoginOverlayRunning)
            return;
        _guidedLoginOverlayRunning = true;

        // The player is out of the game -- that beats whatever they were configuring. Close the
        // editor before pushing the overlay: stacking on top of it would leave them staring at a
        // settings page with no sign they had been dropped, and the editor's own close would then
        // pop the overlay instead of itself. Unsaved edits are discarded, which is the lesser
        // annoyance -- they were being made while at risk in the game.
        if (_fkeyEditorPage is { } editor)
        {
            await editor.CloseAsync();
            _fkeyEditorPage = null;
            _isFkeyEditorOpen = false;
        }

        var controller = _vm.CreateGuidedLoginController(options);
        var loginVm = new GuidedLoginViewModel(controller, drop);
        GuidedLoginPage? page = null;
        var leftAtOptionMenu = false;
        var endConnection = false;
        try
        {
            _isGuidedLoginOverlayOpen = true;
            page = new GuidedLoginPage(loginVm);
            await Navigation.PushModalAsync(page);

            var result = await controller.RunAsync(page.CancellationToken);
            // Not worth an alert if the session went away underneath us (an account logout also
            // exits game mode) -- the disconnect handling already tells the player.
            if (result.Outcome == Mucka.Core.GuidedLogin.GuidedLoginOutcome.Failed
                && result.FailureReason != null
                && _vm.IsConnected)
            {
                await DisplayAlertAsync(
                    "Persona Login Failed",
                    $"{result.FailureReason}\n\nYou can stay at the Option menu and continue manually.",
                    "OK");
            }

            // The MUD Shell must stay behind the curtain of our guided-login UI: the only door
            // back to it is the picker's explicit "[Drop to menu]" option (ManualAtOptionMenu).
            // Every other way of leaving the picker -- Escape, its own Cancel button, a stray
            // click outside it -- is Cancelled, and Cancelled always ends the connection, exactly
            // like cancelling the initial-connect picker in ConnectPage. Do not special-case
            // Cancelled here to "stay at the shell" again: that was tried (to dodge one bad
            // stray-click-after-death recording) and it defeats the entire point of having a
            // dedicated, explicit "drop to shell" affordance.
            leftAtOptionMenu = result.Outcome == Mucka.Core.GuidedLogin.GuidedLoginOutcome.ManualAtOptionMenu;
            endConnection = result.Outcome == Mucka.Core.GuidedLogin.GuidedLoginOutcome.Cancelled;
        }
        finally
        {
            loginVm.Detach();
            controller.Dispose();
            if (page != null)
                await Navigation.PopModalAsync();
            _isGuidedLoginOverlayOpen = false;
            _guidedLoginOverlayRunning = false;
            if (!Terminal.IsHistoryMode)
                FocusInput();
        }

        // Say so in the terminal: the overlay vanishing with no explanation is exactly the
        // "no obvious indication of what happened" problem the headline was added to solve.
        if (leftAtOptionMenu && _vm.IsConnected)
            _vm.NoteLeftAtOptionMenu();

        if (endConnection && _vm.IsConnected)
        {
            await _vm.DisconnectAsync();
            await Navigation.PopAsync();
        }
    }

    // Dragging is only allowed while the windlet is unlocked (the lock icon toggles it).
    private void OnFloatingPanelPanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        if (_vm.SidePanel.IsFloatingOnlineLocked) return;
        switch (e.StatusType)
        {
            case GestureStatus.Started:
            case GestureStatus.Running:
                FloatingOnlinePanel.TranslationX = _floatTransX + e.TotalX;
                FloatingOnlinePanel.TranslationY = _floatTransY + e.TotalY;
                break;
            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                _floatTransX = FloatingOnlinePanel.TranslationX;
                _floatTransY = FloatingOnlinePanel.TranslationY;
                break;
        }
    }

    private void OnFloatingMapPanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        if (_vm.SidePanel.IsFloatingMapLocked) return;
        switch (e.StatusType)
        {
            case GestureStatus.Started:
            case GestureStatus.Running:
                FloatingMapPanel.TranslationX = _floatMapTransX + e.TotalX;
                FloatingMapPanel.TranslationY = _floatMapTransY + e.TotalY;
                break;
            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                _floatMapTransX = FloatingMapPanel.TranslationX;
                _floatMapTransY = FloatingMapPanel.TranslationY;
                break;
        }
    }

    // Hover brightens the corner lock so it's obvious while you're working with the windlet,
    // and dims back to a discreet 0.5 at rest (still visible enough to tap on a touch screen,
    // where these never fire). Opacity only — no layout impact.
    private void OnFloatingOnlinePointerEntered(object? sender, PointerEventArgs e) => OnlineLockIcon.Opacity = 1.0;
    private void OnFloatingOnlinePointerExited(object? sender, PointerEventArgs e)  => OnlineLockIcon.Opacity = 0.5;
    private void OnFloatingMapPointerEntered(object? sender, PointerEventArgs e)    => MapLockIcon.Opacity = 1.0;
    private void OnFloatingMapPointerExited(object? sender, PointerEventArgs e)     => MapLockIcon.Opacity = 0.5;

    private void OnFloatingOnlineSizeChanged(object? sender, EventArgs e)
    {
        Reanchor(FloatingOnlinePanel, ref _onlineLastSize);
        _floatTransX = FloatingOnlinePanel.TranslationX;
        _floatTransY = FloatingOnlinePanel.TranslationY;
    }

    private void OnFloatingMapSizeChanged(object? sender, EventArgs e)
    {
        Reanchor(FloatingMapPanel, ref _mapLastSize);
        _floatMapTransX = FloatingMapPanel.TranslationX;
        _floatMapTransY = FloatingMapPanel.TranslationY;
    }

    // Keep a floating panel anchored by the screen quadrant it sits in when it grows/shrinks
    // (resize buttons, lock/unlock revealing the title strip, fold). A panel in the bottom half grows upward;
    // one pinned to the right edge grows leftward; a top-docked panel just grows down.
    private void Reanchor(Border panel, ref Size last)
    {
        var cur = new Size(panel.Width, panel.Height);
        if (cur.Width <= 0 || cur.Height <= 0) return;      // hidden / not yet measured
        if (last.Width <= 0) { last = cur; return; }        // first measure — nothing to anchor against

        double dW = cur.Width  - last.Width;
        double dH = cur.Height - last.Height;
        last = cur;
        if (Math.Abs(dW) < 0.5 && Math.Abs(dH) < 0.5) return;

        if (panel.Parent is not VisualElement parent || parent.Width <= 0 || parent.Height <= 0)
            return;

        // The panel is centre-anchored horizontally, so TranslationX is its offset from centre.
        double bandX = parent.Width * 0.15;
        if (panel.TranslationX > bandX)        panel.TranslationX -= dW;        // right edge fixed
        else if (panel.TranslationX < -bandX)  { /* left edge fixed — no change */ }
        else                                   panel.TranslationX -= dW / 2;    // stays centred

        double panelCentreY = panel.Y + panel.TranslationY + cur.Height / 2;
        double bandY = parent.Height * 0.15;
        if (panelCentreY > parent.Height / 2 + bandY)      panel.TranslationY -= dH;      // bottom → grow up
        else if (panelCentreY < parent.Height / 2 - bandY) { /* top → grow down */ }
        else                                               panel.TranslationY -= dH / 2;  // vertically centred
    }

    // Deferred a dispatcher tick: when invoked from a tap/click handler, WinUI settles pointer
    // focus on the clicked control AFTER the handler returns, which would clobber an immediate
    // Focus() call. Posting the focus wins that race.
    private void FocusInput() => Dispatcher.Dispatch(() =>
    {
#if WINDOWS
        FocusDiag($"FocusInput dispatch: IsFocused={InputEntry.IsFocused}");
        // The deferred dispatch fires after WinUI has fully settled focus, so IsFocused is
        // authoritative here. Skip if the input already has focus — Focus(Programmatic) on
        // an already-focused WinUI TextBox resets the cursor to position 0, which causes a
        // "|o" symptom on rapid Enter+type sequences (character appears after the cursor).
        if (!InputEntry.IsFocused)
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

#if WINDOWS
    // WinUI-level Activated fires synchronously in the message queue before pointer events,
    // guaranteeing the activation timestamp is set before PointerPress can run (the MAUI
    // Window.Activated event can be marshalled asynchronously and lose the race).
    private void OnWinUIWindowActivated(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState != Microsoft.UI.Xaml.WindowActivationState.Deactivated)
            Terminal.NotifyWindowActivated();
    }
#endif

    private void OnClearScreenRequested() => Terminal.Clear();

    // Chat filter flipped: repaint the whole terminal from the matching buffer. This is a
    // user-initiated toggle (not the typing hot path), so a full Clear + re-append is fine —
    // one screenful paints sub-millisecond and the source buffers live in the VM, so nothing
    // is lost either way. Kind==Chat lines that scrolled out of the main ring still show,
    // because the chat ring is kept deeper (ChatHistoryCap).
    // Shown in chat mode when no chat has arrived yet, so the toggle never lands on a blank screen.
    private static readonly StyledLine ChatEmptyPlaceholderLine =
        new(new[] { new StyledSpan("[no chat in this session yet]", new TextStyle(Foreground: (AnsiColor)8)) });
    private bool _chatPlaceholderShown;

    private void OnChatModeChanged()
    {
        Terminal.Clear();
        if (_vm.ChatMode)
        {
            var chat = _vm.ChatSnapshot();
            _chatPlaceholderShown = chat.Count == 0;
            Terminal.AppendLines(_chatPlaceholderShown ? new[] { ChatEmptyPlaceholderLine } : chat);
        }
        else
        {
            _chatPlaceholderShown = false;
            Terminal.AppendLines(_vm.HistorySnapshot());
        }
        // A toggle is the user acknowledging activity — cancel any pending flash and reset the tint.
        _chatFlashGen++;
        _chatFlashActive = false;
        ChatButton.BackgroundColor = ChatButtonRest;
    }

    // Chat button colours. Rest matches the XAML; alert is a bright-orange pulse — deliberately a
    // different hue/luminance from the dark-slate rest and the yellow "on" cue so it reads as an
    // alert, not "still selected".
    private static readonly Color ChatButtonRest  = Color.FromArgb("#2d333b");
    private static readonly Color ChatButtonAlert = Color.FromArgb("#f0883e");
    private int _chatFlashGen;
    private bool _chatFlashActive;

    // One-shot attention pulse when non-chat output arrives while filtered. NOT a repeating
    // UI-thread timer (Invariant #1): a single DispatchDelayed clears the tint. While a pulse is
    // in flight we skip re-arming, so a burst of hidden output (combat) does not churn a timer +
    // closure per flush — it pulses at most every ~450 ms until the output settles.
    // If a stronger sustained pulse is ever wanted, drive it from a WinUI composition animation.
    private void FlashChatButton()
    {
        if (!_vm.ChatMode || _chatFlashActive) return;
        _chatFlashActive = true;
        ChatButton.BackgroundColor = ChatButtonAlert;
        int gen = ++_chatFlashGen;
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(450), () =>
        {
            if (gen != _chatFlashGen) return;   // superseded by a toggle
            _chatFlashActive = false;
            if (_vm.ChatMode) ChatButton.BackgroundColor = ChatButtonRest;
        });
    }

#if WINDOWS
    // $f<n> annotation: drop the "// ..." note above the live prompt, restoring the prompt below it.
    private void OnAnnotationReady(StyledLine line) => Terminal.InjectAnnotation(line);
#endif

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
        else if (e.PropertyName == nameof(GameViewModel.WindowTitle))
        {
            if (Window is not null) Window.Title = _vm.WindowTitle;
        }
        else if (e.PropertyName == nameof(GameViewModel.MaxColumns))
        {
            Terminal.Columns = _vm.EffCols;
#if WINDOWS
            UpdateWindowMinimumWidth();
            ResizeWindowToFitColumns();
#endif
        }
#if WINDOWS
        else if (e.PropertyName == nameof(GameViewModel.InputText))
        {
            // Windows drives the native TextBox manually (the Text binding is removed in
            // OnInputHandlerChanged — see the rationale there). Push deliberate VM changes
            // (clear-on-send, history nav, Escape) into the box and park the cursor at the end.
            // The != guard makes the Enter-path's "read native into VM" set a harmless no-op.
            if (_inputTextBox is not null && _inputTextBox.Text != _vm.InputText)
            {
                _inputTextBox.Text = _vm.InputText;
                _inputTextBox.SelectionStart = _inputTextBox.Text.Length;
            }
        }
#endif
    }

    private void OnSettingsSaved() => ShowToast("* Settings saved");

    /// <summary>Closes the About overlay if it is open. Returns true when it was.</summary>
    private bool TryCloseAbout()
    {
        if (!_vm.SidePanel.IsAboutVisible) return false;
        _vm.SidePanel.CloseAboutCommand.Execute(null);
        return true;
    }

    // Android hardware/gesture back: dismiss the About overlay if open. Otherwise confirm before
    // leaving — a gesture-nav back swipe is trivially easy to hit by accident, and popping this
    // page tears down the live session.
    protected override bool OnBackButtonPressed()
    {
        if (TryCloseAbout()) return true;
#if ANDROID
        Dispatcher.Dispatch(async () =>
        {
            if (await DisplayAlertAsync("Disconnect", "Leave the game and disconnect?", "Disconnect", "Stay"))
                await Navigation.PopAsync();
        });
        return true;   // swallow the back action; pop only on confirmation
#else
        return base.OnBackButtonPressed();
#endif
    }

    // Scrollback is an explicit mode: swap the input row for the yellow "SCROLLBACK" indicator,
    // and restore + re-focus the input box on return to live.
    private void OnHistoryModeChanged(object? sender, EventArgs e)
    {
        // Overlay the indicator ON TOP of the (still-present) input controls rather than hiding
        // them, so the input row's measured height never changes — the text view above must not
        // reflow when entering/leaving scrollback.
        ScrollbackBar.IsVisible = Terminal.IsHistoryMode;
#if WINDOWS
        // Attach the scrollback key handler ONLY while reviewing history; detach on return so it
        // is never in the live typing path. (Detach is idempotent if it was never attached.)
        if (_rootElement is not null)
        {
            _rootElement.PreviewKeyDown -= OnScrollbackKeyDown;
            if (Terminal.IsHistoryMode)
                _rootElement.PreviewKeyDown += OnScrollbackKeyDown;
        }
#endif
        if (!Terminal.IsHistoryMode) FocusInput();
    }

    // An activation click (the click that brought the app to the foreground) just focuses input.
    private void OnFocusInputRequested(object? sender, EventArgs e) => FocusInput();

    // Tapping the SCROLLBACK indicator returns to live.
    private void OnScrollbackBarTapped(object? sender, TappedEventArgs e) => Terminal.ScrollToBottom();

    // Compact-mode overflow menu: shows a dark-themed popup in the body area.
    // Long-press (Android) / right-click (Windows) triggers the attached MenuFlyout bonus.
    private void OnOverflowMenuTapped(object? sender, TappedEventArgs e)
        => OverflowMenuOverlay.IsVisible = true;
    private void OnOverflowMenuDismiss(object? sender, TappedEventArgs e)
        => OverflowMenuOverlay.IsVisible = false;
    // Side Panel / Onlines / Compass are checkable toggles a player might flip back and forth
    // while the menu is open, so their tap-driven overlay rows bind straight to the toggle
    // commands and deliberately stay open (the ✓ updates live) - these three OnOverflow* methods
    // only back the long-press/right-click MenuFlyout, which auto-dismisses natively regardless.
    // All three flip live visibility only — saved settings are untouched.
    private void OnOverflowTogglePanel(object? sender, EventArgs e)
    {
        OverflowMenuOverlay.IsVisible = false;
        _vm.SidePanel.TogglePanelCommand.Execute(null);
    }
    private void OnOverflowOnlines(object? sender, EventArgs e)
    {
        OverflowMenuOverlay.IsVisible = false;
        _vm.SidePanel.ToggleOnlinePinnedCommand.Execute(null);
    }
    private void OnOverflowCompass(object? sender, EventArgs e)
    {
        OverflowMenuOverlay.IsVisible = false;
        _vm.SidePanel.ToggleMapPinnedCommand.Execute(null);
    }
    // Unlike the three above, Combat Rail is a one-shot panel toggle a player isn't watching
    // live while the menu stays open (more like Settings/About below) - so its overlay row also
    // routes through here (a Tapped handler, not a raw Command binding) to close the menu.
    private void OnOverflowCombatRail(object? sender, EventArgs e)
    {
        OverflowMenuOverlay.IsVisible = false;
        _vm.SidePanel.ToggleCombatPanelCommand.Execute(null);
    }
    private void OnOverflowSettings(object? sender, EventArgs e)
    {
        OverflowMenuOverlay.IsVisible = false;
        _vm.ConfigCommand.Execute(null);
    }
    private void OnOverflowAbout(object? sender, EventArgs e)
    {
        OverflowMenuOverlay.IsVisible = false;
        _vm.SidePanel.ShowAboutCommand.Execute(null);
    }

    // Orientation sub-menu: the header toggles the three choices open/closed in place; the
    // chevron flips ▸/▾ to match. Kept inside the overlay so a tap outside still dismisses all.
    private void OnOverflowOrientationTapped(object? sender, TappedEventArgs e)
    {
        bool show = !OrientationSubmenu.IsVisible;
        OrientationSubmenu.IsVisible = show;
        OrientationChevron.Text = show ? "▾" : "▸";
    }

    private void OnOrientationSystem(object? sender, EventArgs e)    => ApplyOrientation(OrientationMode.System);
    private void OnOrientationPortrait(object? sender, EventArgs e)  => ApplyOrientation(OrientationMode.Portrait);
    private void OnOrientationLandscape(object? sender, EventArgs e) => ApplyOrientation(OrientationMode.Landscape);

    private enum OrientationMode { System, Portrait, Landscape }

    // Set the display orientation. Android drives the real activity; other platforms are a no-op
    // (the sub-menu is Android-only). Collapse the overlay and hand focus back per Invariant #0.
    private void ApplyOrientation(OrientationMode mode)
    {
        OrientationSubmenu.IsVisible = false;
        OrientationChevron.Text = "▸";
        OverflowMenuOverlay.IsVisible = false;
        // Mark the active choice (session-only — no saved-settings change).
        OrientCheckSystem.IsVisible    = mode == OrientationMode.System;
        OrientCheckPortrait.IsVisible  = mode == OrientationMode.Portrait;
        OrientCheckLandscape.IsVisible = mode == OrientationMode.Landscape;
#if ANDROID
        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
        if (activity is not null)
        {
            activity.RequestedOrientation = mode switch
            {
                OrientationMode.Portrait  => Android.Content.PM.ScreenOrientation.Portrait,
                OrientationMode.Landscape => Android.Content.PM.ScreenOrientation.Landscape,
                _                         => Android.Content.PM.ScreenOrientation.Unspecified,
            };
        }
#endif
        FocusInput();
    }

    // Prepends a clickable span's insert text ("Name ") to the command box when the caret is at
    // the start with nothing selected. Shared code: the subscription in OnAppearing is
    // unconditional, so this must live outside the Windows-only region below (the Android build
    // broke when it didn't). On Android nothing raises SpanInsertTextRequested yet — the touch
    // path only pans — so it is dormant there until a tap gesture is wired up.
    private void OnTerminalSpanInsertTextRequested(string insertText)
    {
        if (string.IsNullOrWhiteSpace(insertText)) return;
#if WINDOWS
        if (_inputTextBox is not null)
        {
            // Only inject a quick-reply token when the caret is exactly at the start and
            // there is no active selection.
            if (_inputTextBox.SelectionStart != 0 || _inputTextBox.SelectionLength != 0)
                return;
            SetInputText(insertText + _inputTextBox.Text, insertText.Length);
            return;
        }
#endif
        if (InputEntry.CursorPosition != 0 || InputEntry.SelectionLength != 0)
            return;
        SetInputText(insertText + (InputEntry.Text ?? string.Empty), insertText.Length);
    }

    // Ctrl+R (issue #147): unconditionally replaces the input with "<sender> "" and puts the
    // caret after the quote, ready for the reply text. Unlike OnTerminalSpanInsertTextRequested's
    // prepend, this clears whatever was typed — ctrl-r is a deliberate "start a reply now" action.
    private void ReplyToLastTell()
    {
        var sender = _vm.LastTellSender;
        if (string.IsNullOrEmpty(sender))
        {
            _vm.NoteNoTellToReplyTo();
            FocusInput();
            return;
        }
        var text = sender + " \"";
        SetInputText(text, text.Length);
        FocusInput();
    }

    // Sets the command box's text and caret position on whichever platform control is live, and
    // keeps _vm.InputText in sync. Shared tail for the click-to-insert-name and Ctrl+R reply paths.
    private void SetInputText(string text, int caretPos)
    {
#if WINDOWS
        if (_inputTextBox is not null)
        {
            _inputTextBox.Text = text;
            _inputTextBox.SelectionStart = caretPos;
            _vm.InputText = text;
            return;
        }
#endif
        InputEntry.Text = text;
        InputEntry.CursorPosition = caretPos;
        InputEntry.SelectionLength = 0;
        _vm.InputText = text;
    }

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
    /// (and resizes if needed) whenever the LEFT panel is expanded or collapsed, and drives the
    /// Combat Rail's shared pulse layer whenever its tier changes. Deliberately one handler for
    /// both - they are the same class of "a view-model flag changed, react on the UI thread" work,
    /// and the LEFT panel's own resize-then-refocus dance below is unrelated to (and must not be
    /// duplicated for) the combat panel's own resize, which lives in ResizeWindowForCombatPanel.
    /// </summary>
    private void OnSidePanelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SidePanelViewModel.IsPanelExpanded))
        {
            UpdateWindowMinimumWidth();
            // Toggling the panel can resize the window (its min width changes), and that resize
            // lands keyboard focus elsewhere AFTER TogglePanelCommand's own RequestFocus has
            // already fired — so the input box is left unfocused. Re-assert focus once the resize
            // and re-layout have settled (a plain dispatch still races it; a short delay wins).
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(50), () =>
            {
                if (!_isFkeyEditorOpen && !Terminal.IsHistoryMode && !InputEntry.IsFocused)
                    _inputTextBox?.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
            });
        }
        else if (e.PropertyName == nameof(SidePanelViewModel.PulseTier))
        {
            // Only T3 ever requests real motion (see SidePanelViewModel.RefreshCombatSignals's
            // remarks on why lower/event tiers are static colour only in this implementation phase).
            var isCritical = _vm.SidePanel.PulseTier == MudSharp.Combat.CombatTier.T3;
            _combatPanelPulse?.SetTier(isCritical ? PulseTier.T3 : PulseTier.None);
            UpdateCombatEdges();
        }
        else if (e.PropertyName == nameof(SidePanelViewModel.Live))
        {
            UpdateCombatTickSweep(_vm.SidePanel.Live);
            UpdateCombatEdges();
        }
        else if (e.PropertyName == nameof(SidePanelViewModel.IsCombatMetronomeEnabled))
        {
            // Arming mid-fight joins the fight already in progress rather than waiting for the next
            // one - but on the BEAT, from the fight's own anchor, never from the button press.
            _combatMetronome.SetEnabled(_vm.SidePanel.IsCombatMetronomeEnabled);
            UpdateCombatMetronome();
        }
        else if (e.PropertyName is nameof(SidePanelViewModel.IsCombatPanelVisible)
                                or nameof(SidePanelViewModel.IsCombatGracePeriod)
                                or nameof(SidePanelViewModel.TickPhaseUtc))
        {
            // The window grows/shrinks by the rail's own width on the PROPERTY, not on whatever
            // toggled it. It used to hang off the "$clog on"/"$clog off" handlers, so the rail's own
            // ToggleCombatPanelCommand - which existed but had nothing bound to it - would have shown
            // the panel without making room, taking 300dp straight out of the terminal. Now every
            // route to the property resizes, and there is only one place that knows how.
            if (e.PropertyName == nameof(SidePanelViewModel.IsCombatPanelVisible))
                ResizeWindowForCombatPanel(_vm.SidePanel.IsCombatPanelVisible);

            // Hiding the rail silences the click, and the post-kill grace window is not a fight.
            // Neither of those raises the Live property, so without watching them directly the
            // metronome carried on through both. TickPhaseUtc joins them because it arrives LATER
            // than combat start - the encounter's first swing is what finally reveals the phase -
            // and both the bar and the click have to re-align to it when it does.
            UpdateCombatTickSweep(_vm.SidePanel.Live);
            UpdateCombatEdges();
        }
    }

    /// <summary>Amber while a fight is live. Campbell's normal yellow, NOT the bright yellow the
    /// chat-mode cue on InputFrame uses - the two borders can be lit at once and sit within a couple
    /// of pixels of each other, so identical colours would read as one thick rule rather than as two
    /// separate facts.</summary>
    private static readonly Color CombatEdgeLive = Color.FromArgb("#C19C00");

    /// <summary>The same bright red the tick meter alarms in. A 1px line needs the bright slot to
    /// register at all; the Rail's glow uses the dark red because it is filling a whole panel.</summary>
    private static readonly Color CombatEdgeCritical = Color.FromArgb("#E74856");

    /// <summary>Tracks the colour last applied, so the notification storm (Live republishes on every
    /// combat event, every heartbeat and every 1 Hz tick) does not set the same brush over and over -
    /// each set is a property change on the typing path's own visual tree (Invariant #1).</summary>
    private Color? _combatEdgeColor;

    /// <summary>
    /// Drives the 1px combat rules around the terminal and the input row.
    ///
    /// <para><b>Not during the grace period.</b> InCombat itself already flips false the instant the
    /// last tracked opponent dies or flees, so `Live.InCombat` alone should already exclude the
    /// grace window - the explicit `!IsCombatGracePeriod` here is belt-and-braces against the UI
    /// thread observing that flip a tick behind ClogWriter's own tail-only signal. Either way,
    /// nothing is attacking during grace (the clog is just draining its tail) - and an alarm that
    /// outlives the danger is how an alarm stops being read. Same rule the tick meter and the
    /// metronome already obey.</para>
    ///
    /// <para><b>Red at exactly the Rail's own threshold.</b> The switch is PulseTier == T3, which is
    /// the identical condition driving CombatPanelGlow. Deliberately NOT a stamina number of its own:
    /// two readouts of one state must never disagree about when it changed, and a second threshold
    /// here would drift from the Rail's the first time either was tuned.</para>
    ///
    /// <para><b>No motion, and that is a rule rather than an omission.</b> The design allows at most
    /// one T3 element pulsing at a time (DESIGN_FINAL.md 4.2) - the Rail's glow already owns it.
    /// Three things pulsing on their own phases would be noise, not urgency. These are static colour
    /// only, which is also why they cost the UI thread nothing (Invariant #1).</para>
    /// </summary>
    private void UpdateCombatEdges()
    {
        var panel = _vm.SidePanel;
        var live = panel.Live.InCombat && !panel.IsCombatGracePeriod;
        var color = !live
            ? Colors.Transparent
            : panel.PulseTier == MudSharp.Combat.CombatTier.T3 ? CombatEdgeCritical : CombatEdgeLive;

        if (_combatEdgeColor == color)
            return;
        _combatEdgeColor = color;

        var brush = new SolidColorBrush(color);
        CombatEdgeTerminal.Stroke = brush;
        CombatEdgeInput.Stroke = brush;
    }

    /// <summary>
    /// Runs the Combat Rail's tick meter: sweeping while a fight is live, empty and still otherwise.
    ///
    /// <para>Gated on an actual transition rather than run on every notification, because
    /// <c>Live</c> is republished on every combat event, every FES heartbeat and every 1 Hz tick -
    /// restarting the animation each time would reset the bar to empty several times a second, which
    /// is exactly the "it doesn't animate" symptom seen from the outside.</para>
    ///
    /// <para><b>The bar waits for the anchor.</b> Until the encounter's first swing lands there is no
    /// known phase, so the bar stays empty - exactly the condition the click already obeyed. It used
    /// to sweep anyway from the moment combat started, but the line that flips InCombat is the reply
    /// to the player's own <c>kill</c>, so that phase is the KEYSTROKE'S: a measured median ~1.0 s
    /// away from the real boundary, effectively at random. The bar therefore spent the opening tick
    /// showing a countdown we had invented, then visibly jumped when the real anchor arrived, while
    /// the click sat silent through the same window - two instruments on one panel disagreeing in
    /// front of the player. A blank bar for up to one tick is the honest reading, and it is the price
    /// of the two being one clock rather than two.</para>
    /// </summary>
    private void UpdateCombatTickSweep(Mucka.ViewModels.CombatLiveView live)
    {
        // The visual sweep needs its native layer; the metronome does not, so its update sits outside
        // this guard - the click must not depend on whether a Composition visual happens to exist.
        if (_combatTickSweep is not null)
        {
            // The anchor is the encounter's first SWING, not the moment combat started - see
            // SidePanelViewModel.TickPhaseUtc for why that is worth ~1 s of accuracy. A change of
            // anchor restarts the sweep even if it was already running, because the first swing is
            // usually the moment we learn the phase was wrong.
            var anchor = _vm.SidePanel.TickPhaseUtc;
            var shouldSweep = anchor is not null && TickIsMeaningful();
            if (shouldSweep != _tickSweepRunning || (shouldSweep && anchor != _tickPhaseAnchorUtc))
            {
                _tickSweepRunning = shouldSweep;
                _tickPhaseAnchorUtc = anchor;
                if (shouldSweep)
                    _combatTickSweep.Restart(anchor!.Value);
                else
                    _combatTickSweep.Stop();
            }

            UpdateTickSweepColour(live);
        }

        UpdateCombatMetronome();
    }

    /// <summary>
    /// Whether there is a next swing to count down to - the single condition both renderings of the
    /// tick obey.
    ///
    /// <para>`InCombat` alone should already be that condition - CombatTracker closes the encounter
    /// the instant the last tracked NPC dies or flees, so nothing is left running the moment
    /// `InCombat` flips false. `!IsCombatGracePeriod` is checked anyway as belt-and-braces: the
    /// grace flag now tracks ClogWriter's tail-only state (the clog still draining trailing prose
    /// up to the next prompt), which is purely a logging concern and must never imply anything is
    /// still attacking, even if some future change ever let it outlive InCombat's own flip.</para>
    ///
    /// <para>So during grace there is nothing to time, and a bar counting down to a swing that will
    /// never come is a lie told by an instrument. Same for the click. An earlier version of this ran
    /// the bar through grace anyway, justified by preserving phase - which was wrong twice over, since
    /// resuming calls Restart() and begins at keyframe zero regardless.</para>
    /// </summary>
    private bool TickIsMeaningful()
    {
        var panel = _vm.SidePanel;
        return panel.IsCombatPanelVisible && panel.Live.InCombat && !panel.IsCombatGracePeriod;
    }

    /// <summary>
    /// The audible tick: <see cref="TickIsMeaningful"/> plus the player having armed it. The rail
    /// being on screen is part of that condition because the metronome's only switch is drawn on the
    /// rail - clicking away while the panel is hidden gives the player a noise whose source they
    /// cannot see and cannot silence without first finding the rail in the overflow menu.
    /// </summary>
    private void UpdateCombatMetronome()
    {
        // No anchor means the encounter's first swing has not landed, so the tick's phase is unknown.
        // Silence is the only honest option: the bracket's whole meaning is "either side of the
        // boundary", and clicking either side of a boundary we have guessed at would be theatre.
        var anchor = _vm.SidePanel.TickPhaseUtc;
        var shouldRun = _vm.SidePanel.IsCombatMetronomeEnabled && anchor is not null && TickIsMeaningful();

        if (shouldRun == _metronomeRunning && anchor == _metronomeAnchorUtc)
            return;

        _metronomeRunning = shouldRun;
        _metronomeAnchorUtc = anchor;
        // Stop first even when starting: Start() is idempotent by design, so a running metronome
        // would otherwise ignore a better anchor arriving.
        _combatMetronome.Stop();
        if (shouldRun)
            _combatMetronome.Start(anchor!.Value);
    }

    private void UpdateTickSweepColour(Mucka.ViewModels.CombatLiveView live)
    {
        // The spec's only two exceptions to "the tick carries no colour coding": red at 30 stamina
        // and below. A timer is not a verdict, so nothing else ever recolours it.
        var alarmed = live.InCombat && live.StaminaCurrent is int sta && sta <= 30;
        if (alarmed != _tickSweepAlarmed)
        {
            _tickSweepAlarmed = alarmed;
            CombatTickSweep.Color = alarmed ? Color.FromArgb("#E74856") : Colors.White;
            CombatTickSweep.Opacity = alarmed ? 0.75 : 0.20;
        }
    }

    /// <summary>
    /// Fires once when the Combat Rail's glow layer's native platform view first exists (and again
    /// if the handler is ever recreated) - mirrors OnTerminalHandlerChanged/OnFnButtonHandlerChanged
    /// above. Creates the PulseLayer bound to that native FrameworkElement; DESIGN_FINAL.md 7.1's
    /// PulseLayer sketch needs a real WinUI element to attach a Composition visual to, which a MAUI
    /// BoxView only exposes once its handler has produced one.
    /// </summary>
    private void OnCombatPanelGlowHandlerChanged(object? sender, EventArgs e)
    {
        if (CombatPanelGlow.Handler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement fe)
            _combatPanelPulse = PulseLayer.Attach(fe);
    }

    /// <summary>
    /// Fires once the tick sweep's native view exists. Sizes the element onto the exact track the
    /// canvas draws (<see cref="CombatRailView.TickTrackDp"/> owns that arithmetic - the rail lays
    /// itself out in a fixed 336-unit space scaled to the panel's real width, so a sibling in dp has
    /// to have the same factor applied), then attaches the Composition animator.
    /// </summary>
    private void OnCombatTickSweepHandlerChanged(object? sender, EventArgs e)
    {
        var (left, right, bottom, height) = CombatRailView.TickTrackDp(CombatPanelWidthDp);
        CombatTickSweep.Margin = new Thickness(left, 0, right, bottom);
        CombatTickSweep.HeightRequest = height;

        if (CombatTickSweep.Handler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement fe)
        {
            _combatTickSweep = TickSweep.Attach(fe);
            // The handler can appear after a fight has already started (the panel can be toggled
            // on mid-combat), so adopt the current state rather than waiting for the next
            // transition.
            _tickSweepRunning = false;
            UpdateCombatTickSweep(_vm.SidePanel.Live);
        }
    }

    /// <summary>
    /// Positions the metronome toggle's invisible hit target over the switch the canvas draws, and
    /// takes it out of the tab order.
    ///
    /// <para>The tab-stop removal is the Invariant #0 half of this control. The click itself already
    /// hands focus back (SidePanelViewModel.ToggleCombatMetronome), but a focusable button in the
    /// panel would also be reachable by Tab from the command box, which would strand the keyboard
    /// somewhere the player never asked to go.</para>
    /// </summary>
    private void OnCombatMetronomeHandlerChanged(object? sender, EventArgs e)
    {
        // Same reserved block the canvas draws the switch in, so the target cannot drift off it.
        var (_, _, bottom, _) = CombatRailView.TickTrackDp(CombatPanelWidthDp);
        const double sizeDp = 24.0;
        CombatMetronomeHit.WidthRequest = sizeDp;
        CombatMetronomeHit.HeightRequest = sizeDp;
        CombatMetronomeHit.Margin = new Thickness(0, 0, 6, bottom - (sizeDp / 2.0) + 2);

        if (CombatMetronomeHit.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.Control control)
        {
            control.IsTabStop = false;
            control.UseSystemFocusVisuals = false;
        }

        // Adopt the current setting. Necessary because the metronome is ON by default and a default
        // raises no PropertyChanged, so the handler that normally carries this across would never
        // run and the click would stay silent until the player toggled it off and on again.
        _combatMetronome.SetEnabled(_vm.SidePanel.IsCombatMetronomeEnabled);
        UpdateCombatMetronome();
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

    // ── Hotkeys as KeyboardAccelerators (NOT a per-keystroke handler) ────────
    // The framework matches these natively and invokes our callback only on a hit, so plain
    // typing never runs any of this. Every callback is gated on _isFkeyEditorOpen (the game
    // root's accelerators are still live under the modal editor) and marks itself Handled.
    private void RegisterHotkeyAccelerators(Microsoft.UI.Xaml.UIElement root)
    {
        void Add(Windows.System.VirtualKey key, Windows.System.VirtualKeyModifiers mods, Action action)
        {
            var acc = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = key, Modifiers = mods };
            acc.Invoked += (_, e) =>
            {
                e.Handled = true;                 // we own this combo; don't let it fall through
                if (_isFkeyEditorOpen) return;    // editor is up — hotkeys are inert
                action();
            };
            root.KeyboardAccelerators.Add(acc);
            _accelerators.Add(acc);
        }

        // F1-F12 with no modifier / Shift / Ctrl → macro slots 0-11 / 12-23 / 24-35.
        for (int f = 0; f < 12; f++)
        {
            var key = (Windows.System.VirtualKey)((int)Windows.System.VirtualKey.F1 + f);
            int slot = f;
            Add(key, Windows.System.VirtualKeyModifiers.None,    () => _vm.SendFkeyAbsolute(slot));
            Add(key, Windows.System.VirtualKeyModifiers.Shift,   () => _vm.SendFkeyAbsolute(12 + slot));
            Add(key, Windows.System.VirtualKeyModifiers.Control, () => _vm.SendFkeyAbsolute(24 + slot));
        }

        // Ctrl+D speak dreamword (exits scrollback first if reviewing — dreamwords are
        // time-critical); Ctrl+Shift+D speaks it then chains the typed command (or "sleep");
        // Ctrl+L clear screen; Ctrl+` window selfie.
        Add(Windows.System.VirtualKey.D, Windows.System.VirtualKeyModifiers.Control,
            () => { if (Terminal.IsHistoryMode) Terminal.ScrollToBottom(); _vm.SpeakDreamword(); });
        Add(Windows.System.VirtualKey.D, Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift,
            () => { if (Terminal.IsHistoryMode) Terminal.ScrollToBottom(); _vm.SpeakDreamwordThen(); });
        // Ctrl+F flee; Ctrl+Shift+F flee in the typed direction.
        Add(Windows.System.VirtualKey.F, Windows.System.VirtualKeyModifiers.Control,
            () => { if (Terminal.IsHistoryMode) Terminal.ScrollToBottom(); _vm.Flee(); });
        Add(Windows.System.VirtualKey.F, Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift,
            () => { if (Terminal.IsHistoryMode) Terminal.ScrollToBottom(); _vm.FleeThen(); });
        // Ctrl+W wields the Combat Rail's alternate weapon. Registered as an accelerator like the
        // rest, so it is marked Handled and never reaches any default close-window behaviour.
        Add(Windows.System.VirtualKey.W, Windows.System.VirtualKeyModifiers.Control,
            () => { if (Terminal.IsHistoryMode) Terminal.ScrollToBottom(); _vm.WieldAlternateWeapon(); });
        Add(Windows.System.VirtualKey.Number1, Windows.System.VirtualKeyModifiers.Control,
            () => { if (Terminal.IsHistoryMode) Terminal.ScrollToBottom(); _vm.SendControlAlias(1); });
        Add(Windows.System.VirtualKey.Number2, Windows.System.VirtualKeyModifiers.Control,
            () => { if (Terminal.IsHistoryMode) Terminal.ScrollToBottom(); _vm.SendControlAlias(2); });
        Add(Windows.System.VirtualKey.Number3, Windows.System.VirtualKeyModifiers.Control,
            () => { if (Terminal.IsHistoryMode) Terminal.ScrollToBottom(); _vm.SendControlAlias(3); });
        Add(Windows.System.VirtualKey.Number4, Windows.System.VirtualKeyModifiers.Control,
            () => { if (Terminal.IsHistoryMode) Terminal.ScrollToBottom(); _vm.SendControlAlias(4); });
        Add(Windows.System.VirtualKey.Number5, Windows.System.VirtualKeyModifiers.Control,
            () => { if (Terminal.IsHistoryMode) Terminal.ScrollToBottom(); _vm.SendControlAlias(5); });
        Add(Windows.System.VirtualKey.L, Windows.System.VirtualKeyModifiers.Control, () => _vm.ClearScreen());
        Add((Windows.System.VirtualKey)0xC0, Windows.System.VirtualKeyModifiers.Control, () => _ = TakeSelfieAsync());
        // Ctrl+R replies to the last person who sent us a tell (issue #147). Prefills the input
        // rather than sending the game's 're' command, since 're' re-resolves its target
        // server-side at send time — a second tell arriving mid-keystroke can redirect it.
        Add(Windows.System.VirtualKey.R, Windows.System.VirtualKeyModifiers.Control,
            () => { if (Terminal.IsHistoryMode) Terminal.ScrollToBottom(); ReplyToLastTell(); });

        // PageUp/PageDown scroll history; PageUp from live enters scrollback. These stay live in
        // both modes (the scrollback handler, when attached, handles them before accelerators).
        Add(Windows.System.VirtualKey.PageUp,   Windows.System.VirtualKeyModifiers.None, () => Terminal.ScrollByPages(1));
        Add(Windows.System.VirtualKey.PageDown, Windows.System.VirtualKeyModifiers.None, () => Terminal.ScrollByPages(-1));
    }

    private void UnregisterHotkeyAccelerators()
    {
        if (_rootElement is not null)
            foreach (var acc in _accelerators)
                _rootElement.KeyboardAccelerators.Remove(acc);
        _accelerators.Clear();
        _rootElement = null;
    }

    // Scrollback-only key handler. Attached to the window root ONLY while reviewing history
    // (OnHistoryModeChanged) and detached on exit, so it is NEVER in the live typing path.
    // Handles scroll/copy/exit keys and swallows everything else (no typing in scrollback).
    private void OnScrollbackKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        var key = e.Key;
        if (IsModifierKey(key)) return;   // lone modifiers pass through harmlessly
        bool ctrl = (GetKeyState((int)Windows.System.VirtualKey.Control) & 0x8000) != 0;

        if (ctrl && key is Windows.System.VirtualKey.Number1
            or Windows.System.VirtualKey.Number2
            or Windows.System.VirtualKey.Number3
            or Windows.System.VirtualKey.Number4
            or Windows.System.VirtualKey.Number5)
        {
            Terminal.ScrollToBottom();
            TrySendControlAlias(key);
            e.Handled = true;
            return;
        }
        if (ctrl && key == Windows.System.VirtualKey.C)
        {
            if (Terminal.CopySelectionToClipboard()) ShowCopiedToast();
            e.Handled = true;
            return;
        }
        bool shift = (GetKeyState((int)Windows.System.VirtualKey.Shift) & 0x8000) != 0;
        if (ctrl && key == Windows.System.VirtualKey.D)
        {
            Terminal.ScrollToBottom();
            if (shift) _vm.SpeakDreamwordThen(); else _vm.SpeakDreamword();
            e.Handled = true;
            return;
        }
        if (ctrl && key == Windows.System.VirtualKey.F)
        {
            Terminal.ScrollToBottom();
            if (shift) _vm.FleeThen(); else _vm.Flee();
            e.Handled = true;
            return;
        }
        switch (key)
        {
            case Windows.System.VirtualKey.PageUp:   Terminal.ScrollByPages(1);  e.Handled = true; return;
            case Windows.System.VirtualKey.PageDown: Terminal.ScrollByPages(-1); e.Handled = true; return;
            case Windows.System.VirtualKey.Home:     Terminal.ScrollToTop();     e.Handled = true; return;
            case Windows.System.VirtualKey.End:
            case Windows.System.VirtualKey.Escape:   Terminal.ScrollToBottom();  e.Handled = true; return;
        }
        e.Handled = true;   // swallow all other keys — input box is hidden in scrollback
    }

    private void OnSendButtonClicked(object? sender, EventArgs e)
    {
        // Mirror the Enter path: the native TextBox owns the text while typing.
        if (_inputTextBox is not null)
            _vm.InputText = _inputTextBox.Text;
        _vm.SendCommand.Execute(null);
    }

    private void OnInputHandlerChanged(object? sender, EventArgs e)
    {
        if (_inputTextBox != null)
        {
            _inputTextBox.PreviewKeyDown -= OnInputPreviewKeyDown;
            _inputTextBox.SelectionChanged -= OnInputSelectionChanged;
            _inputTextBox = null;
            _inputScroller = null;
        }
        if (InputEntry.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.TextBox tb)
        {
            _inputTextBox = tb;
            tb.PreviewKeyDown += OnInputPreviewKeyDown;
            tb.SelectionChanged += OnInputSelectionChanged;
            // Shadow the ReturnCommand with a local null so MAUI's KeyDown handler
            // (registered with handledEventsToo:true) sees null and skips execution.
            // Do NOT use RemoveBinding — that fires PropertyChanged which causes MAUI's
            // binding infrastructure to re-evaluate and re-apply the XAML binding,
            // restoring ReturnCommand=SendCommand and producing a second send.
            // A plain null SetValue shadows the live binding without disturbing it.
            InputEntry.ReturnCommand = null;
            // Remove the Text binding entirely on Windows and drive the native TextBox by hand.
            // The XAML binding is TwoWay (Android/Mac rely on it), and a runtime SetBinding(OneWay)
            // was proven NOT to stick — INPUT_DIAG showed VM.InputText written on EVERY keystroke,
            // i.e. the binding still round-trips TextBox→VM. That feedback loop is the bug: when a
            // type-then-Enter happens faster than the round-trip settles (~10ms), SendNow's clear
            // races the in-flight keystroke write-back, the write-back lands last, and the last
            // character is re-stranded in the box ("n"+Enter → next "n" appends → "nn").
            // With no binding, typed text never touches the VM; we read it directly on Enter, and
            // the VM pushes to the box only on deliberate changes (clear/history/Escape) via
            // OnVmPropertyChanged — all synchronous on the UI thread, so there is no loop to race.
            InputEntry.RemoveBinding(Entry.TextProperty);
            tb.Text = _vm.InputText;   // seed the initial value (e.g. a prefilled account id)
            InputDiag.Log($"OnInputHandlerChanged: Text binding removed; driving TextBox manually; seed=\"{tb.Text}\"; ReturnCommand null={InputEntry.ReturnCommand is null}");
#if INPUT_DIAG
            tb.TextChanged += OnInputDiagTextChanged;
#endif
        }
    }

    // WinUI's TextBox stops bringing the caret into view after another window in the
    // app (the $con raw console) has been activated and focus returns to this one —
    // typed text appends past the right edge and the box stays anchored left. The
    // internal auto-scroll never recovers, so track the caret ourselves: whenever the
    // caret sits at the end of the text (the typing case), pin the inner ScrollViewer
    // to its right edge. A no-op while the native behaviour works, and it doesn't
    // disturb selections or mid-text caret moves.
    private void OnInputSelectionChanged(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        // UpdateLayout() is only needed after WinUI's native caret-follow has been broken
        // by activating an auxiliary window. Skip entirely until that has happened.
        if (!_auxiliaryWindowOpened) return;
        var tb = _inputTextBox;
        if (tb is null || tb.SelectionLength != 0 || tb.SelectionStart < tb.Text.Length)
            return;
        _inputScroller ??= FindChildScrollViewer(tb);
        if (_inputScroller is null) return;
        // The text change that moved the caret may not be measured yet; settle layout
        // so ScrollableWidth includes the new character before pinning to the edge.
        tb.UpdateLayout();
        if (_inputScroller.ScrollableWidth > 0)
            _inputScroller.ChangeView(_inputScroller.ScrollableWidth, null, null, disableAnimation: true);
    }

    private static Microsoft.UI.Xaml.Controls.ScrollViewer? FindChildScrollViewer(Microsoft.UI.Xaml.DependencyObject root)
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is Microsoft.UI.Xaml.Controls.ScrollViewer sv)
                return sv;
            if (FindChildScrollViewer(child) is { } nested)
                return nested;
        }
        return null;
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
        await OpenConfigAsync(initialTab: 2);   // straight to the Hotkeys tab

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
        if (Terminal.TryActivateSpanInsert((float)pt.Position.X, (float)pt.Position.Y))
        {
            e.Handled = true;
            return;
        }
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
        InputDiag.Log($"KeyDown {e.Key}");
        // Mark the keyboard as recently active so a stray touchpad tap in the next 300 ms does
        // not trip scrollback. (This is the box's own handler — the only managed key code in the
        // live typing path — so a plain letter falls straight through after this one cheap set.)
        if (!IsModifierKey(e.Key))
            Terminal.NotifyKeyPressed();
        if ((GetKeyState((int)Windows.System.VirtualKey.Control) & 0x8000) != 0
            && TrySendControlAlias(e.Key))
        {
            e.Handled = true;
            return;
        }
        if (e.Key == Windows.System.VirtualKey.Up)
        {
            _vm.HistoryUpCommand.Execute(null);
            // WinUI resets the cursor to position 0 when TextBox.Text is set programmatically;
            // move it back to the end so the user can edit the recalled command immediately.
            if (_inputTextBox is not null)
                _inputTextBox.SelectionStart = _inputTextBox.Text.Length;
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Down)
        {
            _vm.HistoryDownCommand.Execute(null);
            if (_inputTextBox is not null)
                _inputTextBox.SelectionStart = _inputTextBox.Text.Length;
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            // Close the About overlay if it is open; otherwise clear the input box.
            // Clear the NATIVE TextBox directly: typing no longer flows into the VM (binding
            // removed), so _vm.InputText is usually already "" while the box holds text — setting
            // it to "" would be a no-op that never reaches the box. Clear the box, sync the VM.
            if (!TryCloseAbout() && _inputTextBox is not null)
            {
                _inputTextBox.Text = string.Empty;
                _vm.InputText = string.Empty;
            }
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Enter)
        {
            // No Text binding on Windows — the native TextBox owns its text while typing.
            // Read the authoritative value directly, then send (SendNow clears via the VM,
            // which pushes the empty string back into the box through OnVmPropertyChanged).
            if (_inputTextBox is not null)
                _vm.InputText = _inputTextBox.Text;
            _vm.SendCommand.Execute(null);
            e.Handled = true;
        }
    }

    private bool TrySendControlAlias(Windows.System.VirtualKey key)
    {
        var slot = key switch
        {
            Windows.System.VirtualKey.Number1 => 1,
            Windows.System.VirtualKey.Number2 => 2,
            Windows.System.VirtualKey.Number3 => 3,
            Windows.System.VirtualKey.Number4 => 4,
            Windows.System.VirtualKey.Number5 => 5,
            _ => 0,
        };
        if (slot == 0)
            return false;

        _vm.SendControlAlias(slot);
        return true;
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
        _auxiliaryWindowOpened = true;
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

    private void OnMapPanelRequested()
    {
        _auxiliaryWindowOpened = true;
        // Reuse existing window if it is still open.
        if (_mapWindow != null &&
            Application.Current?.Windows.Contains(_mapWindow) == true)
            return;
        _mapWindow = new Window(new MappingPage(_vm))
        {
            Title  = "Mucka — Mapping",
            Width  = 900,
            Height = 550,
        };
        Application.Current?.OpenWindow(_mapWindow);
    }

    /// <summary>True exactly while the window is currently carrying the rail's extra width, and the
    /// width it had before that was added. Together they make <see cref="ResizeWindowForCombatPanel"/>
    /// idempotent AND exactly reversible.</summary>
    private bool _railWidthApplied;
    private int _widthWithoutRailPx;

    /// <summary>
    /// Grows or shrinks the window by exactly CombatPanelWidthDp so the rail's space appears and
    /// disappears without the terminal's column count or the left panel's width changing as a side
    /// effect (DESIGN_FINAL.md D3). This is the ONLY place the window resizes for a combat-panel
    /// reason; it never resizes on combat start/end.
    ///
    /// <para><b>Restores a remembered width rather than subtracting.</b> It used to add a delta on
    /// the way in and subtract one on the way out, which is only reversible if the subtraction is
    /// never clamped - and it is clamped, by <c>_minWindowWidthPx</c>. A window already at its
    /// minimum therefore grew by the rail's width on every show and shrank by nothing on every hide,
    /// so the width ratcheted up once per toggle and never came back down. That was survivable while
    /// the only way to toggle was typing "$clog on"; it is not survivable now the toggle is a menu
    /// item next to Onlines and Compass.</para>
    ///
    /// <para>Idempotent for the same reason: a duplicate or spurious notification must not be able to
    /// add the width twice. The guard is on what the WINDOW is currently carrying, not on the
    /// view-model flag, because those are what can disagree.</para>
    /// </summary>
    private void ResizeWindowForCombatPanel(bool showing)
    {
        if (_hwnd == IntPtr.Zero) return;
        if (showing == _railWidthApplied) return;
        var nativeWindow = Window?.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
        if (nativeWindow is null) return;

        var dpi = GetDpiForWindow(_hwnd);
        var deltaPx = (int)Math.Round(CombatPanelWidthDp * dpi / 96.0);
        var appWindow = nativeWindow.AppWindow;

        int targetWidth;
        if (showing)
        {
            // Remembered on the way in so the way out is an exact restore, whatever the user has
            // done to the window in between.
            _widthWithoutRailPx = appWindow.Size.Width;
            targetWidth = _widthWithoutRailPx + deltaPx;
        }
        else
        {
            targetWidth = _widthWithoutRailPx > 0
                ? _widthWithoutRailPx
                : Math.Max(_minWindowWidthPx, appWindow.Size.Width - deltaPx);
            _widthWithoutRailPx = 0;
        }
        _railWidthApplied = showing;

        if (targetWidth < _minWindowWidthPx)
            targetWidth = _minWindowWidthPx;
        if (appWindow.Size.Width != targetWidth)
            appWindow.Resize(new Windows.Graphics.SizeInt32(targetWidth, appWindow.Size.Height));

        // Same race as OnSidePanelPropertyChanged's left-panel resize above: the resize itself can
        // land keyboard focus elsewhere after the toggle command's own RequestFocus already fired. Re-assert once
        // the resize/re-layout has settled (Invariant #0).
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(50), () =>
        {
            if (!_isFkeyEditorOpen && !Terminal.IsHistoryMode && !InputEntry.IsFocused)
                _inputTextBox?.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
        });
    }

#if INPUT_DIAG
    // ── INPUT_DIAG: UI-thread responsiveness probe ───────────────────────────
    // Posts a 16ms heartbeat on the WinUI DispatcherQueue (the same thread the TextBox paints
    // on). When the thread is busy — a terminal repaint, a layout/composite pass from the
    // side-panel fade timer, a per-keystroke binding round-trip — the heartbeat can't fire on
    // schedule, so the measured gap spikes. Any gap well over 16ms that lines up (in the +ms
    // column of mucka-input.txt) with a keystroke is the smoking gun for that stall.
    private void StartUiThreadProbe()
    {
        var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (dq is null) { InputDiag.Log("UI PROBE: no DispatcherQueue on this thread"); return; }
        _uiProbeLast = DateTime.UtcNow;
        _uiProbeTimer = dq.CreateTimer();
        _uiProbeTimer.Interval = TimeSpan.FromMilliseconds(16);
        _uiProbeTimer.IsRepeating = true;
        _uiProbeTimer.Tick += OnUiProbeTick;
        _uiProbeTimer.Start();
        InputDiag.Log("UI PROBE: started (16ms heartbeat; logging gaps > 33ms)");
    }

    private void OnUiProbeTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        var now = DateTime.UtcNow;
        var gapMs = (now - _uiProbeLast).TotalMilliseconds;
        _uiProbeLast = now;
        // 16ms nominal; anything past ~2 intervals means the thread was blocked. Threshold 33ms.
        if (gapMs > 33.0)
            InputDiag.Log($"UI STALL {gapMs:F0}ms (heartbeat blocked; nominal 16ms)");
    }

    private void StopUiThreadProbe()
    {
        if (_uiProbeTimer is null) return;
        _uiProbeTimer.Stop();
        _uiProbeTimer.Tick -= OnUiProbeTick;
        _uiProbeTimer = null;
        InputDiag.Log("UI PROBE: stopped");
        InputDiag.Flush();
    }

    // Timeline marker: when the native TextBox actually commits a typed character. Correlate the
    // +ms of this line against the OnInputPreviewKeyDown/OnRootPreviewKeyDown KEY lines for the
    // same character — a large key→TextChanged delta means the keystroke itself was delayed.
    private void OnInputDiagTextChanged(object sender, Microsoft.UI.Xaml.Controls.TextChangedEventArgs e)
    {
        // Log only the length — NOT the full text. Interpolating the whole box contents on every
        // keystroke allocated a growing string per char, adding GC pressure the real (non-diag)
        // app never has, which would itself perturb the latency we're trying to measure.
        var tb = (Microsoft.UI.Xaml.Controls.TextBox)sender;
        InputDiag.Log("TextChanged len=" + tb.Text.Length);
    }
#endif
#endif
    private void OnDisconnected()
    {
        _antiIdleTimer?.Stop();
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
