using Mucka.Store;

namespace Mucka.Pages;

/// <summary>
/// The modal over a page's whole window while the database is being worked on (<see cref="Maintenance"/>):
/// a scrim that takes the pointer, a title, a sub-heading, a spinner and a line saying what is being
/// done. It shows and hides itself from <see cref="Maintenance.Current"/>, and subscribes only while it
/// is on a page, so a page that has gone is not kept alive by it. While the newest job can be given up
/// on (<see cref="MaintenanceTracker.CanCancel"/>: this Mucka is waiting for another), it also says why
/// and offers <see cref="ExitText"/>, which cancels the wait and quits.
///
/// <para>It takes the pointer and nothing else: the keyboard stays where it was, so the command box
/// still owns it (Invariant #0) and the player can type ahead. <see cref="Dismissed"/> is raised when
/// it hides, for the page to hand focus back to the command box.</para>
/// </summary>
public sealed class MaintenanceOverlay : ContentView
{
    /// <summary>The overlay's title.</summary>
    public const string TitleText = "Putting MUD back in the barrel";

    /// <summary>The overlay's sub-heading.</summary>
    public const string SubHeadingText = "Data maintenance";

    /// <summary>What the overlay says, under the detail, when the player can give up on the work: another
    /// Mucka has the database and this one is waiting for it.</summary>
    public const string WaitingExplanation =
        "Another Mucka is busy with your game data. This one carries on by itself when it is done, " +
        "or you can exit and try again later.";

    /// <summary>The button that gives up on the wait by exiting this Mucka.</summary>
    public const string ExitText = "Exit this Mucka and try again later";

    private readonly Label _detail;
    private readonly Label _subDetail;
    private readonly VerticalStackLayout _giveUp;
    private bool _shown;

    /// <summary>Raised on the UI thread when the overlay hides.</summary>
    public event EventHandler? Dismissed;

    public MaintenanceOverlay()
    {
        IsVisible = false;
        HorizontalOptions = LayoutOptions.Fill;
        VerticalOptions = LayoutOptions.Fill;

        _detail = new Label
        {
            TextColor = Color.FromArgb("#8b949e"),
            FontSize = 12,
            HorizontalTextAlignment = TextAlignment.Center,
        };

        _subDetail = new Label
        {
            TextColor = Color.FromArgb("#6e7681"),
            FontSize = 10,
            IsVisible = false,
            HorizontalTextAlignment = TextAlignment.Center,
        };

        var exit = new Button
        {
            Text = ExitText,
            TextColor = Colors.White,
            BackgroundColor = Color.FromArgb("#21262d"),
            BorderColor = Color.FromArgb("#30363d"),
            BorderWidth = 1,
            HorizontalOptions = LayoutOptions.Center,
        };
        exit.Clicked += (_, _) =>
        {
            Maintenance.Current.Cancel();
            Application.Current?.Quit();
        };
        _giveUp = new VerticalStackLayout
        {
            IsVisible = false,
            Spacing = 12,
            Margin = new Thickness(0, 12, 0, 0),
            Children =
            {
                new Label
                {
                    Text = WaitingExplanation,
                    TextColor = Color.FromArgb("#c9d1d9"),
                    FontSize = 12,
                    MaximumWidthRequest = 360,
                    HorizontalTextAlignment = TextAlignment.Center,
                },
                exit,
            },
        };

        Content = new Grid
        {
            BackgroundColor = Color.FromArgb("#B3000000"),
            Children =
            {
                new Border
                {
                    BackgroundColor = Color.FromArgb("#161b22"),
                    Stroke = Color.FromArgb("#30363d"),
                    StrokeThickness = 1,
                    StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
                    Padding = new Thickness(32, 24),
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center,
                    Content = new VerticalStackLayout
                    {
                        Spacing = 6,
                        Children =
                        {
                            new Label
                            {
                                Text = TitleText,
                                TextColor = Colors.White,
                                FontSize = 18,
                                FontAttributes = FontAttributes.Bold,
                                HorizontalTextAlignment = TextAlignment.Center,
                            },
                            new Label
                            {
                                Text = SubHeadingText,
                                TextColor = Color.FromArgb("#8b949e"),
                                FontSize = 12,
                                HorizontalTextAlignment = TextAlignment.Center,
                            },
                            new ActivityIndicator
                            {
                                IsRunning = true,
                                Color = Color.FromArgb("#58a6ff"),
                                HorizontalOptions = LayoutOptions.Center,
                                Margin = new Thickness(0, 12, 0, 6),
                            },
                            _detail,
                            _subDetail,
                            _giveUp,
                        },
                    },
                },
            },
        };

        Loaded += (_, _) =>
        {
            // Loaded can be raised again without an Unloaded between.
            Maintenance.Current.Changed -= OnChanged;
            Maintenance.Current.Changed += OnChanged;
            Apply();
        };
        Unloaded += (_, _) => Maintenance.Current.Changed -= OnChanged;
    }

    private void OnChanged() => MainThread.BeginInvokeOnMainThread(Apply);

    private void Apply()
    {
        var active = Maintenance.Current.IsActive;
        if (active)
        {
            var lines = Maintenance.Current.Detail.Split('\n', 2);
            _detail.Text = lines[0];
            _subDetail.Text = lines.Length > 1 ? lines[1] : string.Empty;
            _subDetail.IsVisible = lines.Length > 1;
        }
        _giveUp.IsVisible = active && Maintenance.Current.CanCancel;
        IsVisible = active;
        if (_shown && !active)
            Dismissed?.Invoke(this, EventArgs.Empty);
        _shown = active;
    }
}
