#if MACCATALYST
namespace Mucka.Pages;

/// <summary>
/// The one-time question of where saved passwords live on a Mac. Built in code and compiled for
/// the Mac only. A DisplayAlert has two buttons but cannot carry these labels and their
/// explanations, hence a page. It completes with true (keychain), false (plain text in the ini),
/// and cannot be dismissed any other way.
/// </summary>
public sealed class PasswordStorePage : ContentPage
{
    private readonly TaskCompletionSource<bool> _tcs;
    private bool _done;

    public PasswordStorePage(TaskCompletionSource<bool> tcs)
    {
        _tcs = tcs;
        BackgroundColor = Color.FromArgb("#0C0C0C");

        var column = new VerticalStackLayout
        {
            Spacing = 14,
            MaximumWidthRequest = 520,
            VerticalOptions = LayoutOptions.Center,
            Padding = new Thickness(24),
            Children =
            {
                new Label
                {
                    Text = "Important: Password Management",
                    TextColor = Colors.White, FontSize = 18, FontAttributes = FontAttributes.Bold,
                },
                new Label
                {
                    Text = "To store your MUD2 login passwords securely, Mucka needs keychain access.",
                    TextColor = Color.FromArgb("#cccccc"), FontSize = 14,
                },
                Choice("I don't know about that",
                    "Mucka keeps your passwords as plain text in ~/.mucka/mucka.ini. You won't be asked about this again.",
                    false),
                Choice("I don't mind the occasional prompt for security",
                    "macOS will ask you to allow Mucka to use your keychain. Choose Allow to be asked every time, "
                    + "or Always Allow to be asked once for each Mucka update.",
                    true),
                new Label
                {
                    Text = "You can change this later in Settings > General > Store Passwords Securely.",
                    TextColor = Color.FromArgb("#888888"), FontSize = 12, FontAttributes = FontAttributes.Italic,
                },
            },
        };
        Content = new ScrollView { Content = column, HorizontalOptions = LayoutOptions.Center };
    }

    private Border Choice(string heading, string detail, bool secure)
    {
        var border = new Border
        {
            BackgroundColor = Color.FromArgb("#161b22"),
            Stroke = Color.FromArgb("#2d333b"),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            Padding = new Thickness(14, 12),
            Content = new VerticalStackLayout
            {
                Spacing = 4,
                Children =
                {
                    new Label { Text = heading, TextColor = Color.FromArgb("#58a6ff"), FontSize = 15, FontAttributes = FontAttributes.Bold },
                    new Label { Text = detail, TextColor = Color.FromArgb("#cccccc"), FontSize = 13 },
                },
            },
        };
        SemanticProperties.SetDescription(border, $"{heading}. {detail}");
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => await Complete(secure);
        border.GestureRecognizers.Add(tap);
        return border;
    }

    private async Task Complete(bool secure)
    {
        if (_done) return;
        _done = true;
        _tcs.TrySetResult(secure);
        await Navigation.PopModalAsync();
    }
}
#endif
