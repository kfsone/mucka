using Microsoft.Maui.Graphics;
using MudSharp.Models;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;

namespace Mucka.ViewModels;

public sealed class SidePanelViewModel : BaseViewModel, IDisposable
{
    // On Windows the side panel defaults to expanded; the initial window width
    // is sized to fit it alongside the terminal view (see GamePage.SetPreferredInitialWindowSize).
    private bool _isPanelExpanded
#if WINDOWS
        = true
#endif
        ;
    private bool _isAboutVisible;
    private string _currentRoom  = "";
    private string _previousRoom = "Option Menu";
    private string _oldestRoom   = "Logging in";

    // ── Section fold/unfold state ─────────────────────────────────────────────
    // Each section heading has a ▼/▶ widget; folding is equivalent to disabling in settings.
    private bool _isOnlineExpanded   = true;
    private bool _isInventoryExpanded = true;
    private bool _isItemsHereExpanded = true;
    private bool _isMapExpanded      = true;
    private bool _isOnlinePinned = true;   // pinned (floating panel follows when side panel is hidden)
    private bool _isFloatingOnlineFolded;
    private bool _isFloatingOnlineLocked = true;   // windlets start locked: content only, no strip, no drag
    private bool _namesOnly;
    private int  _maxOnline;

    public bool IsPanelExpanded
    {
        get => _isPanelExpanded;
        set => SetAndNotify(ref _isPanelExpanded, value,
            [nameof(IsPanelCollapsed), nameof(PanelToggleGlyph)]);
    }
    public bool IsPanelCollapsed => !_isPanelExpanded;
    // ▼ when collapsed (click to show panel), ◀ when expanded (click to hide the left-edge panel)
    public string PanelToggleGlyph => _isPanelExpanded ? "◀" : "▼";

    // ── Section fold/unfold ────────────────────────────────────────────────────
    // ▼ = expanded (content visible), ▶ = collapsed (content hidden).
    public bool IsOnlineExpanded
    {
        get => _isOnlineExpanded;
        set
        {
            if (SetAndNotify(ref _isOnlineExpanded, value, [nameof(OnlineFoldGlyph)]))
                RaiseSubscriptionChanged();
        }
    }
    public string OnlineFoldGlyph => _isOnlineExpanded ? "\u25bc" : "\u25b6";

    public bool IsInventoryExpanded
    {
        get => _isInventoryExpanded;
        set
        {
            if (SetAndNotify(ref _isInventoryExpanded, value, [nameof(InventoryFoldGlyph)]))
                RaiseSubscriptionChanged();
        }
    }
    public string InventoryFoldGlyph => _isInventoryExpanded ? "\u25bc" : "\u25b6";

    public bool IsItemsHereExpanded
    {
        get => _isItemsHereExpanded;
        set
        {
            if (SetAndNotify(ref _isItemsHereExpanded, value, [nameof(ItemsHereFoldGlyph)]))
                RaiseSubscriptionChanged();
        }
    }
    public string ItemsHereFoldGlyph => _isItemsHereExpanded ? "\u25bc" : "\u25b6";

    public bool IsMapExpanded
    {
        get => _isMapExpanded;
        set => SetAndNotify(ref _isMapExpanded, value, [nameof(MapFoldGlyph), nameof(IsDockedCompassVisible)]);
    }
    public string MapFoldGlyph => _isMapExpanded ? "\u25bc" : "\u25b6";

    // \u2500\u2500 Compass float/dock state \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
    // Mirrors the online panel: the compass can be docked in the side rail or floated free
    // (for phone users). The room trail never floats \u2014 only the compass moves.
    private bool _isMapPinned = true;
    private bool _isFloatingMapFolded;
    private bool _isFloatingMapLocked = true;   // windlets start locked: content only, no strip, no drag

    /// <summary>When true the compass is docked in the side rail; when false it floats.</summary>
    public bool IsMapPinned
    {
        get => _isMapPinned;
        set => SetAndNotify(ref _isMapPinned, value,
            [nameof(IsFloatingMapVisible), nameof(IsDockedCompassVisible), nameof(MapPinGlyph), nameof(MapPinColor)]);
    }

    /// <summary>Glyph for the compass float toggle \u2014 shows the action, not the state:
    /// hollow "float me" square while docked, filled "dock me" square while floating.</summary>
    public string MapPinGlyph => _isMapPinned ? "\u25a1" : "\u25a0";
    /// <summary>Color for the compass float toggle: gold when docked, dim grey when floating.</summary>
    public Color  MapPinColor => _isMapPinned ? Color.FromArgb("#FFD700") : Color.FromArgb("#555555");

