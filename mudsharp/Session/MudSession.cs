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
    // -- Reactive stale-stats probing -------------------------------------------
    // C1 codes hint that state changed (ProbeHintReceived). Rather than probing
    // instantly (Clio's txfes), the hinted categories are marked stale and a one-shot
    // timer fires StaleProbeDelay later; updates that arrive in the meantime - the
    // inline "(84/90)" after a hit, an unsolicited FES - clear their flags so only
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
    // -- "Sniff" value-probe state -----------------------------------------------
    // A `value <name>` command prepended to a routine probe to disambiguate a player who
    // dropped off the FEW list (see QueueValueProbe / SniffResult). _pendingSniff (a queued
    // request) is guarded by _fesLock; _sniffInFlight is volatile so the Feed-thread line
    // filter can fast-check it without taking the lock on every line.
    private string? _pendingSniff;
    private volatile string? _sniffInFlight;

    // -- Resite/supersite recovery probe -----------------------------------------
    // Ordinary movement's room description is always followed by an auto-fex FEEXITS block in
    // the same transmission. A spell-driven relocation (resite, supersite, and any future
    // mechanic shaped the same way) fires no auto commands at all, so RoomEntered arrives with
    // no FEX to follow. One-shot timer armed on RoomEntered, cancelled by FexListStarting (the
    // earliest signal a FEX is genuinely on the way); if it elapses unanswered, fire the same
    // explicit FEX probe used at game-mode entry. Guarded by _fesLock like the other one-shot
    // timers in this file.
    private IOneShotTimer? _roomFexProbeTimer;

    // -- In-combat inventory probe -----------------------------------------------
    // A drop or a take during a fight changes the two numbers a fight is decided by - dexterity is
    // burdened by item COUNT and strength by WEIGHT - and until this existed nothing asked
    // the server about it on purpose. The generic plain-line hint happened to cover most drops
    // (measured across 36 captures: median 209ms from "X dropped." to the next FES-carrying probe,
    // 85% within 500ms), but only because a drop prints an un-coded line, and not at all when the
    // side panel's item sections are collapsed - OnStaleDeadline's `fei` term is gated on
    // _includeFei, and with no FEW pending either it returns having sent nothing.
    //
    // Trailing debounce, deliberately: the timer is RESTARTED by each change line, so a burst is
    // one probe. FES and FEI each cost the player a tick and ticks are short, but three
    // items must not cost six.
    private static readonly byte[] InventoryProbe = System.Text.Encoding.Latin1.GetBytes("\x1b-[FES,FEI\x1b-]");
    private Timer? _inventoryProbeTimer;
    // Volatile so SendLine can reject the overwhelmingly common case with one field read and no
    // lock. Set on the Feed thread; cleared by whichever of the two paths - the timer or a player
    // dispatch riding it out - claims it first.
    private volatile bool _inventoryProbePending;
    // When the change that armed the pending probe was seen. Any FES-carrying probe sent AFTER this
    // instant has already asked the server about the new loadout, so the pending one is redundant
    // and is abandoned rather than spending a second tick on the same question - see
    // TakeInventoryProbeLocked. This is not rare: combat prints un-coded lines constantly, so the
    // generic stale timer is often already part-way through its own delay when a drop lands.
    private DateTime _inventoryChangeSeenUtc;

    // -- In-combat creature value probe ------------------------------------------
    // `val`/`value <name>` reports the points awarded for killing that creature; multiple targets
    // in one command ("value x and y and z") cost ONE server tick, so one probe covers the whole
    // live roster rather than one per creature.
    //
    // Fired off CombatTracker.ParticipantJoined, not off FightStart alone: several combat lines
    // Begin() a participant defensively for a pack member that spoke no aggro line of its own
    // (YouHit/NpcHitsYou/NpcWeaponEquip's own remarks), and ParticipantJoined is what catches those
    // too - it fires exactly when a name becomes newly active, regardless of which line did it. A
    // creature that joins mid-fight re-arms the same trailing debounce as the one already pending,
    // so a pack that arrives over several seconds still costs one probe per quiet period rather than
    // one per arrival - the same batching InventoryProbeDebounce gives the loadout probe below.
    //
    // Two things this probe does NOT share with the routine FES/FEW/FEI heartbeat, deliberately:
    //   1. It never rides the heartbeat's FEW gate the player "sniff" probe uses (_pendingSniff /
    //      _sniffInFlight) - that gate exists only because FEW-completion is what times out an
    //      INVISIBLE PLAYER (no reply ever arrives), which has no meaning for a creature reply.
    //   2. It is sent as its own line, not composed into ComposeBeatLocked's escape - `value` is an
    //      ordinary typed command, not an FES/FEW/FEI subscription component.
    // So it needs its own in-flight slot, discriminated by target from the player sniff's, rather
    // than sharing (and colliding with) that one.
    //
    // That discrimination is necessary but was not, on its own, sufficient: both probes' replies
    // share the exact prose shape "The value of ... points.", so until TryConsumeSniffLine anchored
    // its match on where the sniffed NAME sits in the line (see that method), a live creature reply
    // ("The value of the ram2 is 313 points.") could satisfy the sniff's old bare Contains(name)
    // check and get misread as proof a same-named PLAYER was present - a false positive in the
    // PK-awareness path of a permadeath game. The two slots only stay independent because BOTH
    // sides now discriminate: this one by target name, the sniff by the name's anchored position in
    // the reply text.
    //
    // Debounced with the SAME trailing-quiet-period + tick-guard shape as the inventory probe below
    // (ScheduleInventoryProbeLocked), including its options - InventoryProbeDebounce/TickGuard/
    // TickClearance - rather than inventing a second scheduler for what is the same problem (don't
    // fire on every arrival; don't fire onto a tick boundary the player's own command wanted).
    // Value can be NEGATIVE and the noun agrees with it, so neither the sign nor the plural is
    // optional decoration: over the session recordings in %LOCALAPPDATA%\Temp\mucka plus
    // ~/.mucka/clogs (458 "The value of" lines, 246 distinct), 36 occurrences across 21
    // distinct forms are negative ("The value of the map is -12 points.") and 6 across 5 are
    // singular ("The value of the penny is 1 point."). Every one of those uses this "the" form, so
    // a [\d,]+ / "points." pattern drops all 42 on the floor. Negatives are inanimate only - no
    // bestiary creature has one - but they arrive through the same probe, so they are this
    // pattern's problem. Query: grep -o 'The value of [^"]*' over both corpora.
    private static readonly System.Text.RegularExpressions.Regex CreatureValueReply = new(
        @"^The value of the (?<name>.+?) is (?<value>-?[\d,]+) points?\.$",
        System.Text.RegularExpressions.RegexOptions.Compiled);
    // "I don't know to what \"vase1\" you're referring." - the bad-target rejection. Different
    // wording from the player sniff's "I don't know the word" (that one means the PERSONA name is
    // unknown at all; this one means the OBJECT reference did not resolve), so it gets its own
    // pattern rather than being folded into TryConsumeSniffLine's.
    private static readonly System.Text.RegularExpressions.Regex CreatureValueBadTarget = new(
        @"^I don't know to what ""(?<name>.+?)"" you're referring\.$",
        System.Text.RegularExpressions.RegexOptions.Compiled);
    // Names already answered THIS ENCOUNTER - cleared at the start of every new one (see
    // OnParticipantJoined). Deliberately NOT session-lifetime: a creature's value is not a fixed
    // per-species constant, it is CUMULATIVE and climbs with whatever that individual has scored
    // since it was created - swamping items day to day, and in big jumps off a player's death or a
    // failed flee. Corpus evidence for the same creature NAME across separate
    // fights: ram 106 -> 129 -> 313, dwarf 12 -> 78 -> 102, billy goat 74 -> 118 -> 146, banshee
    // 102 -> 143. A session-lifetime cache would answer a later fight against the same name from a
    // stale pre-climb (or, across a reset, pre-reset - resets pull the value back toward base; a
    // thief usually only gets 1-2 levels in a reset) reading and never notice the climb this
    // feature exists to surface. Per-fight is the rule: this only needs doing once per fight, read
    // literally. Guarded by _fesLock; the Feed thread takes the lock to write it, exactly as
    // ClearStale already does from that thread for _staleFlags.
    private readonly HashSet<string> _creatureValueKnown = new(StringComparer.OrdinalIgnoreCase);
    // Names seen but not yet sent - guarded by _fesLock, like _pendingSniff.
    private readonly List<string> _pendingCreatureNames = new();
    private Timer? _creatureValueProbeTimer;
    // The batch actually sent, and its exact echo text, so the Feed-thread line filter can swallow
    // the echo and attribute replies without positional pairing (one request can draw more or fewer
    // reply lines than names sent - e.g. a prefix like "gg" answering both gargoyle0 and gargoyle1 -
    // so a reply is matched by NAME against this set, never by position). Set once, under _fesLock,
    // when the probe is composed; the Feed thread only reads the reference (volatile, mirroring
    // _sniffInFlight) and never mutates its contents, and clears it back to null itself once every
    // requested name has been accounted for AND the frame boundary following that point has arrived
    // (see TryConsumeCreatureValueLine/_creatureProbeUnresolved) - or once the timeout backstop gives
    // up on a name that never gets accounted for at all (_creatureProbeTimeoutTimer).
    private volatile IReadOnlyList<string>? _creatureProbeInFlight;
    private volatile string? _creatureProbeEcho;
    // Requested names not yet accounted for (a reply or bad-target rejection seen) THIS batch -
    // composed fresh under _fesLock alongside the two fields above; the Feed thread removes names
    // from it as it resolves them (see NoteCreatureProbeNameAccountedFor). Once empty, the window
    // is armed to close but does not close outright until the NEXT frame boundary (see
    // TryConsumeCreatureValueLine) - see _creatureProbeReadyToClose.
    private volatile HashSet<string>? _creatureProbeUnresolved;
    // Set once every requested name has been accounted for; read/written on the Feed thread only
    // except for the benign race with the timeout backstop's force-close (a torn read costs at most
    // one missed close-on-next-frame, never a correctness issue - see CloseCreatureValueProbeWindow).
    private volatile bool _creatureProbeReadyToClose;
    // Backstop that force-closes the window if it is never fully accounted for (see
    // MudSessionOptions.CreatureValueProbeTimeout) - guards against a name that can legitimately
    // never draw a reply wedging the window open past its usefulness.
    private Timer? _creatureProbeTimeoutTimer;

    // -- Post-character-select setup swallow state -------------------------------
    // On game-mode entry we inject a setup batch ("auto fex\r\nscore\r\n") and hide its echo +
    // replies from the terminal (TrySwallowSetupLine). Each reply arrives as its own server
    // "frame", and every frame is introduced by an IsPartial '*' prompt line - a boundary that
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
    private volatile bool _setupWindowActive;  // window open (game-entry -> score frame closed)
    private bool _setupSwallowingFrame;   // inside a setup frame we've claimed - swallow its lines
    private bool _setupCloseAfterFrame;   // the score frame is in progress; close when it ends
    private string? _currentCharName;

    private GameStatsSnapshot _currentStats = GameStatsSnapshot.Empty;
    private string? _currentDreamword;
    // Periodic probe: composed per beat (ComposeBeatLocked) - FES always leads because the server
    // does not reliably refresh FEW/FEI without it; FEW rides every beat, and FEI only when marked
    // dirty by a C1 hint. FEW and FEI are omitted when those side-panel sections are disabled.
    private bool _includeFew = true;
    private bool _includeFei = true;
    private readonly EffectTracker _effects = new();
    private readonly CombatTracker _combat = new();

    // Last room short description seen at column 0. Feed thread only, like every other field here.
    // Read by NoteRoomShort to tell a real move from a `look` at the room already occupied.
    private string? _lastRoomShort;

    // Testability seam only: production code never overrides this, so combat timestamps are
    // always the real wall clock. WyvernPoisonDeathReplayTests overrides it to replay a captured
    // session's own original timestamps, since a fast in-memory replay's real elapsed time bears
    // no relation to the many-hour session it captures and would otherwise stamp every event with
    // whatever instant the test happened to run at.
    internal Func<DateTime> CombatClock { get; set; } = () => DateTime.UtcNow;

    // Testability seam only: production code never overrides this, so the room-entry recovery probe
    // always runs on a real System.Threading.Timer. RoomEntryFexProbeTests substitutes a timer with a
    // virtual clock it advances by hand, so the probe's deadline arithmetic is asserted rather than
    // slept against. Invoked once per session, under _fesLock, on the first room entry; the instance
    // is reused from then on, so a factory set after that point is silently never called.
    internal Func<Action, IOneShotTimer> OneShotTimerFactory { get; set; } = callback => new ThreadingOneShotTimer(callback);

    /// <summary>
    /// Milliseconds from now to the next MUD2 combat-tick boundary, or null while the phase is
    /// unknown. Supplied by the layer that owns the estimate (Mucka.Core.TickPhase, published as
    /// SidePanelViewModel.TickPhaseUtc and resolved through CombatTiming.MillisecondsToNextBoundary)
    /// rather than re-derived here, so there is exactly one lattice in the client.
    ///
    /// <para>A delegate rather than a pushed value for the same reason ClogWriter's reset estimate
    /// is one: the answer is only meaningful at the instant it is asked, and it refines continuously
    /// as the estimate converges.</para>
    ///
    /// <para>Called from the probe timer's thread while the estimate is updated on the UI thread, so
    /// the read is unsynchronised. Deliberately: the resolver normalises any input to a value in
    /// (0, tick], so the worst a stale or torn read can do is place one probe on the wrong side of
    /// one boundary - and the probe is not critical but very useful.</para>
    /// </summary>
    public Func<double?>? MillisecondsToNextCombatTick { get; set; }

    // Reset-time projection: folds the minute-granular FES reset value into an absolute target and,
    // once per session near the start, runs a staged burst (~1 s then ~250 ms probes) to pin it to
    // sub-second. Owned here (not the VM) so all sub-second probe timing stays off the UI thread and
    // reply<->probe correlation sits next to the wire. See ResetClock.
    private readonly ResetClock _resetClock;

    // -- Public events (forwarded from parser) ---------------------------------
    public event Action<StyledLine>? LineReady;
    public event Action<GameStatsSnapshot>? StatsUpdated;
    /// <summary>The server's C08+C13 ("Not updating persona.") signal: permadeath wiped the
    /// current persona. Fires alongside <see cref="StatsUpdated"/>'s zeroed snapshot.</summary>
    public event Action? PersonaWiped;

    /// <summary>MUD2 announced a score change - see <see cref="MudStreamParser.ScoreSaved"/> and
    /// <see cref="ScoreSave"/>. The only place the client is told what an event was WORTH; a kill's
    /// award and a flee's cost both arrive here and nowhere else.</summary>
    public event Action<ScoreSave>? ScoreSaved;

    /// <summary>One of the eight tasks was discharged - see <see cref="MudStreamParser.TaskCompleted"/>
    /// and <see cref="TaskCompletion"/>. Fires ahead of the <see cref="ScoreSaved"/> for the task's own
    /// payout, which is the fact consumers actually need from it.</summary>
    public event Action<TaskCompletion>? TaskCompleted;
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
    /// <summary>One creature-presence sentence from a room description or an arrive/depart line, as
    /// the game worded it. The only evidence MUD2 gives about which names in the FEI "here" list are
    /// alive - see MudStreamParser.CreatureTextReady.</summary>
    public event Action<string>? CreatureTextReady;
    public event Action<string>? FexItemReady;
    public event Action? FexListStarting;
    public event Action? FexListComplete;
    /// <summary>An exits-verb line "direction: Destination." was parsed. Payload: (direction, destination name).</summary>
    public event Action<string, string>? ExitLineReady;
    /// <summary>The local player's active temporary-effect set changed (buffs/debuffs/glow).</summary>
    public event Action<StatusEffectState>? StatusEffectsChanged;
    /// <summary>Fires whenever combat is entered/left (see <see cref="CombatTracker"/>).</summary>
    public event Action<bool>? InCombatChanged;
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
    /// outcome (present / offline / invisible). Fires on the Feed thread - consumers marshal.
    /// </summary>
    public event Action<string, SniffOutcome>? SniffResult;
    /// <summary>
    /// An in-combat creature-value probe answered for one name. Payload is the creature name
    /// exactly as engaged (a numbered instance echoes its own number) and the points a `value`
    /// probe reported for killing it. Fires on the Feed thread - consumers marshal.
    /// </summary>
    public event Action<string, int>? CreatureValueResolved;
    /// <summary>
    /// The character occupying this session has been identified from the post-character-select
    /// setup <c>score</c> reply. Payload is the character name (e.g. "Ollie"). Fires once per
    /// game-mode entry, on the Feed thread - consumers marshal. Used to key per-character score
    /// tracking and the window title.
    /// </summary>
    public event Action<string>? CharacterIdentified;
    /// <summary>The reset-time projection changed. Optional immediate UI-refresh hint; the countdown
    /// also polls <see cref="ResetEstimate"/> on its own 1 Hz tick. Fires off the UI thread.</summary>
    public event Action? ResetEstimateChanged;
    /// <summary>A reset-projection reading was folded - for diagnostic logging only. Fires on the
    /// read-loop thread.</summary>
    public event Action<ResetObservation>? ResetObservationRecorded;
    /// <summary>A notable reset-projection incident (unanswered sample, lock contradiction, auto-reset
    /// anchor) - for the capture log. Fires off the UI thread.</summary>
    public event Action<string>? ResetDiagnostic;
    /// <summary>The server announced the auto-reset (C06 C04, "you have 120 seconds to finish up").
    /// Unlike the projection, this is an exact, unambiguous "a reset is happening now" statement -
    /// consumers use it to tell a reset-driven drop to the Option menu from a deliberate quit.
    /// Fires on the read-loop thread.</summary>
    public event Action? AutoResetInitiated;

    /// <summary>The reset actually LANDED - FE 06 06, corroborated against the reset countdown.
    /// Distinct from <see cref="AutoResetInitiated"/>, which is the 06 04 warning two minutes
    /// earlier: anything that wants to mark the boundary between one world and the next wants this
    /// one, or it groups the finish-up window with the wrong cycle. Fires at most once per reset and
    /// not at all when the projection cannot corroborate the line.</summary>
    public event Action? WorldResetLanded;

    /// <summary>Forwarded verbatim from <see cref="MudStreamParser.FrameClosed"/>.</summary>
    public event Action? FrameClosed;

    // -- Public state -----------------------------------------------------------
    /// <summary>The current merged stats snapshot (see <c>MergeStats</c>) - always up to date
    /// thanks to the periodic FES heartbeat, so callers that just need "whatever we currently
    /// know" (e.g. a baseline read before an item-eval drop/get pair) should read this directly
    /// rather than subscribing to <see cref="StatsUpdated"/> and waiting for the next event, which
    /// races the heartbeat's own cadence and can read as empty right after subscribing.</summary>
    public GameStatsSnapshot CurrentStats => _currentStats;
    public string? CurrentDreamword => _currentDreamword;
    public bool InGameMode => _parser.InGameMode;
    public bool InCombat => _combat.InCombat;
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
                StopInventoryProbeLocked();
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

    /// <summary>
    /// Compose one heartbeat's probe. FES always leads: in practice the server does not reliably
    /// update FEW or FEI when either is queried without FES. FEW remains the every-beat component
    /// for who-list vigilance; FEI remains event-driven and is included only when marked dirty.
    /// Caller holds _fesLock. Returns the parts for flag bookkeeping.
    /// </summary>
    private byte[] ComposeBeatLocked(out bool fes, out bool few, out bool fei)
    {
        fes = true;
        few = _includeFew;
        fei = _includeFei && (_staleFlags & StaleStats.Inventory) != 0;
        var cmds = new List<string>(3);
        if (fes) cmds.Add("FES");
        if (few) cmds.Add("FEW");
        if (fei) cmds.Add("FEI");
        return System.Text.Encoding.ASCII.GetBytes("\x1b-[" + string.Join(',', cmds) + "\x1b-]");
    }

    /// <summary>Feed raw bytes from the network. Thread-safe relative to the FES timer - Feed() itself is not thread-safe.</summary>
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

    /// <summary>
    /// Send a line of text to the server (appends \r\n).
    ///
    /// <para>Also the piggyback seam for the in-combat inventory probe: if one is pending, its
    /// escape rides out in front of this command in a single write, so the server queues the probe
    /// and the command together instead of the two racing. This is the point at which a command is
    /// already fully assembled - once per Enter/F-key/click, never per keystroke - so it is not the
    /// typing path, and the fast path costs one volatile read.</para>
    /// </summary>
    public void SendLine(string line)
    {
        var bytes = System.Text.Encoding.Latin1.GetBytes(line + "\r\n");
        if (_inventoryProbePending && CanCarryInventoryProbe(line))
        {
            byte[]? probe;
            lock (_fesLock)
                probe = TakeInventoryProbeLocked(DateTime.UtcNow);
            if (probe is not null)
            {
                var combined = new byte[probe.Length + bytes.Length];
                probe.CopyTo(combined, 0);
                bytes.CopyTo(combined, probe.Length);
                OutgoingBytes?.Invoke(combined);
                ProbeSent?.Invoke();
                return;
            }
        }
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
            StopInventoryProbeLocked();
            StopCreatureValueProbeLocked();
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
            StopInventoryProbeLocked();
            _inventoryProbeTimer?.Dispose();
            _inventoryProbeTimer = null;
            StopCreatureValueProbeLocked();
            _creatureValueProbeTimer?.Dispose();
            _creatureValueProbeTimer = null;
            _creatureProbeTimeoutTimer?.Dispose();
            _creatureProbeTimeoutTimer = null;
        }
        _resetClock.Dispose();
    }

    // -- Private ----------------------------------------------------------------
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
            // Swallow the echo + reply/replies of an injected in-combat creature-value probe -
            // its own in-flight slot, discriminated by target from the player sniff's above. An
            // outstanding player sniff and an outstanding creature probe only avoid eating each
            // other's lines because BOTH sides discriminate - this slot by target name, the sniff
            // above by the reply's anchored name position (see the "In-combat creature value
            // probe" field remarks and TryConsumeSniffLine) - discriminating by target alone is not
            // sufficient: a creature reply can satisfy a bare substring match against the sniff's
            // name.
            if (_creatureProbeInFlight != null && TryConsumeCreatureValueLine(line))
                return;
            // Cancel the dreamword when we see our own persona speak it: speaking uses it,
            // whether it recovered stamina (scenario: server also sends a C1 clear) or was a
            // no-op (full stamina / already consumed - no C1 clear ever arrives). Cheap guard:
            // only runs while a dreamword is active. See TryCancelSpokenDreamword.
            if (_currentDreamword is not null)
                TryCancelSpokenDreamword(line);
            _combat.Observe(line, CombatClock());
            // After _combat.Observe, so InCombat already reflects any fight this very line opened.
            NoteInventoryChangeLine(line);
            LineReady?.Invoke(line);
        };
        _parser.StatsUpdated += MergeStats;
        _parser.PersonaWiped += () => PersonaWiped?.Invoke();
        _parser.ScoreSaved += save => ScoreSaved?.Invoke(save);
        _parser.TaskCompleted += t => TaskCompleted?.Invoke(t);
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
            // Do NOT force-end combat here: a reset wipes game state with no fight-end line, but
            // this warning is not the reset itself. Ending combat on the warning discards every
            // subsequent non-FightStart combat event (CombatStatsAggregator.Observe returns early
            // while !InCombat) - which silently eats weapon equips and leaves fights reading as
            // UNARMED for their whole duration. Confirmed in the clog corpus: an encounter that
            // reopened on "The eagle misses you." ran 41 events across 4 participants with no
            // weapon, having swallowed "You are now using the broadsword to fight!" in the pre-roll.
            //
            // What the real transition looks like, established from three complete reset captures.
            // All three agree:
            //   T+0      this warning (C06 C04). The server means "no further warnings" literally -
            //            zero further broadcasts across all three full countdowns.
            //   T+120s   C06 C06 "Something magical is happening." + "(Persona saved on N)." That
            //            is the reset, to within 5 ms of the promised 120 in both timed captures,
            //            and the last in-world line.
            //   T+120.2s "Option (H for help): ". The TCP connection is NEVER dropped; the socket
            //            just starts carrying the outer MUD-Shell menu.
            // The parser's option-menu matcher fires on that last step and OnGameModeExited
            // force-ends the encounter; WorldResetEndsCombatTests replays those exact bytes and pins
            // it.
            // What that leaves open is the ~200 ms between the reset landing and the shell prompt,
            // in which the client is still feeding a live encounter from a world that no longer
            // exists. C06 C06 closes it - see OnWorldResetLanded.
            _resetClock.NoteAutoResetInitiated(_resetClock.NowMono);
            // Still forwarded: consumers use it to tell a reset-driven drop from a deliberate quit.
            AutoResetInitiated?.Invoke();
        };
        _parser.WorldResetLanded  += OnWorldResetLanded;
        _parser.FrameClosed       += () => FrameClosed?.Invoke();
        _parser.PresenceNameSeen  += OnPresenceName;
        _parser.StatusEffectChanged += _effects.Apply;
        // The coded blind line, same frame it lands: the combat tracker's anonymous-opponent rule
        // turns on whether the PLAYER can see, and the FES flag below is up to a heartbeat late. Only
        // the start is coded into a change today (the end returns null from the decoder); sight
        // returning is picked up by the FES flag in MergeStats, which is the safe direction to be late
        // in - a sighted player is being sent named lines again, so nothing anonymous arrives to be
        // misresolved in the gap.
        _parser.StatusEffectChanged += change =>
        {
            if (change.Kind == StatusEffectKind.Blind && change.Transition == EffectTransition.Started)
                _combat.NoteCannotSee(true);
        };
        _effects.Changed += state => StatusEffectsChanged?.Invoke(state);
        _combat.InCombatChanged += v =>
        {
            // Every encounter end, however it ended - clean fight-end line, room change, ForceEnd
            // from logout/reset/app-exit. The creature-value probe's queue is scoped to one fight
            // and has no other owner that runs on all of those paths.
            if (!v)
            {
                lock (_fesLock)
                    DropPendingCreatureValueProbeLocked();
            }
            InCombatChanged?.Invoke(v);
        };
        _combat.EventOccurred += e => CombatEventOccurred?.Invoke(e);
        _combat.ParticipantJoined += OnParticipantJoined;
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
            // A sniff still in flight when this probe's FEW completes drew no `value` reply -
            // the reply always precedes the FEW in the same transmission - so the player is
            // online but invisible (the game says nothing). Fire AFTER FewListComplete so the
            // list diff runs before any promotion the outcome triggers.
            var pendingSniff = _sniffInFlight;
            if (pendingSniff != null)
                ResolveSniff(pendingSniff, SniffOutcome.Invisible);
        };
        _parser.RoomEntered      += () => { ArmRoomFexProbe(); RoomEntered?.Invoke(); };
        _parser.RoomShortReady   += name => { NoteRoomShort(name); RoomShortReady?.Invoke(name); };
        _parser.FeiItemReady     += item => FeiItemReady?.Invoke(item);
        _parser.FeiListStarting  += () => FeiListStarting?.Invoke();
        _parser.FeiListComplete  += () =>
        {
            _lastProbeReplyUtc = DateTime.UtcNow;
            ClearStale(StaleStats.Inventory);
            FeiListComplete?.Invoke();
        };
        _parser.CreatureTextReady += text => CreatureTextReady?.Invoke(text);
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

        // An FES snapshot is a probe reply - the panel data is fresh again.
        if (partial.HasFesStats)
            _lastProbeReplyUtc = DateTime.UtcNow;

        // Whatever values this update carries - a full FES snapshot or an inline text
        // line like "(84/90)" - are no longer stale, so a pending hint for them won't
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
            // refresh) - sex never changes at all, and weight/objects/value change only on our own
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
        // The FES flag is authoritative for the combat tracker's blind gate in both directions, and
        // the only source at all on a relog into an already-blind persona. Asserted as a LEVEL on
        // every genuine reply, never as an edge on this snapshot: the coded <11.00> line sets the
        // tracker blind without writing IsBlind here, so a blind episode shorter than one heartbeat
        // (a backfire and an "unblind me" is about 2 s against a 10 s default) never shows FES a Y,
        // an edge test sees no transition, and the tracker would stay blind for the rest of the
        // session - reinstating the very misattribution the gate exists to prevent. A repeat of the
        // current value is a no-op in the tracker, so the level costs nothing.
        if (partial.HasFesStats)
            _combat.NoteCannotSee(_currentStats.IsBlind);
        // Fold the reset value into the projection. Called outside _fesLock (ClearStale above took and
        // released it) so the engine->_fesLock order holds when Observe fires a burst probe.
        _resetClock.Observe(_currentStats.TimeToReset, partial.HasFesStats, replyMono);
        StatsUpdated?.Invoke(_currentStats);
    }

    private void OnGameModeEntered()
    {
        _effects.Reset();   // fresh character - no effects carried from a previous session
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
        //   identify   - name creatures individually ("rat21", not "the rat")
        //   fightbrief - report combat in the terse form the parsers are written against
        //   auto fex   - enable the per-move front-end exit list
        //   score      - pull the character sheet (for the character name + a score baseline)
        //
        // identify and fightbrief are not conveniences; without them the client is close to blind in
        // a fight, which is why they are FIRST in the batch. Two real sessions
        // (session-rec.mud2.co.uk.20260819-000137 / -001608) were captured with both off:
        //   * fightbrief off replaces every swing line with narrative prose - "Your limp, frontal
        //     attack is indifferently killed by the rat.", "You are only just hurt by a weighty nip
        //     from the rat.", "Damage range: 5-9." - and NONE of it is parsed, so an entire fight can
        //     pass with the client counting no swings at all.
        //   * identify off names every creature "the rat" in prose while the FEW panel lists
        //     rat21/rat19/rat16/rat17, so four separate fights collapse onto one participant and the
        //     per-creature history is attributed to a name that does not identify anything.
        //
        // Both are SETs, not toggles - sending them to a persona that already has
        // them on is a no-op, so this batch is safe to fire unconditionally on every game-mode entry
        // and needs no "is it already on?" probe first.
        //
        // Each replies in its OWN frame, in one of two wordings depending on whether the setting was
        // already on - see the matcher in TrySwallowSetupLine for all four verbatim strings.
        // The echo + replies are hidden from the terminal (TrySwallowSetupLine), but the score
        // line's stats still reach the UI (the parser's analyzer fires StatsUpdated before
        // LineReady). Future user-defined setup commands slot in before `score`, which stays
        // LAST so its reply frame is the one that closes the swallow window.
        OpenSetupWindow();
        // The server echoes each command back on its own line, then executes them on subsequent
        // game turns - the outputs (auto-fex FEEXITS confirmation, then the score sheet) trickle
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
            StopInventoryProbeLocked();
            StopCreatureValueProbeLocked();
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
        // Forget where we were. Harmless today - the ForceEnd below leaves nothing for a spurious
        // room change to close, and NoteRoomChanged is a no-op while out of combat - but a stale room
        // name from the PREVIOUS login is not a fact about this one, and leaving it set makes the
        // backstop's correctness depend on the order of the next two lines.
        _lastRoomShort = null;
        _combat.ForceEnd(CombatClock());   // logout ends any open encounter - no fight-end line will arrive
        _resetClock.OnGameModeExited();   // drop the projection incl. the once-per-session token
        GameModeExited?.Invoke();
    }

    /// <summary>
    /// C06 C06, "Something magical is happening." (Bartle 06 06) - the reset landing. Ends any open
    /// encounter about 200 ms before the shell prompt would, so no combat line from a world that has
    /// already been rebuilt is folded into a fight from the world that is gone.
    ///
    /// <para><b>Corroborated, not trusted.</b> Bartle's own gloss for this code is generic, and the
    /// corpus has exactly two occurrences of the line - both at a reset, no counter-example, but
    /// n=2. Acting on it unconditionally risks the same premature-end failure mode: once an
    /// encounter is closed early, CombatStatsAggregator.Observe drops every subsequent
    /// non-FightStart event, which is how a whole fight can read as UNARMED. So this only fires
    /// when the reset countdown independently
    /// says the reset is due right now. That countdown is the solid half of the evidence: it is
    /// anchored either by the C06 C04 warning (exact, +120 s) or by the FES "minutes to next reset"
    /// field, which is Bartle's own documented FES output.</para>
    ///
    /// <para>If the projection has nothing to say - not in game, no reading yet - this does nothing
    /// and the shell prompt closes the encounter as it always has. Degrading to the previous
    /// behaviour is the correct failure mode for a signal this thinly observed.</para>
    /// </summary>
    private void OnWorldResetLanded()
    {
        var estimate = _resetClock.Snapshot();
        if (estimate.TargetUtc is not DateTime target)
            return;
        // The tolerance is the projection's own stated uncertainty, floored at 2 s so a hard lock
        // (+/-0.3 s) still absorbs ordinary jitter between the anchor and this line's arrival.
        var tolerance = Math.Max(2.0, estimate.UncertaintySec);
        if (Math.Abs((DateTime.UtcNow - target).TotalSeconds) > tolerance)
            return;
        _combat.ForceEnd(CombatClock(), "world reset");
        // Raised only past the corroboration above, so a stray "Something magical is happening."
        // with no reset due tells nobody anything. This is the moment the world actually turned
        // over - consumers that were previously hanging off AutoResetInitiated, which fires on the
        // WARNING two minutes earlier, should hang off this instead.
        WorldResetLanded?.Invoke();
    }

    private void OnDreamwordChanged(string? word)
    {
        _currentDreamword = word;
        DreamwordChanged?.Invoke(word);
    }

    // The dreamword is a server-generated word given to sleeping players; the first to speak it
    // wins a random stamina refresh. When we speak it successfully the server both echoes our
    // speech and sends a C1 code that clears the dreamword (OnDreamwordChanged(null)). But when
    // the speak is a no-op - we spoke at full stamina, or someone drained the FIFO queue first -
    // no C1 clear arrives, and we would otherwise keep advertising a dead dreamword forever.
    // Detection: our own persona saying the exact current dreamword. Speaking uses it, full stop.
    // Runs on the Feed thread (LineReady), so no marshalling; _currentDreamword/_currentCharName
    // are only touched here and on that same thread.
    private void TryCancelSpokenDreamword(StyledLine line)
    {
        var word = _currentDreamword;
        if (word is null || _currentCharName is null)
            return;
        // Player speech is C1 code 09 -> LineKind.Chat; anything else can't be a `says` line.
        if (line.Kind != LineKind.Chat)
            return;

        var text = line.PlainText;
        // Speaker must be our persona - including while we are invisible, when the game
        // parenthesises the whole name ("(Ollie the warlock) says ..."). Testing the raw prefix
        // directly would miss every invisible speak; PlayerNameParts.StartsWithPersona owns the
        // rule (and the "Ollie" must not match "Ollier" boundary) for both this and
        // SelfChatColorizer.
        if (!PlayerNameParts.StartsWithPersona(text, _currentCharName))
            return;

        // `... says "<word>"` - the quoted content must be exactly the current dreamword.
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
        // EmitDreamwordChanged fires DreamwordChanged -> OnDreamwordChanged, syncing session state.
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
            // The beat refreshes only what it carries - clear exactly those pending flags.
            var carried = StaleStats.None;
            if (fes) carried |= StaleStats.AllStats;
            if (few) carried |= StaleStats.WhoList;
            if (fei) carried |= StaleStats.Inventory;
            _staleFlags &= ~carried;
            // Ride a queued sniff (value <name>) on this probe, but only when the probe carries
            // FEW: the FEW-complete boundary is what closes out an invisible (no-reply) sniff, so
            // a FEW-less probe could never resolve it. LIFO - one sniff per probe.
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
    /// LIFO - a newer request replaces an unsent one, since only one sniff rides each probe.
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
        // Echo of the command we injected - swallow but keep waiting for the reply.
        if (text.Equals("value " + name, StringComparison.OrdinalIgnoreCase))
            return true;
        // Outcome 1 - present & visible.
        //
        // WIRE GRAMMAR (raw session recordings under %LOCALAPPDATA%\Temp\mucka plus the clog
        // corpus; 458 "The value of ..." lines, 246 distinct). Exactly three shapes exist:
        //   PLAYER   "The value of Crispybob the necromancer is 5,965 points."   (2 captures, each
        //            with its own `val <name>` echo on the preceding tx frame; the other is
        //            "The value of Drizzle the wobbly mage is 26,105 points.")
        //   CREATURE "The value of the wyvern is 239 points."                    (241 distinct)
        //   OBJECT   "The value of Columbus is 10 points."                        (proper-noun
        //            objects; 7 occurrences, 3 distinct, no "the " and no trailing description.
        //            Counts only the objects: the 2 PLAYER lines above also lack the "the ", so a
        //            no-"the" query returns 9.)
        // The number may be comma-grouped ("5,965"), zero, or NEGATIVE (36 occurrences, all
        // objects), and the noun is SINGULAR at one point ("The value of the penny is 1 point." -
        // 6 occurrences), which is why the tail below accepts both.
        //
        // The literal "the " after "of " is the discriminator between the player and creature
        // forms, and it is the whole reason a `ram2` creature reply must not resolve a queued sniff
        // for persona "Ram". It is kept.
        //
        // What is NOT kept is anchoring the persona name at exactly offset 13. The corpus shows the
        // rank rendered as a SUFFIX ("Kram the hero") and never the other way round - 9,879 matches
        // for "<Name> the <rank>" against 0 for the reverse order, over 31 distinct rank forms
        // (same corpus as the sibling figure above; the query is the
        // PersonaRanks title vocabulary as one alternation, matched as
        // /\b[A-Z][A-Za-z'\-]+ the ((?:[a-z][a-z'\-]*[ ])*?(?:<titles>))\b/ and then with the two
        // halves swapped).
        //
        // But MUD2's own `levels` table - captured verbatim on the wire - makes "Sir" and "Lady" the
        // level-10 NORMAL titles, and this codebase's one name grammar (PlayerNameParts) models
        // those two as PREFIXES. A "Lady Polly ..." reply to a sniff for persona "Polly" therefore
        // fails a fixed offset and reads as "no reply", which the FEW-completion backstop then
        // promotes to Invisible - a fabricated invisibility claim, the mirror of the fabricated
        // sighting. Deferring to PlayerNameParts.Parse means there is still exactly ONE place that
        // knows what a MUD2 name looks like, whichever end the honorific sits at.
        //
        // Honest limit: NO capture of a "Sir <Name>"/"Lady <Name>" player exists in the corpus at
        // all - the prefix model is inferred from the levels table, not observed. (Every line in that
        // corpus containing the bare word "Sir" or "Lady" is a row of the levels table itself, e.g.
        // "Sir          mage        102400 10  20555".) Handling both
        // orders costs nothing and neither order can be ruled out; asserting which one MUD2 emits
        // would be inventing grammar.
        const string presencePrefix = "The value of ";
        const string creatureLead   = "the ";     // "The value of the wyvern is ..." - never a player
        if (text.StartsWith(presencePrefix, StringComparison.Ordinal) &&
            (text.EndsWith(" points.", StringComparison.Ordinal) ||
             text.EndsWith(" point.", StringComparison.Ordinal)))
        {
            var rest = text[presencePrefix.Length..];
            if (!rest.StartsWith(creatureLead, StringComparison.Ordinal) &&
                string.Equals(PlayerNameParts.Parse(rest).PersonaName, name, StringComparison.OrdinalIgnoreCase))
            {
                ResolveSniff(name, SniffOutcome.Present);
                return true;
            }
        }
        // Outcome 2 - logged out: I don't know the word "{name}".
        //
        // The quoted word is compared for EQUALITY, never Contains. Same class of bug as the
        // sighting above and the same severity in the other direction: this line is the game's
        // generic "that token is not in my vocabulary" and the corpus is full of them from the
        // player's own typing - 48 occurrences, 35 distinct words over the same corpus as the wire
        // grammar above (query: count of /I don't know the word "([^"]*)"\./
        // matches, distinct on the captured group as written), e.g. "dro", "clsoe", "krat11",
        // "atomcibob". Under a substring test any of those containing the sniffed persona as a
        // substring ("krat11" for a persona "Rat", "atomcibob" for a persona "Bob") resolved the
        // sniff to Offline, swallowed the line, and asserted the player had logged out when
        // nothing of the kind had happened. The shape is fixed and fully delimited - ASCII quotes,
        // trailing full stop, no variants observed - so the word can simply be lifted out.
        //
        // OrdinalIgnoreCase is required, not defensive: the server canonicalises case in the
        // matching Present reply (tx "val crispybob" -> rx "The value of Crispybob ...").
        const string unknownWordPrefix = "I don't know the word \"";
        const string unknownWordSuffix = "\".";
        if (text.Length > unknownWordPrefix.Length + unknownWordSuffix.Length &&
            text.StartsWith(unknownWordPrefix, StringComparison.Ordinal) &&
            text.EndsWith(unknownWordSuffix, StringComparison.Ordinal))
        {
            var word = text[unknownWordPrefix.Length..^unknownWordSuffix.Length];
            if (string.Equals(word, name, StringComparison.OrdinalIgnoreCase))
            {
                ResolveSniff(name, SniffOutcome.Offline);
                return true;
            }
        }
        return false;
    }

    private void ResolveSniff(string name, SniffOutcome outcome)
    {
        _sniffInFlight = null;
        SniffResult?.Invoke(name, outcome);
    }

    // -- In-combat creature value probe ------------------------------------------

    /// <summary>
    /// A name just became newly active in the current encounter (CombatTracker.ParticipantJoined).
    /// Queue it for the next value probe unless it is already known, already queued, or already
    /// riding an outstanding batch.
    /// </summary>
    private void OnParticipantJoined(string npc)
    {
        if (string.IsNullOrWhiteSpace(npc))
            return;
        // An opponent the game would not name gets no value probe: "value someone" is answered with
        // the PLAYER's own value ("Your value is 225 points."), which CreatureValueReply does not
        // match, so it cost two junk lines on the screen and a game turn spent mid-fight - three
        // times in one run - for nothing the client could use.
        if (AnonymousOpponent.IsAnonymous(npc))
            return;
        lock (_fesLock)
        {
            // A new encounter's first participant: forget every reading AND every pending/in-flight
            // batch from the previous one (see _creatureValueKnown's own remarks on why value
            // cannot be cached past a fight - and StopCreatureValueProbeLocked's on why a stale
            // in-flight batch must go too, not just the known set, or a lingering entry from the
            // last encounter's probe can wrongly suppress this one's). Keyed off "the encounter was
            // not yet open when Begin() called us", rather than a separate InCombatChanged
            // subscription, because CombatTracker.Begin fires ParticipantJoined BEFORE it flips
            // _encounterOpen/fires InCombatChanged(true) - so this reads false exactly once per
            // encounter, for its first participant, and true for every later joiner of the same
            // fight.
            if (!_combat.InCombat)
                StopCreatureValueProbeLocked();
            // Heartbeat disabled is this codebase's existing "reactive probing is off" switch (see
            // OnProbeHint) - honoured here too rather than treating this as a separate feature with
            // its own opinion about it.
            if (_fesInterval <= TimeSpan.Zero || !InGameMode)
                return;
            if (_creatureValueKnown.Contains(npc))
                return;
            if (_pendingCreatureNames.Contains(npc, StringComparer.OrdinalIgnoreCase))
                return;
            if (_creatureProbeInFlight is { } inFlight && inFlight.Contains(npc, StringComparer.OrdinalIgnoreCase))
                return;
            _pendingCreatureNames.Add(npc);
            ScheduleCreatureValueProbeLocked(DateTime.UtcNow);
        }
    }

    /// <summary>
    /// When the pending batch goes out. Same trailing-debounce-plus-tick-guard shape as
    /// <see cref="ScheduleInventoryProbeLocked"/> (see that method's remarks for why a restarted
    /// timer is what makes a burst of joiners cost one probe, and why a guarded boundary is moved
    /// past it rather than raced) - deliberately the same options, not a second set of tunables for
    /// what is the same scheduling problem.
    /// </summary>
    private void ScheduleCreatureValueProbeLocked(DateTime now)
    {
        var delay = _options.InventoryProbeDebounce;
        if (MillisecondsToNextCombatTick?.Invoke() is double toTick
            && toTick <= _options.InventoryProbeTickGuard.TotalMilliseconds)
        {
            delay = TimeSpan.FromMilliseconds(toTick) + _options.InventoryProbeTickClearance;
        }
        _creatureValueProbeTimer ??= new Timer(_ => OnCreatureValueProbeDeadline(), null, Timeout.Infinite, Timeout.Infinite);
        _creatureValueProbeTimer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Claims the pending batch and returns its bytes, or null to leave it pending (rescheduled) or
    /// to abandon it. Caller holds <see cref="_fesLock"/>.
    /// </summary>
    private byte[]? TakeCreatureValueProbeLocked()
    {
        if (_pendingCreatureNames.Count == 0)
            return null;
        if (!InGameMode)
        {
            _pendingCreatureNames.Clear();
            return null;
        }
        // Hard gate: this probe only exists to price the creatures in the CURRENT fight, so it must
        // never leave the wire outside one. InGameMode alone is not that gate. Death is the case
        // that matters: the protocol's own death signal is C08 C13 ("Not updating persona.",
        // Bartle 08 13) and the drop to the shell that follows, but the parser only leaves game
        // mode when it matches the "Option:" prompt further down the stream - so between the kill
        // and that prompt InGameMode is still true while the far end is already a login shell.
        // A batch left pending across that window would type `value goblin1` at the shell. In a
        // permadeath game that is not a cosmetic bug.
        if (!_combat.InCombat)
        {
            _pendingCreatureNames.Clear();
            return null;
        }
        if (_creatureProbeInFlight is { Count: > 0 })
        {
            // A batch is already outstanding - wait for its window to close (every requested name
            // accounted for, at the following frame boundary - or the timeout backstop giving up;
            // see TryConsumeCreatureValueLine) rather than sending a second `value` command whose
            // replies could not be told apart from the first's.
            ScheduleCreatureValueProbeLocked(DateTime.UtcNow);
            return null;
        }
        if (_probesHeld || _resetDiscoveryHold || _resetClock.IsSamplingInFlight)
        {
            // Something else owns the wire; wait it out and try again, exactly as the inventory
            // probe does.
            ScheduleCreatureValueProbeLocked(DateTime.UtcNow);
            return null;
        }
        // Deliberately NOT folded into _lastProbeSentUtc/MinProbeSpacing: that floor is shared by
        // the whole FES/FEW/FEI probe family specifically so they space themselves out from EACH
        // OTHER, and a `value` command is neither part of that family nor competing with it for
        // anything - coupling this probe's timing to that shared clock only makes an unrelated
        // feature's own carefully-timed spacing unpredictable.
        var names = new List<string>(_pendingCreatureNames);
        _pendingCreatureNames.Clear();
        var command = "value " + string.Join(" and ", names);
        _creatureProbeEcho = command;
        _creatureProbeUnresolved = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        _creatureProbeReadyToClose = false;
        _creatureProbeTimeoutTimer ??= new Timer(_ => OnCreatureValueProbeTimeout(), null, Timeout.Infinite, Timeout.Infinite);
        _creatureProbeTimeoutTimer.Change(_options.CreatureValueProbeTimeout, Timeout.InfiniteTimeSpan);
        _creatureProbeInFlight = names;   // publish last - the Feed thread starts matching against
                                          // this the instant it becomes non-null
        return System.Text.Encoding.Latin1.GetBytes(command + "\r\n");
    }

    /// <summary>The quiet period elapsed with no player command to ride: send the batch on its own.
    /// (Unlike the inventory probe, this command is never piggybacked onto a player line - `value`
    /// is an ordinary typed command with its own frame, not an FES/FEW/FEI subscription component
    /// that rides in front of one - so it only ever goes out from here.)</summary>
    private void OnCreatureValueProbeDeadline()
    {
        byte[]? probe;
        lock (_fesLock)
            probe = TakeCreatureValueProbeLocked();
        if (probe is null)
            return;
        OutgoingBytes?.Invoke(probe);
        ProbeSent?.Invoke();
    }

    // Feed thread. Returns true when the line is the echo, a reply, or a bad-target rejection for
    // the in-flight batch and should be swallowed.
    //
    // Frame prompts do NOT close this window on sight. On real wire traffic every frame is LED by
    // its prompt (see PostSelectSetupTests' own model, and MudSession's post-select setup remarks):
    // the very first thing back after arming is the prompt that introduces the ECHO's own frame,
    // not a closing boundary - closing there is wrong: every documented frame shape - "prompt, echo"
    // then "prompt, reply" as two frames, or "prompt, echo+reply" as one - would shut the window
    // before a reply, or even the echo, had been seen. Instead the window
    // tracks which requested names are still unaccounted for (_creatureProbeUnresolved) and only
    // actually closes at the frame boundary that FOLLOWS the point where every one of them has
    // drawn at least one reply or bad-target rejection (_creatureProbeReadyToClose) - deliberately
    // the NEXT prompt after that point, not the moment it happens, so a further reply for a name
    // already accounted for THIS batch (two live creatures sharing an unnumbered name, both
    // answering the same `value <name>` - see FightAccumulator.NoteValue) still lands inside the
    // still-open window and can be flagged ambiguous rather than leaking to the terminal as an
    // unswallowed line. A batch that never gets fully accounted for (a name that legitimately never
    // draws a reply) is bounded by _creatureProbeTimeoutTimer instead, so it cannot wedge the
    // window open forever. Positional pairing is still never assumed - a reply is matched by NAME
    // against the requested set, exactly the "value gg" answering both gargoyle0 and gargoyle1 shape
    // the domain notes describe.
    private bool TryConsumeCreatureValueLine(StyledLine line)
    {
        var inFlight = _creatureProbeInFlight;
        if (inFlight is null)
            return false;

        if (line.IsPartial)
        {
            if (_creatureProbeReadyToClose)
                CloseCreatureValueProbeWindow();
            return false;   // let the prompt render as it always does; not part of the swallow
        }

        var text = line.PlainText.Trim('\r', '\n', '\0', ' ');

        if (_creatureProbeEcho is { } echo && text.Equals(echo, StringComparison.OrdinalIgnoreCase))
            return true;   // echo of the command we injected

        var reply = CreatureValueReply.Match(text);
        if (reply.Success)
        {
            var name = reply.Groups["name"].Value;
            if (!inFlight.Contains(name, StringComparer.OrdinalIgnoreCase))
                return false;   // names someone we did not ask about - not ours, show it
            // Thousands separator observed on the wire ("1,419 points") - strip it before parsing;
            // see the domain notes on why this is stated rather than assumed as a general locale rule.
            var digits = reply.Groups["value"].Value.Replace(",", "");
            // AllowLeadingSign, not None: the regex now admits the 36 observed negative values, and
            // None would reject every one of them here instead - the reply would be swallowed as
            // ours and no CreatureValueResolved would ever fire. Thousands stay hand-stripped above
            // rather than delegated to AllowThousands, so the accepted shape is still exactly the
            // one the wire produces.
            if (int.TryParse(digits, System.Globalization.NumberStyles.AllowLeadingSign,
                    System.Globalization.CultureInfo.InvariantCulture, out var value))
            {
                lock (_fesLock)
                    _creatureValueKnown.Add(name);
                // Fired for EVERY reply, including a second one for a name already accounted for
                // this batch - FightAccumulator.NoteValue is what detects and retracts that
                // ambiguous case, and ClogWriter.OnCreatureValueResolved flags the clog row, rather
                // than this call site trying to suppress the resend itself.
                CreatureValueResolved?.Invoke(name, value);
            }
            NoteCreatureProbeNameAccountedFor(name);
            return true;
        }

        var bad = CreatureValueBadTarget.Match(text);
        if (bad.Success && inFlight.Contains(bad.Groups["name"].Value, StringComparer.OrdinalIgnoreCase))
        {
            NoteCreatureProbeNameAccountedFor(bad.Groups["name"].Value);
            return true;   // bad target for one of our own names - swallow, no value recorded
        }

        return false;
    }

    // Marks one requested name as accounted for (a reply or bad-target rejection has been seen for
    // it this batch). Once every requested name has been, arms the window to close - but does not
    // close it outright, so a further reply for an already-accounted-for name (see this method's
    // caller) still lands inside the window instead of leaking to the terminal unswallowed.
    private void NoteCreatureProbeNameAccountedFor(string name)
    {
        var unresolved = _creatureProbeUnresolved;
        unresolved?.Remove(name);
        if (unresolved is { Count: 0 })
            _creatureProbeReadyToClose = true;
    }

    // Actually closes the creature-value probe window: called either from TryConsumeCreatureValueLine
    // (the frame boundary after every requested name was accounted for) or from the timeout backstop
    // (OnCreatureValueProbeTimeout, when one never was).
    private void CloseCreatureValueProbeWindow()
    {
        lock (_fesLock)
        {
            _creatureProbeTimeoutTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _creatureProbeInFlight = null;
            _creatureProbeEcho = null;
            _creatureProbeUnresolved = null;
        }
        _creatureProbeReadyToClose = false;
    }

    // The backstop fired: the batch was never fully accounted for within CreatureValueProbeTimeout
    // (a requested name that legitimately never draws a reply - e.g. an ambiguous match consumed by
    // another slot, or the creature left before answering). Give up on it rather than wedge the
    // window open forever; whatever wasn't heard from simply stays unknown.
    private void OnCreatureValueProbeTimeout() => CloseCreatureValueProbeWindow();

    /// <summary>
    /// Fully resets the creature-value probe: nothing pending, nothing in flight, the debounce
    /// timer disarmed, AND every reading learned so far forgotten. Called at every boundary past
    /// which a "known" reading or an outstanding batch would be stale rather than merely unused -
    /// a new encounter's first participant (see OnParticipantJoined - the reason
    /// <see cref="_creatureValueKnown"/> must not survive past a fight is on that field's own
    /// remarks), and disconnect/relog/app-exit (<see cref="Reset"/>/<see cref="OnGameModeExited"/>/
    /// <see cref="Dispose"/>), where an open encounter has just been force-ended anyway and a
    /// pending reply could otherwise be swallowed for a fight that no longer exists.
    /// </summary>
    private void StopCreatureValueProbeLocked()
    {
        _pendingCreatureNames.Clear();
        _creatureProbeInFlight = null;
        _creatureProbeEcho = null;
        _creatureProbeUnresolved = null;
        _creatureProbeReadyToClose = false;
        _creatureValueProbeTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _creatureProbeTimeoutTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _creatureValueKnown.Clear();
    }

    /// <summary>
    /// The encounter just closed: drop everything still QUEUED and disarm the debounce timer, so no
    /// `value` command can go out for a fight that is over (see the InCombat gate in
    /// <see cref="TakeCreatureValueProbeLocked"/> for what that costs when it is missing).
    /// <para>Deliberately NOT <see cref="StopCreatureValueProbeLocked"/>: a batch already on the wire
    /// keeps its in-flight window, because that window is the only thing swallowing the echo and the
    /// replies still enroute. Tearing it down here would spill "The value of the goblin is 120
    /// points." into the terminal for every fight that ends within the probe's round trip. The window
    /// closes itself at its frame boundary, or the timeout backstop closes it; the stale
    /// <see cref="_creatureValueKnown"/> readings it may add on the way out are cleared by the next
    /// encounter's first participant (OnParticipantJoined).</para>
    /// Caller holds <see cref="_fesLock"/>.
    /// </summary>
    private void DropPendingCreatureValueProbeLocked()
    {
        _pendingCreatureNames.Clear();
        _creatureValueProbeTimer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    // -- Post-character-select setup swallow -------------------------------------
    // Commands injected on game-mode entry, in order. `score` MUST stay last: its reply frame is
    // the one whose closing prompt shuts the swallow window. Future user-defined setup commands go
    // BEFORE `score`. (A trailing CTRL-T "done" sentinel was tried and removed - the server
    // answers CTRL-T on receipt, ~500ms ahead of the queued command outputs, so it cannot mark
    // completion; verified from a live session recording.)
    private static readonly string[] SetupCommands = { "identify", "fightbrief", "auto fex", "score" };

    // First line of the `score` sheet: "name:           Ollie". A leading frame prompt ("*name:")
    // is stripped before matching. Character names are single tokens, so the "name:" line never
    // wraps - a reliable frame-start marker even at narrow terminal widths.
    private static readonly System.Text.RegularExpressions.Regex SetupNameRegex = new(
        @"^name:\s+(\S+)",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // Feed thread. Returns true when the line belongs to the injected setup batch and should be
    // swallowed. Works by frame, not by matching every line: each server reply arrives as a frame
    // led by an IsPartial '*' prompt, so we recognise a setup frame from its FIRST content line
    // (echo / FEEXITS / "name:" - all at column 0, so wrapping never hides them) and then swallow
    // every line of that frame up to the next prompt. This is width-independent: a wrapped reply
    // just adds more content lines inside the same frame (matching by individual line label would
    // leak the moment a value wrapped). Player chatter arrives in its own frame, is not claimed, and still
    // shows. The score frame is the last we claim; its closing prompt shuts the window. Called
    // only while _setupWindowActive.
    private bool TrySwallowSetupLine(StyledLine line)
    {
        // Frame boundary: the '*' prompt that leads every frame. Let it render as the prompt, but
        // use it to delimit frames - and to close the window once the score frame has ended.
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

        // Already inside a setup frame we've claimed - swallow the rest of it (wrapped
        // continuations included) until the next prompt clears _setupSwallowingFrame.
        if (_setupSwallowingFrame)
            return true;

        // First content line of a fresh frame - decide whether the setup batch owns it.
        var text = line.PlainText.Trim('\r', '\n', '\0', ' ');
        var body = StripLeadingPrompt(text);   // a frame prompt can glue onto the first line

        // Command echoes - the server echoes each injected command on its own line.
        foreach (var cmd in SetupCommands)
            if (body.Equals(cmd, StringComparison.OrdinalIgnoreCase))
                return ClaimFrame();

        // `auto fex` confirmation frame - opens "You will now get an automatic FEEXITS ...".
        if (body.Contains("FEEXITS", StringComparison.OrdinalIgnoreCase) ||
            body.StartsWith("You will now get an automatic", StringComparison.OrdinalIgnoreCase))
            return ClaimFrame();

        // `identify` / `fightbrief` confirmation frames - one frame each, and TWO wordings apiece,
        // because MUD2 answers differently when the setting was already on. All four verbatim
        // (session-rec.mud2.co.uk.20260819-134737 for the first pair, a second capture for the pair):
        //   You'll now get object identification numbers where applicable.
        //   You're already getting object identification numbers where applicable.
        //   You'll now get brief descriptions of fights.
        //   You're already getting brief descriptions of fights.
        // Both "already" forms are the ordinary case from the second login onward: the commands are
        // SETs rather than toggles, so the batch re-sends them every entry and MUD2 says so.
        //
        // Matched as lead-in AND subject rather than on the whole sentence, so a wrap at a narrow
        // terminal width cannot hide the frame's first line from us the way an exact match would -
        // the same reason the auto-fex matcher above keys on "FEEXITS" rather than its full sentence.
        if ((body.StartsWith("You'll now get ", StringComparison.OrdinalIgnoreCase) ||
             body.StartsWith("You're already getting ", StringComparison.OrdinalIgnoreCase)) &&
            (body.Contains("identification numbers", StringComparison.OrdinalIgnoreCase) ||
             body.Contains("descriptions of fights", StringComparison.OrdinalIgnoreCase)))
            return ClaimFrame();

        // `score` sheet frame - opens on the "name:" line, which yields the character name and is
        // the LAST frame we claim, so arm the window to close when this frame's prompt arrives.
        var nm = SetupNameRegex.Match(body);
        if (nm.Success)
        {
            SetCurrentCharacter(nm.Groups[1].Value);
            _setupCloseAfterFrame = true;
            return ClaimFrame();
        }

        return false;   // not ours (e.g. player chatter) - show it

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
        _setupWindowActive    = true;   // volatile store - publishes the two writes above
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

    // -- Resite/supersite recovery probe -----------------------------------------

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
            _roomFexProbeTimer ??= OneShotTimerFactory(OnRoomFexProbeDeadline);
            _roomFexProbeTimer.Change(_options.RoomEntryFexProbeDelay);
        }
    }

    /// <summary>
    /// A room short description arrived at column 0. When it differs from the last one, the player is
    /// somewhere else than they were, and in MUD2 you cannot walk out of a fight - so any
    /// encounter still open is over, and <see cref="CombatTracker.NoteRoomChanged"/> closes it. See
    /// there for why this backstop exists and why it announces itself.
    ///
    /// <para>Gated on the name CHANGING, because RoomShortReady fires for a plain `look` at the room
    /// the player is already standing in - measured, not assumed: of the 399 column-0 room shorts in
    /// session-rec.mud2.co.uk.20260826-134435, five follow a bare `l` and one a probe reply, and
    /// looking around mid-fight is free and constant. Closing a fight on every `look` would be far
    /// worse than a backstop that sometimes abstains.</para>
    ///
    /// <para>What it costs: a move between two rooms whose shorts read identically does not fire. Not
    /// hypothetical - eleven column-0 room shorts in that capture read "You are lost in a misty
    /// graveyard.", ten of them on entry to a room, so those rooms are indistinguishable by name. The
    /// backstop is silent in there and the primary parsing carries the fight, which is the intended
    /// failure direction.</para>
    ///
    /// <para><b>Open question, deliberately not designed around.</b> In that capture a room short
    /// arriving on MOVEMENT is preceded by an ambient-sound code (C20.xx) and one arriving from `look`
    /// is not - 393 of 399 carried one. If that holds it is a true move/look discriminator and would
    /// also work in the maze. It rests on one session, and reading it wrong closes fights on `look`,
    /// so it stays an open observation until a second capture agrees.
    /// As for a snoop putting somebody else's room short in our stream: it cannot, and not merely
    /// because snooping is wiz-only. Bartle's own description of the codes settles it - 94 "brackets
    /// the name of the person being snooped" and 97 is "snooped material FROM the player using
    /// internal FE nn", i.e. both describe content relayed into the SNOOPER's stream. Those bytes
    /// only reach this client if this player is snooping someone, never because someone is snooping
    /// him.</para>
    /// </summary>
    private void NoteRoomShort(string room)
    {
        if (string.IsNullOrWhiteSpace(room))
            return;
        var moved = _lastRoomShort is not null && !string.Equals(_lastRoomShort, room, StringComparison.Ordinal);
        _lastRoomShort = room;
        if (moved)
            _combat.NoteRoomChanged(CombatClock());
    }

    /// <summary>A FEX list has started arriving - the pending recovery probe (if any) is moot.</summary>
    private void CancelRoomFexProbe()
    {
        lock (_fesLock)
            _roomFexProbeTimer?.Stop();
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
        _roomFexProbeTimer?.Stop();
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

    // -- Reactive stale-stats probing -------------------------------------------

    /// <summary>
    /// A C1 code hinted that the given categories may be out of date. Mark them stale
    /// and arm the one-shot probe timer - unless reactive probing is disabled (heartbeat
    /// interval 0) or the routine probe is due soon enough to cover them.
    /// </summary>
    private void OnProbeHint(StaleStats kinds)
    {
        if (kinds == StaleStats.None) return;
        lock (_fesLock)
        {
            if (_fesInterval <= TimeSpan.Zero || !InGameMode)
                return;
            // Record ALL hinted categories - the routine beat composes FEI from the Inventory flag,
            // so the fact must be kept even when no off-cadence probe fires (e.g. beat imminent).
            _staleFlags |= kinds;
            // Only who-list / inventory staleness warrants an off-cadence probe. Stat categories
            // are advisory: combat deltas arrive as inline text ("(84/90)") and the next routine
            // probe catches anything else - rapid-firing on every combat code is pure noise.
            if ((kinds & (StaleStats.WhoList | StaleStats.Inventory)) == StaleStats.None)
                return;
            if (_nextRoutineProbeUtc - DateTime.UtcNow <= _options.MinProbeSpacing)
                return;   // beat imminent - it will carry the flagged parts
            if (!_staleArmed)
            {
                _staleArmed = true;
                _staleTimer ??= new Timer(_ => OnStaleDeadline(), null, Timeout.Infinite, Timeout.Infinite);
                _staleTimer.Change(_options.StaleProbeDelay, Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <summary>
    /// The grace period after a stale hint has elapsed. Query whatever is still stale -
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
            // Routine probe now imminent - keep the flags; the beat composes FEW every time and
            // FEI from the Inventory flag, so it will carry the stale parts itself.
            if (_nextRoutineProbeUtc - now <= _options.MinProbeSpacing)
                return;
            // Off-cadence probes are triggered only by who-list / inventory staleness, but FES
            // must lead every query so the server reliably refreshes the requested sections.
            bool few = (_staleFlags & StaleStats.WhoList)   != 0 && _includeFew;
            bool fei = (_staleFlags & StaleStats.Inventory) != 0 && _includeFei;
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

    // -- In-combat inventory probe ----------------------------------------------

    /// <summary>
    /// One parsed line, checked for the four wordings that mean the loadout just changed (see
    /// <see cref="InventoryChangeLines"/>). Runs on the Feed thread as part of parsing incoming
    /// bytes - never from input handling, which is why the combat and game-mode gates come before
    /// the regexes.
    ///
    /// <para><b>In combat only.</b> That is where the measurement lives and where a stale strength
    /// reading costs the player something. Out of combat the routine heartbeat is soon enough, and a
    /// player emptying a hoard into a container would otherwise spend a tick per item.</para>
    /// </summary>
    private void NoteInventoryChangeLine(StyledLine line)
    {
        if (!InGameMode || line.IsPartial || !_combat.InCombat)
            return;
        if (!InventoryChangeLines.IsChange(line.PlainText))
            return;
        lock (_fesLock)
        {
            if (_fesInterval <= TimeSpan.Zero)
                return;
            var now = DateTime.UtcNow;
            _inventoryProbePending = true;
            _inventoryChangeSeenUtc = now;
            ScheduleInventoryProbeLocked(now);
        }
    }

    /// <summary>
    /// When the pending probe goes out if no player command carries it first. Restarting the timer
    /// rather than "arm if idle" is what makes the delay a QUIET PERIOD measured from the LAST
    /// change line: a bulk command's items all arrive in one server frame - one captured clog has
    /// three inside the same millisecond - and they must cost one probe, not one each.
    ///
    /// <para><b>The tick guard.</b> Commands are drained from a server-side queue one per tick, so a
    /// probe placed just before a boundary can take the slot the player's own action wanted; one
    /// worked case is <c>e,feed coal to dragon</c> failing because the creature got the tick
    /// first. When the next boundary is inside
    /// <see cref="MudSessionOptions.InventoryProbeTickGuard"/> the probe is moved to just PAST it
    /// (<see cref="MudSessionOptions.InventoryProbeTickClearance"/>), which is the position with the
    /// longest clear run before the following boundary. The priority this encodes: the probe is not
    /// critical, but it is very useful - so it always yields to the player's timing, and it is never
    /// dropped merely for being inconvenient.</para>
    ///
    /// <para><b>The lattice is not always known.</b> <see cref="MillisecondsToNextCombatTick"/>
    /// returns null until the phase estimate has settled (it needs samples), and then the plain
    /// delay is used with no guard at all. That is the honest behaviour: no phase, no claim about
    /// where the boundary is. When it IS known, the +50ms placement is comfortably robust to the
    /// estimate's own error - one lattice fits a whole session to a ~26ms median residual (see
    /// Mucka.Core.TickPhase, which owns the estimate; this asks it rather than keeping a second
    /// clock).</para>
    /// </summary>
    private void ScheduleInventoryProbeLocked(DateTime now)
    {
        var delay = _options.InventoryProbeDebounce;
        if (MillisecondsToNextCombatTick?.Invoke() is double toTick
            && toTick <= _options.InventoryProbeTickGuard.TotalMilliseconds)
        {
            delay = TimeSpan.FromMilliseconds(toTick) + _options.InventoryProbeTickClearance;
        }
        _inventoryProbeTimer ??= new Timer(_ => OnInventoryProbeDeadline(), null, Timeout.Infinite, Timeout.Infinite);
        _inventoryProbeTimer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Claims the pending inventory probe and returns its bytes, or null to leave it pending (the
    /// timer having been re-armed) or to abandon it. Caller holds <see cref="_fesLock"/>.
    ///
    /// <para><b>Why FES and FEI together rather than FES alone.</b> Each costs the player a tick, so
    /// the pair costs two - but this probe REPLACES one the client already sends. Every one of these
    /// lines is un-coded, which sets <c>StaleStats.Inventory</c> in the parser, and OnStaleDeadline
    /// answers that with exactly <c>FES,FEI</c> a couple of hundred milliseconds later (measured
    /// across the capture corpus: median 209ms from "X dropped." to the next FES-carrying probe, 85%
    /// within 500ms). Clearing both flag groups here means that generic probe finds nothing stale
    /// and stays home, so the marginal cost of this feature against today's behaviour is zero ticks.
    /// FES alone would be one tick cheaper than today, but only by leaving Inventory flagged for the
    /// generic path to spend later and less usefully.</para>
    ///
    /// <para><b>Nothing needs swallowing.</b> Bartle: a command interrupt "will not be echoed, but
    /// will cause the FES to be executed", and its reply is wholly C1-bracketed - the FES packet is
    /// consumed by the decoder and the FEI list is diverted into the parser's own buffer
    /// (MudStreamParser's InFeiResponseContext), so neither ever reaches LineReady. That is why the
    /// existing heartbeat, which sends this same interrupt at a ~1.26s median cadence, is invisible
    /// in the terminal today. TrySwallowSetupLine exists for TYPED commands whose replies are plain
    /// text; routing an interrupt through it would leave the swallow window waiting for a first
    /// content line that never comes, eating real combat text instead.</para>
    /// </summary>
    private byte[]? TakeInventoryProbeLocked(DateTime now)
    {
        if (!_inventoryProbePending)
            return null;
        if (_fesInterval <= TimeSpan.Zero || !InGameMode)
        {
            StopInventoryProbeLocked();
            return null;
        }
        if (_lastProbeSentUtc >= _inventoryChangeSeenUtc)
        {
            // Some other probe already went out AFTER the change, so the server has already been
            // asked about the new loadout and its reply is post-drop. Spending a tick to ask the
            // same question again is exactly what this feature exists to avoid. Inventory is left
            // flagged in case that probe was one of the FES-only shapes and carried no FEI; the next
            // routine beat then picks it up at no extra cost.
            _staleFlags |= StaleStats.Inventory;
            StopInventoryProbeLocked();
            return null;
        }
        if (_probesHeld || _resetDiscoveryHold || _resetClock.IsSamplingInFlight)
        {
            // Something else owns the wire; wait out another quiet period and try again.
            ScheduleInventoryProbeLocked(now);
            return null;
        }
        var wait = _options.MinProbeSpacing - (now - _lastProbeSentUtc);
        if (wait > TimeSpan.Zero)
        {
            _inventoryProbeTimer?.Change(wait, Timeout.InfiniteTimeSpan);
            return null;
        }
        if (_nextRoutineProbeUtc - now <= _options.MinProbeSpacing)
        {
            // The beat is about to fire and always leads with FES; leaving Inventory flagged is what
            // makes it carry the FEI too. Spending a tick here to save that much is not a trade
            // worth making mid-fight.
            _staleFlags |= StaleStats.Inventory;
            StopInventoryProbeLocked();
            return null;
        }
        _staleFlags &= ~(StaleStats.AllStats | StaleStats.Inventory);
        _lastProbeSentUtc = now;
        _lastFesSentUtc = now;
        StopInventoryProbeLocked();
        return InventoryProbe;
    }

    /// <summary>The quiet period elapsed with no player command to ride: send the probe on its own.</summary>
    private void OnInventoryProbeDeadline()
    {
        byte[]? probe;
        lock (_fesLock)
            probe = TakeInventoryProbeLocked(DateTime.UtcNow);
        if (probe is null)
            return;
        OutgoingBytes?.Invoke(probe);
        ProbeSent?.Invoke();
    }

    /// <summary>
    /// Whether a pending probe may ride out in front of this outgoing command.
    ///
    /// <para><b>Single commands only.</b> MUD2 drains a comma-combo one element per tick, so a probe
    /// prepended to <c>e,feed coal to dragon</c> pushes every element back a tick - one example of a
    /// combo that fails when something else takes the tick first. A combo therefore
    /// goes out untouched and the probe waits for its timer, by which point it is BEHIND the combo
    /// in the queue: strictly better placement than prepending, which is why this is a refusal
    /// rather than a fallback.</para>
    ///
    /// <para>Nothing else needs excluding here: the setup batch goes out through
    /// <see cref="Send(byte[])"/>, not this path, and a probe can only be pending while a fight is
    /// open.</para>
    /// </summary>
    private static bool CanCarryInventoryProbe(string line) => !line.Contains(',');

    private void StopInventoryProbeLocked()
    {
        _inventoryProbePending = false;
        _inventoryProbeTimer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// A player name was seen bracketed by a C05 presence code - that player is online.
    /// If they are missing from the last complete FEW response, the Online list is stale.
    /// The bracketed text may be a full persona ("Polly the witch"), a titled level-10
    /// mortal ("Lady Polly"), or run on into the sentence.
    /// </summary>
    private void OnPresenceName(string name)
    {
        // No baseline yet - nothing to compare against; the routine probe establishes one.
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
    /// If the server just sent real data but the last FES-carrying probe never drew its
    /// stats reply (the character was asleep - FES/FEI/FEW no-op during sleep), fire the heartbeat
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
        if (now - _lastFesSentUtc <= _options.WakeReplySlack)  // in flight - give the reply time to land
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

    // -- Reset-time discovery (driven by ResetClock) -----------------------------
    // ResetClock owns the one-time edge search; these are its wire hooks. While discovering it holds
    // the routine heartbeat suspended (SetResetDiscoveryHold) so a compound reply never races its
    // rate-limited FES samples, and its samples deliberately BYPASS MinProbeSpacing (they are self-paced
    // at >= ~501 ms). The global spacing floor still guards every reactive/routine path.

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
    /// the panel past its stale threshold at every retried minute boundary - the "status updates
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
