#if WINDOWS
using System.Text;
using System.Text.Json;
using Mucka.Core.Mapping;
using Mucka.ViewModels;

namespace Mucka.Pages;


/// <summary>
/// Windows-only mapping console opened by the $map command (own window, lives beside
/// the game). Mapping is operation-driven: the compass shows the current room and its
/// enabled exits (live, from the FE EXITS events every arrival fires); clicking a
/// direction runs move-and-capture (recording refusals too -- failed edges are data);
/// clicking Here (the center) probes/refreshes the current room. Below, the
/// capture-file inventory.
///
/// Compass colours: bold light green = exit enabled and not yet captured from this
/// room; dark green = enabled and already captured; grey = not currently listed by
/// FE EXITS (still clickable -- that is how refusals and unlisted exits get recorded).
/// </summary>
internal sealed class MappingPage : ContentPage
{
    private sealed record CaptureItem(
        string FullPath, string FileName, string Room, DateTime When,
        int EntryCount, string Status, string Detail)
    {
        public string Display =>
            $"{When:MM-dd HH:mm}  {Truncate(Room, 30),-30}  {EntryCount,4}  {Status,-7}  {FileName}";

        private static string Truncate(string s, int max)
            => s.Length <= max ? s : s[..(max - 1)] + "~";
    }

    // Enabled exit: warm beige text on neutral dark (a corner "?" flags ones worth investigating).
    private static readonly Color EnabledText   = Color.FromArgb("#E8DEC9");
    private static readonly Color EnabledBack   = Color.FromArgb("#20201C");
    private static readonly Color UnlistedText  = Color.FromArgb("#555555");
    private static readonly Color UnlistedBack  = Color.FromArgb("#161616");
    // Operation in progress: cool blue-grey across the whole compass.
    private static readonly Color BusyText      = Color.FromArgb("#6F8FBF");
    private static readonly Color BusyBack      = Color.FromArgb("#1A2230");
    // FES/FEW heartbeat response in flight: amber hold.
    private static readonly Color HoldText      = Color.FromArgb("#C8A028");
    private static readonly Color HoldBack      = Color.FromArgb("#2A2210");
    private static readonly Color HereText      = Color.FromArgb("#F2F2F2");
    private static readonly Color HereBack      = Color.FromArgb("#1A2A3A");

    // Guidance: next suggested exit gets a pulsing cyan border over its normal state colours.
    private static readonly Color GuidedBorder = Color.FromArgb("#00CFCF");
    // U-turn sub-button: muted style, coloured with compass state when blocked.
    private static readonly Color UturnText  = Color.FromArgb("#888888");
    private static readonly Color UturnBack  = Color.FromArgb("#1C1C1C");
    // Return-blocked flash: brief red pulse on the Here button when u-turn has no route back.
    private static readonly Color BlockedText = Color.FromArgb("#FF5555");
    private static readonly Color BlockedBack = Color.FromArgb("#2A1010");
    // Close-room active: magenta tint on the Close Room button.
    private static readonly Color CloseActiveText = Color.FromArgb("#FF66FF");
    private static readonly Color CloseActiveBack = Color.FromArgb("#2A1A2A");
    // Close-room blocked: brief orange-red flash.
    private static readonly Color CloseBlockedText = Color.FromArgb("#FF8040");
    private static readonly Color CloseBlockedBack = Color.FromArgb("#2A1400");

    private readonly GameViewModel _vm;
    private readonly MappingSession _session;
    private readonly Dictionary<string, Button> _dirButtons  = new();
    private readonly Dictionary<string, Button> _uturnButtons = new();
    private readonly Dictionary<string, string> _dirBaseText = new();   // dir → bare compass label
    private readonly Button _hereBtn;
    private readonly Button _closeRoomBtn;
    private readonly Button _goToOpenBtn;
    private readonly Label _dirLabel;
    private readonly Label _summaryLabel;
    private readonly Label _globalStatsLabel;
    private readonly Label _deltaStatsLabel;
    private readonly Label _roomDataLabel;
    private readonly Label _statusLabel;
    private readonly ActivityIndicator _probeSpinner;
    private readonly Editor _detailEditor;
    private readonly CollectionView _list;
    private List<CaptureItem> _items = new();
    private int _lastOpsCompleted = -1;
    private Button? _guidedBtn;
    // The Window we attached Activated/Deactivated to. Saved so OnDisappearing unsubscribes
    // from the same instance even if the page's Window property has already gone null.
    private Window? _subscribedWindow;

    // A small "?" marked in the bottom-left of any exit worth investigating from the
    // current room (see MappingSession.InterestingExits).
    private readonly Dictionary<string, Label> _dirMarkers = new();

    // ROOM panel: per-exit edge table + the hand-authored rule editor (guard -> outcome).
    private readonly Label _edgesLabel;
    private readonly HorizontalStackLayout _addRuleRow;
    private readonly Picker _ruleDirPicker;
    private readonly VerticalStackLayout _ruleEditor;
    private readonly Label _ruleTargetLabel;
    private readonly Label _invHintLabel;
    private readonly Picker _guardKindPicker;
    private readonly Picker _outcomeKindPicker;
    private readonly HorizontalStackLayout _carryRow;
    private readonly HorizontalStackLayout _weatherRow;
    private readonly Entry _itemEntry;
    private readonly CheckBox _negateCheck;
    private readonly Entry _classEntry;
    private readonly Entry _weatherEntry;
    private readonly Entry _destEntry;
    private readonly Entry _textEntry;
    private readonly Entry _noteEntry;
    private string? _ruleDir;               // direction the open rule editor targets
    private string _lastEdgesText = string.Empty;   // diff guard for the edge table repaint
    private readonly Dictionary<string, string> _dirTooltip = new();   // diff guard for compass tooltips

