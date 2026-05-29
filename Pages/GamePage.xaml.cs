using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Mucka.Helpers;
using Mucka.ViewModels;
using MudSharp.Models;

namespace Mucka.Pages;

public partial class GamePage : ContentPage
{
    private readonly GameViewModel _vm;
    private readonly bool _exitOnDisconnect;
    private IDispatcherTimer?      _flushTimer;

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

    // Lines pulled from the ViewModel queue but not yet successfully injected.
    // Kept here so they aren't lost if the WebView isn't ready yet.
    private readonly List<StyledLine> _pendingInjection = new();
    // Re-entrancy guard: prevents the 50ms timer from firing a second injection
    // while the first EvaluateJavaScriptAsync/ExecuteScriptAsync is still awaiting.
    private bool _injecting;
    private bool _isFkeyEditorOpen;
    private bool _eventsSubscribed;
    private readonly SemaphoreSlim _scriptExecutionLock = new(1, 1);
#if WINDOWS
    private Window? _rawConsoleWindow;
    private Microsoft.UI.Xaml.Controls.TextBox? _inputTextBox;
    // ── Window minimum-size enforcement ─────────────────────────────────────
    // Must match the WidthRequest of the side-panel Border in GamePage.xaml.
    private const double SidePanelWidthDp = 260.0;
    private int              _minWindowWidthPx;
    private int              _panelAutoAddedPx;
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

        DeviceDisplay.Current.KeepScreenOn = _vm.KeepScreenOn;

        if (_flushTimer == null)
        {
            // Unsubscribe before subscribing to guard against any double-subscribe scenario.
            if (!_eventsSubscribed)
            {
                _vm.Disconnected        += OnDisconnected;
                _vm.RequestFocus        += FocusInput;
                _vm.EditFkeysRequested  += OnEditFkeysRequested;
                _vm.ConfigRequested     += OnConfigRequested;
                _vm.ClearScreenRequested += OnClearScreenRequested;
                _eventsSubscribed = true;
            }

            ScrollbackWebView.Source = new HtmlWebViewSource { Html = HtmlScrollback.GeneratePage(_vm.FontSize) };
            ScrollbackWebView.Navigating += OnScrollbackNavigating;
            ScrollbackWebView.Navigated += OnScrollbackNavigated;
            ScrollbackWebView.Focused += OnScrollbackFocused;

            _flushTimer = Dispatcher.CreateTimer();
            _flushTimer.Interval = TimeSpan.FromMilliseconds(50);
            _flushTimer.Tick += OnFlushTick;
            _flushTimer.Start();

            if (Window is not null)
                Window.Activated += OnWindowActivated;

#if WINDOWS
            // Hook window root for F1-F12 physical key events (fires regardless of focus).
            if (Window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window fwin &&
                fwin.Content is Microsoft.UI.Xaml.UIElement froot)
                froot.PreviewKeyDown += OnRootPreviewKeyDown;
            // Hook the native TextBox so Up/Down/Esc keys work in the entry.
            InputEntry.HandlerChanged += OnInputHandlerChanged;
            _vm.OpenRawConsoleRequested += OnOpenRawConsoleRequested;
            // Enforce minimum window width based on the configured terminal columns.
            _vm.SidePanel.PropertyChanged += OnSidePanelPropertyChanged;
            SetupWindowMinimumSize();
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
            // knows not to reinitialize the WebView or re-hook events on return.
            _flushTimer?.Stop();
            return;
        }

