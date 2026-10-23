using Mucka.Core;
using Mucka.Ui;

namespace Mucka.Pages;

/// <summary>
/// The first-run size picker - docs/font-setup-design.md. Nothing but a heading and a scrolling
/// column of the same line at every UI step, smallest at the top: the player double-taps the one
/// that reads comfortably. A single tap only highlights. Escape keeps the highlighted row.
///
/// <para>Nothing on this page follows the live UI step: its heading and arrows are a fixed size, and
/// each row is drawn at its own step's size, so nothing moves while the player looks. Completes
/// with the picked step; writes nothing.</para>
/// </summary>
internal sealed class InterfaceSizePage : ContentPage
{
    private static readonly Color PageBg = Color.FromArgb("#0C0C0C");
    private static readonly Color RowBg = Color.FromArgb("#d92d4a63");
    private static readonly Color RowText = Color.FromArgb("#f5d76e");
    private static readonly Color Picked = Color.FromArgb("#ffd700");
    private static readonly Color Arrow = Color.FromArgb("#d4a017");

    private const string Sample = "Double-click here if this text is a good size";

    // The rows are the who list's name size, in its font: the smallest text the game window
    // draws that matters, and the text the Mac player could not read.
    private const double SampleSize = 11;
    private const string SampleFont = "Cascadia Mono";
    private const double HeadingSize = 32;
    private const double ArrowSize = 22;
    private const double LogoOpacity = 0.3;
    private const int ArrowScrollRows = 3;

    private readonly TaskCompletionSource<int> _tcs;
    private readonly ScrollView _scroll;
    private readonly Dictionary<int, Border> _rows = new();
    private int _highlighted;
    private bool _centred;
    private bool _closed;
#if MACCATALYST
    private MacEscape.Registration? _macEscape;
#endif

    /// <param name="initial">The step highlighted and scrolled to at first.</param>
    public InterfaceSizePage(int initial, TaskCompletionSource<int> tcs)
    {
        _tcs = tcs;
        _highlighted = UiFontScale.Clamp(initial);
        Title = "mucka - Text size";
        BackgroundColor = PageBg;
        NavigationPage.SetHasNavigationBar(this, false);

        var list = new VerticalStackLayout { Spacing = 10, Padding = new Thickness(0, 4) };
        for (var step = UiFontScale.Min; step <= UiFontScale.Max; step++)
            list.Children.Add(Row(step));
        _scroll = new ScrollView { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Always };
        _scroll.SizeChanged += (_, _) => CentreOnce();
        Grid.SetRow(_scroll, 2);

        var up = ArrowButton(Glyph.TriangleUp, -1);
        Grid.SetRow(up, 1);
        var down = ArrowButton(Glyph.TriangleDown, +1);
        Grid.SetRow(down, 3);

        var heading = new Label
        {
            Text = "Mucka text size",
            TextColor = Colors.White, FontSize = HeadingSize, FontAttributes = FontAttributes.Bold,
            HorizontalOptions = LayoutOptions.Center,
        };
        var hint = new Label
        {
            Text = "Let's make sure Mucka's own text - its buttons, menus and panels - is readable for you. "
                 + "Double-click the line that is comfortable to read. The game's text gets its own size next.",
            TextColor = Color.FromArgb("#cccccc"), FontSize = 15,
            HorizontalOptions = LayoutOptions.Center, HorizontalTextAlignment = TextAlignment.Center,
        };

        Content = new Grid
        {
            Children =
            {
                // The logo fills the window at its own shape; the page colour letterboxes it.
                new Image
                {
                    Source = "mucka_logo.png", Aspect = Aspect.AspectFit, Opacity = LogoOpacity,
                    HorizontalOptions = LayoutOptions.Fill, VerticalOptions = LayoutOptions.Fill,
                    InputTransparent = true,
                },
                new Grid
                {
                    Padding = new Thickness(40, 20),
                    MaximumWidthRequest = 900,
                    RowDefinitions =
                    {
                        new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto),
                        new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto),
                    },
                    RowSpacing = 8,
                    Children =
                    {
                        new VerticalStackLayout { Spacing = 4, Children = { heading, hint } },
                        up, _scroll, down,
                    },
                },
            },
        };
        Highlight(_highlighted);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
