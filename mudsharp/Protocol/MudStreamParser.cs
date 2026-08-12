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

    /// <summary>
    /// The server sent the C08+C13 ("Not updating persona.") signal: permadeath has wiped the
    /// current persona and no probe will bring its stats back. Fires alongside <see cref="StatsUpdated"/>
    /// (which carries the zeroed snapshot) so consumers have an unambiguous protocol-level signal
    /// instead of pattern-matching the rendered line text, which any player chat could also contain.
    /// </summary>
    public event Action? PersonaWiped;

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

    /// <summary>
    /// A C1 code hinted that parts of the player state may have changed (combat hits,
    /// spells, items/creatures arriving, etc.). The payload says which categories.
    /// Policy (debounce, probe scheduling) is the consumer's responsibility — the
    /// parser never sends probes itself.
    /// </summary>
    public event Action<StaleStats>? ProbeHintReceived;

    /// <summary>The server announced an auto-reset (C1 code C06 C04, "Auto reset initiated, you have
    /// 120 seconds…"). The reset is imminent and precisely timed from this instant. Fires on the Feed
    /// thread.</summary>
    public event Action? AutoResetInitiated;

    /// <summary>
    /// A player name bracketed by a C05 presence code (here/arriving/departing/
    /// visible/invisible/fleeing) was seen outside a FEW response. The named player
    /// is demonstrably online; consumers can verify it against their cached who list
    /// and refresh when it is missing.
    /// </summary>
    public event Action<string>? PresenceNameSeen;

    /// <summary>
    /// A temporary magical effect (STR/DEX/STA buff or debuff, or glow) started or ended on
    /// the local player. Derived from the C11 spell-start/end protocol family. Consumers keep
    /// the running per-stat stack and drive the status-icon overlay.
    /// </summary>
    public event Action<StatusEffectChange>? StatusEffectChanged;

    /// <summary>A single exit keyword from the FEX (Front End eXits) response is ready.</summary>
    public event Action<string>? FexItemReady;

    /// <summary>A FEX-response context has opened. Consumers should clear accumulation buffers.</summary>
    public event Action? FexListStarting;

    /// <summary>A FEX-response context has closed — all exit keywords have been delivered.</summary>
    public event Action? FexListComplete;

    /// <summary>
    /// A long-description line was received while in the C02.02 (GREEN) context in game mode.
    /// Fires alongside <see cref="LineReady"/> for each line of the room's long description.
    /// Multi-line descriptions produce one event per line.
    /// </summary>
    public event Action<string>? LongDescLineReady;

    /// <summary>
    /// An exits-format line was received in game mode: "direction: Destination." pattern
    /// with the destination name in a BrightGreen span (C02.01 context).
    /// Fires alongside <see cref="LineReady"/>. Payload is the raw direction word (e.g. "north")
    /// and the destination name (e.g. "Foothills"). Consumer is responsible for direction
    /// normalization (e.g. "north" to "n").
    /// Not fired inside FEX/FEI/FEW response contexts.
    /// </summary>
    public event Action<string, string>? ExitLineReady;

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

    // Semantic kind for the line currently being accumulated. A C1 code decoder (e.g. C09
    // speaker messages in Mud2C1Decoder) calls SetPendingKind when it recognises the line's
    // class; it is stamped onto the StyledLine at newline and reset. Speaker messages are
    // whole lines, so per-line reset is correct; a rare multi-line message only tags line 1 (TODO).
    private LineKind _pendingKind = LineKind.Normal;
    // Set by C09+C03 (tell) when the line starts, then consumed at newline to pick the
    // tell alert variant from the finished text.
    private bool _pendingTellSound;
    // Sound codes decoded on the line currently being accumulated (C06/C07/C08/C11/C13/C14/C18
    // payloads). Emission is deferred to the line's finalisation because the code arrives BEFORE
    // the text, and a self action echo ("OK, you wave." / "OK, Ollie the superheroine waves.")
    // must drop its own act sound — the prefix isn't knowable at decode time. Every other line
    // path (including partial/prompt flushes) plays the queue, so audible timing is unchanged:
    // the code and its line share a packet.
    private readonly List<string> _pendingLineSounds = new();

    /// <summary>Classify the line currently being accumulated. Consumed and reset at the next newline.</summary>
    internal void SetPendingKind(LineKind kind) => _pendingKind = kind;
    /// <summary>Marks the current line as a tell for newline-time tell sound selection.</summary>
    internal void ArmPendingTellSound() => _pendingTellSound = true;
    /// <summary>Queues a C1-decoded sound for emission when the current line finalises.</summary>
    internal void QueueLineSound(string assetPath) => _pendingLineSounds.Add(assetPath);

    // Plays (or, for a self action echo, drops) the sounds queued on the line just finished.
    private void FlushPendingLineSounds(bool suppress = false)
    {
        if (_pendingLineSounds.Count == 0) return;
        if (!suppress)
            foreach (var s in _pendingLineSounds)
                EmitSound(s);
        _pendingLineSounds.Clear();
    }

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

    // ── C1 stream scopes ──────────────────────────────────────────────────────
    // The decoder's colour stack is the single source of truth for the semantic scopes the
    // server brackets in colour pushes (see C1Scope): these properties read it live, and
    // OnC1ScopesClosed below runs the end-of-scope actions when frames unwind. Only capture
    // BUFFERS live here — never a parallel bool/depth pair.

    // Long-description scope (C02.02): while open, each completed line fires
    // LongDescLineReady alongside the normal LineReady.
    internal bool InLongDescContext => C1.HasScope(C1Scope.LongDesc);

    // Chat scope (C09 speaker message): keeps LineKind.Chat on every line of a speaker message
    // the server wrapped across several '\n' lines, not just the one carrying the C09 code.
    // Speaker messages pop their colour before their own newline, so single-line messages rely
    // on _pendingKind; the scope only carries wrapped continuation lines, whose colour is still
    // pushed at the wrap point.
    internal bool InChatContext => C1.HasScope(C1Scope.Chat);
    // Printable non-whitespace text was emitted on the current line while NO colour frame was
    // open — i.e. plain, un-coded game output (command responses like "You drop the sword.").
    // Consumed at the newline: such a line hints Inventory (probe-noise policy, 2026-07-25 —
    // item-moving commands print plain text with no C1 code, so nothing else refreshes FEI).
    private bool _plainTextOnLine;
    // Snapshot of InChatContext taken at each newline — i.e. whether the C09 scope was already
    // open when the NEXT line starts. A line finalised with this true is a server-wrapped
    // continuation of the previous chat line (StyledLine.ContinuesChat); a line that carries its
    // own C09 opens the scope mid-line, after this snapshot, so message starts are never tagged.
    private bool _chatOpenAtLineStart;
    // True when any of the current line's DISPLAYED text was emitted while the chat scope was
    // open. This — not the scope state at the '\n' — is what makes the line part of the message:
    // the message's LAST line carries the closing pop before its own newline (same as
    // single-line messages), so by finalisation the scope is already closed and testing it there
    // dropped exactly the final wrapped row out of Chat (and out of the self recolour).
    private bool _chatTextOnLine;

    // ── FEW-response suppression ──────────────────────────────────────────────
    // The C12+C08+C05 (FE WHO) scope. While open, display output is suppressed but
    // FewPlayerReady events still fire.
    internal bool InFewResponseContext => C1.HasScope(C1Scope.FewResponse);
    internal void BeginFewResponse() => FewListStarting?.Invoke();

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

    internal void FinalizeFewNameOnContextClose()
    {
        if (_fewNameActive)
            FinalizeFewName();
    }

    // ── FEI-response capture ──────────────────────────────────────────────────
    // The C12+C08+C03 (FE INVENTORY) scope. Item text accumulates in _feiLine (bypasses the
    // span machinery to avoid capturing stale spans from before the opener); each '\n' emits
    // one item, and scope close flushes any trailing item.
    private readonly StringBuilder _feiLine = new();

    internal bool InFeiResponseContext => C1.HasScope(C1Scope.FeiResponse);
    internal void BeginFeiResponse()
    {
        _feiLine.Clear();
        FeiListStarting?.Invoke();
    }
    private void FlushFeiItem()
    {
        if (_feiLine.Length == 0) return;
        var itemText = _feiLine.ToString();
        _feiLine.Clear();
        FeiItemReady?.Invoke(itemText);
    }

    // ── FEX-response capture ──────────────────────────────────────────────────
    // The C12+C08+C02 (FE EXITS) scope. Exit keywords accumulate in _fexLine until each '\n';
    // scope close flushes any trailing keyword.
    private readonly StringBuilder _fexLine = new();

    internal bool InFexResponseContext => C1.HasScope(C1Scope.FexResponse);
    internal void BeginFexResponse()
    {
        _fexLine.Clear();
        FexListStarting?.Invoke();
    }
    private void FlushFexItem()
    {
        if (_fexLine.Length == 0) return;
        var itemText = _fexLine.ToString();
        _fexLine.Clear();
        FexItemReady?.Invoke(itemText);
    }

    /// <summary>
    /// End-of-scope actions, invoked by the decoder when colour-stack frames that opened
    /// semantic scopes unwind (a bare FF FF pop, a C90 colour throw, or the C00 init reset).
    /// Several scopes can end on one unwind; the dispatch order below matches the old
    /// per-context close order. Chat and LongDesc need no end action — their consumers read
    /// the In-properties live.
    /// </summary>
    internal void OnC1ScopesClosed(C1Scope closed)
    {
        if ((closed & C1Scope.FewResponse) != 0)
        {
            FinalizeFewNameOnContextClose();
            FewListComplete?.Invoke();
        }
        if ((closed & C1Scope.FeiResponse) != 0)
        {
            FlushFeiItem();
            FeiListComplete?.Invoke();
        }
        if ((closed & C1Scope.FexResponse) != 0)
        {
            FlushFexItem();
            FexListComplete?.Invoke();
        }
        // The prompt container: show the whole captured prompt — '*', '(*)' when invisible,
        // snoop/rank indicators — as a partial line (PromptAllowed) or discard it (FES
        // heartbeat). Skipped when a mid-container newline already aborted the capture.
        if ((closed & C1Scope.Prompt) != 0 && _inPromptContext)
            ClosePromptContext();
    }

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
    // span buffer, and never touching _atLineStart. The container's C1Scope.Prompt frame
    // popping closes the capture (OnC1ScopesClosed). _inPromptContext is the CAPTURE state,
    // not the scope: a mid-container newline aborts the capture while the scope frame is
    // still on the stack, and its eventual pop must then do nothing.
    private bool _inPromptContext;
    private readonly List<StyledSpan> _promptSpans = new();
    private readonly StringBuilder _promptText = new();

    internal bool InPromptContext => _inPromptContext;

    internal void EnterPromptContext()
    {
        _inPromptContext = true;
        _promptSpans.Clear();
        _promptText.Clear();
    }

    /// <summary>
    /// Close the prompt-capture context (the container's C1Scope.Prompt frame unwound).
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

        // Reset sub-parsers before clearing _state.
        Ansi.Reset();
        Telnet.Reset();
        C1.Reset();

        _state = ParserState.Normal;
        _iacSbBuf.Clear();
        _c1Buf.Clear();
        _spans.Clear();
        _text.Clear();
        _pendingReprocess = null;
        PromptAllowed = true;
        _inPromptContext = false;
        _promptSpans.Clear();
        _promptText.Clear();
        _fewNameActive = false;
        _fewName.Clear();
        _feiLine.Clear();
        _fexLine.Clear();
        _atLineStart = true;
        _pendingRoomShort = false;
        _chatOpenAtLineStart = false;
        _chatTextOnLine = false;
        _plainTextOnLine = false;
        _pendingKind = LineKind.Normal;
        _pendingTellSound = false;
        _pendingLineSounds.Clear();
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
            // Whether the C09 chat scope was open when THIS line started (set at the previous
            // newline), consumed for the ContinuesChat tag below; then re-snapshot for the next
            // line. Taken up front so every return path below leaves the snapshot correct.
            bool chatOpenAtLineStart = _chatOpenAtLineStart;
            _chatOpenAtLineStart = InChatContext;
            _optionMatchLen = 0;   // the option-menu match never spans a newline
            // A colour-interrupted WHO-list name ends at its line's newline.
            if (_fewNameActive) FinalizeFewName();
            // A newline must never occur inside the prompt container; if one does
            // (lost pop, line noise) abandon the capture and render its text normally.
            if (_inPromptContext) AbortPromptContext();
            // Newlines inside the FEX/FEI/FEW probe contexts produce no visible output,
            // so they must NOT set PromptAllowed: on narrow terminals the server line-wraps
            // even these escaped responses, and ticking the flag here made the NEXT
            // heartbeat's prompt display as a stray '*'. PromptAllowed means "visible
            // output occurred since the last displayed prompt" — only real lines set it.
            // These probe-context newlines emit no normal line, so clear any pending chat tag here
            // too — otherwise a C09 seen just before an FE-probe response could stamp the next real
            // line as Chat. (Implausible in practice, but the reset belongs on every newline path.)
            if (InFexResponseContext)
            {
                var itemText = _fexLine.ToString();
                _fexLine.Clear();
                _pendingKind = LineKind.Normal;
                _chatTextOnLine = false;
                _plainTextOnLine = false;
                _pendingTellSound = false;
                FlushPendingLineSounds();
                if (itemText.Length > 0) FexItemReady?.Invoke(itemText);
                return;
            }
            if (InFeiResponseContext)
            {
                var itemText = _feiLine.ToString();
                _feiLine.Clear();
                _pendingKind = LineKind.Normal;
                _chatTextOnLine = false;
                _plainTextOnLine = false;
                _pendingTellSound = false;
                FlushPendingLineSounds();
                if (itemText.Length > 0) FeiItemReady?.Invoke(itemText);
                return;
            }
            FlushSpan();
            if (InFewResponseContext)
            {
                _spans.Clear();
                _pendingKind = LineKind.Normal;
                _chatTextOnLine = false;
                _plainTextOnLine = false;
                _pendingTellSound = false;
                FlushPendingLineSounds();
                return;
            }
            // Pre-game terminal-width confirmation line: "[New terminal width is N]"
            // Arrives without an ESC-<n>W prefix on plain-mud connections; swallow it and
            // fire TerminalWidthConfirmed so callers can verify the requested width.
            if (!_inGameMode && TryEmitTerminalWidthLine())
            {
                FlushPendingLineSounds();
                return;
            }
            // In game mode, suppress all-asterisk lines entirely (Clio: prompt_allowed / preamble
            // suppression — telnet.l:438-444). These are MUD2 prompt-preamble separator lines.
            bool isAsteriskPreamble = _inGameMode && SpansAreAllAsterisks();
            // Chat if a C09 code was seen on this line (_pendingKind), OR any of the line's text
            // was emitted inside the C09 scope (_chatTextOnLine — covers the message's LAST
            // wrapped row, whose closing pop precedes its newline), OR the scope is still open at
            // the newline (a blank wrapped row). See C1Scope.Chat.
            var lineKind = (_pendingKind == LineKind.Chat || _chatTextOnLine || InChatContext)
                ? LineKind.Chat : LineKind.Normal;
            var line = new StyledLine(_spans.ToArray(), isPartial: false, kind: lineKind,
                continuesChat: lineKind == LineKind.Chat && chatOpenAtLineStart);
            var tellAlertRequested = _pendingTellSound;
            var plainTextLine = _plainTextOnLine;
            _spans.Clear();
            _pendingKind = LineKind.Normal;   // per-line signals; InChatContext carries wrapped continuation lines
            _chatTextOnLine = false;
            _plainTextOnLine = false;
            _pendingTellSound = false;
            PromptAllowed = true;   // Clio: prompt_allowed = 1 on each newline
            // A self action echo plays no sound: the act's sound code announces the action to
            // the ROOM, and hearing your own wave/yodel back is noise. All other lines play
            // their queued sounds now.
            FlushPendingLineSounds(suppress: _inGameMode && SelfChatColorizer.IsOkActEcho(line.PlainText));
            if (isAsteriskPreamble) return;
            // Your own "send" to your listeners echoes as: You tell your listeners "...". It rides
            // the tell channel (C09+C03) but is your own output — suppress the tell alert and
            // italicise only the "your listeners" phrase, rather than the sender/"tells you"
            // decoration meant for tells directed AT you.
            bool ownListenersSend = _inGameMode && tellAlertRequested
                                    && IsOwnListenersSend(line.PlainText);
            if (_inGameMode && tellAlertRequested)
                line = ownListenersSend ? ItalicisePhrase(line, ListenersPhrase) : DecorateTellLine(line);
            var stats = LineAnalyzer.Analyze(line, _inGameMode);
            if (stats != null) StatsUpdated?.Invoke(stats);
            if (_inGameMode) { var sf = LineAnalyzer.CheckSoundTrigger(line); if (sf != null) EmitSound(sf); }
            if (_inGameMode && tellAlertRequested && !ownListenersSend)
                EmitSound(ChooseTellAlertSound(line.PlainText));
            if (_inGameMode)
            {
                // Plain un-coded output can be an item-moving command's response ("You drop the
                // sword." carries no C1 code) — mark the FEI panels dirty so the debounced
                // reactive probe / next beat refreshes them (probe-noise policy, 2026-07-25).
                if (plainTextLine)
                    EmitProbeHint(StaleStats.Inventory);
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
                // Prefix match: the server ends this line with "!" in some contexts and
                // "." in others (observed live: moving up into an unlit loft drew
                // "It's too dark to see now." -- period -- with an empty FE EXITS block).
                else if (line.PlainText.StartsWith("It's too dark to see now", StringComparison.Ordinal))
                {
                    RoomEntered?.Invoke();
                }
                // Long description: fire alongside LineReady for each line while C02.02 is active.
                if (InLongDescContext)
                    LongDescLineReady?.Invoke(line.PlainText);
                // Exits: fire for "direction: Destination." lines (not within long-desc context
                // to avoid false matches if a description happens to contain a colon).
                else
                    TryEmitExitLine(line);
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
            if (InFewResponseContext) return;
            // FEX and FEI items accumulate in dedicated buffers — bypasses the span machinery.
            // They are invisible (surfaced via events), so like FEW they must not touch
            // _atLineStart: clearing it here suppressed RoomEntered/RoomShortReady for any
            // room short arriving right after a heartbeat's FEI block or an auto-FEX block.
            if (InFexResponseContext)
            {
                _fexLine.Append(ch);
                return;
            }
            if (InFeiResponseContext)
            {
                _feiLine.Append(ch);
                return;
            }
            bool wasLineStart = _atLineStart;
            _atLineStart = false;
            if (InChatContext) _chatTextOnLine = true;
            // Plain (un-coded) game output: printable text arriving with no colour frame open.
            // Any such line can be the response to an item-moving command ("You drop the
            // sword.") — the server prints these with no C1 code at all, so no decoder Hint
            // covers them. Consumed at the newline as an Inventory probe hint.
            else if (_inGameMode && !char.IsWhiteSpace(ch) && !C1.HasOpenColourFrame)
                _plainTextOnLine = true;
            _text.Append(ch);
            MatchOptionMenu(ch, wasLineStart);
        }
    }

    // Advance the streaming match of the option-menu prompt against one in-game text character,
    // firing ExitGameMode the moment the whole prompt has been seen (before the trailing ": ").
    // A plain restart-on-mismatch suffices — the prompt has no self-overlap.
    // A new match only arms at column 0: the real menu prompt always starts its line, and
    // matching mid-line let any player's speech ('say Option (H for help)') kick the client
    // out of game mode (stopping the heartbeat and sending a stray 'auto fex' on re-entry).
    private void MatchOptionMenu(char ch, bool atLineStart)
    {
        if (!_inGameMode) { _optionMatchLen = 0; return; }
        if (_optionMatchLen > 0 && ch == OptionMenuPrompt[_optionMatchLen])
        {
            if (++_optionMatchLen == OptionMenuPrompt.Length)
            {
                _optionMatchLen = 0;
                ExitGameMode();
            }
            return;
        }
        _optionMatchLen = atLineStart && ch == OptionMenuPrompt[0] ? 1 : 0;
    }

    // Direction words as spelled out by the MUD2 exits verb (full names and a few aliases).
    private static readonly HashSet<string> ExitKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "north", "northeast", "east", "southeast", "south", "southwest", "west", "northwest",
        "up", "down", "in", "out", "swampward", "over",
        // abbreviated forms appear in some contexts (e.g. look around output)
        "n", "ne", "e", "se", "s", "sw", "w", "nw",
    };

    /// <summary>
    /// Fires <see cref="ExitLineReady"/> when <paramref name="line"/> matches the exits-verb
    /// format: a direction keyword followed by ": " and a BrightGreen (C02.01) destination span.
    /// Called only in game mode and only outside of the C02.02 long-description context.
    /// </summary>
    private void TryEmitExitLine(StyledLine line)
    {
        var plain = line.PlainText;
        var colon = plain.IndexOf(':');
        if (colon < 1 || colon > 12) return;
        var dir = plain[..colon].Trim();
        if (!ExitKeywords.Contains(dir)) return;
        // Require at least one BrightGreen span to confirm this is an exits-verb line —
        // destination room names are always in C02.01 (LT_GREEN/BrightGreen). Guard
        // against spurious matches on other "word: text." lines with no coloured spans.
        bool hasRoomNameSpan = false;
        foreach (var span in line.Spans)
            if (span.Style.Foreground == AnsiColor.BrightGreen) { hasRoomNameSpan = true; break; }
        if (!hasRoomNameSpan) return;
        // Extract destination from plain text: everything after ": " up to the trailing ".".
        var afterColon = colon + 1;
        if (afterColon >= plain.Length) return;
        var dest = plain[afterColon..].TrimStart().TrimEnd('.');
        if (dest.Length == 0) return;
        ExitLineReady?.Invoke(dir, dest.Trim());
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
        // Sounds queued on a line that ends at a prompt (no '\n' yet) still play — only a
        // completed "OK, …" echo suppresses, and those always newline-terminate.
        FlushPendingLineSounds();
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
        // Drop all stream-scope state — it's only valid within a game session. Leaving a
        // scope open on relog causes text suppression (FEW) or spurious item/exit events;
        // C1.ResetGameState() clears the colour stack (and with it every open scope) silently,
        // without firing end-of-scope actions.
        _fewNameActive = false;
        _fewName.Clear();
        _feiLine.Clear();
        _fexLine.Clear();
        _pendingRoomShort = false;
        _chatOpenAtLineStart = false;
        _chatTextOnLine = false;
        _plainTextOnLine = false;
        _pendingKind = LineKind.Normal;
        _pendingTellSound = false;
        _pendingLineSounds.Clear();   // dropped, not played — the session they belong to is over
        C1.ResetGameState();
        GameModeExited?.Invoke();
    }

    // Your own broadcast to your listeners (the "send" command) echoes on the tell channel as
    // You tell your listeners "...". Detected by lead so we can mute its alert and italicise
    // just the "your listeners" phrase.
    private const string OwnListenersLead = "You tell your listeners";
    private const string ListenersPhrase  = "your listeners";

    private static bool IsOwnListenersSend(string lineText)
        => lineText.StartsWith(OwnListenersLead, StringComparison.Ordinal);

    // Italicise every occurrence-spanning run of <phrase> within the line, splitting spans on the
    // phrase boundaries and leaving all other styling (and any click-insert metadata) intact.
    private static StyledLine ItalicisePhrase(StyledLine line, string phrase)
    {
        var text = line.PlainText;
        int idx = text.IndexOf(phrase, StringComparison.Ordinal);
        if (idx < 0) return line;
        int phraseStart = idx;
        int phraseEnd = idx + phrase.Length;

        var rewritten = new List<StyledSpan>(line.Spans.Count + 2);
        int absolute = 0;
        foreach (var span in line.Spans)
        {
            int spanStart = absolute;
            int spanEnd = spanStart + span.Text.Length;
            absolute = spanEnd;

            var cuts = new List<int> { 0, span.Text.Length };
            AddCut(cuts, phraseStart, phraseEnd, spanStart, spanEnd);
            cuts.Sort();

            for (int i = 1; i < cuts.Count; i++)
            {
                int localA = cuts[i - 1];
                int localB = cuts[i];
                if (localB <= localA) continue;
                int absA = spanStart + localA;
                int absB = spanStart + localB;
                var piece = span.Text.Substring(localA, localB - localA);

                var style = span.Style;
                if (absA >= phraseStart && absB <= phraseEnd) style = style with { Italic = true };
                rewritten.Add(new StyledSpan(piece, style, span.ClickInsertText));
            }
        }

        return new StyledLine(rewritten, line.IsPartial, line.Kind, line.ContinuesChat);
    }

    private static string ChooseTellAlertSound(string lineText)
    {
        if (StartsWithTellLead(lineText, "Someone powerful tells you"))
            return "sounds/tell-wiz.wav";
        if (StartsWithTellLead(lineText, "Someone tells you"))
            return "sounds/tell-invis.wav";
        return "sounds/tell.wav";
    }

    private static StyledLine DecorateTellLine(StyledLine line)
    {
        const string tellsYou = "tells you";
        var text = line.PlainText;
        if (text.Length == 0) return line;

        // Look for " <tells you>" so we can style only the phrase, not the leading spacer.
        var marker = text.IndexOf(" " + tellsYou, StringComparison.OrdinalIgnoreCase);
        if (marker <= 0) return line;
        int phraseStart = marker + 1;
        int phraseEnd = phraseStart + tellsYou.Length;

        int firstSpace = text.IndexOfAny([' ', '\t']);
        if (firstSpace < 1 || firstSpace > marker)
            firstSpace = marker;

        var senderToken = text[..firstSpace];
        var hasNamedSender = !senderToken.Equals("Someone", StringComparison.OrdinalIgnoreCase);
        var clickInsert = hasNamedSender ? senderToken + " " : null;

        var rewritten = new List<StyledSpan>(line.Spans.Count + 4);
        int absolute = 0;
        foreach (var span in line.Spans)
        {
            int spanStart = absolute;
            int spanEnd = spanStart + span.Text.Length;
            absolute = spanEnd;

            var cuts = new List<int> { 0, span.Text.Length };
            AddCut(cuts, 0, firstSpace, spanStart, spanEnd);
            AddCut(cuts, phraseStart, phraseEnd, spanStart, spanEnd);
            cuts.Sort();

            for (int i = 1; i < cuts.Count; i++)
            {
                int localA = cuts[i - 1];
                int localB = cuts[i];
                if (localB <= localA) continue;
                int absA = spanStart + localA;
                int absB = spanStart + localB;
                var piece = span.Text.Substring(localA, localB - localA);

                bool inSender = hasNamedSender && absA >= 0 && absB <= firstSpace;
                bool inPhrase = absA >= phraseStart && absB <= phraseEnd;

                var style = span.Style;
                if (inSender) style = style with { Underline = true };
                if (inPhrase) style = style with { Italic = true };

                rewritten.Add(new StyledSpan(piece, style, inSender ? clickInsert : null));
            }
        }

        return new StyledLine(rewritten, line.IsPartial, line.Kind, line.ContinuesChat);
    }

    private static void AddCut(List<int> cuts, int rangeStart, int rangeEnd, int spanStart, int spanEnd)
    {
        if (rangeStart > spanStart && rangeStart < spanEnd)
            cuts.Add(rangeStart - spanStart);
        if (rangeEnd > spanStart && rangeEnd < spanEnd)
            cuts.Add(rangeEnd - spanStart);
    }

    private static bool StartsWithTellLead(string lineText, string lead)
    {
        if (!lineText.StartsWith(lead, StringComparison.OrdinalIgnoreCase))
            return false;
        if (lineText.Length == lead.Length)
            return true;

        // Require a delimiter after the exact lead phrase so only the two canonical
        // anonymous forms are treated specially.
        return char.IsWhiteSpace(lineText[lead.Length]) || lineText[lead.Length] is '"' or ',' or ':';
    }

    /// <summary>Clear accumulated spans (used after emitting the prompt partial line).</summary>
    internal void ClearSpans() => _spans.Clear();

    internal void EmitStatsUpdate(GameStatsSnapshot stats) => StatsUpdated?.Invoke(stats);
    internal void EmitPersonaWiped() => PersonaWiped?.Invoke();
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
    internal void EmitProbeHint(StaleStats kinds) => ProbeHintReceived?.Invoke(kinds);
    internal void EmitAutoResetInitiated() => AutoResetInitiated?.Invoke();
    internal void EmitPresenceName(string name) => PresenceNameSeen?.Invoke(name);
    internal void EmitStatusEffect(StatusEffectChange change) => StatusEffectChanged?.Invoke(change);
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
                // If a high-bit control byte arrives instead, the annotation was interrupted;
                // hand that byte back to the main parser so we do not swallow binary traffic.
                if (b >= 0x80)
                {
                    _pendingReprocess = b;
                    _state = ParserState.Normal;
                    break;
                }
                if (b == '\n') _state = ParserState.Normal;
                break;
            case ParserState.C1Seq:
            case ParserState.C1Data:
            case ParserState.C1Ff1:
            case ParserState.FesData:
            case ParserState.FesLineTail:
            case ParserState.FewPlayerData:
            case ParserState.PresenceNameData:
            case ParserState.StatusPhraseData:
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
