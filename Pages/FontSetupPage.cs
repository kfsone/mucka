using Mucka.Core;
using Mucka.Rendering;
using Mucka.Ui;
using Mucka.ViewModels;
using MudSharp.Models;
using MudSharp.Protocol;

namespace Mucka.Pages;

/// <summary>What the Text Wizard chose: the sizes, and a chat colour theme to switch to, or null to
/// leave the chat colours as they are.</summary>
internal sealed record FontSetupResult(FontSetupChoice Sizes, ChatTheme? Theme);

/// <summary>
/// The Text Wizard - docs/font-setup-design.md. The "Mucka text size" slider at the top, a mock of
/// the game window in the middle, and the game text size and chat colours below it. The mock is a
/// status bar line and a who list drawn with the game page's own <c>StatLabelStyle</c> and
/// <c>WhoEntryTemplate</c>, and a real <see cref="TerminalView"/> showing
/// <see cref="SampleScreenScript"/>. The UI step is applied to the whole app as it changes, but this
/// page's own words and buttons stay at the size it opened with, so nothing moves under the pointer.
///
/// <para>Completes with the choice on "Looks good", or null on Skip / Cancel / Escape after putting
/// the UI step back to the one it was given. It writes nothing; the caller saves.</para>
/// </summary>
internal sealed class FontSetupPage : ContentPage
{
    private static readonly Color PageBg = Color.FromArgb("#0C0C0C");
    private static readonly Color PanelBg = Color.FromArgb("#101618");
    private static readonly Color PanelEdge = Color.FromArgb("#2d333b");
    private static readonly Color Body = Color.FromArgb("#cccccc");
    private static readonly Color Muted = Color.FromArgb("#999999");
    private static readonly Color Accent = Color.FromArgb("#58a6ff");
    private static readonly Color Outline = Color.FromArgb("#ffd700");

    private const double LogoOpacity = 0.12;
    private const double MockMinHeight = 220;

    // While the pointer is on a control row, what it changes is outlined and the rest of the mock
    // fades to this. The outline is a 3px border that is always there in the colour behind it, so
    // showing it moves nothing.
    private const double FadedOpacity = 0.6;
    private const double OutlineStroke = 3;

    private static readonly IReadOnlyList<StyledLine> s_sample = SampleScreenScript.Lines();

    /// <summary>The drop-down's first entry when the stored colours are not any theme's own.</summary>
    private const string OwnColours = "Your own colours";

    private readonly TaskCompletionSource<FontSetupResult?> _tcs;
    private readonly int _restoreStep;
    private readonly int _chromeStep;
    private readonly ChatColorizer.Palette _storedPalette;
    private readonly ChatTheme _storedTheme;
    private readonly bool _ownColours;
    private int _gameFontPx;
    private ChatTheme? _pickedTheme;
    private readonly Label _uiValue;
    private readonly Label _gameValue;
    private readonly TerminalView _terminal;
    private readonly Border _statusFrame;
    private readonly Border _whoFrame;
    private readonly Border _terminalFrame;
    private readonly Dictionary<Border, Color> _idleStroke = new();
    private Picker? _themePicker;
    private bool _closed;
#if UIKIT
    private MacEscape.Registration? _macEscape;
#endif

