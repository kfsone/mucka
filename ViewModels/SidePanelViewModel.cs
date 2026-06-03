using MudSharp.Models;
using System.Collections.ObjectModel;
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
    private string _characterName = "";
    private string _currentRoom  = "";
    private string _previousRoom = "Option Menu";
    private string _oldestRoom   = "Logging in";

    public bool IsPanelExpanded
    {
        get => _isPanelExpanded;
        set => SetAndNotify(ref _isPanelExpanded, value,
            [nameof(IsPanelCollapsed), nameof(PanelToggleGlyph)]);
    }
    public bool IsPanelCollapsed => !_isPanelExpanded;
    // ▼ when collapsed (click to show panel), ▶ when expanded (click to hide panel)
    public string PanelToggleGlyph => _isPanelExpanded ? "▶" : "▼";

    /// <summary>True while the About dialog overlay is shown (opened via the ⓘ status-bar icon).</summary>
    public bool IsAboutVisible
    {
        get => _isAboutVisible;
        set => Set(ref _isAboutVisible, value);
    }

    public string CharacterName
    {
        get => _characterName;
        set => SetAndNotify(ref _characterName, value, [nameof(HasCharacterName)]);
    }
    public bool HasCharacterName => !string.IsNullOrEmpty(_characterName);

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

    // ── Stale-fade state ──────────────────────────────────────────────────────
    // Last time each list type was fully refreshed (set on UI thread; null = never refreshed).
    private DateTime? _feiLastRefresh;
    private DateTime? _fewLastRefresh;

    private double _feiSectionOpacity = 1.0;
    private double _fewSectionOpacity = 1.0;

    /// <summary>Opacity of the Here / Carrying section; fades to 0.7 when FEI data is stale (>15 s).</summary>
    public double FeiSectionOpacity { get => _feiSectionOpacity; private set => Set(ref _feiSectionOpacity, value); }

    /// <summary>Opacity of the Online section; fades to 0.7 when FEW data is stale (>15 s).</summary>
    public double FewSectionOpacity { get => _fewSectionOpacity; private set => Set(ref _fewSectionOpacity, value); }

    private IDispatcherTimer? _fadeTimer;

    public ICommand TogglePanelCommand { get; }
    public ICommand ShowAboutCommand { get; }
    public ICommand CloseAboutCommand { get; }
    public ICommand OpenLinkCommand { get; }

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
    }

    /// <summary>
    /// Creates and starts the UI-thread fade timer.  Must be called once from the UI thread
    /// after game-mode is entered (mirrors the lifecycle of the GamePage flush timer).
    /// </summary>
    public void InitializeFadeTimer(IDispatcher dispatcher)
    {
        if (_fadeTimer is not null) return;
        _fadeTimer = dispatcher.CreateTimer();
        _fadeTimer.Interval = TimeSpan.FromMilliseconds(100);
        _fadeTimer.Tick += OnFadeTick;
        _fadeTimer.Start();
    }

    private void OnFadeTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;

        FeiSectionOpacity = ComputeStaleOpacity(_feiLastRefresh, now);
        FewSectionOpacity = ComputeStaleOpacity(_fewLastRefresh, now);

        const double transitionSec = 4.0;

        // Advance player departure fades and arrival/update glows.
        // Iterate in reverse so removal by index is safe.
        for (int i = WhosList.Count - 1; i >= 0; i--)
        {
            var entry = WhosList[i];

            if (entry.DepartingSince is { } since)
            {
                double elapsed = (now - since).TotalSeconds;
                if (elapsed >= transitionSec)
                    WhosList.RemoveAt(i);
                else
                    entry.Opacity = 1.0 - elapsed / transitionSec;
            }
            else if (entry.GlowSince is { } glowSince)
            {
                double elapsed = (now - glowSince).TotalSeconds;
                if (elapsed >= transitionSec)
                {
                    entry.GlowSince = null;
                    entry.SetGlowProgress(1.0f);
                }
                else
                    entry.SetGlowProgress((float)(elapsed / transitionSec));
            }
        }
    }

    /// <summary>
    /// Computes the stale-fade opacity for a section.
    /// Returns 1.0 until 15 s after the last refresh, then linearly fades to 0.7 over the next 5 s.
    /// Returns 1.0 when the section has never been refreshed (no data yet — nothing to fade).
    /// </summary>
    private static double ComputeStaleOpacity(DateTime? lastRefresh, DateTime now)
    {
        if (lastRefresh is null) return 1.0;
        double elapsed = (now - lastRefresh.Value).TotalSeconds;
        if (elapsed <= 15.0) return 1.0;
        if (elapsed >= 20.0) return 0.7;
        return 1.0 - 0.3 * (elapsed - 15.0) / 5.0;
    }

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
    /// </summary>
    public void OnFewListComplete()
    {
        var snapshot = _pendingWhos.ToList();
        _pendingWhos.Clear();
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _fewLastRefresh = DateTime.UtcNow;

            // Key by persona name (first word) so a level-up — which changes the
            // description suffix — is treated as the same player, not a departure + arrival.
            var newByPersona = snapshot.ToDictionary(
                w => w.PersonaName, StringComparer.OrdinalIgnoreCase);

            // Mark departing players; cancel departure and refresh display for returnees.
            foreach (var existing in WhosList)
            {
                if (newByPersona.TryGetValue(existing.PersonaName, out var updated))
                {
                    if (existing.DepartingSince is not null)
                    {
                        existing.DepartingSince = null;
                        existing.Opacity = 1.0;
                    }
                    // Reflect a level-up or colour change with a glow.
                    var nameChanged  = existing.Name  != updated.Name;
                    var colorChanged = existing.Color != updated.Color;
                    if (nameChanged)  existing.Name  = updated.Name;
                    if (colorChanged) existing.Color = updated.Color;
                    if (nameChanged || colorChanged)  existing.StartGlow();
                }
                else if (existing.DepartingSince is null)
                {
                    existing.DepartingSince = DateTime.UtcNow;
                }
            }

            // Append new arrivals (not already in the list, including after a full-removal cycle).
            var currentPersonas = new HashSet<string>(
                WhosList.Select(w => w.PersonaName), StringComparer.OrdinalIgnoreCase);
            foreach (var entry in snapshot)
            {
                if (!currentPersonas.Contains(entry.PersonaName))
                {
                    entry.StartGlow();
                    WhosList.Add(entry);
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
            _feiLastRefresh = DateTime.UtcNow;

            RoomItemsList.Clear();
            foreach (var item in snapRoom)
                RoomItemsList.Add(item);

            InventoryList.Clear();
            foreach (var item in snapInv)
                InventoryList.Add(item);

            OnPropertiesChanged(
                nameof(HasRoomItems), nameof(NoRoomItems),
                nameof(HasInventory), nameof(NoInventory));
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

    public void Dispose()
    {
        if (_fadeTimer is null) return;
        _fadeTimer.Stop();
        _fadeTimer.Tick -= OnFadeTick;
        _fadeTimer = null;
    }
}


