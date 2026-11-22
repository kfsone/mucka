using Mucka.ViewModels;

namespace Mucka.Pages;

/// <summary>
/// One chat colour's page, over the settings editor: its hex box and the square it is drawn in, the
/// R, G and B sliders, the italic, bold and dim of your own lines and of other people's, and the
/// example lines it covers. Everything edits the row itself, so the settings editor's Apply and Save
/// take it as it stands; Done closes the page.
/// </summary>
public partial class ChatColorPage : ContentPage
{
    // The widest the controls grow on a desktop window; past it the sliders are mostly track.
    private const double MaxBodyWidthDp = 560;
    private const double BodyGutterDp = 24;

    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _isClosing;

    public ChatColorPage(ChatColorEditorItem row)
    {
        InitializeComponent();
        BindingContext = row;
    }

    /// <summary>Completes once the page has been closed, by Done, Escape, the system back button or the
    /// editor closing under it.</summary>
    public Task Closed => _closed.Task;

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        if (width > 0)
            Body.WidthRequest = Math.Min(MaxBodyWidthDp, width - BodyGutterDp);
    }

    private async void OnDoneClicked(object? sender, EventArgs e) => await CloseAsync();

#if UIKIT
    private MacEscape.Registration? _macEscape;

    protected override void OnAppearing()
    {
        base.OnAppearing();
        MacEscape.Detach(_macEscape);
        _macEscape = MacEscape.Attach(this, () => _ = CloseAsync());
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        MacEscape.Detach(_macEscape);
        _macEscape = null;
    }
#endif

    protected override bool OnBackButtonPressed()
    {
        _ = CloseAsync();
        return true;
    }

    /// <summary>Closes this page, at most once, and only while it is the top modal.</summary>
    public async Task CloseAsync()
    {
        if (_isClosing)
            return;
        _isClosing = true;
        try
        {
            var stack = Navigation.ModalStack;
            if (stack.Count > 0 && ReferenceEquals(stack[^1], this))
                await Navigation.PopModalAsync(Mucka.Core.NavAnimation.ModalAnimated);
        }
        finally
        {
            _closed.TrySetResult();
        }
    }
}
