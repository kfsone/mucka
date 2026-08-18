using MudSharp.Combat;
using MudSharp.Models;
using MudSharp.Protocol;

namespace MudSharp.Session;

/// <summary>
/// Policy wrapper around MudStreamParser.
/// Owns FES heartbeat, stats merging, dreamword tracking, and outgoing line queue.
///
/// THREADING: Events forwarded from MudStreamParser fire on the Feed() caller thread.
/// The FES heartbeat fires on a ThreadPool thread.
/// All public events preserve the same threading contract as MudStreamParser.
/// </summary>
public sealed class MudSession : IDisposable
{
    private readonly MudStreamParser _parser;
    private readonly MudSessionOptions _options;
    private readonly object _fesLock = new();
    private Timer? _fesTimer;
    private TimeSpan _fesInterval;
    // Wake-probe state: while the character is asleep the periodic probes are no-ops, so
    // probe replies stop arriving. Any real bytes from the server while replies are stale
    // (the wake) trigger an immediate re-probe instead of waiting out the current period.
    private DateTime _lastProbeReplyUtc;
    private DateTime _lastWakeProbeUtc;
    // FES-only probe: reset-time lives in FES, so a precision probe leaves FEW/FEI undisturbed.
    private static readonly byte[] FesOnlyProbe = System.Text.Encoding.Latin1.GetBytes("\x1b-[FES\x1b-]");
    // Minimum spacing between wake probes so a chatty burst (e.g. dream text) fires only one.
    private static readonly TimeSpan WakeProbeFloor = TimeSpan.FromSeconds(2);
    // ── Reactive stale-stats probing ───────────────────────────────────────────
    // C1 codes hint that state changed (ProbeHintReceived). Rather than probing
    // instantly (Clio's txfes), the hinted categories are marked stale and a one-shot
    // timer fires StaleProbeDelay later; updates that arrive in the meantime — the
    // inline "(84/90)" after a hit, an unsolicited FES — clear their flags so only
    // genuinely missing values are queried. Probes cost the player a game turn, so
    // they are also rate-limited (MinProbeSpacing) and suppressed when the routine
    // heartbeat is about to cover them anyway. All fields guarded by _fesLock except
    // the who-list name caches, which are only touched on the Feed thread.
    private StaleStats _staleFlags;
    private Timer? _staleTimer;
    private bool _staleArmed;
    private DateTime _lastProbeSentUtc;
    // Last FES send drives wake detection: only a fresh stats reply proves a probe was answered.
    private DateTime _lastFesSentUtc = DateTime.MinValue;
    private DateTime _nextRoutineProbeUtc;
    // While held, routine and stale probes are suppressed (see SetProbeHold).
    private bool _probesHeld;
    // While the reset-time discovery pass owns the channel, the routine heartbeat is suspended so its
    // compound reply never races our rate-limited FES samples (see SetResetDiscoveryHold / ResetClock).
    private bool _resetDiscoveryHold;
    // Persona name from each entry in the last complete FEW response (Feed thread only).
    // Used by the C05 presence check: a player seen in the room but absent from this
    // set means the Online list is stale.
    private readonly HashSet<string> _onlineNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingOnlineNames = new(StringComparer.OrdinalIgnoreCase);
    // ── "Sniff" value-probe state ───────────────────────────────────────────────
    // A `value <name>` command prepended to a routine probe to disambiguate a player who
    // dropped off the FEW list (see QueueValueProbe / SniffResult). _pendingSniff (a queued
    // request) is guarded by _fesLock; _sniffInFlight is volatile so the Feed-thread line
    // filter can fast-check it without taking the lock on every line.
    private string? _pendingSniff;
    private volatile string? _sniffInFlight;

    // ── Resite/supersite recovery probe ─────────────────────────────────────────
    // Ordinary movement's room description is always followed by an auto-fex FEEXITS block in
    // the same transmission. A spell-driven relocation (resite, supersite, and any future
    // mechanic shaped the same way) fires no auto commands at all, so RoomEntered arrives with
    // no FEX to follow. One-shot timer armed on RoomEntered, cancelled by FexListStarting (the
    // earliest signal a FEX is genuinely on the way); if it elapses unanswered, fire the same
    // explicit FEX probe used at game-mode entry. Guarded by _fesLock like the other one-shot
    // timers in this file.
    private Timer? _roomFexProbeTimer;

    // ── Post-character-select setup swallow state ───────────────────────────────
    // On game-mode entry we inject a setup batch ("auto fex\r\nscore\r\n") and hide its echo +
    // replies from the terminal (TrySwallowSetupLine). Each reply arrives as its own server
    // "frame", and every frame is introduced by an IsPartial '*' prompt line — a boundary that
    // survives line-wrapping (narrow widths only add more content lines within a frame, never
    // more prompts). So we recognise each setup frame by its first content line and then swallow
    // the whole frame up to the next prompt; the score frame is the last, and its closing prompt
    // shuts the window. All fields are touched only on the Feed thread (game-entry and line
    // processing both run there).
    //
    // The window opens ONCE, at game entry, and nothing reopens it. There is deliberately NO periodic
    // `score` refresh, and this is a hard rule rather than a tuning choice.
    //
    // The cost is not the game turn - MUD2 turns are short server slices (~10-50ms) that exist to
    // stop action spam, and a `score` does not consume a combat round. The cost is BANDWIDTH. The
    // sheet is a dozen-plus lines, the server's link is not fat, and every byte of it is time spent
    // dispatching housekeeping down the same pipe the player's combat text and flee acknowledgement
    // have to come back through. Injecting that on a timer means occasionally delaying exactly the
    // output a player is waiting on to decide whether to run.
    //
    // Gating it on "not in combat" does not rescue it either: that is the CLIENT's view of combat,
    // which lags the server, so a sheet already in flight when a fight starts still lands in the
    // middle of it. In a permadeath game no inventory count is worth that.
    private volatile bool _setupWindowActive;  // window open (game-entry → score frame closed)
    private bool _setupSwallowingFrame;   // inside a setup frame we've claimed — swallow its lines
    private bool _setupCloseAfterFrame;   // the score frame is in progress; close when it ends
    private string? _currentCharName;

    private GameStatsSnapshot _currentStats = GameStatsSnapshot.Empty;
    private string? _currentDreamword;
    // Periodic probe: composed per beat (ComposeBeatLocked) - FES always leads because the server
    // does not reliably refresh FEW/FEI without it; FEW rides every beat, and FEI only when marked
    // dirty by a C1 hint. FEW and FEI are omitted when those side-panel sections are disabled.
    private bool _includeFew = true;
    private bool _includeFei = true;
    // While the mapping window has focus the heartbeat omits FEI, but retains FES+FEW so the
    // online list remains reliable and an arriving PKer is visible.
    private bool _mappingFocus;
    private readonly EffectTracker _effects = new();
    private readonly CombatTracker _combat = new();

    // Drives CombatTracker.Tick (the post-kill grace window's time-only expiry check) at a fixed
    // ~1Hz, independent of any UI. This used to be driven by GamePage's own anti-idle UI-thread
    // tick via a TickCombat() call chain - moved onto its own pool timer (2026-08-16) because that
    // wiring raced the UI thread against CombatTracker's Observe/ForceEnd (Feed thread) on every
    // single encounter. See CombatTracker's own remarks on the lock this now pairs with.
    private static readonly TimeSpan CombatTickInterval = TimeSpan.FromSeconds(1);
    private readonly Timer _combatTickTimer;

