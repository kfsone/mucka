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
    // Slack beyond the heartbeat interval before missing replies count as stale.
    private static readonly TimeSpan StaleReplySlack = TimeSpan.FromSeconds(5);
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
    private DateTime _nextRoutineProbeUtc;
    // While held, routine and stale probes are suppressed (see SetProbeHold).
    private bool _probesHeld;
    // First word of each name from the last complete FEW response (Feed thread only).
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

    private GameStatsSnapshot _currentStats = GameStatsSnapshot.Empty;
    private string? _currentDreamword;
    // Periodic probe: ESC-[FES,FEW,FEI ESC-] — fetches stats, who-list, inventory, and exits together.
    // Reactive C1-triggered FES sends (in Mud2C1Decoder) still use FES-only to avoid clearing
    // the who list during combat/spell events.
    // The FEW and FEI components are omitted when those side-panel sections are disabled
    // (see UpdateSubscriptionOptions).
    private bool _includeFew = true;
    private bool _includeFei = true;
    // While the mapping window has focus the heartbeat collapses to FEW-only: stats (FES)
    // and inventory (FEI) are irrelevant mid-survey, but the online list must keep flowing
    // so an arriving PKer is visible. Separate escaped FEx queries are fine standalone.
    private bool _mappingFocus;
    private byte[] _fesSubscription = BuildSubscription(includeFew: true, includeFei: true);

    // ── Public events (forwarded from parser) ─────────────────────────────────
    public event Action<StyledLine>? LineReady;
    public event Action<GameStatsSnapshot>? StatsUpdated;
    public event Action? GameModeEntered;
    public event Action? GameModeExited;
    public event Action<byte[]>? OutgoingBytes;
    public event Action? BellReceived;
    public event Action<string?>? DreamwordChanged;
    public event Action<string>? ClientModeReceived;
    public event Action<string>? SoundRequested;
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

    // ── Public state ───────────────────────────────────────────────────────────
    public GameStatsSnapshot CurrentStats => _currentStats;
    public string? CurrentDreamword => _currentDreamword;
    public bool InGameMode => _parser.InGameMode;

    public MudSession(MudSessionOptions? options = null)
    {
        _options = options ?? new MudSessionOptions();
        _fesInterval = _options.FesHeartbeatInterval;
        _parser = new MudStreamParser();
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
            }
        }
    }

    /// <summary>
    /// Update which components are included in the periodic heartbeat probe.
    /// When <paramref name="includeFew"/> is false the online list (FEW) is omitted.
    /// When <paramref name="includeFei"/> is false the inventory/room-items list (FEI) is omitted.
    /// May be called from any thread.
    /// </summary>
    public void UpdateSubscriptionOptions(bool includeFew, bool includeFei)
    {
        lock (_fesLock)
        {
            if (_includeFew == includeFew && _includeFei == includeFei) return;
            _includeFew = includeFew;
            _includeFei = includeFei;
            _fesSubscription = BuildSubscriptionLocked();
        }
    }

    /// <summary>Mapping window focus gained (true) / lost (false). While focused the
    /// periodic heartbeat is reduced to FEW-only so the online list keeps refreshing
    /// (PKer awareness) without FES/FEI noise the operator does not need mid-survey.
    /// May be called from any thread.</summary>
    public void SetMappingFocus(bool focused)
    {
        lock (_fesLock)
        {
            if (_mappingFocus == focused) return;
            _mappingFocus = focused;
            _fesSubscription = BuildSubscriptionLocked();
        }
    }

    // Current heartbeat payload given focus + subscription toggles. Caller holds _fesLock.
    private byte[] BuildSubscriptionLocked()
        => _mappingFocus
            ? System.Text.Encoding.ASCII.GetBytes("\x1b-[FEW\x1b-]")   // online list only
            : BuildSubscription(_includeFew, _includeFei);

    private static byte[] BuildSubscription(bool includeFew, bool includeFei)
    {
        // ESC-[ FES [,FEW] [,FEI] ESC-]
        var args = "FES";
        if (includeFew) args += ",FEW";
        if (includeFei) args += ",FEI";
        // The framing bytes: ESC '-' '[' ... ESC '-' ']'
        var payload = System.Text.Encoding.ASCII.GetBytes($"\x1b-[{args}\x1b-]");
        return payload;
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
            // Drop mapping focus and restore the full FES/FEW/FEI heartbeat. The session is
            // reused across reconnects/relogs, so leaving focus set would resume FEW-only on
            // the next game-mode entry and silently starve the main window of stats/inventory.
            _mappingFocus = false;
            _fesSubscription = BuildSubscriptionLocked();
            _pendingSniff = null;
            _sniffInFlight = null;
        }
        _onlineNames.Clear();
        _pendingOnlineNames.Clear();
        _parser.Reset();
        _currentStats = GameStatsSnapshot.Empty;
        _currentDreamword = null;
    }

    public void Dispose()
    {
        StopFesTimer();
        lock (_fesLock)
        {
            StopStaleProbeLocked();
            _staleTimer?.Dispose();
            _staleTimer = null;
        }
    }

    // ── Private ────────────────────────────────────────────────────────────────
    private void WireParserEvents()
    {
        _parser.LineReady += line =>
        {
            // Swallow the echo + reply of an injected `value <name>` sniff so it never
            // reaches the terminal. Fast volatile check keeps normal lines free of cost.
            if (_sniffInFlight != null && TryConsumeSniffLine(line))
                return;
            LineReady?.Invoke(line);
        };
        _parser.StatsUpdated += MergeStats;
        _parser.GameModeEntered += OnGameModeEntered;
        _parser.GameModeExited += OnGameModeExited;
        _parser.OutgoingBytes  += bytes => OutgoingBytes?.Invoke(bytes);
        _parser.BellReceived   += () => BellReceived?.Invoke();
        _parser.DreamwordChanged += OnDreamwordChanged;
        _parser.ClientModeReceived += data => ClientModeReceived?.Invoke(data);
        _parser.SoundRequested += s => SoundRequested?.Invoke(s);
        _parser.ProbeHintReceived += OnProbeHint;
        _parser.PresenceNameSeen  += OnPresenceName;
        _parser.FewPlayerReady += (name, color) =>
        {
            _pendingOnlineNames.Add(FirstWord(name));
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
        _parser.RoomEntered      += () => RoomEntered?.Invoke();
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
        _parser.FexListStarting  += () => FexListStarting?.Invoke();
        _parser.FexListComplete  += () => FexListComplete?.Invoke();
        _parser.ExitLineReady    += (dir, dest) => ExitLineReady?.Invoke(dir, dest);
        _parser.TerminalWidthConfirmed += w => TerminalWidthConfirmed?.Invoke(w);
    }

    private void MergeStats(GameStatsSnapshot partial)
    {
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
            if (partial.Strength     is not null || partial.MaxStrength  is not null) refreshed |= StaleStats.Strength;
            if (partial.Dexterity    is not null || partial.MaxDexterity is not null) refreshed |= StaleStats.Dexterity;
            if (partial.CurrentMagic is not null || partial.MaxMagic     is not null) refreshed |= StaleStats.Magic;
            if (partial.Score        is not null)                                     refreshed |= StaleStats.Score;
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
            MaxStrength:  partial.MaxStrength  ?? _currentStats.MaxStrength,
            Dexterity:    partial.Dexterity    ?? _currentStats.Dexterity,
            MaxDexterity: partial.MaxDexterity ?? _currentStats.MaxDexterity,
            CurrentMagic: partial.CurrentMagic ?? _currentStats.CurrentMagic,
            MaxMagic:     partial.MaxMagic     ?? _currentStats.MaxMagic,
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
            StaminaColor: partial.StaminaColor ?? _currentStats.StaminaColor
        );
        StatsUpdated?.Invoke(_currentStats);
    }

    private void OnGameModeEntered()
    {
        GameModeEntered?.Invoke();
        lock (_fesLock)
        {
            _lastProbeReplyUtc = DateTime.UtcNow;   // nothing is stale yet
            if (_fesInterval > TimeSpan.Zero)
            {
                SendFesSubscription();
                _fesTimer = new Timer(_ => SendFesSubscription(), null, _fesInterval, _fesInterval);
            }
        }

        // We need to request our first front-end exit list *and* tell the game to send use
        // exit lists with every room.
        SendLine("auto fex");
        Send(System.Text.Encoding.Latin1.GetBytes("\x1b-[FEX\x1b-]"));
    }

    private void OnGameModeExited()
    {
        StopFesTimer();
        lock (_fesLock)
        {
            StopStaleProbeLocked();
            // Drop mapping focus and restore the full FES/FEW/FEI heartbeat. The session is
            // reused across reconnects/relogs, so leaving focus set would resume FEW-only on
            // the next game-mode entry and silently starve the main window of stats/inventory.
            _mappingFocus = false;
            _fesSubscription = BuildSubscriptionLocked();
            _pendingSniff = null;
            _sniffInFlight = null;
        }
        _onlineNames.Clear();
        _pendingOnlineNames.Clear();
        GameModeExited?.Invoke();
    }

    private void OnDreamwordChanged(string? word)
    {
        _currentDreamword = word;
        DreamwordChanged?.Invoke(word);
    }

    private void SendFesSubscription()
    {
        byte[] payload;
        lock (_fesLock)
        {
            if (_probesHeld) return;   // skipped beat; SetProbeHold(false) re-phases the tick
            var now = DateTime.UtcNow;
            _lastProbeSentUtc = now;
            _nextRoutineProbeUtc = now + _fesInterval;
            _staleFlags = StaleStats.None;   // the full probe refreshes everything pending
            payload = _fesSubscription;
            // Ride a queued sniff (value <name>) on this probe, but only when the probe carries
            // FEW: the FEW-complete boundary is what closes out an invisible (no-reply) sniff, so
            // a FEW-less probe could never resolve it. LIFO — one sniff per probe.
            if (_pendingSniff is { } sniff && (_mappingFocus || _includeFew))
            {
                _sniffInFlight = sniff;
                _pendingSniff = null;
                var prefix = System.Text.Encoding.Latin1.GetBytes("value " + sniff + "\r\n");
                payload = new byte[prefix.Length + _fesSubscription.Length];
                Buffer.BlockCopy(prefix, 0, payload, 0, prefix.Length);
                Buffer.BlockCopy(_fesSubscription, 0, payload, prefix.Length, _fesSubscription.Length);
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
            if (_nextRoutineProbeUtc - DateTime.UtcNow <= _options.MinProbeSpacing)
                return;
            _staleFlags |= kinds;
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
            if (_probesHeld)
            {
                // A mapping operation owns the wire; keep the flags and try again shortly.
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
            // Routine probe now imminent — it refreshes everything anyway.
            if (_nextRoutineProbeUtc - now <= _options.MinProbeSpacing)
            {
                _staleFlags = StaleStats.None;
                return;
            }
            bool fes = (_staleFlags & StaleStats.AllStats)  != 0;
            bool few = (_staleFlags & StaleStats.WhoList)   != 0;
            bool fei = (_staleFlags & StaleStats.Inventory) != 0;
            _staleFlags = StaleStats.None;
            _lastProbeSentUtc = now;
            if (fes && few && fei)
            {
                // Everything is stale: treat as an early routine probe and re-phase the tick.
                _nextRoutineProbeUtc = now + _fesInterval;
                _fesTimer?.Change(_fesInterval, _fesInterval);
                probe = _fesSubscription;
            }
            else
            {
                var cmds = new List<string>(2);
                if (fes) cmds.Add("FES");
                if (few) cmds.Add("FEW");
                if (fei) cmds.Add("FEI");
                probe = System.Text.Encoding.Latin1.GetBytes("\x1b-[" + string.Join(',', cmds) + "\x1b-]");
            }
        }
        OutgoingBytes?.Invoke(probe);
        ProbeSent?.Invoke();
    }

    /// <summary>
    /// A player name was seen bracketed by a C05 presence code — that player is online.
    /// If they are missing from the last complete FEW response, the Online list is stale.
    /// The bracketed text may be a full persona ("Polly the witch") or run on into the
    /// sentence, so only the first word (the character name proper) is compared.
    /// </summary>
    private void OnPresenceName(string name)
    {
        // No baseline yet — nothing to compare against; the routine probe establishes one.
        if (_onlineNames.Count == 0)
            return;
        if (!_onlineNames.Contains(FirstWord(name)))
            OnProbeHint(StaleStats.WhoList);
    }

    private static string FirstWord(string name)
    {
        int space = name.IndexOf(' ');
        return space < 0 ? name : name[..space];
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
    /// BUGS #5: if the server just sent real data but probe replies have gone stale (the
    /// character was asleep — FES/FEI/FEW no-op during sleep), fire the heartbeat now and
    /// re-phase its period, so the panel recovers on wake instead of up to a full interval
    /// later. Rate-limited so a wake-up text burst fires only one early probe.
    /// </summary>
    private void MaybeSendWakeProbe()
    {
        if (_fesInterval <= TimeSpan.Zero || !InGameMode)
            return;
        var now = DateTime.UtcNow;
        if (now - _lastProbeReplyUtc <= _fesInterval + StaleReplySlack)
            return;
        if (now - _lastWakeProbeUtc < WakeProbeFloor)
            return;
        _lastWakeProbeUtc = now;
        lock (_fesLock)
            _fesTimer?.Change(TimeSpan.Zero, _fesInterval);   // fire immediately, keep the period
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
