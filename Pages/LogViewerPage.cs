using System.Globalization;
using Mucka.Combat;
using Mucka.Core;
using Mucka.Terminal;
using Mucka.WireLog;
using static Mucka.Pages.LogsPage;

namespace Mucka.Pages;

/// <summary>
/// One Session shown as a log (<see cref="SessionTextLog.Lines"/>, through the filters), opened from
/// Logs: its row, its Find and Filters icons and their panels, and the log itself. A window of its own
/// on a desktop, so several Sessions can be open beside each other; a page over Logs on a phone or
/// tablet, in black margins round a border of the accent colour. Built in code, as Logs is.
///
/// <para>Its "..." menu is Logs's (<see cref="LogsPage.RowMenuAsync"/>), whose dialogs open over this
/// page. Closes on its X, on Escape (Windows and Mac), or on the system back button.</para>
/// </summary>
internal sealed class LogViewerPage : ContentPage
{
    /// <summary>The viewer's surface: the page colour lifted, so it reads as a layer over it.</summary>
    private static readonly Color CardColor = Color.FromArgb("#161B22");
    /// <summary>The backdrop left showing round the card on a phone or tablet.</summary>
    private const double PhoneCardMargin = 6;
    /// <summary>The backdrop above the card on a phone or tablet: clear of Android's status bar.</summary>
    private const double PhoneCardTop = 24;
    /// <summary>Added to each of the game pane's red, green and blue for the log, so a log is never
    /// mistaken for the game.</summary>
    private const byte LogBackgroundLift = 12;
    /// <summary>The lift in a desktop window: less, as no black margin sets the log apart there.</summary>
    private const byte WindowLogBackgroundLift = 8;
    private static SkiaSharp.SKColor LogBackground(byte lift) => new(
        (byte)(Mucka.Rendering.TerminalTheme.Background.Red + lift),
        (byte)(Mucka.Rendering.TerminalTheme.Background.Green + lift),
        (byte)(Mucka.Rendering.TerminalTheme.Background.Blue + lift));
    /// <summary>A magnifying glass, on a 16-unit square: the Find icon.</summary>
    private const string MagnifierPath = "M 11,6.5 A 4.5,4.5 0 1 1 2,6.5 A 4.5,4.5 0 1 1 11,6.5 M 10,10 L 14.5,14.5";
    /// <summary>A funnel, on a 16-unit square: the Filters icon.</summary>
    private const string FunnelPath = "M 1.5,2.5 L 14.5,2.5 L 9.5,8.5 L 9.5,14 L 6.5,12.5 L 6.5,8.5 Z";
    /// <summary>Chevrons, on a 16-unit square: Find's previous and next match.</summary>
    private const string ChevronUpPath = "M 3,10.5 L 8,5.5 L 13,10.5";
    private const string ChevronDownPath = "M 3,5.5 L 8,10.5 L 13,5.5";
    /// <summary>An open eye, on a 16-unit square: a rule shows what it matches.</summary>
    private const string EyePath = "M 1.5,8 C 4,3.5 12,3.5 14.5,8 C 12,12.5 4,12.5 1.5,8 Z M 10,8 A 2,2 0 1 1 6,8 A 2,2 0 1 1 10,8";
    /// <summary>The eye struck through: a rule hides what it matches.</summary>
    private const string EyeClosedPath = EyePath + " M 2.5,13.5 L 13.5,2.5";
    /// <summary>An x, on a 16-unit square: remove a rule, the x that closes and removes elsewhere.</summary>
    private const string RemovePath = "M 4,4 L 12,12 M 12,4 L 4,12";

    private readonly LogsPage _owner;
    // A window of its own, not a page over Logs.
    private readonly bool _windowed;
    // A menu of the Filters panel is open: one at a time, as Logs's row menu is.
    private bool _busy;
    // Reading the Session has been started, and the viewer let go (closed).
    private bool _started;
    private bool _released;
#if UIKIT
    private MacEscape.Registration? _macEscape;
#endif

