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
    private bool _isConfirmingDisconnect;
    private readonly SemaphoreSlim _scriptExecutionLock = new(1, 1);

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

        if (_flushTimer == null)
        {
            _vm.Disconnected += OnDisconnected;
            _vm.RequestFocus += FocusInput;
            _vm.EditFkeysRequested += OnEditFkeysRequested;
            _vm.ClearScreenRequested += OnClearScreenRequested;

            ScrollbackWebView.Source = new HtmlWebViewSource { Html = HtmlScrollback.InitialPage };
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
#endif
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

    protected override bool OnBackButtonPressed()
    {
        // If the fkey editor is open, allow back button to close it (it's handled by the page itself)
        if (_isFkeyEditorOpen)
            return false;

        // If in game mode, prompt for confirmation before disconnecting
        if (_vm.IsInGameMode && !_isConfirmingDisconnect)
        {
            _ = ConfirmDisconnectAsync();
            return true; // Consume the back button press
        }

        return false; // Allow default back behavior
    }

    private async Task ConfirmDisconnectAsync()
    {
        _isConfirmingDisconnect = true;
        try
        {
            var result = await DisplayAlertAsync(
                "Disconnect?",
                "You are in the game. Do you want to disconnect?",
                "Disconnect",
                "Cancel");

            if (result)
            {
                // User confirmed disconnect — proceed with navigation
                await Navigation.PopAsync();
            }
        }
        finally
        {
            _isConfirmingDisconnect = false;
        }
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
            return;

        _flushTimer?.Stop();
        _flushTimer = null;
        ScrollbackWebView.Navigating -= OnScrollbackNavigating;
        ScrollbackWebView.Navigated -= OnScrollbackNavigated;
        ScrollbackWebView.Focused -= OnScrollbackFocused;
        _vm.Disconnected -= OnDisconnected;
        _vm.RequestFocus -= FocusInput;
        _vm.EditFkeysRequested -= OnEditFkeysRequested;
        _vm.ClearScreenRequested -= OnClearScreenRequested;
        if (Window is not null)
            Window.Activated -= OnWindowActivated;
#if WINDOWS
        if (Window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window fwin &&
            fwin.Content is Microsoft.UI.Xaml.UIElement froot)
            froot.PreviewKeyDown -= OnRootPreviewKeyDown;
        InputEntry.HandlerChanged -= OnInputHandlerChanged;
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

    private async void OnFlushTick(object? sender, EventArgs e)
    {
        if (_injecting) return;   // previous injection still in flight — pick up lines next tick

        // Move new lines from the ViewModel queue into our local buffer.
        var newLines = _vm.FlushPendingLines();
        if (newLines != null) _pendingInjection.AddRange(newLines);

        // While in scroll mode, buffer lines but don't inject them into the WebView.
        if (_vm.IsScrollMode) return;

        if (_pendingInjection.Count == 0) return;

        _injecting = true;
        try
        {
            await InjectLinesAsync(_pendingInjection);
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

    /// <summary>
    /// Inject a batch of lines using insertAdjacentHTML — no pre-defined JS function needed.
    /// </summary>
    private async Task InjectLinesAsync(List<StyledLine> lines)
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
        // Scroll AFTER the IIFE so it runs even if earlier operations short-circuit.
        // window.scrollTo is unambiguous on WebView2 regardless of scrollingElement.
        if (!_vm.IsScrollMode)
            sb.Append("window.scrollTo(0,document.body.scrollHeight);");

        await ExecuteScriptAsync(sb.ToString());
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

    private const double CharWidthDp = 8.0;

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

    private async void OnEditFkeysRequested()
    {
        Func<string[], Task>? onSave = _vm.CanSaveFkeys
            ? fkeys => _vm.SaveFkeysAsync(fkeys)
            : null;
        var editorVm = new FkeyEditorViewModel(_vm.GetAllFkeys(), _vm.ApplyFkeys, onSave);
        _isFkeyEditorOpen = true;
        await Navigation.PushModalAsync(new FkeyEditorPage(editorVm));
    }

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
        => await ExecuteScriptAsync("document.getElementById('out').innerHTML=''");

#if WINDOWS
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
        if (InputEntry.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.TextBox tb)
            tb.PreviewKeyDown += OnInputPreviewKeyDown;
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