    // Testability seam only: production code never overrides this, so combat timestamps are
    // always the real wall clock (the correct behaviour for the live grace-period logic in
    // CombatTracker). CombatCaptureReplayTests overrides it to replay the research capture's own
    // original timestamps, since a fast in-memory replay's real elapsed time bears no relation to
    // the many-hour session it captures and would otherwise never trip (or would spuriously trip)
    // CombatTracker's 5-second post-kill grace window.
    internal Func<DateTime> CombatClock { get; set; } = () => DateTime.UtcNow;

    // Reset-time projection: folds the minute-granular FES reset value into an absolute target and,
    // once per session near the start, runs a staged burst (~1 s then ~250 ms probes) to pin it to
    // sub-second. Owned here (not the VM) so all sub-second probe timing stays off the UI thread and
    // reply↔probe correlation sits next to the wire. See ResetClock.
    private readonly ResetClock _resetClock;

    // ── Public events (forwarded from parser) ─────────────────────────────────
    public event Action<StyledLine>? LineReady;
    public event Action<GameStatsSnapshot>? StatsUpdated;
    /// <summary>The server's C08+C13 ("Not updating persona.") signal: permadeath wiped the
    /// current persona. Fires alongside <see cref="StatsUpdated"/>'s zeroed snapshot.</summary>
    public event Action? PersonaWiped;
    public event Action? GameModeEntered;
    public event Action? GameModeExited;
    public event Action<byte[]>? OutgoingBytes;
    public event Action? BellReceived;
    public event Action<string?>? DreamwordChanged;
    public event Action<string>? ClientModeReceived;
    public event Action<string>? SoundRequested;
    public event Action<string>? TellReceived;
    public event Action<string, AnsiColor>? FewPlayerReady;
    public event Action? FewListStarting;
    public event Action? FewListComplete;
    public event Action? RoomEntered;
    public event Action<string>? RoomShortReady;
    public event Action<string>? FeiItemReady;
    public event Action? FeiListStarting;
    public event Action? FeiListComplete;
    public event Action<string>? FexItemReady;
    public event Action? FexListStarting;
    public event Action? FexListComplete;
    /// <summary>An exits-verb line "direction: Destination." was parsed. Payload: (direction, destination name).</summary>
    public event Action<string, string>? ExitLineReady;
    /// <summary>The local player's active temporary-effect set changed (buffs/debuffs/glow).</summary>
    public event Action<StatusEffectState>? StatusEffectsChanged;
    /// <summary>Fires whenever combat is entered/left (see <see cref="CombatTracker"/>).</summary>
    public event Action<bool>? InCombatChanged;
    /// <summary>Fires whenever <see cref="IsInCombatGracePeriod"/> flips.</summary>
    public event Action<bool>? CombatGracePeriodChanged;
    /// <summary>Fires for every classified combat line while (or just as) InCombat.</summary>
    public event Action<CombatEvent>? CombatEventOccurred;
    /// <summary>
    /// Server confirmed the terminal width (ESC-<n>W response or "[New terminal width is N]" annotation).
    /// Payload is the confirmed column count.
    /// </summary>
    public event Action<int>? TerminalWidthConfirmed;
    /// <summary>
    /// A FES/FEW/FEI probe interrupt was just transmitted (routine heartbeat or stale
    /// re-probe). Its response ends in a prompt redraw, so consumers that key off
    /// prompts (the mapping console) treat the next moments as contended.
    /// </summary>
    public event Action? ProbeSent;
    /// <summary>
    /// A queued "sniff" value-probe has resolved. Payload is the probed persona name and the
    /// outcome (present / offline / invisible). Fires on the Feed thread — consumers marshal.
    /// </summary>
    public event Action<string, SniffOutcome>? SniffResult;
    /// <summary>
    /// The character occupying this session has been identified from the post-character-select
    /// setup <c>score</c> reply. Payload is the character name (e.g. "Ollie"). Fires once per
    /// game-mode entry, on the Feed thread — consumers marshal. Used to key per-character score
    /// tracking and the window title.
    /// </summary>
    public event Action<string>? CharacterIdentified;
    /// <summary>The reset-time projection changed. Optional immediate UI-refresh hint; the countdown
    /// also polls <see cref="ResetEstimate"/> on its own 1 Hz tick. Fires off the UI thread.</summary>
    public event Action? ResetEstimateChanged;
    /// <summary>A reset-projection reading was folded — for diagnostic logging only. Fires on the
    /// read-loop thread.</summary>
    public event Action<ResetObservation>? ResetObservationRecorded;
    /// <summary>A notable reset-projection incident (unanswered sample, lock contradiction, auto-reset
    /// anchor) — for the capture log. Fires off the UI thread.</summary>
    public event Action<string>? ResetDiagnostic;
    /// <summary>The server announced the auto-reset (C06 C04, "you have 120 seconds to finish up").
    /// Unlike the projection, this is an exact, unambiguous "a reset is happening now" statement —
    /// consumers use it to tell a reset-driven drop to the Option menu from a deliberate quit.
    /// Fires on the read-loop thread.</summary>
    public event Action? AutoResetInitiated;

    // ── Public state ───────────────────────────────────────────────────────────
    /// <summary>The current merged stats snapshot (see <c>MergeStats</c>) — always up to date
    /// thanks to the periodic FES heartbeat, so callers that just need "whatever we currently
    /// know" (e.g. a baseline read before an item-eval drop/get pair) should read this directly
    /// rather than subscribing to <see cref="StatsUpdated"/> and waiting for the next event, which
    /// races the heartbeat's own cadence and can read as empty right after subscribing.</summary>
    public GameStatsSnapshot CurrentStats => _currentStats;
    public string? CurrentDreamword => _currentDreamword;
    public bool InGameMode => _parser.InGameMode;
    public bool InCombat => _combat.InCombat;
    /// <summary>See <see cref="CombatTracker.IsInGracePeriod"/>.</summary>
    public bool IsInCombatGracePeriod => _combat.IsInGracePeriod;
    /// <summary>Latest reset-time projection snapshot (target instant + uncertainty + phase).</summary>
    public ResetEstimate ResetEstimate => _resetClock.Snapshot();

    public MudSession(MudSessionOptions? options = null)
    {
        _options = options ?? new MudSessionOptions();
        _fesInterval = _options.FesHeartbeatInterval;
        _parser = new MudStreamParser();
        _resetClock = new ResetClock(_options.ResetClock, TrySendResetFesProbe, CanResetProbe, SetResetDiscoveryHold);
        _resetClock.ObservationRecorded += o => ResetObservationRecorded?.Invoke(o);
        _resetClock.EstimateChanged     += () => ResetEstimateChanged?.Invoke();
        _resetClock.DiagnosticNote      += n => ResetDiagnostic?.Invoke(n);
        WireParserEvents();
        _combatTickTimer = new Timer(_ => _combat.Tick(CombatClock()), null, CombatTickInterval, CombatTickInterval);
    }

