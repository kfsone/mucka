using Microsoft.Maui.Graphics;
using Mucka.Core;
using MudSharp.Combat;
using MudSharp.Models;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using Mucka.Combat;

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

    // -- Section fold/unfold state ---------------------------------------------
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
    // UTC time the last FEW response completed - the "last seen" baseline for players who drop
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
    // panel) -- same fixed-width ASCII fold convention as the section glyphs below: a
    // variable-width glyph mixed into fixed-width monospace ASCII breaks column alignment.
    public string PanelToggleGlyph => _isPanelExpanded ? "[v]" : "[>]";

    // -- Section fold/unfold ----------------------------------------------------
    // [v] = expanded (content visible), [>] = collapsed (content hidden). ASCII-only.
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
    /// padding. Bound to the Border's WidthRequest so the windlet hugs the dial. Without it, the
    /// panel stretches to the full screen width on Android (not observed on Windows).
    /// </summary>
    public double FloatingMapPanelWidth => FloatingMapWidth + 16;

    // -- Buffs / status effects -------------------------------------------------
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
    // active, and split +/-1 apart only when both are present (the stacked look). TranslationY, so
    // it never affects layout. Buff sits behind (+1, down), debuff in front (-1, up).
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

    // -- Combat indicator ---------------------------------------------------------
    // Driven by MudSharp.Combat.CombatTracker via MuckaConnection.InCombatChanged.
    private readonly CombatStatsAggregator _combatStats = new();
    // Bounds how often the clog readout actually rebuilds/republishes (see ClogRenderGate). Combat
    // events and the FES heartbeat can fire many times a second in a pack fight; this collapses
    // that burst to a bounded rate while the existing 1 Hz tick (TickCombatDisplay) guarantees any
    // deferred ("dirty") state is still flushed within a second, and combat-ending transitions
    // render directly so a final state is never lost behind the throttle.
    private readonly ClogRenderGate _clogRenderGate = new();
    private bool _inCombat, _hasCombatData, _isCombatGrace;
    private int _combatClearGeneration;
    // -- Combat Rail: the right-edge panel -----------
    // Show/hide only - driven by ToggleCombatPanelCommand from the overflow menu, with GamePage
    // resizing the window on the change. GameViewModel's constructor is the one exception to "an
    // explicit toggle is the only way this becomes true": it seeds this from the connecting profile's
    // persisted ClientSettings.ShowCombatRail (T2), so the panel restores to whatever a given persona
    // last left it at on relog. The window never resizes itself on any other change - see
    // GamePage.OnAppearing's own remarks on applying that first state.
    private bool _isCombatPanelVisible;
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
    // The dead strip's session-scoped history of every PRIOR encounter's endings, chronological by
    // EndedUtc (see CombatEndingOrder.Sorted). Grown in once per encounter close (OnInCombatChanged's
    // else branch, alongside _session's own fold) and never reset per encounter - same lifetime as
    // _tickPhase below, for the same reason: a fight closing is not a reason to forget what the
    // session has already shown the player.
    //
    // WORKING storage only - never published. CombatLiveView.DeadStripHistory is documented as never
    // mutated after publish (see CombatContracts.cs), and this list keeps growing via Add() for the
    // rest of the session, so handing IT out would alias a "final" frame to storage a later encounter
    // close then mutates. _archiveSnapshot below is the immutable copy that actually gets published;
    // see BuildDeadStripHistory.
    // The dead strip's two grouping counters draw a 1px yellow dotted separator between encounters
    // and a 2px white solid line between resets. Both are simple monotonic session-scoped ordinals -
    // see CombatEnding's own remarks for why not a timestamp/epoch.
    // Neither is ever decremented or reset mid-session: a fresh CombatEnding always gets the CURRENT
    // value of both, so two endings compare equal on one iff they were recorded in the same
    // encounter/reset cycle, whatever else about the session has happened since.
    //
    // Starts at 0 rather than 1 so the first encounter/reset cycle's endings still compare equal to
    // each other (0 == 0) without a separate "has anything happened yet" flag - the value only has to
    // be a stable key, never a human-facing count.
    private int _encounterOrdinal;
    private int _resetOrdinal;
    // True once the corroborated reset landing has advanced _resetOrdinal for the current cycle, so
    // the shell-prompt backstop in OnGameModeExited does not advance it a second time. Cleared by
    // that same handler, which every reset passes through. See OnWorldResetLanded.
    private bool _resetOrdinalAdvanced;
    private readonly List<CombatEnding> _endingArchive = new();
    // The published, immutable form of _endingArchive - a fresh array taken only when the archive
    // actually grows (at an encounter close), so every frame published before that close keeps
    // aliasing a snapshot nothing will ever mutate again. Safe to hand straight to CombatLiveView
    // and to reuse across any number of refreshes between closes at zero additional cost.
    private IReadOnlyList<CombatEnding> _archiveSnapshot = Array.Empty<CombatEnding>();
    // True from the moment the CURRENT _combatStats encounter's endings are folded into
    // _endingArchive (the same close) until the NEXT encounter begins clears it. While true,
    // BuildDeadStripHistory must not also read _combatStats.Fights for "this encounter's own
    // endings" - the aggregator is not reset until BeginEncounter runs, so its Fights list would
    // still answer with exactly what was just archived, and reading both would double every row.
    private bool _currentEncounterArchived;
    // The dead strip's published-history cache for the LIVE tail (archive + this encounter's own
    // resolved endings so far), and the resolved-fight count it was built from. Merging and
    // re-sorting the tail is the one non-trivial cost BuildDeadStripHistory has, and it runs on
    // every combat event/FES heartbeat/1 Hz tick while any fight in the encounter has resolved
    // (Invariant #1) - mirroring _historyCache below, which exists for exactly this reason. The
    // resolved COUNT is a valid dirty check because a fight's (Name, Outcome, EndedUtc) never changes
    // once resolved (FightAccumulator.Resolve is first-resolution-wins) and never un-resolves, so
    // "the count is the same as last time" means "the resolved set is byte-for-byte the same as last
    // time" - nothing to rebuild, and the previously published list is still exactly correct.
    private IReadOnlyList<CombatEnding> _deadStripHistoryCache = Array.Empty<CombatEnding>();
    private int _deadStripHistoryCachedResolvedCount = -1;
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

    /// <summary>Stamina lost on the most recent combat tick, for the STA ring's tinted slice. Fed by
    /// every stats reading (see OnStatsUpdated) rather than only combat events, because a stamina GAIN
    /// is what clears a stale slice and gains arrive on the heartbeat.</summary>
    private readonly Mucka.Combat.TickStaminaLoss _tickLoss = new();

    // Set once at startup (see AttachSwingDamage). Null in unit/design contexts, in which case the
    // opponents' "ever" damage row simply is not drawn. Unlike _fightHistory this needs no cache: the
    // lookup is a dictionary probe under a lock held for exactly that probe, run for a handful of
    // opponents per refresh, and the index only changes at encounter close - so there is no repeated
    // work to memoize and no self-comparison hazard to design around (SwingDamageIndex settles that
    // one at the writing end, by not folding a live encounter in at all).
    private SwingDamageIndex? _swingDamage;

    // Same lifetime and the same null contract as _swingDamage: attached once at startup, absent in
    // unit/design contexts, in which case the target's remaining-stamina band simply is not computed.
    private StaminaPoolIndex? _staminaPool;

    // Same lifetime and the same null contract again. Feeds ReachAggregate.GreatestThreat, which picks
    // the ONE live opponent the player's own incoming-damage prediction bands are projected from (see
    // IncomingPerBlowOf) - the reason this index is read per roster row rather than once for the
    // primary target is that the selection is a question about the whole opposition, not one target.
    private ReachMarkIndex? _reachMarks;
    // Cached once so the per-carried-item weapon test costs no allocation on the refresh path. Reads
    // _fightHistory through the closure rather than capturing it, so attaching the store later (as
    // startup does) is picked up without rebuilding the delegate.
    private readonly Func<string, bool> _isKnownWeapon;

    public bool InCombat   => _inCombat;
    /// <summary>True while a just-ended encounter's clog is still draining its tail (trailing
    /// prose captured up to the next prompt - see ClogWriter.IsTailOnly) with no new encounter
    /// yet live - bind the combat indicator's opacity to this so it dims instead of looking
    /// identical to an actively-ongoing fight.</summary>
    public bool IsCombatGracePeriod => _isCombatGrace;
    public double CombatIconOpacity => _isCombatGrace ? 0.4 : 1.0;
    public string CombatTip => _isCombatGrace ? "Combat winding down..." : "In combat";
    public bool HasCombatData => _hasCombatData;
    public bool NoCombatData => !_hasCombatData;

    /// <summary>Whether the Combat Rail is available on this platform at all. Windows only: the
    /// panel's supporting logic (window resize, tick sweep, metronome, pulse wiring - all in
    /// GamePage.xaml.cs's #if WINDOWS block) was built for a desktop window that can grow to make
    /// room for it, not a phone-sized screen. Drives both the overflow-menu entry's visibility and
    /// <see cref="IsCombatPanelVisible"/>'s own setter, so there is one place that decides this
    /// rather than the menu and the panel disagreeing.</summary>
    public bool IsCombatRailSupported =>
#if ANDROID
        false;
#else
        true;