    // The viewer: a card over Logs, on a backdrop, holding the opened Session's row, its Find and
    // Filters icons and their panels, and the Session as a log.
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
#if WINDOWS || MACCATALYST
    // Counts copy notes, so only the latest one hides itself.
    private int _copiedNote;
    /// <summary>How long the "Copied" note stays over the log.</summary>
    private const int CopiedNoteMs = 1500;
#endif
    private readonly List<FilterRule> _rules = [];
    // Each rule row's count, and the counts last worked out, so a rebuilt table shows them at once.
    private readonly List<Label> _ruleCounts = [];
    private List<string> _ruleCountValues = [];
    // How many of the opened Session's lines each kind holds, for "+ Add a rule".
    private readonly Dictionary<RuleKind, int> _kindCounts = [];
    // Counts typing in a rule's pattern box, so only the last pause refilters.
    private int _patternVersion;
    // While any rule is set: "Showing N of M lines - ..." and Show all, under the card's header.
    private readonly Grid _filteredStrip;
    private readonly Label _filteredText;
    private readonly Mucka.Rendering.TerminalView _log;
    // The opened Session's lines as the parser made them, and what the filters leave of them on screen.
    private List<MudSharp.Models.StyledLine> _fedLines = [];
    // The opened Session has been read: until then a rule change waits, and counts nothing.
    private bool _fedReady;
    // How many lines of screen the opened Session has unfiltered, once worked out: the strip's "of M".
    private int? _allLineCount;
    private List<MudSharp.Models.StyledLine> _logLines = [];
    private SessionItem _opened;
    private TextMatch? _match;
    // Counts openings, so a Session read after another was opened is dropped.
    private int _openVersion;
    // Counts filterings, so only the latest filter change is shown.
    private int _filterRun;
#if WINDOWS || MACCATALYST
    private readonly Mucka.Rendering.TerminalPointer _logPointer;
#endif

