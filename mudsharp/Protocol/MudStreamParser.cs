using MudSharp.Models;
using System.Text;

namespace MudSharp.Protocol;

/// <summary>
/// Incremental byte-stream parser for the MUD2 telnet protocol.
///
/// THREADING CONTRACT:
/// All events fire synchronously on the thread that called <see cref="Feed"/>.
/// Consumers are responsible for marshaling to their own UI or processing thread.
/// MudStreamParser is NOT thread-safe; Feed() must not be called concurrently.
/// </summary>
public sealed class MudStreamParser
{
    // ── Events ────────────────────────────────────────────────────────────────
    /// <summary>A line (or partial line) of styled text is ready to display.</summary>
    public event Action<StyledLine>? LineReady;

    /// <summary>FES stats snapshot has been updated.</summary>
    public event Action<GameStatsSnapshot>? StatsUpdated;

    /// <summary>Server signalled game-mode entry (0x9D 0x9C 0xFF 0xFF).</summary>
    public event Action? GameModeEntered;

    /// <summary>Parser has exited game mode (via Reset()).</summary>
    public event Action? GameModeExited;

    /// <summary>
    /// A room-short sequence (C02+C01) appeared at frame start — the player has entered or
    /// is looking at the current room. The room name follows via <see cref="LineReady"/>.
    /// Not fired for C02+C01 that appears mid-frame (exits/look-around).
    /// </summary>
    public event Action? RoomEntered;

    /// <summary>Parser wants to send bytes to the server.</summary>
    public event Action<byte[]>? OutgoingBytes;

    /// <summary>A BEL character (0x07) was received in the stream.</summary>
    public event Action? BellReceived;

    /// <summary>Dreamword has changed. Null means cleared.</summary>
    public event Action<string?>? DreamwordChanged;

    /// <summary>C95 client-mode data block received.</summary>
    public event Action<string>? ClientModeReceived;

    /// <summary>A sound effect should be played. Payload is the app-package-relative asset path, e.g. "sounds/clio.1311.wav".</summary>
    public event Action<string>? SoundRequested;

    /// <summary>A player name decoded from the WHO-list (FEW response) is ready.</summary>
    public event Action<string>? FewPlayerReady;

    /// <summary>
    /// A FEW-response context (C12+C08+C05 wrapper) has just opened.
    /// Consumers should start accumulating names; do not replace the visible list yet.
    /// </summary>
    public event Action? FewListStarting;

    /// <summary>
    /// A FEW-response context has just closed — all names for this response have been
    /// delivered via <see cref="FewPlayerReady"/>. Replace the visible list atomically now.
    /// </summary>
    public event Action? FewListComplete;

    // ── Sub-parsers (set by internal wiring, replaceable for testing) ─────────
    internal TelnetNegotiator Telnet { get; }
    internal AnsiSgrState Ansi { get; }
    internal Mud2C1Decoder C1 { get; }
    internal GameLineAnalyzer LineAnalyzer { get; }

    // ── Parser state ──────────────────────────────────────────────────────────
    private ParserState _state = ParserState.Normal;
    private readonly List<byte> _iacSbBuf = new();
    private readonly List<byte> _c1Buf = new();
    private byte _c1Lead;

    // Pending reprocess: a byte queued by a sub-parser to be replayed in Normal after its call returns.
    private byte? _pendingReprocess;

    // ── Text accumulation ─────────────────────────────────────────────────────
    private readonly List<StyledSpan> _spans = new();
    private readonly StringBuilder _text = new();
    private bool _inGameMode;

    // ── Game state ────────────────────────────────────────────────────────────
    public bool InGameMode => _inGameMode;

    // ── Frame tracking ────────────────────────────────────────────────────────
    // Set after the game '*' prompt is shown (EmitPartialOnPop in PopColour).
    // Cleared when the first C1 dispatch or printable text arrives in the new frame.
    // Used to distinguish a room-short (C02+C01) "where you are" from one that appears
    // mid-frame as exits or look-around info.
    private bool _atFrameStart;
    internal bool AtFrameStart => _atFrameStart;
    internal void SetFrameStart() => _atFrameStart = true;
    internal void ClearFrameStart() => _atFrameStart = false;

