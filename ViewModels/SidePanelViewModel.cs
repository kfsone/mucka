using Microsoft.Maui.Graphics;
using Mucka.Core;
using MudSharp.Combat;
using MudSharp.Models;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
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
    // Each section heading has a [v]/[>] widget; folding is equivalent to disabling in settings.
    private bool _isOnlineExpanded   = true;
    private bool _isInventoryExpanded = true;
    private bool _isItemsHereExpanded = true;
    private bool _isMapExpanded      = true;
    private bool _isOnlinePinned = true;   // pinned (floating panel follows when side panel is hidden)
    private bool _isFloatingOnlineFolded;
    private bool _isFloatingOnlineLocked = true;   // windlets start locked: content only, no strip, no drag
    private bool _namesOnly;
    private int  _maxOnline;
    private int  _forgetWindowMinutes;
    // UTC time the last FEW response completed — the "last seen" baseline for players who drop
    // off it, and the gap used to low-clamp an overdue FEW's Recent lifetimes.
    private DateTime _lastFewCompleteUtc;
    // Source of truth for the Recent list (side-panel only). RecentGroups is the grouped view.
    private readonly List<WhoEntry> _recent = new();
    private string _recentSignature = "";

    public bool IsPanelExpanded
    {
        get => _isPanelExpanded;
        set => SetAndNotify(ref _isPanelExpanded, value,
            [nameof(IsPanelCollapsed), nameof(PanelToggleGlyph)]);
    }
    public bool IsPanelCollapsed => !_isPanelExpanded;
    // [v] when expanded (click to hide the left-edge panel), [>] when collapsed (click to show
    // panel) -- same fixed-width ASCII fold convention as the section glyphs below
    // (DESIGN_FINAL.md D12/4.5: a variable-width glyph mixed into fixed-width monospace
    // ASCII is the exact bug class that broke ClogPage's column alignment once already,
    // so the whole panel now shares one fold-glyph convention).
    public string PanelToggleGlyph => _isPanelExpanded ? "[v]" : "[>]";

    // ── Section fold/unfold ────────────────────────────────────────────────────
    // [v] = expanded (content visible), [>] = collapsed (content hidden). ASCII-only
    // per DESIGN_FINAL.md D12 -- these used to be triangle glyph escapes.
    public bool IsOnlineExpanded
    {
        get => _isOnlineExpanded;
        set
        {
            if (SetAndNotify(ref _isOnlineExpanded, value, [nameof(OnlineFoldGlyph)]))
                RaiseSubscriptionChanged();
        }
    }
    public string OnlineFoldGlyph => _isOnlineExpanded ? "[v]" : "[>]";

    public bool IsInventoryExpanded
    {
        get => _isInventoryExpanded;
        set
        {
            if (SetAndNotify(ref _isInventoryExpanded, value, [nameof(InventoryFoldGlyph)]))
                RaiseSubscriptionChanged();
        }
    }
    public string InventoryFoldGlyph => _isInventoryExpanded ? "[v]" : "[>]";

    public bool IsItemsHereExpanded
    {
        get => _isItemsHereExpanded;
        set
        {
            if (SetAndNotify(ref _isItemsHereExpanded, value, [nameof(ItemsHereFoldGlyph)]))
                RaiseSubscriptionChanged();
        }
    }
    public string ItemsHereFoldGlyph => _isItemsHereExpanded ? "[v]" : "[>]";

    public bool IsMapExpanded
    {
        get => _isMapExpanded;
        set => SetAndNotify(ref _isMapExpanded, value, [nameof(MapFoldGlyph), nameof(IsDockedCompassVisible)]);
    }
    public string MapFoldGlyph => _isMapExpanded ? "[v]" : "[>]";

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
    public string FloatingMapFoldGlyph => _isFloatingMapFolded ? "[>]" : "[v]";

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
    /// <summary>
    /// Outer width for the floating-compass windlet: the dial plus the panel's 8+8 horizontal
    /// padding. Bound to the Border's WidthRequest so the windlet hugs the dial. Without it,
    /// Android's stack layout lets the star-column heading row and the Fill'd swamp seam stretch
    /// the panel to the full screen width (Windows measures to content, so it looked fine there).
    /// </summary>
    public double FloatingMapPanelWidth => FloatingMapWidth + 16;

    // ── Buffs / status effects ─────────────────────────────────────────────────
    // Rendered in the status-bar effect cluster (see GamePage.xaml StatusBar). Per-slot active
    // flags drive each icon's IsVisible; tooltips carry the exact detected game line.
    private bool _strBuff, _strDebuff, _dexBuff, _dexDebuff, _staBuff, _staDebuff, _glow;
    private string? _strBuffTip, _strDebuffTip, _dexBuffTip, _dexDebuffTip, _staBuffTip, _staDebuffTip, _glowTip;
    private string? _deafTip, _blindTip, _dumbTip, _crippledTip;

    public bool StrengthBuff    => _strBuff;
    public bool StrengthDebuff  => _strDebuff;
    public bool DexterityBuff   => _dexBuff;
    public bool DexterityDebuff => _dexDebuff;
    public bool StaminaBuff     => _staBuff;
    public bool StaminaDebuff   => _staDebuff;
    public bool Glow            => _glow;

    // Vertical nudge (px) for the +/- overlap: icons sit vertically centred when only one sign is
    // active, and split ±1 apart only when both are present (the stacked look). TranslationY, so
    // it never affects layout. Buff sits behind (+1, down), debuff in front (−1, up).
    public double StaminaBuffDy    => (_staBuff && _staDebuff) ?  1 : 0;
    public double StaminaDebuffDy  => (_staBuff && _staDebuff) ? -1 : 0;
    public double StrengthBuffDy   => (_strBuff && _strDebuff) ?  1 : 0;
    public double StrengthDebuffDy => (_strBuff && _strDebuff) ? -1 : 0;
    public double DexterityBuffDy  => (_dexBuff && _dexDebuff) ?  1 : 0;
    public double DexterityDebuffDy=> (_dexBuff && _dexDebuff) ? -1 : 0;

    // Tooltips: the exact detected game line, with a hardcoded fallback when none was captured
    // (e.g. an affliction set by FES on login rather than by an observed spell).
    public string StrengthBuffTip    => _strBuffTip    ?? "Strengthened";
    public string StrengthDebuffTip  => _strDebuffTip  ?? "Weakened";
    public string DexterityBuffTip   => _dexBuffTip    ?? "More adroit";
    public string DexterityDebuffTip => _dexDebuffTip  ?? "Less adroit";
    public string StaminaBuffTip     => _staBuffTip    ?? "Fitter";
    public string StaminaDebuffTip   => _staDebuffTip  ?? "Less fit";
    public string GlowTip            => _glowTip       ?? "Glowing";
    public string DeafTip            => _deafTip       ?? "You are deaf";
    public string BlindTip           => _blindTip      ?? "You are blind";
    public string DumbTip            => _dumbTip       ?? "You are dumb";
    public string CrippledTip        => _crippledTip   ?? "You are crippled";

    // -- Combat / clogging indicator --------------------------------------------
    // Driven by MudSharp.Combat.CombatTracker via MuckaConnection.InCombatChanged. IsClogging
    // mirrors InCombat in v1 (a clog is recorded for the full duration of every detected
    // encounter) - kept as a separate property so the UI can later show "in combat but not
    // recording" if clog writing ever fails independently of detection.
    private readonly CombatStatsAggregator _combatStats = new();
    // Bounds how often the clog readout actually rebuilds/republishes (see ClogRenderGate). Combat
    // events and the FES heartbeat can fire many times a second in a pack fight; this collapses
    // that burst to a bounded rate while the existing 1 Hz tick (TickCombatDisplay) guarantees any
    // deferred ("dirty") state is still flushed within a second, and combat-ending transitions
    // render directly so a final state is never lost behind the throttle.
    private readonly ClogRenderGate _clogRenderGate = new();
    private bool _inCombat, _isClogging, _hasCombatData, _isCombatGrace;
    private int _combatClearGeneration;
    // -- Combat Rail: the new right-edge panel (DESIGN_FINAL.md D3/2.2, corrected) -----------
    // Show/hide only - driven by "$clog on"/"$clog off" (GameViewModel.SetClogEnabled) and
    // GamePage's window-resize handlers. Never true at startup: the panel is additive and must
    // never appear without an explicit toggle (D3's "window never resizes itself" rule).
    private bool _isCombatPanelVisible;
    // Hysteresis/tie-break state for the tier table (4.2/4.3/4.4) - one instance per encounter,
    // reset in OnInCombatChanged(inCombat: true) so a fresh fight always starts both bracket
    // latches armed.
    private readonly CombatTierResolver _tierResolver = new();
    // The plain-language "why is this going badly" line, as text. Deliberately NOT a styled-span
    // type: the canvas owns colour, and the view model owns wording.
    private string? _whyText;
    private CombatTier _pulseTier = CombatTier.None;

    /// <summary>Stamina at or below which the rail's glow keeps running even with no fight on -
    /// see the use in RefreshCombatSignals.</summary>
    private const int OutOfCombatVulnerableStamina = 25;
    private CombatTier _encumbranceTier = CombatTier.None;
    // The Combat Rail's LIVE hero section - threat indicator, opposition roster, survival numbers -
    // composed fresh each refresh (see RefreshCombatSignals). CombatLiveView.Idle until an encounter
    // exists at all, exactly like every other combat signal field in this file.
    private CombatLiveView _live = CombatLiveView.Idle;
    // Running session tally, so the window reports something between fights instead of blanking.
    private SessionCombatTotals _session = SessionCombatTotals.Empty;
    // Latest stats, kept so a refresh triggered by a combat event (not a stats event) can still
    // report the current load penalty.
    private CombatStatDeficits _combatDeficits = CombatStatDeficits.None;

    // Set once at startup (see AttachFightHistory). Null in unit/design contexts, in which case the
    // history block simply stays hidden.
    private FightHistoryStore? _fightHistory;
    // The historical lookup only changes when the target/weapon/encounter changes, so it is cached
    // instead of re-running on every combat event and every 1 Hz tick (Invariant #1). Extracted into
    // its own MAUI-independent class (CombatHistoryCache) so the self-comparison-exclusion invariant
    // is unit-testable - see that class's remarks for the full reasoning.
    private readonly CombatHistoryCache _historyCache = new();
    // Cached once so the per-carried-item weapon test costs no allocation on the refresh path. Reads
    // _fightHistory through the closure rather than capturing it, so attaching the store later (as
    // startup does) is picked up without rebuilding the delegate.
    private readonly Func<string, bool> _isKnownWeapon;

    public bool InCombat   => _inCombat;
    public bool IsClogging => _isClogging;
    /// <summary>True while the encounter is only lingering on the post-kill grace window (last
    /// tracked NPC already dead/gone) - bind the combat indicator's opacity to this so it dims
    /// instead of looking identical to an actively-ongoing fight.</summary>
    public bool IsCombatGracePeriod => _isCombatGrace;
    public double CombatIconOpacity => _isCombatGrace ? 0.4 : 1.0;
    public string CombatTip => _isCombatGrace
        ? "Combat winding down..."
        : _isClogging ? "In combat - recording a clog" : "In combat";
    public bool HasCombatData => _hasCombatData;
    public bool NoCombatData => !_hasCombatData;

    /// <summary>Whether the Combat Rail (the new right-edge panel) is shown. Toggled by "$clog
    /// on"/"$clog off" and, when it changes, by GamePage resizing the window by the panel's own
    /// width (DESIGN_FINAL.md D3/2.2) - never by anything else; this property does not change on
    /// combat start/end, only on an explicit toggle.</summary>
    public bool IsCombatPanelVisible
    {
        get => _isCombatPanelVisible;
        set => Set(ref _isCombatPanelVisible, value);
    }

    /// <summary>The plain-language "why is this going badly" line, or null when nothing in the
    /// priority table currently applies. Text only - the canvas decides how it looks.</summary>
    public string? WhyText => _whyText;

    /// <summary>
    /// The Combat Rail's whole frame state: threat, opposition roster, and the survival numbers
    /// behind both. The render surface composes its own layout from this and inherits layout from
    /// nowhere else - the direct fix for a canvas that previously drew a text formatter's
    /// pre-composed lines verbatim, so the signal that mattered most was never composed for the
    /// canvas at all.
    /// </summary>
    public CombatLiveView Live => _live;

    /// <summary>
    /// Whether the combat metronome clicks once per tick. **On by default** - the beat is the point,
    /// and a feature that has to be found and switched on every session is a feature nobody uses.
    /// Session-scoped: not yet persisted to mucka.ini, so switching it off lasts only until restart.
    /// </summary>
    public bool IsCombatMetronomeEnabled
    {
        get => _isCombatMetronomeEnabled;
        set => Set(ref _isCombatMetronomeEnabled, value);
    }
    private bool _isCombatMetronomeEnabled = true;

    /// <summary>Toggles the metronome and hands focus straight back to the command box. The
    /// focus hand-back is not optional - Invariant #0 - and is why this is a command on the view
    /// model rather than a click handled inside the canvas.</summary>
    public void ToggleCombatMetronome()
    {
        IsCombatMetronomeEnabled = !IsCombatMetronomeEnabled;
        RequestFocus?.Invoke();
    }

    /// <summary>The tier driving the Combat Rail's single shared Composition glow layer (4.2: "at
    /// most one T3 element at a time"). Only <see cref="CombatTier.T3"/> ever requests motion - the
    /// glow helper (PulseLayer) treats every other value as "stop".</summary>
    public CombatTier PulseTier => _pulseTier;

    /// <summary>Encumbrance tier for the load line's colour intensity (4.3: T1 below 75% of max
    /// effective strength, T2 below 50%). Computed unconditionally whenever there is anything on
    /// screen at all - carrying too much is worth flagging even during the post-fight grace window,
    /// not only while a fight is actively live.</summary>
    public CombatTier EncumbranceTier => _encumbranceTier;

    /// <summary>True once there is a finished encounter on screen worth dismissing. Never while the
    /// fight is live - clearing mid-fight would just refill on the next line.</summary>
    public bool CanClearCombatSummary => _hasCombatData && !_inCombat;

    /// <summary>Whether there is anything at all to render. Distinct from <see cref="HasCombatData"/>:
    /// with no encounter on screen the panel still shows the session's running totals, so "no
    /// encounter" and "nothing to show" are not the same thing.</summary>
    public bool HasClogContent => _hasCombatData || _session.HasAnything;
    public bool NoClogContent => !HasClogContent;

    /// <summary>Supplies the accumulated per-fight history the live figures are contrasted against.
    /// Call once at startup; the store loads itself off-thread (see MuckaConnection).</summary>
    public void AttachFightHistory(FightHistoryStore store) => _fightHistory = store;

    public void OnInCombatChanged(bool inCombat, bool isClogging)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (inCombat)
            {
                _combatStats.BeginEncounter(DateTime.UtcNow);
                _hasCombatData = true;
                // Phase is unknown until this encounter's first swing arrives - see TickPhaseUtc.
                _tickPhaseUtc = null;
                OnPropertyChanged(nameof(TickPhaseUtc));
                // Fresh encounter: both bracket hysteresis latches re-arm (4.2) - a crossing from a
                // PREVIOUS fight must never suppress this fight's own first crossing.
                _tierResolver.Reset();
            }
            else
            {
                _combatStats.EndEncounter();
                // Fold the finished encounter into the session tally BEFORE anything can clear it, so
                // dismissing the summary never costs the session totals.
                _session = _session.Accumulate(_combatStats.Snapshot(DateTime.UtcNow));
            }

            _inCombat = inCombat;
            _isClogging = isClogging;
            _isCombatGrace = false;   // a fresh Begin() always clears the tracker's own grace state
            // Unthrottled and direct: a combat start/end transition must never be swallowed by the
            // render gate, so it bypasses RequestRender and renders straight away, then tells the
            // gate a render just happened so its throttle window measures from THIS moment onward.
            var now = DateTime.UtcNow;
            RefreshCombatDisplay(now);
            _clogRenderGate.MarkRendered(now);
            OnPropertiesChanged(nameof(InCombat), nameof(IsClogging), nameof(IsCombatGracePeriod),
                nameof(CombatIconOpacity), nameof(CombatTip), nameof(CanClearCombatSummary));
        });
    }

    /// <summary>See <see cref="MudSharp.Combat.CombatTracker.GracePeriodChanged"/> - flips true
    /// once the last tracked NPC is dead/gone but the encounter is still open pending the
    /// post-kill grace window, so the indicator can dim rather than look fully "in combat".</summary>
    public void OnCombatGracePeriodChanged(bool isGrace)
        => MainThread.BeginInvokeOnMainThread(() =>
        {
            _isCombatGrace = isGrace;
            OnPropertiesChanged(nameof(IsCombatGracePeriod), nameof(CombatIconOpacity), nameof(CombatTip));
        });

    /// <summary>
    /// When the current encounter's FIRST swing landed - the tick lattice's phase, and the anchor the
    /// tick bar and metronome align to.
    ///
    /// <para>Not the moment combat started. The line that flips InCombat is the reply to the player's
    /// own <c>kill</c> command, so its phase is the keystroke's rather than the server's: measured
    /// across 16 encounters, anchoring there put the indicator a median of ~1.0 s away from the real
    /// boundary, effectively at random, which is why the lag felt intermittent. A swing line is
    /// emitted BY the tick, so anchoring on the first one measures a median error of ~22 ms.</para>
    ///
    /// <para>Set once per encounter and then left alone. Re-anchoring on every swing would be more
    /// accurate still, but would yank the bar and the click around several times a fight; a phase
    /// this stable (one lattice fits a whole session to ~4 ppm) does not need chasing.</para>
    ///
    /// <para>The timestamp is the tracker's own, stamped on the feed thread when the line completed -
    /// never <c>DateTime.UtcNow</c> here, which would add this dispatch hop to the measurement.</para>
    /// </summary>
    public DateTime? TickPhaseUtc => _tickPhaseUtc;
    private DateTime? _tickPhaseUtc;

    private static bool IsSwing(CombatEventKind kind) => kind
        is CombatEventKind.Hit or CombatEventKind.Miss
        or CombatEventKind.HitByNpc or CombatEventKind.MissByNpc;

    public void OnCombatEvent(CombatEvent combatEvent)
        => MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_tickPhaseUtc is null && IsSwing(combatEvent.Kind))
            {
                _tickPhaseUtc = combatEvent.TimestampUtc;
                OnPropertyChanged(nameof(TickPhaseUtc));
            }
            _combatClearGeneration++;
            _combatStats.Observe(combatEvent);
            if (_combatStats.HasEncounter)
                _hasCombatData = true;
            // A pack fight can fire this once per swing per participant - far faster than anyone
            // can read the readout, and each render rebuilds a native FormattedString on the UI
            // thread (see ClogPage.Render). Route through the render gate so a burst collapses to
            // a bounded rate instead of one full rebuild per event (CLAUDE.md Invariant #1); a
            // throttled event is not lost, it just waits for the next tick (TickCombatDisplay).
            var now = DateTime.UtcNow;
            if (_clogRenderGate.RequestRender(now))
                RefreshCombatDisplay(now);
        });

    public void OnStatsUpdated(GameStatsSnapshot stats)
        => MainThread.BeginInvokeOnMainThread(() =>
        {
            _combatStats.ObserveStamina(stats.Stamina);
            // Deltas, not absolutes: effective-minus-raw is what the player's current load and
            // afflictions are costing them right now, which is actionable (drop the load) where a
            // bare "83/100 raw 94" was not.
            _combatDeficits = new CombatStatDeficits(
                StrengthDelta: Delta(stats.Strength, stats.RawStrength),
                DexterityDelta: Delta(stats.Dexterity, stats.RawDexterity),
                StaminaCurrent: stats.Stamina,
                StaminaMax: stats.MaxStamina,
                // Carried weight is not here, and not anywhere - it is not captured, stored or shown
                // by this client at all. See GameLineAnalyzer's score-sheet branch for why. The
                // strength DELTA above still carries the real "you are loaded down" signal, because
                // effective strength rides the FES heartbeat and already has the load priced in.
                ObjectsCarried: LiveObjectsCarried,
                // Absolute effective/max strength+dexterity, for the Combat Rail's encumbrance-tier
                // signal (DESIGN_FINAL.md 4.3), which needs fraction-of-max rather than the
                // delta-from-raw the fields above already served. Score rides along for the flee-cost
                // ladder's points column (5.5) - same snapshot, no new capture.
                StrengthEffective: stats.Strength,
                StrengthMax: stats.MaxStrength,
                DexterityEffective: stats.Dexterity,
                DexterityMax: stats.MaxDexterity,
                Score: stats.Score,
                MagicCurrent: stats.CurrentMagic,
                MagicMax: stats.MaxMagic);
            // Same reasoning as OnCombatEvent above: this fires on every FES heartbeat, which is
            // independent of (and can be faster than) the combat tick, so it goes through the same
            // render gate rather than forcing a rebuild every time.
            var now = DateTime.UtcNow;
            if (_clogRenderGate.RequestRender(now))
                RefreshCombatDisplay(now);
        });

    public void TickCombatDisplay()
    {
        if (!_hasCombatData)
            return;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            // The flush pump: this is GamePage's existing 1 Hz timer (see GamePage.OnAntiIdleTick),
            // not a new one (CLAUDE.md forbids adding a second UI-thread ticker for this). It
            // always renders unconditionally - both because 1 Hz is already comfortably under the
            // render gate's own rate cap, so it is never throttled anyway, and because the
            // encounter/fight duration clocks need to visibly advance once a second even with no
            // new combat event. That unconditional render also guarantees any event the gate
            // deferred ("dirty") during the last second is now flushed - no render is ever lost,
            // just delayed by at most one tick.
            var now = DateTime.UtcNow;
            RefreshCombatDisplay(now);
            _clogRenderGate.MarkRendered(now);
        });
    }

    /// <summary>Wipes the last encounter's readout, leaving the session totals. Bound to the clog
    /// window's clear button - the summary used to self-erase after 8 seconds, which was far too
    /// quick to read after a fight, so it now persists until dismissed on purpose.</summary>
    public void ClearCombatSummaryCommand() => ClearCombatSummary();

    private void ClearCombatSummary()
    {
        _combatStats.Reset();
        _hasCombatData = false;
        var now = DateTime.UtcNow;
        RefreshCombatDisplay(now);
        _clogRenderGate.MarkRendered(now);   // discrete user action, not a hot-path event: always renders
        OnPropertiesChanged(nameof(CanClearCombatSummary));
    }

    private void RefreshCombatDisplay(DateTime nowUtc)
    {
        // Session totals still render with no encounter on screen, so this is no longer gated on
        // _hasCombatData - the panel reports the session's running tally between fights instead of
        // going blank.
        //
        // Snapshot once and hand it down to ResolveHistory instead of letting each spot call
        // _combatStats.Snapshot() again - Snapshot() allocates a fresh FightSnapshot per active
        // NPC, and this whole method runs on every combat event, every StatsUpdated (FES
        // heartbeat), and every 1 Hz tick, so a per-call allocation here is UI-thread churn that
        // adds up (Invariant #1).
        var snapshot = _hasCombatData ? _combatStats.Snapshot(nowUtc) : IdleSnapshot;
        var history = _hasCombatData ? ResolveHistory(snapshot) : CombatHistoryContext.Empty;
        // Compose the live frame state. The previous implementation built a list of styled text
        // lines here and diffed it before publishing, because publishing rebuilt a native
        // FormattedString - one WinUI Run per span, a full teardown-and-remeasure on the UI thread.
        // That surface is gone: the canvas draws this state directly, so the only cost of a publish
        // is an invalidate. Rate limiting is still enforced upstream by _clogRenderGate, which is
        // what actually protects Invariant #1 here.
        RefreshCombatSignals(snapshot, _combatDeficits, history, nowUtc);

        OnPropertiesChanged(nameof(HasCombatData), nameof(NoCombatData),
            nameof(WhyText), nameof(PulseTier),
            nameof(EncumbranceTier), nameof(Live));
    }

    /// <summary>
    /// Computes the Combat Rail's genuinely NEW content (DESIGN_FINAL.md, this implementation
    /// phase): the flee-cost ladder + risk pairing (5.3/5.4), the plain-language "why" line (3.8),
    /// and the tier driving the shared pulse layer (4.2/4.3/4.4). Kept separate from
    /// <see cref="CombatHistoryFormatter"/> deliberately - that class's content (survivability,
    /// participants, exchange, history comparison, weapon table, session totals) is salvaged as-is
    /// per the brief; this is the new layer built on top of it.
    /// </summary>
    private void RefreshCombatSignals(
        CombatEncounterSnapshot snapshot, CombatStatDeficits deficits, CombatHistoryContext history,
        DateTime nowUtc)
    {
        // Unconditional (not gated on InCombat): carrying too much is worth flagging through the
        // post-fight grace window too, and costs nothing to recompute - pure arithmetic on values
        // already on hand.
        _encumbranceTier = CombatTierResolver.StrengthTier(deficits.StrengthEffective, deficits.StrengthMax);

        // The panel's glow keeps running at low stamina whether or not a fight is happening, because
        // the danger does not stop when the fight does. At this stamina a wandering NPC that would
        // ignore a healthy player will attack (that is RATE crossing its threshold, computed against
        // the stats the 40 and 30 knees have already degraded), one blow from most creatures can kill,
        // and fleeing still costs real points. Walking away from a fight at 22 stamina and forgetting
        // about it is a way to lose a character between fights.
        //
        // 25 rather than the documented 20: the owner's chosen margin, close enough to the survival
        // threshold to matter with a little room before it.
        var vulnerable = deficits.StaminaCurrent is int sta && sta <= OutOfCombatVulnerableStamina
            ? CombatTier.T3
            : CombatTier.None;

        if (!snapshot.HasEncounter)
        {
            _whyText = null;
            _pulseTier = vulnerable;
            _live = CombatLiveView.Idle;
            return;
        }

        // Roster/weapon/duration context is worth showing whenever an encounter exists at all, live
        // or just-finished - mirrors CombatComposition.Build's own AppendHeadline/AppendParticipants,
        // which never gated on InCombat either (2.4/3.7's post-combat wireframe still names the target).
        var roster = ParticipantRoster.Build(ToParticipantFacts(snapshot.Fights, nowUtc));
        // Live while fighting, historical once the fight is over. MUD2 auto-drops your weapon when
        // you flee - printing the drop in the same tick, just BEFORE the flee line - so the live
        // "what is in my hands" answer is correctly empty the instant an encounter ends that way. It
        // is the wrong answer for a finished fight, which was fought with something: the primary
        // fight's own WeaponUsed is the durable fact, and reading it here is what stops a
        // just-completed axe fight being summarised as UNARMED.
        var liveWeapon = snapshot.InCombat
            ? snapshot.CurrentWeapon
            : CombatComposition.PrimaryFight(snapshot)?.Weapon;
        var hasWeapon = !string.IsNullOrWhiteSpace(liveWeapon);
        var weaponText = hasWeapon ? CombatComposition.DisplayName(liveWeapon) : "UNARMED";
        // The current target's own weapon (owner's standing "NPC weapon use highlighted"
        // requirement) - null once nobody is still live, matching "no current target" everywhere
        // else in this method.
        var currentTargetNpcWeapon = snapshot.Fights.FirstOrDefault(f => !f.IsResolved)?.NpcWeapon;
        // The Ctrl+W offer, in combat only. MUD2 has no equipment slots and no default weapon: a
        // weapon is chosen while fighting, or as part of starting a fight ("kill x with y"). There is
        // nothing a wield could mean between fights, so the offer - and with it the chip advertising
        // the key - exists only while a fight is live.
        //
        // Recomputed on every refresh rather than latched, because the pack changes mid-fight (things
        // get picked up, weapons break) and a stale offer would send a wield for something no longer
        // carried - which costs a dropped guard and a free enemy swing. Cheap: one dictionary probe
        // per carried item over an inventory of a handful.
        var altWeapon = snapshot.InCombat
            ? CombatComposition.ChooseAltWeapon(
                InventoryList, snapshot.CurrentWeapon, history.ByWeapon, _isKnownWeapon)
            : null;

        if (!snapshot.InCombat)
        {
            // Post-combat / grace window: only the survival PROJECTION (threat/flee/why) goes quiet -
            // projecting a finished fight's death clock would be a lie. The roster and weapon/duration
            // context stay, exactly as the old formatter's headline/participant rows did.
            _whyText = null;
            _pulseTier = vulnerable;
            _live = new CombatLiveView(
                InCombat: false, HasEncounter: true, WeaponText: weaponText, IsUnarmed: !hasWeapon,
                EncounterDuration: snapshot.Duration, Threat: ThreatReading.Idle, Roster: roster,
                CurrentTargetNpcWeapon: currentTargetNpcWeapon,
                OutlookVerdict: OutlookVerdict.Unknown, SecondsToDie: null, SecondsToKill: null,
                StaminaCurrent: deficits.StaminaCurrent, StaminaMax: deficits.StaminaMax,
                StrengthDelta: deficits.StrengthDelta, DexterityDelta: deficits.DexterityDelta,
                ObjectsCarried: deficits.ObjectsCarried,
                // The exchange bars describe what HAPPENED, so unlike the survival projection they
                // stay up through the post-fight grace window - reviewing the fight you just had is
                // the whole reason that window exists. Kill progress does not: the target is
                // resolved, so a partial "how close was it" bar would be answering nothing.
                Measures: BuildMeasures(snapshot, history, CombatComposition.PrimaryFight(snapshot)),
                TargetDamageDone: null, TargetEstimatedPool: null,
                MagicCurrent: deficits.MagicCurrent, MagicMax: deficits.MagicMax,
                AltWeapon: altWeapon);
            return;
        }

        var primary = CombatComposition.PrimaryFight(snapshot);
        var outlook = CombatComposition.ComputeOutlook(snapshot, deficits, history, primary);

        // Incoming per-hit rate this fight - thin-sample gated (MinimumOwnHits) the same way the old
        // ladder's own risk pairing gated it, reused here for the tier table's "hits-left" trigger
        // (4.3) too, so the threat indicator and the tier table never quietly disagree about "how
        // close is this fight".
        double? incomingPerHit = primary is { TheyHits: > 0 } f ? f.ApproxDamageTaken / f.TheyHits : null;
        int? hitsLeft = incomingPerHit is double rate && rate > 0
            && deficits.StaminaCurrent is int sta1 && primary!.TheyHits >= CombatOutlook.MinimumOwnHits
            ? (int)Math.Ceiling(sta1 / rate)
            : null;

        if (deficits.StaminaCurrent is int currentSta)
            _tierResolver.ObserveStaminaForCrossings(currentSta, nowUtc);

        var staminaTier = CombatTierResolver.StaminaTier(
            deficits.StaminaCurrent, deficits.StaminaMax, hitsLeft, outlook.SecondsToDie, outlook.SecondsToKill);
        var fightTier = CombatTierResolver.ResolvePulseTier(staminaTier, CombatTier.None);

        // The whole-panel glow is the loudest thing this client owns, so it answers to ONE stamina
        // threshold - the same 25 that governs it out of combat - rather than to the survival
        // projection on its own.
        //
        // The projection promotes to T3 at "under 15 seconds to die", which against an ordinary
        // zombie is arithmetically true from about 30 stamina. That is a correct reading and still
        // too eager for a full-panel flash: it fires while the player is comfortably above the
        // threshold they actually act on, and an alarm that cries wolf at 30 is an alarm that gets
        // ignored at 20. The projection still drives everything quieter.
        //
        // One override survives, because it is not a projection but a count: two hits left or fewer.
        // That is imminent whatever the absolute stamina says - it is how a dragon kills someone at
        // full health.
        var imminent = fightTier == CombatTier.T3 && hitsLeft is int left && left <= 2;
        _pulseTier = imminent || vulnerable == CombatTier.T3
            ? CombatTier.T3
            : fightTier == CombatTier.T3 ? CombatTier.T2 : fightTier;

        // No flee-cost figure is computed or published here, deliberately. The owner's instruction is
        // explicit: "We don't need to be telling/showing the player the flee statistics - that's
        // stupid cognitive burden." The player already knows fleeing is expensive - one accidental
        // flee from a zombie at 90/100 stamina cost 1300 of 13,000 points and a level. What the panel
        // owes them is the zone signal (staminaTier, above) and a valid direction to run, not a price
        // tag to read while deciding. FleeCostLadder is retained as documented domain knowledge so
        // nobody re-introduces a "fleeing is cheap here" affordance; it drives no UI.
        _whyText = BuildWhyText(snapshot, deficits, history, primary, nowUtc);

        var threat = ThreatIndicator.Resolve(
            inCombat: true, staminaTier, outlook.Verdict, outlook.SecondsToDie, hitsLeft,
            deficits.StaminaCurrent, deficits.StaminaMax);

        _live = new CombatLiveView(
            InCombat: true, HasEncounter: true, WeaponText: weaponText, IsUnarmed: !hasWeapon,
            EncounterDuration: snapshot.Duration, Threat: threat, Roster: roster,
            CurrentTargetNpcWeapon: currentTargetNpcWeapon,
            OutlookVerdict: outlook.Verdict, SecondsToDie: outlook.SecondsToDie, SecondsToKill: outlook.SecondsToKill,
            StaminaCurrent: deficits.StaminaCurrent, StaminaMax: deficits.StaminaMax,
            StrengthDelta: deficits.StrengthDelta, DexterityDelta: deficits.DexterityDelta,
            ObjectsCarried: deficits.ObjectsCarried,
            Measures: BuildMeasures(snapshot, history, primary),
            // Kill progress is shown only against a target still standing, and only once a kill of
            // that kind is on record to estimate the pool from.
            TargetDamageDone: primary is { IsResolved: false } ? primary.ApproxDamageDone : null,
            TargetEstimatedPool: primary is { IsResolved: false } ? history.Primary.EstimatedStaminaPool : null,
            MagicCurrent: deficits.MagicCurrent, MagicMax: deficits.MagicMax,
            AltWeapon: altWeapon);
    }

    /// <summary>
    /// Builds the exchange as drawable bars (see <see cref="CombatMeasure"/>) instead of the numeric
    /// "now"/"usual" matrix the panel used to print.
    ///
    /// <para>Hit rates share a natural 0..1 scale. Damage per hit does not, so both sides take ONE
    /// shared full-scale derived from the largest value in play - without that the two bars would be
    /// individually normalised and "you hit for 9.5, they hit for 4.0" would draw as two identical
    /// full bars, which is worse than no chart at all.</para>
    /// </summary>
    private static IReadOnlyList<CombatMeasure> BuildMeasures(
        CombatEncounterSnapshot snapshot, CombatHistoryContext history, FightSnapshot? primary)
    {
        var youAttempts = (primary?.YouHits ?? 0) + (primary?.YouMisses ?? 0);
        var theyAttempts = (primary?.TheyHits ?? 0) + (primary?.TheyMisses ?? 0);
        double? youRate = youAttempts == 0 ? null : primary!.YouHits / (double)youAttempts;
        double? theyRate = theyAttempts == 0 ? null : primary!.TheyHits / (double)theyAttempts;

        double? youPerHit = primary is { YouHits: > 0 } pd ? pd.ApproxDamageDone / pd.YouHits : null;
        double? theyPerHit = primary is { TheyHits: > 0 } pt ? pt.ApproxDamageTaken / pt.TheyHits : null;
        var usualPerHit = history.Primary.MedianDamagePerHit;

        // One shared ceiling for both damage bars, with headroom so a current best does not sit
        // pinned at full width with nowhere to grow.
        var damageScale = Math.Max(
            Math.Max(youPerHit ?? 0, theyPerHit ?? 0),
            usualPerHit ?? 0) * 1.25;
        if (damageScale <= 0) damageScale = 1;

        var n = history.Primary.SampleSize;
        return
        [
            new CombatMeasure("you", youRate, history.Primary.MedianYouHitRate, 1.0, n, true, true),
            new CombatMeasure("them", theyRate, history.Primary.MedianTheyHitRate, 1.0, n, false, true),
            // Only the player's side carries a historical tick: FightHistorySummary records the
            // player's damage per landed blow, and has no equivalent per-hit figure for the NPC.
            new CombatMeasure("you", youPerHit, usualPerHit, damageScale, n, true, false),
            new CombatMeasure("them", theyPerHit, null, damageScale, 0, false, false),
        ];
    }

    /// <summary>Maps the app-side <see cref="FightSnapshot"/> list down to the plain, MAUI-independent
    /// facts <see cref="ParticipantRoster.Build"/> needs - that class lives in mudsharp (no MAUI
    /// dependency, directly unit-testable), so it cannot reference <see cref="FightSnapshot"/>
    /// itself.</summary>
    private static IReadOnlyList<ParticipantFact> ToParticipantFacts(
        IReadOnlyList<FightSnapshot> fights, DateTime nowUtc)
    {
        var facts = new ParticipantFact[fights.Count];
        for (var i = 0; i < fights.Count; i++)
        {
            var fight = fights[i];
            // Age is resolved to seconds here, at the one point that knows what "now" is, so nothing
            // downstream has to be handed a clock. Negative ages (a reading timestamped marginally
            // ahead of this refresh) clamp to zero rather than reading as fresher than fresh.
            double? healthAge = fight.HealthReadUtc is DateTime read
                ? Math.Max(0.0, (nowUtc - read).TotalSeconds)
                : null;
            facts[i] = new ParticipantFact(
                fight.NpcName, fight.IsResolved, fight.Outcome,
                fight.HealthRung, fight.HealthPhrase, healthAge, fight.ApproxDamageTaken,
                fight.NpcWeapon);
        }
        return facts;
    }

    /// <summary>The plain-language "why" line - surfaces causes, never coefficients, and yields at
    /// most one sentence (the single highest-priority active condition). Returns text; the canvas
    /// owns how it is drawn.</summary>
    private string? BuildWhyText(
        CombatEncounterSnapshot snapshot, CombatStatDeficits deficits, CombatHistoryContext history,
        FightSnapshot? primary, DateTime nowUtc)
    {
        var hasWeapon = !string.IsNullOrWhiteSpace(snapshot.CurrentWeapon);
        var livePerHit = CombatComposition.LivePerHit(snapshot, history);

        var weaponEntry = history.ByWeapon.FirstOrDefault(e =>
            hasWeapon && string.Equals(e.Weapon, snapshot.CurrentWeapon, StringComparison.OrdinalIgnoreCase));
        var weaponDisplayName = hasWeapon ? CombatComposition.DisplayName(snapshot.CurrentWeapon) : null;

        var attempts = (primary?.YouHits ?? 0) + (primary?.YouMisses ?? 0);
        double? liveHitRate = attempts == 0 ? null : primary!.YouHits / (double)attempts;

        double? secondsSinceEquip = primary?.NpcWeaponEquippedUtc is DateTime equippedUtc
            ? (nowUtc - equippedUtc).TotalSeconds
            : null;
        var npcWeaponDisplayName = primary?.NpcWeapon is string npcWeapon
            ? CombatComposition.DisplayName(npcWeapon)
            : null;

        var result = CombatWhyLine.Resolve(
            hasWeapon,
            deficits.StrengthDelta,
            deficits.ObjectsCarried,
            livePerHit,
            weaponEntry?.Summary.MedianDamagePerHit,
            weaponEntry?.Summary.FightCount ?? 0,
            weaponDisplayName,
            deficits.DexterityDelta,
            liveHitRate,
            history.Primary.MedianYouHitRate,
            secondsSinceEquip,
            primary?.NpcName,
            npcWeaponDisplayName);

        return result?.Text;
    }

    /// <summary>Stand-in for "no encounter", so the formatter's session-totals path can run without a
    /// live snapshot.</summary>
    private static readonly CombatEncounterSnapshot IdleSnapshot = new(
        HasEncounter: false, InCombat: false, StartedUtc: null, CurrentWeapon: null, ActiveNpcs: [],
        YouHits: 0, YouMisses: 0, TheyHits: 0, TheyMisses: 0, YouHitRate: 0, TheyHitRate: 0,
        ApproxDamageDone: 0, ApproxDamageTaken: 0, Duration: TimeSpan.Zero,
        ApproxDps: 0, TheirApproxDps: 0, Fights: []);

    /// <summary>Resolves the history context for the encounter's primary target via the incremental
    /// index (see CombatHistoryCache/HistoryIndex) instead of scanning the whole fight corpus -
    /// DESIGN_FINAL.md section 7.3, and the reason this whole file's history block used to be the
    /// clog window's lag source on a long session.</summary>
    private CombatHistoryContext ResolveHistory(CombatEncounterSnapshot snapshot)
    {
        var primary = CombatComposition.PrimaryFight(snapshot);
        if (_fightHistory is null || primary is null || string.IsNullOrWhiteSpace(primary.NpcGroup))
            return CombatHistoryContext.Empty;

        return _historyCache.Resolve(
            _fightHistory, primary.NpcName, primary.NpcGroup, snapshot.CurrentWeapon, snapshot.StartedUtc);
    }

    private static int? Delta(int? effective, int? raw)
        => effective is null || raw is null ? null : effective.Value - raw.Value;


    /// <summary>Apply a new status-effect snapshot from the session (fires on the read-loop thread).</summary>
    public void OnStatusEffectsChanged(StatusEffectState s)
        => MainThread.BeginInvokeOnMainThread(() =>
        {
            _strBuff = s.StrengthBuff;   _strDebuff = s.StrengthDebuff;
            _dexBuff = s.DexterityBuff;  _dexDebuff = s.DexterityDebuff;
            _staBuff = s.StaminaBuff;    _staDebuff = s.StaminaDebuff;
            _glow    = s.Glow;
            _strBuffTip = s.StrengthBuffMsg;   _strDebuffTip = s.StrengthDebuffMsg;
            _dexBuffTip = s.DexterityBuffMsg;  _dexDebuffTip = s.DexterityDebuffMsg;
            _staBuffTip = s.StaminaBuffMsg;    _staDebuffTip = s.StaminaDebuffMsg;
            _glowTip    = s.GlowMsg;
            _deafTip = s.DeafMsg; _blindTip = s.BlindMsg; _dumbTip = s.DumbMsg; _crippledTip = s.CrippledMsg;
            OnPropertiesChanged(
                nameof(StrengthBuff), nameof(StrengthDebuff), nameof(DexterityBuff), nameof(DexterityDebuff),
                nameof(StaminaBuff), nameof(StaminaDebuff), nameof(Glow),
                nameof(StaminaBuffDy), nameof(StaminaDebuffDy), nameof(StrengthBuffDy), nameof(StrengthDebuffDy),
                nameof(DexterityBuffDy), nameof(DexterityDebuffDy),
                nameof(StrengthBuffTip), nameof(StrengthDebuffTip), nameof(DexterityBuffTip), nameof(DexterityDebuffTip),
                nameof(StaminaBuffTip), nameof(StaminaDebuffTip), nameof(GlowTip),
                nameof(DeafTip), nameof(BlindTip), nameof(DumbTip), nameof(CrippledTip));
        });

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
    public string FloatingFoldGlyph => _isFloatingOnlineFolded ? "[>]" : "[v]";

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

    /// <summary>Minutes a departed player lingers in the Recent list before being forgotten.
    /// 0 = disabled (Recent list never populates). Range 0–10.</summary>
    public int ForgetWindowMinutes
    {
        get => _forgetWindowMinutes;
        set
        {
            if (!Set(ref _forgetWindowMinutes, Math.Clamp(value, 0, 10))) return;
            if (_forgetWindowMinutes <= 0) ClearRecent();
        }
    }

    /// <summary>Grouped view of the Recent list (one bucket per "minutes since last seen").
    /// Rebuilt wholesale by <see cref="RebuildRecentGroups"/>. Side-panel only — never floated.</summary>
    public ObservableCollection<RecentGroup> RecentGroups { get; } = new();

    /// <summary>True when the Recent list has any entries (drives its section visibility).</summary>
    public bool HasRecent => _recent.Count > 0;

    /// <summary>Count of non-departing online players.</summary>
    public int WhoCount { get; private set; }

    /// <summary>Formatted count for the Online section heading, e.g. " (3)".</summary>
    public string OnlineCountText => $" ({WhoCount})";

    /// <summary>Raised when the user taps the hamburger in the floating panel — opens settings/display.</summary>
    public event Action? FloatingOpenDisplaySettings;

    /// <summary>Raised when the user taps a Recent-list name — requests a "sniff" value-probe
    /// for that persona (see MudSession.QueueValueProbe). Payload is the persona name.</summary>
    public event Action<string>? ValueProbeRequested;

    /// <summary>Raised (on the UI thread) when FEW/FEI subscription needs updating.
    /// Payload: (includeFew, includeFei).</summary>
    public event Action<bool, bool>? SubscriptionOptionsChanged;

    private void RaiseSubscriptionChanged()
    {
        // Collapsing BOTH item sections stops FEI being probed at all, which freezes InventoryList
        // at whatever it last held. Drop the latch so the combat readout reports the count as
        // unknown rather than quietly serving a frozen one - the exact failure being fixed here,
        // just with a different stale source (see LiveObjectsCarried). It re-latches on the next
        // list that actually arrives.
        if (!_isInventoryExpanded && !_isItemsHereExpanded)
            _feiEverCompleted = false;
        SubscriptionOptionsChanged?.Invoke(_isOnlineExpanded, _isInventoryExpanded || _isItemsHereExpanded);
    }

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
    // See LiveObjectsCarried: latched true by the first completed FEI list.
    private bool _feiEverCompleted;

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
    /// <summary>Toggles the Combat Rail (the new right-edge panel - DESIGN_FINAL.md D3/2.2). The
    /// live path is "$clog on"/"$clog off" (GameViewModel.HandleClogCommand), which sets
    /// <see cref="IsCombatPanelVisible"/> directly and drives the window resize; this command exists
    /// so the same toggle is independently reachable (and testable) without a live connection.</summary>
    public ICommand ToggleCombatPanelCommand { get; }
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
    public ICommand ProbeRecentCommand { get; }
    public ICommand ToggleCombatMetronomeCommand { get; }

    /// <summary>Raised when an interaction should hand keyboard focus back to the input box.
    /// Opening the About dialog deliberately does not raise it — focus belongs to the dialog.</summary>
    public event Action? RequestFocus;

    public SidePanelViewModel()
    {
        _isKnownWeapon = name => _fightHistory?.IsKnownWeapon(name) ?? false;
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
        ToggleCombatPanelCommand = new Command(() => { IsCombatPanelVisible = !IsCombatPanelVisible; RequestFocus?.Invoke(); });
        ToggleCombatMetronomeCommand = new Command(ToggleCombatMetronome);
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
            OnPropertiesChanged(nameof(FloatingMapWidth), nameof(FloatingMapHeight), nameof(FloatingMapPanelWidth));
        });
        DecreaseMapSizeCommand = new Command(() =>
        {
            if (_mapSizeIx >= MapSizes.Length - 1) return;
            _mapSizeIx++;
            OnPropertiesChanged(nameof(FloatingMapWidth), nameof(FloatingMapHeight), nameof(FloatingMapPanelWidth));
        });
        ToggleFloatingOnlineLockCommand = new Command(() => { IsFloatingOnlineLocked = !IsFloatingOnlineLocked; RequestFocus?.Invoke(); });
        ToggleFloatingMapLockCommand    = new Command(() => { IsFloatingMapLocked    = !IsFloatingMapLocked;    RequestFocus?.Invoke(); });
        // Tapping a Recent name asks for a one-shot value-probe, then hands focus back to the
        // command box (Invariant #0 — every interaction leaves the user able to type).
        ProbeRecentCommand = new Command<WhoEntry>(e =>
        {
            if (e is not null && !string.IsNullOrEmpty(e.PersonaName))
                ValueProbeRequested?.Invoke(e.PersonaName);
            RequestFocus?.Invoke();
        });
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
            CurrentRoom = "Option Menu.";
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
            var now = DateTime.UtcNow;
            // Key by persona name so a level-up -- which changes the description or adds a
            // Sir/Lady prefix -- is treated as the same player, not a departure + arrival.
            var newByPersona = snapshot.ToDictionary(
                w => w.PersonaName, StringComparer.OrdinalIgnoreCase);

            // A persona present in this FEW is live again — it must never also sit in Recent
            // (covers both returnees still in WhosList and a floating-departure copy in Recent).
            _recent.RemoveAll(r => newByPersona.ContainsKey(r.PersonaName));

            // Does a departing player land in the (side-panel) Recent list at all? Only when Recent
            // is enabled, the side panel is showing, and the Online section is on.
            bool recentEligible = _forgetWindowMinutes > 0 && _isPanelExpanded && _isOnlineExpanded;

            // Update returnees in place; route departures by display state:
            //  • docked + Recent   → jump straight to Recent, no fade
            //  • floating + Recent → fade out in the floater AND show in Recent immediately
            //  • otherwise         → the plain fade-out then removal (no Recent)
            for (int i = WhosList.Count - 1; i >= 0; i--)
            {
                var existing = WhosList[i];
                if (newByPersona.TryGetValue(existing.PersonaName, out var updated))
                {
                    existing.IsDeparting = false;   // present again — cancel any pending fade-out + removal
                    if (existing.Name  != updated.Name)  existing.Name  = updated.Name;
                    if (existing.Color != updated.Color) existing.Color = updated.Color;
                    continue;
                }
                if (existing.IsDeparting)
                    continue;   // already fading — leave its pending removal alone

                var leaving = existing;
                // They were present in the previous FEW, so that completion is their "last seen"
                // time; the gap since then low-clamps their Recent lifetime (an overdue FEW can't
                // instant-flush them — see MoveToRecent).
                var lastSeenUtc = _lastFewCompleteUtc == default ? now : _lastFewCompleteUtc;

                if (recentEligible && _isOnlinePinned)
                {
                    // Docked: no fade — jump straight to Recent.
                    WhosList.RemoveAt(i);
                    MoveToRecent(leaving, lastSeenUtc);
                }
                else if (recentEligible)
                {
                    // Floating: the online copy fades out in the floater, while Recent gets a fresh
                    // (non-fading) copy right away — the original is removed once the fade finishes.
                    existing.IsDeparting = true;
                    MoveToRecent(new WhoEntry(leaving.Name, leaving.Color), lastSeenUtc);
                    _dispatcher?.DispatchDelayed(TimeSpan.FromMilliseconds(3400), () =>
                    {
                        if (leaving.IsDeparting) WhosList.Remove(leaving);
                    });
                }
                else
                {
                    // No Recent (disabled / side panel hidden / Online folded): plain fade + removal.
                    existing.IsDeparting = true;
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

            _lastFewCompleteUtc = now;
            // Re-age the Recent groups (and sweep anything past its window) on the heartbeat —
            // no repeating UI-thread timer (Invariant #1); this piggybacks the FEW refresh.
            // Unconditional: a returning player removed from _recent above may have emptied it,
            // and the view still needs clearing (the signature guard makes the no-op case cheap).
            RebuildRecentGroups();
        });
    }

    // ── Recent list (players who faded off Online, kept for the Forget window) ──────────

    /// <summary>
    /// Move a just-departed player into the Recent list. Their lifetime there is
    /// <c>clamp(ForgetWindow − minutesSinceLastSeen, 1 min, ForgetWindow)</c>: a normal
    /// departure keeps almost the full window, while a seriously overdue FEW (we slept, the
    /// gap dwarfs the poll interval) floors at 1 minute instead of instant-flushing everyone.
    /// No-op when the Forget window is disabled. UI thread.
    /// </summary>
    private void MoveToRecent(WhoEntry entry, DateTime lastSeenUtc)
    {
        if (_forgetWindowMinutes <= 0) return;
        var now = DateTime.UtcNow;
        var ageMin = Math.Max(0.0, (now - lastSeenUtc).TotalMinutes);
        var lifetimeMin = Math.Clamp(_forgetWindowMinutes - ageMin, 1.0, _forgetWindowMinutes);
        entry.IsDeparting = false;   // Recent entries do not fade; they age then expire
        entry.LastSeenUtc = lastSeenUtc;
        entry.ExpiryUtc   = now + TimeSpan.FromMinutes(lifetimeMin);
        // De-dupe by persona (e.g. a re-probed invisible entry cycling back).
        _recent.RemoveAll(e => string.Equals(e.PersonaName, entry.PersonaName, StringComparison.OrdinalIgnoreCase));
        _recent.Add(entry);
        RebuildRecentGroups();
        // One-shot removal at expiry (the RebuildRecentGroups sweep is a backstop if this misses).
        var expiring = entry;
        _dispatcher?.DispatchDelayed(TimeSpan.FromMinutes(lifetimeMin), () =>
        {
            if (_recent.Contains(expiring)) RemoveFromRecent(expiring);
        });
    }

    private void RemoveFromRecent(WhoEntry entry)
    {
        if (_recent.Remove(entry))
            RebuildRecentGroups();
    }

    private void ClearRecent()
    {
        if (_recent.Count == 0 && RecentGroups.Count == 0) return;
        _recent.Clear();
        RecentGroups.Clear();
        _recentSignature = "";
        OnPropertyChanged(nameof(HasRecent));
    }

    /// <summary>
    /// Rebuild the grouped Recent view from <see cref="_recent"/>, first sweeping expired entries.
    /// Buckets by whole minutes since last seen (floored at 1, so a fresh fade reads "~1 min").
    /// Guarded by a signature so an unchanged heartbeat does not re-template the list.
    /// </summary>
    private void RebuildRecentGroups()
    {
        var now = DateTime.UtcNow;
        _recent.RemoveAll(e => e.ExpiryUtc <= now);

        var grouped = _recent
            .GroupBy(e => Math.Max(1, (int)Math.Round((now - e.LastSeenUtc).TotalMinutes)))
            .OrderBy(g => g.Key)
            .ToList();

        var sig = string.Join("|",
            grouped.Select(g => g.Key + ":" + string.Join(",", g.Select(e => e.Name))));
        if (sig == _recentSignature)
            return;
        _recentSignature = sig;

        RecentGroups.Clear();
        foreach (var g in grouped)
            RecentGroups.Add(new RecentGroup($"~{g.Key} min", g.ToList()));
        OnPropertyChanged(nameof(HasRecent));
    }

    /// <summary>
    /// Result of a "sniff" value-probe on a Recent name (see MudSession.SniffResult). UI-marshalled.
    ///   • Present   → the player is online and visible; promote back into Online (plain).
    ///   • Invisible → online but invisible; promote into Online wrapped in parens for one probe
    ///                 interval, then the next FEW drops them back to Recent (parens retained).
    ///   • Offline   → logged out; leave the entry to age out of Recent on its own (a probe only
    ///                 ever *promotes* — it never removes).
    /// The next FEW makes the final call in every case; we never auto-re-probe.
    /// </summary>
    public void OnSniffResult(string name, SniffOutcome outcome)
        => MainThread.BeginInvokeOnMainThread(() =>
        {
            var recent = _recent.FirstOrDefault(
                e => string.Equals(e.PersonaName, name, StringComparison.OrdinalIgnoreCase));
            switch (outcome)
            {
                case SniffOutcome.Offline:
                    // Confirmed logged out — do nothing. The entry just ages out of Recent on its
                    // own; a probe never removes it (we only act when they turn out to be online).
                    break;
                case SniffOutcome.Present:
                    PromoteToOnline(recent, name, invisible: false);
                    break;
                case SniffOutcome.Invisible:
                    PromoteToOnline(recent, name, invisible: true);
                    break;
            }
        });

    // Move a Recent entry (or a bare name, if the entry already expired) back onto the live
    // Online list. Invisible promotions wrap the name in parens as the last-known-invisible marker.
    private void PromoteToOnline(WhoEntry? recent, string name, bool invisible)
    {
        if (recent is not null) RemoveFromRecent(recent);
        // Already live again (a concurrent FEW re-added them)? Leave that entry alone.
        if (WhosList.Any(w => string.Equals(w.PersonaName, name, StringComparison.OrdinalIgnoreCase)))
            return;

        var color   = recent?.Color ?? Color.FromArgb("#FFFFFF");
        var rawName = recent is null ? name
                    : recent.IsInvisible ? recent.Name[1..^1] : recent.Name;
        var display = invisible ? "(" + rawName + ")" : rawName;
        WhosList.Add(new WhoEntry(display, color));
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

            // Latched, never cleared: it distinguishes "FEI says you carry nothing" from "FEI has
            // never reported". Without it an empty InventoryList reads as a confirmed zero, and the
            // combat readout would assert "0 items" before the first probe ever landed.
            _feiEverCompleted = true;

            FeiRefreshed?.Invoke();   // restart the section's compositor stale-dim
        });
    }

    /// <summary>
    /// The live count of objects carried, or null when FEI has not reported yet.
    ///
    /// <para>This exists because the FES heartbeat does NOT carry an object count or a carried
    /// weight - it is 15 fields (stamina/str/dex/magic/score/afflictions/reset/weather) and nothing
    /// more (see Mud2C1Decoder.ParseAndEmitFes). Those two figures reach GameStatsSnapshot ONLY by
    /// parsing the text of a `score` command, and MudSession.MergeStats then carries the last value
    /// forward forever. So `stats.ObjectsCarried` is whatever was true when `score` last ran -
    /// typically the automatic one at character select - and it does not move when the player picks
    /// anything up or puts anything down.</para>
    ///
    /// <para>Reported live: the panel claimed "7 items cost you N str right now" against a real
    /// inventory of 3. FEI rides the heartbeat and is the same source the side panel's own inventory
    /// section renders, so reading it here also stops the two panels contradicting each other on
    /// screen at the same moment.</para>
    /// </summary>
    private int? LiveObjectsCarried => _feiEverCompleted ? InventoryList.Count : null;

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