        DeviceDisplay.Current.KeepScreenOn = false;
        _flushTimer?.Stop();
        _flushTimer = null;
        ScrollbackWebView.Navigating -= OnScrollbackNavigating;
        ScrollbackWebView.Navigated -= OnScrollbackNavigated;
        ScrollbackWebView.Focused -= OnScrollbackFocused;
        _vm.Disconnected        -= OnDisconnected;
        _vm.RequestFocus        -= FocusInput;
        _vm.EditFkeysRequested  -= OnEditFkeysRequested;
        _vm.ConfigRequested     -= OnConfigRequested;
        _vm.ClearScreenRequested -= OnClearScreenRequested;
        _eventsSubscribed = false;
        if (Window is not null)
            Window.Activated -= OnWindowActivated;
#if WINDOWS
        if (Window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window fwin &&
            fwin.Content is Microsoft.UI.Xaml.UIElement froot)
            froot.PreviewKeyDown -= OnRootPreviewKeyDown;
        if (_inputTextBox != null)
        {
            _inputTextBox.PreviewKeyDown -= OnInputPreviewKeyDown;
            _inputTextBox = null;
        }
        InputEntry.HandlerChanged -= OnInputHandlerChanged;
        _vm.OpenRawConsoleRequested -= OnOpenRawConsoleRequested;
        _vm.SidePanel.PropertyChanged -= OnSidePanelPropertyChanged;
        TeardownWindowMinimumSize();
#endif
        _ = _vm.DisposeAsync();
    }

    /// <summary>
    /// Intercept mucka:// navigation messages sent from the WebView's scroll-detection script.
    /// mucka://scroll/pause  — user has scrolled away from the bottom; enter scroll mode.
    /// mucka://scroll/resume — user has returned to the bottom (or pressed ESC); exit scroll mode.
    /// </summary>
    private void OnScrollbackNavigating(object? sender, WebNavigatingEventArgs e)
    {
        if (!e.Url.StartsWith("mucka://scroll/", StringComparison.Ordinal))
            return;

        e.Cancel = true;
        _vm.IsScrollMode = e.Url == "mucka://scroll/pause";
    }

    private void OnFlushTick(object? sender, EventArgs e)
    {
        _vm.AntiIdleTick();

        if (_injecting) return;   // previous injection still in flight — pick up lines next tick

        // Move new lines from the ViewModel queue into our local buffer.
        var newLines = _vm.FlushPendingLines();
        if (newLines != null) _pendingInjection.AddRange(newLines);

        // While in scroll mode, buffer lines but don't inject them into the WebView.
        if (_vm.IsScrollMode) return;

        if (_pendingInjection.Count == 0) return;

        _injecting = true;
        ScheduleInjection();
    }

    private void ScheduleInjection()
    {
#if WINDOWS
        // Low priority lets queued keyboard events drain before the JS string-building
        // work runs, preventing key-repeat stutter when game output arrives.
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()
            .TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, RunInjectionAsync);
#else
        _ = RunInjectionTaskAsync();
#endif
    }

#if WINDOWS
    private async void RunInjectionAsync()
#else
    private async Task RunInjectionTaskAsync()
