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
    private const string SimpleColorFormat = "Simple Color Log (.log)";
    private const string FancyColorFormat = "Fancy Color Log (.log)";
    private const string WebFormat = "Web page (.html)";
    private static readonly string[] SaveFormats = [ReplayFormat, TextFormat, SimpleColorFormat, FancyColorFormat, WebFormat];
    private const string PlayChoice = "Play";
    private const string ShareChoice = "Share";
    private const string DeleteChoice = "Delete";

    private enum Shown { Sessions, Replays }

    /// <summary>The viewer card's surface: the page colour lifted, so it reads as a layer over it.</summary>
    private static readonly Color CardColor = Color.FromArgb("#161B22");
    /// <summary>The backdrop over History while the viewer is open.</summary>
    private static readonly Color Scrim = Color.FromRgba(0, 0, 0, 0.6);
    /// <summary>The widest the viewer card grows on a desktop window.</summary>
    private const double MaxCardWidth = 1100;
    /// <summary>The backdrop left showing round the card on a desktop window.</summary>
    private const double CardMargin = 32;
    /// <summary>A magnifying glass, on a 16-unit square: the Find icon.</summary>
    private const string MagnifierPath = "M 11,6.5 A 4.5,4.5 0 1 1 2,6.5 A 4.5,4.5 0 1 1 11,6.5 M 10,10 L 14.5,14.5";
    /// <summary>A funnel, on a 16-unit square: the Filters icon.</summary>
    private const string FunnelPath = "M 1.5,2.5 L 14.5,2.5 L 9.5,8.5 L 9.5,14 L 6.5,12.5 L 6.5,8.5 Z";

    private readonly Func<string?> _playRefusal;
    private readonly Func<string, Task> _playReplay;
    private readonly Grid _root;
    private readonly Button _sessionsTab;
    private readonly Button _replaysTab;
    // The viewer: a card over History, on a backdrop, holding the opened Session's row, its Find and
    // Filters icons and their panels, and the Session as a log.
    private readonly Grid _cardLayer;
    private readonly Border _card;
    private readonly Grid _cardHeader;
    private readonly SessionLines _cardLines;
    // Find and Filters: each an icon in the card's header, opening its own panel over the log, one
    // at a time.
    private readonly IconButton _searchIcon;
    private readonly IconButton _filterIcon;
    private readonly Border _searchPanel;
    private readonly Border _filterPopup;
    private readonly Entry _search;
    private readonly Label _searchStatus;
    private readonly VerticalStackLayout _filterPanel;
    private readonly Label _copied;
    // Counts copy notes, so only the latest one hides itself.
    private int _copiedNote;
    /// <summary>How long the "Copied" note stays over the log.</summary>
    private const int CopiedNoteMs = 1500;
    private readonly List<FilterRule> _rules = [];
    private readonly Mucka.Rendering.TerminalView _log;
    // The opened Session's lines as the parser made them, and what the filters leave of them on screen.
    private List<MudSharp.Models.StyledLine> _fedLines = [];
    private List<MudSharp.Models.StyledLine> _logLines = [];
    private SessionItem? _opened;
    private TextMatch? _match;
    // Counts openings, so a Session read after another was opened is dropped.
    private int _openVersion;
    // Counts filterings, so only the latest filter change is shown.
    private int _filterRun;
#if WINDOWS || MACCATALYST
    private readonly Mucka.Rendering.TerminalPointer _logPointer;