    /// <summary>True when the compass should render in the floating panel (undocked).</summary>
    public bool IsFloatingMapVisible => !_isMapPinned;
    /// <summary>True when the compass should render docked in the side rail (expanded and pinned).</summary>
    public bool IsDockedCompassVisible => _isMapExpanded && _isMapPinned;

    /// <summary>True when the floating compass is folded to its title bar only.</summary>
    public bool IsFloatingMapFolded
    {
        get => _isFloatingMapFolded;
        set => SetAndNotify(ref _isFloatingMapFolded, value, [nameof(FloatingMapFoldGlyph)]);
    }
    public string FloatingMapFoldGlyph => _isFloatingMapFolded ? "\u25b6" : "\u25bc";

    /// <summary>When true the floating compass is locked: content only, no title strip, no drag \u2014
    /// just the dial with a small corner lock icon. Its controls live in the side rail anyway.
    /// Unlocking reveals the strip and enables dragging.</summary>
    public bool IsFloatingMapLocked
    {
        get => _isFloatingMapLocked;
        set => SetAndNotify(ref _isFloatingMapLocked, value,
            [nameof(IsFloatingMapUnlocked), nameof(FloatingMapLockGlyph)]);
    }
    /// <summary>Convenience inverse \u2014 binds the title strip's visibility (shown while unlocked).</summary>
    public bool IsFloatingMapUnlocked => !_isFloatingMapLocked;
    /// <summary>Padlock glyph: \ud83d\udd12 locked, \ud83d\udd13 unlocked (drag-enabled).</summary>
    public string FloatingMapLockGlyph => _isFloatingMapLocked ? "\U0001F512" : "\U0001F513";

    // \u2500\u2500 Floating-panel size steps (the \u2212 / + buttons step through these) \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
    private static readonly double[] OnlineWidths = { 160, 190, 220 };
    private int _onlineSizeIx = 2;
    /// <summary>Current width of the floating online panel; stepped by the \u2212 / + buttons.</summary>
    public double FloatingOnlineWidth => OnlineWidths[_onlineSizeIx];

    // Largest \u2192 smallest. The final step is a horizontal oval (12px shorter than wide)
    // for the most compact phone-float footprint.
    private static readonly (double W, double H)[] MapSizes =
        { (128, 128), (104, 104), (84, 84), (84, 66) };
    private int _mapSizeIx = 1;
    /// <summary>Current width of the floating compass; stepped by the \u2212 / + buttons.</summary>
    public double FloatingMapWidth  => MapSizes[_mapSizeIx].W;
    /// <summary>Current height of the floating compass (shorter than width at the oval step).</summary>
    public double FloatingMapHeight => MapSizes[_mapSizeIx].H;

    // ── Floating online panel state ────────────────────────────────────────────

    /// <summary>When true (and the side panel is hidden), a floating online-list panel is shown.</summary>
    public bool IsOnlinePinned
    {
        get => _isOnlinePinned;
        set => SetAndNotify(ref _isOnlinePinned, value,
            [nameof(IsFloatingOnlineVisible), nameof(IsOnlineSectionVisible), nameof(PinGlyph), nameof(PinColor)]);
    }

    // \u25CF = ● (filled circle)  \u25CB = ○ (hollow circle)
    // These are regular text glyphs that obey TextColor — unlike emoji which ignore it.
    /// <summary>Glyph for the dock toggle \u2014 shows the action, not the state:
    /// hollow "float me" square while docked, filled "dock me" square while floating.</summary>
    public string PinGlyph => _isOnlinePinned ? "\u25A1" : "\u25A0";
    /// <summary>Color for the pin toggle: gold when docked, dim grey when floating.</summary>
    public Color  PinColor  => _isOnlinePinned
        ? Color.FromArgb("#FFD700")
        : Color.FromArgb("#555555");

    /// <summary>True when the floating panel should be rendered (online is unpinned from side panel).</summary>
    public bool IsFloatingOnlineVisible => !_isOnlinePinned;

    /// <summary>True when the online section should appear in the side panel (pinned).</summary>
    public bool IsOnlineSectionVisible => _isOnlinePinned;

