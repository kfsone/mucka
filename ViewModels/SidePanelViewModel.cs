using MudSharp.Models;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace Mucka.ViewModels;

public sealed class SidePanelViewModel : BaseViewModel, IDisposable
{
    private bool _isPanelExpanded;
    private int _activeTab;
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

    public int ActiveTab
    {
        get => _activeTab;
        set => SetAndNotify(ref _activeTab, value,
            [nameof(IsExtrasTab), nameof(IsAboutTab)]);
    }
    public bool IsExtrasTab => _activeTab == 0;
    public bool IsAboutTab  => _activeTab == 1;

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
    public ICommand SetTabCommand { get; }
    public ICommand OpenLinkCommand { get; }

    public SidePanelViewModel()
    {
        TogglePanelCommand = new Command(() => IsPanelExpanded = !IsPanelExpanded);
        SetTabCommand = new Command<string>(s =>
        {
            if (int.TryParse(s, out var tab))
                ActiveTab = tab;
        });
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

        // Advance player departure animations; iterate in reverse so removal by index is safe.
        const double departureDurationSec = 2.5;
        for (int i = WhosList.Count - 1; i >= 0; i--)
        {
            var entry = WhosList[i];
            if (entry.DepartingSince is not { } since) continue;

            double elapsed = (now - since).TotalSeconds;
            if (elapsed >= departureDurationSec)
                WhosList.RemoveAt(i);
            else
                entry.Opacity = 1.0 - elapsed / departureDurationSec;
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
    /// </summary>
    public void OnRoomEntered()
        => MainThread.BeginInvokeOnMainThread(() =>
        {
            RoomItemsList.Clear();
            OnPropertiesChanged(nameof(HasRoomItems), nameof(NoRoomItems));
        });

    /// <summary>
    /// Called on the TCP read thread when a room-short line arrives at line start.
    /// Pushes the current room into history (if non-empty) and updates CurrentRoom.
    /// </summary>
    public void OnRoomNameReady(string name)
        => MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!string.IsNullOrEmpty(_currentRoom))
            {
                OldestRoom   = PreviousRoom;
                PreviousRoom = _currentRoom;
            }
            CurrentRoom = name;
        });

    /// <summary>
    /// Called when the player exits game mode (e.g. types 'qq').
    /// Sets CurrentRoom to "Option Menu" so the Extras tab reflects the player's new location.
    /// Does not push history — history shifts on the next real room entry.
    /// </summary>
    public void OnGameModeExited()
        => MainThread.BeginInvokeOnMainThread(() => CurrentRoom = "Option Menu");

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
    ///   • Players no longer in the snapshot are marked departing and fade out over 2.5 s.
    ///   • Players that reappear before their fade completes have their departure cancelled.
    ///   • New arrivals are appended.
    /// </summary>
    public void OnFewListComplete()
    {
        var snapshot = _pendingWhos.ToList();
        _pendingWhos.Clear();
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _fewLastRefresh = DateTime.UtcNow;

            var newNames = new HashSet<string>(
                snapshot.Select(w => w.Name), StringComparer.OrdinalIgnoreCase);

            // Mark departing players; cancel departure for players that reappeared.
            foreach (var existing in WhosList)
            {
                if (newNames.Contains(existing.Name))
                {
                    if (existing.DepartingSince is not null)
                    {
                        existing.DepartingSince = null;
                        existing.Opacity = 1.0;
                    }
                }
                else if (existing.DepartingSince is null)
                {
                    existing.DepartingSince = DateTime.UtcNow;
                }
            }

            // Append new arrivals (not already in the list, including after a full-removal cycle).
            var currentNames = new HashSet<string>(
                WhosList.Select(w => w.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var entry in snapshot)
            {
                if (!currentNames.Contains(entry.Name))
                    WhosList.Add(entry);
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

    public void Dispose()
    {
        if (_fadeTimer is null) return;
        _fadeTimer.Stop();
        _fadeTimer.Tick -= OnFadeTick;
        _fadeTimer = null;
    }
}