    // ── FEW-response suppression ──────────────────────────────────────────────
    // Set when the parser enters a C12+C08+C05 (FE WHO) context block. While active,
    // display output is suppressed but FewPlayerReady events still fire.
    // Cleared when the colour stack returns to the depth it was at before the push.
    private bool _inFewResponseContext;
    private int _fewContextDepth;

    internal bool InFewResponseContext => _inFewResponseContext;
    internal int FewContextDepth => _fewContextDepth;
    internal void EnterFewContext(int targetDepth)
    {
        _inFewResponseContext = true;
        _fewContextDepth = targetDepth;
    }
    internal void ExitFewContext() => _inFewResponseContext = false;
    internal void EmitFewListStarting() => FewListStarting?.Invoke();
    internal void EmitFewListComplete() => FewListComplete?.Invoke();

    /// <summary>
    /// Mirrors Clio's prompt_allowed flag.
    /// Set to true by each real game '\n' (via EmitChar); set to false by the C01
    /// game-mode dispatch when it shows the '*' prompt. Persists across TCP packet
    /// boundaries — this is what lets the C01 gate distinguish a real prompt (which
    /// follows a game newline, possibly in a previous packet) from a FES heartbeat
    /// (which arrives when no real '\n' has occurred since the last prompt display).
    /// C98 must NOT set this; doing so would make every FES heartbeat appear as a
    /// real prompt because C98 always precedes the C01 prompt preamble bytes.
    /// </summary>
    internal bool PromptAllowed { get; set; } = true;

    /// <summary>
    /// When true, EmitChar discards non-newline characters.
    /// Set by the C01 game-mode dispatch when PromptAllowed is false (suppress end-of-frame prompt).
    /// Cleared by the first PopColour call after being set.
    /// </summary>
    internal bool SuppressNextText { get; set; }

    /// <summary>
    /// When true, the next PopColour call will emit a partial line then clear spans.
    /// Set by the C01 game-mode dispatch when PromptAllowed is true (show prompt once).
    /// Cleared by the first PopColour call after being set.
    /// </summary>
    internal bool EmitPartialOnPop { get; set; }
    /// <summary>Current dreamword (updated by C15 sequences; included in FES snapshots).</summary>
    internal string? CurrentDreamword { get; private set; }

    /// <summary>Account ID from the last C95 Rule A block; included in FES snapshots.</summary>
    internal string? CurrentAccountId { get; private set; }

    /// <summary>Privilege level from the last C95 Rule A block; included in FES snapshots.</summary>
    internal int CurrentPrivs { get; private set; }

    /// <summary>Most recent FES weather char; used by C14 conditional txfes logic.</summary>
    internal char CurrentWeather { get; private set; }