    /// <summary>True when the floating panel is folded to title-bar only.</summary>
    public bool IsFloatingOnlineFolded
    {
        get => _isFloatingOnlineFolded;
        set => SetAndNotify(ref _isFloatingOnlineFolded, value, [nameof(FloatingFoldGlyph)]);
    }

    /// <summary>Fold glyph for the floating panel (same convention as side-panel sections).</summary>
    public string FloatingFoldGlyph => _isFloatingOnlineFolded ? "\u25b6" : "\u25bc";

    /// <summary>When true the floating online windlet is locked: content only, no title strip, no
    /// drag \u2014 just the list with a small corner lock icon. Its controls live in the side rail
    /// anyway. Unlocking reveals the strip and enables dragging.</summary>
    public bool IsFloatingOnlineLocked
    {
        get => _isFloatingOnlineLocked;
        set => SetAndNotify(ref _isFloatingOnlineLocked, value,
            [nameof(IsFloatingOnlineUnlocked), nameof(FloatingOnlineLockGlyph)]);
    }
    /// <summary>Convenience inverse \u2014 binds the title strip's visibility (shown while unlocked).</summary>
    public bool IsFloatingOnlineUnlocked => !_isFloatingOnlineLocked;
    /// <summary>Padlock glyph: \ud83d\udd12 locked, \ud83d\udd13 unlocked (drag-enabled).</summary>
    public string FloatingOnlineLockGlyph => _isFloatingOnlineLocked ? "\U0001F512" : "\U0001F513";

    /// <summary>True only when names-only display mode is active: the title/level suffix is hidden.</summary>
    public bool NamesOnly
    {
        get => _namesOnly;
        set
        {
            if (!Set(ref _namesOnly, value)) return;
            WhoEntry.NamesOnlyMode = value;
            foreach (var e in WhosList) e.NotifyDisplaySuffixChanged();
        }
    }

    /// <summary>Maximum entries shown in the who-list; 0 = unlimited.</summary>
    public int MaxOnline
    {
        get => _maxOnline;
        set => Set(ref _maxOnline, value);
    }

    /// <summary>Count of non-departing online players.</summary>
    public int WhoCount { get; private set; }

    /// <summary>Formatted count for the Online section heading, e.g. " (3)".</summary>
    public string OnlineCountText => $" ({WhoCount})";

    /// <summary>Raised when the user taps the hamburger in the floating panel — opens settings/display.</summary>
    public event Action? FloatingOpenDisplaySettings;

    /// <summary>Raised (on the UI thread) when FEW/FEI subscription needs updating.
    /// Payload: (includeFew, includeFei).</summary>
    public event Action<bool, bool>? SubscriptionOptionsChanged;

    private void RaiseSubscriptionChanged()
        => SubscriptionOptionsChanged?.Invoke(_isOnlineExpanded, _isInventoryExpanded || _isItemsHereExpanded);

    /// <summary>True while the About dialog overlay is shown (opened via the ⓘ status-bar icon).</summary>
    public bool IsAboutVisible
    {
        get => _isAboutVisible;
        set => Set(ref _isAboutVisible, value);
    }

    public string CurrentRoom
    {
        get => _currentRoom;
        private set => SetAndNotify(ref _currentRoom, value, [nameof(HasCurrentRoom), nameof(NoCurrentRoom)]);
    }
    public bool HasCurrentRoom => !string.IsNullOrEmpty(_currentRoom);
    public bool NoCurrentRoom  => string.IsNullOrEmpty(_currentRoom);

    public string PreviousRoom { get => _previousRoom; private set => Set(ref _previousRoom, value); }
    public string OldestRoom   { get => _oldestRoom;   private set => Set(ref _oldestRoom,   value); }

    public string AppVersion => AppInfo.VersionString;

    // ── WHO list ──────────────────────────────────────────────────────────────
    public ObservableCollection<WhoEntry> WhosList { get; } = new();
    private readonly List<WhoEntry> _pendingWhos = new();

    // ── Room exits (FEX) ─────────────────────────────────────────────────────
    public ExitIndicator ExitNorth     { get; } = new();
    public ExitIndicator ExitSouth     { get; } = new();
    public ExitIndicator ExitEast      { get; } = new();
    public ExitIndicator ExitWest      { get; } = new();
    public ExitIndicator ExitNorthEast { get; } = new();
    public ExitIndicator ExitNorthWest { get; } = new();
    public ExitIndicator ExitSouthEast { get; } = new();
    public ExitIndicator ExitSouthWest { get; } = new();
    public ExitIndicator ExitUp        { get; } = new();
    public ExitIndicator ExitDown      { get; } = new();
    public ExitIndicator ExitIn        { get; } = new();
    public ExitIndicator ExitOut       { get; } = new();
    public ExitIndicator ExitSwampward { get; } = new();