    /// <summary>
    /// Update the FES heartbeat interval at runtime. Zero or negative disables the heartbeat.
    /// If already in game mode the running timer is replaced immediately.
    /// </summary>
    public void UpdateFesInterval(TimeSpan interval)
    {
        lock (_fesLock)
        {
            _fesInterval = interval;
            StopFesTimerLocked();
            if (InGameMode && _fesInterval > TimeSpan.Zero)
            {
                _nextRoutineProbeUtc = DateTime.UtcNow + _fesInterval;
                _fesTimer = new Timer(_ => SendFesSubscription(), null, _fesInterval, _fesInterval);
            }
            else
            {
                StopStaleProbeLocked();
            }
        }
    }

    /// <summary>
    /// Update which components may be included in the periodic heartbeat probe.
    /// When <paramref name="includeFew"/> is false the online list (FEW) is omitted.
    /// When <paramref name="includeFei"/> is false the inventory/room-items list (FEI) is omitted.
    /// May be called from any thread.
    /// </summary>
    public void UpdateSubscriptionOptions(bool includeFew, bool includeFei)
    {
        lock (_fesLock)
        {
            _includeFew = includeFew;
            _includeFei = includeFei;
        }
    }

    /// <summary>Mapping window focus gained (true) / lost (false). While focused the
    /// periodic heartbeat omits FEI but retains FES+FEW so the online list refreshes reliably.
    /// May be called from any thread.</summary>
    public void SetMappingFocus(bool focused)
    {
        lock (_fesLock)
            _mappingFocus = focused;
    }

    /// <summary>
    /// Compose one heartbeat's probe. FES always leads: in practice the server does not reliably
    /// update FEW or FEI when either is queried without FES. FEW remains the every-beat component
    /// for who-list vigilance; FEI remains event-driven and is included only when marked dirty.
    /// Mapping focus suppresses FEI but keeps the reliable FES+FEW pair.
    /// Caller holds _fesLock. Returns the parts for flag bookkeeping.
    /// </summary>
    private byte[] ComposeBeatLocked(out bool fes, out bool few, out bool fei)
    {
        fes = true;
        few = _includeFew || _mappingFocus;
        fei = !_mappingFocus && _includeFei && (_staleFlags & StaleStats.Inventory) != 0;
        var cmds = new List<string>(3);
        if (fes) cmds.Add("FES");
        if (few) cmds.Add("FEW");
        if (fei) cmds.Add("FEI");
        return System.Text.Encoding.ASCII.GetBytes("\x1b-[" + string.Join(',', cmds) + "\x1b-]");
    }

    /// <summary>Feed raw bytes from the network. Thread-safe relative to the FES timer — Feed() itself is not thread-safe.</summary>
    public void Feed(ReadOnlySpan<byte> data)
    {
        var nonEmpty = data.Length > 0;
        _parser.Feed(data);
        if (nonEmpty)
            MaybeSendWakeProbe();
    }

    /// <summary>Set the login username advertised via NEW-ENVIRON USER during telnet negotiation.</summary>
    public void SetLoginUser(string? user) => _parser.SetLoginUser(user);

    /// <summary>
    /// Emit any buffered partial text as a partial <see cref="StyledLine"/>.
    /// Call after each <see cref="Feed"/> to surface non-game-mode prompts
    /// (e.g. "Account ID:") that arrive without a trailing newline or C98 signal.
    /// </summary>
    public void EmitPartial() => _parser.EmitPartialLine();

    /// <summary>Send a line of text to the server (appends \r\n).</summary>
    public void SendLine(string line)
    {
        var bytes = System.Text.Encoding.Latin1.GetBytes(line + "\r\n");
        OutgoingBytes?.Invoke(bytes);
    }

    /// <summary>Send raw bytes to the server.</summary>
    public void Send(byte[] bytes) => OutgoingBytes?.Invoke(bytes);

    /// <summary>
    /// Update the advertised terminal window size. Sends an updated NAWS subnegotiation if
    /// NAWS has already been negotiated with the server. May be called from any thread.
    /// </summary>
    public void SetWindowSize(int cols, int rows) => _parser.SetWindowSize(cols, rows);

    /// <summary>Reset parser state (call on disconnect).</summary>
    public void Reset()
    {
        StopFesTimer();
        lock (_fesLock)
        {
            StopStaleProbeLocked();
            StopRoomFexProbeLocked();
            // Drop mapping focus so the next game-mode entry includes FEI again. The session is
            // reused across reconnects/relogs, so stale focus would starve inventory updates.
            _mappingFocus = false;
            _pendingSniff = null;
            _sniffInFlight = null;
        }
        _onlineNames.Clear();
        _pendingOnlineNames.Clear();
        _parser.Reset();
        _currentStats = GameStatsSnapshot.Empty;
        _currentDreamword = null;
        _resetClock.OnGameModeExited();   // disconnect: drop any live projection
    }

    public void Dispose()
    {
        // Stop the combat tick timer before anything else, so ForceEnd below is the last thing that
        // can ever touch _combat during teardown.
        _combatTickTimer.Dispose();

        // Force-close any open encounter BEFORE tearing anything else down: unlike Reset() (used for
        // an ordinary disconnect/relog, where the server-side reset/logout already ends combat on its
        // own), app exit gives no such signal, so without this an encounter that was live at shutdown
        // never resolves and its fight rows are lost entirely (they only ever get written from
        // CombatTracker.InCombatChanged -> false). ForceEnd is idempotent (no-op if not InCombat) and
        // raises the same InCombatChanged(false)/EventOccurred events a normal fight-end would, which
        // MuckaConnection has already wired to FightHistoryRecorder for the lifetime of this session -
        // so this one call is what makes "no fight rows lost on app exit mid-fight" true.
        _combat.ForceEnd(CombatClock());
        StopFesTimer();
        lock (_fesLock)
        {
            StopStaleProbeLocked();
            _staleTimer?.Dispose();
            _staleTimer = null;
            StopRoomFexProbeLocked();
            _roomFexProbeTimer?.Dispose();
            _roomFexProbeTimer = null;
        }
        _resetClock.Dispose();
    }