    public MappingPage(GameViewModel vm)
    {
        _vm = vm;
        _session = vm.MapSession;
        BackgroundColor = Color.FromArgb("#0C0C0C");

        // ── Compass ──────────────────────────────────────────────────────────
        // Here = probe/refresh the room you are standing in.
        _hereBtn = new Button
        {
            FontFamily      = "Cascadia Mono, Consolas, monospace",
            FontSize        = 13,
            TextColor       = Color.FromArgb("#F2F2F2"),
            BackgroundColor = Color.FromArgb("#1A2A3A"),
            BorderColor     = Color.FromArgb("#333333"),
            BorderWidth     = 1,
            CornerRadius    = 4,
            Padding         = new Thickness(8, 4),
            LineBreakMode   = LineBreakMode.WordWrap,
        };
        _hereBtn.Clicked += (_, _) => RunOp(() =>
            _session.TryStartProbe(out var err) ? null : err);

        var compass = new Grid
        {
            RowSpacing    = 4,
            ColumnSpacing = 4,
            HorizontalOptions = LayoutOptions.Center,
            ColumnDefinitions =
            {
                new ColumnDefinition(new GridLength(64)),
                new ColumnDefinition(new GridLength(64)),
                new ColumnDefinition(new GridLength(130)),
                new ColumnDefinition(new GridLength(64)),
                new ColumnDefinition(new GridLength(64)),
            },
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(new GridLength(52)),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
            },
        };
        AddDir(compass, "out",   0, 0, "out");
        AddDir(compass, "nw",    0, 1, "nw");
        AddDir(compass, "n",     0, 2, "N");
        AddDir(compass, "ne",    0, 3, "ne");
        AddDir(compass, "up",    0, 4, "up");
        AddDir(compass, "w",     1, 0, "W");
        compass.Add(_hereBtn, column: 1, row: 1);
        Grid.SetColumnSpan(_hereBtn, 3);
        AddDir(compass, "e",     1, 4, "E");
        AddDir(compass, "down",  2, 0, "down");
        AddDir(compass, "sw",    2, 1, "sw");
        AddDir(compass, "s",     2, 2, "S");
        AddDir(compass, "se",    2, 3, "se");
        AddDir(compass, "in",    2, 4, "in");
        var swampBtn = AddDir(compass, "swamp", 3, 2, "swamp");
        swampBtn.WidthRequest = 130;

        // ── Status / actions ─────────────────────────────────────────────────
        _dirLabel = MonoLabel("#767676", 10);
        _summaryLabel = MonoLabel("#888888", 11);
        _statusLabel = MonoLabel("#F9F1A5", 11);
        _statusLabel.VerticalOptions = LayoutOptions.Center;

        // Probing spinner: visible only while an operation is in flight.
        _probeSpinner = new ActivityIndicator
        {
            Color           = Color.FromArgb("#6F8FBF"),
            WidthRequest    = 16,
            HeightRequest   = 16,
            VerticalOptions = LayoutOptions.Center,
            IsVisible       = false,
        };

        // Stats / room panels (populated by UpdateStats from the session snapshot).
        _globalStatsLabel = MonoLabel("#CCCCCC", 12);
        _deltaStatsLabel  = MonoLabel("#9FD0FF", 12);   // blue = session delta
        _roomDataLabel    = MonoLabel("#CCCCCC", 12);
        _roomDataLabel.LineBreakMode = LineBreakMode.WordWrap;   // long exits lists wrap, don't widen the panel

        // File actions live with the capture history (bottom panel); map actions stay in the
        // controls column. Resolve sits beside the ROOM header (right) -- it acts on the room.
        var reloadBtn = MakeSmallButton("Reload", (_, _) => Reload());
        var folderBtn = MakeSmallButton("Reveal in Explorer", OnOpenFolderClicked);
        _closeRoomBtn = MakeButton("Resolve", OnCloseRoomClicked);
        _goToOpenBtn  = MakeButton("Seek", OnGoToOpenClicked);

        var buttonRow = new HorizontalStackLayout
        {
            Spacing           = 8,
            HorizontalOptions = LayoutOptions.Center,
            Children          = { _goToOpenBtn },
        };

        // ── Top region: global stats / delta (left) | controls (mid) | room (right) ──
        var leftColumn = new VerticalStackLayout
        {
            Spacing  = 6,
            Children =
            {
                PanelHeader("MODEL"),
                _globalStatsLabel,
                new BoxView { Color = Color.FromArgb("#2A2A2A"), HeightRequest = 1, Margin = new Thickness(0, 4) },
                PanelHeader("THIS SESSION"),
                _deltaStatsLabel,
            },
        };

        var statusRow = new HorizontalStackLayout
        {
            Spacing           = 6,
            HorizontalOptions = LayoutOptions.Center,
            Children          = { _probeSpinner, _statusLabel },
        };

        var controls = new VerticalStackLayout
        {
            Spacing           = 6,
            HorizontalOptions = LayoutOptions.Center,
            Children          = { compass, buttonRow, statusRow },
        };