    /// <param name="initial">The sizes the page opens with. Its UI step is applied at once, and is
    /// the size of this page's own words.</param>
    /// <param name="restoreStep">The UI step Skip / Cancel puts back.</param>
    /// <param name="firstRun">Opened by itself: the leave button reads Skip rather than Cancel.</param>
    /// <param name="palette">The chat colours as they are now.</param>
    /// <param name="theme">The stored chat colour theme.</param>
    public FontSetupPage(FontSetupChoice initial, int restoreStep, bool firstRun,
        ChatColorizer.Palette palette, ChatTheme theme, TaskCompletionSource<FontSetupResult?> tcs)
    {
        _tcs = tcs;
        _restoreStep = restoreStep;
        _chromeStep = UiFontScale.Clamp(initial.UiStep);
        _storedPalette = palette;
        _storedTheme = theme;
        _ownColours = palette.Emote != theme.Emote || palette.Say != theme.Say
                      || palette.Shout != theme.Shout || palette.Tell != theme.Tell;
        _gameFontPx = FontSetup.ClampGameFont(initial.GameFontPx);
        UiFontScaleState.Current.Step = initial.UiStep;
        Title = "mucka - Text Wizard";
        BackgroundColor = PageBg;
        NavigationPage.SetHasNavigationBar(this, false);

        _uiValue = ValueLabel();
        _gameValue = ValueLabel();
        _terminal = new TerminalView { BackgroundColor = PageBg };
        _terminal.ChatPalette = palette;
        _terminal.AppendLines(s_sample);
        _statusFrame = StatusExample();
        _whoFrame = WhoExample();
        _terminalFrame = Outlined(new Border { Content = _terminal, Padding = 0 }, PageBg);

        var leave = PageButton(firstRun ? "Skip" : "Cancel", Color.FromArgb("#1a1a1a"), Body);
        leave.BorderColor = PanelEdge;
        leave.BorderWidth = 1;
        leave.Clicked += async (_, _) => await CloseAsync(null);
        var accept = PageButton("Looks good", Color.FromArgb("#1f6feb"), Colors.White);
        accept.Clicked += async (_, _) => await CloseAsync(new FontSetupResult(
            new FontSetupChoice(UiFontScaleState.Current.Step, _gameFontPx), _pickedTheme));

        var heading = new Label { VerticalOptions = LayoutOptions.Center, FormattedText = new FormattedString() };
        heading.FormattedText.Spans.Add(new Span
        {
            Text = "Mucka Text Wizard", TextColor = Colors.White, FontAttributes = FontAttributes.Bold, FontSize = Chrome(24),
        });
        heading.FormattedText.Spans.Add(new Span { Text = "  (pun intended)", TextColor = Muted, FontSize = Chrome(13) });

        var menus = SizeSlider("Mucka text size", "Buttons, menus, the status bar and who is online.", _uiValue,
            UiFontScale.Min, UiFontScale.Max, _chromeStep, step => UiFontScaleState.Current.Step = step);
        HighlightWhileHovered(menus, game: false);
        var game = SizeSlider("Game text size", "Everything the game says to you: rooms, chat and the rest.", _gameValue,
            FontSetup.MinGameFontPx, FontSetup.MaxGameFontPx, _gameFontPx, SetGameFont);
        HighlightWhileHovered(game, game: true);
        var colours = ThemeRow();
        HighlightWhileHovered(colours, game: true);

        var content = new Grid
        {
            Padding = new Thickness(20, 14),
            RowSpacing = 10,
            MaximumWidthRequest = 1100,
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto),
            },
        };
        content.Add(new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
            ColumnSpacing = 12,
            Children =
            {
                heading,
                Column(new HorizontalStackLayout
                {
                    Spacing = 12, VerticalOptions = LayoutOptions.Center, Children = { leave, accept },
                }, 1),
            },
        }, 0, 0);
        content.Add(new Label
        {
            Text = "Choose basic display settings to start! You can change them later in "
                 + Glyph.Settings + " Settings.",
            TextColor = Body, FontSize = Chrome(14),
        }, 0, 1);
        content.Add(menus, 0, 2);
        content.Add(MockWindow(), 0, 3);
        content.Add(game, 0, 4);
        content.Add(colours, 0, 5);

        // The logo fills the window at its own shape; the page colour letterboxes it.
        Content = new Grid
        {
            Children =
            {
                new Image
                {
                    Source = "mucka_logo.png", Aspect = Aspect.AspectFit, Opacity = LogoOpacity,
                    HorizontalOptions = LayoutOptions.Fill, VerticalOptions = LayoutOptions.Fill,
                    InputTransparent = true,
                },
                content,
            },
        };

        ShowValues();
        SetGameFont(_gameFontPx);
    }

    /// <summary>An authored size for this page's own words: fixed at the step it opened with.</summary>
    private double Chrome(double size) => UiFontScale.Apply(size, _chromeStep);

    private static T Column<T>(T view, int column) where T : BindableObject
    {
        Grid.SetColumn(view, column);
        return view;
    }

    // The window's own title while this page is up, put back when it goes.
    private string? _windowTitle;

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (Window is { } window && window.Title != Title)
        {
            _windowTitle = window.Title;
            window.Title = Title;
        }
        UiFontScaleState.Current.PropertyChanged -= OnStepChanged;
        UiFontScaleState.Current.PropertyChanged += OnStepChanged;