    // ── Private ────────────────────────────────────────────────────────────────
    private void WireParserEvents()
    {
        _parser.LineReady += line =>
        {
            // Swallow the echo + replies of the post-character-select setup batch (auto fex,
            // score, CTRL-T time sentinel) so they never reach the terminal. Bounded to the
            // brief window after game-mode entry; the flag keeps normal lines free of cost.
            if (_setupWindowActive && TrySwallowSetupLine(line))
                return;
            // Swallow the echo + reply of an injected `value <name>` sniff so it never
            // reaches the terminal. Fast volatile check keeps normal lines free of cost.
            if (_sniffInFlight != null && TryConsumeSniffLine(line))
                return;
            // Cancel the dreamword when we see our own persona speak it: speaking uses it,
            // whether it recovered stamina (scenario: server also sends a C1 clear) or was a
            // no-op (full stamina / already consumed — no C1 clear ever arrives). Cheap guard:
            // only runs while a dreamword is active. See TryCancelSpokenDreamword.
            if (_currentDreamword is not null)
                TryCancelSpokenDreamword(line);
            _combat.Observe(line, CombatClock());
            LineReady?.Invoke(line);
        };
        _parser.StatsUpdated += MergeStats;
        _parser.PersonaWiped += () => PersonaWiped?.Invoke();
        _parser.GameModeEntered += OnGameModeEntered;
        _parser.GameModeExited += OnGameModeExited;
        _parser.OutgoingBytes  += bytes => OutgoingBytes?.Invoke(bytes);
        _parser.BellReceived   += () => BellReceived?.Invoke();
        _parser.DreamwordChanged += OnDreamwordChanged;
        _parser.ClientModeReceived += data => ClientModeReceived?.Invoke(data);
        _parser.SoundRequested += s => SoundRequested?.Invoke(s);
        _parser.TellReceived += name => TellReceived?.Invoke(name);
        _parser.ProbeHintReceived += OnProbeHint;
        _parser.AutoResetInitiated += () =>
        {
            // Timing only. This fires on the WARNING - "Auto reset initiated, you have 120 seconds to
            // finish up" - not on the reset itself, so the fight in progress is still very much in
            // progress and the player has two minutes of play left.
            //
            // This used to also call _combat.ForceEnd here, on the reasoning that "a reset wipes game
            // state, no fight-end line ever arrives". True of the reset; false of the warning. The
            // effect was that the client declared combat over up to two minutes early and then
            // discarded every subsequent non-FightStart combat event (CombatStatsAggregator.Observe
            // returns early while !InCombat) - which silently ate weapon equips and left fights
            // reading as UNARMED for their whole duration. Confirmed in the clog corpus: an encounter
            // that reopened on "The eagle misses you." ran 41 events across 4 participants with no
            // weapon, having swallowed "You are now using the broadsword to fight!" in the pre-roll.
            //
            // The real transition is already covered - GameModeExited force-ends the encounter when
            // the reset actually lands - so this call was premature AND redundant.
            _resetClock.NoteAutoResetInitiated(_resetClock.NowMono);
            // Still forwarded: consumers use it to tell a reset-driven drop from a deliberate quit.
            // It is only the combat force-end that was wrong here.
            AutoResetInitiated?.Invoke();
        };
        _parser.PresenceNameSeen  += OnPresenceName;
        _parser.StatusEffectChanged += _effects.Apply;
        _effects.Changed += state => StatusEffectsChanged?.Invoke(state);
        _combat.InCombatChanged += v => InCombatChanged?.Invoke(v);
        _combat.GracePeriodChanged += v => CombatGracePeriodChanged?.Invoke(v);
        _combat.EventOccurred += e => CombatEventOccurred?.Invoke(e);
        _parser.FewPlayerReady += (name, color) =>
        {
            _pendingOnlineNames.Add(PlayerNameParts.Parse(name).PersonaName);
            FewPlayerReady?.Invoke(name, color);
        };
        _parser.FewListStarting  += () => { _pendingOnlineNames.Clear(); FewListStarting?.Invoke(); };
        _parser.FewListComplete  += () =>
        {
            _lastProbeReplyUtc = DateTime.UtcNow;
            _onlineNames.Clear();
            _onlineNames.UnionWith(_pendingOnlineNames);
            _pendingOnlineNames.Clear();
            ClearStale(StaleStats.WhoList);
            FewListComplete?.Invoke();
            // A sniff still in flight when this probe's FEW completes drew no `value` reply —
            // the reply always precedes the FEW in the same transmission — so the player is
            // online but invisible (the game says nothing). Fire AFTER FewListComplete so the
            // list diff runs before any promotion the outcome triggers.
            var pendingSniff = _sniffInFlight;
            if (pendingSniff != null)
                ResolveSniff(pendingSniff, SniffOutcome.Invisible);
        };
        _parser.RoomEntered      += () => { ArmRoomFexProbe(); RoomEntered?.Invoke(); };
        _parser.RoomShortReady   += name => RoomShortReady?.Invoke(name);
        _parser.FeiItemReady     += item => FeiItemReady?.Invoke(item);
        _parser.FeiListStarting  += () => FeiListStarting?.Invoke();
        _parser.FeiListComplete  += () =>
        {
            _lastProbeReplyUtc = DateTime.UtcNow;
            ClearStale(StaleStats.Inventory);
            FeiListComplete?.Invoke();
        };
        _parser.FexItemReady     += item => FexItemReady?.Invoke(item);
        _parser.FexListStarting  += () => { CancelRoomFexProbe(); FexListStarting?.Invoke(); };
        _parser.FexListComplete  += () => FexListComplete?.Invoke();
        _parser.ExitLineReady    += (dir, dest) => ExitLineReady?.Invoke(dir, dest);
        _parser.TerminalWidthConfirmed += w => TerminalWidthConfirmed?.Invoke(w);
    }