    /// <param name="owner">Logs, which opened this viewer and runs its "..." menu.</param>
    /// <param name="item">The Session to show.</param>
    /// <param name="windowed">This viewer is a window of its own.</param>
    public LogViewerPage(LogsPage owner, SessionItem item, bool windowed)
    {
        _owner = owner;
        _windowed = windowed;
        _opened = item;
        // ---- the viewer card ----
        _cardLines = new SessionLines(titleFirst: true);
        _search = new Entry
        {
            Placeholder = "Find in this Session",
            TextColor = Body,
            PlaceholderColor = Muted,
            ClearButtonVisibility = ClearButtonVisibility.WhileEditing,
            VerticalOptions = LayoutOptions.Center,
        };
        _search.SetBinding(Entry.FontSizeProperty, ScaledFontBinding.For(13));
        // New text is a new search, from what the view shows.
        _search.TextChanged += (_, _) => ShowMatch(null, string.Empty);
#if !WINDOWS
        // Windows takes Enter, and Shift+Enter, before the box does (OnWindowPreviewKeyDown).
        _search.Completed += (_, _) => Find(backward: false);
#endif
        // A fixed-width status, so "3 of 12" and "No match" never change the panel's width.
        _searchStatus = Line(Muted, 12);
        _searchStatus.VerticalOptions = LayoutOptions.Center;
        _searchStatus.HorizontalTextAlignment = TextAlignment.End;
        _searchStatus.WidthRequest = 84;
        var previous = new IconButton(ChevronUpPath, "Previous match (Shift+Enter)");
        previous.Tapped += () => Find(backward: true);
        var next = new IconButton(ChevronDownPath, "Next match (Enter)");
        next.Tapped += () => Find(backward: false);
        var searchRow = new Grid
        {
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto)],
            ColumnSpacing = 2,
        };
        searchRow.Add(_search, 0);
        searchRow.Add(_searchStatus, 1);
        searchRow.Add(previous.View, 2);
        searchRow.Add(next.View, 3);
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
            if (!Held)
                await _owner.RowMenuAsync(_opened, more, this);
        };
        var closeCard = ToolbarButton(Glyph.Close, "Close the Session");
        closeCard.Clicked += (_, _) => _ = CloseAsync();

        _cardHeader = new Grid
        {
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto),
                new(GridLength.Auto)],
            ColumnSpacing = 4,
        };
        _cardHeader.Add(_cardLines.View, 0);
        _cardHeader.Add(_searchIcon.View, 1);
        _cardHeader.Add(_filterIcon.View, 2);
        _cardHeader.Add(more, 3);
        _cardHeader.Add(closeCard, 4);

        _log = new Mucka.Rendering.TerminalView
        {
            ClearColor = LogBackground(windowed ? WindowLogBackgroundLift : LogBackgroundLift),
        };
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
        // The filtered strip: always in sight while rules are set, whether or not their panel is open.
        _filteredText = Line(Body, 12);
        _filteredText.VerticalOptions = LayoutOptions.Center;
        var showAll = ToolbarButton("Show all", "Remove every rule", size: 12);
        showAll.TextColor = Accent;
        showAll.Clicked += (_, _) =>
        {
            _rules.Clear();
            RulesChanged();
        };
        _filteredStrip = new Grid
        {
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)],
            Padding = new Thickness(16, 0, 8, 0),
            BackgroundColor = Color.FromArgb("#1F2A38"),
            IsVisible = false,
        };
        _filteredStrip.Add(_filteredText, 0);
        _filteredStrip.Add(showAll, 1);

        var cardContent = new Grid
        {
            RowDefinitions = [new(GridLength.Auto), new(1), new(GridLength.Auto), new(GridLength.Star)],
        };
        cardContent.Add(_cardHeader, 0, 0);
        cardContent.Add(new BoxView { Color = Rule, HeightRequest = 1 }, 0, 1);
        cardContent.Add(_filteredStrip, 0, 2);
        cardContent.Add(logArea, 0, 3);
        // A window is the card itself. On a phone or tablet the card has the accent border, rounded, in
        // black margins, the top one clear of Android's status bar.
        _card = new Border
        {
            Stroke = Accent,
            StrokeThickness = windowed ? 0 : 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = windowed ? 0 : 8 },
            BackgroundColor = CardColor,
            Padding = 0,
            Content = cardContent,
            Margin = windowed ? new Thickness(0)
                : new Thickness(PhoneCardMargin, PhoneCardTop, PhoneCardMargin, PhoneCardMargin),
        };
        // Less below the header than above: the panels hang close under their icons.
        _cardHeader.Padding = new Thickness(16, 8, 4, 4);

        _cardLines.Show(item);
        BackgroundColor = windowed ? CardColor : Colors.Black;
        var page = new Grid();
        page.Add(_card);
        // Over everything while a delete, a trim or a Maintenance pass runs (Maintenance.Current).
        page.Add(new MaintenanceOverlay());
        Content = page;
    }

    // ------------------------------------------------------------------------ layout ----

    /// <summary>One of the viewer card's panels: floating over the log's top right, under the header's
    /// icons, hidden until its icon opens it. It shuts by its icon, the other icon, or Escape: it has
    /// no close button of its own (operator).</summary>
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

    // ------------------------------------------------------------------------ Viewer ----

    /// <summary>True until the viewer is closed.</summary>
    private bool Viewing => !_released;

    /// <summary>Reads the Session: its log (<see cref="SessionTextLog.Lines"/>, through the filters), in
    /// the player's chat colours and game text size, read off the UI thread.</summary>
    private async Task LoadAsync()
    {
        var version = ++_openVersion;
        var summary = _opened.Summary;
        _fedLines = [];
        _fedReady = false;
        _allLineCount = null;
        _kindCounts.Clear();
        _ruleCountValues = [];
        foreach (var count in _ruleCounts)
            count.Text = string.Empty;
        _filteredStrip.IsVisible = false;
        _search.IsEnabled = false;
        ShowMatch(null, string.Empty);
        ShowNote("Reading the Session...");

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
        // Whatever stopped it, the player is told here: a Session that never reached the game, a
        // failed schema upgrade, a maintenance pass holding the file, or a recording the parser trips on.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (version == _openVersion)
                ShowNote($"This Session could not be shown: {ex.Message}");
            return;
        }
        if (version != _openVersion || _released)
            return;
        if (lines is null)
        {
            ShowNote("This Session has no recording left.");
            return;
        }
        _fedLines = lines;
        _fedReady = true;
        _log.ChatPalette = palette;
        if (int.TryParse(fontSize, NumberStyles.Integer, CultureInfo.InvariantCulture, out var px))
            _log.SetFontSize(Mucka.Ui.FontSetup.ClampGameFont(px));
        await ShowFilteredAsync();
        if (version != _openVersion || _released)
            return;
        _search.IsEnabled = true;
    }

    /// <summary>The panels' sizes for the window: on a desktop a Find box as wide as a comfortable search
    /// and a rule table never wider than the window leaves; on a phone or tablet both span the log.</summary>
    private void LayOutPanels()
    {
        if (Width <= 0)
            return;
        var width = _windowed ? Width : Width - 2 * PhoneCardMargin;
        foreach (var panel in new[] { _searchPanel, _filterPopup })
        {
            panel.HorizontalOptions = _windowed ? LayoutOptions.End : LayoutOptions.Fill;
            // On a desktop, clear of the log's scrollbar on the right.
            panel.Margin = _windowed ? new Thickness(0, 0, 16, 0) : new Thickness(8, 0, 8, 0);
        }
        _searchPanel.WidthRequest = _windowed ? 380 : -1;
        // The rule table's six columns need a wide panel.
        _filterPopup.WidthRequest = _windowed ? Math.Min(700, width - 40) : -1;
    }

    /// <summary>Shows the filter rules as a table - a header, then per rule its eye, what it picks out,
    /// which of it, the pattern it must hold, how many lines it matches, and a remove button - then a
    /// button that adds a rule. Operator rule: a rule is its kind, its "which" where the kind has one,
    /// and its pattern where it takes one.</summary>
    private void ShowRules()
    {
        _filterPanel.Clear();
        _ruleCounts.Clear();
        var desktop = OperatingSystem.IsWindows() || OperatingSystem.IsMacCatalyst();
        if (_rules.Count == 0)
        {
            var empty = Line(Muted, 13);
            empty.Text = "No rules: every line of the Session is shown.";
            empty.Margin = new Thickness(4, 4, 4, 2);
            _filterPanel.Add(empty);
        }
        else
        {
            if (desktop)
                _filterPanel.Add(RuleHeader());
            for (var i = 0; i < _rules.Count; i++)
            {
                _filterPanel.Add(new BoxView { Color = Rule, HeightRequest = 1 });
                _filterPanel.Add(RuleRow(i, desktop));
            }
            _filterPanel.Add(new BoxView { Color = Rule, HeightRequest = 1 });
        }
        var add = ToolbarButton("+ Add a rule", "Add a rule");
        add.HorizontalOptions = LayoutOptions.Start;
        add.TextColor = Accent;
        add.Clicked += async (_, _) => await AddRuleAsync(add);
        _filterPanel.Add(add);
        // The funnel stays lit while any rule is set, open or not, so a filtered log is never mistaken
        // for the whole Session.
        _filterIcon.Set = _rules.Count > 0;
    }

    // The rule table's columns on a desktop: eye, what, which, matching, lines, remove.
    private static Grid RuleGrid() => new()
    {
        ColumnDefinitions = [new(36), new(140), new(GridLength.Auto), new(GridLength.Star), new(70), new(36)],
        ColumnSpacing = 6,
        Padding = new Thickness(0, 2),
    };

    private static Grid RuleHeader()
    {
        var header = RuleGrid();
        string[] titles = ["SHOW", "WHAT", "WHICH", "MATCHING", "LINES"];
        for (var i = 0; i < titles.Length; i++)
        {
            var title = Line(Muted, 11);
            title.Text = titles[i];
            title.VerticalOptions = LayoutOptions.Center;
            if (i == 4)
                title.HorizontalTextAlignment = TextAlignment.End;
            header.Add(title, i);
        }
        return header;
    }

    /// <summary>One rule's row. On a phone it takes two lines: the eye, what, lines and remove, then
    /// which and matching.</summary>
    private Grid RuleRow(int index, bool desktop)
    {
        var rule = _rules[index];
        var row = RuleGrid();
        if (!desktop)
        {
            row.ColumnDefinitions = [new(36), new(GridLength.Star), new(60), new(36)];
            row.RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto)];
        }

        var eye = new IconButton(rule.Show ? EyePath : EyeClosedPath,
            rule.Show ? "Showing what this rule matches; click to hide it instead"
                : "Hiding what this rule matches; click to show it instead") { Set = rule.Show };
        // Every handler here checks its row still stands: a table rebuilt under a late event has fewer rows.
        eye.Tapped += () =>
        {
            if (index >= _rules.Count)
                return;
            _rules[index] = _rules[index] with { Show = !_rules[index].Show };
            RulesChanged();
        };

        var kinds = Enum.GetValues<RuleKind>();
        var kind = new Picker { TextColor = Body, VerticalOptions = LayoutOptions.Center };
        kind.SetBinding(Picker.FontSizeProperty, ScaledFontBinding.For(13));
        kind.ItemsSource = kinds.Select(KindName).ToList();
        kind.SelectedIndex = Array.IndexOf(kinds, rule.Kind);
        kind.SelectedIndexChanged += (_, _) =>
        {
            if (index >= _rules.Count || kind.SelectedIndex < 0 || kinds[kind.SelectedIndex] == _rules[index].Kind)
                return;
            _rules[index] = NewRule(kinds[kind.SelectedIndex]) with { Show = _rules[index].Show };
            // Its old count was the old kind's.
            if (index < _ruleCountValues.Count)
                _ruleCountValues[index] = string.Empty;
            RulesChanged();
        };

        var which = SubControl(index, rule);
        View matching = LogFilter.PatternOf(rule.Kind) is { } hint ? PatternBox(index, rule, hint) : new ContentView();

        var count = Line(Muted, 12);
        count.HorizontalTextAlignment = TextAlignment.End;
        count.VerticalOptions = LayoutOptions.Center;
        count.Text = _ruleCountValues.Count > index ? _ruleCountValues[index] : string.Empty;
        _ruleCounts.Add(count);

        var remove = new IconButton(RemovePath, "Remove this rule");
        remove.Tapped += () =>
        {
            if (index >= _rules.Count)
                return;
            _rules.RemoveAt(index);
            // The rows below move up, and their counts with them.
            if (index < _ruleCountValues.Count)
                _ruleCountValues.RemoveAt(index);
            RulesChanged();
        };

        if (desktop)
        {
            row.Add(eye.View, 0);
            row.Add(kind, 1);
            row.Add(which, 2);
            row.Add(matching, 3);
            row.Add(count, 4);
            row.Add(remove.View, 5);
        }
        else
        {
            row.Add(eye.View, 0, 0);
            row.Add(kind, 1, 0);
            row.Add(count, 2, 0);
            row.Add(remove.View, 3, 0);
            var second = new Grid
            {
                ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)],
                ColumnSpacing = 6,
            };
            second.Add(which, 0);
            second.Add(matching, 1);
            row.Add(second, 1, 1);
            Grid.SetColumnSpan(second, 3);
        }
        return row;
    }

    /// <summary>A rule's "which": the Room text sub as its four segments (operator), any other kind's
    /// as a drop-down; nothing for a kind without one.</summary>
    private View SubControl(int index, FilterRule rule)
    {
        var subs = LogFilter.SubsOf(rule.Kind);
        if (subs.Count == 0)
            return new ContentView();
        if (rule.Kind == RuleKind.RoomText)
        {
            var segments = new HorizontalStackLayout { Spacing = 0, VerticalOptions = LayoutOptions.Center };
            for (var i = 0; i < subs.Count; i++)
            {
                var sub = i;
                var chosen = sub == rule.Sub;
                var segment = new Button
                {
                    Text = subs[i],
                    TextColor = chosen ? Colors.White : Body,
                    BackgroundColor = chosen ? Color.FromArgb("#1F6FEB") : Colors.Transparent,
                    BorderColor = Color.FromArgb("#30363D"),
                    BorderWidth = 1,
                    CornerRadius = 0,
                    Padding = new Thickness(10, 0),
                    MinimumHeightRequest = 30,
                };
                segment.SetBinding(Button.FontSizeProperty, ScaledFontBinding.For(12));
                segment.Clicked += (_, _) =>
                {
                    if (index >= _rules.Count)
                        return;
                    _rules[index] = _rules[index] with { Sub = sub };
                    RulesChanged();
                };
                segments.Add(segment);
            }
            return segments;
        }
        var picker = new Picker { TextColor = Body, VerticalOptions = LayoutOptions.Center, MinimumWidthRequest = 110 };
        picker.SetBinding(Picker.FontSizeProperty, ScaledFontBinding.For(13));
        picker.ItemsSource = subs.ToList();
        picker.SelectedIndex = Math.Clamp(rule.Sub, 0, subs.Count - 1);
        picker.SelectedIndexChanged += (_, _) =>
        {
            if (index >= _rules.Count || picker.SelectedIndex < 0 || picker.SelectedIndex == _rules[index].Sub)
                return;
            _rules[index] = _rules[index] with { Sub = picker.SelectedIndex };
            RulesChanged();
        };
        return picker;
    }

    /// <summary>A rule's pattern box. Typing does not rebuild the table, which would take the box's
    /// focus; it refilters once typing pauses.</summary>
    private Entry PatternBox(int index, FilterRule rule, string hint)
    {
        var box = new Entry
        {
            Text = rule.Pattern,
            Placeholder = hint,
            TextColor = Body,
            PlaceholderColor = Muted,
            ClearButtonVisibility = ClearButtonVisibility.WhileEditing,
            VerticalOptions = LayoutOptions.Center,
        };
        box.SetBinding(Entry.FontSizeProperty, ScaledFontBinding.For(13));
        box.TextChanged += async (_, e) =>
        {
            if (index >= _rules.Count)
                return;
            _rules[index] = _rules[index] with { Pattern = e.NewTextValue ?? string.Empty };
            var typed = ++_patternVersion;
            await Task.Delay(FilterPauseMs);
            if (typed == _patternVersion)
                await ShowFilteredAsync();
        };
        return box;
    }

    /// <summary>"+ Add a rule": a drop-down of the kinds, each with how many of the Session's lines it
    /// holds.</summary>
    private async Task AddRuleAsync(View anchor)
    {
        if (Held)
            return;
        var kinds = Enum.GetValues<RuleKind>();
        var items = kinds.Select(k => _kindCounts.TryGetValue(k, out var n)
            ? string.Create(CultureInfo.CurrentCulture, $"{KindName(k)}   ({n:N0})")
            : KindName(k)).ToArray();
        _busy = true;
        try
        {
            var pick = await ActionMenu.ShowAsync(this, anchor, "Add a rule", "Cancel", null, items);
            if (pick is null)
                return;
            _rules.Add(NewRule(kinds[Array.IndexOf(items, pick)]));
            RulesChanged();
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>A new rule of <paramref name="kind"/>, showing what it matches; Room text starts on All.</summary>
    private static FilterRule NewRule(RuleKind kind)
        => new(kind, kind == RuleKind.RoomText ? LogFilter.DefaultRoomSub : 0);

    private static string KindName(RuleKind kind) => kind switch
    {
        RuleKind.Chat => "Chat",
        RuleKind.RoomText => "Room text",
        RuleKind.Combat => "Combat",
        RuleKind.Kills => "Kills",
        RuleKind.Flees => "Flees",
        RuleKind.Weather => "Weather",
        RuleKind.Text => "Any text",
        _ => kind.ToString(),
    };

    /// <summary>The rules in a few words, for the strip under the header.</summary>
    private string RulesSummary() => string.Join("; ", _rules.Select(r =>
    {
        var subs = LogFilter.SubsOf(r.Kind);
        var text = (r.Show ? string.Empty : "hiding ") + KindName(r.Kind).ToLowerInvariant();
        if (subs.Count > 0 && r.Sub > 0 && r.Sub < subs.Count)
            text += " (" + subs[r.Sub] + ")";
        if (r.Pattern.Trim().Length > 0 && LogFilter.PatternOf(r.Kind) is { } hint)
            text += " " + hint.Split(' ')[0] + " \"" + r.Pattern.Trim() + "\"";
        return text;
    }));

    private void RulesChanged()
    {
        ShowRules();
        _ = ShowFilteredAsync();
    }

    /// <summary>Shows what the rules leave of the opened Session, from its start, with each rule's
    /// count and the strip saying how much is shown; a search starts again from there. Filtered and
    /// counted off the UI thread; a later change or opening wins.</summary>
    private async Task ShowFilteredAsync()
    {
        // While the Session is still being read there is nothing to filter: OpenAsync filters once it is.
        if (!_fedReady)
            return;
        var version = ++_filterRun;
        var fed = _fedLines;
        var rules = _rules.ToArray();
        var countKinds = _kindCounts.Count == 0;
        var known = _allLineCount;
        var (screen, all, counts, kindCounts) = await Task.Run(() =>
        {
            var filtered = TerminalBuffer.ScreenOf(LogFilter.Apply(fed, rules));
            var total = known ?? (rules.Length == 0 ? filtered.Count : TerminalBuffer.ScreenOf(fed).Count);
            var each = rules.Select(r => LogFilter.Count(fed, r)).ToArray();
            var kinds = countKinds
                ? Enum.GetValues<RuleKind>().Where(k => k != RuleKind.Text)
                    .ToDictionary(k => k, k => LogFilter.Count(fed, NewRule(k)))
                : null;
            return (filtered, total, each, kinds);
        });
        if (version != _filterRun || !ReferenceEquals(fed, _fedLines))
            return;
        _allLineCount = all;
        if (kindCounts is not null)
            foreach (var (kind, n) in kindCounts)
                _kindCounts[kind] = n;
        _ruleCountValues = [.. counts.Select(n => n.ToString("N0", CultureInfo.CurrentCulture))];
        for (var i = 0; i < _ruleCounts.Count && i < _ruleCountValues.Count; i++)
            _ruleCounts[i].Text = _ruleCountValues[i];
        _filteredStrip.IsVisible = rules.Length > 0;
        _filteredText.Text = string.Create(CultureInfo.CurrentCulture,
            $"Showing {screen.Count:N0} of {all:N0} lines - {RulesSummary()}");
        _logLines = screen;
        _log.ShowDocument(_logLines);
        ShowMatch(null, string.Empty);
        if (_logLines.Count == 0)
            ShowNote(fed.Count == 0 ? "This Session has no lines in the game." : "No lines are left by these rules.");
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
            _searchStatus.TextColor = Loss;
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
        _searchStatus.TextColor = Muted;
    }

#if WINDOWS || MACCATALYST
    /// <summary>Says a copy was made, over the log's foot for a moment: the panels may all be shut.</summary>
    private async void ShowCopied()
    {
        var shown = ++_copiedNote;
        _copied.IsVisible = true;
        await Task.Delay(CopiedNoteMs);
        if (shown == _copiedNote)
            _copied.IsVisible = false;
    }
#endif

    // ------------------------------------------------------------------- open, close ----

    /// <summary>The window's title, in the task bar: the title the player gave, else the date, character and points.</summary>
    public string WindowTitle => _opened.Summary.ShownTitle ?? _opened.Ident;

    /// <summary>Shows <paramref name="item"/> in the header in place of the Session's row as it was: its title
    /// or tags have changed. The log is the same Session's.</summary>
    public void Show(SessionItem item)
    {
        _opened = item;
        _cardLines.Show(item);
        if (_windowed && Window is { } window)
            window.Title = WindowTitle;
    }

    /// <summary>Brings this viewer's window in front of the others.</summary>
    public void BringForward()
    {
        if (_windowed && Window is { } window)
            Application.Current?.ActivateWindow(window);
    }

    /// <summary>Closes this viewer: its window, or the page over Logs. Whatever read of the Session is
    /// still running is dropped.</summary>
    public async Task CloseAsync()
    {
        if (_released)
            return;
        Release();
        if (_windowed)
        {
            if (Window is { } window)
                Application.Current?.CloseWindow(window);
            return;
        }
        var stack = Navigation.ModalStack;
        if (stack.Count > 0 && ReferenceEquals(stack[^1], this))
            await Navigation.PopModalAsync();
    }

    /// <summary>Lets go of what this viewer holds: at most once, whichever way it closed.</summary>
    public void Release()
    {
        if (_released)
            return;
        _released = true;
        ++_openVersion;
#if WINDOWS || MACCATALYST
        _logPointer.Detach();
#endif
        _owner.ViewerClosed(this);
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        LayOutPanels();
    }

    /// <summary>Escape, or the system back button, closes the innermost thing open: the Find or Filters
    /// panel, then the viewer.</summary>
    private void Back()
    {
        if (_searchPanel.IsVisible || _filterPopup.IsVisible)
            ShowPanel(null);
        else
            _ = CloseAsync();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (!_started)
        {
            _started = true;
            _ = LoadAsync();
            BringForward();
        }
#if WINDOWS
        if (Window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window win &&
            win.Content is Microsoft.UI.Xaml.UIElement root)
            root.PreviewKeyDown += OnWindowPreviewKeyDown;
#elif UIKIT
        MacEscape.Detach(_macEscape);
        // An action and its prompts own Escape until it ends, as on Windows.
        _macEscape = MacEscape.Attach(this, () =>
        {
            if (!Held)
                Back();
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

    /// <summary>A menu, a prompt or an action is running: it owns the keys until it ends.</summary>
    private bool Held => _busy || _owner.ActionRunning;

    protected override bool OnBackButtonPressed()
    {
        if (!Held)
            Back();
        return true;
    }

#if WINDOWS
    private void OnWindowPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (Held)
            return;
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            Back();
        }
        else if (ViewerKey(e.Key))
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
}
