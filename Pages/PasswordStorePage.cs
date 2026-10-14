#if MACCATALYST
namespace Mucka.Pages;

/// <summary>
/// The one-time question of where saved passwords live on a Mac. Built in code and compiled for
/// the Mac only. Two choices are shown as buttons and nothing happens until one is picked and
/// Accept is pressed; the page cannot be dismissed any other way, so a user who does not want to
/// choose has to quit. It completes with true (keychain) or false (plain text in the ini).
/// </summary>
public sealed class PasswordStorePage : ContentPage
{
    private static readonly Color Panel = Color.FromArgb("#161b22");
    private static readonly Color PanelEdge = Color.FromArgb("#2d333b");
    private static readonly Color Chosen = Color.FromArgb("#58a6ff");

    private readonly TaskCompletionSource<bool> _tcs;
    private readonly Border _insecure;
    private readonly Border _secure;
    private readonly Button _accept;
    private bool? _choice;
    private bool _done;

    public PasswordStorePage(TaskCompletionSource<bool> tcs)
    {
        _tcs = tcs;
        BackgroundColor = Color.FromArgb("#0C0C0C");
        NavigationPage.SetHasNavigationBar(this, false);

        _insecure = Choice(
            "Insecure",
            "Don't want to give it permission or deal with prompts",
            "Mucka stores your passwords as plain text in its config file, ~/.mucka/mucka.ini. "
            + "There are no further prompts, but anyone who sees that file can read your passwords, "
            + "so keep it to yourself.",
            secure: false);
        _secure = Choice(
            "Secure",
            "Don't mind the occasional system prompt for access",
            "macOS will ask for your permission whenever Mucka needs a saved password. "
            + "Choose 'Allow' to approve each access, or 'Always Allow' to approve once for each "
            + "new version of Mucka.",
            secure: true);

        _accept = new Button
        {
            Text = "Accept",
            IsEnabled = false,
            HorizontalOptions = LayoutOptions.End,
            Padding = new Thickness(28, 8),
        };
        _accept.Clicked += async (_, _) => await AcceptAsync();

        var column = new VerticalStackLayout
        {
            Spacing = 16,
            MaximumWidthRequest = 560,
            VerticalOptions = LayoutOptions.Center,
            Padding = new Thickness(24),
            Children =
            {
                new Label
                {
                    Text = "Saving your passwords",
                    TextColor = Colors.White, FontSize = 20, FontAttributes = FontAttributes.Bold,
                },
                new Label
                {
                    Text = "Mucka can remember your MUD2 passwords so it can log you in automatically. "
                        + "To do that securely, it needs permission to use your Mac's password keychain.",
                    TextColor = Color.FromArgb("#cccccc"), FontSize = 14,
                },
                new Label
                {
                    Text = "Please choose one:",
                    TextColor = Colors.White, FontSize = 14, FontAttributes = FontAttributes.Bold,
                },
                _insecure,
                _secure,
                new Label
                {
                    Text = "You can change this later in Settings > General > Store Passwords Securely.",
                    TextColor = Color.FromArgb("#999999"), FontSize = 12, FontAttributes = FontAttributes.Italic,
                },
                _accept,
            },
        };
        Content = new ScrollView { Content = column, HorizontalOptions = LayoutOptions.Center };
    }

    protected override bool OnBackButtonPressed() => true;

    private Border Choice(string heading, string hint, string detail, bool secure)
    {
        var border = new Border
        {
            BackgroundColor = Panel,
            Stroke = PanelEdge,
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            Padding = new Thickness(16, 12),
            Content = new VerticalStackLayout
            {
                Spacing = 4,
                Children =
                {
                    new Label { Text = heading, TextColor = Chosen, FontSize = 17, FontAttributes = FontAttributes.Bold },
                    new Label { Text = "(" + hint + ")", TextColor = Color.FromArgb("#999999"), FontSize = 12, FontAttributes = FontAttributes.Italic },
                    new Label { Text = detail, TextColor = Color.FromArgb("#cccccc"), FontSize = 13, Margin = new Thickness(0, 4, 0, 0) },
                },
            },
        };
        SemanticProperties.SetDescription(border, $"{heading}. {hint}. {detail}");
        SemanticProperties.SetHint(border, "Selects this option");
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => Select(secure);
        border.GestureRecognizers.Add(tap);
        return border;
    }

    private void Select(bool secure)
    {
        _choice = secure;
        Mark(_secure, secure);
        Mark(_insecure, !secure);
        _accept.IsEnabled = true;
    }

    private static void Mark(Border card, bool on)
    {
        card.Stroke = on ? Chosen : PanelEdge;
        card.StrokeThickness = on ? 2 : 1;
    }

    private async Task AcceptAsync()
    {
        if (_done || _choice is not { } secure) return;
        _done = true;
        _tcs.TrySetResult(secure);
        await Navigation.PopModalAsync();
    }
}
#endif
