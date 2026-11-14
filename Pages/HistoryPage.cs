using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Mucka.Combat;
using Mucka.Core;
using Mucka.Store;
using Mucka.WireLog;

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
    /// <summary>Every other row: <see cref="PageColor"/> plus 10 on each channel.</summary>
    private static readonly Color AlternateRow = Color.FromArgb("#161616");
    /// <summary>The selected row: the page colour pulled toward <see cref="Accent"/>.</summary>
    private static readonly Color SelectedRow = Color.FromArgb("#16263A");
    private static readonly Color Rule = Color.FromArgb("#222222");
    private static readonly Color Body = Color.FromArgb("#E6E6E6");
    private static readonly Color Muted = Color.FromArgb("#8B949E");
    private static readonly Color Accent = Color.FromArgb("#58A6FF");
    private static readonly Color Gain = Color.FromArgb("#3FB950");
    private static readonly Color Loss = Color.FromArgb("#F85149");

    /// <summary>The widest the lists grow on a desktop window; past it a row is mostly gap.</summary>
    private const double MaxContentWidth = 820;

    /// <summary>The date column's width, authored against its 14-point text and scaled with it, so the
    /// character names after it line up. Wide enough for the widest date the format makes
    /// ("Wed 28 Nov 2026  20:57"), measured by eye on Windows.</summary>
    private const double DateColumnWidth = 14 * 11.5;

    /// <summary>What a Session that ended in permanent death is called: the game's own word, where the
    /// store says <see cref="PersonaSessionEnd.Permadeath"/>.</summary>
    private const string DeadDead = "deaddead";

    private static readonly string[] SortChoices =
        ["Newest first", "Oldest first", "Most points", "Fewest points", "Longest", "Shortest"];

    private const string AllCharacters = "All characters";

    private const string RenameChoice = "Rename";
    private const string OpenChoice = "Open";
    private const string TitleChoice = "Set title";
    private const string TagsChoice = "Tags";
    private const string SaveAsChoice = "Save as...";
    private const string TrimChoice = "Trim";
    private const string ReplayFormat = "Replay (.muckrep)";
    private const string TextFormat = "Text log (.txt)";
    private const string ColourFormat = "Colour log (.log)";
    private const string WebFormat = "Web page (.html)";
    private static readonly string[] SaveFormats = [ReplayFormat, TextFormat, ColourFormat, WebFormat];
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
    private readonly Grid _sessionTools;
    private readonly Entry _filter;
    private readonly Picker _character;
    private readonly Picker _sort;
    private List<SessionSummary> _allSessions = [];
    private int _filterVersion;

    /// <summary>How long the filter box waits after a keystroke before the list is rebuilt.</summary>
    private const int FilterPauseMs = 200;
    private ObservableCollection<SessionItem> _sessionItems = [];
    private List<ReplayItem> _replayItems = [];
    private bool _showingReplays;
    private bool _tabShown;
    private bool _closed;
    // Set from a row's menu opening until its action has finished: one action at a time, and Escape
    // belongs to the menu or prompt over the page meanwhile.
    private bool _busy;
    // The selected Session's id, kept across the list being rebuilt by the filter and the sort.
    private long? _selectedId;
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

        _filter = new Entry
        {
            Placeholder = "Filter: character, server, title, tag, outcome",
            TextColor = Body,
            PlaceholderColor = Muted,
            ClearButtonVisibility = ClearButtonVisibility.WhileEditing,
            VerticalOptions = LayoutOptions.Center,
        };
        _filter.SetBinding(Entry.FontSizeProperty, ScaledFontBinding.For(13));
        _filter.TextChanged += async (_, _) =>
        {
            // The list is rebuilt once typing pauses, not on every keystroke.
            var typed = ++_filterVersion;
            await Task.Delay(FilterPauseMs);
            if (typed == _filterVersion)
                ShowSessions();
        };
        _character = ToolPicker("Character");
        _character.ItemsSource = new List<string> { AllCharacters };
        _character.SelectedIndex = 0;
        _character.SelectedIndexChanged += (_, _) => ShowSessions();
        _sort = ToolPicker("Sort");
        _sort.ItemsSource = SortChoices;
        _sort.SelectedIndex = 0;
        _sort.SelectedIndexChanged += (_, _) => ShowSessions();
        _sessionTools = new Grid
        {
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)],
            ColumnSpacing = 8,
            Padding = new Thickness(16, 6, 16, 6),
        };
        _sessionTools.Add(_filter, 0);
        _sessionTools.Add(_character, 1);
        _sessionTools.Add(_sort, 2);

        var body = new Grid();
        body.Add(_sessions);
        body.Add(_replays);
        body.Add(_empty);

        var root = new Grid
        {
            RowDefinitions = [new(GridLength.Auto), new(1), new(GridLength.Auto), new(GridLength.Star)],
        };
        root.Add(header, 0, 0);
        root.Add(new BoxView { Color = Rule, HeightRequest = 1 }, 0, 1);
        root.Add(_sessionTools, 0, 2);
        root.Add(body, 0, 3);
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

    private static Picker ToolPicker(string title)
    {
        var picker = new Picker { Title = title, TextColor = Body, TitleColor = Muted, VerticalOptions = LayoutOptions.Center };
        picker.SetBinding(Picker.FontSizeProperty, ScaledFontBinding.For(13));
        return picker;
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

    /// <summary>A row: its lines, its "..." menu of actions, and the rule under it. With
    /// <paramref name="open"/>, the row itself opens on a double-click, or a tap on a phone or tablet,
    /// where there is no double-click; with <paramref name="select"/>, a single click on a desktop
    /// selects it.</summary>
    private static Grid Row(View content, string what, EventHandler more, EventHandler<TappedEventArgs>? open = null,
        EventHandler<TappedEventArgs>? select = null)
    {
        var row = new Grid
        {
            RowDefinitions = [new(GridLength.Auto), new(1)],
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)],
            BackgroundColor = PageColor,
        };
        // Transparent rather than no background, so the padding takes a click as the text does.
        var padded = new ContentView
        {
            Content = content,
            Padding = new Thickness(16, 8, 8, 8),
            BackgroundColor = Colors.Transparent,
        };
        // By platform, not idiom: the Mac build runs as an iPad app and may report a tablet.
        var desktop = OperatingSystem.IsWindows() || OperatingSystem.IsMacCatalyst();
        if (open is not null)
        {
            var gesture = new TapGestureRecognizer { NumberOfTapsRequired = desktop ? 2 : 1 };
            gesture.Tapped += open;
            padded.GestureRecognizers.Add(gesture);
        }
        if (select is not null && desktop)
        {
            var gesture = new TapGestureRecognizer { NumberOfTapsRequired = 1 };
            gesture.Tapped += select;
            padded.GestureRecognizers.Add(gesture);
        }
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
        when.SetBinding(WidthRequestProperty, ScaledFontBinding.For(DateColumnWidth));
        var persona = Line(Body, 14, bold: true);
        var points = Line(Body, 14);
        points.HorizontalOptions = LayoutOptions.End;
        points.Margin = new Thickness(12, 0, 0, 0);

        var top = new Grid
        {
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
        };
        top.Add(when, 0);
        top.Add(persona, 1);
        top.Add(points, 2);

        var host = Line(Muted, 12);
        var played = Line(Muted, 12);
        played.HorizontalOptions = LayoutOptions.End;
        played.Margin = new Thickness(12, 0, 0, 0);
        var second = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)] };
        second.Add(host, 0);
        second.Add(played, 1);

        var labels = Line(Accent, 12);

        var lines = new VerticalStackLayout { Spacing = 2, Children = { top, second, labels } };
        var row = Row(lines, "More: open, set title, tags, save as, trim, delete", OnSessionMore, OnSessionOpen,
            OnSessionSelect);
        // The row follows its item's selection in place: the item is never replaced to repaint it,
        // which between the two clicks of a double-click would drop the second.
        SessionItem? shown = null;
        void Paint() => row.BackgroundColor = shown is { Selected: true } ? SelectedRow
            : shown?.Index % 2 == 1 ? AlternateRow : PageColor;
        void OnItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Paint();
        row.BindingContextChanged += (_, _) =>
        {
            if (shown is not null)
                shown.PropertyChanged -= OnItemChanged;
            shown = row.BindingContext as SessionItem;
            if (shown is not { } item)
                return;
            item.PropertyChanged += OnItemChanged;
            Paint();
            when.Text = item.When;
            persona.Text = item.Persona;
            points.Text = item.Points;
            points.TextColor = item.PointsColor;
            host.Text = item.Where;
            host.TextColor = item.Summary.ShownTitle is null ? Muted : Body;
            played.Text = item.Played;
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
            row.BackgroundColor = item.Index % 2 == 0 ? PageColor : AlternateRow;
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
        _sessionTools.IsVisible = !showReplays;
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
        _allSessions = [.. sessions];

        // The character list follows what was read; a character no longer there falls back to all.
        var chosen = _character.SelectedItem as string;
        var characters = new List<string> { AllCharacters };
        characters.AddRange(sessions.Select(s => s.Persona).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase));
        _character.ItemsSource = characters;
        _character.SelectedIndex = Math.Max(0,
            characters.FindIndex(c => string.Equals(c, chosen, StringComparison.OrdinalIgnoreCase)));

        ShowSessions();
    }

    /// <summary>Lists the Sessions the filter, character and sort pick out. Cheap: the list is in
    /// memory, and nothing is read again.</summary>
    private void ShowSessions()
    {
        if (_showingReplays)
            return;
        var character = _character.SelectedIndex > 0 ? _character.SelectedItem as string : null;
        var text = _filter.Text?.Trim().TrimStart('#') ?? string.Empty;

        var picked = _allSessions.Where(s =>
            (character is null || string.Equals(s.Persona, character, StringComparison.OrdinalIgnoreCase))
            && (text.Length == 0 || Matches(s, text)));
        picked = _sort.SelectedIndex switch
        {
            1 => picked.OrderBy(s => s.StartedMs).ThenBy(s => s.Id),
            2 => picked.OrderBy(s => s.Points is null).ThenByDescending(s => s.Points),
            3 => picked.OrderBy(s => s.Points is null).ThenBy(s => s.Points),
            4 => picked.OrderBy(s => s.DurationMs is null).ThenByDescending(s => s.DurationMs),
            5 => picked.OrderBy(s => s.DurationMs is null).ThenBy(s => s.DurationMs),
            _ => picked,
        };

        _sessionItems = new ObservableCollection<SessionItem>(
            picked.Select((s, i) => new SessionItem(s, i) { Selected = s.Id == _selectedId }));
        _sessions.ItemsSource = _sessionItems;
        ShowEmpty(_allSessions.Count == 0 ? "No Sessions yet. Each time you play a character, it is listed here."
            : _sessionItems.Count == 0 ? "No Sessions match."
            : null);
    }

    private static bool Matches(SessionSummary s, string text)
    {
        bool Has(string? field) => field?.Contains(text, StringComparison.OrdinalIgnoreCase) == true;
        return Has(s.Persona) || Has(s.Host) || Has(s.Title ?? s.DefaultTitle) || Has(Outcome(s.EndedNote))
               || s.Tags.Any(Has);
    }

    /// <summary>How a Session's end reads in the list: the stored note, in the game's own words.</summary>
    private static string? Outcome(string? endedNote)
        => endedNote == PersonaSessionEnd.Permadeath ? DeadDead : endedNote;

    private void OnSessionSelect(object? sender, TappedEventArgs e)
    {
        if (sender is View { BindingContext: SessionItem item })
            Select(item);
    }

    /// <summary>Highlights <paramref name="item"/> as the one selected Session, and no other. It
    /// stays selected while the list is filtered or sorted, as long as it is listed.</summary>
    private void Select(SessionItem item)
    {
        _selectedId = item.Summary.Id;
        foreach (var other in _sessionItems)
            other.Selected = ReferenceEquals(other, item);
    }

    private async void OnSessionOpen(object? sender, TappedEventArgs e)
    {
        if (_busy || sender is not View { BindingContext: SessionItem item })
            return;
        Select(item);
        _busy = true;
        try
        {
            await AlertAsync(OpenChoice, $"{OpenChoice} is not built yet.");
        }
        finally
        {
            _busy = false;
        }
    }

    private async void OnSessionMore(object? sender, EventArgs e)
    {
        if (_busy || sender is not View { BindingContext: SessionItem item } button)
            return;
        Select(item);
        _busy = true;
        try
        {
            // Every dialog this menu opens names the whole row, so a mis-click shows which Session it is.
            var pick = await ActionMenu.ShowAsync(this, button, item.Described, "Cancel", DeleteChoice,
                [OpenChoice, TitleChoice, TagsChoice, SaveAsChoice, TrimChoice],
                new Dictionary<string, string[]> { [SaveAsChoice] = SaveFormats });
            var id = item.Summary.Id;
            switch (pick)
            {
                case TitleChoice:
                    var typed = await DisplayPromptAsync("Set title",
                        item.Described + "\n\nLeave it blank to show the server again.",
                        "Save", "Cancel", initialValue: item.Summary.Title ?? item.Summary.DefaultTitle);
                    if (typed is null)
                        return;
                    var title = item.Summary.TitleToStore(typed);
                    await WriteAsync(item,
                        () => SessionLabels.SetTitle(MuckaPaths.GetDatabasePath(), id, title),
                        item.Summary with { Title = title });
                    break;
                case TagsChoice:
                    var line = await DisplayPromptAsync("Tags", item.Described + "\n\nSeparate tags with commas.",
                        "Save", "Cancel", initialValue: string.Join(SessionLabels.Separator + " ", item.Summary.Tags));
                    if (line is null)
                        return;
                    var tags = SessionLabels.Parse(line);
                    await WriteAsync(item,
                        () => SessionLabels.SetTags(MuckaPaths.GetDatabasePath(), id, tags),
                        item.Summary with { Tags = SessionLabels.Ordered(tags) });
                    break;
                case ReplayFormat:
                    await MakeReplayAsync(item);
                    break;
                case TextFormat or ColourFormat when !OperatingSystem.IsWindows() && !OperatingSystem.IsMacCatalyst():
                    await AlertAsync(pick, "If you want this on your phone, send Ollie a mudmail.");
                    break;
                case TextFormat:
                    await SaveTextLogAsync(item);
                    break;
                // Placeholders: on the menu so the shape is visible, and each says it is not built.
                case OpenChoice or TrimChoice or DeleteChoice or ColourFormat or WebFormat:
                    await AlertAsync(pick, $"{pick} is not built yet.");
                    break;
            }
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Writes the Session's recording as a replay in the player's replay folder, where the
    /// Replays tab lists it; on a phone or tablet the share sheet then offers it.</summary>
    private async Task MakeReplayAsync(SessionItem item)
    {
        const string Heading = "Save as replay";
        var summary = item.Summary;
        var typed = await DisplayPromptAsync(Heading,
            item.Described + "\n\nName the replay. It is listed on the Replays tab.", "Save",
            "Cancel", initialValue: ReplayLibrary.OfferedName(summary.Title ?? summary.DefaultTitle));
        if (typed is null)
            return;
        var name = ReplayLibrary.NameFromTyped(typed);
        try
        {
            var listed = await Task.Run(ReplayLibrary.List);
            if (ReplayLibrary.NewNameProblem(name, listed) is { } problem)
            {
                await AlertAsync(Heading, problem);
                return;
            }
            var path = ReplayLibrary.NewPath(name);
            var id = summary.Id;
            var database = MuckaPaths.GetDatabasePath();
            var written = await Task.Run(() =>
            {
                if (SessionSlice.Read(database, id) is not { IsEmpty: false } slice)
                    return false;
                Muckrep.WriteNewFile(path, slice.ToReplay());
                return true;
            });
            if (!written)
            {
                await AlertAsync(Heading, "This Session has no recording left.");
                return;
            }
#if ANDROID || IOS
            try
            {
                if (!_closed)
                    await Share.Default.RequestAsync(new ShareFileRequest { Title = name, File = new ShareFile(path) });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                await AlertAsync(Heading, $"Saved as \"{name}\" on the Replays tab, but it could not be shared: {ex.Message}");
            }
#else
            await AlertAsync(Heading, $"Saved as \"{name}\" on the Replays tab.");
#endif
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException
                                       or InvalidOperationException)
        {
            await AlertAsync(Heading, $"The replay could not be made: {ex.Message}");
        }
    }

    /// <summary>Writes the Session's text log (<see cref="SessionTextLog"/>) where the player picks in
    /// the system's save dialog. Desktop only: a phone or tablet never offers it.</summary>
    private async Task SaveTextLogAsync(SessionItem item)
    {
        const string Heading = "Save as text log";
        var summary = item.Summary;
        var name = ReplayLibrary.OfferedName(summary.Title ?? summary.DefaultTitle);
        string? path = null;
        var opened = false;
#if MACCATALYST
        // The Mac's dialog exports a file that already exists, so the log is written first, to a
        // folder of its own, and copied where the player picks.
        var scratch = Path.Combine(FileSystem.CacheDirectory, "text-logs", Guid.NewGuid().ToString("N"));
#endif
        try
        {
#if MACCATALYST
            Directory.CreateDirectory(scratch);
            path = Path.Combine(scratch, name + ".txt");
#elif WINDOWS
            path = await TextLogPathAsync(name);
#endif
            if (path is null)
                return;
            var target = path;
            var id = summary.Id;
            var host = summary.Host;
            var database = MuckaPaths.GetDatabasePath();
            var written = await Task.Run(async () =>
            {
                if (SessionSlice.Read(database, id) is not { IsEmpty: false } slice)
                    return false;
                // The Windows picker has already asked about replacing a file; anywhere else one that
                // exists is never touched.
                var stream = new FileStream(target, OperatingSystem.IsWindows() ? FileMode.Create : FileMode.CreateNew,
                    FileAccess.Write);
                opened = true;
                await SessionTextLog.WriteAsync(slice, host, new StreamWriter(stream, new System.Text.UTF8Encoding(false)));
                return true;
            });
            if (!written)
            {
                DeleteUnwritten(path, opened);
                await AlertAsync(Heading, "This Session has no recording left.");
                return;
            }
#if MACCATALYST
            var exported = await ExportAsync(path);
            if (exported is null)
                return;   // cancelled; the scratch copy goes below
            path = exported;
#endif
            await AlertAsync(Heading, $"Saved as {path}");
        }
        // Whatever stopped it, the player is told, and no empty or half-written file is left behind.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (path is not null)
                DeleteUnwritten(path, opened);
            await AlertAsync(Heading, $"The text log could not be saved: {ex.Message}");
        }
#if MACCATALYST
        finally
        {
            try
            {
                Directory.Delete(scratch, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The system clears its cache folder in time.
            }
        }
#endif
    }

#if MACCATALYST
    /// <summary>Shows the system's export dialog for <paramref name="file"/>, which it copies; returns
    /// where the copy went, or null when the player cancelled.</summary>
    private static Task<string?> ExportAsync(string file)
    {
        if (Microsoft.Maui.ApplicationModel.Platform.GetCurrentUIViewController() is not { } presenter)
            throw new InvalidOperationException("There is no window to show the save dialog over.");
        var chosen = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var picker = new UIKit.UIDocumentPickerViewController([Foundation.NSUrl.FromFilename(file)], asCopy: true);
        picker.DidPickDocumentAtUrls += (_, e) => chosen.TrySetResult(e.Urls.FirstOrDefault()?.Path);
        picker.WasCancelled += (_, _) => chosen.TrySetResult(null);
        presenter.PresentViewController(picker, true, null);
        return chosen.Task;
    }
#endif

    /// <summary>Removes what a failed save left: the file it opened, or the empty one the Windows
    /// picker makes when it returns. Never a file with anything in it that this save did not
    /// write.</summary>
    private static void DeleteUnwritten(string path, bool opened)
    {
        try
        {
            if (opened || (OperatingSystem.IsWindows() && new FileInfo(path) is { Exists: true, Length: 0 }))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left where it is; the alert has already said the save failed.
        }
    }

#if WINDOWS
    /// <summary>Where a text log called <paramref name="name"/> goes, or null when the player
    /// cancelled.</summary>
    private async Task<string?> TextLogPathAsync(string name)
    {
        if (Window?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window window)
        {
            await AlertAsync("Save as text log", "The save dialog could not be opened.");
            return null;
        }
        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            SuggestedFileName = name,
        };
        picker.FileTypeChoices.Add("Text log", new List<string> { ".txt" });
        // An unpackaged WinUI app's picker has no window of its own until it is given one.
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }
#endif

    /// <summary>An alert over this page, or nothing once it has closed.</summary>
    private Task AlertAsync(string title, string message)
        => _closed ? Task.CompletedTask : DisplayAlertAsync(title, message, "OK");

    /// <summary>One edit, off the UI thread; then the row shows <paramref name="after"/> in place, so
    /// the list keeps its scroll position.</summary>
    private async Task WriteAsync(SessionItem item, Action write, SessionSummary after)
    {
        try
        {
            await Task.Run(write);
            var all = _allSessions.FindIndex(s => s.Id == after.Id);
            if (all >= 0)
                _allSessions[all] = after;
            var index = _sessionItems.IndexOf(item);
            if (index >= 0)
                _sessionItems[index] = new SessionItem(after, item.Index) { Selected = item.Selected };
        }
        // The write opens the store as the launch does, so it can also meet a failed schema upgrade
        // (InvalidOperationException) or a maintenance pass holding the file (OperationCanceledException).
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException
                                       or OperationCanceledException)
        {
            await AlertAsync("History", $"That could not be saved: {ex.Message}");
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
        _replayItems = [.. replays.Select((r, i) => new ReplayItem(r, i))];
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

    /// <summary>What one Sessions row shows, worked out once. <paramref name="index"/> is its place in
    /// the list as shown, which picks its background.</summary>
    private sealed class SessionItem(SessionSummary summary, int index) : System.ComponentModel.INotifyPropertyChanged
    {
        public SessionSummary Summary { get; } = summary;

        private bool _selected;

        /// <summary>The selected Session, highlighted (<see cref="SelectedRow"/>).</summary>
        public bool Selected
        {
            get => _selected;
            set
            {
                if (_selected == value)
                    return;
                _selected = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Selected)));
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public int Index { get; } = index;

        public string When { get; } = DateTimeOffset.FromUnixTimeMilliseconds(summary.StartedMs).LocalDateTime
            .ToString("ddd d MMM yyyy  HH:mm", CultureInfo.CurrentCulture);

        public string Persona { get; } = summary.Persona ?? "(character not recorded)";

        public string Points { get; } = summary.Points is not { } p ? string.Empty
            : (p > 0 ? "+" : string.Empty) + p.ToString("N0", CultureInfo.CurrentCulture);

        public Color PointsColor { get; } = summary.Points switch { > 0 => Gain, < 0 => Loss, _ => Muted };

        /// <summary>How long it lasted and how it ended, under the points.</summary>
        public string Played { get; } = string.Join("   ", new[]
        {
            summary.DurationMs is { } ms ? Duration(ms) : null,
            Outcome(summary.EndedNote),
        }.Where(s => !string.IsNullOrEmpty(s)));

        /// <summary>Under the date: the title the player gave it, else the server.</summary>
        public string Where { get; } = summary.ShownTitle ?? summary.Host;

        public string Labels { get; } = string.Join("   ", summary.Tags.Select(t => "#" + t));

        /// <summary>Everything the row shows, as lines of text, for a dialog about it.</summary>
        public string Described => string.Join("\n", new[]
        {
            $"{Persona}   {When}   {Points}".TrimEnd(),
            string.Join("   ", new[] { Where, Played }.Where(s => s.Length > 0)),
            Labels,
        }.Where(s => s.Length > 0));

        private static string Duration(long ms)
        {
            var span = TimeSpan.FromMilliseconds(Math.Max(0, ms));
            return span.TotalMinutes < 1 ? $"{Math.Max(1, (int)span.TotalSeconds)}s"
                : span.TotalHours < 1 ? $"{(int)span.TotalMinutes}m"
                : $"{(int)span.TotalHours}h {span.Minutes:00}m";
        }
    }

    /// <summary>What one Replays row shows. <paramref name="index"/> is its place in the list.</summary>
    private sealed class ReplayItem(ReplayEntry entry, int index)
    {
        public ReplayEntry Entry { get; } = entry;

        public int Index { get; } = index;

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