#if WINDOWS
        if (Window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window win &&
            win.Content is Microsoft.UI.Xaml.UIElement root)
        {
            root.PreviewKeyDown -= OnWindowPreviewKeyDown;
            root.PreviewKeyDown += OnWindowPreviewKeyDown;
        }
#elif MACCATALYST
        MacEscape.Detach(_macEscape);
        _macEscape = MacEscape.Attach(this, () => _ = PickAsync(_highlighted));
#endif
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
#if WINDOWS
        if (Window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window win &&
            win.Content is Microsoft.UI.Xaml.UIElement root)
            root.PreviewKeyDown -= OnWindowPreviewKeyDown;
#elif MACCATALYST
        MacEscape.Detach(_macEscape);
        _macEscape = null;
#endif
    }

    protected override bool OnBackButtonPressed()
    {
        _ = PickAsync(_highlighted);
        return true;
    }

#if WINDOWS
    private void OnWindowPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Escape:
            case Windows.System.VirtualKey.Enter:
                e.Handled = true;
                _ = PickAsync(_highlighted);
                break;
            case Windows.System.VirtualKey.Up:
                e.Handled = true;
                Highlight(_highlighted - 1, scroll: true);
                break;
            case Windows.System.VirtualKey.Down:
                e.Handled = true;
                Highlight(_highlighted + 1, scroll: true);
                break;
        }
    }
#endif

    private Border Row(int step)
    {
        var row = new Border
        {
            BackgroundColor = RowBg, StrokeThickness = 3, Stroke = Colors.Transparent,
            Padding = new Thickness(18, 8),
            Content = new Label
            {
                Text = Sample, TextColor = RowText, FontFamily = SampleFont,
                FontSize = UiFontScale.Apply(SampleSize, step),
            },
        };
        SemanticProperties.SetDescription(row, $"Text size {UiFontScale.Name(step)}");
        var tap = new TapGestureRecognizer { NumberOfTapsRequired = 1 };
        tap.Tapped += (_, _) => Highlight(step);
        var pick = new TapGestureRecognizer { NumberOfTapsRequired = 2 };
        pick.Tapped += (_, _) => _ = PickAsync(step);
        row.GestureRecognizers.Add(tap);
        row.GestureRecognizers.Add(pick);
        _rows[step] = row;
        return row;
    }

    private Label ArrowButton(string glyph, int direction)
    {
        var arrow = new Label
        {
            Text = glyph, TextColor = Arrow, FontSize = ArrowSize,
            HorizontalOptions = LayoutOptions.Center, Padding = new Thickness(40, 0),
        };
        SemanticProperties.SetDescription(arrow, direction < 0 ? "Smaller" : "Bigger");
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => Highlight(_highlighted + direction * ArrowScrollRows, scroll: true);
        arrow.GestureRecognizers.Add(tap);
        return arrow;
    }

    private void Highlight(int step, bool scroll = false)
    {
        step = UiFontScale.Clamp(step);
        if (_rows.TryGetValue(_highlighted, out var old)) old.Stroke = Colors.Transparent;
        _highlighted = step;
        if (_rows.TryGetValue(step, out var row))
        {
            row.Stroke = Picked;
            if (scroll) _ = _scroll.ScrollToAsync(row, ScrollToPosition.Center, animated: true);
        }
    }

    // The highlighted row is centred once the list has a size to centre in.
    private void CentreOnce()
    {
        if (_centred || _scroll.Height <= 0 || !_rows.TryGetValue(_highlighted, out var row)) return;
        _centred = true;
        Dispatcher.Dispatch(() => _ = _scroll.ScrollToAsync(row, ScrollToPosition.Center, animated: false));
    }

    private async Task PickAsync(int step)
    {
        if (_closed) return;
        _closed = true;
        Highlight(step);
        _tcs.TrySetResult(UiFontScale.Clamp(step));
        var stack = Navigation.ModalStack;
        if (stack.Count > 0 && ReferenceEquals(stack[^1], this))
            await Navigation.PopModalAsync(NavAnimation.Animated);
    }
}