    // ── Constructor ────────────────────────────────────────────────────────────
    public MudStreamParser()
    {
        Ansi = new AnsiSgrState();
        Telnet = new TelnetNegotiator(send => OutgoingBytes?.Invoke(send));
        C1 = new Mud2C1Decoder(this);
        LineAnalyzer = new GameLineAnalyzer();
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Feed raw bytes from the network into the parser.
    /// May emit events synchronously before returning.
    /// </summary>
    public void Feed(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            ProcessByte(b);
    }

    /// <summary>
    /// Update the advertised terminal window size. Sends an updated NAWS subnegotiation if
    /// NAWS has already been negotiated. May be called from any thread.
    /// </summary>
    public void SetWindowSize(int cols, int rows) => Telnet.SetWindowSize(cols, rows);

    /// <summary>Reset all parser state. Call on disconnect before reuse.
    /// Fires <see cref="GameModeExited"/> if currently in game mode.
    /// </summary>
    public void Reset()
    {
        ExitGameMode();

        // If we were mid-sequence when the connection dropped, surface whatever plain text
        // was accumulated so the caller can at least show what was received. Binary C1
        // payload in _c1Buf is not recoverable and is silently dropped.
        if (_text.Length > 0 || _spans.Count > 0)
            EmitPartialLine();

        // Reset sub-parsers before clearing _state so they can inspect the current state.
        // C1.Reset takes _state by ref and may correct it (e.g. C95Data → Normal).
        Ansi.Reset();
        Telnet.Reset();
        C1.Reset(ref _state);

        _state = ParserState.Normal;
        _iacSbBuf.Clear();
        _c1Buf.Clear();
        _spans.Clear();
        _text.Clear();
        _pendingReprocess = null;
        PromptAllowed = true;
        SuppressNextText = false;
        EmitPartialOnPop = false;
        _inFewResponseContext = false;
        _atFrameStart = false;
        CurrentDreamword = null;
        CurrentAccountId = null;
        CurrentPrivs = 0;
        CurrentWeather = '\0';
    }

    // ── Internal helpers (called by sub-parsers) ───────────────────────────────

    internal void EmitChar(char ch)
    {
        if (ch == '\n')
        {
            FlushSpan();
            if (_inFewResponseContext)
            {
                // Discard the line but still tick PromptAllowed so the next prompt frame works.
                _spans.Clear();
                PromptAllowed = true;
                SuppressNextText = false;
                EmitPartialOnPop = false;
                return;
            }
            // In game mode, suppress all-asterisk lines entirely (Clio: prompt_allowed / preamble
            // suppression — telnet.l:438-444). These are MUD2 prompt-preamble separator lines.
            bool isAsteriskPreamble = _inGameMode && SpansAreAllAsterisks();
            var line = new StyledLine(_spans.ToArray(), isPartial: false);
            _spans.Clear();
            PromptAllowed = true;   // Clio: prompt_allowed = 1 on each newline
            SuppressNextText = false;
            EmitPartialOnPop = false;
            if (isAsteriskPreamble) return;
            var stats = LineAnalyzer.Analyze(line, _inGameMode);
            if (stats != null) StatsUpdated?.Invoke(stats);
            if (_inGameMode) { var sf = LineAnalyzer.CheckSoundTrigger(line); if (sf != null) EmitSound(sf); }
            LineReady?.Invoke(line);
        }
        else if (ch == '\r')
        {
            // Suppress bare CR
        }
        else
        {
            // Discard characters when inside a suppressed end-of-frame prompt preamble or FEW response.
            if (SuppressNextText || _inFewResponseContext) return;
            _atFrameStart = false;
            _text.Append(ch);
        }
    }

    internal void FlushSpan()
    {
        if (_text.Length == 0) return;
        _spans.Add(new StyledSpan(_text.ToString(), Ansi.CurrentStyle));
        _text.Clear();
    }

    internal void EmitPartialLine()
    {
        FlushSpan();
        if (_spans.Count == 0) return;
        var line = new StyledLine(_spans.ToArray(), isPartial: true);
        _spans.Clear();   // snapshot: next text accumulates fresh so the complete line only holds new content
        LineReady?.Invoke(line);
    }

    internal void EnterGameMode()
    {
        if (_inGameMode) return;
        _inGameMode = true;
        GameModeEntered?.Invoke();
    }

    internal void ExitGameMode()
    {
        if (!_inGameMode) return;
        _inGameMode = false;
        GameModeExited?.Invoke();
    }

    /// <summary>Clear accumulated spans (used after emitting the prompt partial line).</summary>
    internal void ClearSpans() => _spans.Clear();

    internal void EmitStatsUpdate(GameStatsSnapshot stats) => StatsUpdated?.Invoke(stats);
    internal void EmitOutgoing(byte[] bytes) => OutgoingBytes?.Invoke(bytes);
    internal void EmitBell() => BellReceived?.Invoke();
    internal void EmitDreamwordChanged(string? word)
    {
        CurrentDreamword = word;
        DreamwordChanged?.Invoke(word);
    }
    internal void EmitClientMode(string data) => ClientModeReceived?.Invoke(data);
    internal void EmitSound(string assetPath) => SoundRequested?.Invoke(assetPath);
    internal void EmitFewPlayer(string name) => FewPlayerReady?.Invoke(name);
    internal void EmitRoomEntered() => RoomEntered?.Invoke();
    internal void SetAccountInfo(string? accountId, int privs)
    {
        CurrentAccountId = accountId;
        CurrentPrivs = privs;
    }
    internal void SetWeather(char weather) => CurrentWeather = weather;

    /// <summary>
    /// Emits the current accumulated text as a partial line.
    /// C98 is an explicit "show prompt" signal — it fires unconditionally so that login-phase
    /// prompts ("Account ID:", etc.) always surface. Game-mode prompts are additionally gated
    /// by the C01+C02 PromptAllowed mechanism; ShowPrompt itself has no gate.
    /// </summary>
    internal void ShowPrompt() => EmitPartialLine();

    /// <summary>
    /// Queue a byte to be replayed through the main ProcessByte loop immediately after
    /// the current sub-parser call returns.  Safe for at most one pending byte.
    /// </summary>
    internal void QueueReprocessByte(byte b) => _pendingReprocess = b;

    /// <summary>
    /// Returns true when every accumulated span contains only '*' characters and at least
    /// one span is non-empty — i.e. the current line is the MUD2 ready-prompt re-echo.
    /// </summary>
    private bool SpansAreAllAsterisks()
    {
        bool hasContent = false;
        foreach (var span in _spans)
        {
            foreach (var c in span.Text)
            {
                if (c != '*') return false;
            }
            if (span.Text.Length > 0) hasContent = true;
        }
        return hasContent;
    }

    // ── Byte dispatch ──────────────────────────────────────────────────────────
    private void ProcessByte(byte b)
    {
        switch (_state)
        {
            case ParserState.Normal:
                ProcessNormal(b);
                break;
            case ParserState.Ff1:
                if (b == 0xFF)
                {
                    // Bare FF FF: Clio C255 rule — pop colour stack (telnet.l:1040)
                    C1.PopColour();
                    _state = ParserState.Normal;
                }
                else
                {
                    // First 0xFF was a telnet IAC; route second byte as the IAC command
                    _state = Telnet.ProcessByte(b, ParserState.Iac, _iacSbBuf);
                }
                break;
            case ParserState.Iac:
            case ParserState.IacDo:
            case ParserState.IacDont:
            case ParserState.IacWill:
            case ParserState.IacWont:
            case ParserState.IacSb:
            case ParserState.IacSbData:
            case ParserState.IacSbIac:
                _state = Telnet.ProcessByte(b, _state, _iacSbBuf);
                break;
            case ParserState.Escape:
            case ParserState.EscapeBracket:
            case ParserState.CsiParam:
            case ParserState.EscapeDash:
            case ParserState.EscapeDashWidth:
                _state = Ansi.ProcessByte(b, _state);
                break;
            case ParserState.C1Seq:
            case ParserState.C1Data:
            case ParserState.C1Ff1:
            case ParserState.FesData:
            case ParserState.FewPlayerData:
            case ParserState.DreamwordData:
            case ParserState.C95Data:
            case ParserState.C95LogoutLine:
                _state = C1.ProcessByte(b, _state, _c1Lead, _c1Buf);
                break;
            default:
                _state = ParserState.Normal;
                break;
        }

        // A sub-parser may queue one byte for replay (e.g. dreamword terminator).
        if (_pendingReprocess.HasValue)
        {
            var replay = _pendingReprocess.Value;
            _pendingReprocess = null;
            ProcessByte(replay);
        }
    }

    private void ProcessNormal(byte b)
    {
        switch (b)
        {
            case 0x07: // BEL
                EmitBell();
                break;
            case 0xFF: // first byte of FF FF — may be bare C255 pop or telnet IAC
                FlushSpan();
                _state = ParserState.Ff1;
                break;
            case 0x1B: // ESC
                FlushSpan();
                _state = ParserState.Escape;
                break;
            case >= 0x9B and <= 0xFE: // MUD2 C1 lead byte
                FlushSpan();
                _c1Lead = b;
                _c1Buf.Clear();
                _state = ParserState.C1Seq;
                break;
            default:
                EmitChar((char)b);
                break;
        }
    }
}