#endif

    /// <summary>Whether the Combat Rail (the new right-edge panel) is shown. Toggled from the
    /// overflow menu's "Combat" entry and, when it changes, by GamePage resizing the window
    /// by the panel's own width (docs/combat-panel-design.md, window policy) - never by anything else; this property
    /// does not change on combat start/end, only on an explicit toggle. Refuses to become true at
    /// all when <see cref="IsCombatRailSupported"/> is false, so no future caller can light up an
    /// unmanaged panel on a platform its supporting logic was never built for.</summary>
    public bool IsCombatPanelVisible
    {
        get => _isCombatPanelVisible;
        set => Set(ref _isCombatPanelVisible, value && IsCombatRailSupported);
    }

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

    /// <summary>
    /// Whether the rail prints damage floats - the small "5-9" / "-7" / "Miss" that rises off an
    /// opponent's slot or the stamina seal and fades. **On by default**, like the metronome and for
    /// the same reason.
    ///
    /// <para>Session-scoped, matching every other rail option: the metronome is not persisted
    /// either, and the overflow menu's Side Panel/Onlines/Compass rows are explicit that they flip
    /// live visibility only. See <see cref="CombatFloatRaised"/> for what is being suppressed - the
    /// event simply is not raised, so with floats off the whole feature costs a bool test per combat
    /// line.</para>
    /// </summary>
    public bool IsCombatFloatsEnabled
    {
        get => _isCombatFloatsEnabled;
        set => Set(ref _isCombatFloatsEnabled, value);
    }
    private bool _isCombatFloatsEnabled = true;

    /// <summary>Toggles the floats and hands focus straight back to the command box (Invariant #0),
    /// exactly as <see cref="ToggleCombatMetronome"/> does.</summary>
    public void ToggleCombatFloats()
    {
        IsCombatFloatsEnabled = !IsCombatFloatsEnabled;
        RequestFocus?.Invoke();
    }

    /// <summary>
    /// Whether the Combat Rail draws its two stat rows and the exchange spark. Unlike the floats and
    /// the metronome beside it, this one is PERSISTED (mucka.ini, global) and it changes the panel's
    /// WIDTH - the host re-sizes the Border and the window from <see cref="CombatStatsChanged"/>.
    /// </summary>
    public bool IsCombatStatsEnabled
    {
        get => _isCombatStatsEnabled;
        set
        {
            if (!Set(ref _isCombatStatsEnabled, value)) return;
            CombatStatsChanged?.Invoke(value);
        }
    }
    private bool _isCombatStatsEnabled = true;

    /// <summary>Raised after <see cref="IsCombatStatsEnabled"/> changes. The host re-sizes the panel
    /// and the window, and writes the new value to mucka.ini.</summary>
    public event Action<bool>? CombatStatsChanged;

    /// <summary>Toggles the stat rows and hands focus back to the command box (Invariant #0).</summary>
    public void ToggleCombatStats()
    {
        IsCombatStatsEnabled = !IsCombatStatsEnabled;
        RequestFocus?.Invoke();
    }

    /// <summary>
    /// Raised once per combat event that earns a damage float, already resolved to the pane it
    /// belongs over. The host (GamePage) owns the pooled elements, the motion budget and the
    /// animation; this side owns only "what happened, and to whom".
    ///
    /// <para>An event rather than a property because a float is an OCCURRENCE, not a state. Every
    /// other thing on this rail is a readout that can be recomputed from
    /// <see cref="Live"/> at any moment; a float cannot - it is gone in a second and a half, and
    /// putting it in the frame state would have the 1 Hz refresh re-raise blows that already
    /// landed.</para>
    /// </summary>
    public event Action<RailFloat>? CombatFloatRaised;

    /// <summary>The tier driving the Combat Rail's single shared Composition glow layer. At most one
    /// T3 element runs at a time: only <see cref="CombatTier.T3"/> ever requests motion - the glow
    /// helper (PulseLayer) treats every other value as "stop".</summary>
    public CombatTier PulseTier => _pulseTier;

    /// <summary>Encumbrance tier for the load line's colour intensity: T1 below 75% of max
    /// effective strength, T2 below 50%. Computed unconditionally whenever there is anything on
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

    /// <summary>Supplies the accumulated per-creature incoming-damage record behind each opponent's
    /// "ever" figures. Call once at startup; the index fills itself off-thread (see
    /// MuckaConnection.LoadSwingDamageAsync) and is safe to read before it has.</summary>
    public void AttachSwingDamage(SwingDamageIndex index) => _swingDamage = index;

    /// <summary>Attaches the per-species stamina-pool index (see MudSharp.Combat.StaminaPoolIndex).
    /// Separate from <see cref="AttachSwingDamage"/> only because the two are read at different points
    /// - the damage index per roster row, this one once per encounter through the history cache.</summary>
    public void AttachStaminaPool(StaminaPoolIndex index) => _staminaPool = index;

    /// <summary>Attaches the per-species reach marks (see MudSharp.Combat.ReachMarkIndex) behind the
    /// live-opponent selection <see cref="IncomingPerBlowOf"/> projects the player's own prediction
    /// bands from (MudSharp.Combat.ReachAggregate.GreatestThreat). Unlike the swing-damage index this
    /// one folds the LIVE encounter's own blows in as they land and has no sample floor, which is
    /// exactly what a "largest blow seen so far" floor needs - the blow that just landed is part of
    /// the answer.</summary>
    public void AttachReachMarks(ReachMarkIndex index) => _reachMarks = index;

    public void OnInCombatChanged(bool inCombat)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (inCombat)
            {
                _combatStats.BeginEncounter(DateTime.UtcNow);
                _hasCombatData = true;
                // A fresh encounter is about to start filling _combatStats.Fights again, so its
                // endings are no longer represented anywhere until THIS encounter closes and folds
                // them in turn - see BuildDeadStripHistory.
                _currentEncounterArchived = false;
                // The dead strip's encounter-grouping ordinal (CombatEnding.EncounterOrdinal):
                // incremented once per encounter OPEN, never on close, so every ending this encounter
                // produces - whether recorded live in BuildDeadStripHistory's tail or later folded in
                // below - shares the one value assigned here.
                _encounterOrdinal++;
                // Discard any still-fading just-lost slice from the PREVIOUS encounter. CombatTracker.
                // Begin documents that MUD2 can close one encounter and open the next in the very same
                // frame, with no prompt between them - so without this, the last blow of a fight that
                // just ended could still be drawn tinted over the new encounter's first frames, marking
                // a loss that happened to a different fight against a ring that has already reset to
                // the new one's own stamina.
                _tickLoss.Reset();
                // The tick phase is deliberately NOT cleared here: a new fight does not re-learn the
                // phase from its own first swing. Re-deriving the lattice from one noisy sample was off
                // by more than 150 ms in 19% of encounters and by up to 963 ms at worst. MUD2's tick is
                // a server-side lattice that outlives any one fight; see Mucka.Combat.TickPhase for the
                // measurements.
            }
            else
            {
                _combatStats.EndEncounter();
                // Fold the finished encounter into the session tally BEFORE anything can clear it, so
                // dismissing the summary never costs the session totals. Snapshotted once and reused
                // for both folds below, so the tally and the archive describe the identical instant.
                var closingSnapshot = _combatStats.Snapshot(DateTime.UtcNow);
                _session = _session.Accumulate(closingSnapshot);
                // Freeze this encounter's own endings into the session-scoped dead-strip archive too.
                // Every fight here should already be resolved (the encounter just closed), but the
                // IsResolved guard matters the one time it is not - CombatEventKind.KilledByNpc force-
                // resolves every open fight before this fires, but a genuinely unmatched line could in
                // principle leave one Unresolved, and Unresolved is not an ending to record.
                //
                // Sorted by EndedUtc before appending (CombatEndingOrder.Sorted) -
                // closingSnapshot.Fights is in FIRST-ENGAGED order, not resolution order, and appending
                // it unsorted drew a creature engaged first above one engaged second but killed first,
                // moving the second creature's row when the first one died. This is the ONLY place a
                // batch is appended to the archive, so sorting here (rather than the whole archive on
                // every read) is enough to keep it globally ordered - every fight in THIS batch ended
                // during this encounter, which by wall-clock necessity is entirely before every fight in
                // any FUTURE batch, so appending one internally-sorted batch after another can never
                // require re-sorting what came before it.
                var newEndings = new List<CombatEnding>();
                foreach (var fight in closingSnapshot.Fights)
                {
                    if (fight.IsResolved)
                        newEndings.Add(EndingFor(fight, _encounterOrdinal, _resetOrdinal));
                }
                _endingArchive.AddRange(CombatEndingOrder.Sorted(newEndings));
                // A fresh immutable copy - see _archiveSnapshot's own remarks on why this is the ONLY
                // moment it may be rebuilt, and why nothing may ever publish _endingArchive itself.
                _archiveSnapshot = _endingArchive.ToArray();
                // The live-tail cache below describes a tail that no longer exists as of this fold (the
                // aggregator that produced it is about to be superseded), and it was built against the
                // OLD _archiveSnapshot - so it must not survive to be compared against a resolved count
                // from a future encounter that happens to match by coincidence. See its own remarks.
                _deadStripHistoryCachedResolvedCount = -1;
                _currentEncounterArchived = true;
            }

            _inCombat = inCombat;
            // A fresh InCombatChanged always clears grace: on true, ClogWriter's own
            // TailOnlyChanged(false) should already have fired (a new active entry ends any
            // tail-only state), and on false there is no tail yet - it only begins draining after
            // this Stop() returns. Redundant with that event most of the time; cheap insurance
            // against it lagging behind this transition on the UI thread.
            _isCombatGrace = false;
            // Unthrottled and direct: a combat start/end transition must never be swallowed by the
            // render gate, so it bypasses RequestRender and renders straight away, then tells the
            // gate a render just happened so its throttle window measures from THIS moment onward.
            var now = DateTime.UtcNow;
            RefreshCombatDisplay(now);
            _clogRenderGate.MarkRendered(now);
            OnPropertiesChanged(nameof(InCombat), nameof(IsCombatGracePeriod),
                nameof(CombatIconOpacity), nameof(CombatTip), nameof(CanClearCombatSummary));
        });
    }

    /// <summary>See <see cref="Mucka.Combat.ClogWriter.TailOnlyChanged"/> - flips true once the
    /// encounter has already closed (InCombat is already false) but its clog is still draining
    /// its tail, so the indicator can dim rather than snap straight back to idle.</summary>
    public void OnCombatGracePeriodChanged(bool isGrace)
        => MainThread.BeginInvokeOnMainThread(() =>
        {
            _isCombatGrace = isGrace;
            OnPropertiesChanged(nameof(IsCombatGracePeriod), nameof(CombatIconOpacity), nameof(CombatTip));
        });

    /// <summary>The tick boundary both instruments align to - the bar's zero crossing and the click's
    /// bracket - or null until the estimate has enough swings to be worth publishing.
    ///
    /// <para>An estimate over the session's accumulated swings. Fed from swing lines rather than the
    /// combat-start line: the combat-start line is the reply to the player's own <c>kill</c> command,
    /// so its phase is the keystroke's rather than the server's, while a swing line is emitted BY the
    /// tick. See <see cref="Mucka.Combat.TickPhase"/>: a single-sample anchor was off by over 150 ms in
    /// 19% of encounters and by up to 963 ms - half a tick - matching the symptom that the indicator
    /// did not seem to coincide with the server's combat tick.</para></summary>
    public DateTime? TickPhaseUtc => _tickPhase.Anchor;

    /// <summary>Whether the phase estimate is trusted enough to make a SOUND. The bar takes
    /// <see cref="TickPhaseUtc"/> as soon as it exists and accepts a visible correction; the click waits
    /// for this. See Mucka.Combat.TickPhase.SettledSamples - a briefly-wrong bar explains itself, a
    /// confidently-wrong click does not.</summary>
    public bool IsTickPhaseSettled => _tickPhase.IsSettled;

    /// <summary>Session-scoped, on purpose - it is not reset per encounter. Reset only belongs to a
    /// genuinely different lattice (another server), not to another fight.</summary>
    private readonly Mucka.Combat.TickPhase _tickPhase = new();

    private static bool IsSwing(CombatEventKind kind) => kind
        is CombatEventKind.Hit or CombatEventKind.Miss
        or CombatEventKind.HitByNpc or CombatEventKind.MissByNpc;

    public void OnCombatEvent(CombatEvent combatEvent)
        => MainThread.BeginInvokeOnMainThread(() =>
        {
            // EVERY swing refines the phase, not just the first one of an encounter. The estimator
            // decides when the anchor has actually moved enough to be worth republishing - each
            // republish restarts the bar's animation, so it must not happen per swing.
            if (IsSwing(combatEvent.Kind) && _tickPhase.Observe(combatEvent.TimestampUtc))
                OnPropertyChanged(nameof(TickPhaseUtc));
            _combatClearGeneration++;
            // Queued BEFORE the aggregator sees it, so the ending is waiting by the time the award line
            // that follows it arrives (KillAwardLedger). The event's own stamp is what the fight will
            // resolve with, so this key and the one AwardFor looks up with are the same by construction.
            // NpcFled joins Kill, showing what an NPC's flight was worth; NpcFleeFailed does not - the
            // creature is still in the room and nothing was scored.
            if (combatEvent.Kind is CombatEventKind.Kill or CombatEventKind.NpcFled
                && combatEvent.NpcName is { Length: > 0 } ended)
                _killAwards.NoteEnding(_encounterOrdinal, ended, combatEvent.TimestampUtc);
            if (combatEvent.Kind is CombatEventKind.YouFled or CombatEventKind.YouFleeFailed)
                OnPlayerFled(combatEvent.TimestampUtc);
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
            // AFTER the refresh, not before: a float is placed by roster INDEX, and the roster the
            // index means is the one the canvas last drew. The first blow on a creature that just
            // joined arrives in the same frame as its own FightStart, and that start is what opened
            // the render window this refresh used - so by here the newcomer already has a slot.
            RaiseCombatFloat(combatEvent);
        });

    public void OnStatsUpdated(GameStatsSnapshot stats)
        => MainThread.BeginInvokeOnMainThread(() =>
        {
            ObserveFloatStamina(stats.Stamina);
            // The game's own colour for the stamina figure. Sticky: a partial snapshot that carries no
            // colour must not reset it to "unknown" and flip the condition line to a default tone
            // mid-fight - MudSession.OnStatsUpdated carries the previous value forward for the same
            // reason.
            if (stats.StaminaColor is byte staColor)
                _staminaAnsiColor = staColor;
            // Every stamina reading, not just the ones inside a fight: the tracker's own gain rule is
            // what clears a stale slice, and it only sees a gain if it sees the reading.
            _tickLoss.Observe(stats.Stamina, DateTime.UtcNow);
            _combatStats.ObserveStamina(stats.Stamina);
            _combatDeficits = new CombatStatDeficits(
                StaminaCurrent: stats.Stamina,
                StaminaMax: stats.MaxStamina,
                // Carried weight is not here, and not anywhere - it is not captured, stored or shown
                // by this client at all. See GameLineAnalyzer's score-sheet branch for why. Effective
                // strength below still carries the real "you are loaded down" signal, because it rides
                // the FES heartbeat and already has the load priced in.
                ObjectsCarried: LiveObjectsCarried,
                // Absolute effective/max strength, for the Combat Rail's encumbrance tier, which needs
                // fraction-of-max. The dexterity pair beside it went with the deltas: nothing read it.
                StrengthEffective: stats.Strength,
                StrengthMax: stats.MaxStrength,
                // MUD2's own input to what leaving costs - see MudSharp.Combat.FleeWorth.
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

    // -- Damage floats -----------------------------------------------------------------------
    // The rail's one deliberate piece of motion: a small figure that rises off the pane an event
    // belongs to and is gone in about a second and a half. Everything about WHAT it may say is
    // decided here; everything about how it moves is GamePage's (RailFloatLayer) and everything
    // about how many may move at once is RailFloatBudget's.

    /// <summary>
    /// This feature's own stamina baseline, used for two things the frame state cannot answer:
    /// exactly how much a blow took, and whether stamina has gone UP since the last reading.
    ///
    /// <para>A private instance, which is <see cref="StaminaDeltaRelay"/>'s documented pattern
    /// rather than a shortcut - SwingLedger, FightHistoryRecorder and CombatStatsAggregator each
    /// hold their own for the same reason. The relay exists because MUD2's "The zombie hits you
    /// (95/100)." line is parsed twice: the generic stats scan fires first and moves the baseline,
    /// then the combat tracker's own regex arrives with the same 95, so without stashing the value
    /// held immediately before the first parse every delta computes to zero. Feeding it from the
    /// same two methods that feed the aggregator's, in the same order, is what keeps the two
    /// baselines identical.</para>
    /// </summary>
    private readonly StaminaDeltaRelay _floatStamina = new();

    /// <summary>
    /// One stamina reading, and the deduced GAIN if it went up.
    ///
    /// <para><b>Heals are deduced, not announced.</b> MUD2 prints no "+3 health" line anywhere; the
    /// only way to learn stamina is for a reading to print. So a "+14" here is the sum of everything
    /// that happened since the previous reading - natural regen, food, a spell, in unknown
    /// proportions - reported at the moment it was observed. It is deliberately NOT tick-aligned and
    /// deliberately not split into per-tick increments: nothing on the wire says when in the gap it
    /// happened, and manufacturing a schedule for it would be the panel inventing a mechanism.</para>
    ///
    /// <para>In combat only. Out of combat stamina regenerates continuously and every heartbeat
    /// would raise one, which is precisely the ambient motion the budget exists to prevent - and a
    /// number drifting up the panel in the tea room is not news.</para>
    /// </summary>
    private void ObserveFloatStamina(int? stamina)
    {
        if (stamina is null)
            return;

        var previous = _floatStamina.LastKnown;
        _floatStamina.Observe(stamina);

        if (previous is not int was || stamina.Value <= was)
            return;
        if (!_live.InCombat)
            return;

        RaisePlayerFloat(RailFloatKind.StaminaGain, RailFloatText.Gain(stamina.Value - was), DateTime.UtcNow);
    }

    /// <summary>
    /// Turns one classified combat line into a float, or into nothing.
    ///
    /// <para>Only the four swing kinds qualify. Everything else the rail reports - health phrases,
    /// weapon changes, fight ends - is a state the canvas already draws in place, and a rising copy
    /// of it would be the second telling of the same fact.</para>
    ///
    /// <para><b>NPC heals are not rendered at all.</b> Creatures demonstrably regenerate, but MUD2
    /// emits no signal for it, so there is nothing to report and no honest number to report it
    /// with.</para>
    /// </summary>
    private void RaiseCombatFloat(CombatEvent combatEvent)
    {
        switch (combatEvent.Kind)
        {
            case CombatEventKind.Hit:
                // The BRACKET the game printed, never a midpoint - see RailFloatText for why the
                // outgoing and incoming sides are formatted differently on purpose.
                RaiseOpponentFloat(combatEvent, RailFloatKind.OutgoingHit,
                    RailFloatText.Outgoing(combatEvent.RangeLow, combatEvent.RangeHigh));
                break;

            case CombatEventKind.Miss:
                RaiseOpponentFloat(combatEvent, RailFloatKind.OutgoingMiss, RailFloatText.Miss);
                break;

            case CombatEventKind.HitByNpc:
            {
                // Resolved unconditionally, even with floats switched off: ResolveDelta consumes the
                // relay's one-shot stash, and skipping it would leave a stale pre-update value to be
                // paired with an unrelated later reading.
                var (delta, _) = _floatStamina.ResolveDelta(combatEvent.RangeLow);
                // Null on the killing blow, which prints bare because no stamina survives it. No
                // number is the honest rendering of no reading; a "-0" would be a measurement.
                if (delta is int taken && taken > 0)
                    RaisePlayerFloat(RailFloatKind.IncomingHit, RailFloatText.Incoming(taken),
                        combatEvent.TimestampUtc);
                break;
            }

            case CombatEventKind.MissByNpc:
                RaisePlayerFloat(RailFloatKind.IncomingMiss, RailFloatText.Miss, combatEvent.TimestampUtc);
                break;
        }
    }

    /// <summary>
    /// Raises a float pinned to the creature the line named, resolving it to its row in the roster
    /// the rail is currently drawing.
    ///
    /// <para>Dropped silently when the name is not on that roster. That is the honest outcome:
    /// without a row there is no pane, and defaulting to a nearby slot would attribute the blow to
    /// whatever creature happens to be sitting there.</para>
    /// </summary>
    private void RaiseOpponentFloat(CombatEvent combatEvent, RailFloatKind kind, string? text)
    {
        if (text is null || !_isCombatFloatsEnabled || CombatFloatRaised is null)
            return;
        if (combatEvent.NpcName is not { Length: > 0 } name)
            return;

        var rows = _live.Roster.Rows;
        for (var i = 0; i < rows.Count; i++)
        {
            // OrdinalIgnoreCase, matching CombatStatsAggregator's own fight dictionary - the roster
            // row's Name IS the tracker's NpcName, so this is an exact lookup and not a fuzzy one.
            if (!string.Equals(rows[i].Name, name, StringComparison.OrdinalIgnoreCase))
                continue;
            // LiveCount, not rows.Count and not TotalCount: only LIVE opponents get a vertical slot
            // now - the resolved ones are in the top-anchored dead strip and have no pane to float
            // over - and the overflow row is surrendered on that same count, which can exceed the
            // roster's own row cap. A float placed against either other number would land on a slot
            // the canvas gave to something else.
            CombatFloatRaised.Invoke(new RailFloat(
                kind, text, i, _live.Roster.LiveCount, combatEvent.TimestampUtc));
            return;
        }
    }

    private void RaisePlayerFloat(RailFloatKind kind, string text, DateTime atUtc)
    {
        if (!_isCombatFloatsEnabled || CombatFloatRaised is null)
            return;
        CombatFloatRaised.Invoke(new RailFloat(kind, text, RailFloat.PlayerAnchor, 0, atUtc));
    }

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
    /// window's clear button; the summary persists until dismissed rather than self-erasing.</summary>
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
        // The Here list's second signal: which of the names on the floor are currently being fought.
        // Done here rather than off the FEI beat because a fight opens and closes on its own clock,
        // and the icon has to follow the fight, not the heartbeat.
        if (_engagedNpcs.Update(snapshot.Fights))
            ReclassifyRoomEntries();
        var history = _hasCombatData ? ResolveHistory(snapshot) : CombatHistoryContext.Empty;
        // Compose the live frame state. The previous implementation built a list of styled text
        // lines here and diffed it before publishing, because publishing rebuilt a native
        // FormattedString - one WinUI Run per span, a full teardown-and-remeasure on the UI thread.
        // That surface is gone: the canvas draws this state directly, so the only cost of a publish
        // is an invalidate. Rate limiting is still enforced upstream by _clogRenderGate, which is
        // what actually protects Invariant #1 here.
        RefreshCombatSignals(snapshot, _combatDeficits, history, nowUtc);

        OnPropertiesChanged(nameof(HasCombatData), nameof(NoCombatData),
            nameof(PulseTier),
            nameof(EncumbranceTier), nameof(Live));
    }

    /// <summary>
    /// Computes the Combat Rail's own content: the threat indicator, the plain-language "why" line,
    /// and the tier driving the shared pulse layer. Kept separate from
    /// <c>CombatHistoryFormatter</c>'s content (survivability, participants, exchange, history
    /// comparison, weapon table, session totals), which is salvaged as-is; this is the new layer
    /// built on top of it.
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
        // ignore a healthy player will attack, one blow from most creatures can kill, and fleeing
        // still costs real points. Walking away from a fight at 22 stamina and forgetting about it is
        // a way to lose a character between fights.
        //
        // 25 rather than 20: chosen as a margin close enough to the survival threshold to matter with
        // a little room before it.
        var vulnerable = deficits.StaminaCurrent is int sta && sta <= OutOfCombatVulnerableStamina
            ? CombatTier.T3
            : CombatTier.None;

        if (!snapshot.HasEncounter)
        {
            _pulseTier = vulnerable;
            // The dead strip is session-scoped and must survive dismissing the encounter summary -
            // ClearCombatSummaryCommand leaves the session totals, and the strip is part of that, not
            // part of the per-encounter readout being wiped. CombatLiveView.Idle alone blanks it
            // (DeadStripHistory defaults to empty), so HasEncounter is set true here whenever there IS
            // session history to show. CombatRailView.DrawOpponents gates the WHOLE
            // opponent-slot/dead-strip region on that one flag, and with an empty Roster
            // (RosterPlan.Empty, from CombatLiveView.Idle) the live-slot loop draws nothing regardless
            // of it - so this only ever re-enables the dead strip, never the live stack.
            _live = _archiveSnapshot.Count == 0
                ? CombatLiveView.Idle
                : CombatLiveView.Idle with { HasEncounter = true, DeadStripHistory = _archiveSnapshot };
            return;
        }

        // IN COMBAT ONLY. A weapon is a property of the ENCOUNTER, not of the player: MUD2 has no
        // equipment slots and no persistent wield - one is named for the current fight, or as part of
        // starting it ("kill x with y"), and when that fight ends nothing is held. So between fights
        // there is no weapon to report, not an old one worth remembering.
        var liveWeapon = snapshot.InCombat ? snapshot.CurrentWeapon : null;
        var hasWeapon = !string.IsNullOrWhiteSpace(liveWeapon);
        // Empty rather than "UNARMED" for the bare-handed case: the tile draws that word off
        // CombatLiveView.IsUnarmed, which knows whether a fight is running, and this string is only
        // ever the NAME of something held.
        var weaponText = hasWeapon ? CombatComposition.DisplayName(liveWeapon) : string.Empty;
        // Roster/weapon/duration context is worth showing whenever an encounter exists at all, live
        // or just-finished - mirrors CombatComposition.Build's own AppendHeadline/AppendParticipants,
        // which never gated on InCombat either.
        //
        // Built AFTER the weapon is known, because each participant's novelty is asked twice - once
        // bare, once against what is actually in hand - and the second question has no answer until
        // the line above has run.
        var facts = ToParticipantFacts(snapshot.Fights, nowUtc, liveWeapon);
        var roster = ParticipantRoster.Build(facts);
        // The weapon's own mark: the worst thing the corpus says about this weapon against anything
        // still engaged. Over the FACTS, not the roster rows, so a pack past the row cap still counts.
        var weaponNovelty = CombatNovelty.WeaponRollup(facts);
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
        var deadStripHistory = BuildDeadStripHistory(snapshot);

        if (!snapshot.InCombat)
        {
            // Post-combat / grace window: only the survival PROJECTION (threat/flee) goes quiet -
            // projecting a finished fight's death clock would be a lie. The roster and weapon/duration
            // context stay, exactly as the old formatter's headline/participant rows did.
            _pulseTier = vulnerable;
            _live = new CombatLiveView(
                InCombat: false, HasEncounter: true, WeaponText: weaponText,
                // FALSE out of combat, whatever the player is holding. MUD2 has no persistent notion
                // of being armed - a weapon is named for the CURRENT fight and stops being wielded
                // when that fight ends. So "unarmed" is not a state the player can be in between
                // fights; it is the only state, and an alarm about it would fire from the end of every
                // fight until the start of the next one.
                //
                // This flag therefore means "unarmed IN A FIGHT", which is the only thing it can
                // usefully mean; !hasWeapon would fire falsely for the common case where the
                // post-combat branch resolves a weapon from the fight that just ended.
                IsUnarmed: false,
                Roster: roster,
                StaminaCurrent: deficits.StaminaCurrent, StaminaMax: deficits.StaminaMax,
                ObjectsCarried: deficits.ObjectsCarried, Score: deficits.Score,
                DeadStripHistory: deadStripHistory,
                MagicCurrent: deficits.MagicCurrent, MagicMax: deficits.MagicMax,
                AltWeapon: altWeapon,
                // Rolls up to None on its own here - WeaponRollup skips resolved fights, and outside
                // combat every fight in the encounter is resolved. Passed rather than omitted so the
                // two construction sites stay readable as the same record.
                WeaponNovelty: weaponNovelty);
            return;
        }

        var primary = CombatComposition.PrimaryFight(snapshot);
        var outlook = CombatComposition.ComputeOutlook(snapshot, deficits, history, primary);

        // Incoming per-hit rate this fight - thin-sample gated (MinimumOwnHits) the same way the old
        // ladder's own risk pairing gated it, reused here for the tier table's "hits-left" trigger
        // too, so the threat indicator and the tier table never quietly disagree about "how
        // close is this fight".
        double? incomingPerHit = primary is { TheyHits: > 0 } f ? f.ApproxDamageTaken / f.TheyHits : null;
        int? hitsLeft = incomingPerHit is double rate && rate > 0
            && deficits.StaminaCurrent is int sta1 && primary!.TheyHits >= CombatOutlook.MinimumOwnHits
            ? (int)Math.Ceiling(sta1 / rate)
            : null;

        // The encounter table's one coloured cell, off the SAME outlook the tier resolver below reads.
        // Two consumers of one projection, which is what ComputeOutlook was extracted for; what must
        // never happen again is a second ladder derived from the raw seconds beside it.
        var survival = Survival.Read(outlook, CombatTiming.TickMilliseconds);

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

        // The flee pill computes no flee-cost figure and publishes no price; its loudest state is an
        // alarm about the cheap band, not a report of a cost. One accidental flee from a zombie at
        // 90/100 stamina cost 1300 of 13,000 points and a level, so the player already knows fleeing
        // is expensive. What the panel owes them is the zone signal (staminaTier, above) and a valid
        // direction to run, not a price tag to read while deciding.

        var incomingPerBlow = IncomingPerBlowOf(roster);

        _live = new CombatLiveView(
            InCombat: true, HasEncounter: true, WeaponText: weaponText, IsUnarmed: !hasWeapon,
            Roster: roster,
            StaminaCurrent: deficits.StaminaCurrent, StaminaMax: deficits.StaminaMax,
            ObjectsCarried: deficits.ObjectsCarried, Score: deficits.Score,
            DeadStripHistory: deadStripHistory,
            MagicCurrent: deficits.MagicCurrent, MagicMax: deficits.MagicMax,
            AltWeapon: altWeapon,
            // The flee pill. Fed hitsLeft rather than the resolved tier, so it agrees with the count
            // that already overrides the whole-panel glow instead of deriving a second opinion from the
            // same inputs.
            //
            // Not gated on the grace window here: the grace flag changes without the frame state being
            // rebuilt, so folding it in would leave it stale exactly when it matters. The renderer and
            // the pulse layer both apply that gate themselves, as the tick meter already does.
            FleePill: FleePillResolver.Resolve(
                inCombat: true, deficits.StaminaCurrent,
                FleePillResolver.WorstCaseTickDamage(roster), hitsLeft),
            // The incoming half of the border language, pooled over everything still swinging at the
            // player. See CombatLiveView.IncomingTempo for why it is a ratio of sums.
            IncomingTempo: IncomingTempoOf(snapshot),
            WeaponNovelty: weaponNovelty,
            // The player's own prediction bands, off the ONE creature ReachAggregate.GreatestThreat
            // names (see IncomingPerBlowOf) - never a pack summed together. See
            // DamagePrediction.IncomingPerBlow.
            YourNextBlow: DamagePrediction.PlayerAfterBlows(
                1, deficits.StaminaCurrent, deficits.StaminaMax, incomingPerBlow),
            YourBlowAfter: DamagePrediction.PlayerAfterBlows(
                2, deficits.StaminaCurrent, deficits.StaminaMax, incomingPerBlow),
            StaminaLostLastTick: _tickLoss.LostThisTick,
            // Only meaningful alongside a nonzero LostThisTick - see CombatLiveView.StaminaLossUtc.
            StaminaLossUtc: _tickLoss.LostThisTick > 0 ? _tickLoss.LastLossUtc : null,
            // The encounter table. Par counts what is still up; Op counts everything this encounter
            // has produced, which is why a fight that has killed four of five reads "1" and "5".
            PlayerName: _personaName,
            StaminaAnsiColor: _staminaAnsiColor,
            YourDealt: EncounterLine(snapshot, outgoing: true),
            YourTaken: EncounterLine(snapshot, outgoing: false),
            YourExchange: snapshot.Exchange,
            // One reading, resolved here off the same outlook the survivability line uses. The phase
            // rides with it rather than being sampled at paint time, so the 1 Hz flush that already
            // runs through a fight is what makes a blink visible - and it only alternates while the
            // reading is Dire, so nothing republishes once a second for a blink nobody is drawing.
            Survival: survival,
            BlinkOn: survival == MudSharp.Combat.SurvivalReading.Dire
                && Mucka.Combat.Blink.PhaseOn(nowUtc),
            // Unarmed AND below maximum - an unarmed opening is normal and must not raise an alarm.
            BlinkOffPhase: !hasWeapon
                && deficits.StaminaCurrent is int sta2 && deficits.StaminaMax is int max2 && sta2 < max2
                && Mucka.Combat.Blink.PhaseOn(nowUtc, inverted: true),
            LiveOpponents: roster.LiveCount,
            OpponentsFaced: roster.TotalCount,
            // Duration comes off the encounter, not the primary fight: a fight that started when the
            // third creature joined has been going a fraction of the time the player has been in
            // trouble, and the table is about the encounter.
            EncounterTicks: EncounterTicksOf(snapshot),
            // Both null until the projection will commit. Same instrument the survivability line uses
            // (CombatComposition.ComputeOutlook), converted to ticks in this one place.
            TicksToVictory: TicksFromSeconds(outlook.SecondsToKill),
            TicksToDeath: TicksFromSeconds(outlook.SecondsToDie),
            // The player's name emphasis, off the same instant the ring's just-lost slice fades from -
            // TickStaminaLoss already groups a tick's blows into one burst with one arrival time, which
            // is exactly the granularity this cue wants. Gated on there being a loss at all, for the
            // same reason StaminaLossUtc is: LastLossUtc is DateTime.MinValue until something lands,
            // and a default that reads as "damage in 1 AD" is not a timestamp.
            PlayerTookDamageThisTick: _tickLoss.LostThisTick > 0
                && TickDamageEmphasis.IsOn(_tickLoss.LastLossUtc, nowUtc, _tickPhase.Anchor));
    }

    /// <summary>
    /// The Combat Rail dead strip's whole session history, chronological by EndedUtc (see
    /// <see cref="CombatEndingOrder.Sorted"/>): every prior encounter's endings, already frozen into
    /// <see cref="_endingArchive"/>/<see cref="_archiveSnapshot"/> at the encounter close in
    /// <see cref="OnInCombatChanged"/>, plus the CURRENT encounter's own endings as they land.
    ///
    /// <para><b>Read straight off <paramref name="snapshot"/>'s fights, not off the roster.</b>
    /// <c>ParticipantRoster.Build</c> caps its row list at <c>ParticipantRoster.MaxRows</c> for the
    /// live slot stack's sake - a limit that exists to bound the vertical badges the canvas draws at
    /// once, and has nothing to do with how many endings a pack fight produced. <c>snapshot.Fights</c>
    /// (via <c>CombatStatsAggregator.Fights</c>) is unbounded, so a fourteen-rat pack records all
    /// fourteen endings here even though only eight of them ever had a roster row - the OLD strip did
    /// not lose the other six either (they were summarised via the roster's own hidden-count), but it
    /// could only ever say how many, never which. This can say which.</para>
    ///
    /// <para><b>Why this cannot just always read <c>snapshot.Fights</c> and skip the archive.</b>
    /// <c>_combatStats</c> is not cleared at encounter close - only at the NEXT
    /// <c>BeginEncounter</c> - so between the fold and the next fight starting, <c>snapshot.Fights</c>
    /// still answers with exactly the endings that were just archived. <see cref="_currentEncounterArchived"/>
    /// is the flag that says which side of that fold the caller is on, so an ending is counted
    /// exactly once.</para>
    ///
    /// <para><b>Cached against the resolved-fight count.</b> This runs on every combat
    /// event, every FES heartbeat and every 1 Hz tick once anything in the encounter has resolved
    /// (Invariant #1) - mirroring <see cref="_historyCache"/> below. The resolved count is a valid
    /// dirty check (see <see cref="_deadStripHistoryCache"/>'s own remarks), so the steady state - no
    /// new resolution since the last call - returns the SAME cached list rather than re-sorting and
    /// re-copying the whole thing, and the returned list is never the live <c>_endingArchive</c>/tail
    /// working storage, so it stays safe to publish into a record documented as immutable after
    /// publish.</para>
    /// </summary>
    private IReadOnlyList<CombatEnding> BuildDeadStripHistory(CombatEncounterSnapshot snapshot)
    {
        var resolvedThisEncounter = 0;
        if (!_currentEncounterArchived)
        {
            foreach (var fight in snapshot.Fights)
            {
                if (fight.IsResolved)
                    resolvedThisEncounter++;
            }
        }

        if (resolvedThisEncounter == _deadStripHistoryCachedResolvedCount)
            return _deadStripHistoryCache;

        // The ARCHIVE goes through the award fill too, which is the whole fix for the last kill of an
        // encounter. That kill closes the encounter, so the archive fold runs and freezes its row -
        // and MUD2 prints the award on the NEXT line, after the fold. Returning the frozen snapshot
        // verbatim meant the final creature of every encounter could never show what it paid, while
        // every earlier one could, because those rows are still being rebuilt from the live tail.
        //
        // So an ending records what was observed when the fight ended, and the award is looked up at
        // read time from the one place awards live. Nothing is patched after the fact.
        if (resolvedThisEncounter == 0)
        {
            var archived = new List<CombatEnding>(_archiveSnapshot);
            FillAwards(archived);
            _deadStripHistoryCache = archived;
            _deadStripHistoryCachedResolvedCount = 0;
            return _deadStripHistoryCache;
        }

        var tail = new List<CombatEnding>(resolvedThisEncounter);
        foreach (var fight in snapshot.Fights)
        {
            if (fight.IsResolved)
                tail.Add(EndingFor(fight, _encounterOrdinal, _resetOrdinal));
        }

        var combined = new List<CombatEnding>(_archiveSnapshot.Count + tail.Count);
        combined.AddRange(_archiveSnapshot);
        // Sorted (CombatEndingOrder.Sorted): snapshot.Fights is in first-engaged order, not
        // resolution order - see CombatEndingOrder's own remarks. _archiveSnapshot above needs no re-sort: it
        // is already chronological, and every one of its timestamps precedes every timestamp in THIS
        // still-open encounter by wall-clock necessity.
        combined.AddRange(CombatEndingOrder.Sorted(tail));
        FillAwards(combined);

        _deadStripHistoryCache = combined;
        _deadStripHistoryCachedResolvedCount = resolvedThisEncounter;
        return _deadStripHistoryCache;
    }

    /// <summary>
    /// Resolves each row's award from <see cref="_killAwards"/>, in place.
    ///
    /// <para>Called on every rebuild rather than at fold time because the ENCOUNTER FOLD RUNS
    /// MID-FRAME: the ending line drops the fight count to zero, which closes the encounter and freezes
    /// its rows, and the award is still a line or two further into that same frame - certain to arrive,
    /// and certain to arrive AFTER the fold. Resolving at read time is what makes the fold's timing not
    /// matter.</para>
    ///
    /// <para>Rebuilds are cached and invalidated when an award lands, so this walks the list once per
    /// award rather than once per paint.</para>
    /// </summary>
    private void FillAwards(List<CombatEnding> endings)
    {
        // Flights already charged on this walk. A player's flee ends EVERY active fight in the
        // encounter, so a pack flee produces several rows sharing one timestamp - and MUD2 charged for
        // it once. Printing the figure on each would read as three separate deductions and invite
        // summing them, so it goes on the first row of the group and the rest stay blank. Blank is
        // already what "no announcement" looks like here, and the rows are visibly one flee.
        HashSet<(int Encounter, long AtTicks)>? charged = null;

        for (var i = 0; i < endings.Count; i++)
        {
            var ending = endings[i];
            var isFlight = ending.Outcome is FightOutcome.UFled or FightOutcome.UFledFail;

            if (ending.ScoreAwarded is not null)
            {
                // A row filled on an earlier walk still holds the group's charge, so it has to be
                // counted or the second row would take a duplicate on the next rebuild.
                if (isFlight && ending.EndedUtc is DateTime held)
                    (charged ??= []).Add((ending.EncounterOrdinal, held.Ticks));
                continue;
            }

            if (isFlight)
            {
                if (ending.EndedUtc is not DateTime ended
                    || !(charged ??= []).Add((ending.EncounterOrdinal, ended.Ticks)))
                    continue;
                // NEGATIVE: what leaving took, not what it gave. See DrawDeadStrip for the tone.
                if (_fleeCharges.ChargeFor(ending.EncounterOrdinal, ended) is int points)
                    endings[i] = ending with { ScoreAwarded = -points };
                continue;
            }

            if (AwardFor(ending.EncounterOrdinal, ending.Name, ending.EndedUtc) is int award)
                endings[i] = ending with { ScoreAwarded = award };
        }
    }

    /// <summary>
    /// What one incoming blow takes, from the creature <see cref="ReachAggregate.GreatestThreat"/>
    /// names - the greatest measured single-blow reach among the live rows, tie-broken on damage
    /// actually taken.
    ///
    /// <para>One creature, never the pack summed. Falls back to the first live row when nothing has a
    /// reach mark yet, so the bands appear as soon as anything has swung rather than waiting for the
    /// threat ranking to have evidence.</para>
    /// </summary>
    private static DamageBracket? IncomingPerBlowOf(RosterPlan roster)
    {
        var index = ReachAggregate.GreatestThreat(roster);
        if (index < 0)
        {
            for (var i = 0; i < roster.Rows.Count; i++)
            {
                if (roster.Rows[i].IsLive)
                {
                    index = i;
                    break;
                }
            }
        }

        if (index < 0)
            return null;

        var row = roster.Rows[index];
        return DamagePrediction.IncomingPerBlow(row.FightDamage, row.EverDamage);
    }

    /// <summary>
    /// Every live opponent's swings at the player this encounter, pooled into one tempo.
    ///
    /// <para>Hits and misses are ADDED, so the resulting rate is a ratio of sums. An average of the
    /// per-creature rates would weight a rat that has swung twice the same as an ogre that has swung
    /// forty times, which is the shape of aggregate error this panel has been bitten by before.</para>
    ///
    /// <para>Live participants only: a creature that is already dead is not part of what is coming at
    /// the player, and leaving its swings in would keep the border reading busy after a pack was
    /// cleared.</para>
    /// </summary>
    private static SwingTempo IncomingTempoOf(CombatEncounterSnapshot snapshot)
    {
        var tempo = SwingTempo.None;
        foreach (var fight in snapshot.Fights)
        {
            if (!fight.IsResolved)
                tempo = tempo.Plus(new SwingTempo(fight.TheyHits, fight.TheyMisses));
        }
        return tempo;
    }

    /// <summary>One creature's remaining-stamina band: the species estimate, less this fight's damage
    /// brackets, narrowed by the live health rung and by any `diagnose` reading. Null when nothing at
    /// all supports one - a band with no evidence is not a band.
    ///
    /// <para>Absolute, and it never leaves this file. Its only consumer is NpcVitality.Estimate, which
    /// divides it by the pool to place the seal's fill inside the rung the game printed; no figure it
    /// produces is ever drawn.</para></summary>
    private static NpcStaminaBand? RemainingFor(StaminaPoolEstimate pool, FightSnapshot fight)
    {
        var band = NpcRemainingStamina.Compute(
            pool, fight.YourDamage, fight.RungAnchor, fight.StaminaReading);
        return band.HasEvidence ? band : null;
    }

    /// <summary>Maps the app-side <see cref="FightSnapshot"/> list down to the plain, MAUI-independent
    /// facts <see cref="ParticipantRoster.Build"/> needs - that class lives in mudsharp (no MAUI
    /// dependency, directly unit-testable), so it cannot reference <see cref="FightSnapshot"/>
    /// itself.</summary>
    /// <summary>An instance method rather than static purely so it can reach <see cref="_swingDamage"/>
    /// - the "ever" figures are a per-participant fact and belong to the participant, exactly as the
    /// NPC's own weapon does, so this is the one place that can attach them without the roster or the
    /// renderer having to know a store exists.</summary>
    private IReadOnlyList<ParticipantFact> ToParticipantFacts(
        IReadOnlyList<FightSnapshot> fights, DateTime nowUtc, string? currentWeapon)
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
            // Null (not StaminaPoolEstimate.None) with no index attached, so the narrowing step can
            // tell "no pool index in this context" from "an index with nothing on file for this
            // creature" - the second is a real answer about a species and the first is not an answer
            // at all. Either way the rung's own seventh still stands on its own.
            var pool = _staminaPool?.Lookup(fight.NpcName);
            // The seal's fill, per participant rather than for the primary target alone - the whole
            // point of a seal per slot is that a pack can be read row against row, which a figure only
            // the current target carries cannot support.
            //
            // A FRACTION, not a stamina figure. The rung is the source (it is what MUD2 actually
            // printed and what the player themselves read); the pool estimate and this fight's damage
            // brackets only narrow the position inside that seventh, and no absolute number they
            // produce reaches the screen. Both probes are dictionary lookups under a lock held for
            // exactly that probe (StaminaPoolIndex's own remarks), run for at most MaxRows opponents
            // per refresh.
            var vitality = NpcVitality.Estimate(
                fight.HealthRung, pool, pool is null ? null : RemainingFor(pool, fight),
                // The crossing is the sharpest constraint of the three on a large creature, and it needs
                // this fight's cumulative bracket because on a first encounter that is the only floor
                // under the pool there is.
                fight.RungCrossing, fight.YourDamage,
                // "full of life" / "full of energy" is cur == max exactly, not merely the top band, so
                // it is the one reading that can fill the seal. Without it the hard fill tops out at
                // the rung floor - 6/7 - and an untouched creature never draws full.
                atMax: fight.HealthPhrase is { } phrase && NpcHealthRungs.IsAtMax(phrase));
            // One probe, three answers. Narrowed by the creature's CURRENT weapon so the armed-as-now
            // profile comes back alongside the species-wide one; both are dictionary lookups under a
            // single lock (SwingDamageIndex's own remarks), and taking them together rather than in
            // two calls halves the locking on a path that runs once per opponent per refresh.
            var damage = _swingDamage?.Lookup(fight.NpcName, fight.NpcWeapon) ?? OpponentDamage.Empty;
            var perBlow = DamagePrediction.PerBlow(
                fight.YourDamage, fight.YouHits, damage.Outgoing);
            // (None, None) with no store attached (unit/design contexts): no corpus means no evidence
            // either way, and "unfought" is a claim about the corpus rather than the absence of one.
            var novelty = _fightHistory?.NoveltyFor(fight.NpcName, currentWeapon)
                ?? (NoveltyMark.None, NoveltyMark.None);

            facts[i] = new ParticipantFact(
                fight.NpcName, fight.IsResolved, fight.Outcome,
                fight.HealthRung, fight.HealthPhrase, healthAge, fight.ApproxDamageTaken,
                fight.NpcWeapon,
                fight.TheirDamage,
                // Empty (which draws as nothing) whenever the cache is absent or has too few blows on
                // file - never a zero, which would read as "this thing cannot hurt you". Only the
                // incoming half reaches the rail today; the outgoing brackets are cached alongside it
                // for the exchange bars and the analysis view, which want both sides.
                // BestIncoming, not Incoming: narrowed to the weapon this creature is actually holding
                // when enough blows have been seen through it. This feeds the player's own prediction
                // bands and the flee readout, which is exactly where the sharper number belongs.
                damage.BestIncoming,
                vitality,
                // rDPT: where the next two landed blows put this creature's boundary, as intervals off
                // the player's own damage brackets. This fight's bracket is preferred over history so a
                // weapon swap, a dropped load or a magic buff moves the bands on the very next blow -
                // nothing here latches. See MudSharp.Combat.DamagePrediction for what each evidence
                // state buys and why this replaced a time-based forecast.
                DamagePrediction.AfterBlows(1, vitality, pool, perBlow, fight.YourDamage, fight.RungCrossing),
                DamagePrediction.AfterBlows(2, vitality, pool, perBlow, fight.YourDamage, fight.RungCrossing),
                new SwingTempo(fight.YouHits, fight.YouMisses),
                // The reach mark is per SPECIES and includes this encounter's own blows, so it is the
                // only figure on this row that can already know about the hit that landed a second ago.
                _reachMarks?.Lookup(fight.NpcName) ?? ReachMark.None,
                // Novelty: has this creature's KIND ever been fought, and ever killed - once bare, once
                // narrowed to the weapon in hand. Two dictionary probes under one lock, per row.
                //
                // The index holds only CLOSED, flushed fights (HistoryIndex's own remarks), so the
                // encounter on screen cannot enter its own answer. That is the property that makes the
                // marks hold still: a creature met for the first time stays orange for the whole of the
                // fight that is teaching you about it, and only stops being new on the NEXT one.
                novelty.Name, novelty.Weapon,
                // The diagnose probe, carried verbatim so the slot can show what the game actually
                // printed. Kept for the rest of the fight rather than expiring - see
                // RosterRow.StaleAfterSeconds for why silence corroborates a reading here.
                fight.StaminaReading,
                fight.Value,
                DealtLine(fight),
                TakenLine(fight),
                fight.Exchange,
                // The name's emphasis. HealthReadUtc is the arrival of a WOUND DESCRIPTOR, and MUD2
                // prints one after every landed blow that does not kill - 3,559 descriptors against
                // 3,561 such hits across 1,197 fights - so it is the sharpest "this creature just took
                // damage" instant the client has. `diagnose` does not touch it (that is
                // FightAccumulator.NoteStaminaRead), so a probe cannot fake a blow. What it does
                // include is damage the player did not deal: NPC-versus-NPC combat is in the corpus,
                // and a creature being hurt by something else is still a creature being hurt, which is
                // what the cue claims.
                TickDamageEmphasis.IsOn(fight.HealthReadUtc, nowUtc, _tickPhase.Anchor));
        }
        return facts;
    }

    /// <summary>How many combat ticks this fight has been running, as a real number. Wall clock over
    /// the measured 2000 ms tick, NOT a count of swings: roughly half the ticks an engaged creature is
    /// present for carry no swing at all (DamagePrediction's own remarks), so swings would understate
    /// the elapsed time by about half and double every rate built on it.</summary>
    private static double TicksElapsed(TimeSpan duration)
        => duration.TotalMilliseconds / CombatTiming.TickMilliseconds;

    /// <summary>Seconds into ticks, or null straight through. Null is the whole point: CombatOutlook
    /// returns null for "not enough evidence to project", and turning that into a zero here would put
    /// a confident "0t to death" on the table at the exact moment the projection was refusing to make
    /// one. Permadeath game; that particular zero is the worst lie on the panel.</summary>
    private static double? TicksFromSeconds(double? seconds)
        => seconds is double value ? value * 1000.0 / CombatTiming.TickMilliseconds : null;

    /// <summary>
    /// The player's own stat row, pooled over every fight in the encounter.
    ///
    /// <para>Folded from the per-fight figures rather than kept as a second running tally, so the
    /// player's row and the opponents' rows cannot disagree: a total is a sum, the extremes are the
    /// extremes, and the mean comes from the pooled numerator and the pooled denominator rather than
    /// from averaging averages (which would weight a creature hit twice like one hit forty times).</para>
    ///
    /// <para>The rate divides by the ENCOUNTER's duration, not the sum of the fights': three
    /// creatures swinging at once for ten ticks is ten ticks of trouble, not thirty.</para>
    /// </summary>
    private static ExchangeLine EncounterLine(CombatEncounterSnapshot snapshot, bool outgoing)
    {
        var samples = 0;
        var min = 0.0;
        var max = 0.0;
        var low = 0.0;
        var high = 0.0;

        foreach (var fight in snapshot.Fights)
        {
            var line = outgoing ? DealtLine(fight) : TakenLine(fight);
            if (!line.HasSamples)
                continue;

            if (samples == 0 || line.Min < min)
                min = line.Min;
            if (line.Max > max)
                max = line.Max;
            samples += line.Samples;
            low += line.Total.Low;
            high += line.Total.High;
        }

        if (samples == 0)
            return ExchangeLine.Empty;

        // Both ends pooled on the outgoing side, matching DealtLine's own definition of a mean; the
        // incoming side's two ends are equal, so the same expression is simply the exact mean.
        return new ExchangeLine(
            samples, min, max, (low + high) / (2.0 * samples),
            PerTick((low + high) / 2.0, snapshot.Duration),
            new DamageBracket(low, high));
    }

    /// <summary>
    /// How long the whole ENCOUNTER has been running, in ticks.
    ///
    /// <para>Straight off <c>snapshot.Duration</c>, which the aggregator keeps as
    /// <c>nowUtc - _encounterStartUtc</c>. Walking the fights and taking the longest instead would be
    /// wrong twice over: <see cref="FightAccumulator.DurationAt"/> FREEZES at <c>EndedUtc</c> once a
    /// fight resolves, so killing the last creature would stop Dur advancing while the encounter is
    /// still open through the grace window - and the <c>dmg/tick</c> cells on the same tile divide by
    /// this same encounter duration, so the two readouts would visibly drift apart every second.</para>
    /// </summary>
    private static double? EncounterTicksOf(CombatEncounterSnapshot snapshot)
        => snapshot.Duration > TimeSpan.Zero ? TicksElapsed(snapshot.Duration) : null;

    /// <summary>The player's side of the stat row. <see cref="ExchangeLine.Mean"/> pools BOTH ends of
    /// every bracket: three blows of (1-5), (5-9) and (10-14) average to (1+5+5+9+10+14)/6. The
    /// extremes are upper bounds for the reason ExchangeLine records.</summary>
    private static ExchangeLine DealtLine(FightSnapshot fight)
    {
        if (fight.DealtSamples <= 0)
            return ExchangeLine.Empty;

        var bothEnds = fight.YourDamage.Low + fight.YourDamage.High;
        return new ExchangeLine(
            fight.DealtSamples,
            fight.DealtMinHigh,
            fight.DealtMaxHigh,
            bothEnds / (2.0 * fight.DealtSamples),
            PerTick(bothEnds / 2.0, fight.Duration),
            fight.YourDamage);
    }

    /// <summary>The creature's side. Exact throughout - MUD2 prints the player absolute stamina on
    /// every blow that lands - so the total comes back with equal ends rather than as a range.</summary>
    private static ExchangeLine TakenLine(FightSnapshot fight)
    {
        var profile = fight.TheirDamage;
        if (profile.Samples <= 0)
            return ExchangeLine.Empty;

        return new ExchangeLine(
            profile.Samples,
            fight.MinDamageTaken,
            profile.Max,
            profile.Average,
            PerTick(profile.Sum, fight.Duration),
            new DamageBracket(profile.Sum, profile.Sum));
    }

    /// <summary>A rate, or zero for "not yet worth stating". Under one full tick there is no rate to
    /// report - dividing by a fraction of a tick turns the first blow of a fight into a catastrophic
    /// -40/tick - so this returns zero and the tile draws the cell as unknown rather than as a
    /// measurement. Same refusal CombatOutlook makes for the same reason, at a lower bar
    /// because this states what HAS happened rather than projecting what will.</summary>
    private static double PerTick(double total, TimeSpan duration)
    {
        var ticks = TicksElapsed(duration);
        return ticks < 1.0 ? 0.0 : total / ticks;
    }

    /// <summary>Stand-in for "no encounter", so the formatter's session-totals path can run without a
    /// live snapshot.</summary>
    private static readonly CombatEncounterSnapshot IdleSnapshot = new(
        HasEncounter: false, InCombat: false, StartedUtc: null, CurrentWeapon: null, ActiveNpcs: [],
        YouHits: 0, YouMisses: 0, TheyHits: 0, TheyMisses: 0, YouHitRate: 0, TheyHitRate: 0,
        ApproxDamageDone: 0, ApproxDamageTaken: 0, Duration: TimeSpan.Zero,
        ApproxDps: 0, TheirApproxDps: 0, Fights: []);

    /// <summary>Resolves the history context for the encounter's primary target via the incremental
    /// index (see CombatHistoryCache/HistoryIndex) instead of scanning the whole fight corpus.</summary>
    private CombatHistoryContext ResolveHistory(CombatEncounterSnapshot snapshot)
    {
        var primary = CombatComposition.PrimaryFight(snapshot);
        if (_fightHistory is null || primary is null || string.IsNullOrWhiteSpace(primary.NpcGroup))
            return CombatHistoryContext.Empty;

        return _historyCache.Resolve(
            _fightHistory, primary.NpcName, primary.NpcGroup, snapshot.CurrentWeapon, snapshot.StartedUtc,
            _staminaPool);
    }



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

    // -- Floating online panel state --------------------------------------------

    /// <summary>When true (and the side panel is hidden), a floating online-list panel is shown.</summary>
    public bool IsOnlinePinned
    {
        get => _isOnlinePinned;
        set => SetAndNotify(ref _isOnlinePinned, value,
            [nameof(IsFloatingOnlineVisible), nameof(IsOnlineSectionVisible), nameof(PinGlyph), nameof(PinColor)]);
    }

    // \u25CF = filled circle (filled circle)  \u25CB = hollow circle (hollow circle)
    // These are regular text glyphs that obey TextColor - unlike emoji which ignore it.
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
    /// 0 = disabled (Recent list never populates). Range 0-10.</summary>
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
    /// Rebuilt wholesale by <see cref="RebuildRecentGroups"/>. Side-panel only - never floated.</summary>
    public ObservableCollection<RecentGroup> RecentGroups { get; } = new();

    /// <summary>True when the Recent list has any entries (drives its section visibility).</summary>
    public bool HasRecent => _recent.Count > 0;

    /// <summary>Count of non-departing online players.</summary>
    public int WhoCount { get; private set; }

    /// <summary>Formatted count for the Online section heading, e.g. " (3)".</summary>
    public string OnlineCountText => $" ({WhoCount})";

    /// <summary>Raised when the user taps the hamburger in the floating panel - opens settings/display.</summary>
    public event Action? FloatingOpenDisplaySettings;

    /// <summary>Raised when the user taps a Recent-list name - requests a "sniff" value-probe
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

    /// <summary>True while the About dialog overlay is shown (opened via the info status-bar icon).</summary>
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

    // -- WHO list --------------------------------------------------------------
    public ObservableCollection<WhoEntry> WhosList { get; } = new();
    private readonly List<WhoEntry> _pendingWhos = new();

    // -- Room exits (FEX) -----------------------------------------------------
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

    // -- Inventory / room items ------------------------------------------------
    public ObservableCollection<string> InventoryList { get; } = new();

    /// <summary>The "Here" list. Rows rather than bare strings, because a name on the floor may be a
    /// creature and the panel draws those differently - see <see cref="RoomEntry"/> and
    /// <see cref="MudSharp.Models.RoomCreatures"/>.</summary>
    public ObservableCollection<RoomEntry> RoomItemsList { get; } = new();

    /// <summary>The game's own creature-presence sentences for the room the player is standing in.
    /// The only thing that can tell a rat from a vial in an FEI list.</summary>
    private readonly MudSharp.Models.RoomCreatures _roomCreatures = new();

    /// <summary>Every NPC in an OPEN fight right now, refreshed from each combat snapshot. Second,
    /// independent source of "this is a creature" - and the only source of "and you are fighting it".
    /// See <see cref="EngagedNpcSet"/> for the unchanged-refresh property the Here list depends on.</summary>
    private readonly EngagedNpcSet _engagedNpcs = new();
    private readonly List<string> _pendingInventory = new();
    private readonly List<string> _pendingRoomItems = new();
    private bool _feiPastSeparator;
    // See LiveObjectsCarried: latched true by the first completed FEI list.
    private bool _feiEverCompleted;

    public bool HasInventory  => InventoryList.Count  > 0;
    public bool HasRoomItems  => RoomItemsList.Count  > 0;
    public bool NoInventory   => InventoryList.Count  == 0;
    public bool NoRoomItems   => RoomItemsList.Count  == 0;

    // -- Stale-fade signals -----------------------------------------------------
    // Raised (on the UI thread) when a list type is fully refreshed. StaleDimBehavior listens and
    // (re)starts a COMPOSITOR opacity animation on the section: hold full-bright for 15 s, then
    // ease to 70% - entirely on the render thread, so it never touches typing.
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
    /// <summary>Toggles the Combat Rail (the right-edge panel), from the
    /// overflow menu's "Combat" entry, alongside Side Panel / Onlines / Compass. GamePage
    /// resizes the window by the rail's width off the resulting
    /// <see cref="IsCombatPanelVisible"/> change, so every route to that property makes room for
    /// the panel rather than taking the space out of the terminal.</summary>
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
    public ICommand ToggleCombatFloatsCommand { get; }
    /// <summary>Toggles the Combat Rail's stat rows - the hamburger's "Stats" row.</summary>
    public ICommand ToggleCombatStatsCommand { get; }

    /// <summary>Raised when an interaction should hand keyboard focus back to the input box.
    /// Opening the About dialog deliberately does not raise it - focus belongs to the dialog.</summary>
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
        ToggleCombatFloatsCommand = new Command(ToggleCombatFloats);
        ToggleCombatStatsCommand  = new Command(ToggleCombatStats);
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
        // MapSizes runs largest -> smallest, so "increase" walks the index down.
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
        // command box (Invariant #0 - every interaction leaves the user able to type).
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
    // that remove a who-entry AFTER its GPU fade-out finishes (see OnFewListComplete) - NOT a
    // repeating animation tick. All visual fading runs on the compositor via behaviors.
    private IDispatcher? _dispatcher;

    /// <summary>Captures the UI-thread dispatcher (call once on the UI thread after game-mode is
    /// entered). Named for call-site compatibility - it no longer starts any timer.</summary>
    public void InitializeFadeTimer(IDispatcher dispatcher) => _dispatcher = dispatcher;

    // -- Room name -------------------------------------------------------------

    /// <summary>
    /// Called on the TCP read thread when the player has entered (or can now see) a room.
    /// Clears the "Here" (room items) list. InventoryList is intentionally preserved -
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
            // The creature sentences describe THIS room's occupants and nothing else, so they go with
            // the contents. Keeping them would let a rat described one room ago mark an identically
            // named object in the next one as alive.
            _roomCreatures.Clear();
            RoomItemsList.Clear();
            OnPropertiesChanged(nameof(HasRoomItems), nameof(NoRoomItems));
        });

    /// <summary>
    /// One creature-presence sentence, straight from the C04 scope (Feed thread). Marshalled onto the
    /// UI thread so it shares a thread with <see cref="OnFeiListComplete"/>'s read of the same index -
    /// the two arrive on different clocks and must not race.
    /// </summary>
    public void OnCreatureText(string sentence)
        => MainThread.BeginInvokeOnMainThread(() =>
        {
            _roomCreatures.Observe(sentence);
            // A creature can arrive after the room's own FEI list was built (C04.00.02, "arriving"),
            // and the arrival hints an Inventory refresh anyway - but that refresh may report the same
            // names and be skipped by the diff below, leaving the newcomer drawn as an object. Reclass
            // in place instead: no list rebuild, no re-templating.
            ReclassifyRoomEntries();
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
    /// and clears the compass - the option menu is not a room, so any exits are stale.
    /// Does not push history - history shifts on the next real room entry.
    /// </summary>
    public void OnGameModeExited()
        => MainThread.BeginInvokeOnMainThread(() =>
        {
            CurrentRoom = "Option Menu.";
            SetAllExitsPresent(false);
            // Backstop for the dead strip's reset boundary - see OnWorldResetLanded. Only advances
            // when the landing did not, so a corroborated reset gets its line at 06 06 and an
            // uncorroborated one gets it here, at the shell prompt about 200 ms later, rather than
            // not at all.
            if (!_resetOrdinalAdvanced)
                _resetOrdinal++;
            _resetOrdinalAdvanced = false;
        });

    /// <summary>
    /// One resolved fight as the dead strip records it: the outcome, the exchange summary its live
    /// tile carried, and the kill award if one has been paired to it.
    ///
    /// <para>Both places that build an ending route through here - the encounter-close fold and the
    /// live tail - so a row cannot pick up different figures depending on which side of the close it
    /// was built on.</para>
    /// </summary>
    private CombatEnding EndingFor(FightSnapshot fight, int encounterOrdinal, int resetOrdinal)
        => new(
            fight.NpcName, fight.Outcome, fight.EndedUtc, encounterOrdinal, resetOrdinal,
            DealtLine(fight), TakenLine(fight),
            AwardFor(encounterOrdinal, fight.NpcName, fight.EndedUtc));

    /// <summary>The award paired to one ending - see <see cref="KillAwardLedger"/>, which owns the
    /// pairing and the reason it is scoped to the frame that produced the ending.</summary>
    private int? AwardFor(int encounterOrdinal, string name, DateTime? endedUtc)
        => _killAwards.AwardFor(encounterOrdinal, name, endedUtc);

    /// <summary>What each kill and creature flight was scored, and what the player's own flights cost.
    /// Kept out of this class (MAUI-dependent, unreachable from mudsharp.Tests) so the pairing can be
    /// replayed by a test, like <c>CombatEndingOrder</c>.</summary>
    private readonly KillAwardLedger _killAwards = new();
    private readonly FleeChargeLedger _fleeCharges = new();

    /// <summary>MUD2 announced a score change. A rise is an award for the oldest ending still waiting
    /// in this frame (<see cref="KillAwardLedger"/>); a fall is held as the frame's flight charge
    /// (<see cref="FleeChargeLedger"/>).</summary>
    public void OnScoreSaved(MudSharp.Models.ScoreSave save)
        // MainThread, like every other handler on this class: MuckaConnection re-raises this on the
        // FEED thread, and both ledgers are plain single-threaded state that OnCombatEvent writes from
        // the UI thread. The hop is also what makes the frame boundary usable - OnTaskCompleted and
        // OnFrameClosed hop the same way, so ending, award, task line and frame close reach the ledgers
        // in the order their lines arrived.
        => MainThread.BeginInvokeOnMainThread(() =>
        {
            if (save.Delta is not int delta || delta == 0)
                return;
            if (delta < 0)
            {
                _fleeCharges.NoteScoreFall(-delta);   // the row changes at OnPlayerFled, not here
                return;
            }
            if (!_killAwards.NoteScoreRise(delta))
                return;

            // The dirty check is a resolved-count comparison, which cannot see a figure attaching to a
            // row that already exists - so the cache is invalidated explicitly.
            _deadStripHistoryCachedResolvedCount = -1;
            RefreshCombatDisplay(DateTime.UtcNow);
        });

    /// <summary>See <see cref="KillAwardLedger.NoteTaskCompleted"/>. Hops like OnScoreSaved.</summary>
    public void OnTaskCompleted(MudSharp.Models.TaskCompletion completed)
        => MainThread.BeginInvokeOnMainThread(_killAwards.NoteTaskCompleted);

    /// <summary>A frame of game output ended (<see cref="MudSharp.Protocol.MudStreamParser.FrameClosed"/>):
    /// anything either ledger is still waiting on is never coming. Hops like OnScoreSaved.</summary>
    public void OnFrameClosed()
        => MainThread.BeginInvokeOnMainThread(() =>
        {
            _killAwards.NoteFrameClosed();
            _fleeCharges.NoteFrameClosed();
        });

    /// <summary>The player fled, or tried to. The charge has already gone past - see
    /// <see cref="FleeChargeLedger"/>.</summary>
    private void OnPlayerFled(DateTime atUtc)
    {
        if (!_fleeCharges.NoteFled(_encounterOrdinal, atUtc))
            return;
        _deadStripHistoryCachedResolvedCount = -1;
        RefreshCombatDisplay(DateTime.UtcNow);
    }

    /// <summary>
    /// The persona occupying this session, from MudSession.CharacterIdentified - the same post-login
    /// handshake FightHistoryRecorder stamps its rows with.
    ///
    /// <para><b>NOTHING ELSE IS RESET HERE, and that is deliberate.</b> The dead strip is a record of
    /// what this SITTING has killed - it survives a persona switch, a world reset, and a hop to a
    /// different server: it answers "what have I been doing", not "what has this character done". It
    /// lives as long as the app run and is deliberately not persisted past it.</para>
    ///
    /// <para>Session TOTALS likewise carry: they are the client's own "this sitting" tally.</para>
    /// </summary>
    public void OnCharacterIdentified(string name)
    {
        _personaName = name ?? string.Empty;
        RefreshCombatDisplay(DateTime.UtcNow);
    }

    private string _personaName = string.Empty;
    private byte? _staminaAnsiColor;

    /// <summary>
    /// Advances the dead strip's reset-grouping ordinal (<see cref="CombatEnding.ResetOrdinal"/>) on
    /// <c>MudSession.WorldResetLanded</c> (FE 06 06). Fires on the reset LANDING, not the C06 C04
    /// warning: an ending inside the finish-up window belongs to the cycle that is ending, not the
    /// next one. <see cref="OnGameModeExited"/> is the backstop when the landing is not raised; a
    /// missing separator merges two cycles, which is worse than one drawn late.
    /// </summary>
    public void OnWorldResetLanded()
        => MainThread.BeginInvokeOnMainThread(() => { _resetOrdinal++; _resetOrdinalAdvanced = true; });

    // -- WHO list (FEW) --------------------------------------------------------

    /// <summary>
    /// Called when the parser opens a FEW-response context (C12+C08+C05).
    /// Clears the accumulation buffer; WhosList is not touched until the response is complete.
    /// Fires on the TCP read thread - no marshal needed (_pendingWhos is read-loop-only).
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
    /// Called when the FEW-response context closes - all names have been delivered.
    /// Diffs the incoming snapshot against the current WhosList:
    ///   - Players no longer in the snapshot are marked departing and fade out over 4 s.
    ///   - Players that reappear before their fade completes have their departure cancelled.
    ///   - New arrivals are appended with a white->color glow over 4 s.
    ///   - Players whose name or color changed (e.g. level-up) are updated in-place with a glow.
    ///   - A visibility change ("Ollie the sorcerer" becoming "(Ollie the sorcerer)", or back) is a status
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

            // A persona present in this FEW is live again - it must never also sit in Recent
            // (covers both returnees still in WhosList and a floating-departure copy in Recent).
            _recent.RemoveAll(r => newByPersona.ContainsKey(r.PersonaName));

            // Does a departing player land in the (side-panel) Recent list at all? Only when Recent
            // is enabled, the side panel is showing, and the Online section is on.
            bool recentEligible = _forgetWindowMinutes > 0 && _isPanelExpanded && _isOnlineExpanded;

            // Update returnees in place; route departures by display state:
            //  - docked + Recent   -> jump straight to Recent, no fade
            //  - floating + Recent -> fade out in the floater AND show in Recent immediately
            //  - otherwise         -> the plain fade-out then removal (no Recent)
            for (int i = WhosList.Count - 1; i >= 0; i--)
            {
                var existing = WhosList[i];
                if (newByPersona.TryGetValue(existing.PersonaName, out var updated))
                {
                    existing.IsDeparting = false;   // present again - cancel any pending fade-out + removal
                    if (existing.Name  != updated.Name)  existing.Name  = updated.Name;
                    if (existing.Color != updated.Color) existing.Color = updated.Color;
                    continue;
                }
                if (existing.IsDeparting)
                    continue;   // already fading - leave its pending removal alone

                var leaving = existing;
                // They were present in the previous FEW, so that completion is their "last seen"
                // time; the gap since then low-clamps their Recent lifetime (an overdue FEW can't
                // instant-flush them - see MoveToRecent).
                var lastSeenUtc = _lastFewCompleteUtc == default ? now : _lastFewCompleteUtc;

                if (recentEligible && _isOnlinePinned)
                {
                    // Docked: no fade - jump straight to Recent.
                    WhosList.RemoveAt(i);
                    MoveToRecent(leaving, lastSeenUtc);
                }
                else if (recentEligible)
                {
                    // Floating: the online copy fades out in the floater, while Recent gets a fresh
                    // (non-fading) copy right away - the original is removed once the fade finishes.
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
            // Re-age the Recent groups (and sweep anything past its window) on the heartbeat -
            // no repeating UI-thread timer (Invariant #1); this piggybacks the FEW refresh.
            // Unconditional: a returning player removed from _recent above may have emptied it,
            // and the view still needs clearing (the signature guard makes the no-op case cheap).
            RebuildRecentGroups();
        });
    }

    // -- Recent list (players who faded off Online, kept for the Forget window) ----------

    /// <summary>
    /// Move a just-departed player into the Recent list. Their lifetime there is
    /// <c>clamp(ForgetWindow - minutesSinceLastSeen, 1 min, ForgetWindow)</c>: a normal
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
    ///   - Present   -> the player is online and visible; promote back into Online (plain).
    ///   - Invisible -> online but invisible; promote into Online wrapped in parens for one probe
    ///                 interval, then the next FEW drops them back to Recent (parens retained).
    ///   - Offline   -> logged out; leave the entry to age out of Recent on its own (a probe only
    ///                 ever *promotes* - it never removes).
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
                    // Confirmed logged out - do nothing. The entry just ages out of Recent on its
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

    /// <summary>
    /// A creature's `value &lt;name&gt;` reply resolved (see MudSession's creature-value probe).
    /// UI-marshalled, like every other session callback here. Routes straight into the aggregator's
    /// own bucket for that name and asks for the usual throttled refresh - the same shape as
    /// OnCombatEvent/OnStatsUpdated - because the value is not read anywhere on its own timer, only
    /// as part of the roster row the next refresh builds.
    /// </summary>
    public void OnCreatureValueResolved(string name, int value)
        => MainThread.BeginInvokeOnMainThread(() =>
        {
            _combatStats.ObserveCreatureValue(name, value);
            var now = DateTime.UtcNow;
            if (_clogRenderGate.RequestRender(now))
                RefreshCombatDisplay(now);
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

    // -- Inventory / room items (FEI) ------------------------------------------

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
            var roomChanged = snapRoom.Count != RoomItemsList.Count;
            if (!roomChanged)
            {
                for (var i = 0; i < snapRoom.Count; i++)
                {
                    if (!string.Equals(snapRoom[i], RoomItemsList[i].Name, StringComparison.Ordinal))
                    {
                        roomChanged = true;
                        break;
                    }
                }
            }

            if (roomChanged)
            {
                RoomItemsList.Clear();
                foreach (var item in snapRoom)
                    RoomItemsList.Add(new RoomEntry(item, IsCreatureName(item), _engagedNpcs.Contains(item)));
            }
            else
            {
                // Same names as last beat, so no rebuild - but a creature sentence or a fight may have
                // landed since, and both change how an unchanged row is drawn. Two bindable properties
                // at most per row, only where they actually changed: no re-templating.
                ReclassifyRoomEntries();
            }

            if (!snapInv.SequenceEqual(InventoryList))
            {
                InventoryList.Clear();
                foreach (var item in snapInv)
                    InventoryList.Add(item);
                OnPropertiesChanged(nameof(HasInventory), nameof(NoInventory));
            }

            if (roomChanged)
                OnPropertiesChanged(nameof(HasRoomItems), nameof(NoRoomItems));

            // Latched, never cleared: it distinguishes "FEI says you carry nothing" from "FEI has
            // never reported". Without it an empty InventoryList reads as a confirmed zero, and the
            // combat readout would assert "0 items" before the first probe ever landed.
            _feiEverCompleted = true;

            FeiRefreshed?.Invoke();   // restart the section's compositor stale-dim
        });
    }

    /// <summary>
    /// Whether an FEI room entry names a living thing. Two independent sources, unioned:
    ///
    /// <para>The game's own C1 taxonomy (code 04 = creature), captured per room - see
    /// <see cref="MudSharp.Models.RoomCreatures"/> for why that is the only classification available
    /// and what it costs.</para>
    ///
    /// <para>And the fight tracker: whatever is currently being swung at is a creature, whether or not
    /// a description was ever seen for it. This is what covers the case the first source cannot - a
    /// client attached mid-room, with no room description behind it.</para>
    /// </summary>
    private bool IsCreatureName(string name)
        => _engagedNpcs.Contains(name) || _roomCreatures.IsCreature(name);

    /// <summary>Re-applies both flags to the rows already on screen. Touches only the two bindable
    /// properties, and only when they actually change, so an unchanged beat costs a handful of string
    /// comparisons and no native re-templating (Invariant #1).</summary>
    private void ReclassifyRoomEntries()
    {
        foreach (var entry in RoomItemsList)
        {
            var engaged = _engagedNpcs.Contains(entry.Name);
            entry.IsEngaged = engaged;
            // Never demoted: a creature that stops being fought is still a creature, and the room's
            // description does not get re-sent when a fight ends.
            if (!entry.IsCreature && (engaged || _roomCreatures.IsCreature(entry.Name)))
                entry.IsCreature = true;
        }
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

    // -- Room exits (FEX) ------------------------------------------------------

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
        // No resources to release. Kept for the IDisposable contract.
    }
}