#endif
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
        _sessionsTab = Tab("Sessions", Shown.Sessions);
        _replaysTab = Tab("Replays", Shown.Replays);
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

        _root = new Grid
        {
            RowDefinitions = [new(GridLength.Auto), new(1), new(GridLength.Auto), new(GridLength.Star)],
        };
        _root.Add(header, 0, 0);
        _root.Add(new BoxView { Color = Rule, HeightRequest = 1 }, 0, 1);
        _root.Add(_sessionTools, 0, 2);
        _root.Add(body, 0, 3);

        // ---- the viewer card ----
        _cardLines = new SessionLines();
        _search = new Entry
        {
            Placeholder = "Find",
            TextColor = Body,
            PlaceholderColor = Muted,
            ClearButtonVisibility = ClearButtonVisibility.WhileEditing,
            VerticalOptions = LayoutOptions.Center,
            WidthRequest = 200,
        };
        _search.SetBinding(Entry.FontSizeProperty, ScaledFontBinding.For(13));
        // New text is a new search, from what the view shows.
        _search.TextChanged += (_, _) => ShowMatch(null, string.Empty);
#if !WINDOWS
        // Windows takes Enter, and Shift+Enter, before the box does (OnWindowPreviewKeyDown).
        _search.Completed += (_, _) => Find(backward: false);
#endif
        _searchStatus = Line(Muted, 12);
        _searchStatus.VerticalOptions = LayoutOptions.Center;
        _searchStatus.MinimumWidthRequest = 64;
        var searchRow = new Grid
        {
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto)],
            ColumnSpacing = 2,
        };
        searchRow.Add(_search, 0);
        searchRow.Add(SearchButton(Glyph.TriangleUp, "Previous match", backward: true), 1);
        searchRow.Add(SearchButton(Glyph.TriangleDown, "Next match", backward: false), 2);
        searchRow.Add(_searchStatus, 3);
        _searchPanel = Popup(searchRow);

        _filterPanel = new VerticalStackLayout { Spacing = 4 };
        _filterPopup = Popup(_filterPanel);

        _searchIcon = new IconButton(MagnifierPath, "Find in this Session");
        _searchIcon.Tapped += () => ShowPanel(_searchPanel.IsVisible ? null : _searchPanel);
        _filterIcon = new IconButton(FunnelPath, "Filter this Session");
        _filterIcon.Tapped += () => ShowPanel(_filterPopup.IsVisible ? null : _filterPopup);
        ShowRules();

        var more = ToolbarButton(Glyph.More, "More: set title, tags, save as, trim, delete", size: 18);
        more.Clicked += async (_, _) =>
        {
            if (!_busy && _opened is { } opened)
                await RowMenuAsync(opened, more, fromViewer: true);
        };
        var closeCard = ToolbarButton(Glyph.Close, "Close the Session");
        closeCard.Clicked += (_, _) => CloseCard();

        _cardHeader = new Grid
        {
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto),
                new(GridLength.Auto)],
            ColumnSpacing = 4,
            Padding = new Thickness(16, 8, 4, 8),
        };
        _cardHeader.Add(_cardLines.View, 0);
        _cardHeader.Add(_searchIcon.View, 1);
        _cardHeader.Add(_filterIcon.View, 2);
        _cardHeader.Add(more, 3);
        _cardHeader.Add(closeCard, 4);

        _log = new Mucka.Rendering.TerminalView();
#if WINDOWS || MACCATALYST
        _logPointer = new Mucka.Rendering.TerminalPointer(_log, ShowCopied);