#if WINDOWS
        if (Window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window win &&
            win.Content is Microsoft.UI.Xaml.UIElement root)
        {
            root.PreviewKeyDown -= OnWindowPreviewKeyDown;
            root.PreviewKeyDown += OnWindowPreviewKeyDown;
        }
#elif UIKIT
        MacEscape.Detach(_macEscape);
        _macEscape = MacEscape.Attach(this, () => _ = CloseAsync(null));
#endif
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        // Only while the title is still this page's: a game page behind may have set its own since.
        if (Window is { } window && _windowTitle is not null)
        {
            if (window.Title == Title)
                window.Title = _windowTitle;
            _windowTitle = null;
        }
        UiFontScaleState.Current.PropertyChanged -= OnStepChanged;
#if WINDOWS
        if (Window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window win &&
            win.Content is Microsoft.UI.Xaml.UIElement root)
            root.PreviewKeyDown -= OnWindowPreviewKeyDown;
#elif UIKIT
        MacEscape.Detach(_macEscape);
        _macEscape = null;
#endif
    }

    // The page completes the same way whichever button closed it.
    protected override bool OnBackButtonPressed()
    {
        _ = CloseAsync(null);
        return true;
    }

#if WINDOWS
    private void OnWindowPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape) return;
        e.Handled = true;
        _ = CloseAsync(null);
    }