    private void MergeStats(GameStatsSnapshot partial)
    {
        // Stamp the reply arrival on the reset clock's monotonic clock BEFORE any merge work, so a
        // burst probe's RTT/observation-time correction uses the earliest possible reply instant.
        long replyMono = _resetClock.NowMono;

        // An FES snapshot is a probe reply — the panel data is fresh again.
        if (partial.HasFesStats)
            _lastProbeReplyUtc = DateTime.UtcNow;

        // Whatever values this update carries — a full FES snapshot or an inline text
        // line like "(84/90)" — are no longer stale, so a pending hint for them won't
        // trigger a probe.
        var refreshed = StaleStats.None;
        if (partial.HasFesStats)
        {
            refreshed = StaleStats.AllStats;
        }
        else
        {
            if (partial.Stamina      is not null || partial.MaxStamina   is not null) refreshed |= StaleStats.Stamina;
            if (partial.Strength     is not null || partial.RawStrength is not null || partial.MaxStrength  is not null) refreshed |= StaleStats.Strength;
            if (partial.Dexterity    is not null || partial.RawDexterity is not null || partial.MaxDexterity is not null) refreshed |= StaleStats.Dexterity;
            if (partial.CurrentMagic is not null || partial.MaxMagic     is not null) refreshed |= StaleStats.Magic;
            if (partial.Score        is not null || partial.ScoreThisGame is not null || partial.PlayerValue is not null) refreshed |= StaleStats.Score;
        }
        ClearStale(refreshed);

        // Keep _currentDreamword in sync when the dreamword arrives via text analysis
        // (pre-game path, DreamwordLineRegex) rather than the binary C15 decoder.
        // In game mode the C15 path fires DreamwordChanged which updates _currentDreamword
        // directly; in pre-game mode the text path is the only source.
        if (partial.DreamWord != null)
            _currentDreamword = partial.DreamWord;

        // Merge: only overwrite fields that are non-null in the partial snapshot.
        // Nullable int? means null="absent" rather than 0="actual zero", so a death
        // that legitimately sets Stamina=0 is correctly written to _currentStats.
        _currentStats = new GameStatsSnapshot(
            Stamina:      partial.Stamina      ?? _currentStats.Stamina,
            MaxStamina:   partial.MaxStamina   ?? _currentStats.MaxStamina,
            Score:        partial.Score        ?? _currentStats.Score,
            Strength:     partial.Strength     ?? _currentStats.Strength,
            RawStrength:  partial.RawStrength  ?? _currentStats.RawStrength,
            MaxStrength:  partial.MaxStrength  ?? _currentStats.MaxStrength,
            Dexterity:    partial.Dexterity    ?? _currentStats.Dexterity,
            RawDexterity: partial.RawDexterity ?? _currentStats.RawDexterity,
            MaxDexterity: partial.MaxDexterity ?? _currentStats.MaxDexterity,
            CurrentMagic: partial.CurrentMagic ?? _currentStats.CurrentMagic,
            MaxMagic:     partial.MaxMagic     ?? _currentStats.MaxMagic,
            ObjectsCarried:     partial.ObjectsCarried     ?? _currentStats.ObjectsCarried,
            MaxObjectsCarried:  partial.MaxObjectsCarried  ?? _currentStats.MaxObjectsCarried,
            Level:              partial.Level              ?? _currentStats.Level,
            GamesPlayed:        partial.GamesPlayed        ?? _currentStats.GamesPlayed,
            // Boolean flags from FES are authoritative (replace); from text-analysis they OR-accumulate.
            // This lets FES N-snapshots clear IsBlind/IsDeaf/IsCrippled/IsDumb/PersonaSaved when
            // the condition is gone, while text-analysis mentions (rare pre-game path) still set them.
            IsBlind:      partial.HasFesStats ? partial.IsBlind      : (partial.IsBlind      || _currentStats.IsBlind),
            IsDeaf:       partial.HasFesStats ? partial.IsDeaf       : (partial.IsDeaf       || _currentStats.IsDeaf),
            IsCrippled:   partial.HasFesStats ? partial.IsCrippled   : (partial.IsCrippled   || _currentStats.IsCrippled),
            IsDumb:       partial.HasFesStats ? partial.IsDumb       : (partial.IsDumb       || _currentStats.IsDumb),
            Weather:      partial.Weather     != ' ' ? partial.Weather   : _currentStats.Weather,
            TimeToReset:  partial.TimeToReset  ?? _currentStats.TimeToReset,
            DreamWord:    _currentDreamword,
            PersonaSaved: partial.HasFesStats ? partial.PersonaSaved : (partial.PersonaSaved || _currentStats.PersonaSaved),
            AccountId:    partial.AccountId    ?? _currentStats.AccountId,
            Privs:        partial.Privs        ?? _currentStats.Privs,
            StaminaColor: partial.StaminaColor ?? _currentStats.StaminaColor,
            // `score`-sheet-only fields. FES never carries them, so every heartbeat would otherwise
            // blank them; carrying forward is the same "null = not reported this time" rule the
            // fields above follow. They stay valid until the next sheet (see the periodic `score`
            // refresh) — sex never changes at all, and weight/objects/value change only on our own
            // actions, which is exactly what the refresh cadence is sized for.
            Sex:           partial.Sex           ?? _currentStats.Sex,
            ScoreThisGame: partial.ScoreThisGame ?? _currentStats.ScoreThisGame,
            PlayerValue:   partial.PlayerValue   ?? _currentStats.PlayerValue
        )
        {
            // Carry the freshness bit through the merge so consumers can tell a real FES reply from
            // a carried-forward value (combat/text lines re-emit the last stats). The reset-time
            // projection relies on this to only re-anchor on genuine readings.
            HasFesStats = partial.HasFesStats
        };
        // Fold the reset value into the projection. Called outside _fesLock (ClearStale above took and
        // released it) so the engine→_fesLock order holds when Observe fires a burst probe.
        _resetClock.Observe(_currentStats.TimeToReset, partial.HasFesStats, replyMono);
        StatsUpdated?.Invoke(_currentStats);
    }

    private void OnGameModeEntered()
    {
        _effects.Reset();   // fresh character — no effects carried from a previous session
        _resetClock.OnGameModeEntered();   // eligible for a fresh one-time reset-time refinement
        GameModeEntered?.Invoke();
        lock (_fesLock)
        {
            _lastProbeReplyUtc = DateTime.UtcNow;   // nothing is stale yet
            if (_fesInterval > TimeSpan.Zero)
            {
                // First beat populates everything: mark the FEI panel dirty so the entry probe is
                // the full FES,FEW,FEI.
                _lastFesSentUtc = DateTime.MinValue;
                _staleFlags |= StaleStats.Inventory;
                SendFesSubscription();
                _fesTimer = new Timer(_ => SendFesSubscription(), null, _fesInterval, _fesInterval);
            }
        }

        // Post-character-select setup. One batched write so the server processes it in order:
        //   auto fex  — enable the per-move front-end exit list
        //   score     — pull the character sheet (for the character name + a score baseline)
        // The echo + replies are hidden from the terminal (TrySwallowSetupLine), but the score
        // line's stats still reach the UI (the parser's analyzer fires StatsUpdated before
        // LineReady). Future user-defined setup commands slot in before `score`, which stays
        // LAST so its reply frame is the one that closes the swallow window.
        OpenSetupWindow();
        // The server echoes each command back on its own line, then executes them on subsequent
        // game turns — the outputs (auto-fex FEEXITS confirmation, then the score sheet) trickle
        // in over the next ~700ms. Both echoes and outputs are hidden (TrySwallowSetupLine).
        Send(System.Text.Encoding.Latin1.GetBytes(string.Join("\r\n", SetupCommands) + "\r\n"));

        // Request our first front-end exit list now (auto fex only arms it for future moves).
        Send(System.Text.Encoding.Latin1.GetBytes("\x1b-[FEX\x1b-]"));
    }

    private void OnGameModeExited()
    {
        StopFesTimer();
        lock (_fesLock)
        {
            StopStaleProbeLocked();
            StopRoomFexProbeLocked();
            // Drop mapping focus so the next game-mode entry includes FEI again. The session is
            // reused across reconnects/relogs, so stale focus would starve inventory updates.
            _mappingFocus = false;
            _pendingSniff = null;
            _sniffInFlight = null;
        }
        _onlineNames.Clear();
        _pendingOnlineNames.Clear();
        // Safety net: tear the setup window down on exit in case the score frame's closing
        // prompt never arrived. The character is gone until the next entry re-runs the batch.
        _setupWindowActive    = false;
        _setupSwallowingFrame = false;
        _setupCloseAfterFrame = false;
        _currentCharName      = null;
        _effects.Reset();     // relog/logout clears all effects
        _combat.ForceEnd(CombatClock());   // logout ends any open encounter — no fight-end line will arrive
        _resetClock.OnGameModeExited();   // drop the projection incl. the once-per-session token
        GameModeExited?.Invoke();
    }

    private void OnDreamwordChanged(string? word)
    {
        _currentDreamword = word;
        DreamwordChanged?.Invoke(word);
    }