#endif
    {
        if (_pendingInjection.Count == 0) { _injecting = false; return; }
        var lines    = _pendingInjection.ToList();
        var atBottom = !_vm.IsScrollMode;
        try
        {
            var script = await Task.Run(() => BuildInjectionScript(lines, atBottom));
            await ExecuteScriptAsync(script);
            _pendingInjection.Clear();
        }
        catch
        {
            // WebView2 not ready yet — lines stay in _pendingInjection, retry next tick.
        }
        finally
        {
            _injecting = false;
        }
    }

    private static string BuildInjectionScript(IReadOnlyList<StyledLine> lines, bool atBottom)
    {
        var sb = new StringBuilder(lines.Count * 100);
        sb.Append("(function(){var o=document.getElementById('out');if(!o)return;");

        foreach (var line in lines)
        {
            if (line.PlainText.Contains('\f'))
            {
                sb.Append("o.innerHTML='';");
                continue;
            }
            var html     = HtmlScrollback.LineToHtml(line);
            var cls      = line.IsPartial ? "lnp" : "ln";
            var jsonHtml = JsonSerializer.Serialize($"<span class='{cls}'>{html}\u200b</span>");
            if (line.IsPartial)
            {
                // Replace existing partial span or append a new one.
                var innerJson = JsonSerializer.Serialize(html + "\u200b");
                sb.Append($"(function(){{var p=o.querySelector('.lnp');if(p){{p.innerHTML={innerJson};}}else{{o.insertAdjacentHTML('beforeend',{jsonHtml});}}}})();");
            }
            else if (string.IsNullOrEmpty(html))
            {
                // Empty complete line (e.g. blank Enter): if there is a live partial prompt,
                // just promote it — do not insert a blank line between consecutive prompts.
                // If there is no partial, insert the blank line as normal paragraph spacing.
                sb.Append($"(function(){{var p=o.querySelector('.lnp');if(p){{p.className='ln';}}else{{o.insertAdjacentHTML('beforeend',{jsonHtml});}}}})();");
            }
            else
            {
                // Non-empty complete line: if there is a live partial prompt, merge this line's
                // content into it (prompt + echo on one line) and promote to .ln.
                // If there is no partial, append as a new line.
                var innerJson = JsonSerializer.Serialize(html + "\u200b");
                sb.Append($"(function(){{var p=o.querySelector('.lnp');if(p){{p.insertAdjacentHTML('beforeend',{innerJson});p.className='ln';}}else{{o.insertAdjacentHTML('beforeend',{jsonHtml});}}}})();");
            }
        }

        // Trim to 120 permanent lines.
        sb.Append("while(o.querySelectorAll('.ln').length>120){var f=o.querySelector('.ln');if(f)f.remove();else break;}");
        sb.Append("})();");
        if (atBottom)
            sb.Append("window.scrollTo(0,document.body.scrollHeight);");
        return sb.ToString();
    }

    /// <summary>
    /// Exit scroll mode: re-enable auto-scroll and scroll immediately to the bottom.
    /// </summary>
    private async Task ExitScrollModeAsync()
    {
        try
        {
            await ExecuteScriptAsync("window._atBottom=true;(function(){var s=document.scrollingElement||document.documentElement||document.body;s.scrollTop=s.scrollHeight;})();");
            _vm.IsScrollMode = false;
        }
        catch
        {
            // WebView not ready — leave scroll mode enabled until we can scroll to the bottom.
        }
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
    }


    /// <summary>
    /// Called when the user taps the scroll-mode banner — exits scroll mode and returns focus to input.
    /// </summary>
    private void OnScrollModeBannerTapped(object? sender, TappedEventArgs e)
    {
        _ = ExitScrollModeAsync();
        FocusInput();
    }

    /// <summary>
    /// Called when the input entry gains focus — exits scroll mode if active.
    /// </summary>
    private void OnInputEntryFocused(object? sender, FocusEventArgs e)
    {
        if (_vm.IsScrollMode)
            _ = ExitScrollModeAsync();
    }

    /// <summary>
    /// Execute JavaScript reliably across platforms.
    /// On WinUI, MAUI's EvaluateJavaScriptAsync silently fails for HtmlWebViewSource pages;
    /// we go directly to the CoreWebView2 API instead.
    /// </summary>
    private async Task ExecuteScriptAsync(string script)
    {
        await _scriptExecutionLock.WaitAsync();
        try
        {
#if WINDOWS
            if (ScrollbackWebView.Handler?.PlatformView is
                Microsoft.UI.Xaml.Controls.WebView2 wv2)
            {
                if (wv2.CoreWebView2 is null)
                    await wv2.EnsureCoreWebView2Async();
                await (wv2.CoreWebView2 ?? throw new InvalidOperationException("CoreWebView2 unavailable")).ExecuteScriptAsync(script);
                return;
            }
#endif
            await ScrollbackWebView.EvaluateJavaScriptAsync(script);
        }
        finally
        {
            _scriptExecutionLock.Release();
        }
    }

    private Task OpenConfigAsync(int initialTab)
    {
        Func<string[], Task>? onSave = _vm.CanSaveFkeys
            ? fkeys => _vm.SaveFkeysAsync(fkeys)
            : null;
        var editorVm = new FkeyEditorViewModel(
            _vm.GetAllFkeys(),
            _vm.FontSize, _vm.MaxColumns, _vm.Volume,
            _vm.StatUpdateFrequency,
            _vm.ApplyFkeys,
            onSave,
            _vm.ApplyMaxColumns,
            _vm.ApplyStatUpdateFrequency,
            muteBeepSession: _vm.MuteBeepSession,
            muteBeepPermanently: _vm.MuteBeepPermanently,
            onMuteSessionApplied: b => _vm.MuteBeepSession = b,
            onMutePermanentlyApplied: b => _vm.ApplyMutePermanently(b))
        {
            ActiveTab = initialTab
        };
        _isFkeyEditorOpen = true;
        return Navigation.PushModalAsync(new FkeyEditorPage(editorVm));
    }

    private async void OnEditFkeysRequested() => await OpenConfigAsync(initialTab: 0);

    private async void OnConfigRequested() => await OpenConfigAsync(initialTab: 0);

    private void FocusInput() => InputEntry.Focus();

    // Redirect focus back to the typing box whenever the WebView captures it
    // (e.g. on initial load or when the user clicks the scrollback area).
    private void OnScrollbackFocused(object? sender, FocusEventArgs e) => FocusInput();

    // Focus the typing box once the WebView finishes its initial page load
    // (WebView2 grabs focus during first load; this wins it back).
    private void OnScrollbackNavigated(object? sender, WebNavigatedEventArgs e)
    {
        if (e.Result == WebNavigationResult.Success)
            FocusInput();
    }

    // Re-focus the typing box when the window regains activation (Alt+Tab back, etc.).
    private void OnWindowActivated(object? sender, EventArgs e) => FocusInput();

    private async void OnClearScreenRequested()
    {
        try { await ExecuteScriptAsync("document.getElementById('out').innerHTML=''"); }
        catch { /* ignore: WebView may not be ready */ }
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
    /// Recomputes the required minimum window width (in physical pixels) and adjusts the
    /// window size accordingly.
    /// <para>
    /// When <paramref name="panelToggled"/> is <c>false</c> (initial setup) the window is
    /// only grown, never shrunk.  When <paramref name="panelToggled"/> is <c>true</c> the
    /// full expand/collapse behaviour is applied: the window grows to fit when the panel
    /// opens and shrinks by the panel width when the panel closes.
    /// </para>
    /// </summary>
    private void UpdateWindowMinimumWidth(bool panelToggled = false)
    {
        if (_hwnd == IntPtr.Zero) return;
        var nativeWindow = Window?.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
        if (nativeWindow is null) return;

        var panelExpanded = _vm.SidePanel.IsPanelExpanded;
        var dpi       = GetDpiForWindow(_hwnd);
        var textMinPx = (int)Math.Ceiling(_vm.MaxColumns * CharWidthDp * dpi / 96.0);
        var panelPx   = (int)Math.Ceiling(SidePanelWidthDp * dpi / 96.0);
        _minWindowWidthPx = panelExpanded ? textMinPx + panelPx : textMinPx;

        var appWindow    = nativeWindow.AppWindow;
        var currentWidth = appWindow.Size.Width;

        if (panelExpanded)
        {
            // Panel is open: grow the window if it is too narrow, and record how much we added.
            if (currentWidth < _minWindowWidthPx)
            {
                _panelAutoAddedPx = _minWindowWidthPx - currentWidth;
                appWindow.Resize(new Windows.Graphics.SizeInt32(_minWindowWidthPx, appWindow.Size.Height));
            }
            else
            {
                _panelAutoAddedPx = 0;
            }
        }
        else if (panelToggled)
        {
            // Panel just closed: remove only what was auto-added when it opened,
            // but never shrink below the text-only minimum.
            var targetWidth = Math.Max(currentWidth - _panelAutoAddedPx, textMinPx);
            if (targetWidth != currentWidth)
                appWindow.Resize(new Windows.Graphics.SizeInt32(targetWidth, appWindow.Size.Height));
            _panelAutoAddedPx = 0;
        }
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
            UpdateWindowMinimumWidth(panelToggled: true);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    private void OnRootPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (_isFkeyEditorOpen) return;
        var key = e.Key;
        bool ctrl  = (GetKeyState((int)Windows.System.VirtualKey.Control) & 0x8000) != 0;
        bool shift = (GetKeyState((int)Windows.System.VirtualKey.Shift)   & 0x8000) != 0;

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
            // Null the ReturnCommand so MAUI's KeyDown handler (registered with
            // handledEventsToo:true) doesn't fire a second send after our PreviewKeyDown.
            // Enter is handled exclusively via OnInputPreviewKeyDown on Windows.
            InputEntry.ReturnCommand = null;
        }
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
            _vm.InputText = string.Empty;
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Enter)
        {
            // Handle Enter here via the tunneling PreviewKeyDown so MAUI's KeyDown-based
            // ReturnCommand handler never fires. That handler triggers the Send button's
            // visual pressed state, introducing a brief gap that lets fast typists race
            // their next keystroke in before the input box is cleared.
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
