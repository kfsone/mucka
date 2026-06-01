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
    private Timer? _fesTimer;
    private TimeSpan _fesInterval;
    private GameStatsSnapshot _currentStats = GameStatsSnapshot.Empty;
    private string? _currentDreamword;
    // Periodic probe: ESC-[FES,FEW,FEI ESC-] — fetches stats, who-list, inventory, and exits together.
    // Reactive C1-triggered FES sends (in Mud2C1Decoder) still use FES-only to avoid clearing
    // the who list during combat/spell events.
    private static readonly byte[] FesAndFewSubscription =
        [0x1B, 0x2D, 0x5B, 0x46, 0x45, 0x53, 0x2C, 0x46, 0x45, 0x57, 0x2C, 0x46, 0x45, 0x49, 0x1B, 0x2D, 0x5D];

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
    /// <summary>
    /// Server confirmed the terminal width (ESC-<n>W response or "[New terminal width is N]" annotation).
    /// Payload is the confirmed column count.
    /// </summary>
    public event Action<int>? TerminalWidthConfirmed;

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
        _fesInterval = interval;
        StopFesTimer();
        if (InGameMode && _fesInterval > TimeSpan.Zero)
            _fesTimer = new Timer(_ => SendFesSubscription(), null, _fesInterval, _fesInterval);
    }

    /// <summary>Feed raw bytes from the network. Thread-safe relative to the FES timer — Feed() itself is not thread-safe.</summary>
    public void Feed(ReadOnlySpan<byte> data) => _parser.Feed(data);

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
        _parser.Reset();
        _currentStats = GameStatsSnapshot.Empty;
        _currentDreamword = null;
    }

    public void Dispose()
    {
        StopFesTimer();
    }

    // ── Private ────────────────────────────────────────────────────────────────
    private void WireParserEvents()
    {
        _parser.LineReady += line => LineReady?.Invoke(line);
        _parser.StatsUpdated += MergeStats;
        _parser.GameModeEntered += OnGameModeEntered;
        _parser.GameModeExited += OnGameModeExited;
        _parser.OutgoingBytes  += bytes => OutgoingBytes?.Invoke(bytes);
        _parser.BellReceived   += () => BellReceived?.Invoke();
        _parser.DreamwordChanged += OnDreamwordChanged;
        _parser.ClientModeReceived += data => ClientModeReceived?.Invoke(data);
        _parser.SoundRequested += s => SoundRequested?.Invoke(s);
        _parser.FewPlayerReady += (name, color) => FewPlayerReady?.Invoke(name, color);
        _parser.FewListStarting  += () => FewListStarting?.Invoke();
        _parser.FewListComplete  += () => FewListComplete?.Invoke();
        _parser.RoomEntered      += () => RoomEntered?.Invoke();
        _parser.RoomShortReady   += name => RoomShortReady?.Invoke(name);
        _parser.FeiItemReady     += item => FeiItemReady?.Invoke(item);
        _parser.FeiListStarting  += () => FeiListStarting?.Invoke();
        _parser.FeiListComplete  += () => FeiListComplete?.Invoke();
        _parser.FexItemReady     += item => FexItemReady?.Invoke(item);
        _parser.FexListStarting  += () => FexListStarting?.Invoke();
        _parser.FexListComplete  += () => FexListComplete?.Invoke();
        _parser.TerminalWidthConfirmed += w => TerminalWidthConfirmed?.Invoke(w);
    }

    private void MergeStats(GameStatsSnapshot partial)
    {
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
        if (_fesInterval > TimeSpan.Zero)
        {
            SendFesSubscription();
            _fesTimer = new Timer(_ => SendFesSubscription(), null, _fesInterval, _fesInterval);
        }

        // We need to request our first front-end exit list *and* tell the game to send use
        // exit lists with every room.
        SendLine("auto fex");
        Send(System.Text.Encoding.Latin1.GetBytes("\x1b-[FEX\x1b-]"));
    }

    private void OnGameModeExited()
    {
        StopFesTimer();
        GameModeExited?.Invoke();
    }

    private void OnDreamwordChanged(string? word)
    {
        _currentDreamword = word;
        DreamwordChanged?.Invoke(word);
    }

    private void SendFesSubscription() => OutgoingBytes?.Invoke(FesAndFewSubscription);

    private void StopFesTimer()
    {
        _fesTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _fesTimer?.Dispose();
        _fesTimer = null;
    }
}