    // The dreamword is a server-generated word given to sleeping players; the first to speak it
    // wins a random stamina refresh. When we speak it successfully the server both echoes our
    // speech and sends a C1 code that clears the dreamword (OnDreamwordChanged(null)). But when
    // the speak is a no-op — we spoke at full stamina, or someone drained the FIFO queue first —
    // no C1 clear arrives, and we would otherwise keep advertising a dead dreamword forever.
    // Detection: our own persona saying the exact current dreamword. Speaking uses it, full stop.
    // Runs on the Feed thread (LineReady), so no marshalling; _currentDreamword/_currentCharName
    // are only touched here and on that same thread.
    private void TryCancelSpokenDreamword(StyledLine line)
    {
        var word = _currentDreamword;
        if (word is null || _currentCharName is null)
            return;
        // Player speech is C1 code 09 → LineKind.Chat; anything else can't be a `says` line.
        if (line.Kind != LineKind.Chat)
            return;

        var text = line.PlainText;
        // Speaker must be our persona — including while we are invisible, when the game
        // parenthesises the whole name ("(Ollie the warlock) says ..."). This used to test the
        // raw prefix itself and so missed every invisible speak; PlayerNameParts.StartsWithPersona
        // owns the rule (and the "Ollie" must not match "Ollier" boundary) for both this and
        // SelfChatColorizer.
        if (!PlayerNameParts.StartsWithPersona(text, _currentCharName))
            return;

        // `... says "<word>"` — the quoted content must be exactly the current dreamword.
        const string verb = " says \"";
        var idx = text.IndexOf(verb, StringComparison.Ordinal);
        if (idx < 0)
            return;
        var start = idx + verb.Length;
        // Need room for the word plus its closing quote, and an exact word match ending on that
        // quote (so "wordy" doesn't satisfy a "word" dreamword).
        if (start + word.Length + 1 > text.Length)
            return;
        if (string.CompareOrdinal(text, start, word, 0, word.Length) != 0)
            return;
        if (text[start + word.Length] != '"')
            return;

        // Clear at the parser too (not just _currentDreamword): FES snapshots carry
        // _parser.CurrentDreamword, so a stale value there would resurrect it on the next probe.
        // EmitDreamwordChanged fires DreamwordChanged → OnDreamwordChanged, syncing session state.
        _parser.EmitDreamwordChanged(null);
    }

    private void SendFesSubscription()
    {
        byte[] payload;
        lock (_fesLock)
        {
            if (_probesHeld) return;   // skipped beat; SetProbeHold(false) re-phases the tick
            // A reset-time discovery pass owns the wire: defer this beat so its rate-limited FES samples
            // aren't raced by this compound reply. The pass lasts only seconds, so the beat is delayed,
            // not dropped; SetResetDiscoveryHold(false) re-phases the tick when it ends.
            if (_resetDiscoveryHold || _resetClock.IsSamplingInFlight) return;
            var now = DateTime.UtcNow;
            payload = ComposeBeatLocked(out bool fes, out bool few, out bool fei);
            _lastProbeSentUtc = now;
            _lastFesSentUtc = now;
            _nextRoutineProbeUtc = now + _fesInterval;
            // The beat refreshes only what it carries — clear exactly those pending flags.
            var carried = StaleStats.None;
            if (fes) carried |= StaleStats.AllStats;
            if (few) carried |= StaleStats.WhoList;
            if (fei) carried |= StaleStats.Inventory;
            _staleFlags &= ~carried;
            // Ride a queued sniff (value <name>) on this probe, but only when the probe carries
            // FEW: the FEW-complete boundary is what closes out an invisible (no-reply) sniff, so
            // a FEW-less probe could never resolve it. LIFO — one sniff per probe.
            if (_pendingSniff is { } sniff && few)
            {
                _sniffInFlight = sniff;
                _pendingSniff = null;
                var prefix = System.Text.Encoding.Latin1.GetBytes("value " + sniff + "\r\n");
                var combined = new byte[prefix.Length + payload.Length];
                Buffer.BlockCopy(prefix, 0, combined, 0, prefix.Length);
                Buffer.BlockCopy(payload, 0, combined, prefix.Length, payload.Length);
                payload = combined;
            }
        }
        OutgoingBytes?.Invoke(payload);
        ProbeSent?.Invoke();
    }

    /// <summary>
    /// Queue a "sniff" probe for <paramref name="name"/>: the next routine FES heartbeat is
    /// prefixed with <c>value &lt;name&gt;</c> so we can tell whether a player who fell off the
    /// Online list is present/visible, logged out, or invisible (see <see cref="SniffResult"/>).
    /// LIFO — a newer request replaces an unsent one, since only one sniff rides each probe.
    /// May be called from any thread.
    /// </summary>
    public void QueueValueProbe(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        lock (_fesLock)
            _pendingSniff = name.Trim();
    }

    // Feed thread. Returns true when the line is the echo or reply of the in-flight sniff and
    // should be swallowed (never shown in the terminal). Resolves SniffResult on the reply.
    private bool TryConsumeSniffLine(StyledLine line)
    {
        var name = _sniffInFlight;
        if (name is null) return false;
        var text = line.PlainText.Trim('\r', '\n', '\0', ' ');
        // Echo of the command we injected — swallow but keep waiting for the reply.
        if (text.Equals("value " + name, StringComparison.OrdinalIgnoreCase))
            return true;
        // Outcome 1 — present & visible: "The value of {name} the {title} is {n} points."
        if (text.StartsWith("The value of ", StringComparison.Ordinal) &&
            text.EndsWith("points.", StringComparison.Ordinal) &&
            text.Contains(name, StringComparison.OrdinalIgnoreCase))
        {
            ResolveSniff(name, SniffOutcome.Present);
            return true;
        }
        // Outcome 2 — logged out: "I don't know the word \"{name}\"."
        if (text.StartsWith("I don't know the word \"", StringComparison.Ordinal) &&
            text.Contains(name, StringComparison.OrdinalIgnoreCase))
        {
            ResolveSniff(name, SniffOutcome.Offline);
            return true;
        }
        return false;
    }

    private void ResolveSniff(string name, SniffOutcome outcome)
    {
        _sniffInFlight = null;
        SniffResult?.Invoke(name, outcome);
    }

    // ── Post-character-select setup swallow ─────────────────────────────────────
    // Commands injected on game-mode entry, in order. `score` MUST stay last: its reply frame is
    // the one whose closing prompt shuts the swallow window. Future user-defined setup commands go
    // BEFORE `score`. (A trailing CTRL-T "done" sentinel was tried and removed — the server
    // answers CTRL-T on receipt, ~500ms ahead of the queued command outputs, so it cannot mark
    // completion; verified from a live session recording 2026-07-09.)
    private static readonly string[] SetupCommands = { "auto fex", "score" };

