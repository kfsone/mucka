using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Mucka.Combat;
using Mucka.Core;
using Mucka.Store;

namespace Mucka.Pages;

/// <summary>
/// History, opened from the connect page's header: the player's Sessions (one login each) and the
/// replays they can play. Built in code, as <see cref="ScorePage"/> is.
///
/// <para>Closes on its x, on Escape (Windows and Mac), or on the system back button.</para>
/// </summary>
internal sealed class HistoryPage : ContentPage
{
    private static readonly Color PageColor = Color.FromArgb("#0C0C0C");
    private static readonly Color Rule = Color.FromArgb("#222222");
    private static readonly Color Body = Color.FromArgb("#E6E6E6");
    private static readonly Color Muted = Color.FromArgb("#8B949E");
    private static readonly Color Accent = Color.FromArgb("#58A6FF");
    private static readonly Color Gain = Color.FromArgb("#3FB950");
    private static readonly Color Loss = Color.FromArgb("#F85149");

    /// <summary>The widest the lists grow on a desktop window; past it a row is mostly gap.</summary>
    private const double MaxContentWidth = 820;

    private const string RenameChoice = "Rename";
    private const string TagsChoice = "Tags";
    private const string PlayChoice = "Play";
    private const string ShareChoice = "Share";
    private const string DeleteChoice = "Delete";

    private readonly Func<string?> _playRefusal;
    private readonly Func<string, Task> _playReplay;
    private readonly Button _sessionsTab;
    private readonly Button _replaysTab;
    private readonly CollectionView _sessions;
    private readonly CollectionView _replays;
    private readonly Label _empty;
    private ObservableCollection<SessionItem> _sessionItems = [];
    private List<ReplayItem> _replayItems = [];
    private bool _showingReplays;
    private bool _tabShown;
    private bool _closed;
    // Set from a row's menu opening until its action has finished: one action at a time, and Escape
    // belongs to the menu or prompt over the page meanwhile.
    private bool _busy;
#if UIKIT
    private MacEscape.Registration? _macEscape;
#endif

