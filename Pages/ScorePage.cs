using Mucka.Commands;
using Mucka.Core;
using Mucka.ViewModels;

namespace Mucka.Pages;

/// <summary>
/// The $SCORE panel on its own page, opened from a profile on the connect page so the history can be
/// read without being in a game. It reads the same database; with no Mucka run of its own, every
/// session and run left open in it is one whose client exited without closing it.
///
/// <para>Closes on the panel's x, on Escape (Windows), or on the system back button.</para>
/// </summary>
internal sealed class ScorePage : ContentPage
{
    private readonly ScoreGraphViewModel _graph;
    private bool _closed;

    /// <param name="host">The profile's server, which the panel ticks; the character shown first is
    /// the one played there most recently.</param>
    public ScorePage(string host)
    {
        // -1 is no mucka_runs.id: this page belongs to no run.
        _graph = new ScoreGraphViewModel(MuckaPaths.GetDatabasePath, () => -1, () => host, () => null, () => { });
        BackgroundColor = Color.FromArgb("#0C0C0C");
        Content = new Grid
        {
            Padding = new Thickness(0, 24, 0, 0),
            Children =
            {
                new ScorePanelView
                {
                    BindingContext = _graph,
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Start,
                },
            },
        };
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _graph.Closed -= OnClosed;
        _graph.Closed += OnClosed;
        if (!_graph.IsVisible)
            _graph.Open(new ScoreCommandArgs(ScoreCommandArgs.DefaultDays, null));
#if WINDOWS
        if (Window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window win &&
            win.Content is Microsoft.UI.Xaml.UIElement root)
            root.PreviewKeyDown += OnWindowPreviewKeyDown;
#endif
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _graph.Closed -= OnClosed;
#if WINDOWS
        if (Window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window win &&
            win.Content is Microsoft.UI.Xaml.UIElement root)
            root.PreviewKeyDown -= OnWindowPreviewKeyDown;
#endif
    }

    private void OnClosed() => MainThread.BeginInvokeOnMainThread(async () => await CloseAsync());

    /// <summary>Pops this page, at most once, and only while it is the top modal.</summary>
    private async Task CloseAsync()
    {
        if (_closed)
            return;
        _closed = true;
        var stack = Navigation.ModalStack;
        if (stack.Count > 0 && ReferenceEquals(stack[^1], this))
            await Navigation.PopModalAsync();
    }

    protected override bool OnBackButtonPressed()
    {
        if (!_graph.TryClose())
            _ = CloseAsync();
        return true;
    }

#if WINDOWS
    private void OnWindowPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            _graph.TryClose();
        }
    }
#endif
}
