using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Mucka.Combat;
using Mucka.Core;
using Mucka.Store;
using Mucka.Terminal;
using Mucka.WireLog;

namespace Mucka.Pages;

/// <summary>
/// Logs, opened from the connect page's header: the player's Sessions (one login each) and the
/// replays they can play. Built in code, as <see cref="ScorePage"/> is.
///
/// <para>Closes on its x, on Escape (Windows and Mac), or on the system back button.</para>
/// </summary>
internal sealed class LogsPage : ContentPage
{
    internal static readonly Color PageColor = Color.FromArgb("#0C0C0C");
    /// <summary>Every other row: <see cref="PageColor"/> plus 10 on each channel.</summary>
    internal static readonly Color AlternateRow = Color.FromArgb("#161616");
    /// <summary>The selected row: the page colour pulled toward <see cref="Accent"/>.</summary>
    internal static readonly Color SelectedRow = Color.FromArgb("#16263A");
    internal static readonly Color Rule = Color.FromArgb("#222222");
    internal static readonly Color Body = Color.FromArgb("#E6E6E6");
    internal static readonly Color Muted = Color.FromArgb("#8B949E");
    internal static readonly Color Accent = Color.FromArgb("#58A6FF");
    internal static readonly Color Gain = Color.FromArgb("#3FB950");
    internal static readonly Color Loss = Color.FromArgb("#F85149");

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
    private const string SimpleColorFormat = "Simple Color Log (.log)";
    private const string FancyColorFormat = "Fancy Color Log (.log)";
    private const string WebFormat = "Web page (.html)";
    private static readonly string[] SaveFormats = [ReplayFormat, TextFormat, SimpleColorFormat, FancyColorFormat, WebFormat];
    private const string PlayChoice = "Play";
    private const string ShareChoice = "Share";
    private const string DeleteChoice = "Delete";
    // Delete's backups: each saves the Session first, then deletes it; a save that fails or is
    // cancelled deletes nothing.
    private const string BackupReplay = "Save a Replay first";
    private const string BackupText = "Save a Text log first";
    private const string BackupSimple = "Save a Simple Color Log first";
    private const string BackupFancy = "Save a Fancy Color Log first";
    private const string BackupWeb = "Save a Web page first";

    /// <summary>What a log is saved as.</summary>
    private enum LogKind { Text, SimpleColor, FancyColor, Web }
    private const string DeleteWithoutBackup = "Delete without saving";

    private enum Shown { Sessions, Replays, Maintenance }

    /// <summary>A head and shoulders, on a 16-unit square: the Sessions list's Character icon.</summary>
    private const string PersonPath = "M 10.5,5 A 2.5,2.5 0 1 1 5.5,5 A 2.5,2.5 0 1 1 10.5,5 M 2.5,14.5 C 2.5,9.5 13.5,9.5 13.5,14.5";
    /// <summary>An up arrow beside a down arrow, on a 16-unit square: the Sessions list's Sort icon.</summary>
    private const string SortPath = "M 5,13.5 L 5,2.5 M 2.5,5 L 5,2.5 L 7.5,5 M 11,2.5 L 11,13.5 M 8.5,11 L 11,13.5 L 13.5,11";

    private readonly Func<string?> _playRefusal;
    private readonly Func<string, Task> _playReplay;
    private readonly Grid _root;
    private readonly Button _sessionsTab;
    private readonly Button _replaysTab;
    private readonly Button _maintenanceTab;
    // The Maintenance tab: the passes over every Session's recording, and what they would work on.
    private readonly ScrollView _maintenance;
    private readonly Label _waiting;
    // Restore and Flush with their (i)s: shown while there is a trim backup.
    private View[] _trimBackupControls = [];
    private readonly Label _fileSize;
    private readonly Label _backupSize;
    private readonly CollectionView _sessions;
    private readonly CollectionView _replays;
    private readonly Label _empty;
    private readonly Grid _sessionTools;
    private readonly Entry _filter;
    private readonly IconButton _characterIcon;
    private readonly IconButton _sortIcon;
    // The character the list shows, or null for all; and the sort, an index into SortChoices.
    private string? _character;
    private int _sort;
    private List<string> _characters = [];
    private List<SessionSummary> _allSessions = [];
    private int _filterVersion;

