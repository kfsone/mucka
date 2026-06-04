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
    /// A room-short description line was received: the player has entered or looked at a room.
    /// Detected by LT_GREEN foreground c on the first span (mirrors Clio telnet.l:1218-1226).
    /// The payload is the plain-text room name.
    /// </summary>
    public event Action<string>? RoomShortReady;

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

    /// <summary>
    /// Server confirmed the terminal width with an ESC-<n>W response, or the parser
    /// detected the server's "[New terminal width is N]" annotation line.
    /// The payload is the confirmed column count.
    /// </summary>
    public event Action<int>? TerminalWidthConfirmed;

    /// <summary>Dreamword has changed. Null means cleared.</summary>
    public event Action<string?>? DreamwordChanged;

    /// <summary>C95 client-mode data block received.</summary>
    public event Action<string>? ClientModeReceived;

    /// <summary>A sound effect should be played. Payload is the app-package-relative asset path, e.g. "sounds/clio.1311.wav".</summary>
    public event Action<string>? SoundRequested;

    /// <summary>A player name decoded from the WHO-list (FEW response) is ready.</summary>
    public event Action<string, AnsiColor>? FewPlayerReady;

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

    /// <summary>A single item line from the FEI inventory response is ready. "========" is the room/carry separator.</summary>
    public event Action<string>? FeiItemReady;

    /// <summary>A FEI-response context has opened. Consumers should clear accumulation buffers.</summary>
    public event Action? FeiListStarting;

    /// <summary>A FEI-response context has closed — all item lines have been delivered.</summary>
    public event Action? FeiListComplete;

    /// <summary>A single exit keyword from the FEX (Front End eXits) response is ready.</summary>
    public event Action<string>? FexItemReady;

    /// <summary>A FEX-response context has opened. Consumers should clear accumulation buffers.</summary>
    public event Action? FexListStarting;

    /// <summary>A FEX-response context has closed — all exit keywords have been delivered.</summary>
    public event Action? FexListComplete;

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

    // qq-to-option-menu detection: the MUD2 server sends NO binary exit signal when the player
    // quits — it just resets colour and prints the option-menu prompt as plain text. Match that
    // prompt char-by-char in the in-game text stream and exit game mode the instant it completes
    // (as Clio does), so the FES heartbeat stops before it misfires into the menu. Reset on each
    // newline so a partial match never carries across lines.
    private const string OptionMenuPrompt = "Option (H for help)";
    private int _optionMatchLen;

    // ── Game state ────────────────────────────────────────────────────────────
    public bool InGameMode => _inGameMode;

    // ── Line-start tracking ───────────────────────────────────────────────────
    // True when no text characters have been output on the current display line yet
    // (i.e. the cursor is at column 0). Mirrors Clio's column-0 rule for room-short
    // detection (telnet.l:1218-1226: bold+GREEN at column 0 = room short description).
    //
    // Set to true:
    //   - On parser construction (start of first line).
    //   - By SetLineStart() — called from ClosePromptContext when the captured prompt
    //     partial line is shown, marking the start of the next game-output frame.
    //   - After each real '\n' line is emitted (end of EmitChar newline path).
    //
    // Set to false:
    //   - When any printable text character is appended (_text.Append or _feiLine.Append).
    //   - By ClearLineStart() — called from C02+C01 dispatch after consuming line-start
    //     to prevent a second consecutive C02+C01 in the same line from double-firing.
    //
    // C1 color sequences do NOT touch _atLineStart (they don't advance display column).
    private bool _atLineStart = true;
    internal bool AtLineStart => _atLineStart;
    internal void SetLineStart() => _atLineStart = true;
    internal void ClearLineStart() => _atLineStart = false;

    // True when C02+C01 arrived at line start on the current line but the line has not
    // yet ended. Cleared (and RoomShortReady fired) when '\n' is processed.
    private bool _pendingRoomShort;
    internal void SetPendingRoomShort() => _pendingRoomShort = true;

    // ── FEW-response suppression ──────────────────────────────────────────────
    // Set when the parser enters a C12+C08+C05 (FE WHO) context block. While active,
    // display output is suppressed but FewPlayerReady events still fire.
    // Cleared when the color stack returns to the depth it was at before the push.
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

    // ── FEW name continuation ─────────────────────────────────────────────────
    // A WHO-list name interrupted by embedded colour codes (e.g. the C90 catch/throw
    // rainbow colouring of a wiz name) hands its partial text here; subsequent printable
    // characters append across the colour sequences and the name completes at '\n'.
    private bool _fewNameActive;
    private readonly StringBuilder _fewName = new();
    private AnsiColor _fewNameColor;

    internal void BeginFewNameContinuation(string partial, AnsiColor color)
    {
        _fewNameActive = true;
        _fewName.Clear();
        _fewName.Append(partial);
        _fewNameColor = color;
    }

    private void FinalizeFewName()
    {
        _fewNameActive = false;
        var name = _fewName.ToString().Trim();
        _fewName.Clear();
        if (name.Length > 0)
            FewPlayerReady?.Invoke(name, _fewNameColor);
    }

    // ── FEI-response capture ──────────────────────────────────────────────────
    // Set when the parser enters a C12+C08+C03 (FE INVENTORY) context block.
    // Item text accumulates in _feiLine (bypasses the span machinery to avoid
    // capturing stale spans from before the opener). Each '\n' emits one item.
    // Cleared when the c stack returns to the depth it was at before the push.
    private bool _inFeiResponseContext;
    private int _feiContextDepth;
    private readonly StringBuilder _feiLine = new();

    internal bool InFeiResponseContext => _inFeiResponseContext;
    internal int FeiContextDepth => _feiContextDepth;
    internal void EnterFeiContext(int targetDepth)
    {
        _inFeiResponseContext = true;
        _feiContextDepth = targetDepth;
        _feiLine.Clear();
    }
    internal void ExitFeiContext() => _inFeiResponseContext = false;
    internal void EmitFeiListStarting() => FeiListStarting?.Invoke();
    internal void EmitFeiListComplete() => FeiListComplete?.Invoke();

    // ── FEX-response capture ──────────────────────────────────────────────────
    // Set when the parser enters a C12+C08+C02 (FE EXITS) context block.
    // Exit keywords accumulate in _fexLine until each '\n'; cleared when the
    // color stack returns to the depth it was at before the push.
    private bool _inFexResponseContext;
    private int _fexContextDepth;
    private readonly StringBuilder _fexLine = new();

    internal bool InFexResponseContext => _inFexResponseContext;
    internal int FexContextDepth => _fexContextDepth;
    internal void EnterFexContext(int targetDepth)
    {
        _inFexResponseContext = true;
        _fexContextDepth = targetDepth;
        _fexLine.Clear();
    }
    internal void ExitFexContext() => _inFexResponseContext = false;
    internal void EmitFexListStarting() => FexListStarting?.Invoke();
    internal void EmitFexListComplete() => FexListComplete?.Invoke();

    // ── Prompt capture ────────────────────────────────────────────────────────
    // Code 01 is "invisibility brackets around the prompt" (mud2_fe4 §codes): the OUTER
    // {C01}{C255} container wraps the ENTIRE prompt, and the inner {C01}{C0n}{C255}
    // variant (01 01 wiz / 01 02 mortal / 01 03 wiz-set / 01 04 snooping) colours the
    // core prompt character(s). "The prompt" is contextual — everything inside the
    // outer container — not just the '*':
    //   visible:    {C01}{C255}{C01}{C02}{C255}*{C255}{C255}
    //   invisible:  {C01}{C255}({C01}{C02}{C255}*{C255}){C255}
    // so the whole container is captured here and shown or discarded atomically.
    // While active, text accumulates in _promptText/_promptSpans — never the display
    // span buffer, and never touching _atLineStart. PopColor closes the context when
    // the colour stack returns to the recorded entry depth.
    private bool _inPromptContext;
    private int _promptContextDepth;
    private readonly List<StyledSpan> _promptSpans = new();
    private readonly StringBuilder _promptText = new();

    internal bool InPromptContext => _inPromptContext;
    internal int PromptContextDepth => _promptContextDepth;

    internal void EnterPromptContext(int targetDepth)
    {
        _inPromptContext = true;
        _promptContextDepth = targetDepth;
        _promptSpans.Clear();
        _promptText.Clear();
    }

    /// <summary>
    /// Close the prompt-capture context (colour stack returned to entry depth).
    /// PromptAllowed=true  → show the captured prompt once as a partial line (mirrors
    ///                       Clio's prompt_allowed gate, telnet.l:438-444).
    /// PromptAllowed=false → discard it entirely (FES-heartbeat end-of-frame marker).
    /// </summary>
    internal void ClosePromptContext()
    {
        _inPromptContext = false;
        FlushPromptText();
        if (PromptAllowed && _promptSpans.Count > 0)
        {
            PromptAllowed = false;
            LineReady?.Invoke(new StyledLine(_promptSpans.ToArray(), isPartial: true));
            SetLineStart();     // prompt shown: next game-output frame starts a new line
        }
        _promptSpans.Clear();
        _promptText.Clear();
    }

    // Flush pending prompt text into _promptSpans with the current style.
    private void FlushPromptText()
    {
        if (_promptText.Length == 0) return;
        _promptSpans.Add(new StyledSpan(_promptText.ToString(), Ansi.CurrentStyle));
        _promptText.Clear();
    }

    // Abandon prompt capture without losing data: spill captured spans/text into the
    // display buffers so a malformed container (e.g. a '\n' arriving before the
    // closing pop — lost C255, line noise) still renders as ordinary text.
    private void AbortPromptContext()
    {
        _inPromptContext = false;
        _spans.AddRange(_promptSpans);
        _promptSpans.Clear();
        if (_promptText.Length > 0)
        {
            _text.Append(_promptText);
            _promptText.Clear();
        }
    }

    /// <summary>
    /// Mirrors Clio's prompt_allowed flag.
    /// Set to true by each real game '\n' (via EmitChar); set to false by
    /// <see cref="ClosePromptContext"/> when it shows the captured prompt. Persists
    /// across TCP packet boundaries — this is what lets the prompt gate distinguish a
    /// real prompt (which follows a game newline, possibly in a previous packet) from
    /// a FES heartbeat (which arrives when no real '\n' has occurred since the last
    /// prompt display).
    /// C98 must NOT set this; doing so would make every FES heartbeat appear as a
    /// real prompt because C98 always precedes the C01 prompt preamble bytes.
    /// </summary>
    internal bool PromptAllowed { get; set; } = true;
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
        Ansi.WidthConfirmed = w => TerminalWidthConfirmed?.Invoke(w);
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

    /// <summary>Set the login username advertised via NEW-ENVIRON USER during telnet negotiation.</summary>
    public void SetLoginUser(string? user) => Telnet.LoginUser = user;

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
        _inPromptContext = false;
        _promptContextDepth = 0;
        _promptSpans.Clear();
        _promptText.Clear();
        _inFewResponseContext = false;
        _fewNameActive = false;
        _fewName.Clear();
        _inFeiResponseContext = false;
        _feiContextDepth = 0;
        _feiLine.Clear();
        _inFexResponseContext = false;
        _fexContextDepth = 0;
        _fexLine.Clear();
        _atLineStart = true;
        _pendingRoomShort = false;
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
            _optionMatchLen = 0;   // the option-menu match never spans a newline
            // A colour-interrupted WHO-list name ends at its line's newline.
            if (_fewNameActive) FinalizeFewName();
            // A newline must never occur inside the prompt container; if one does
            // (lost pop, line noise) abandon the capture and render its text normally.
            if (_inPromptContext) AbortPromptContext();
            if (_inFexResponseContext)
            {
                var itemText = _fexLine.ToString();
                _fexLine.Clear();
                PromptAllowed = true;
                if (itemText.Length > 0) FexItemReady?.Invoke(itemText);
                return;
            }
            if (_inFeiResponseContext)
            {
                // Emit the accumulated FEI item line; reset prompt-state flags as for a real newline.
                var itemText = _feiLine.ToString();
                _feiLine.Clear();
                PromptAllowed = true;
                if (itemText.Length > 0) FeiItemReady?.Invoke(itemText);
                return;
            }
            FlushSpan();
            if (_inFewResponseContext)
            {
                // Discard the line but still tick PromptAllowed so the next prompt frame works.
                _spans.Clear();
                PromptAllowed = true;
                return;
            }
            // Pre-game terminal-width confirmation line: "[New terminal width is N]"
            // Arrives without an ESC-<n>W prefix on plain-mud connections; swallow it and
            // fire TerminalWidthConfirmed so callers can verify the requested width.
            if (!_inGameMode && TryEmitTerminalWidthLine())
                return;
            // In game mode, suppress all-asterisk lines entirely (Clio: prompt_allowed / preamble
            // suppression — telnet.l:438-444). These are MUD2 prompt-preamble separator lines.
            bool isAsteriskPreamble = _inGameMode && SpansAreAllAsterisks();
            var line = new StyledLine(_spans.ToArray(), isPartial: false);
            _spans.Clear();
            PromptAllowed = true;   // Clio: prompt_allowed = 1 on each newline
            if (isAsteriskPreamble) return;
            var stats = LineAnalyzer.Analyze(line, _inGameMode);
            if (stats != null) StatsUpdated?.Invoke(stats);
            if (_inGameMode) { var sf = LineAnalyzer.CheckSoundTrigger(line); if (sf != null) EmitSound(sf); }
            if (_inGameMode)
            {
                // Fire RoomShortReady only when C02+C01 appeared at line start on this line
                // (mirrors Clio telnet.l:1218-1226: bold+GREEN at column 0 = room short desc).
                // Mid-line room-name mentions (e.g. "look around" exits) do not set the flag.
                if (_pendingRoomShort)
                {
                    _pendingRoomShort = false;
                    RoomShortReady?.Invoke(line.PlainText);
                }
                // "Too dark" signals the player has entered a room they cannot see.
                // Treat it as a room transition so the Here list is cleared.
                else if (line.PlainText == "It's too dark to see now!")
                {
                    RoomEntered?.Invoke();
                }
            }
            _atLineStart = true;    // next line starts at column 0
            LineReady?.Invoke(line);
        }
        else if (ch is '\r' or '\0')
        {
            // Suppress CR and NUL. Telnet transmits a bare carriage return as CR NUL
            // (RFC 854), so MUD2's "\r\0\r\n" line endings would otherwise leak the NUL
            // into line text — breaking exact-match consumers (watchword triggers,
            // the too-dark room check) even though the terminal renders it invisibly.
        }
        else
        {
            // Prompt-container text ('(', '*', ')', snoop/rank indicators) accumulates
            // in the prompt buffer — never the display spans, never _atLineStart.
            if (_inPromptContext)
            {
                _promptText.Append(ch);
                return;
            }
            // Continue a colour-interrupted WHO-list name across its colour codes.
            if (_fewNameActive)
                _fewName.Append(ch);
            // Discard characters inside a FEW response (names surface via FewPlayerReady).
            if (_inFewResponseContext) return;
            // FEX and FEI items accumulate in dedicated buffers — bypasses the span machinery.
            if (_inFexResponseContext)
            {
                _fexLine.Append(ch);
                _atLineStart = false;
                return;
            }
            if (_inFeiResponseContext)
            {
                _feiLine.Append(ch);
                _atLineStart = false;
                return;
            }
            _atLineStart = false;
            _text.Append(ch);
            MatchOptionMenu(ch);
        }
    }

    // Advance the streaming match of the option-menu prompt against one in-game text character,
    // firing ExitGameMode the moment the whole prompt has been seen (before the trailing ": ").
    // A plain restart-on-mismatch suffices — the prompt has no self-overlap.
    private void MatchOptionMenu(char ch)
    {
        if (!_inGameMode) { _optionMatchLen = 0; return; }
        if (ch == OptionMenuPrompt[_optionMatchLen])
        {
            if (++_optionMatchLen == OptionMenuPrompt.Length)
            {
                _optionMatchLen = 0;
                ExitGameMode();
            }
        }
        else
        {
            _optionMatchLen = ch == OptionMenuPrompt[0] ? 1 : 0;
        }
    }

    internal void FlushSpan()
    {
        // Inside the prompt container, style boundaries flush into the prompt buffer.
        if (_inPromptContext) { FlushPromptText(); return; }
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
        _optionMatchLen = 0;
        // Discard any half-captured prompt — it belongs to the game session just ended.
        _inPromptContext = false;
        _promptSpans.Clear();
        _promptText.Clear();
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
    internal void EmitFewPlayer(string name, AnsiColor color) => FewPlayerReady?.Invoke(name, color);
    internal void EmitRoomEntered() => RoomEntered?.Invoke();    internal void SetAccountInfo(string? accountId, int privs)
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
    /// Checks whether the accumulated spans form the server's pre-game width-confirmation
    /// annotation "[New terminal width is N]". If so, fires <see cref="TerminalWidthConfirmed"/>,
    /// resets line-boundary state, and returns true (caller should skip LineReady).
    /// </summary>
    private bool TryEmitTerminalWidthLine()
    {
        const string prefix = "[New terminal width is ";
        // Build plain text only if the line could plausibly be the width notification.
        // _spans has already been flushed by the caller (FlushSpan was called).
        if (_spans.Count == 0) return false;

        // Build plain text without LINQ to keep it allocation-lean.
        int totalLen = 0;
        foreach (var s in _spans) totalLen += s.Text.Length;
        if (totalLen <= prefix.Length + 1) return false;

        // Quick prefix check using the first span before building the full string.
        var first = _spans[0].Text;
        if (!first.StartsWith(prefix[..Math.Min(first.Length, prefix.Length)], StringComparison.Ordinal)
            && _spans.Count > 1)
            return false;

        var sb = new System.Text.StringBuilder(totalLen);
        foreach (var s in _spans) sb.Append(s.Text);
        var text = sb.ToString();

        if (!text.StartsWith(prefix, StringComparison.Ordinal) || text[^1] != ']')
            return false;

        var numSpan = text.AsSpan(prefix.Length, text.Length - prefix.Length - 1);
        if (!int.TryParse(numSpan, out int w))
            return false;

        TerminalWidthConfirmed?.Invoke(w);
        _spans.Clear();
        PromptAllowed = true;
        _atLineStart = true;
        return true;
    }

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
                    // Bare FF FF: C255 rule — pop color stack (cf Clio telnet.l:1040)
                    C1.PopColor();
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
            case ParserState.EscapeDashAnnotation:
                // Swallow the server's "[New terminal width is N]\r\n" annotation.
                // The ESC-<n>W already fired TerminalWidthConfirmed; just discard text until \n.
                if (b == '\n') _state = ParserState.Normal;
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
