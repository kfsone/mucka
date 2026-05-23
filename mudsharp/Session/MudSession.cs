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
    private GameStatsSnapshot _currentStats = GameStatsSnapshot.Empty;
    private string? _currentDreamword;
    // FES subscription bytes: ESC-[FES ESC-]  (0x1B 0x2D 0x5B 0x46 0x45 0x53 0x1B 0x2D 0x5D)
    // Source: Mucka MudStream.cs FesSubscriptionRequestBytes; verified against Clio telnet.l txfes sequence.
    private static readonly byte[] FesSubscription =
        [0x1B, 0x2D, 0x5B, 0x46, 0x45, 0x53, 0x1B, 0x2D, 0x5D];

    // ── Public events (forwarded from parser) ─────────────────────────────────
    public event Action<StyledLine>? LineReady;
    public event Action<GameStatsSnapshot>? StatsUpdated;
    public event Action? GameModeEntered;
    public event Action? GameModeExited;
    public event Action<byte[]>? OutgoingBytes;
    public event Action<string?>? DreamwordChanged;
    public event Action<string>? ClientModeReceived;
    public event Action<string>? SoundRequested;

    // ── Public state ───────────────────────────────────────────────────────────
    public GameStatsSnapshot CurrentStats => _currentStats;
    public string? CurrentDreamword => _currentDreamword;
    public bool InGameMode => _parser.InGameMode;

    public MudSession(MudSessionOptions? options = null)
    {
        _options = options ?? new MudSessionOptions();
        _parser = new MudStreamParser();
        WireParserEvents();
    }

    /// <summary>Feed raw bytes from the network. Thread-safe relative to the FES timer — Feed() itself is not thread-safe.</summary>
    public void Feed(ReadOnlySpan<byte> data) => _parser.Feed(data);

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
        _parser.OutgoingBytes += bytes => OutgoingBytes?.Invoke(bytes);
        _parser.DreamwordChanged += OnDreamwordChanged;
        _parser.ClientModeReceived += data => ClientModeReceived?.Invoke(data);
        _parser.SoundRequested += s => SoundRequested?.Invoke(s);
    }

    private void MergeStats(GameStatsSnapshot partial)
    {
        // Keep _currentDreamword in sync when the dreamword arrives via text analysis
        // (pre-game path, DreamwordLineRegex) rather than the binary C15 decoder.
        // In game mode the C15 path fires DreamwordChanged which updates _currentDreamword
        // directly; in pre-game mode the text path is the only source.
        if (partial.DreamWord != null)
            _currentDreamword = partial.DreamWord;

        // Merge: only overwrite fields that differ from zero/default in the partial snapshot.
        _currentStats = new GameStatsSnapshot(
            Stamina:      partial.Stamina     != 0 ? partial.Stamina     : _currentStats.Stamina,
            MaxStamina:   partial.MaxStamina  != 0 ? partial.MaxStamina  : _currentStats.MaxStamina,
            Score:        partial.Score       != 0 ? partial.Score       : _currentStats.Score,
            Strength:     partial.Strength    != 0 ? partial.Strength    : _currentStats.Strength,
            MaxStrength:  partial.MaxStrength != 0 ? partial.MaxStrength : _currentStats.MaxStrength,
            Dexterity:    partial.Dexterity   != 0 ? partial.Dexterity   : _currentStats.Dexterity,
            MaxDexterity: partial.MaxDexterity != 0 ? partial.MaxDexterity : _currentStats.MaxDexterity,
            CurrentMagic: partial.CurrentMagic != 0 ? partial.CurrentMagic : _currentStats.CurrentMagic,
            MaxMagic:     partial.MaxMagic    != 0 ? partial.MaxMagic    : _currentStats.MaxMagic,
            IsBlind:      partial.IsBlind     || _currentStats.IsBlind,
            IsDeaf:       partial.IsDeaf      || _currentStats.IsDeaf,
            IsCrippled:   partial.IsCrippled  || _currentStats.IsCrippled,
            IsDumb:       partial.IsDumb      || _currentStats.IsDumb,
            Weather:      partial.Weather     != ' ' ? partial.Weather   : _currentStats.Weather,
            TimeToReset:  partial.TimeToReset != 0 ? partial.TimeToReset : _currentStats.TimeToReset,
            DreamWord:    _currentDreamword,
            PersonaSaved: partial.PersonaSaved || _currentStats.PersonaSaved,
            AccountId:    partial.AccountId   ?? _currentStats.AccountId,
            Privs:        partial.Privs       != 0 ? partial.Privs       : _currentStats.Privs,
            StaminaColor: partial.StaminaColor != 0 ? partial.StaminaColor : _currentStats.StaminaColor
        );
        StatsUpdated?.Invoke(_currentStats);
    }

    private void OnGameModeEntered()
    {
        GameModeEntered?.Invoke();
        SendFesSubscription();
        _fesTimer = new Timer(_ => SendFesSubscription(), null,
            _options.FesHeartbeatInterval, _options.FesHeartbeatInterval);
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

    private void SendFesSubscription() => OutgoingBytes?.Invoke(FesSubscription);

    private void StopFesTimer()
    {
        _fesTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _fesTimer?.Dispose();
        _fesTimer = null;
    }
}