        // ROOM header carries the Resolve action (it operates on the current room).
        var roomHeaderLabel = PanelHeader("ROOM");
        roomHeaderLabel.VerticalOptions = LayoutOptions.Center;
        var roomHeader = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
        };
        roomHeader.Add(roomHeaderLabel, column: 0, row: 0);
        roomHeader.Add(_closeRoomBtn,   column: 1, row: 0);

        // Per-exit edge table (destinations + state + any rules), string-diffed on repaint.
        _edgesLabel = MonoLabel("#CCCCCC", 12);
        _edgesLabel.LineBreakMode = LineBreakMode.WordWrap;
        _edgesLabel.IsVisible = false;

        // Add-rule row: pick a direction, open the rule editor for it.
        _ruleDirPicker = new Picker
        {
            Title      = "dir",
            FontFamily = "Cascadia Mono, Consolas, monospace",
            FontSize   = 11,
            TextColor  = Color.FromArgb("#CCCCCC"),
            WidthRequest = 70,
            // All directions -- rules must be addable to exits the game currently hides
            // (e.g. a rain-gated exit absent from the fex), not just the enabled ones.
            ItemsSource = MappingSession.Directions,
        };
        var addRuleBtn = MakeSmallButton("Add rule...", OnAddRuleClicked);
        var ruleRowLabel = MonoLabel("#767676", 11);
        ruleRowLabel.Text = "rule:";
        ruleRowLabel.VerticalOptions = LayoutOptions.Center;
        _addRuleRow = new HorizontalStackLayout
        {
            Spacing   = 6,
            IsVisible = false,
            Children  = { ruleRowLabel, _ruleDirPicker, addRuleBtn },
        };

        // Rule editor (hidden until Add rule...). A guard -> outcome row, human-authored.
        _ruleTargetLabel = MonoLabel("#9FD0FF", 11);
        _invHintLabel    = MonoLabel("#767676", 10);
        _invHintLabel.LineBreakMode = LineBreakMode.WordWrap;

        _guardKindPicker = new Picker
        {
            Title = "when", FontFamily = "Cascadia Mono, Consolas, monospace", FontSize = 11,
            TextColor = Color.FromArgb("#CCCCCC"), WidthRequest = 110,
            ItemsSource = new List<string> { "carrying", "weather", "else" },
            SelectedIndex = 0,
        };
        _guardKindPicker.SelectedIndexChanged += (_, _) => UpdateRuleEditorVisibility();

        _itemEntry   = MakeRuleEntry("item (e.g. coracle)", 130);
        _negateCheck = new CheckBox { Color = Color.FromArgb("#9FD0FF") };
        _classEntry  = MakeRuleEntry("class (opt, e.g. boat)", 140);
        _carryRow = new HorizontalStackLayout
        {
            Spacing  = 6,
            Children = { _itemEntry, _negateCheck, MakeMini("not carrying"), _classEntry },
        };

        _weatherEntry = MakeRuleEntry("weather state (e.g. rain)", 180);
        _weatherRow = new HorizontalStackLayout
        {
            Spacing  = 6,
            Children = { MakeMini("state"), _weatherEntry },
        };

        _outcomeKindPicker = new Picker
        {
            Title = "then", FontFamily = "Cascadia Mono, Consolas, monospace", FontSize = 11,
            TextColor = Color.FromArgb("#CCCCCC"), WidthRequest = 110,
            ItemsSource = new List<string> { "arrive", "refuse", "absent" },
            SelectedIndex = 0,
        };
        _outcomeKindPicker.SelectedIndexChanged += (_, _) => UpdateRuleEditorVisibility();

        _destEntry = MakeRuleEntry("destination room", 220);
        _textEntry = MakeRuleEntry("refusal message", 260);
        _noteEntry = MakeRuleEntry("note (optional)", 260);

        var ruleSaveBtn   = MakeSmallButton("Save rule", OnRuleSaveClicked);
        var ruleCancelBtn = MakeSmallButton("Cancel", OnRuleCancelClicked);

        _ruleEditor = new VerticalStackLayout
        {
            Spacing         = 4,
            IsVisible       = false,
            Padding         = new Thickness(6),
            BackgroundColor = Color.FromArgb("#141414"),
            Children =
            {
                _ruleTargetLabel,
                _invHintLabel,
                RuleFieldRow("when", _guardKindPicker),
                _carryRow,
                _weatherRow,
                RuleFieldRow("then", _outcomeKindPicker),
                _destEntry,
                _textEntry,
                _noteEntry,
                new HorizontalStackLayout { Spacing = 6, Children = { ruleSaveBtn, ruleCancelBtn } },
            },
        };

        var roomColumn = new VerticalStackLayout
        {
            Spacing  = 6,
            Children = { roomHeader, _roomDataLabel, _edgesLabel, _addRuleRow, _ruleEditor },
        };

        var topRegion = new Grid
        {
            ColumnSpacing = 14,
            ColumnDefinitions =
            {
                new ColumnDefinition(new GridLength(190)),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
            },
        };
        topRegion.Add(leftColumn, column: 0, row: 0);
        topRegion.Add(controls,   column: 1, row: 0);
        topRegion.Add(roomColumn, column: 2, row: 0);

        // ── History: capture summary + list + detail ──────────────────────────
        _list = new CollectionView
        {
            SelectionMode = SelectionMode.Single,
            ItemTemplate = new DataTemplate(() =>
            {
                var label = MonoLabel("#CCCCCC", 12);
                label.Padding = new Thickness(4, 2);
                label.SetBinding(Label.TextProperty, nameof(CaptureItem.Display));
                return label;
            }),
        };
        _list.SelectionChanged += OnSelectionChanged;

        _detailEditor = new Editor
        {
            FontFamily      = "Cascadia Mono, Consolas, monospace",
            FontSize        = 11,
            TextColor       = Color.FromArgb("#CCCCCC"),
            BackgroundColor = Color.FromArgb("#1A1A1A"),
            IsReadOnly      = true,
            HeightRequest   = 120,
            AutoSize        = EditorAutoSizeOption.Disabled,
        };

        var historyButtons = new HorizontalStackLayout
        {
            Spacing  = 6,
            Children = { reloadBtn, folderBtn },
        };
        var historyHeader = new VerticalStackLayout
        {
            Spacing  = 4,
            Children = { historyButtons, _summaryLabel, _dirLabel },
        };
        var separator = new BoxView { Color = Color.FromArgb("#333333"), HeightRequest = 1 };

        var grid = new Grid
        {
            Padding    = new Thickness(6),
            RowSpacing = 6,
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),   // top region (3 panels)
                new RowDefinition(GridLength.Auto),   // separator
                new RowDefinition(GridLength.Auto),   // capture summary + dir
                new RowDefinition(GridLength.Star),   // capture list
                new RowDefinition(GridLength.Auto),   // detail
            },
        };
        grid.Add(topRegion,     column: 0, row: 0);
        grid.Add(separator,     column: 0, row: 1);
        grid.Add(historyHeader, column: 0, row: 2);
        grid.Add(_list,         column: 0, row: 3);
        grid.Add(_detailEditor, column: 0, row: 4);

        Content = grid;
    }

    private static Label PanelHeader(string text) => new()
    {
        Text           = text,
        FontFamily     = "Cascadia Mono, Consolas, monospace",
        FontSize       = 10,
        FontAttributes = FontAttributes.Bold,
        TextColor      = Color.FromArgb("#6F6F6F"),
    };

    private Button AddDir(Grid compass, string dir, int row, int col, string text)
    {
        var main = new Button
        {
            Text         = text,
            FontFamily   = "Cascadia Mono, Consolas, monospace",
            FontSize     = 13,
            Padding      = new Thickness(0, 8),
            CornerRadius = 4,
        };
        main.Clicked += (_, _) => RunOp(() => _session.TryStartMove(dir, out var err) ? null : err);
        _dirButtons[dir] = main;
        _dirBaseText[dir] = text;

        // Every direction shares a cell so the "?" marker can overlay the button's
        // corner. InputTransparent lets taps fall through to the button.
        var cell = new Grid();
        cell.Add(main);

        // Bottom-LEFT, opposite the top-right u-turn button. Amber "?" flags an exit
        // worth investigating; InputTransparent so the move button beneath takes the click.
        var marker = new Label
        {
            Text              = "?",
            TextColor         = Color.FromArgb("#E3B341"),
            FontAttributes    = FontAttributes.Bold,
            FontSize          = 13,
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions   = LayoutOptions.End,
            Margin            = new Thickness(2, 0, 0, 2),
            IsVisible         = false,
            InputTransparent  = true,
        };
        cell.Add(marker);
        _dirMarkers[dir] = marker;

        var reciprocal = MapGraph.Reciprocal(dir);
        if (reciprocal is not null)
        {
            // There-and-back: a small u-turn icon inset in the button's top-right corner.
            // Tapping the button moves; tapping the icon moves AND attempts the reciprocal
            // return (decided on arrival -- the destination may not offer a way back).
            // Min*Request = 0 beats the WinUI native 32px button minimum that would
            // otherwise swallow an icon this small.
            var uturn = new Button
            {
                Text                  = "↩",
                FontSize              = 10,
                Padding               = new Thickness(0),
                CornerRadius          = 3,
                WidthRequest          = 22,
                HeightRequest         = 18,
                MinimumWidthRequest   = 0,
                MinimumHeightRequest  = 0,
                HorizontalOptions     = LayoutOptions.End,
                VerticalOptions       = LayoutOptions.Start,
                Margin                = new Thickness(0, 1, 1, 0),
                TextColor             = UturnText,
                BackgroundColor       = UturnBack,
            };
            uturn.Clicked += (_, _) => RunOp(() => _session.TryStartUturn(dir, out var err) ? null : err);
            _uturnButtons[dir] = uturn;
            cell.Add(uturn);
        }

        compass.Add(cell, column: col, row: row);
        return main;
    }

    private static Label MonoLabel(string color, double size) => new()
    {
        FontFamily = "Cascadia Mono, Consolas, monospace",
        FontSize   = size,
        TextColor  = Color.FromArgb(color),
    };

    // Primary action button: reads as clickable -- steel-blue fill, bright bold text, border.
    private static readonly Color BtnText   = Color.FromArgb("#E6ECF2");
    private static readonly Color BtnBack   = Color.FromArgb("#33445A");
    private static readonly Color BtnBorder = Color.FromArgb("#5A7390");

    private static Button MakeButton(string text, EventHandler onClick)
    {
        var btn = new Button
        {
            Text            = text,
            TextColor       = BtnText,
            BackgroundColor = BtnBack,
            BorderColor     = BtnBorder,
            BorderWidth     = 1,
            CornerRadius    = 5,
            FontAttributes  = FontAttributes.Bold,
            Padding         = new Thickness(16, 6),
        };
        btn.Clicked += onClick;
        return btn;
    }

    // Secondary/file action button for the history panel -- smaller, quieter.
    private static Button MakeSmallButton(string text, EventHandler onClick)
    {
        var btn = new Button
        {
            Text                 = text,
            FontSize             = 11,
            TextColor            = Color.FromArgb("#C8D2DC"),
            BackgroundColor      = Color.FromArgb("#2A323C"),
            BorderColor          = Color.FromArgb("#46525E"),
            BorderWidth          = 1,
            CornerRadius         = 4,
            Padding              = new Thickness(10, 3),
            MinimumWidthRequest  = 0,
            MinimumHeightRequest = 0,
        };
        btn.Clicked += onClick;
        return btn;
    }

    // Restyle an existing button in place -- keeps the Text/colour triple in one spot.
    private static void StyleButton(Button b, string text, Color fg, Color bg)
    {
        b.Text = text; b.TextColor = fg; b.BackgroundColor = bg;
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _session.StateChanged   += OnSessionStateChanged;
        _session.Status         += OnSessionStatus;
        _session.ReturnBlocked  += OnSessionReturnBlocked;
        _session.CloseRoomComplete += OnCloseRoomComplete;
        _session.CloseRoomBlocked  += OnCloseRoomBlocked;
        // Heartbeat omits FEI while this window has focus, but keeps FES+FEW so the online list
        // refreshes reliably. Appearing implies focus.
        if (Window is { } w)
        {
            _subscribedWindow = w;
            w.Activated   += OnWindowActivated;
            w.Deactivated += OnWindowDeactivated;
        }
        _session.SetMappingFocus(true);
        UpdateCompass();
        Reload();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _session.StateChanged   -= OnSessionStateChanged;
        _session.Status         -= OnSessionStatus;
        _session.ReturnBlocked  -= OnSessionReturnBlocked;
        _session.CloseRoomComplete -= OnCloseRoomComplete;
        _session.CloseRoomBlocked  -= OnCloseRoomBlocked;
        if (_subscribedWindow is { } w)
        {
            w.Activated   -= OnWindowActivated;
            w.Deactivated -= OnWindowDeactivated;
            _subscribedWindow = null;
        }
        _session.SetMappingFocus(false);
        // Stop the guidance pulse — it's a repeating dispatcher-ticker animation on the
        // shared UI thread and would keep ticking against the closed window.
        // OnAppearing → UpdateCompass re-arms it.
        SetGuidance(null);
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        _session.SetMappingFocus(true);
        // Refresh: state may have changed (manual moves in the game window) while we were away.
        UpdateCompass();
        Reload();
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        _session.SetMappingFocus(false);
        // Stop the guidance pulse while the game window has focus -- it's a repeating
        // dispatcher animation on the shared UI thread and must not tick against the
        // input box. OnWindowActivated → UpdateCompass re-arms it on return.
        SetGuidance(null);
    }

    // ── Session events (arbitrary thread) ────────────────────────────────────

    private void OnSessionStateChanged()
        => MainThread.BeginInvokeOnMainThread(() =>
        {
            UpdateCompass();
            if (_session.OpsCompleted != _lastOpsCompleted)
            {
                _lastOpsCompleted = _session.OpsCompleted;
                Reload();   // an operation finished -- the walk file grew
            }
        });

    private void OnSessionStatus(string status)
        => MainThread.BeginInvokeOnMainThread(() => _statusLabel.Text = status);

    private void OnSessionReturnBlocked()
        => MainThread.BeginInvokeOnMainThread(() => _ = FlashReturnBlockedAsync());

    private async Task FlashReturnBlockedAsync()
    {
        _hereBtn.TextColor       = BlockedText;
        _hereBtn.BackgroundColor = BlockedBack;
        await Task.Delay(900);
        UpdateCompass();
    }

    private void OnCloseRoomComplete()
        => MainThread.BeginInvokeOnMainThread(() =>
        {
            _statusLabel.Text = "close room: done";
            UpdateCompass();
        });

    private void OnCloseRoomBlocked(string reason)
        => MainThread.BeginInvokeOnMainThread(() => _ = FlashCloseRoomBlockedAsync(reason));

    private async Task FlashCloseRoomBlockedAsync(string reason)
    {
        _closeRoomBtn.TextColor       = CloseBlockedText;
        _closeRoomBtn.BackgroundColor = CloseBlockedBack;
        _statusLabel.Text = $"close room: blocked -- {reason}";
        await Task.Delay(1200);
        UpdateCompass();
    }

    private void OnCloseRoomClicked(object? sender, EventArgs e)
    {
        if (_session.IsClosingRoom)
        {
            _session.CancelCloseRoom();
        }
        else
        {
            RunOp(() => _session.TryStartCloseRoom(out var err) ? null : err);
        }
    }

    private void OnGoToOpenClicked(object? sender, EventArgs e)
    {
        if (_session.IsGoingToOpen)
            _session.CancelGoToOpen();
        else
            RunOp(() => _session.TryStartGoToOpen(out var err) ? null : err);
    }

    // ── Actions ──────────────────────────────────────────────────────────────

    private void RunOp(Func<string?> start)
    {
        var error = start();
        if (error is not null)
            _statusLabel.Text = error;
        UpdateCompass();
    }

    private void OnOpenFolderClicked(object? sender, EventArgs e)
    {
        var dir = _vm.MappingDirectory;
        Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start("explorer.exe", dir);
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _detailEditor.Text = e.CurrentSelection.FirstOrDefault() is CaptureItem item
            ? item.Detail
            : string.Empty;
    }

    // ── Compass state ────────────────────────────────────────────────────────

    private void UpdateCompass()
    {
        UpdateStats();

        var room = _session.CurrentRoom;
        _hereBtn.Text = room.Length > 0 ? room : "Here?";

        // Probing indicator: spin whenever an operation is in flight.
        _probeSpinner.IsVisible = _probeSpinner.IsRunning = _session.Busy;

        // Mapping blocked? Tint the whole compass rather than greying it out: blue =
        // our own operation running, amber = a stats/who heartbeat response in flight.
        // Buttons stay clickable; a click while blocked is rejected with the reason.
        var (blockedText, blockedBack) =
            _session.Busy             ? (BusyText, BusyBack) :
            _session.HeartbeatBlocked ? (HoldText, HoldBack) :
            (null, null!);
        if (blockedText is not null)
        {
            SetGuidance(null);
            _hereBtn.TextColor       = blockedText;
            _hereBtn.BackgroundColor = blockedBack;
            foreach (var btn in _dirButtons.Values)
            {
                btn.TextColor       = blockedText;
                btn.BackgroundColor = blockedBack;
                btn.FontAttributes  = FontAttributes.None;
            }
            foreach (var btn in _uturnButtons.Values)
            {
                btn.TextColor       = blockedText;
                btn.BackgroundColor = blockedBack;
            }
            // Hide the "?" markers while blocked -- a stale amber "?" left painted over the
            // busy/heartbeat tint contradicts the "working, hands off" signal.
            foreach (var marker in _dirMarkers.Values)
                marker.IsVisible = false;
            return;
        }

        _hereBtn.TextColor       = HereText;
        _hereBtn.BackgroundColor = HereBack;

        // Ask the guidance engine for the next suggested direction.
        var suggested = _session.SuggestedNextExit();
        SetGuidance(suggested is not null && _dirButtons.TryGetValue(suggested, out var gb) ? gb : null);

        // Close Room button: active style when cycling, showing remaining count.
        if (_session.IsClosingRoom)
        {
            var remaining = _session.CloseRoomRemainingExits;
            StyleButton(_closeRoomBtn, remaining > 0 ? $"Cancel ({remaining} left)" : "Cancel", CloseActiveText, CloseActiveBack);
        }
        else
        {
            StyleButton(_closeRoomBtn, "Resolve", BtnText, BtnBack);
        }

        // Seek: active (cyan) while auto-walking, with a Cancel affordance.
        if (_session.IsGoingToOpen)
        {
            StyleButton(_goToOpenBtn, "Cancel", Color.FromArgb("#00CFCF"), Color.FromArgb("#10242A"));
        }
        else
        {
            StyleButton(_goToOpenBtn, "Seek", BtnText, BtnBack);
        }

        // Colour model: gray = no exit, green = exit. A "?" in the corner flags exits worth
        // investigating (and bolds the label). One batched query per repaint.
        var interesting = _session.InterestingExits();
        foreach (var (dir, btn) in _dirButtons)
        {
            var flag    = interesting.Contains(dir);
            var enabled = _session.IsExitEnabled(dir);
            // Tooltip: only touch the native control when the text actually changed (this runs
            // every repaint / heartbeat -- see invariant #1).
            var tip = BuildTooltip(dir);
            if (!_dirTooltip.TryGetValue(dir, out var prevTip) || prevTip != tip)
            {
                _dirTooltip[dir] = tip;
                Microsoft.Maui.Controls.ToolTipProperties.SetText(btn, tip);
            }
            btn.Text            = _dirBaseText.GetValueOrDefault(dir, dir);
            btn.TextColor       = enabled ? EnabledText : UnlistedText;
            btn.BackgroundColor = enabled ? EnabledBack : UnlistedBack;
            btn.FontAttributes  = flag ? FontAttributes.Bold : FontAttributes.None;

            if (_dirMarkers.TryGetValue(dir, out var marker))
                marker.IsVisible = flag;

            // Reset u-turn button to muted style when not blocked.
            if (_uturnButtons.TryGetValue(dir, out var ub))
            {
                ub.TextColor       = UturnText;
                ub.BackgroundColor = UturnBack;
            }
        }
    }

    /// <summary>Repaints the three data panels from the session snapshot. Cheap; called
    /// on every state change alongside UpdateCompass.</summary>
    private void UpdateStats()
    {
        var stats = _session.Stats;
        var baseline = _session.BaselineStats;

        _globalStatsLabel.Text = string.Join('\n', new[]
        {
            $"rooms   {stats.Rooms,4}",
            $" open   {stats.OpenRooms,4}",
            $" closed {stats.ClosedRooms,4}  (provisional)",
            $"edges   {stats.Edges,4}",
            $"dark    {stats.DarkExits,4}  to revisit",
        });

        _deltaStatsLabel.Text = string.Join('\n', new[]
        {
            $"rooms  {Delta(stats.Rooms - baseline.Rooms)}",
            $"closed {Delta(stats.ClosedRooms - baseline.ClosedRooms)}",
            $"edges  {Delta(stats.Edges - baseline.Edges)}",
            $"dark   {Delta(stats.DarkExits - baseline.DarkExits)}",
        });

        // Room panel: name / darkness, and a per-exit edge table (destinations, state, rules).
        if (_session.CurrentRoomIsDark)
        {
            _roomDataLabel.Text = "** DARK **\nno light source --\ncan't identify this room";
            HideEdgePanel();
        }
        else if (_session.CurrentRoom.Length == 0)
        {
            _roomDataLabel.Text = "(unknown)\nclick Here to probe";
            HideEdgePanel();
        }
        else
        {
            _roomDataLabel.Text = _session.CurrentRoom;

            var edges = BuildEdgesText();
            if (edges != _lastEdgesText) { _edgesLabel.Text = edges; _lastEdgesText = edges; }
            _edgesLabel.IsVisible = true;
            _addRuleRow.IsVisible = true;
        }
    }

    /// <summary>Hide the edge table + rule controls (dark/unknown room -- nothing to show).</summary>
    private void HideEdgePanel()
    {
        _edgesLabel.IsVisible = false;
        _addRuleRow.IsVisible = false;
        _ruleEditor.IsVisible = false;
        _ruleDir = null;
        _lastEdgesText = string.Empty;
    }

    private static string Delta(int n) => n > 0 ? $"+{n}" : n.ToString();

    // -- Edge table + rule editor --

    /// <summary>Per-exit table for the current room: each enabled (or ruled/dark) direction
    /// with its destination and state, plus any hand-authored rule rows beneath it.</summary>
    private string BuildEdgesText()
    {
        var sb = new StringBuilder();
        foreach (var dir in MappingSession.Directions)
        {
            var info = _session.GetEdgeInfo(dir);
            // Show any direction we know something about -- including a recorded refusal or a
            // hand-authored rule on an exit the game currently hides (a rain-gated exit absent
            // from the fex). Skip only directions with no signal at all.
            if (!info.Enabled && !info.Dark && !info.Resolved && info.Reported is null && info.Rules.Count == 0)
                continue;
            string dest, state;
            if (info.Dark)                                { dest = "??"; state = "dark"; }
            else if (info.Resolved && info.Dest is { } d) { dest = info.SelfLoop ? "(self)" : Trunc(d, 22); state = info.SelfLoop ? "loop" : "walked"; }
            else if (info.Resolved && !info.Enabled)      { dest = "--"; state = "refused"; }
            else if (info.Resolved)                       { dest = "--"; state = "closed"; }
            else if (info.Reported is { } rn)             { dest = Trunc(rn, 22); state = "reported"; }
            else                                          { dest = "?";  state = "new"; }
            sb.AppendLine($"{dir,-5}-> {dest,-22} {state}");
            foreach (var r in info.Rules)
                sb.AppendLine($"       {r.Guard.Describe()} {r.Outcome.Describe()}");
        }
        var s = sb.ToString().TrimEnd();
        return s.Length == 0 ? "(no exits listed)" : s;
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "~";

    /// <summary>Hover summary for a compass direction: destination/state + any rule rows.</summary>
    private string BuildTooltip(string dir)
    {
        var info = _session.GetEdgeInfo(dir);
        var head =
            info.Dark                           ? $"{dir} -> ?? (dark, needs light)" :
            info.Resolved && info.Dest is { } d ? $"{dir} -> {(info.SelfLoop ? "(self-loop)" : d)}" :
            info.Resolved                       ? $"{dir} -> (closed/refused)" :
            info.Reported is { } rn             ? $"{dir} -> {rn} (reported)" :
            info.Enabled                        ? $"{dir} -> ? (unexplored)" :
                                                  $"{dir} (not listed)";
        if (info.Rules.Count > 0)
            head += "\n" + string.Join("\n", info.Rules.Select(r => $"  {r.Guard.Describe()} {r.Outcome.Describe()}"));
        return head;
    }

    private void OnAddRuleClicked(object? sender, EventArgs e)
    {
        if (_ruleDirPicker.SelectedItem is not string dir)
        {
            _statusLabel.Text = "pick a direction to add a rule for";
            return;
        }
        _ruleDir = dir;
        _ruleTargetLabel.Text = $"Rule for {dir} in {_session.CurrentRoom}";
        var inv = _session.CurrentInventory;
        _invHintLabel.Text = inv.Count > 0 ? "carrying: " + string.Join(", ", inv) : "carrying: (nothing seen)";
        var info = _session.GetEdgeInfo(dir);
        _guardKindPicker.SelectedIndex   = 0;
        _outcomeKindPicker.SelectedIndex = 0;
        _itemEntry.Text  = inv.Count > 0 ? inv[0] : string.Empty;
        _negateCheck.IsChecked = false;
        _classEntry.Text = string.Empty;
        _weatherEntry.Text = string.Empty;
        _destEntry.Text  = info.Dest ?? info.Reported ?? string.Empty;
        _textEntry.Text  = string.Empty;
        _noteEntry.Text  = string.Empty;
        UpdateRuleEditorVisibility();
        _ruleEditor.IsVisible = true;
    }

    private void OnRuleSaveClicked(object? sender, EventArgs e)
    {
        if (_ruleDir is null) return;

        RuleGuard guard;
        switch (_guardKindPicker.SelectedItem as string)
        {
            case "carrying":
                var item = (_itemEntry.Text ?? string.Empty).Trim();
                if (item.Length == 0) { _statusLabel.Text = "rule: enter the item"; return; }
                guard = new RuleGuard("carrying", item, _negateCheck.IsChecked, NullIfBlank(_classEntry.Text));
                break;
            case "weather":
                var state = (_weatherEntry.Text ?? string.Empty).Trim();
                if (state.Length == 0) { _statusLabel.Text = "rule: enter the weather state (e.g. rain)"; return; }
                guard = new RuleGuard("weather", State: state);
                break;
            default:
                guard = new RuleGuard("else");
                break;
        }

        RuleOutcome outcome;
        switch (_outcomeKindPicker.SelectedItem as string)
        {
            case "arrive":
                var dest = (_destEntry.Text ?? string.Empty).Trim();
                if (dest.Length == 0) { _statusLabel.Text = "rule: enter the destination room"; return; }
                outcome = new RuleOutcome("arrive", dest);
                break;
            case "refuse":
                var text = (_textEntry.Text ?? string.Empty).Trim();
                if (text.Length == 0) { _statusLabel.Text = "rule: enter the refusal message"; return; }
                outcome = new RuleOutcome("refuse", null, text);
                break;
            default:
                outcome = new RuleOutcome("absent");
                break;
        }

        if (_session.AddEdgeRule(_ruleDir, guard, outcome, NullIfBlank(_noteEntry.Text), out var err))
        {
            _ruleEditor.IsVisible = false;
            _ruleDir = null;
            _lastEdgesText = string.Empty;   // force the edge table to repaint with the new rule
            UpdateCompass();
        }
        else _statusLabel.Text = err ?? "rule: could not add";
    }

    private void OnRuleCancelClicked(object? sender, EventArgs e)
    {
        _ruleEditor.IsVisible = false;
        _ruleDir = null;
    }

    private void UpdateRuleEditorVisibility()
    {
        var guard = _guardKindPicker.SelectedItem as string;
        _carryRow.IsVisible   = guard == "carrying";
        _weatherRow.IsVisible = guard == "weather";
        var outcome = _outcomeKindPicker.SelectedItem as string;
        _destEntry.IsVisible = outcome == "arrive";
        _textEntry.IsVisible = outcome == "refuse";
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static Entry MakeRuleEntry(string placeholder, double width) => new()
    {
        Placeholder     = placeholder,
        FontFamily      = "Cascadia Mono, Consolas, monospace",
        FontSize        = 11,
        TextColor       = Color.FromArgb("#E6ECF2"),
        BackgroundColor = Color.FromArgb("#1A1A1A"),
        WidthRequest    = width,
    };

    private static Label MakeMini(string text) => new()
    {
        Text            = text,
        FontFamily      = "Cascadia Mono, Consolas, monospace",
        FontSize        = 10,
        TextColor       = Color.FromArgb("#767676"),
        VerticalOptions = LayoutOptions.Center,
    };

    private static View RuleFieldRow(string label, View control) => new HorizontalStackLayout
    {
        Spacing  = 6,
        Children = { MakeMini(label), control },
    };

    /// <summary>Moves the pulsing guidance border to the given button (null = clear).
    /// State colours stay as-is -- the border is an overlay, so the suggested exit
    /// still reads as wanted/resolved/unlisted underneath the attention pulse.</summary>
    private void SetGuidance(Button? btn)
    {
        if (ReferenceEquals(_guidedBtn, btn)) return;   // keep the running pulse, no restart flicker
        if (_guidedBtn is { } old)
        {
            old.AbortAnimation("guide");
            old.BorderWidth = 0;
            old.BorderColor = Colors.Transparent;
        }
        _guidedBtn = btn;
        if (btn is null) return;

        btn.BorderWidth = 2;
        var pulse = new Animation();
        pulse.Add(0.0, 0.5, new Animation(
            t => btn.BorderColor = GuidedBorder.WithAlpha((float)t), 0.15, 1.0, Easing.SinInOut));
        pulse.Add(0.5, 1.0, new Animation(
            t => btn.BorderColor = GuidedBorder.WithAlpha((float)t), 1.0, 0.15, Easing.SinInOut));
        pulse.Commit(btn, "guide", length: 1100, repeat: () => true);
    }

    // ── Inventory scan ───────────────────────────────────────────────────────

    private async void Reload()
    {
        var dir = _vm.MappingDirectory;
        _dirLabel.Text = dir;
        try
        {
            _items = await Task.Run(() => ScanDirectory(dir));
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"scan failed: {ex.Message}";
            return;
        }

        _list.ItemsSource = _items;
        var rooms = _items.SelectMany(i => i.Room.Split(" / "))
                          .Where(r => r.Length > 0)
                          .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var edges = _items.Sum(i => i.Detail.Split('\n').Count(l => l.StartsWith("an: edge:")));
        _summaryLabel.Text = _items.Count == 0
            ? "No captures yet -- click Here (compass center) to record the room you are standing in."
            : $"{_items.Count} capture(s), {rooms} distinct room name(s), {edges} edge(s) recorded.";
        UpdateCompass();   // re-apply guidance now that the graph is fresh
    }

    private static List<CaptureItem> ScanDirectory(string dir)
    {
        var items = new List<CaptureItem>();
        if (!Directory.Exists(dir))
            return items;

        foreach (var path in Directory.GetFiles(dir, "*.jsonl"))
        {
            var entries = 0;
            var rooms = new List<string>();
            var status = string.Empty;
            long rxBytes = 0;
            var detail = new StringBuilder();
            try
            {
                // ReadLinesShared: the live walk file is still open for writing.
                foreach (var line in MappingStore.ReadLinesShared(path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    entries++;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;  // {"extra":...} records
                        var mode = doc.RootElement[1].GetString();
                        var data = doc.RootElement[2].GetString() ?? string.Empty;
                        switch (mode)
                        {
                            case "an":
                                if (data.StartsWith("room: ", StringComparison.Ordinal) && !rooms.Contains(data[6..]))
                                    rooms.Add(data[6..]);
                                if (data is "probe complete") status = "ok";
                                else if (data.EndsWith("timeout", StringComparison.Ordinal)) status = "timeout";
                                detail.AppendLine($"an: {data}");
                                break;
                            case "tx":
                                detail.AppendLine($"tx: {Printable(data)}");
                                break;
                            case "rx":
                                rxBytes += data.Length;
                                break;
                        }
                    }
                    catch { /* partial or malformed line (mid-write) -- skip, keep scanning */ }
                }
                detail.AppendLine($"rx: {rxBytes} bytes total");
                detail.Append($"decode: uv run tools/mapping/decode_probe.py \"{path}\"");
            }
            catch (Exception ex)
            {
                status = "bad";
                detail.AppendLine($"unreadable: {ex.Message}");
            }
            items.Add(new CaptureItem(
                path, Path.GetFileName(path), string.Join(" / ", rooms), File.GetLastWriteTime(path),
                entries, status, detail.ToString()));
        }
        return items.OrderByDescending(i => i.When).ToList();
    }

    /// <summary>Escape control bytes so tx lines (command interrupts) render readably.</summary>
    private static string Printable(string data)
    {
        var sb = new StringBuilder(data.Length + 8);
        foreach (var c in data)
            sb.Append(c switch
            {
                '\x1b' => "\\e",
                '\r'   => "\\r",
                '\n'   => "\\n",
                < ' '  => $"\\x{(int)c:X2}",
                _      => c.ToString(),
            });
        return sb.ToString();
    }
}
#endif