#endif

    /// <summary>Completes and pops this page, at most once, and only while it is the top modal. A null
    /// <paramref name="result"/> (Skip, Cancel, Escape) puts the UI step back first. Also how a dropped
    /// session closes it from underneath.</summary>
    public async Task CloseAsync(FontSetupResult? result)
    {
        if (_closed) return;
        _closed = true;
        if (result is null)
            UiFontScaleState.Current.Step = _restoreStep;
        _tcs.TrySetResult(result);
        var stack = Navigation.ModalStack;
        if (stack.Count > 0 && ReferenceEquals(stack[^1], this))
            await Navigation.PopModalAsync(NavAnimation.ModalAnimated);
    }

    private void OnStepChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => ShowValues();

    // The terminal fills its row of the mock: a larger font shows fewer rows, the newest at the bottom.
    private void SetGameFont(int px)
    {
        _gameFontPx = FontSetup.ClampGameFont(px);
        _terminal.SetFontSize(_gameFontPx);
        ShowValues();
    }

    private void ShowValues()
    {
        _uiValue.Text = "Size: " + UiFontScale.Name(UiFontScaleState.Current.Step);
        _gameValue.Text = "Size: " + FontSetup.GameFontName(_gameFontPx);
    }

    private Button PageButton(string text, Color background, Color foreground)
        => new()
        {
            Text = text, BackgroundColor = background, TextColor = foreground, CornerRadius = 6,
            Padding = new Thickness(20, 8), MinimumHeightRequest = 40, FontSize = Chrome(15),
        };

    private Label ValueLabel()
        => new()
        {
            TextColor = Colors.White, FontAttributes = FontAttributes.Bold, FontSize = Chrome(18),
            HorizontalTextAlignment = TextAlignment.Center, VerticalOptions = LayoutOptions.Center,
            MinimumWidthRequest = Chrome(140),
        };

    private Border Card(string heading, string hint, View control)
    {
        Grid.SetColumn(control, 1);
        return new Border
        {
            BackgroundColor = PanelBg, Stroke = PanelEdge, StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            Padding = new Thickness(16, 8),
            Content = new Grid
            {
                ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
                ColumnSpacing = 16,
                Children =
                {
                    new VerticalStackLayout
                    {
                        Spacing = 2, VerticalOptions = LayoutOptions.Center,
                        Children =
                        {
                            new Label { Text = heading, TextColor = Colors.White, FontAttributes = FontAttributes.Bold, FontSize = Chrome(17) },
                            new Label { Text = hint, TextColor = Muted, FontSize = Chrome(13) },
                        },
                    },
                    control,
                },
            },
        };
    }

    private const double SliderWidth = 320;

    /// <summary>A heading, a hint, and "Tiny ---o--- Large" with the size named beside it. The slider
    /// moves in whole steps from <paramref name="min"/> to <paramref name="max"/>.</summary>
    private Border SizeSlider(string heading, string hint, Label value, int min, int max, int start, Action<int> set)
    {
        var slider = new Slider
        {
            Minimum = min, Maximum = max, Value = start,
            WidthRequest = Chrome(SliderWidth), VerticalOptions = LayoutOptions.Center,
            MinimumTrackColor = Accent, MaximumTrackColor = PanelEdge, ThumbColor = Colors.White,
        };
        var last = start;
        slider.ValueChanged += (_, e) =>
        {
            var step = (int)Math.Round(e.NewValue);
            if (Math.Abs(e.NewValue - step) > 0.001) slider.Value = step;   // snap to whole steps
            if (step == last) return;
            last = step;
            set(step);
        };
        SemanticProperties.SetDescription(slider, heading);

        Label End(string text) => new()
        {
            Text = text, TextColor = Body, FontSize = Chrome(14), VerticalOptions = LayoutOptions.Center,
        };
        return Card(heading, hint, new HorizontalStackLayout
        {
            Spacing = 10, VerticalOptions = LayoutOptions.Center,
            Children = { End("Tiny"), slider, End("Large"), value },
        });
    }

    /// <summary>The chat colour theme: the stored one selected, or "Your own colours" first when the
    /// stored colours are not a theme's own. Picking one recolours the example at once.</summary>
    private Border ThemeRow()
    {
        var names = ChatTheme.All.Select(t => t.Name).ToList();
        if (_ownColours) names.Insert(0, OwnColours);
        var picker = new Picker
        {
            ItemsSource = names,
            SelectedIndex = _ownColours ? 0 : Math.Max(0, ChatTheme.All.ToList().IndexOf(_storedTheme)),
            TextColor = Colors.White, BackgroundColor = PanelEdge, FontSize = Chrome(15),
            MinimumWidthRequest = Chrome(190), MinimumHeightRequest = Chrome(40), VerticalOptions = LayoutOptions.Center,
        };
        picker.SelectedIndexChanged += (_, _) =>
        {
            var index = picker.SelectedIndex - (_ownColours ? 1 : 0);
            if (index < 0)
            {
                _pickedTheme = null;
                _terminal.ChatPalette = _storedPalette;
                return;
            }
            var theme = ChatTheme.All[index];
            _pickedTheme = !_ownColours && theme == _storedTheme ? null : theme;
            _terminal.ChatPalette = theme.ApplyTo(_storedPalette);
        };
        _themePicker = picker;
        return Card("Chat colours",
            "Colours Mucka uses for MUD text itself. You can tweak these any time via the "
            + Glyph.Settings + " Settings menu.", picker);
    }

    private Border Outlined(Border border, Color idle)
    {
        border.StrokeThickness = OutlineStroke;
        border.Stroke = idle;
        _idleStroke[border] = idle;
        return border;
    }

    /// <summary>While the pointer is on <paramref name="row"/>, outline the part of the mock it
    /// changes and fade the rest. The game rows keep it while the theme drop-down is open, since its
    /// list takes the pointer just when the colours are being compared.</summary>
    private void HighlightWhileHovered(View row, bool game)
    {
        var pointer = new PointerGestureRecognizer();
        pointer.PointerEntered += (_, _) => ShowWhatChanges(game);
        pointer.PointerExited += (_, _) =>
        {
            if (game && _themePicker is { IsFocused: true }) return;
            ShowWhatChanges(null);
        };
        row.GestureRecognizers.Add(pointer);
        if (game && _themePicker is { } picker)
            picker.Unfocused += (_, _) => ShowWhatChanges(null);
    }

    /// <summary>Outlines the game part of the mock (true), the menus part (false), or neither (null).</summary>
    private void ShowWhatChanges(bool? game)
    {
        foreach (var (frame, isGame) in new[] { (_statusFrame, false), (_whoFrame, false), (_terminalFrame, true) })
        {
            var lit = game == isGame;
            frame.Stroke = lit ? Outline : _idleStroke[frame];
            frame.Opacity = game is null || lit ? 1.0 : FadedOpacity;
        }
    }

    /// <summary>The examples inside a rough window frame, so they read as a picture of the game
    /// window rather than part of this page.</summary>
    private Border MockWindow()
    {
        var titleBar = new Grid
        {
            BackgroundColor = Color.FromArgb("#1c2128"),
            Padding = new Thickness(10, 4),
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
            Children =
            {
                new Label { Text = "How it will look", TextColor = Accent, FontAttributes = FontAttributes.Bold,
                            VerticalOptions = LayoutOptions.Center, FontSize = Chrome(12) },
                Column(new Label { Text = "_   " + Glyph.SquareHollow + "   " + Glyph.Close, TextColor = Muted,
                                   VerticalOptions = LayoutOptions.Center, FontSize = Chrome(12) }, 1),
            },
        };
        var body = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) },
            ColumnSpacing = 0,
        };
        body.Add(_whoFrame, 0, 0);
        body.Add(_terminalFrame, 1, 0);
        var stack = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star),
            },
        };
        stack.Add(titleBar, 0, 0);
        stack.Add(_statusFrame, 0, 1);
        stack.Add(body, 0, 2);
        return new Border
        {
            Stroke = Color.FromArgb("#444c56"), StrokeThickness = 1, BackgroundColor = PageBg,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 },
            Padding = 0, MinimumHeightRequest = MockMinHeight,
            Content = stack,
        };
    }

    /// <summary>One status bar line in the game page's own style, following the live UI step.</summary>
    private Border StatusExample()
    {
        var minor = Application.Current!.Resources["StatMinorFontSize"] switch
        {
            double d => d,
            OnPlatform<double> p => (double)p,
            _ => 13.0,
        };

        Label Stat(string name, string cur, string minorText, Color minorColour)
        {
            var minorSpan = new Span { Text = minorText, TextColor = minorColour };
            minorSpan.SetBinding(Span.FontSizeProperty, ScaledFontBinding.For(minor, tight: true));
            return new Label
            {
                Style = (Style)Application.Current!.Resources["StatLabelStyle"],
                Margin = new Thickness(0, 0, 8, 0),
                FormattedText = new FormattedString
                {
                    Spans =
                    {
                        new Span { Text = name + ": ", TextColor = Accent },
                        new Span { Text = cur, TextColor = Colors.White, FontAttributes = FontAttributes.Bold },
                        minorSpan,
                    },
                },
            };
        }

        // Padding plus the outline make the bar's own 6,4.
        var grey = Color.FromArgb("#808080");
        return Outlined(new Border
        {
            BackgroundColor = PanelBg, Padding = new Thickness(6 - OutlineStroke, 4 - OutlineStroke),
            Content = new HorizontalStackLayout
            {
                Children =
                {
                    Stat("Sta", "87", "/100", grey), Stat("Mag", "40", "/100", grey),
                    Stat("Str", "62", "/70", grey), Stat("Dex", "55", "/60", grey),
                    Stat("Score", "1234", " (-50)", Colors.White),
                },
            },
        }, PanelBg);
    }

    /// <summary>A docked who list, as wide as the side panel is at the chosen step.</summary>
    private Border WhoExample()
    {
        var heading = new Label { Text = "Online 4", TextColor = Accent, FontFamily = "Cascadia Mono" }.Scaled(11);
        var list = new VerticalStackLayout { Spacing = 1 };
        BindableLayout.SetItemTemplate(list, (DataTemplate)Application.Current!.Resources["WhoEntryTemplate"]);
        BindableLayout.SetItemsSource(list, new[]
        {
            new WhoEntry("Ollie the amateur", AnsiPalette.GetFg((byte)AnsiColor.BrightCyan)),
            new WhoEntry("Lady Polly the witch", AnsiPalette.GetFg((byte)AnsiColor.BrightMagenta)),
            new WhoEntry("Drizzle the wobbly mage", AnsiPalette.GetFg((byte)AnsiColor.White)),
            new WhoEntry("(Crispybob the warrior)", AnsiPalette.GetFg((byte)AnsiColor.White)),
        });

        // Padding plus the outline make the side panel's own 1px edge and 8,6,8,4 padding.
        var panel = Outlined(new Border
        {
            BackgroundColor = PanelBg,
            Padding = new Thickness(9 - OutlineStroke, 7 - OutlineStroke, 9 - OutlineStroke, 5 - OutlineStroke),
            Content = new VerticalStackLayout { Spacing = 2, Children = { heading, list } },
        }, PanelBg);
        panel.SetBinding(WidthRequestProperty, SidePanelWidthExtension.Create());
        return panel;
    }
}