    /// <summary>How long the filter box waits after a keystroke before the list is rebuilt.</summary>
    internal const int FilterPauseMs = 200;
    private ObservableCollection<SessionItem> _sessionItems = [];
    private List<ReplayItem> _replayItems = [];
    private Shown _shown;
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
    public LogsPage(Func<string?> playRefusal, Func<string, Task> playReplay)
    {
        _playRefusal = playRefusal;
        _playReplay = playReplay;
        BackgroundColor = PageColor;

        var title = new Label
        {
            Text = "Logs",
            TextColor = Body,
            FontAttributes = FontAttributes.Bold,
            VerticalOptions = LayoutOptions.Center,
            Margin = new Thickness(0, 0, 16, 0),
        }.Scaled(18);
        _sessionsTab = Tab("Sessions", Shown.Sessions);
        _replaysTab = Tab("Replays", Shown.Replays);
        _maintenanceTab = Tab("Maintenance", Shown.Maintenance);
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
        SemanticProperties.SetDescription(close, "Close Logs");
        close.Clicked += async (_, _) =>
        {
            if (!_busy)
                await CloseAsync();
        };

        var header = new Grid
        {
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto),
                new(GridLength.Star)],
            // The top as ScorePage's, which clears Android's status bar.
            Padding = new Thickness(16, 24, 8, 8),
        };
        header.Add(title, 0);
        header.Add(_sessionsTab, 1);
        header.Add(_replaysTab, 2);
        header.Add(_maintenanceTab, 3);
        header.Add(close, 4);

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
        // Character and sort are icons opening a drop-down, as the rows' "..." does: a labelled picker
        // put its title above its box and pushed the toolbar out of line (operator).
        _characterIcon = new IconButton(PersonPath, "Character: all characters");
        _characterIcon.Tapped += async () => await PickCharacterAsync();
        _sortIcon = new IconButton(SortPath, "Sort: " + SortChoices[0]);
        _sortIcon.Tapped += async () => await PickSortAsync();
        _sessionTools = new Grid
        {
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)],
            ColumnSpacing = 4,
            Padding = new Thickness(16, 6, 12, 6),
        };
        _sessionTools.Add(_filter, 0);
        _sessionTools.Add(_characterIcon.View, 1);
        _sessionTools.Add(_sortIcon.View, 2);

        _waiting = Line(Muted, 13);
        _fileSize = Line(Muted, 12);
        _fileSize.LineBreakMode = LineBreakMode.WordWrap;
        _backupSize = Line(Muted, 13);
        _maintenance = MaintenanceView();
        _maintenance.IsVisible = false;

        var body = new Grid();
        body.Add(_sessions);
        body.Add(_replays);
        body.Add(_maintenance);
        body.Add(_empty);

        _root = new Grid
        {
            RowDefinitions = [new(GridLength.Auto), new(1), new(GridLength.Auto), new(GridLength.Star)],
        };
        _root.Add(header, 0, 0);
        _root.Add(new BoxView { Color = Rule, HeightRequest = 1 }, 0, 1);
        _root.Add(_sessionTools, 0, 2);
        _root.Add(body, 0, 3);

        var page = new Grid();
        page.Add(_root);
        // Over everything while a delete, a trim or a Maintenance pass runs (Maintenance.Current).
        page.Add(new MaintenanceOverlay());
        Content = page;

        ShowTab(Shown.Sessions);
    }

    // ------------------------------------------------------------------------ layout ----

    private Button Tab(string text, Shown tab)
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
        button.Clicked += (_, _) => ShowTab(tab);
        return button;
    }


    /// <summary>A flat icon button drawn as a path, since no icon font is on every platform. Its
    /// stroke and outline are the accent colour while its panel is open, and its stroke alone while
    /// what it opens has something set (a filter).</summary>
    internal sealed class IconButton
    {
        private readonly Microsoft.Maui.Controls.Shapes.Path _path;
        private readonly Border _frame;
        private bool _open;
        private bool _set;

        public IconButton(string data, string what)
        {
            _path = new Microsoft.Maui.Controls.Shapes.Path
            {
                Data = (Microsoft.Maui.Controls.Shapes.Geometry?)new Microsoft.Maui.Controls.Shapes.PathGeometryConverter()
                    .ConvertFromInvariantString(data),
                StrokeThickness = 1.6,
                WidthRequest = 16,
                HeightRequest = 16,
            };
            _frame = new Border
            {
                Content = _path,
                Padding = new Thickness(8),
                StrokeThickness = 1,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 4 },
                BackgroundColor = Colors.Transparent,
                VerticalOptions = LayoutOptions.Center,
            };
            Describe(what);
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => Tapped?.Invoke();
            _frame.GestureRecognizers.Add(tap);
            Paint();
        }

        public View View => _frame;

        public event Action? Tapped;

        /// <summary>What it is and does, for a screen reader and as its tooltip.</summary>
        public void Describe(string what)
        {
            SemanticProperties.SetDescription(_frame, what);
            ToolTipProperties.SetText(_frame, what);
        }

        /// <summary>Its panel is open.</summary>
        public bool Open
        {
            get => _open;
            set { _open = value; Paint(); }
        }

        /// <summary>What it opens has something set.</summary>
        public bool Set
        {
            get => _set;
            set { _set = value; Paint(); }
        }

        private void Paint()
        {
            _path.Stroke = new SolidColorBrush(_open || _set ? Accent : Body);
            _frame.Stroke = new SolidColorBrush(_open ? Accent : Colors.Transparent);
        }
    }

    /// <summary>A flat text button for the viewer card: its "...", its close, its panels' buttons.</summary>
    internal static Button ToolbarButton(string text, string what, double size = 13)
    {
        var button = new Button
        {
            Text = text,
            TextColor = Body,
            BackgroundColor = Colors.Transparent,
            BorderWidth = 0,
            Padding = new Thickness(10, 0),
            MinimumWidthRequest = 40,
            VerticalOptions = LayoutOptions.Center,
        };
        button.SetBinding(Button.FontSizeProperty, ScaledFontBinding.For(size));
        SemanticProperties.SetDescription(button, what);
        return button;
    }

    private static CollectionView List(DataTemplate template) => new()
    {
        ItemTemplate = template,
        SelectionMode = SelectionMode.None,
    };

    internal static Label Line(Color color, double size, bool bold = false) => new Label
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

    /// <summary>What a Session shows, in its row of the list and over its log in the viewer, built
    /// once so the two cannot differ: the date, the character and the points; the title or server,
    /// and how long it lasted and how it ended; and its tags. Title first, as over a log, a title the
    /// player gave comes first and the date and character under it.</summary>
    internal sealed class SessionLines
    {
        private readonly bool _titleFirst;
        private readonly Label _when = Line(Body, 14);
        private readonly Label _persona = Line(Body, 14, bold: true);
        private readonly Label _points = Line(Body, 14);
        private readonly Label _host = Line(Muted, 12);
        private readonly Label _played = Line(Muted, 12);
        private readonly Label _labels = Line(Accent, 12);

        public SessionLines(bool titleFirst)
        {
            _titleFirst = titleFirst;
            _when.SetBinding(WidthRequestProperty, ScaledFontBinding.For(DateColumnWidth));
            _points.HorizontalOptions = LayoutOptions.End;
            _points.Margin = new Thickness(12, 0, 0, 0);
            var top = new Grid
            {
                ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
            };
            top.Add(_when, 0);
            top.Add(_persona, 1);
            top.Add(_points, 2);

            _played.HorizontalOptions = LayoutOptions.End;
            _played.Margin = new Thickness(12, 0, 0, 0);
            var second = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)] };
            second.Add(_host, 0);
            second.Add(_played, 1);

            View = new VerticalStackLayout { Spacing = 2, Children = { top, second, _labels } };
        }

        public View View { get; }

        public void Show(SessionItem item)
        {
            var titled = _titleFirst && item.Summary.ShownTitle is not null;
            _when.IsVisible = !titled;
            _when.Text = item.When;
            _persona.Text = titled ? item.Summary.ShownTitle : item.Persona;
            _points.Text = item.Points;
            _points.TextColor = item.PointsColor;
            _host.Text = titled ? item.Who : item.Where;
            _host.TextColor = titled || item.Summary.ShownTitle is null ? Muted : Body;
            _played.Text = item.Played;
            _labels.Text = item.Labels;
            _labels.IsVisible = item.Labels.Length > 0;
        }
    }

    private DataTemplate SessionTemplate() => new(() =>
    {
        var lines = new SessionLines(titleFirst: false);
        var row = Row(lines.View, "More: open, set title, tags, save as, trim, delete", OnSessionMore, OnSessionOpen,
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
            lines.Show(item);
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

    private void ShowTab(Shown tab)
    {
        // The tab already showing keeps its list and its scroll position.
        if (_tabShown && tab == _shown)
            return;
        _tabShown = true;
        _shown = tab;
        ShowEmpty(null);
        foreach (var (button, its) in new[]
                 { (_sessionsTab, Shown.Sessions), (_replaysTab, Shown.Replays), (_maintenanceTab, Shown.Maintenance) })
        {
            button.TextColor = its == tab ? Accent : Muted;
            button.FontAttributes = its == tab ? FontAttributes.Bold : FontAttributes.None;
        }
        _sessions.IsVisible = tab == Shown.Sessions;
        _sessionTools.IsVisible = tab == Shown.Sessions;
        _replays.IsVisible = tab == Shown.Replays;
        _maintenance.IsVisible = tab == Shown.Maintenance;
        _ = tab switch
        {
            Shown.Replays => LoadReplaysAsync(),
            Shown.Maintenance => ShowStoredAsync(),
            _ => LoadSessionsAsync(),
        };
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
            if (_shown == Shown.Sessions)
                ShowEmpty($"Your Sessions could not be read: {ex.Message}");
            return;
        }
        if (_shown != Shown.Sessions)
            return;
        _allSessions = [.. sessions];

        // The character list follows what was read; a character no longer there falls back to all.
        _characters = [.. sessions.Select(s => s.Persona).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
        if (_character is not null && !_characters.Contains(_character, StringComparer.OrdinalIgnoreCase))
            SetCharacter(null);

        ShowSessions();
    }

    /// <summary>The character drop-down: All characters, then each character listed, the current one
    /// ticked.</summary>
    private async Task PickCharacterAsync()
    {
        if (_busy)
            return;
        string Mark(string name, bool current) => current ? Glyph.Check + " " + name : name;
        var items = new List<string> { Mark(AllCharacters, _character is null) };
        items.AddRange(_characters.Select(c => Mark(c, string.Equals(c, _character, StringComparison.OrdinalIgnoreCase))));
        _busy = true;
        try
        {
            var pick = await ActionMenu.ShowAsync(this, _characterIcon.View, "Character", "Cancel", null, [.. items]);
            if (pick is null)
                return;
            var index = items.IndexOf(pick);
            SetCharacter(index <= 0 ? null : _characters[index - 1]);
            ShowSessions();
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>The sort drop-down, the current sort ticked.</summary>
    private async Task PickSortAsync()
    {
        if (_busy)
            return;
        var items = SortChoices.Select((s, i) => i == _sort ? Glyph.Check + " " + s : s).ToArray();
        _busy = true;
        try
        {
            var pick = await ActionMenu.ShowAsync(this, _sortIcon.View, "Sort", "Cancel", null, items);
            if (pick is null)
                return;
            _sort = Array.IndexOf(items, pick);
            // Lit while the sort is not the first, newest first: the list is not in its usual order.
            _sortIcon.Set = _sort > 0;
            _sortIcon.Describe("Sort: " + SortChoices[_sort]);
            ShowSessions();
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Shows only <paramref name="character"/>'s Sessions, or all with null; the icon is lit,
    /// and the filter box names the character, while one is chosen.</summary>
    private void SetCharacter(string? character)
    {
        _character = character;
        _characterIcon.Set = character is not null;
        _characterIcon.Describe("Character: " + (character ?? "all characters"));
        _filter.Placeholder = character is null ? "Filter: character, server, title, tag, outcome"
            : $"Filter {character}'s Sessions: server, title, tag, outcome";
    }

    /// <summary>Lists the Sessions the filter, character and sort pick out. Cheap: the list is in
    /// memory, and nothing is read again.</summary>
    private void ShowSessions()
    {
        if (_shown != Shown.Sessions)
            return;
        var character = _character;
        var text = _filter.Text?.Trim().TrimStart('#') ?? string.Empty;

        var picked = _allSessions.Where(s =>
            (character is null || string.Equals(s.Persona, character, StringComparison.OrdinalIgnoreCase))
            && (text.Length == 0 || Matches(s, text)));
        picked = _sort switch
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
        await OpenAsync(item);
    }

    private async void OnSessionMore(object? sender, EventArgs e)
    {
        if (_busy || sender is not View { BindingContext: SessionItem item } button)
            return;
        Select(item);
        await RowMenuAsync(item, button, viewer: null);
    }

    // The page the menu being run, and every dialog it opens, belongs to: this page, or the viewer
    // it was called from (a window of its own on a desktop). One action runs at a time (_busy).
    private Page? _host;
    private Page Host => _host ?? this;

    /// <summary>A Session's "..." menu, from its row or from <paramref name="viewer"/>, which leaves Open
    /// out: the Session is open there already. Its dialogs open over the page it was called from.</summary>
    internal async Task RowMenuAsync(SessionItem item, View button, LogViewerPage? viewer)
    {
        _busy = true;
        _host = viewer;
        try
        {
            // Every dialog this menu opens names the whole row, so a mis-click shows which Session it is.
            string[] choices = viewer is not null
                ? [TitleChoice, TagsChoice, SaveAsChoice, TrimChoice]
                : [OpenChoice, TitleChoice, TagsChoice, SaveAsChoice, TrimChoice];
            // The formats are offered only where they can be saved; elsewhere Save as... answers at once.
            var desktop = OperatingSystem.IsWindows() || OperatingSystem.IsMacCatalyst();
            var pick = await ActionMenu.ShowAsync(Host, button, item.Described, "Cancel", DeleteChoice, choices,
                desktop
                    ? new Dictionary<string, string[]> { [SaveAsChoice] = SaveFormats }
                    : new Dictionary<string, string[]>());
            var id = item.Summary.Id;
            switch (pick)
            {
                case TitleChoice:
                    var typed = await Host.DisplayPromptAsync("Set title",
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
                    var line = await Host.DisplayPromptAsync("Tags", item.Described + "\n\nSeparate tags with commas.",
                        "Save", "Cancel", initialValue: string.Join(SessionLabels.Separator + " ", item.Summary.Tags));
                    if (line is null)
                        return;
                    var tags = SessionLabels.Parse(line);
                    await WriteAsync(item,
                        () => SessionLabels.SetTags(MuckaPaths.GetDatabasePath(), id, tags),
                        item.Summary with { Tags = SessionLabels.Ordered(tags) });
                    break;
                case OpenChoice:
                    // The viewer owns the keys from here; reading the Session is not an action to wait on.
                    _busy = false;
                    await OpenAsync(item);
                    break;
                case ReplayFormat:
                    await MakeReplayAsync(item);
                    break;
                case SaveAsChoice:
                    await AlertAsync(SaveAsChoice, "If you want this on your phone, send \"Ollie\" a mudmail.");
                    break;
                case TextFormat:
                    await SaveLogAsync(item, LogKind.Text);
                    break;
                case SimpleColorFormat:
                    await SaveLogAsync(item, LogKind.SimpleColor);
                    break;
                case FancyColorFormat:
                    await SaveLogAsync(item, LogKind.FancyColor);
                    break;
                case WebFormat:
                    await SaveLogAsync(item, LogKind.Web);
                    break;
                case TrimChoice:
                    await TrimAsync(item);
                    break;
                case DeleteChoice:
                    await DeleteAsync(item, button);
                    break;
            }
        }
        finally
        {
            _busy = false;
            _host = null;
        }
    }

    /// <summary>Writes the Session's recording as a replay in the player's replay folder, where the
    /// Replays tab lists it; on a phone or tablet the share sheet then offers it. Returns where it went,
    /// or null when nothing was saved; with <paramref name="quiet"/> a save that worked is not
    /// announced, for a caller that says so itself.</summary>
    private async Task<string?> MakeReplayAsync(SessionItem item, bool quiet = false)
    {
        const string Heading = "Save as replay";
        var summary = item.Summary;
        var typed = await Host.DisplayPromptAsync(Heading,
            item.Described + "\n\nName the replay. It is listed on the Replays tab.", "Save",
            "Cancel", initialValue: ReplayLibrary.OfferedName(summary.Title ?? summary.DefaultTitle));
        if (typed is null)
            return null;
        var name = ReplayLibrary.NameFromTyped(typed);
        try
        {
            var listed = await Task.Run(ReplayLibrary.List);
            if (ReplayLibrary.NewNameProblem(name, listed) is { } problem)
            {
                await AlertAsync(Heading, problem);
                return null;
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
                return null;
            }
            var saved = $"\"{name}\" on the Replays tab";
#if ANDROID || IOS
            try
            {
                if (!_closed)
                    await Share.Default.RequestAsync(new ShareFileRequest { Title = name, File = new ShareFile(path) });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                await AlertAsync(Heading, $"Saved as {saved}, but it could not be shared: {ex.Message}");
            }
#else
            if (!quiet)
                await AlertAsync(Heading, $"Saved as {saved}.");
#endif
            return saved;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException
                                       or InvalidOperationException)
        {
            await AlertAsync(Heading, $"The replay could not be made: {ex.Message}");
            return null;
        }
    }

    /// <summary>Writes the Session's text log (<see cref="SessionTextLog"/>), its Color Log (ANSI SGR)
    /// in 16 or 24-bit colours, or its web page (HTML), the coloured ones in the player's chat colours,
    /// where the player picks in the system's save dialog. Desktop only: a phone or tablet never offers
    /// it. Returns where it went, or null when nothing was saved; with <paramref name="quiet"/> a save
    /// that worked is not announced.</summary>
    private async Task<string?> SaveLogAsync(SessionItem item, LogKind format, bool quiet = false)
    {
        var kind = format switch
        {
            LogKind.SimpleColor => "Simple Color Log",
            LogKind.FancyColor => "Fancy Color Log",
            LogKind.Web => "web page",
            _ => "text log",
        };
        var heading = "Save as " + kind;
        var extension = format switch
        {
            LogKind.Text => ".txt",
            LogKind.Web => ".html",
            _ => ".log",
        };
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
            path = Path.Combine(scratch, name + extension);
#elif WINDOWS
            path = await LogPathAsync(name, heading, char.ToUpperInvariant(kind[0]) + kind[1..], extension);
#endif
            if (path is null)
                return null;
            var target = path;
            var id = summary.Id;
            var host = summary.Host;
            var database = MuckaPaths.GetDatabasePath();
            // The settings file is read off the UI thread, as everything on the save path is.
            MudSharp.Models.ChatColorizer.Palette? palette = format == LogKind.Text
                ? null
                : (await Task.Run(SettingsStore.LoadChatColoursAsync)).Palette;
            var colours = palette is { } chat && format is LogKind.SimpleColor or LogKind.FancyColor
                ? new LogColours(chat, summary.Persona, format == LogKind.SimpleColor ? SgrColours.Simple : SgrColours.Fancy)
                : null;
            var title = summary.Title ?? summary.DefaultTitle;
            var written = await Task.Run(async () =>
            {
                if (SessionSlice.Read(database, id) is not { IsEmpty: false } slice)
                    return false;
                // The Windows picker has already asked about replacing a file; anywhere else one that
                // exists is never touched.
                var stream = new FileStream(target, OperatingSystem.IsWindows() ? FileMode.Create : FileMode.CreateNew,
                    FileAccess.Write);
                opened = true;
                var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
                if (format == LogKind.Web)
                    await SessionTextLog.WriteHtmlAsync(slice, host, writer, palette!.Value, summary.Persona, title);
                else
                    await SessionTextLog.WriteAsync(slice, host, writer, colours);
                return true;
            });
            if (!written)
            {
                DeleteUnwritten(path, opened);
                await AlertAsync(heading, "This Session has no recording left.");
                return null;
            }
#if MACCATALYST
            var exported = await ExportAsync(path);
            if (exported is null)
                return null;   // cancelled; the scratch copy goes below
            path = exported;
#endif
            if (!quiet)
                await AlertAsync(heading, $"Saved as {path}");
            return path;
        }
        // Whatever stopped it, the player is told, and no empty or half-written file is left behind.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (path is not null)
                DeleteUnwritten(path, opened);
            await AlertAsync(heading, $"The {kind} could not be saved: {ex.Message}");
            return null;
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
        var picker = new UIKit.UIDocumentPickerViewController([Foundation.NSUrl.FromFilename(file)], asCopy: true)
        {
            // Opens on Mucka's own folder, beside the live transcripts.
            DirectoryUrl = Foundation.NSUrl.CreateFileUrl(MuckaPaths.GetDataDirectory(), isDir: true, relativeToUrl: null),
        };
        picker.DidPickDocumentAtUrls += (_, e) => chosen.TrySetResult(e.Urls.FirstOrDefault()?.Path);
        picker.WasCancelled += (_, _) => chosen.TrySetResult(null);
        presenter.PresentViewController(picker, true, null);
        return chosen.Task;
    }
#endif

    /// <summary>Removes what a failed save left: the file it opened. Never a file this save did not
    /// open.</summary>
    private static void DeleteUnwritten(string path, bool opened)
    {
        try
        {
            if (opened)
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left where it is; the alert has already said the save failed.
        }
    }

#if WINDOWS
    /// <summary>Where a log called <paramref name="name"/> goes, or null when the player cancelled.
    /// </summary>
    private async Task<string?> LogPathAsync(string name, string heading, string type, string extension)
    {
        // Over the window the action was started in: a viewer's, or this page's.
        if (Host.Window?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window window)
        {
            await AlertAsync(heading, "The save dialog could not be opened.");
            return null;
        }
        // Opens on Mucka's own folder, beside the live transcripts.
        return WindowsSaveDialog.Show(WinRT.Interop.WindowNative.GetWindowHandle(window),
            MuckaPaths.GetDataDirectory(), name, type, extension);
    }
#endif

    /// <summary>An alert over the page the running action belongs to (<see cref="Host"/>), or nothing
    /// once this page has closed.</summary>
    private Task AlertAsync(string title, string message)
        => _closed ? Task.CompletedTask : Host.DisplayAlertAsync(title, message, "OK");

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
            // By id: the viewer holds the item it opened, which the list may have replaced since.
            for (var index = 0; index < _sessionItems.Count; index++)
            {
                var listed = _sessionItems[index];
                if (listed.Summary.Id == after.Id)
                    _sessionItems[index] = new SessionItem(after, listed.Index) { Selected = listed.Selected };
            }
            if (_viewers.TryGetValue(after.Id, out var viewer))
                viewer.Show(new SessionItem(after, item.Index));
        }
        // The write opens the store as the launch does, so it can also meet a failed schema upgrade
        // (InvalidOperationException) or a maintenance pass holding the file (OperationCanceledException).
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException
                                       or OperationCanceledException)
        {
            await AlertAsync("Logs", $"That could not be saved: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- Delete and Trim ----

    /// <summary>Delete: the Session's recording, after the backup the player picks, if any
    /// (<see cref="SessionRecording.Delete"/>). A backup that fails or is cancelled deletes nothing. The
    /// Session then leaves the list; its points and fights stay on the score graph and in Combat
    /// Insights.</summary>
    private async Task DeleteAsync(SessionItem item, View anchor)
    {
        const string Heading = "Delete";
        string[] backups = OperatingSystem.IsWindows() || OperatingSystem.IsMacCatalyst()
            ? [BackupReplay, BackupText, BackupSimple, BackupFancy, BackupWeb]
            : [BackupReplay];
        var pick = await ActionMenu.ShowAsync(Host, anchor, item.Described + "\n\nDelete this Session's recording?",
            "Cancel", DeleteWithoutBackup, backups);
        if (pick is null)
            return;
        // The drop-down on Windows has no title, so the confirmation is where the row and what deleting
        // does are said, on every platform.
        var how = pick == DeleteWithoutBackup ? "without saving it first"
            : "after saving " + pick["Save ".Length..^" first".Length] + " of it";
        if (!await Host.DisplayAlertAsync(Heading, item.Described + $"\n\nDelete this Session's recording {how}? "
                + "It cannot be put back. Its points and fights stay on the score graph and in Combat Insights.",
                "Delete", "Cancel"))
            return;
        var saved = pick switch
        {
            BackupReplay => await MakeReplayAsync(item, quiet: true),
            BackupText => await SaveLogAsync(item, LogKind.Text, quiet: true),
            BackupSimple => await SaveLogAsync(item, LogKind.SimpleColor, quiet: true),
            BackupFancy => await SaveLogAsync(item, LogKind.FancyColor, quiet: true),
            BackupWeb => await SaveLogAsync(item, LogKind.Web, quiet: true),
            _ => string.Empty,
        };
        if (saved is null)
            return;   // the save said why, or was cancelled: nothing is deleted

        var database = MuckaPaths.GetDatabasePath();
        var id = item.Summary.Id;
        var done = await WorkAsync(Heading, "Deleting a Session's recording", "The recording could not be deleted",
            () => $"Deleted {Size(SessionRecording.Delete(database, id).Bytes)} of recording.");
        if (done is null)
            return;
        if (_selectedId == id)
            _selectedId = null;
        // The viewer it was deleted from closes with it; what is said next is said over this page.
        if (_viewers.TryGetValue(id, out var viewer))
            await viewer.CloseAsync();
        _host = null;
        // Read again, so a character whose last Session this was leaves the character drop-down.
        await LoadSessionsAsync();
        await AlertAsync(Heading, saved.Length == 0 ? done : $"Saved as {saved}.\n{done}");
    }

    /// <summary>Trim: takes the repeated background status checks out of the Session's recording
    /// (<see cref="WireTrim.TrimSession"/>). What the Session shows, its logs and its replay stay the
    /// same; Restore, on the Maintenance tab, undoes it.</summary>
    private async Task TrimAsync(SessionItem item)
    {
        const string Heading = "Trim";
        var go = await Host.DisplayAlertAsync(Heading, item.Described
            + "\n\nTake the background status checks Mucka repeats out of this Session's recording? What it "
            + "shows, its logs and its replay stay the same. Restore, on the Maintenance tab, undoes it.",
            "Trim", "Cancel");
        if (!go)
            return;
        var database = MuckaPaths.GetDatabasePath();
        var id = item.Summary.Id;
        var done = await WorkAsync(Heading, "Trimming a Session's recording", "It could not be trimmed", () =>
        {
            var result = WireTrim.TrimSession(database, id, DateTimeOffset.UtcNow);
            return result.RunsRefused > 0 ? "Nothing was taken out: trimming would have changed what it shows."
                : result.RowsMoved == 0 ? "There was nothing to take out."
                : $"Took out {Size(result.BytesMoved)}.";
        });
        if (done is not null)
            await AlertAsync(Heading, done);
    }

    /// <summary>Runs <paramref name="work"/> off the UI thread under a maintenance job, so the overlay
    /// covers the page while it runs. Returns its message, or null when it failed, after telling the
    /// player <paramref name="failed"/> and why.</summary>
    private async Task<string?> WorkAsync(string heading, string detail, string failed, Func<string> work)
    {
        var wasBusy = _busy;
        _busy = true;
        try
        {
            return await Task.Run(() =>
            {
                using var job = Maintenance.Begin(detail);
                return work();
            });
        }
        // Whatever stopped it, the player is told: a Session another Mucka may still be writing, a store
        // that could not be opened or written, a file the work could not touch, or a recording the
        // parser trips on while the work replays it.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await AlertAsync(heading, $"{failed}: {ex.Message}");
            return null;
        }
        finally
        {
            _busy = wasBusy;
        }
    }

    /// <summary>A size as the page shows one: KB under a megabyte, else MB to one place.</summary>
    private static string Size(long bytes)
        => bytes < 1024 * 1024
            ? $"{Math.Max(1, bytes / 1024)} KB"
            : $"{bytes / (1024.0 * 1024.0):0.0} MB";

    // ------------------------------------------------------------------- Maintenance ----

    /// <summary>A circle with an i in it, on a 16-unit square: what a Maintenance button does.</summary>
    private const string InfoPath = "M 14,8 A 6,6 0 1 1 2,8 A 6,6 0 1 1 14,8 M 8,7.2 L 8,11.5 M 8,4.6 L 8,5.1";

    /// <summary>The Maintenance tab: Trim old Sessions, with its backup's Restore and Flush, and Save
    /// space, with Shrink mucka.db and Delete backups. Each button has an (i) saying what it does.</summary>
    private ScrollView MaintenanceView()
    {
        // Trim old Sessions.
        var age = new Entry
        {
            Text = WireTrim.DefaultDays.ToString(CultureInfo.InvariantCulture),
            Keyboard = Keyboard.Numeric,
            TextColor = Body,
            WidthRequest = 64,
            VerticalOptions = LayoutOptions.Center,
        };
        age.SetBinding(Entry.FontSizeProperty, ScaledFontBinding.For(13));
        var days = Line(Muted, 13);
        days.Text = "days";
        days.VerticalOptions = LayoutOptions.Center;
        var trim = ActionButton("Trim", "Trim old Sessions", async () =>
        {
            if (!int.TryParse(age.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var older) || older > MaxDays)
                return $"The age is a whole number of days, from 0 to {MaxDays}.";
            var database = MuckaPaths.GetDatabasePath();
            return await WorkAsync("Trim old Sessions", "Trimming old Sessions", "They could not be trimmed", () =>
            {
                var r = WireTrim.Trim(database, older, DateTimeOffset.UtcNow);
                var text = r.RowsMoved == 0 ? "There was nothing to remove." : $"Removed {Size(r.BytesMoved)}, kept as trim backup.";
                return r.RunsRefused == 0 ? text
                    : text + " Some were left alone: trimming them would have changed what they show.";
            });
        });
        var restore = ActionButton("Restore", "Restore", async () =>
        {
            var database = MuckaPaths.GetDatabasePath();
            return await WorkAsync("Restore", "Restoring the trim backup", "It could not be restored", () =>
            {
                var (restored, skipped) = WireTrim.Revert(database);
                const string Tidy = " Mucka tidies those Sessions when it next starts, and they cannot be trimmed until then.";
                return restored == 0 && skipped == 0 ? "There was nothing to restore."
                    : skipped == 0 ? "All of it is back." + Tidy
                    : $"{skipped:N0} pieces could not go back and are kept in the trim backup."
                        + (restored > 0 ? Tidy : "");
            });
        });
        var flush = ActionButton("Flush", "Flush", async () =>
        {
            if (!await DisplayAlertAsync("Flush", "Delete the trim backup? Trimmed Sessions stay trimmed and cannot "
                    + "be restored after this.", "Flush", "Cancel"))
                return null;
            var database = MuckaPaths.GetDatabasePath();
            return await WorkAsync("Flush", "Flushing the trim backup", "It could not be flushed", () =>
                WireTrim.Expire(database, 0, DateTimeOffset.UtcNow) == 0 ? "There was nothing to flush." : "Flushed.");
        });

        var trimSection = Section("Trim old Sessions",
            "Removes extra lines from Sessions that ended more than this many days ago: the status checks Mucka "
            + "sends to keep the status bar up to date (FES, FEW, FEI, FEX), and the game's replies to them that "
            + "say the same as the reply before. What each Session shows, its logs and its replay stay the same. "
            + "What is removed is kept as a trim backup, so you can restore it, until you flush it.");
        trimSection.Add(Row(age, days));

        // Trim, Restore and Flush share the buttons' column, so they line up at one width. Restore and
        // Flush are there only while a trim backup is.
        var restoreInfo = Info("Restore", "Puts everything Trim removed back into the Sessions it came from.");
        var flushInfo = Info("Flush", "Deletes the trim backup for good. Trimmed Sessions stay trimmed and can no "
            + "longer be restored.");
        _trimBackupControls = [restore, restoreInfo, flush, flushInfo];
        foreach (var control in _trimBackupControls)
            control.IsVisible = false;
        var trimGrid = new Grid
        {
            RowSpacing = 8,
            ColumnSpacing = 8,
            ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) },
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto),
            },
        };
        foreach (var button in new[] { trim, restore, flush })
            button.HorizontalOptions = LayoutOptions.Fill;
        foreach (var info in new[] { restoreInfo, flushInfo })
        {
            info.HorizontalOptions = LayoutOptions.Start;
            info.VerticalOptions = LayoutOptions.Center;
        }
        trimGrid.Add(trim, 0, 0);
        trimGrid.Add(_waiting, 0, 1);
        Grid.SetColumnSpan(_waiting, 2);
        trimGrid.Add(restore, 0, 2);
        trimGrid.Add(restoreInfo, 1, 2);
        trimGrid.Add(flush, 0, 3);
        trimGrid.Add(flushInfo, 1, 3);
        trimSection.Add(trimGrid);

        // Save space.
        var shrink = ActionButton("Shrink mucka.db", "Shrink mucka.db", async () =>
        {
            var database = MuckaPaths.GetDatabasePath();
            return await WorkAsync("Shrink mucka.db", "Shrinking mucka.db", "It could not be shrunk", () =>
            {
                var r = DatabaseVacuum.Shrink(database);
                return $"{Size(r.BytesBefore)} is now {Size(r.BytesAfter)}.";
            });
        });
        var deleteBackups = ActionButton("Delete backups", "Delete backups", async () =>
        {
            if (!await DisplayAlertAsync("Delete backups", $"Delete {DatabaseBackup.FileName}, the older copy of "
                    + "mucka.db? The next risky change Mucka is allowed to back up saves a new one.", "Delete", "Cancel"))
                return null;
            var database = MuckaPaths.GetDatabasePath();
            return await WorkAsync("Delete backups", "Deleting backups", "They could not be deleted", () =>
                DatabaseBackup.Delete(database) is var freed && freed == 0 ? "There was no backup." : $"Freed {Size(freed)}.");
        });

        var spaceSection = Section("Save space",
            "Shrink rebuilds mucka.db without the space deleted and trimmed recording leaves behind. Delete "
            + "backups deletes the older copy of mucka.db that Mucka may save before a risky change.");
        spaceSection.Add(_fileSize);
        // A button and its (i), and the backup's size, travel together: on a narrow screen the second
        // button goes to the next line as a whole.
        spaceSection.Add(Row(
            Unit(shrink,
                Info("Shrink mucka.db", "Rebuilds mucka.db without its empty space. It needs free space of twice its "
                    + "size. Close any other Mucka first: one that is recording may stop until it restarts.")),
            Unit(deleteBackups,
                Info("Delete backups", $"Deletes {DatabaseBackup.FileName}, a copy of mucka.db from before a risky "
                    + "change: an upgrade, a Delete, a Flush. Mucka keeps one copy, saves at most one each time it "
                    + "starts and only as often as Settings, Data allows, so what was recorded after the copy is "
                    + "not in it. The next risky change it is allowed to back up saves a new one."),
                _backupSize)));

        var stack = new VerticalStackLayout { Spacing = 24, Padding = new Thickness(16, 16, 16, 32) };
        stack.Add(trimSection);
        stack.Add(spaceSection);
        return new ScrollView { Content = stack };
    }

    /// <summary>A Maintenance section: its title and what it does; its rows follow.</summary>
    private static VerticalStackLayout Section(string title, string text)
    {
        var heading = Line(Body, 14, bold: true);
        heading.Text = title;
        var about = Line(Muted, 12);
        about.LineBreakMode = LineBreakMode.WordWrap;
        about.Text = text;
        var section = new VerticalStackLayout { Spacing = 8 };
        section.Add(heading);
        section.Add(about);
        return section;
    }

    /// <summary>A Maintenance row, which wraps on a window too narrow for it.</summary>
    private static FlexLayout Row(params View[] views)
    {
        var row = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap, AlignItems = Microsoft.Maui.Layouts.FlexAlignItems.Center };
        foreach (var view in views)
        {
            view.Margin = new Thickness(0, 2, 8, 2);
            row.Add(view);
        }
        return row;
    }

    /// <summary>Views that stay on one line of a <see cref="Row(View[])"/>, centred on each other.</summary>
    private static HorizontalStackLayout Unit(params View[] views)
    {
        var unit = new HorizontalStackLayout { Spacing = 8 };
        foreach (var view in views)
        {
            view.VerticalOptions = LayoutOptions.Center;
            unit.Add(view);
        }
        return unit;
    }

    /// <summary>An (i): its tooltip, and on a tap an alert, say what <paramref name="title"/> does.</summary>
    private View Info(string title, string text)
    {
        var info = new IconButton(InfoPath, text);
        info.Tapped += async () =>
        {
            if (!_busy)
                await AlertAsync(title, text);
        };
        return info.View;
    }

    /// <summary>A Maintenance button. <paramref name="act"/> returns the message to show, or null when it
    /// showed its own; the tab's figures are read again afterwards. One action at a time, and Escape
    /// belongs to its prompts meanwhile, as a row's menu's do.</summary>
    private Button ActionButton(string text, string title, Func<Task<string?>> act)
    {
        var button = new Button
        {
            Text = text,
            TextColor = Accent,
            BackgroundColor = Colors.Transparent,
            BorderColor = Accent,
            BorderWidth = 1,
            CornerRadius = 4,
            Padding = new Thickness(14, 4),
        };
        button.SetBinding(Button.FontSizeProperty, ScaledFontBinding.For(13));
        button.Clicked += async (_, _) =>
        {
            if (_busy)
                return;
            _busy = true;
            try
            {
                var message = await act();
                if (message is not null)
                    await AlertAsync(title, message);
                await ShowStoredAsync();
            }
            finally
            {
                _busy = false;
            }
        };
        return button;
    }

    /// <summary>The longest age a Maintenance pass takes: ten years.</summary>
    private const int MaxDays = 3650;

    /// <summary>Reads what the Maintenance tab's figures say - the trim backup, mucka.db's size and its
    /// backup's - off the UI thread.</summary>
    private async Task ShowStoredAsync()
    {
        var database = MuckaPaths.GetDatabasePath();
        try
        {
            var (waiting, size, backup) = await Task.Run(() =>
            {
                var backupFile = new FileInfo(DatabaseBackup.PathFor(database));
                return File.Exists(database)
                    ? (WireTrim.Waiting(database).Bytes, new FileInfo(database).Length, backupFile.Exists ? backupFile.Length : 0L)
                    : (0L, 0L, 0L);
            });
            _waiting.Text = waiting == 0 ? "(No current backup)" : $"{Size(waiting)} of trim backup:";
            foreach (var control in _trimBackupControls)
                control.IsVisible = waiting != 0;
            _fileSize.Text = $"mucka.db is {Size(size)}.";
            _backupSize.Text = backup == 0 ? "none" : Size(backup);
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            foreach (var control in _trimBackupControls)
                control.IsVisible = false;
            _waiting.Text = string.Empty;
            _backupSize.Text = string.Empty;
            _fileSize.Text = $"Your Sessions could not be read: {ex.Message}";
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
            if (_shown == Shown.Replays)
                ShowEmpty($"Your replays could not be listed: {ex.Message}");
            return;
        }
        if (_shown != Shown.Replays)
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

    // ------------------------------------------------------------------------ Viewer ----

    /// <summary>A Session open in a viewer, by id: one viewer for each, so opening it again brings the
    /// first forward.</summary>
    private readonly Dictionary<long, LogViewerPage> _viewers = [];

    /// <summary>The initial size of a viewer's window on a desktop.</summary>
    private const double ViewerWindowWidth = 1000;
    private const double ViewerWindowHeight = 720;

    /// <summary>A row's menu or prompt is open or its write is running: the viewers hold their keys.</summary>
    internal bool ActionRunning => _busy;

    /// <summary>
    /// Opens the Session in a viewer (<see cref="LogViewerPage"/>): a window of its own on a desktop,
    /// so several can be open beside each other, and a page over this one on a phone or tablet. A Session
    /// already open is brought forward instead.
    /// </summary>
    private async Task OpenAsync(SessionItem item)
    {
        var id = item.Summary.Id;
        if (_viewers.TryGetValue(id, out var open))
        {
            AfterClick(open.BringForward);
            return;
        }
        // By platform, not idiom: the Mac build runs as an iPad app and may report a tablet.
        var windowed = OperatingSystem.IsWindows() || OperatingSystem.IsMacCatalyst();
        var viewer = new LogViewerPage(this, item, windowed);
        _viewers[id] = viewer;
        if (windowed)
        {
            var window = new Window(viewer)
            {
                Title = viewer.WindowTitle,
                Width = ViewerWindowWidth,
                Height = ViewerWindowHeight,
            };
#if WINDOWS
            // The title bar draws no text: the header's top line is the Session's title.
            window.TitleBar = new TitleBar { ForegroundColor = Colors.White, BackgroundColor = Color.FromArgb("#161b22") };
#endif
            // Closed from its own title bar, or with this page: what it held is let go either way.
            window.Destroying += (_, _) => viewer.Release();
            // Closed in the meantime, with Logs: there is nothing left to show.
            AfterClick(() =>
            {
                if (!viewer.Released)
                    Application.Current?.OpenWindow(window);
            });
        }
        else
        {
            await Navigation.PushModalAsync(viewer);
        }
    }

#if WINDOWS
    private static bool LeftButtonDown() => (GetAsyncKeyState(0x01) & 0x8000) != 0;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
#endif

    /// <summary>Runs <paramref name="action"/> once the click being handled is over. Observed on Windows: the main
    /// window took the front back when the button of the double-click came up, behind a viewer opened or raised
    /// before that, so the action waits for the release.</summary>
    private void AfterClick(Action action)
    {
#if WINDOWS
        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        void Later()
        {
            if (queue is null || !queue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => action()))
                action();
        }
        if (LeftButtonDown() && Window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window { Content: Microsoft.UI.Xaml.UIElement root })
        {
            var done = false;
            Microsoft.UI.Xaml.Input.PointerEventHandler? released = null;
            void Finish()
            {
                if (done)
                    return;
                done = true;
                root.RemoveHandler(Microsoft.UI.Xaml.UIElement.PointerReleasedEvent, released!);
                Later();
            }
            released = (_, _) => Finish();
            root.AddHandler(Microsoft.UI.Xaml.UIElement.PointerReleasedEvent, released, handledEventsToo: true);
            // A release that never reaches the window must not leave the viewer unopened.
            Dispatcher.StartTimer(TimeSpan.FromSeconds(2), () =>
            {
                Finish();
                return false;
            });
            return;
        }
        Later();
#else
        Dispatcher.Dispatch(action);
#endif
    }

    /// <summary>A viewer has closed: its Session can be opened again.</summary>
    internal void ViewerClosed(LogViewerPage viewer)
    {
        foreach (var (id, open) in _viewers.ToList())
            if (ReferenceEquals(open, viewer))
                _viewers.Remove(id);
        // What an action started in the viewer has still to say is said over this page.
        if (ReferenceEquals(_host, viewer))
            _host = null;
    }

    // ------------------------------------------------------------------- open, close ----

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        // The lists are centred in a window wider than MaxContentWidth.
        var side = Math.Max(0, (width - MaxContentWidth) / 2);
        if (width > 0 && Math.Abs(_root.Padding.Left - side) > 0.5)
            _root.Padding = new Thickness(side, 0);
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
        // An action and its prompts own Escape until it ends, as on Windows.
        _macEscape = MacEscape.Attach(this, () =>
        {
            if (!_busy)
                _ = CloseAsync();
        });
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

    /// <summary>Pops this page, at most once, and only while it is the top modal. Its viewers are its
    /// children and close with it.</summary>
    private async Task CloseAsync()
    {
        if (_closed)
            return;
        _closed = true;
        foreach (var viewer in _viewers.Values.ToList())
            await viewer.CloseAsync();
        var stack = Navigation.ModalStack;
        if (stack.Count > 0 && ReferenceEquals(stack[^1], this))
            await Navigation.PopModalAsync();
    }

    protected override bool OnBackButtonPressed()
    {
        // An action and its prompts own the page until it ends, as they own Escape.
        if (!_busy)
            _ = CloseAsync();
        return true;
    }

#if WINDOWS
    private void OnWindowPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        // A row's menu, its prompt or alert, and its write own the keys until the action ends (_busy).
        if (_busy || Navigation.ModalStack is not [.., LogsPage])
            return;
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            _ = CloseAsync();
        }
    }
#endif

    // ------------------------------------------------------------------- row models ----

    /// <summary>What one Sessions row shows, worked out once. <paramref name="index"/> is its place in
    /// the list as shown, which picks its background.</summary>
    internal sealed class SessionItem(SessionSummary summary, int index) : System.ComponentModel.INotifyPropertyChanged
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

        /// <summary>The date and the character, in that order.</summary>
        public string Who => $"{When}   {Persona}";

        /// <summary>The row's first line as text: <see cref="Who"/> and the points.</summary>
        public string Ident => $"{Who}   {Points}".TrimEnd();

        /// <summary>Everything the row shows, as lines of text, for a dialog about it.</summary>
        public string Described => string.Join("\n", new[]
        {
            Ident,
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
    }
}
