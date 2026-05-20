using System.Text;
using System.Text.Json;
using Mucka.Core;
using Mucka.Helpers;
using Mucka.ViewModels;

namespace Mucka.Pages;

public partial class GamePage : ContentPage
{
    private readonly GameViewModel _vm;
    private IDispatcherTimer?      _flushTimer;

    // Lines pulled from the ViewModel queue but not yet successfully injected.
    // Kept here so they aren't lost if the WebView isn't ready yet.
    private readonly List<StyledLine> _pendingInjection = new();
    // Re-entrancy guard: prevents the 50ms timer from firing a second injection
    // while the first EvaluateJavaScriptAsync/ExecuteScriptAsync is still awaiting.
    private bool _injecting;

    public GamePage(GameViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _vm.Disconnected += OnDisconnected;
        _vm.RequestFocus += FocusInput;

        ScrollbackWebView.Source = new HtmlWebViewSource { Html = HtmlScrollback.InitialPage };

        _flushTimer = Dispatcher.CreateTimer();
        _flushTimer.Interval = TimeSpan.FromMilliseconds(50);
        _flushTimer.Tick += OnFlushTick;
        _flushTimer.Start();

        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(200), FocusInput);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _flushTimer?.Stop();
        _flushTimer = null;
        _vm.Disconnected -= OnDisconnected;
        _vm.RequestFocus -= FocusInput;
        _ = _vm.DisposeAsync();
    }

    private async void OnFlushTick(object? sender, EventArgs e)
    {
        if (_injecting) return;   // previous injection still in flight — pick up lines next tick

        // Move new lines from the ViewModel queue into our local buffer.
        var newLines = _vm.FlushPendingLines();
        if (newLines != null) _pendingInjection.AddRange(newLines);
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
            var html     = HtmlScrollback.LineToHtml(line);
            var cls      = line.IsPartial ? "lnp" : "ln";
            var jsonHtml = JsonSerializer.Serialize($"<span class='{cls}'>{html}\u200b</span>");
            if (line.IsPartial)
            {
                // Replace existing partial span or append a new one.
                var innerJson = JsonSerializer.Serialize(html + "\u200b");
                sb.Append($"(function(){{var p=o.querySelector('.lnp');if(p){{p.innerHTML={innerJson};}}else{{o.insertAdjacentHTML('beforeend',{jsonHtml});}}}})();");
            }
            else
            {
                // If a partial span exists, finalise it (update content + promote to .ln).
                // Only append a new span when there is no partial to avoid duplicates.
                var innerJson = JsonSerializer.Serialize(html + "\u200b");
                sb.Append($"(function(){{var p=o.querySelector('.lnp');if(p){{p.innerHTML={innerJson};p.className='ln';}}else{{o.insertAdjacentHTML('beforeend',{jsonHtml});}}}})();");
            }
        }

        // Trim to 120 permanent lines.
        sb.Append("while(o.querySelectorAll('.ln').length>120){var f=o.querySelector('.ln');if(f)f.remove();else break;}");
        // Auto-scroll.
        sb.Append("window.scrollTo(0,document.body.scrollHeight);");
        sb.Append("})();");

        await ExecuteScriptAsync(sb.ToString());
    }

    /// <summary>
    /// Execute JavaScript reliably across platforms.
    /// On WinUI, MAUI's EvaluateJavaScriptAsync silently fails for HtmlWebViewSource pages;
    /// we go directly to the CoreWebView2 API instead.
    /// </summary>
    private async Task ExecuteScriptAsync(string script)
    {
#if WINDOWS
        if (ScrollbackWebView.Handler?.PlatformView is
            Microsoft.UI.Xaml.Controls.WebView2 wv2)
        {
            if (wv2.CoreWebView2 is null)
                await wv2.EnsureCoreWebView2Async();
            await wv2.CoreWebView2.ExecuteScriptAsync(script);
            return;
        }
#endif
        await ScrollbackWebView.EvaluateJavaScriptAsync(script);
    }

    private void FocusInput() => InputEntry.Focus();

    private void OnDisconnected()
    {
        _flushTimer?.Stop();
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            await DisplayAlertAsync("Disconnected", "The server closed the connection.", "OK");
            await Navigation.PopAsync();
        });
    }
}


