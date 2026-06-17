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

    private static readonly Color WantedText    = Color.FromArgb("#16C60C");
    private static readonly Color WantedBack    = Color.FromArgb("#152415");
    private static readonly Color ResolvedText  = Color.FromArgb("#0E6B0E");
    private static readonly Color ResolvedBack  = Color.FromArgb("#141B14");
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
    private readonly Button _hereBtn;
    private readonly Button _closeRoomBtn;
    private readonly Label _dirLabel;
    private readonly Label _summaryLabel;
    private readonly Label _statusLabel;
    private readonly Editor _detailEditor;
    private readonly CollectionView _list;
    private List<CaptureItem> _items = new();
    private int _lastOpsCompleted = -1;
    private Button? _guidedBtn;

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
        _dirLabel = MonoLabel("#767676", 11);
        _summaryLabel = MonoLabel("#CCCCCC", 12);
        _statusLabel = MonoLabel("#F9F1A5", 11);
        _statusLabel.VerticalOptions = LayoutOptions.Center;

        var reloadBtn = MakeButton("Reload", "#333333", "#CCCCCC", (_, _) => Reload());
        var folderBtn = MakeButton("Open folder", "#333333", "#CCCCCC", OnOpenFolderClicked);
        _closeRoomBtn = MakeButton("Close Room", "#333333", "#CCCCCC", OnCloseRoomClicked);

        var buttonRow = new HorizontalStackLayout
        {
            Spacing  = 8,
            Children = { reloadBtn, folderBtn, _closeRoomBtn, _statusLabel },
        };

        // ── Inventory ────────────────────────────────────────────────────────
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

        var separator = new BoxView { Color = Color.FromArgb("#333333"), HeightRequest = 1 };

        var grid = new Grid
        {
            Padding    = new Thickness(6),
            RowSpacing = 4,
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),   // compass
                new RowDefinition(GridLength.Auto),   // buttons + status
                new RowDefinition(GridLength.Auto),   // directory
                new RowDefinition(GridLength.Auto),   // summary
                new RowDefinition(GridLength.Star),   // capture list
                new RowDefinition(GridLength.Auto),   // separator
                new RowDefinition(GridLength.Auto),   // detail
            },
        };
        grid.Add(compass,       column: 0, row: 0);
        grid.Add(buttonRow,     column: 0, row: 1);
        grid.Add(_dirLabel,     column: 0, row: 2);
        grid.Add(_summaryLabel, column: 0, row: 3);
        grid.Add(_list,         column: 0, row: 4);
        grid.Add(separator,     column: 0, row: 5);
        grid.Add(_detailEditor, column: 0, row: 6);

        Content = grid;
    }

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

        var reciprocal = MapGraph.Reciprocal(dir);
        if (reciprocal is null)
        {
            // swamp -- no reciprocal, just the main button
            compass.Add(main, column: col, row: row);
            return main;
        }

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

        // Overlay: both share the cell; the icon sits on top of the main button.
        var cell = new Grid();
        cell.Add(main);
        cell.Add(uturn);
        compass.Add(cell, column: col, row: row);
        return main;
    }

    private static Label MonoLabel(string color, double size) => new()
    {
        FontFamily = "Cascadia Mono, Consolas, monospace",
        FontSize   = size,
        TextColor  = Color.FromArgb(color),
    };

    private static Button MakeButton(string text, string bg, string fg, EventHandler onClick)
    {
        var btn = new Button
        {
            Text            = text,
            BackgroundColor = Color.FromArgb(bg),
            TextColor       = Color.FromArgb(fg),
            Padding         = new Thickness(16, 5),
        };
        btn.Clicked += onClick;
        return btn;
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
        // Stop the guidance pulse — it's a repeating dispatcher-ticker animation on the
        // shared UI thread and would keep ticking against the closed window.
        // OnAppearing → UpdateCompass re-arms it.
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
        var room = _session.CurrentRoom;
        _hereBtn.Text = room.Length > 0 ? room : "Here?";

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
            _closeRoomBtn.Text            = remaining > 0 ? $"Cancel ({remaining} left)" : "Cancel";
            _closeRoomBtn.TextColor       = CloseActiveText;
            _closeRoomBtn.BackgroundColor = CloseActiveBack;
        }
        else
        {
            _closeRoomBtn.Text            = "Close Room";
            _closeRoomBtn.TextColor       = Color.FromArgb("#CCCCCC");
            _closeRoomBtn.BackgroundColor = Color.FromArgb("#333333");
        }

        foreach (var (dir, btn) in _dirButtons)
        {
            var enabled  = _session.IsExitEnabled(dir);
            var resolved = _session.IsResolved(dir);

            if (enabled && !resolved)
            {
                btn.TextColor       = WantedText;
                btn.BackgroundColor = WantedBack;
                btn.FontAttributes  = FontAttributes.Bold;
            }
            else if (enabled)
            {
                btn.TextColor       = ResolvedText;
                btn.BackgroundColor = ResolvedBack;
                btn.FontAttributes  = FontAttributes.None;
            }
            else
            {
                btn.TextColor       = resolved ? ResolvedText : UnlistedText;
                btn.BackgroundColor = UnlistedBack;
                btn.FontAttributes  = FontAttributes.None;
            }

            // Reset u-turn button to muted style when not blocked.
            if (_uturnButtons.TryGetValue(dir, out var ub))
            {
                ub.TextColor       = UturnText;
                ub.BackgroundColor = UturnBack;
            }
        }
    }

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