    // First line of the `score` sheet: "name:           Ollie". A leading frame prompt ("*name:")
    // is stripped before matching. Character names are single tokens, so the "name:" line never
    // wraps — a reliable frame-start marker even at narrow terminal widths.
    private static readonly System.Text.RegularExpressions.Regex SetupNameRegex = new(
        @"^name:\s+(\S+)",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // Feed thread. Returns true when the line belongs to the injected setup batch and should be
    // swallowed. Works by frame, not by matching every line: each server reply arrives as a frame
    // led by an IsPartial '*' prompt, so we recognise a setup frame from its FIRST content line
    // (echo / FEEXITS / "name:" — all at column 0, so wrapping never hides them) and then swallow
    // every line of that frame up to the next prompt. This is width-independent: a wrapped reply
    // just adds more content lines inside the same frame (the old per-line label match leaked the
    // moment a value wrapped). Player chatter arrives in its own frame, is not claimed, and still
    // shows. The score frame is the last we claim; its closing prompt shuts the window. Called
    // only while _setupWindowActive.
    private bool TrySwallowSetupLine(StyledLine line)
    {
        // Frame boundary: the '*' prompt that leads every frame. Let it render as the prompt, but
        // use it to delimit frames — and to close the window once the score frame has ended.
        if (line.IsPartial)
        {
            if (_setupCloseAfterFrame)
            {
                _setupWindowActive    = false;
                _setupCloseAfterFrame = false;
            }
            _setupSwallowingFrame = false;   // a new frame begins; re-decide on its first line
            return false;                    // show the prompt (rendered in place, as normal)
        }

        // Already inside a setup frame we've claimed — swallow the rest of it (wrapped
        // continuations included) until the next prompt clears _setupSwallowingFrame.
        if (_setupSwallowingFrame)
            return true;

        // First content line of a fresh frame — decide whether the setup batch owns it.
        var text = line.PlainText.Trim('\r', '\n', '\0', ' ');
        var body = StripLeadingPrompt(text);   // a frame prompt can glue onto the first line

        // Command echoes — the server echoes each injected command on its own line.
        foreach (var cmd in SetupCommands)
            if (body.Equals(cmd, StringComparison.OrdinalIgnoreCase))
                return ClaimFrame();

        // `auto fex` confirmation frame — opens "You will now get an automatic FEEXITS ...".
        if (body.Contains("FEEXITS", StringComparison.OrdinalIgnoreCase) ||
            body.StartsWith("You will now get an automatic", StringComparison.OrdinalIgnoreCase))
            return ClaimFrame();

        // `score` sheet frame — opens on the "name:" line, which yields the character name and is
        // the LAST frame we claim, so arm the window to close when this frame's prompt arrives.
        var nm = SetupNameRegex.Match(body);
        if (nm.Success)
        {
            SetCurrentCharacter(nm.Groups[1].Value);
            _setupCloseAfterFrame = true;
            return ClaimFrame();
        }

        return false;   // not ours (e.g. player chatter) — show it

        bool ClaimFrame()
        {
            _setupSwallowingFrame = true;
            return true;
        }
    }

    /// <summary>
    /// Opens the swallow window ahead of injecting the post-character-select setup batch. Only
    /// caller is <see cref="OnGameModeEntered"/>, on the Feed thread. The frame fields are reset
    /// BEFORE the volatile <see cref="_setupWindowActive"/> store purely as good publication order,
    /// not because another thread can observe this window mid-open.
    /// </summary>
    private void OpenSetupWindow()
    {
        _setupSwallowingFrame = false;
        _setupCloseAfterFrame = false;
        _setupWindowActive    = true;   // volatile store — publishes the two writes above
    }

    // Strip a leading frame prompt ("*", "(*)", surrounding spaces) that the server glues onto the
    // first reply line of a frame. Conservative: only the prompt punctuation, never letters.
    private static string StripLeadingPrompt(string text)
    {
        int i = 0;
        while (i < text.Length && (text[i] is '*' or '(' or ')' or ' '))
            i++;
        return i == 0 ? text : text[i..];
    }

    private void SetCurrentCharacter(string name)
    {
        if (string.IsNullOrEmpty(name) || name == _currentCharName)
            return;
        _currentCharName = name;
        CharacterIdentified?.Invoke(name);
    }

    // ── Resite/supersite recovery probe ─────────────────────────────────────────

    /// <summary>
    /// A room description just arrived (RoomEntered). Arm a short one-shot timer: if
    /// FexListStarting fires first, this was ordinary auto-fex-covered movement and
    /// CancelRoomFexProbe stops it before it does anything. If the timer elapses with no FEX
    /// having started, the relocation was spell-driven (resite/supersite or similar) and no
    /// auto commands ever fired, so send the explicit probe ourselves. Skipped during the
    /// post-select setup window, which already sends its own explicit entry-time probe.
    /// </summary>
    private void ArmRoomFexProbe()
    {
        if (_setupWindowActive) return;
        lock (_fesLock)
        {
            _roomFexProbeTimer ??= new Timer(_ => OnRoomFexProbeDeadline(), null, Timeout.Infinite, Timeout.Infinite);
            _roomFexProbeTimer.Change(_options.RoomEntryFexProbeDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>A FEX list has started arriving — the pending recovery probe (if any) is moot.</summary>
    private void CancelRoomFexProbe()
    {
        lock (_fesLock)
            _roomFexProbeTimer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// The recovery window elapsed with no FEX list ever starting. Re-send the same explicit
    /// FEX probe used at game-mode entry so the exit list/compass/map recover without waiting
    /// for the player's next real move.
    /// </summary>
    private void OnRoomFexProbeDeadline()
    {
        // Locked like every other timer callback in this file (SendFesSubscription,
        // OnStaleDeadline): without it, a concurrent OnGameModeExited/Dispose could tear the
        // session down between the InGameMode check and Send, sending bytes into a dead session.
        lock (_fesLock)
        {
            if (!InGameMode) return;   // disconnected / logged out since the room description arrived
            Send(System.Text.Encoding.Latin1.GetBytes("\x1b-[FEX\x1b-]"));
        }
    }

    private void StopRoomFexProbeLocked()
    {
        _roomFexProbeTimer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Hold (true) / release (false) the FES/FEW/FEI probe machinery. Used by the
    /// mapping console around its operations: probe responses end in a prompt redraw
    /// and would interleave with a capture in flight. Releasing re-phases the routine
    /// tick a full interval out -- a held beat is delayed, never dropped forever.
    /// </summary>
    public void SetProbeHold(bool held)
    {
        lock (_fesLock)
        {
            if (_probesHeld == held) return;
            _probesHeld = held;
            if (!held && _fesTimer is not null && _fesInterval > TimeSpan.Zero)
            {
                _nextRoutineProbeUtc = DateTime.UtcNow + _fesInterval;
                _fesTimer.Change(_fesInterval, _fesInterval);
            }
        }
    }

    // ── Reactive stale-stats probing ───────────────────────────────────────────

    /// <summary>
    /// A C1 code hinted that the given categories may be out of date. Mark them stale
    /// and arm the one-shot probe timer — unless reactive probing is disabled (heartbeat
    /// interval 0) or the routine probe is due soon enough to cover them.
    /// </summary>
    private void OnProbeHint(StaleStats kinds)
    {
        if (kinds == StaleStats.None) return;
        lock (_fesLock)
        {
            if (_fesInterval <= TimeSpan.Zero || !InGameMode)
                return;
            // Record ALL hinted categories — the routine beat composes FEI from the Inventory flag,
            // so the fact must be kept even when no off-cadence probe fires (e.g. beat imminent).
            _staleFlags |= kinds;
            // Only who-list / inventory staleness warrants an off-cadence probe. Stat categories
            // are advisory: combat deltas arrive as inline text ("(84/90)") and the next routine
            // probe catches anything else — rapid-firing on every combat code was pure noise
            // (probe-noise policy, 2026-07-25).
            if ((kinds & (StaleStats.WhoList | StaleStats.Inventory)) == StaleStats.None)
                return;
            if (_nextRoutineProbeUtc - DateTime.UtcNow <= _options.MinProbeSpacing)
                return;   // beat imminent — it will carry the flagged parts
            if (!_staleArmed)
            {
                _staleArmed = true;
                _staleTimer ??= new Timer(_ => OnStaleDeadline(), null, Timeout.Infinite, Timeout.Infinite);
                _staleTimer.Change(_options.StaleProbeDelay, Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <summary>
    /// The grace period after a stale hint has elapsed. Query whatever is still stale —
    /// values that arrived on their own in the meantime have already cleared their flags.
    /// </summary>
    private void OnStaleDeadline()
    {
        byte[] probe;
        lock (_fesLock)
        {
            _staleArmed = false;
            if (_fesInterval <= TimeSpan.Zero || !InGameMode)
            {
                _staleFlags = StaleStats.None;
                return;
            }
            if (_staleFlags == StaleStats.None)
                return;
            if (_probesHeld || _resetDiscoveryHold || _resetClock.IsSamplingInFlight)
            {
                // A mapping operation or a reset-time discovery pass owns the wire; keep the flags and retry.
                _staleArmed = true;
                _staleTimer?.Change(_options.StaleProbeDelay, Timeout.InfiniteTimeSpan);
                return;
            }
            var now = DateTime.UtcNow;
            // Honour the global probe-spacing floor; try again once it has elapsed.
            var wait = _options.MinProbeSpacing - (now - _lastProbeSentUtc);
            if (wait > TimeSpan.Zero)
            {
                _staleArmed = true;
                _staleTimer?.Change(wait, Timeout.InfiniteTimeSpan);
                return;
            }
            // Routine probe now imminent — keep the flags; the beat composes FEW every time and
            // FEI from the Inventory flag, so it will carry the stale parts itself.
            if (_nextRoutineProbeUtc - now <= _options.MinProbeSpacing)
                return;
            // Off-cadence probes are triggered only by who-list / inventory staleness, but FES
            // must lead every query so the server reliably refreshes the requested sections.
            bool few = (_staleFlags & StaleStats.WhoList)   != 0 && (_includeFew || _mappingFocus);
            bool fei = (_staleFlags & StaleStats.Inventory) != 0 && _includeFei && !_mappingFocus;
            if (!few && !fei)
                return;
            var carried = StaleStats.AllStats;
            if (few) carried |= StaleStats.WhoList;
            if (fei) carried |= StaleStats.Inventory;
            _staleFlags &= ~carried;
            _lastProbeSentUtc = now;
            _lastFesSentUtc = now;
            var cmds = new List<string>(3) { "FES" };
            if (few) cmds.Add("FEW");
            if (fei) cmds.Add("FEI");
            probe = System.Text.Encoding.Latin1.GetBytes("\x1b-[" + string.Join(',', cmds) + "\x1b-]");
        }
        OutgoingBytes?.Invoke(probe);
        ProbeSent?.Invoke();
    }

    /// <summary>
    /// A player name was seen bracketed by a C05 presence code — that player is online.
    /// If they are missing from the last complete FEW response, the Online list is stale.
    /// The bracketed text may be a full persona ("Polly the witch"), a titled level-10
    /// mortal ("Lady Polly"), or run on into the sentence.
    /// </summary>
    private void OnPresenceName(string name)
    {
        // No baseline yet — nothing to compare against; the routine probe establishes one.
        if (_onlineNames.Count == 0)
            return;
        if (!_onlineNames.Contains(PlayerNameParts.Parse(name).PersonaName))
            OnProbeHint(StaleStats.WhoList);
    }

    private void ClearStale(StaleStats kinds)
    {
        if (kinds == StaleStats.None) return;
        lock (_fesLock)
            _staleFlags &= ~kinds;
    }

    private void StopStaleProbeLocked()
    {
        _staleArmed = false;
        _staleFlags = StaleStats.None;
        _staleTimer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// BUGS #5: if the server just sent real data but the last FES-carrying probe never drew its
    /// stats reply (the character was asleep — FES/FEI/FEW no-op during sleep), fire the heartbeat
    /// now and re-phase its period, so the panel recovers on wake instead of waiting out the
    /// current interval. Rate-limited so a wake-up text burst fires only one early probe.
    ///
    /// Staleness is judged by the FES send/reply pairing, NOT by reply age alone. If a probe's
    /// mandatory FES remains unanswered past WakeReplySlack, the first incoming bytes are treated
    /// as a wake-up signal.
    /// </summary>
    private void MaybeSendWakeProbe()
    {
        if (_fesInterval <= TimeSpan.Zero || !InGameMode)
            return;
        if (_resetDiscoveryHold || _resetClock.IsSamplingInFlight)   // discovery owns the wire
            return;
        var now = DateTime.UtcNow;
        if (_lastFesSentUtc <= _lastProbeReplyUtc)     // last FES-carrying probe was answered
            return;
        if (now - _lastFesSentUtc <= _options.WakeReplySlack)  // in flight — give the reply time to land
            return;
        if (now - _lastWakeProbeUtc < WakeProbeFloor)
            return;
        _lastWakeProbeUtc = now;
        lock (_fesLock)
        {
            // The recovery beat carries FES because only a stats reply proves we're awake again.
            _lastFesSentUtc = DateTime.MinValue;
            _fesTimer?.Change(TimeSpan.Zero, _fesInterval);   // fire immediately, keep the period
        }
    }

    // ── Reset-time discovery (driven by ResetClock) ─────────────────────────────
    // ResetClock owns the one-time edge search; these are its wire hooks. While discovering it holds
    // the routine heartbeat suspended (SetResetDiscoveryHold) so a compound reply never races its
    // rate-limited FES samples, and its samples deliberately BYPASS MinProbeSpacing (they are self-paced
    // at ≥ ~501 ms). The global spacing floor still guards every reactive/routine path.

    /// <summary>Can a discovery probe usefully go out right now? (In game, heartbeat enabled, not held.)</summary>
    private bool CanResetProbe()
    {
        lock (_fesLock)
            return InGameMode && _fesInterval > TimeSpan.Zero && !_probesHeld;
    }

    /// <summary>Send one lone FES probe for reset discovery. Returns false if it can't go out right now.</summary>
    private bool TrySendResetFesProbe()
    {
        lock (_fesLock)
        {
            if (!InGameMode || _fesInterval <= TimeSpan.Zero || _probesHeld)
                return false;
            var now = DateTime.UtcNow;
            _lastProbeSentUtc = now;
            _lastFesSentUtc = now;
        }
        OutgoingBytes?.Invoke(FesOnlyProbe);
        ProbeSent?.Invoke();
        return true;
    }

    /// <summary>Suspend (true) / resume (false) the routine heartbeat while a reset-discovery pass owns
    /// the channel. Resuming fires a beat IMMEDIATELY (then keeps the period): the pass already
    /// suppressed beats for several seconds, and re-phasing a full interval out on top of that pushed
    /// the panel past its stale threshold at every retried minute boundary — the "status updates
    /// aren't regular" complaint. The channel is free the instant the hold drops, so an immediate
    /// compound probe is safe. Outside game mode, just restore the period. Called by ResetClock.</summary>
    private void SetResetDiscoveryHold(bool held)
    {
        lock (_fesLock)
        {
            if (_resetDiscoveryHold == held) return;
            _resetDiscoveryHold = held;
            if (!held && _fesTimer is not null && _fesInterval > TimeSpan.Zero)
            {
                var due = InGameMode ? TimeSpan.Zero : _fesInterval;
                _nextRoutineProbeUtc = DateTime.UtcNow + due;
                _fesTimer.Change(due, _fesInterval);
            }
        }
    }

    private void StopFesTimer()
    {
        lock (_fesLock)
            StopFesTimerLocked();
    }

    private void StopFesTimerLocked()
    {
        _fesTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _fesTimer?.Dispose();
        _fesTimer = null;
    }
}