#endif
        // The panels float over the log's top right, under their icons, and push nothing down.
        var logArea = new Grid();
        logArea.Add(_log);
        logArea.Add(_searchPanel);
        logArea.Add(_filterPopup);
        _copied = new Label
        {
            Text = "Copied",
            TextColor = Body,
            BackgroundColor = CardColor,
            Padding = new Thickness(10, 4),
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.End,
            Margin = new Thickness(0, 0, 24, 12),
            IsVisible = false,
        }.Scaled(12);
        logArea.Add(_copied);
        var cardContent = new Grid
        {
            RowDefinitions = [new(GridLength.Auto), new(1), new(GridLength.Star)],
        };
        cardContent.Add(_cardHeader, 0, 0);
        cardContent.Add(new BoxView { Color = Rule, HeightRequest = 1 }, 0, 1);
        cardContent.Add(logArea, 0, 2);
        _card = new Border
        {
            Stroke = Accent,
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            BackgroundColor = CardColor,
            Padding = 0,
            Content = cardContent,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
        };
        // The backdrop is the card's sibling, not its parent, so a click on the card never reaches it.
        var scrim = new BoxView { Color = Scrim };
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacCatalyst())
        {
            // A click beside the card closes it on a desktop; on a phone the card fills the screen.
            var outside = new TapGestureRecognizer();
            outside.Tapped += (_, _) => CloseCard();
            scrim.GestureRecognizers.Add(outside);
        }
        _cardLayer = new Grid { IsVisible = false };
        _cardLayer.Add(scrim);
        _cardLayer.Add(_card);

        var page = new Grid();
        page.Add(_root);
        page.Add(_cardLayer);
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

    private Button SearchButton(string glyph, string what, bool backward)
    {
        var button = ToolbarButton(glyph, what);
        button.Clicked += (_, _) => Find(backward);
        return button;
    }

    /// <summary>One of the viewer card's panels: floating over the log's top right, under the header's
    /// icons, hidden until its icon opens it.</summary>
    private static Border Popup(View content) => new()
    {
        Content = content,
        Padding = new Thickness(10, 6),
        BackgroundColor = CardColor,
        Stroke = Accent,
        StrokeThickness = 1,
        StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 },
        HorizontalOptions = LayoutOptions.End,
        VerticalOptions = LayoutOptions.Start,
        // Clear of the log's scrollbar on the right.
        Margin = new Thickness(0, 6, 16, 0),
        IsVisible = false,
    };

    /// <summary>Opens <paramref name="panel"/> - Find or Filters - and closes the other; null closes
    /// both. Opening Find puts the keyboard in its box.</summary>
    private void ShowPanel(Border? panel)
    {
        _searchPanel.IsVisible = ReferenceEquals(panel, _searchPanel);
        _filterPopup.IsVisible = ReferenceEquals(panel, _filterPopup);
        _searchIcon.Open = _searchPanel.IsVisible;
        _filterIcon.Open = _filterPopup.IsVisible;
        // A tick later: a box made visible in this pass may not take focus until it has been laid out.
        if (_searchPanel.IsVisible)
            Dispatcher.Dispatch(() => _search.Focus());
    }

    /// <summary>A flat icon button drawn as a path, since no icon font is on every platform. Its
    /// stroke and outline are the accent colour while its panel is open, and its stroke alone while
    /// what it opens has something set (a filter).</summary>
    private sealed class IconButton
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
            SemanticProperties.SetDescription(_frame, what);
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => Tapped?.Invoke();
            _frame.GestureRecognizers.Add(tap);
            Paint();
        }

        public View View => _frame;

        public event Action? Tapped;

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
    private static Button ToolbarButton(string text, string what, double size = 13)
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

    /// <summary>What a Session shows, in its row of the list and over its log in the viewer, built
    /// once so the two cannot differ: the date, the character and the points; the title or server,
    /// and how long it lasted and how it ended; and its tags.</summary>
    private sealed class SessionLines
    {
        private readonly Label _when = Line(Body, 14);
        private readonly Label _persona = Line(Body, 14, bold: true);
        private readonly Label _points = Line(Body, 14);
        private readonly Label _host = Line(Muted, 12);
        private readonly Label _played = Line(Muted, 12);
        private readonly Label _labels = Line(Accent, 12);

        public SessionLines()
        {
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
            _when.Text = item.When;
            _persona.Text = item.Persona;
            _points.Text = item.Points;
            _points.TextColor = item.PointsColor;
            _host.Text = item.Where;
            _host.TextColor = item.Summary.ShownTitle is null ? Muted : Body;
            _played.Text = item.Played;
            _labels.Text = item.Labels;
            _labels.IsVisible = item.Labels.Length > 0;
        }
    }

    private DataTemplate SessionTemplate() => new(() =>
    {
        var lines = new SessionLines();
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
        foreach (var (button, its) in new[] { (_sessionsTab, Shown.Sessions), (_replaysTab, Shown.Replays) })
        {
            button.TextColor = its == tab ? Accent : Muted;
            button.FontAttributes = its == tab ? FontAttributes.Bold : FontAttributes.None;
        }
        _sessions.IsVisible = tab == Shown.Sessions;
        _sessionTools.IsVisible = tab == Shown.Sessions;
        _replays.IsVisible = tab == Shown.Replays;
        _ = tab == Shown.Replays ? LoadReplaysAsync() : LoadSessionsAsync();
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
        if (_shown != Shown.Sessions)
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
        await OpenAsync(item);
    }

    private async void OnSessionMore(object? sender, EventArgs e)
    {
        if (_busy || sender is not View { BindingContext: SessionItem item } button)
            return;
        Select(item);
        await RowMenuAsync(item, button, fromViewer: false);
    }

    /// <summary>A Session's "..." menu, from its row or from the viewer's card, which leaves Open out:
    /// the Session is open there already.</summary>
    private async Task RowMenuAsync(SessionItem item, View button, bool fromViewer)
    {
        _busy = true;
        try
        {
            // Every dialog this menu opens names the whole row, so a mis-click shows which Session it is.
            string[] choices = fromViewer
                ? [TitleChoice, TagsChoice, SaveAsChoice, TrimChoice]
                : [OpenChoice, TitleChoice, TagsChoice, SaveAsChoice, TrimChoice];
            var pick = await ActionMenu.ShowAsync(this, button, item.Described, "Cancel", DeleteChoice, choices,
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
                case OpenChoice:
                    // The card owns the keys from here; reading the Session is not an action to wait on.
                    _busy = false;
                    await OpenAsync(item);
                    break;
                case ReplayFormat:
                    await MakeReplayAsync(item);
                    break;
                case TextFormat or SimpleColorFormat or FancyColorFormat
                    when !OperatingSystem.IsWindows() && !OperatingSystem.IsMacCatalyst():
                    await AlertAsync(pick, "If you want this on your phone, send Ollie a mudmail.");
                    break;
                case TextFormat:
                    await SaveLogAsync(item, null);
                    break;
                case SimpleColorFormat:
                    await SaveLogAsync(item, SgrColours.Simple);
                    break;
                case FancyColorFormat:
                    await SaveLogAsync(item, SgrColours.Fancy);
                    break;
                // Placeholders: on the menu so the shape is visible, and each says it is not built.
                case TrimChoice or DeleteChoice or WebFormat:
                    await AlertAsync(pick, $"{pick} is not built yet.");
                    break;
            }
        }
        finally
        {
            _busy = false;
        }
    }

    // ------------------------------------------------------------------------ Viewer ----

    /// <summary>True while the viewer's card is open over History.</summary>
    private bool Viewing => _cardLayer.IsVisible;

    /// <summary>Opens the Session in the viewer's card: its log (<see cref="SessionTextLog.Lines"/>,
    /// through the filters), in the player's chat colours and game text size, read off the UI
    /// thread.</summary>
    private async Task OpenAsync(SessionItem item)
    {
        var version = ++_openVersion;
        var summary = item.Summary;
        _opened = item;
        _cardLines.Show(item);
        _fedLines = [];
        _search.IsEnabled = false;
        ShowMatch(null, string.Empty);
        ShowNote("Reading the Session...");
        _cardLayer.IsVisible = true;
        LayOutCard();

        List<MudSharp.Models.StyledLine>? lines;
        MudSharp.Models.ChatColorizer.Palette palette;
        string? fontSize;
        try
        {
            var database = MuckaPaths.GetDatabasePath();
            var id = summary.Id;
            var persona = summary.Persona;
            // The settings file is read off the UI thread, as the Session is.
            var colours = Task.Run(SettingsStore.LoadChatColoursAsync);
            var font = Task.Run(() => SettingsStore.GetGlobalValueAsync("fontsize"));
            var read = Task.Run(() => SessionSlice.Read(database, id) is { IsEmpty: false } slice
                ? SessionTextLog.Lines(slice, persona)
                : null);
            palette = (await colours).Palette;
            fontSize = await font;
            lines = await read;
        }
        // Whatever stopped it, the player is told in the card: a Session that never reached the game, a
        // failed schema upgrade, a maintenance pass holding the file, or a recording the parser trips on.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (version == _openVersion)
                ShowNote($"This Session could not be shown: {ex.Message}");
            return;
        }
        if (version != _openVersion || _closed)
            return;
        if (lines is null)
        {
            ShowNote("This Session has no recording left.");
            return;
        }
        _fedLines = lines;
        _log.ChatPalette = palette;
        if (int.TryParse(fontSize, NumberStyles.Integer, CultureInfo.InvariantCulture, out var px))
            _log.SetFontSize(Mucka.Ui.FontSetup.ClampGameFont(px));
        await ShowFilteredAsync();
        if (version != _openVersion || _closed)
            return;
        _search.IsEnabled = true;
    }

    /// <summary>Closes the viewer's card. History underneath is as it was left.</summary>
    private void CloseCard()
    {
        ++_openVersion;   // a read still running for it is dropped
        _cardLayer.IsVisible = false;
        ShowPanel(null);
        _opened = null;
        _fedLines = [];
        _logLines = [];
        _log.ShowDocument(_logLines);
        _match = null;
    }

    /// <summary>The card's size for the window: on a desktop it leaves <see cref="CardMargin"/> of
    /// backdrop showing and is at most <see cref="MaxCardWidth"/> wide, and on a phone or tablet it is
    /// the whole screen, where its panels span the log's width.</summary>
    private void LayOutCard()
    {
        if (Width <= 0 || Height <= 0)
            return;
        var desktop = OperatingSystem.IsWindows() || OperatingSystem.IsMacCatalyst();
        var margin = desktop ? CardMargin : 0;
        var width = Math.Min(Width - 2 * margin, MaxCardWidth);
        _card.WidthRequest = width;
        _card.HeightRequest = Height - 2 * margin;
        _card.StrokeThickness = desktop ? 1 : 0;
        _card.StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = desktop ? 8 : 0 };
        // A full-screen card's top clears Android's status bar, as the page's own header does.
        _cardHeader.Padding = new Thickness(16, desktop ? 8 : 24, 4, 8);
        foreach (var panel in new[] { _searchPanel, _filterPopup })
        {
            panel.HorizontalOptions = desktop ? LayoutOptions.End : LayoutOptions.Fill;
            panel.Margin = desktop ? new Thickness(0, 6, 16, 0) : new Thickness(8, 6);
        }
        _search.WidthRequest = desktop ? 220 : 130;
    }

    /// <summary>Shows the filters' rows: each a category, its Show / Hide / Only, and a remove button,
    /// then a button that adds a row.</summary>
    private void ShowRules()
    {
        _filterPanel.Clear();
        for (var i = 0; i < _rules.Count; i++)
            _filterPanel.Add(RuleRow(i));
        var add = ToolbarButton("+ Add a filter", "Add a filter");
        add.HorizontalOptions = LayoutOptions.Start;
        // One row per category: a second row for the same one would only contradict the first.
        add.IsEnabled = _rules.Count < Enum.GetValues<LineCategory>().Length;
        add.Clicked += (_, _) =>
        {
            // A new row starts on the first category not filtered yet, as Only: the commonest ask is one
            // kind of line and nothing else.
            var unused = Enum.GetValues<LineCategory>().FirstOrDefault(c => _rules.All(r => r.Category != c));
            _rules.Add(new FilterRule(unused, FilterAction.Only));
            RulesChanged();
        };
        _filterPanel.Add(add);
        // The funnel stays lit while any filter is set, open or not, so a filtered log is never mistaken
        // for the whole Session.
        _filterIcon.Set = _rules.Count > 0;
    }

    private Grid RuleRow(int index)
    {
        var rule = _rules[index];
        var category = new Picker { TextColor = Body, VerticalOptions = LayoutOptions.Center, MinimumWidthRequest = 200 };
        category.SetBinding(Picker.FontSizeProperty, ScaledFontBinding.For(13));
        var categories = Enum.GetValues<LineCategory>();
        category.ItemsSource = categories.Select(CategoryName).ToList();
        category.SelectedIndex = Array.IndexOf(categories, rule.Category);
        category.SelectedIndexChanged += (_, _) =>
        {
            if (category.SelectedIndex < 0)
                return;
            _rules[index] = _rules[index] with { Category = categories[category.SelectedIndex] };
            RulesChanged();
        };
        // On a phone the category takes a line of its own, above its buttons, so a row fits the width.
        var desktop = OperatingSystem.IsWindows() || OperatingSystem.IsMacCatalyst();
        var row = new Grid
        {
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto),
                new(GridLength.Auto)],
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto)],
            ColumnSpacing = 4,
        };
        if (desktop)
            row.Add(category, 0, 0);
        else
        {
            category.MinimumWidthRequest = 0;
            row.Add(category, 0, 0);
            Grid.SetColumnSpan(category, 4);
        }
        var line = desktop ? 0 : 1;
        var column = desktop ? 1 : 0;
        foreach (var action in Enum.GetValues<FilterAction>())
        {
            var chosen = action == rule.Action;
            var segment = new Button
            {
                Text = action.ToString(),
                TextColor = chosen ? Colors.White : Body,
                BackgroundColor = chosen ? Color.FromArgb("#1F6FEB") : Colors.Transparent,
                BorderColor = Rule,
                BorderWidth = 1,
                CornerRadius = 4,
                Padding = new Thickness(12, 0),
                VerticalOptions = LayoutOptions.Center,
            };
            segment.SetBinding(Button.FontSizeProperty, ScaledFontBinding.For(12));
            segment.Clicked += (_, _) =>
            {
                _rules[index] = _rules[index] with { Action = action };
                RulesChanged();
            };
            row.Add(segment, column++, line);
        }
        var remove = ToolbarButton(Glyph.Close, "Remove this filter");
        remove.Clicked += (_, _) =>
        {
            _rules.RemoveAt(index);
            RulesChanged();
        };
        row.Add(remove, column, line);
        return row;
    }

    private static string CategoryName(LineCategory category) => category switch
    {
        LineCategory.Chat => "Chat",
        LineCategory.FightStartsAndEnds => "Fights starting and ending",
        _ => category.ToString(),
    };

    private void RulesChanged()
    {
        ShowRules();
        _ = ShowFilteredAsync();
    }

    /// <summary>Shows what the filters leave of the opened Session, from its start; a search starts
    /// again from there. Filtered off the UI thread; a later change or opening wins.</summary>
    private async Task ShowFilteredAsync()
    {
        if (_opened is null)
            return;
        var version = ++_filterRun;
        var fed = _fedLines;
        var rules = _rules.ToArray();
        var screen = await Task.Run(() => TerminalBuffer.ScreenOf(LogFilter.Apply(fed, rules)));
        if (version != _filterRun || !ReferenceEquals(fed, _fedLines))
            return;
        _logLines = screen;
        _log.ShowDocument(_logLines);
        ShowMatch(null, string.Empty);
        if (_logLines.Count == 0)
            ShowNote(fed.Count == 0 ? "This Session has no lines in the game." : "No lines are left by these filters.");
    }

    /// <summary>A note in place of the log: while it is read, or why it cannot be shown.</summary>
    private void ShowNote(string note)
    {
        _logLines = [];
        _log.ShowDocument([new MudSharp.Models.StyledLine(
            [new MudSharp.Models.StyledSpan(note, MudSharp.Models.TextStyle.Default)])]);
    }

    /// <summary>The search box's next match, or with <paramref name="backward"/> the one before: from
    /// the current match, or with none from the first line the log shows (the last, backward).
    /// <see cref="LogSearch"/> has the rules.</summary>
    private void Find(bool backward)
    {
        var text = _search.Text ?? string.Empty;
        if (!Viewing || _logLines.Count == 0 || text.Length == 0)
            return;
        var shown = _log.ShownLines ?? (0, 0);
        var match = LogSearch.Find(_logLines, text, _match, backward ? shown.Last : shown.First, backward);
        if (match is null)
        {
            ShowMatch(null, "No match");
            return;
        }
        var (index, count) = LogSearch.Place(_logLines, text, match);
        ShowMatch(match, string.Create(CultureInfo.CurrentCulture, $"{index:N0} of {count:N0}"));
    }

    private void ShowMatch(TextMatch? match, string status)
    {
        _match = match;
        _log.ShowMatch(match);
        _searchStatus.Text = status;
    }

    /// <summary>Says a copy was made, over the log's foot for a moment: the panels may all be shut.</summary>
    private async void ShowCopied()
    {
        var shown = ++_copiedNote;
        _copied.IsVisible = true;
        await Task.Delay(CopiedNoteMs);
        if (shown == _copiedNote)
            _copied.IsVisible = false;
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

    /// <summary>Writes the Session's text log (<see cref="SessionTextLog"/>), or with
    /// <paramref name="depth"/> its Color Log (ANSI SGR) in that many colours, the player's chat
    /// colours among them, where the player picks in the system's save dialog. Desktop only: a phone
    /// or tablet never offers it.</summary>
    private async Task SaveLogAsync(SessionItem item, SgrColours? depth)
    {
        var kind = depth switch
        {
            SgrColours.Simple => "Simple Color Log",
            SgrColours.Fancy => "Fancy Color Log",
            _ => "text log",
        };
        var heading = "Save as " + kind;
        var extension = depth is null ? ".txt" : ".log";
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
                return;
            var target = path;
            var id = summary.Id;
            var host = summary.Host;
            var database = MuckaPaths.GetDatabasePath();
            // The settings file is read off the UI thread, as everything on the save path is.
            var colours = depth is { } colourDepth
                ? new LogColours((await Task.Run(SettingsStore.LoadChatColoursAsync)).Palette, summary.Persona, colourDepth)
                : null;
            var written = await Task.Run(async () =>
            {
                if (SessionSlice.Read(database, id) is not { IsEmpty: false } slice)
                    return false;
                // The Windows picker has already asked about replacing a file; anywhere else one that
                // exists is never touched.
                var stream = new FileStream(target, OperatingSystem.IsWindows() ? FileMode.Create : FileMode.CreateNew,
                    FileAccess.Write);
                opened = true;
                await SessionTextLog.WriteAsync(slice, host, new StreamWriter(stream, new System.Text.UTF8Encoding(false)),
                    colours);
                return true;
            });
            if (!written)
            {
                DeleteUnwritten(path, opened);
                await AlertAsync(heading, "This Session has no recording left.");
                return;
            }
#if MACCATALYST
            var exported = await ExportAsync(path);
            if (exported is null)
                return;   // cancelled; the scratch copy goes below
            path = exported;
#endif
            await AlertAsync(heading, $"Saved as {path}");
        }
        // Whatever stopped it, the player is told, and no empty or half-written file is left behind.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (path is not null)
                DeleteUnwritten(path, opened);
            await AlertAsync(heading, $"The {kind} could not be saved: {ex.Message}");
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
        if (Window?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window window)
        {
            await AlertAsync(heading, "The save dialog could not be opened.");
            return null;
        }
        // Opens on Mucka's own folder, beside the live transcripts.
        return WindowsSaveDialog.Show(WinRT.Interop.WindowNative.GetWindowHandle(window),
            MuckaPaths.GetDataDirectory(), name, type, extension);
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
            // By id: the viewer's card holds the item it opened, which the list may have replaced since.
            for (var index = 0; index < _sessionItems.Count; index++)
            {
                var listed = _sessionItems[index];
                if (listed.Summary.Id == after.Id)
                    _sessionItems[index] = new SessionItem(after, listed.Index) { Selected = listed.Selected };
            }
            if (_opened?.Summary.Id == after.Id)
            {
                _opened = new SessionItem(after, item.Index);
                _cardLines.Show(_opened);
            }
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

    // ------------------------------------------------------------------- open, close ----

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        // The lists are centred in a window wider than MaxContentWidth.
        var side = Math.Max(0, (width - MaxContentWidth) / 2);
        if (width > 0 && Math.Abs(_root.Padding.Left - side) > 0.5)
            _root.Padding = new Thickness(side, 0);
        if (Viewing)
            LayOutCard();
    }

    /// <summary>Escape, or the system back button, closes the innermost thing open: the viewer's Find
    /// or Filters panel, then the viewer, then History.</summary>
    private void Back()
    {
        if (Viewing && (_searchPanel.IsVisible || _filterPopup.IsVisible))
            ShowPanel(null);
        else if (Viewing)
            CloseCard();
        else
            _ = CloseAsync();
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
        _macEscape = MacEscape.Attach(this, Back);
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
#if WINDOWS || MACCATALYST
        _logPointer.Detach();
#endif
        var stack = Navigation.ModalStack;
        if (stack.Count > 0 && ReferenceEquals(stack[^1], this))
            await Navigation.PopModalAsync();
    }

    protected override bool OnBackButtonPressed()
    {
        Back();
        return true;
    }