    private readonly List<string> _pendingExits = new();

    // ── Inventory / room items ────────────────────────────────────────────────
    public ObservableCollection<string> InventoryList { get; } = new();
    public ObservableCollection<string> RoomItemsList { get; } = new();
    private readonly List<string> _pendingInventory = new();
    private readonly List<string> _pendingRoomItems = new();
    private bool _feiPastSeparator;

    public bool HasInventory  => InventoryList.Count  > 0;
    public bool HasRoomItems  => RoomItemsList.Count  > 0;
    public bool NoInventory   => InventoryList.Count  == 0;
    public bool NoRoomItems   => RoomItemsList.Count  == 0;

    // ── Stale-fade signals ─────────────────────────────────────────────────────
    // Raised (on the UI thread) when a list type is fully refreshed. StaleDimBehavior listens and
    // (re)starts a COMPOSITOR opacity animation on the section: hold full-bright for 15 s, then
    // ease to 70% — entirely on the render thread, so it never touches typing. (Was a UI-thread
    // fade timer recomputing opacity 10×/sec — the typing-lag culprit — now deleted.)
    /// <summary>Fired when the Here/Carrying (FEI) lists are refreshed.</summary>
    public event Action? FeiRefreshed;
    /// <summary>Fired when the Online (FEW) list is refreshed.</summary>
    public event Action? FewRefreshed;

    public ICommand TogglePanelCommand { get; }
    public ICommand ShowAboutCommand { get; }
    public ICommand CloseAboutCommand { get; }
    public ICommand OpenLinkCommand { get; }
    public ICommand ToggleOnlineCommand { get; }
    public ICommand ToggleInventoryCommand { get; }
    public ICommand ToggleItemsHereCommand { get; }
    public ICommand ToggleMapCommand { get; }
    public ICommand ToggleOnlinePinnedCommand { get; }
    public ICommand ToggleFloatingFoldCommand { get; }
    public ICommand OpenFloatingDisplaySettingsCommand { get; }
    public ICommand ToggleMapPinnedCommand { get; }
    public ICommand ToggleFloatingMapFoldCommand { get; }
    public ICommand IncreaseOnlineSizeCommand { get; }
    public ICommand DecreaseOnlineSizeCommand { get; }
    public ICommand IncreaseMapSizeCommand { get; }
    public ICommand DecreaseMapSizeCommand { get; }
    public ICommand ToggleFloatingOnlineLockCommand { get; }
    public ICommand ToggleFloatingMapLockCommand { get; }

    /// <summary>Raised when an interaction should hand keyboard focus back to the input box.
    /// Opening the About dialog deliberately does not raise it — focus belongs to the dialog.</summary>
    public event Action? RequestFocus;