    /// <param name="playRefusal">Why a replay cannot be played now, or null when it can; asked
    /// before this page closes for Play.</param>
    /// <param name="playReplay">Plays a replay by name, after this page has closed.</param>
    public HistoryPage(Func<string?> playRefusal, Func<string, Task> playReplay)
    {
        _playRefusal = playRefusal;
        _playReplay = playReplay;
        BackgroundColor = PageColor;

        var title = new Label
        {
            Text = "History",
            TextColor = Body,
            FontAttributes = FontAttributes.Bold,
            VerticalOptions = LayoutOptions.Center,
            Margin = new Thickness(0, 0, 16, 0),
        }.Scaled(18);
        _sessionsTab = Tab("Sessions", showReplays: false);
        _replaysTab = Tab("Replays", showReplays: true);
        var close = new Button
        {
            Text = Glyph.Close,
            TextColor = Muted,
            BackgroundColor = Colors.Transparent,
            BorderWidth = 0,
            Padding = new Thickness(10, 0),
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.Center,
        };
        close.SetBinding(Button.FontSizeProperty, ScaledFontBinding.For(16));
        SemanticProperties.SetDescription(close, "Close History");
        close.Clicked += async (_, _) => await CloseAsync();

        var header = new Grid
        {
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star)],
            // The top as ScorePage's, which clears Android's status bar.
            Padding = new Thickness(16, 24, 8, 8),
        };
        header.Add(title, 0);
        header.Add(_sessionsTab, 1);
        header.Add(_replaysTab, 2);
        header.Add(close, 3);

        _sessions = List(SessionTemplate());
        _replays = List(ReplayTemplate());
        _replays.IsVisible = false;
        _empty = new Label
        {
            TextColor = Muted,
            HorizontalTextAlignment = TextAlignment.Center,
            Margin = new Thickness(16, 32),
            IsVisible = false,
        }.Scaled(14);

        var body = new Grid();
        body.Add(_sessions);
        body.Add(_replays);
        body.Add(_empty);

        var root = new Grid
        {
            RowDefinitions = [new(GridLength.Auto), new(1), new(GridLength.Star)],
        };
        root.Add(header, 0, 0);
        root.Add(new BoxView { Color = Rule, HeightRequest = 1 }, 0, 1);
        root.Add(body, 0, 2);
        Content = root;

        ShowTab(showReplays: false);
    }

    // ------------------------------------------------------------------------ layout ----

    private Button Tab(string text, bool showReplays)
    {
        var button = new Button
        {
            Text = text,
            BackgroundColor = Colors.Transparent,
            BorderWidth = 0,
            Padding = new Thickness(10, 0),
            VerticalOptions = LayoutOptions.Center,
        };
        button.SetBinding(Button.FontSizeProperty, ScaledFontBinding.For(14));
        button.Clicked += (_, _) => ShowTab(showReplays);
        return button;
    }

    private static CollectionView List(DataTemplate template) => new()
    {
        ItemTemplate = template,
        SelectionMode = SelectionMode.None,
    };

    private static Label Line(Color color, double size, bool bold = false) => new Label
    {
        TextColor = color,
        LineBreakMode = LineBreakMode.TailTruncation,
        FontAttributes = bold ? FontAttributes.Bold : FontAttributes.None,
    }.Scaled(size);

    /// <summary>A row: its lines, its "..." menu of actions, and the rule under it. A tap on the row
    /// itself does nothing, so tap and double-click stay free for opening a Session.</summary>
    private static Grid Row(View content, string what, EventHandler more)
    {
        var row = new Grid
        {
            RowDefinitions = [new(GridLength.Auto), new(1)],
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)],
            BackgroundColor = PageColor,
        };
        var padded = new ContentView { Content = content, Padding = new Thickness(16, 8, 8, 8) };
        var button = new Button
        {
            Text = Glyph.More,
            TextColor = Body,
            BackgroundColor = Colors.Transparent,
            BorderWidth = 0,
            Padding = new Thickness(12, 0),
            MinimumWidthRequest = 44,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalOptions = LayoutOptions.Center,
        };
        button.SetBinding(Button.FontSizeProperty, ScaledFontBinding.For(18));
        SemanticProperties.SetDescription(button, what);
        button.Clicked += more;
        row.Add(padded, 0, 0);
        row.Add(button, 1, 0);
        var rule = new BoxView { Color = Rule, HeightRequest = 1 };
        row.Add(rule, 0, 1);
        Grid.SetColumnSpan(rule, 2);
        return row;
    }

    // Rows are filled when their item arrives rather than bound by property path: the text is worked
    // out once per item, and nothing here depends on reflection surviving the Android Release trim.

    private DataTemplate SessionTemplate() => new(() =>
    {
        var when = Line(Body, 14);
        var persona = Line(Body, 14, bold: true);
        persona.Margin = new Thickness(12, 0, 0, 0);
        var points = Line(Body, 14);
        points.HorizontalOptions = LayoutOptions.End;

        var top = new Grid
        {
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
        };
        top.Add(when, 0);
        top.Add(persona, 1);
        top.Add(points, 2);

        var detail = Line(Muted, 12);
        var labels = Line(Accent, 12);

        var lines = new VerticalStackLayout { Spacing = 2, Children = { top, detail, labels } };
        var row = Row(lines, "More: rename, tags", OnSessionMore);
        row.BindingContextChanged += (_, _) =>
        {
            if (row.BindingContext is not SessionItem item)
                return;
            when.Text = item.When;
            persona.Text = item.Persona;
            points.Text = item.Points;
            points.TextColor = item.PointsColor;
            detail.Text = item.Detail;
            labels.Text = item.Labels;
            labels.IsVisible = item.Labels.Length > 0;
        };
        return row;
    });

    private DataTemplate ReplayTemplate() => new(() =>
    {
        var name = Line(Body, 14, bold: true);
        var detail = Line(Muted, 12);
        var lines = new VerticalStackLayout { Spacing = 2, Children = { name, detail } };
        var row = Row(lines, "More: play, rename, share, delete", OnReplayMore);
        row.BindingContextChanged += (_, _) =>
        {
            if (row.BindingContext is not ReplayItem item)
                return;
            name.Text = item.Entry.Name;
            detail.Text = item.Detail;
        };
        return row;
    });

    private void ShowTab(bool showReplays)
    {
        // The tab already showing keeps its list and its scroll position.
        if (_tabShown && showReplays == _showingReplays)
            return;
        _tabShown = true;
        _showingReplays = showReplays;
        ShowEmpty(null);
        _sessionsTab.TextColor = showReplays ? Muted : Accent;
        _replaysTab.TextColor = showReplays ? Accent : Muted;
        _sessionsTab.FontAttributes = showReplays ? FontAttributes.None : FontAttributes.Bold;
        _replaysTab.FontAttributes = showReplays ? FontAttributes.Bold : FontAttributes.None;
        _sessions.IsVisible = !showReplays;
        _replays.IsVisible = showReplays;
        _ = showReplays ? LoadReplaysAsync() : LoadSessionsAsync();
    }

    private void ShowEmpty(string? text)
    {
        _empty.Text = text ?? string.Empty;
        _empty.IsVisible = text is not null;
    }

    // ---------------------------------------------------------------------- Sessions ----

    private async Task LoadSessionsAsync()
    {
        IReadOnlyList<SessionSummary> sessions;
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            sessions = await Task.Run(() => SessionHistoryReader.Read(MuckaPaths.GetDatabasePath(), now));
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            if (!_showingReplays)
                ShowEmpty($"Your Sessions could not be read: {ex.Message}");
            return;
        }
        if (_showingReplays)
            return;
        _sessionItems = new ObservableCollection<SessionItem>(sessions.Select(s => new SessionItem(s)));
        _sessions.ItemsSource = _sessionItems;
        ShowEmpty(sessions.Count == 0 ? "No Sessions yet. Each time you play a character, it is listed here." : null);
    }

    private async void OnSessionMore(object? sender, EventArgs e)
    {
        if (_busy || sender is not View { BindingContext: SessionItem item } button)
            return;
        _busy = true;
        try
        {
            var pick = await ActionMenu.ShowAsync(this, button, $"{item.Persona}, {item.When}", "Cancel", null,
                RenameChoice, TagsChoice);
            var id = item.Summary.Id;
            switch (pick)
            {
                case RenameChoice:
                    var typed = await DisplayPromptAsync("Name this Session", "Leave it blank to remove the name.",
                        "Save", "Cancel", initialValue: item.Summary.Name ?? string.Empty);
                    if (typed is null)
                        return;
                    var name = typed.Trim();
                    await WriteAsync(item,
                        () => SessionLabels.SetName(MuckaPaths.GetDatabasePath(), id, name),
                        item.Summary with { Name = name.Length == 0 ? null : name });
                    break;
                case TagsChoice:
                    var line = await DisplayPromptAsync("Tags", "Separate tags with commas.", "Save", "Cancel",
                        initialValue: string.Join(SessionLabels.Separator + " ", item.Summary.Tags));
                    if (line is null)
                        return;
                    var tags = SessionLabels.Parse(line);
                    await WriteAsync(item,
                        () => SessionLabels.SetTags(MuckaPaths.GetDatabasePath(), id, tags),
                        item.Summary with { Tags = SessionLabels.Ordered(tags) });
                    break;
            }
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>One edit, off the UI thread; then the row shows <paramref name="after"/> in place, so
    /// the list keeps its scroll position.</summary>
    private async Task WriteAsync(SessionItem item, Action write, SessionSummary after)
    {
        try
        {
            await Task.Run(write);
            var index = _sessionItems.IndexOf(item);
            if (index >= 0)
                _sessionItems[index] = new SessionItem(after);
        }
        // The write opens the store as the launch does, so it can also meet a failed schema upgrade
        // (InvalidOperationException) or a maintenance pass holding the file (OperationCanceledException).
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException
                                       or OperationCanceledException)
        {
            await DisplayAlertAsync("History", $"That could not be saved: {ex.Message}", "OK");
        }
    }

    // ----------------------------------------------------------------------- Replays ----

    private async Task LoadReplaysAsync()
    {
        List<ReplayEntry> replays;
        try
        {
            replays = await Task.Run(ReplayLibrary.List);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (_showingReplays)
                ShowEmpty($"Your replays could not be listed: {ex.Message}");
            return;
        }
        if (!_showingReplays)
            return;
        _replayItems = [.. replays.Select(r => new ReplayItem(r))];
        _replays.ItemsSource = _replayItems;
        ShowEmpty(replays.Count == 0 ? "No replays yet." : null);
    }

    private async void OnReplayMore(object? sender, EventArgs e)
    {
        if (_busy || sender is not View { BindingContext: ReplayItem item } button)
            return;
        _busy = true;
        try
        {
            await ReplayActionAsync(item.Entry, button);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task ReplayActionAsync(ReplayEntry entry, View button)
    {
        var pick = entry.Packaged
            ? await ActionMenu.ShowAsync(this, button, entry.Name, "Cancel", null, PlayChoice, ShareChoice)
            : await ActionMenu.ShowAsync(this, button, entry.Name, "Cancel", DeleteChoice, PlayChoice, RenameChoice, ShareChoice);
        try
        {
            switch (pick)
            {
                case PlayChoice:
                    if (_playRefusal() is { } refusal)
                    {
                        await DisplayAlertAsync("Play replay", refusal, "OK");
                        return;
                    }
                    await CloseAsync();
                    await _playReplay(entry.Name);
                    return;
                case RenameChoice:
                    await RenameReplayAsync(entry);
                    break;
                case ShareChoice:
                    await Share.Default.RequestAsync(new ShareFileRequest
                    {
                        Title = entry.Name,
                        File = new ShareFile(entry.Path),
                    });
                    break;
                case DeleteChoice:
                    if (await DisplayAlertAsync("Delete replay",
                            $"Delete \"{entry.Name}\"? This cannot be undone.", DeleteChoice, "Cancel"))
                        await Task.Run(() => File.Delete(entry.Path));
                    break;
                default:
                    return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await DisplayAlertAsync("Replays", ex.Message, "OK");
        }
        await LoadReplaysAsync();
    }

    private async Task RenameReplayAsync(ReplayEntry entry)
    {
        var typed = await DisplayPromptAsync("Rename replay", null, "Rename", "Cancel", initialValue: entry.Name);
        if (typed is null)
            return;
        var name = ReplayLibrary.NameFromTyped(typed);
        if (name == entry.Name)
            return;
        if (ReplayLibrary.RenameProblem(entry, name, _replayItems.Select(r => r.Entry)) is { } problem)
        {
            await DisplayAlertAsync("Rename replay", problem, "OK");
            return;
        }
        await Task.Run(() => ReplayLibrary.Rename(entry, name));
    }

    // ------------------------------------------------------------------- open, close ----

    /// <summary>Centres the content in a window wider than <see cref="MaxContentWidth"/>.</summary>
    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        if (Content is Grid root && width > 0)
        {
            var side = Math.Max(0, (width - MaxContentWidth) / 2);
            if (Math.Abs(root.Padding.Left - side) > 0.5)
                root.Padding = new Thickness(side, 0);
        }
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
#if WINDOWS
        if (Window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window win &&
            win.Content is Microsoft.UI.Xaml.UIElement root)
            root.PreviewKeyDown += OnWindowPreviewKeyDown;
#elif UIKIT
        MacEscape.Detach(_macEscape);
        _macEscape = MacEscape.Attach(this, () => _ = CloseAsync());
#endif
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
#if WINDOWS
        if (Window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window win &&
            win.Content is Microsoft.UI.Xaml.UIElement root)
            root.PreviewKeyDown -= OnWindowPreviewKeyDown;
#elif UIKIT
        MacEscape.Detach(_macEscape);
        _macEscape = null;
#endif
    }

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
        _ = CloseAsync();
        return true;
    }

#if WINDOWS
    private void OnWindowPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        // A row's menu, its prompt or alert, and its write own Escape until the action ends (_busy).
        if (e.Key == Windows.System.VirtualKey.Escape && Navigation.ModalStack is [.., HistoryPage] && !_busy)
        {
            e.Handled = true;
            _ = CloseAsync();
        }
    }
#endif

    // ------------------------------------------------------------------- row models ----

    /// <summary>What one Sessions row shows, worked out once.</summary>
    private sealed class SessionItem(SessionSummary summary)
    {
        public SessionSummary Summary { get; } = summary;

        public string When { get; } = DateTimeOffset.FromUnixTimeMilliseconds(summary.StartedMs).LocalDateTime
            .ToString("ddd d MMM yyyy  HH:mm", CultureInfo.CurrentCulture);

        public string Persona { get; } = summary.Persona ?? "(character not recorded)";

        public string Points { get; } = summary.Points is not { } p ? string.Empty
            : (p > 0 ? "+" : string.Empty) + p.ToString("N0", CultureInfo.CurrentCulture);

        public Color PointsColor { get; } = summary.Points switch { > 0 => Gain, < 0 => Loss, _ => Muted };

        public string Detail { get; } = string.Join("   ", new[]
        {
            summary.Host,
            summary.DurationMs is { } ms ? Duration(ms) : null,
            summary.EndedNote,
        }.Where(s => !string.IsNullOrEmpty(s)));

        public string Labels { get; } = string.Join("   ", new[] { summary.Name }
            .Concat(summary.Tags.Select(t => "#" + t))
            .Where(s => !string.IsNullOrEmpty(s)));

        private static string Duration(long ms)
        {
            var span = TimeSpan.FromMilliseconds(Math.Max(0, ms));
            return span.TotalMinutes < 1 ? "under a minute"
                : span.TotalHours < 1 ? $"{(int)span.TotalMinutes}m"
                : $"{(int)span.TotalHours}h {span.Minutes:00}m";
        }
    }

    /// <summary>What one Replays row shows.</summary>
    private sealed class ReplayItem(ReplayEntry entry)
    {
        public ReplayEntry Entry { get; } = entry;

        public string Detail { get; } = string.Join("   ", new[]
        {
            entry.Modified.ToString("d MMM yyyy  HH:mm", CultureInfo.CurrentCulture),
            Size(entry.Bytes),
            entry.Packaged ? "comes with Mucka" : null,
        }.Where(s => s is not null));

        private static string Size(long bytes)
            => bytes < 1024 * 1024
                ? $"{Math.Max(1, bytes / 1024)} KB"
                : $"{bytes / (1024.0 * 1024.0):0.0} MB";
    }
}