#if WINDOWS
    private void OnWindowPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        // A row's menu, its prompt or alert, and its write own the keys until the action ends (_busy).
        if (_busy || Navigation.ModalStack is not [.., HistoryPage])
            return;
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            Back();
        }
        else if (Viewing && ViewerKey(e.Key))
            e.Handled = true;
    }

    /// <summary>The viewer's keys: Enter in the Find box (or F3) finds the next match and with Shift the one before,
    /// Ctrl+F goes to the Find box, Page Up and Page Down scroll a page, Ctrl+Home and Ctrl+End go to the start and the end, and
    /// Ctrl+C copies the log's selection when the search box has none of its own.</summary>
    private bool ViewerKey(Windows.System.VirtualKey key)
    {
        static bool Down(Windows.System.VirtualKey k) => (GetKeyState((int)k) & 0x8000) != 0;
        var control = Down(Windows.System.VirtualKey.Control);
        switch (key)
        {
            // Enter is the Find box's; on a filter's button or picker it is that control's own.
            case Windows.System.VirtualKey.Enter when _searchPanel.IsVisible && _search.IsFocused:
                Find(backward: Down(Windows.System.VirtualKey.Shift));
                return true;
            // F3 opens Find too, so where it lands and "no match" are never out of sight.
            case Windows.System.VirtualKey.F3:
                if (!_searchPanel.IsVisible)
                    ShowPanel(_searchPanel);
                Find(backward: Down(Windows.System.VirtualKey.Shift));
                return true;
            case Windows.System.VirtualKey.PageUp:
                _log.ScrollByPages(1);
                return true;
            case Windows.System.VirtualKey.PageDown:
                _log.ScrollByPages(-1);
                return true;
            case Windows.System.VirtualKey.F when control:
                ShowPanel(_searchPanel);
                return true;
            case Windows.System.VirtualKey.Home when control:
                _log.ScrollToTop();
                return true;
            case Windows.System.VirtualKey.End when control:
                _log.ScrollToBottom();
                return true;
            case Windows.System.VirtualKey.C when control && _log.HasSelection && _search.SelectionLength == 0:
                if (_log.CopySelectionToClipboard())
                    ShowCopied();
                return true;
            default:
                return false;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);
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