    public SidePanelViewModel()
    {
        TogglePanelCommand = new Command(() => { IsPanelExpanded = !IsPanelExpanded; RequestFocus?.Invoke(); });
        ShowAboutCommand  = new Command(() => IsAboutVisible = true);
        CloseAboutCommand = new Command(() => { IsAboutVisible = false; RequestFocus?.Invoke(); });
        OpenLinkCommand = new Command<string>(url =>
        {
            if (!string.IsNullOrWhiteSpace(url))
                _ = Launcher.OpenAsync(new Uri(url));
        });
        ToggleOnlineCommand    = new Command(() => IsOnlineExpanded    = !IsOnlineExpanded);
        ToggleInventoryCommand = new Command(() => IsInventoryExpanded = !IsInventoryExpanded);
        ToggleItemsHereCommand = new Command(() => IsItemsHereExpanded = !IsItemsHereExpanded);
        ToggleMapCommand       = new Command(() => IsMapExpanded       = !IsMapExpanded);
        ToggleOnlinePinnedCommand = new Command(() => { IsOnlinePinned = !IsOnlinePinned; RequestFocus?.Invoke(); });
        ToggleFloatingFoldCommand = new Command(() => IsFloatingOnlineFolded = !IsFloatingOnlineFolded);
        OpenFloatingDisplaySettingsCommand = new Command(() => FloatingOpenDisplaySettings?.Invoke());
        ToggleMapPinnedCommand = new Command(() => { IsMapPinned = !IsMapPinned; RequestFocus?.Invoke(); });
        ToggleFloatingMapFoldCommand = new Command(() => IsFloatingMapFolded = !IsFloatingMapFolded);
        IncreaseOnlineSizeCommand = new Command(() =>
        {
            if (_onlineSizeIx >= OnlineWidths.Length - 1) return;
            _onlineSizeIx++;
            OnPropertyChanged(nameof(FloatingOnlineWidth));
        });
        DecreaseOnlineSizeCommand = new Command(() =>
        {
            if (_onlineSizeIx <= 0) return;
            _onlineSizeIx--;
            OnPropertyChanged(nameof(FloatingOnlineWidth));
        });
        // MapSizes runs largest → smallest, so "increase" walks the index down.
        IncreaseMapSizeCommand = new Command(() =>
        {
            if (_mapSizeIx <= 0) return;
            _mapSizeIx--;
            OnPropertiesChanged(nameof(FloatingMapWidth), nameof(FloatingMapHeight));
        });
        DecreaseMapSizeCommand = new Command(() =>
        {
            if (_mapSizeIx >= MapSizes.Length - 1) return;
            _mapSizeIx++;
            OnPropertiesChanged(nameof(FloatingMapWidth), nameof(FloatingMapHeight));
        });
        ToggleFloatingOnlineLockCommand = new Command(() => { IsFloatingOnlineLocked = !IsFloatingOnlineLocked; RequestFocus?.Invoke(); });
        ToggleFloatingMapLockCommand    = new Command(() => { IsFloatingMapLocked    = !IsFloatingMapLocked;    RequestFocus?.Invoke(); });
        WhosList.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null)
                foreach (WhoEntry item in e.NewItems)
                    item.PropertyChanged += OnWhoEntryPropertyChanged;
            if (e.OldItems is not null)
                foreach (WhoEntry item in e.OldItems)
                    item.PropertyChanged -= OnWhoEntryPropertyChanged;
            WhoCount = WhosList.Count(w => !w.IsDeparting);
            OnPropertiesChanged(nameof(WhoCount), nameof(OnlineCountText));
        };
    }

    // UI-thread dispatcher, captured from the host. Used only for one-shot DispatchDelayed calls
    // that remove a who-entry AFTER its GPU fade-out finishes (see OnFewListComplete) — NOT a
    // repeating animation tick. The old 100 ms (10 Hz) UI-thread fade timer was the typing-lag
    // culprit and is gone; all visual fading now runs on the compositor via behaviors.
    private IDispatcher? _dispatcher;

    /// <summary>Captures the UI-thread dispatcher (call once on the UI thread after game-mode is
    /// entered). Named for call-site compatibility — it no longer starts any timer.</summary>
    public void InitializeFadeTimer(IDispatcher dispatcher) => _dispatcher = dispatcher;

    // ── Room name ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Called on the TCP read thread when the player has entered (or can now see) a room.
    /// Clears the "Here" (room items) list. InventoryList is intentionally preserved —
    /// carried items do not change just because the room changes.
    ///
    /// Exits are NOT cleared here. RoomEntered fires on the room-short at frame start for
    /// both a movement ("visit") and a bare 'look' ("view"); only a movement frame carries
    /// the embedded FEX exits block (C12+C08+C02), which fully refreshes the exit set via
    /// <see cref="OnFexListComplete"/>. Clearing exits on every room short wiped them on a
    /// 'look', which sends no FEX, leaving the compass blank until the next movement.
    /// </summary>
    public void OnRoomEntered()
        => MainThread.BeginInvokeOnMainThread(() =>
        {
            RoomItemsList.Clear();
            OnPropertiesChanged(nameof(HasRoomItems), nameof(NoRoomItems));
        });

    /// <summary>
    /// Called on the TCP read thread when a room-short line arrives at line start.
    /// Pushes the current room into history only when the name differs from the current room,
    /// suppressing history bumps for repeated looks at the same room.
    /// </summary>
    public void OnRoomNameReady(string name)
        => MainThread.BeginInvokeOnMainThread(() =>
        {
            if (name != _currentRoom && !string.IsNullOrEmpty(_currentRoom))
            {
                OldestRoom   = PreviousRoom;
                PreviousRoom = _currentRoom;
            }
            CurrentRoom = name;
        });

    /// <summary>
    /// Called when the player exits game mode (e.g. types 'qq').
    /// Sets CurrentRoom to "Option Menu" so the side panel reflects the player's new location,
    /// and clears the compass — the option menu is not a room, so any exits are stale.
    /// Does not push history — history shifts on the next real room entry.
    /// </summary>
    public void OnGameModeExited()
        => MainThread.BeginInvokeOnMainThread(() =>
        {
            CurrentRoom = "Option Menu";
            SetAllExitsPresent(false);
        });

    // ── WHO list (FEW) ────────────────────────────────────────────────────────

    /// <summary>
    /// Called when the parser opens a FEW-response context (C12+C08+C05).
    /// Clears the accumulation buffer; WhosList is not touched until the response is complete.
    /// Fires on the TCP read thread — no marshal needed (_pendingWhos is read-loop-only).
    /// </summary>
    public void OnFewListStarting()
        => _pendingWhos.Clear();

    /// <summary>
    /// Called from the TCP read thread for each player name in the FEW response.
    /// The AnsiColor carries the wire-protocol c (e.g. RED = mortal, LT_RED = wizard).
    /// </summary>
    public void OnFewPlayerReceived(string playerName, AnsiColor color)
        => _pendingWhos.Add(new WhoEntry(playerName, AnsiPalette.GetFg((byte)color)));

    /// <summary>
    /// Called when the FEW-response context closes — all names have been delivered.
    /// Diffs the incoming snapshot against the current WhosList:
    ///   • Players no longer in the snapshot are marked departing and fade out over 4 s.
    ///   • Players that reappear before their fade completes have their departure cancelled.
    ///   • New arrivals are appended with a white→color glow over 4 s.
    ///   • Players whose name or color changed (e.g. level-up) are updated in-place with a glow.
    ///   • A visibility change ("Ollie the sorcerer" ⇄ "(Ollie the sorcerer)") is a status
    ///     change, not a rename: WhoEntry.PersonaName ignores the invisibility parens, so the
    ///     entry updates in-place (with glow) instead of fading out and back in.
    /// </summary>
    public void OnFewListComplete()
    {
        var snapshot = _pendingWhos.ToList();
        _pendingWhos.Clear();
        MainThread.BeginInvokeOnMainThread(() =>
        {
            // Key by persona name (first word) so a level-up — which changes the
            // description suffix — is treated as the same player, not a departure + arrival.
            var newByPersona = snapshot.ToDictionary(
                w => w.PersonaName, StringComparer.OrdinalIgnoreCase);

            // Update returnees in place; fade departed players out (the WhoEntryFadeBehavior
            // animates on IsDeparting), then remove them once the GPU fade has finished.
            for (int i = WhosList.Count - 1; i >= 0; i--)
            {
                var existing = WhosList[i];
                if (newByPersona.TryGetValue(existing.PersonaName, out var updated))
                {
                    existing.IsDeparting = false;   // present again — cancel any pending fade-out + removal
                    if (existing.Name  != updated.Name)  existing.Name  = updated.Name;
                    if (existing.Color != updated.Color) existing.Color = updated.Color;
                }
                else if (!existing.IsDeparting)
                {
                    existing.IsDeparting = true;   // gone — behavior fades it out
                    var leaving = existing;
                    // Remove after the 3 s fade-out (plus a little slack). If the player reappears
                    // first, the returnee branch clears IsDeparting and this skips the removal.
                    _dispatcher?.DispatchDelayed(TimeSpan.FromMilliseconds(3400), () =>
                    {
                        if (leaving.IsDeparting) WhosList.Remove(leaving);
                    });
                }
            }

            // Append new arrivals (not already in the list).
            var currentPersonas = new HashSet<string>(
                WhosList.Select(w => w.PersonaName), StringComparer.OrdinalIgnoreCase);
            foreach (var entry in snapshot)
            {
                if (!currentPersonas.Contains(entry.PersonaName))
                    WhosList.Add(entry);   // appears instantly
            }

            FewRefreshed?.Invoke();   // restart the section's compositor stale-dim

            // Trim to MaxOnline if set (remove oldest displayed entries).
            if (_maxOnline > 0)
            {
                while (WhosList.Count(w => !w.IsDeparting) > _maxOnline)
                {
                    // Remove the first non-departing entry beyond the cap.
                    var excess = WhosList.FirstOrDefault(w => !w.IsDeparting);
                    if (excess == null) break;
                    WhosList.Remove(excess);
                }
            }
        });
    }

    // ── Inventory / room items (FEI) ──────────────────────────────────────────

    /// <summary>Called when the FEI context opens. Clears pending buffers.</summary>
    public void OnFeiListStarting()
    {
        _pendingRoomItems.Clear();
        _pendingInventory.Clear();
        _feiPastSeparator = false;
    }

    /// <summary>
    /// Called for each item line in the FEI response.
    /// "========" is the separator: items before it are in the room; items after are carried.
    /// </summary>
    public void OnFeiItemReady(string item)
    {
        if (item == "========")
            _feiPastSeparator = true;
        else if (_feiPastSeparator)
            _pendingInventory.Add(item);
        else
            _pendingRoomItems.Add(item);
    }

    /// <summary>
    /// Called when the FEI context closes. Atomically replaces RoomItemsList and InventoryList on the UI thread.
    /// </summary>
    public void OnFeiListComplete()
    {
        var snapRoom = _pendingRoomItems.ToList();
        var snapInv  = _pendingInventory.ToList();
        _pendingRoomItems.Clear();
        _pendingInventory.Clear();
        MainThread.BeginInvokeOnMainThread(() =>
        {
            // FEI arrives every heartbeat and is usually unchanged; Clear+Add re-templates
            // every native label in both lists (UI-thread work competing with typing), so
            // skip the rebuild when nothing changed. The stale-dim restart still fires.
            if (!snapRoom.SequenceEqual(RoomItemsList) || !snapInv.SequenceEqual(InventoryList))
            {
                RoomItemsList.Clear();
                foreach (var item in snapRoom)
                    RoomItemsList.Add(item);

                InventoryList.Clear();
                foreach (var item in snapInv)
                    InventoryList.Add(item);

                OnPropertiesChanged(
                    nameof(HasRoomItems), nameof(NoRoomItems),
                    nameof(HasInventory), nameof(NoInventory));
            }

            FeiRefreshed?.Invoke();   // restart the section's compositor stale-dim
        });
    }

    // ── Room exits (FEX) ──────────────────────────────────────────────────────

    public void OnFexListStarting()
        => _pendingExits.Clear();

    public void OnFexItemReady(string item)
    {
        foreach (var keyword in item.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            _pendingExits.Add(keyword);
    }

    public void OnFexListComplete()
    {
        var snapshot = _pendingExits.ToList();
        _pendingExits.Clear();
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var exits = new HashSet<string>(snapshot, StringComparer.OrdinalIgnoreCase);
            ExitNorth.Present     = exits.Contains("north");
            ExitSouth.Present     = exits.Contains("south");
            ExitEast.Present      = exits.Contains("east");
            ExitWest.Present      = exits.Contains("west");
            ExitNorthEast.Present = exits.Contains("northeast");
            ExitNorthWest.Present = exits.Contains("northwest");
            ExitSouthEast.Present = exits.Contains("southeast");
            ExitSouthWest.Present = exits.Contains("southwest");
            ExitUp.Present        = exits.Contains("up");
            ExitDown.Present      = exits.Contains("down");
            ExitIn.Present        = exits.Contains("in");
            ExitOut.Present       = exits.Contains("out");
            ExitSwampward.Present = exits.Contains("swampward");
        });
    }

    private void SetAllExitsPresent(bool value)
    {
        ExitNorth.Present     = value;
        ExitSouth.Present     = value;
        ExitEast.Present      = value;
        ExitWest.Present      = value;
        ExitNorthEast.Present = value;
        ExitNorthWest.Present = value;
        ExitSouthEast.Present = value;
        ExitSouthWest.Present = value;
        ExitUp.Present        = value;
        ExitDown.Present      = value;
        ExitIn.Present        = value;
        ExitOut.Present       = value;
        ExitSwampward.Present = value;
    }

    private void OnWhoEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WhoEntry.IsDeparting))
            return;

        WhoCount = WhosList.Count(w => !w.IsDeparting);
        OnPropertiesChanged(nameof(WhoCount), nameof(OnlineCountText));
    }

    public void Dispose()
    {
        // No resources to release — the who-list/stale-fade animation timer was removed
        // (it was the UI-thread typing-lag culprit). Kept for the IDisposable contract.
    }
}
