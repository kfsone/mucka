using MudSharp.Models;
using System.Diagnostics;
using System.Text;

namespace MudSharp.Protocol;

/// <summary>
/// Decoder for the MUD2 proprietary C1 binary protocol (lead bytes 0x9B–0xFE).
/// All sequences end with the C255 terminator (0xFF 0xFF).
///
/// Color indexes mapping (internal, not ansi)
///   BLACK=0  RED=1  GREEN=2  YELLOW=3  BLUE=4  MAGENTA=5  CYAN=6  WHITE=7
///   LT_BLACK=8  LT_RED=9  LT_GREEN=10  LT_YELLOW=11  LT_BLUE=12  LT_MAGENTA=13  LT_CYAN=14  LT_WHITE=15
/// </summary>
internal sealed class Mud2C1Decoder
{
    private readonly MudStreamParser _parser;

    // Color stack: tracks color history for bare FF FF pop()
    private readonly Stack<TextStyle> _colorStack = new();

    // C90 colour-catch depths: {C90}{C255} snapshots the stack depth here;
    // {C90}{C01}{C255} (colour throw) unwinds the stack back to the snapshot.
    private readonly Stack<int> _catchDepths = new();

    // C95 line counter (how many newline-terminated lines remain to collect)
    private int _c95LinesRemaining;

    // After the C95-logout line's '\n', absorb the trailing 0xFF 0xFF color-terminator
    // that the server appends before returning to Normal.
    private bool _c95LogoutSeenNewline;

    internal Mud2C1Decoder(MudStreamParser parser) { _parser = parser; }

    // ── Color index constants ──────────────────────────────────────────────
    private const int BLACK = 0, RED = 1, GREEN = 2, YELLOW = 3;
    private const int BLUE = 4, MAGENTA = 5, CYAN = 6, WHITE = 7;
    private const int LT_BLACK = 8, LT_RED = 9, LT_GREEN = 10, LT_YELLOW = 11;
    private const int LT_BLUE = 12, LT_MAGENTA = 13, LT_CYAN = 14, LT_WHITE = 15;

    // ── Helpers ───────────────────────────────────────────────────────────────

    // FES subscription bytes: ESC - [ F E S ESC - ]
    private static readonly byte[] FesSubscription = { 0x1B, 0x2D, 0x5B, 0x46, 0x45, 0x53, 0x1B, 0x2D, 0x5D };

    private static AnsiColor Clr(int ourCode)
        => (AnsiColor)Math.Clamp(ourCode, 0, 15);

    private static TextStyle Style(int fg, int bg = BLACK)
        => new(Clr(fg), Clr(bg));

    private void Apply(int fg, int bg = BLACK)
    {
        var style = Style(fg, bg);
        _colorStack.Push(style);
        _parser.Ansi.SetStyle(style);
    }

    private void Apply(TextStyle s)
    {
        _colorStack.Push(s);
        _parser.Ansi.SetStyle(s);
    }

    // Clamp a raw C99 color byte to a color index: byte - 0x9B (C00 base)
    private static int C99Color(byte b) => Math.Clamp(b - 0x9B, 0, 15);

    // ── Sound helpers (Clio sound.c formula) ─────────────────────────────────

    private static string SoundFile(int n1, int n2 = 255, int n3 = 255)
    {
        if (n3 == 255)
            return n2 == 255
                ? $"sounds/clio.{n1:D2}.wav"
                : $"sounds/clio.{n1:D2}{n2:D2}.wav";
        return $"sounds/clio.{n1:D2}{n2:D2}{n3:D2}.wav";
    }

    private void Sound(int n1, int n2 = 255, int n3 = 255)
        => _parser.EmitSound(SoundFile(n1, n2, n3));

    // ── Public entry point ────────────────────────────────────────────────────

    /// <summary>
    /// Pop the color stack (Clio pop() on bare FF FF).
    /// Restores the style that was active before the last push, if any.
    /// Also handles the two prompt-preamble flags set by the C01 game-mode dispatch.
    /// </summary>
    internal void PopColor()
    {
        _parser.FlushSpan();
        if (_colorStack.Count > 0)
            _colorStack.Pop();
        if (_colorStack.Count > 0)
            _parser.Ansi.SetStyle(_colorStack.Peek());
        // If stack is empty after pop, silently ignore (Clio: if (pop() == -1) { /* commented out */ })

        CheckContextClosures();

        // Restore style to default when the C1 color stack is fully unwound.
        // Clio ignores this case (pop() == -1, commented out), but we must explicitly reset
        // Ansi.CurrentStyle or subsequent text inherits the last C1 color (e.g. BLUE from
        // the wire prompt preamble, making command echoes appear in prompt color).
        if (_colorStack.Count == 0)
            _parser.Ansi.SetStyle(TextStyle.Default);
    }

    /// <summary>
    /// Close any capture context whose entry depth has been reached by a stack unwind
    /// (bare FF FF pop, or a C90 colour throw that pops multiple entries at once).
    /// </summary>
    private void CheckContextClosures()
    {
        // Exit FEW-response suppression when the color stack returns to the depth it was at
        // before the C12+C08+C05 context push. The nested pushes (C12+C03, C05+C00+C06 etc.)
        // all pop before this point, so the final pop to FewContextDepth ends the context.
        if (_parser.InFewResponseContext && _colorStack.Count <= _parser.FewContextDepth)
        {
            _parser.ExitFewContext();
            _parser.EmitFewListComplete();
        }

        if (_parser.InFeiResponseContext && _colorStack.Count <= _parser.FeiContextDepth)
        {
            _parser.ExitFeiContext();
            _parser.EmitFeiListComplete();
        }

        if (_parser.InFexResponseContext && _colorStack.Count <= _parser.FexContextDepth)
        {
            _parser.ExitFexContext();
            _parser.EmitFexListComplete();
        }

        // Close the prompt-capture container when the colour stack returns to the depth
        // recorded at the outer {C01}{C255} push. ClosePromptContext then shows the
        // whole captured prompt — '*', '(*)' when invisible, snoop/rank indicators —
        // as a partial line (PromptAllowed) or discards it (FES heartbeat).
        if (_parser.InPromptContext && _colorStack.Count <= _parser.PromptContextDepth)
            _parser.ClosePromptContext();
    }

    /// <summary>
    /// C90+C01 colour throw: restore the colour stack to the depth recorded by the most
    /// recent colour catch (fecodes: "the colour stack is restored to what it was when the
    /// last colour catch was made"). Without this, rainbow wiz names (catch + per-letter
    /// C99 pushes + throw) leave the stack permanently too deep, so the FEW context never
    /// closes and all subsequent terminal output stays suppressed.
    /// </summary>
    private void ThrowToCatch()
    {
        if (_catchDepths.Count == 0) return;   // throw with no preceding catch: ignore
        int depth = _catchDepths.Pop();
        while (_colorStack.Count > depth)
            _colorStack.Pop();
        _parser.Ansi.SetStyle(_colorStack.Count > 0 ? _colorStack.Peek() : TextStyle.Default);
        CheckContextClosures();
    }

    /// <summary>
    /// Process one byte while in a C1-related parser state.
    /// Returns the next <see cref="ParserState"/>.
    /// </summary>
    internal ParserState ProcessByte(byte b, ParserState state, byte lead, List<byte> buf)
        => state switch
        {
            ParserState.C1Seq      => OnC1Seq(b, lead, buf),
            ParserState.C1Data     => OnC1Data(b, lead, buf),
            ParserState.C1Ff1      => OnC1Ff1(b, lead, buf),
            ParserState.FesData    => OnFesData(b, buf),
            ParserState.FewPlayerData   => OnFewPlayerData(b, buf),
            ParserState.DreamwordData   => OnDreamwordData(b, buf),
            ParserState.C95Data    => OnC95Data(b, buf),
            ParserState.C95LogoutLine   => OnC95LogoutLine(b),
            _                      => ParserState.Normal,
        };

    internal void Reset(ref ParserState parserState)
    {
        // If we were collecting C95 lines, the counter is now stale; force the parser
        // back to Normal so the zeroed counter cannot immediately satisfy the completion
        // check on the next byte.
        if (parserState == ParserState.C95Data)
            parserState = ParserState.Normal;
        _c95LinesRemaining = 0;
        _c95LogoutSeenNewline = false;
        _colorStack.Clear();
        _catchDepths.Clear();
    }

    // ── C1 sequence accumulation ──────────────────────────────────────────────

    private ParserState OnC1Seq(byte b, byte lead, List<byte> buf)
    {
        if (b == 0xFF) return ParserState.C1Ff1;
        buf.Add(b);
        // C89 (0xF4): non-terminated — F4+C01 (0x9C) is complete after exactly 1 byte (Clio telnet.l:968)
        if (lead == 0xF4 && b == 0x9C)
        {
            var next = Dispatch(lead, buf);
            buf.Clear();
            return next;
        }
        return ParserState.C1Data;
    }

    private const int C1BufMaxBytes = 8192;

    private ParserState OnC1Data(byte b, byte lead, List<byte> buf)
    {
        if (b == 0xFF) return ParserState.C1Ff1;
        buf.Add(b);
        // C89 (0xF4): non-terminated — only F4+C00+xx (buf[0]==0x9B) is complete after exactly
        // 2 bytes (Clio telnet.l:966-967: F4 9B 9B and F4 9B 9C).
        // Any other F4+xx+yy sequence is unrecognised and falls through to FF FF termination.
        if (lead == 0xF4 && buf.Count == 2 && buf[0] == 0x9B)
        {
            var next = Dispatch(lead, buf);
            buf.Clear();
            return next;
        }
        if (buf.Count > C1BufMaxBytes)
        {
            buf.Clear();
            return ParserState.Normal;
        }
        return ParserState.C1Data;
    }

    private ParserState OnC1Ff1(byte b, byte lead, List<byte> buf)
    {
        if (b == 0xFF)
        {
            // C255 complete — dispatch and clear buf
            var next = Dispatch(lead, buf);
            buf.Clear();
            return next;
        }
        // False alarm: the first 0xFF was payload, keep accumulating
        buf.Add(0xFF);
        buf.Add(b);
        return ParserState.C1Data;
    }

    // ── FES data state (after C12+C08+C01+C255) ───────────────────────────────

    private ParserState OnFesData(byte b, List<byte> buf)
    {
        if (b == '\n')
        {
            ParseAndEmitFes(buf);
            buf.Clear();
            Apply(WHITE, BLACK);
            return ParserState.Normal;
        }
        if (b != '\r')
            buf.Add(b);
        return ParserState.FesData;
    }

    // ── FEW player-name data state (after WHO-list color code + C255) ────────

    private ParserState OnFewPlayerData(byte b, List<byte> buf)
    {
        if (b >= 0x20 && b < 0x7F)
        {
            if (!_parser.InFewResponseContext)
                _parser.EmitChar((char)b);
            buf.Add(b);
            return ParserState.FewPlayerData;
        }
        // A C1 colour sequence inside the name (e.g. the C90 catch + per-letter C99
        // colours + C90 throw of a rainbow wiz name): hand the partial name to the
        // parser so the remaining segments accumulate across the colour codes via
        // EmitChar; the name completes at the line's '\n'. 0xFF is NOT a continuation —
        // a bare FF FF pop is the normal end-of-name terminator (handled below).
        if (b >= 0x9B && b <= 0xFE)
        {
            _parser.BeginFewNameContinuation(
                Encoding.ASCII.GetString(buf.ToArray()),
                _parser.Ansi.CurrentStyle.Foreground);
            buf.Clear();
            _parser.QueueReprocessByte(b);
            return ParserState.Normal;
        }
        if (buf.Count > 0)
        {
            var name = Encoding.ASCII.GetString(buf.ToArray()).Trim();
            buf.Clear();
            if (name.Length > 0)
                _parser.EmitFewPlayer(name, _parser.Ansi.CurrentStyle.Foreground);
        }
        _parser.QueueReprocessByte(b);
        return ParserState.Normal;
    }

    // ── Dreamword data state (after C15+C00+C00+C255) ────────────────────────

    private ParserState OnDreamwordData(byte b, List<byte> buf)
    {
        if (b >= (byte)'a' && b <= (byte)'z' && buf.Count < 14)
        {
            buf.Add(b);
            return ParserState.DreamwordData;
        }

        // End of dreamword — emit and reprocess the terminating byte.
        // C (BLACK/CYAN) was already pushed onto the stack when we entered DreamwordData;
        // do NOT call Apply again or the stack will have an extra entry that the server's
        // subsequent \xFF\xFF pop cannot balance, leaving CYAN active for the rest of the line.
        if (buf.Count > 0)
        {
            var word = Encoding.ASCII.GetString(buf.ToArray());
            foreach (var ch in word)
                _parser.EmitChar(ch);
            _parser.EmitDreamwordChanged(word);
            buf.Clear();
        }
        _parser.QueueReprocessByte(b);
        return ParserState.Normal;
    }

    // ── C95 client-mode data (after C95+C255) ─────────────────────────────────

    private ParserState OnC95Data(byte b, List<byte> buf)
    {
        buf.Add(b);
        if (b == '\n' && --_c95LinesRemaining <= 0)
        {
            var data = Encoding.ASCII.GetString(buf.ToArray());
            buf.Clear();
            _parser.EmitClientMode(data);
            // Parse Rule A fields: licence, minclient, maxclient, account, privs
            var lines = data.Split('\n');
            if (lines.Length >= 5)
            {
                var accountId = lines[3].TrimEnd('\r');
                _ = int.TryParse(lines[4].TrimEnd('\r'), out int privs);
                _parser.SetAccountInfo(accountId, privs);
            }
            return ParserState.Normal;
        }
        return ParserState.C95Data;
    }

    // ── C95 account-logout line (after C95+C03+C255) ─────────────────────────

    private ParserState OnC95LogoutLine(byte b)
    {
        if (_c95LogoutSeenNewline)
        {
            // Absorb trailing 0xFF bytes (color-terminator) the server appends after the line.
            if (b == 0xFF) return ParserState.C95LogoutLine;
            _c95LogoutSeenNewline = false;
            _parser.QueueReprocessByte(b);
            return ParserState.Normal;
        }
        if (b == '\n')
        {
            _c95LogoutSeenNewline = true;
            return ParserState.C95LogoutLine;
        }
        return ParserState.C95LogoutLine;
    }

    // ── C1 dispatch ───────────────────────────────────────────────────────────

    /// <summary>
    /// Dispatch based on the C1 lead byte and accumulated payload.
    /// All callers must clear <paramref name="buf"/> after this returns (done in OnC1Ff1).
    /// Returns the next ParserState (Normal, or a data-collection sub-state).
    /// </summary>
    private ParserState Dispatch(byte lead, List<byte> buf)
    {
        _parser.FlushSpan();

        int count = buf.Count;
        byte b0 = count > 0 ? buf[0] : (byte)0;
        byte b1 = count > 1 ? buf[1] : (byte)0;

        switch (lead)
        {
            // ── C00 (0x9B): init_stack → reset to WHITE/BLACK ─────────────────
            // Clio sends C00+C255 at the start of every game-output frame (before the room
            // short description, text, etc.) as a color-stack reset. C1 sequences do not
            // advance the display column, so _atLineStart is untouched here and everywhere
            // else in Dispatch — only text characters clear it.
            case 0x9B:
                Apply(WHITE, BLACK);
                return ParserState.Normal;

            // ── C01 (0x9C): the prompt family — BLUE or LT_BLUE ──────────────
            // Code 01 is "invisibility brackets around the prompt" (mud2_fe4 §codes):
            // {C01}{C255}                 → BLUE/BLACK; in game mode this is the OUTER
            //                               container that wraps the ENTIRE prompt
            // {C01}{C01..C03}{C255}       → LT_BLUE/BLACK + enter game mode; the inner
            //                               prompt core (01 01 wiz / 01 02 mortal / 01 03 wiz-set)
            // {C01}{C04}{C255}|{C01}{C05}{C255} → BLUE/BLACK (snoop/aux prompt indicators)
            //
            // "The prompt" is contextual — it is everything inside the outer container,
            // not just the '*': '(*)' when invisible, plus snoop/rank indicators. The
            // outer push therefore opens a prompt-capture context; PopColor closes it
            // when the stack unwinds to entry depth and shows/discards the capture per
            // Clio's prompt_allowed gate (telnet.l:438-444).
            case 0x9C:
            {
                bool isGameModeVariant = b0 is 0x9C or 0x9D or 0x9E;
                bool wasAlreadyInGameMode = _parser.InGameMode;
                Apply(isGameModeVariant ? LT_BLUE : BLUE, BLACK);
                if (isGameModeVariant)
                    _parser.EnterGameMode();
                if (wasAlreadyInGameMode)
                {
                    if (count == 0)
                    {
                        // Bare {C01}{C255}: the outer prompt container. Capture until its
                        // matching pop. Re-entering while a context is still open means the
                        // previous container lost its pop — restart capture at the new depth.
                        _parser.EnterPromptContext(_colorStack.Count - 1);
                    }
                    else if (isGameModeVariant && !_parser.InPromptContext)
                    {
                        // Inner prompt core with no outer container: capture just this
                        // code's own extent so a bare {C01}{C02}{C255}*{C255} still gates.
                        _parser.EnterPromptContext(_colorStack.Count - 1);
                    }
                }
                return ParserState.Normal;
            }

            // ── C02 (0x9D): GREEN shades + game-mode entry ────────────────────
            // {C02}{C00}{C255} → BLACK/GREEN
            // {C02}{C01}{C255} → LT_GREEN/BLACK + enter game mode
            // {C02}{C02}{C255} → GREEN/BLACK
            case 0x9D:
                switch (b0)
                {
                    case 0x9B when count == 1:
                        Apply(BLACK, GREEN);
                        break;
                    case 0x9C when count == 1:
                        Apply(LT_GREEN, BLACK);
                        _parser.EnterGameMode();
                        // Fire RoomEntered if C02+C01 arrived at line start (column 0).
                        // This applies on game-entry too — the first C02+C01 from the server
                        // is the room-short line for the room the player logged in to.
                        // ClearLineStart() prevents a second consecutive C02+C01 on the same
                        // line from double-firing. SetPendingRoomShort() is read at '\n' emission
                        // to gate RoomShortReady — only fired for line-start room shorts.
                        if (_parser.AtLineStart)
                        {
                            _parser.ClearLineStart();
                            _parser.EmitRoomEntered();
                            _parser.SetPendingRoomShort();
                        }
                        break;
                    case 0x9D when count == 1:
                        Apply(GREEN, BLACK);
                        break;
                    default:
                        Apply(WHITE, BLACK);
                        break;
                }
                return ParserState.Normal;

            // ── C03 (0x9E): CYAN / LT_CYAN (room/location variants) ──────────
            // {C03}{C00..C03}{C255} → GREEN/BLACK
            // {C03}{C01..C03}{C255} → CYAN/BLACK
            // {C03}{C02..C03+variants}{C255} → LT_CYAN/BLACK
            case 0x9E:
                if (b0 == 0x9B)                         Apply(GREEN,    BLACK);
                else if (b0 == 0x9C)                    Apply(CYAN,     BLACK);
                else                                    Apply(LT_CYAN,  BLACK);
                return ParserState.Normal;

            // ── C04 (0x9F): MAGENTA / LT_MAGENTA ─────────────────────────────
            // {C04}{C00}{C06}{C255} → MAGENTA/BLACK   + WHO-list creature name follows
            // {C04}{C01}{C06}{C255} → LT_MAGENTA/BLACK + WHO-list wiz-creature name follows
            // {C04}{C01}{C255} or {C04}{C01}..{C255} → LT_MAGENTA/BLACK
            // everything else → MAGENTA/BLACK
            case 0x9F:
                Apply(b0 == 0x9C ? LT_MAGENTA : MAGENTA, BLACK);
                return count == 2 && b1 == 0xA1 ? ParserState.FewPlayerData : ParserState.Normal;

            // ── C05 (0xA0): RED / LT_RED / LT_YELLOW (combat/damage) ─────────
            // {C05}{C00}{C06}{C255} → RED/BLACK    + WHO-list mortal name follows
            // {C05}{C01}{C06}{C255} → LT_RED/BLACK + WHO-list wiz name follows
            // {C05}{C00+variants}{C255} → RED/BLACK (except C00+C09 → LT_YELLOW)
            // {C05}{C01+variants}{C255} → LT_RED/BLACK (except C01+C09 → LT_YELLOW)
            case 0xA0:
                if (count == 2 && b1 == 0xA1)                    // C00/C01+C06: WHO-list player
                {
                    Apply(b0 == 0x9C ? LT_RED : RED, BLACK);
                    return ParserState.FewPlayerData;
                }
                if (count == 2 && b0 == 0x9B && b1 == 0xA4) Apply(LT_YELLOW, BLACK);  // C00+C09
                else if (count == 2 && b0 == 0x9C && b1 == 0xA4) Apply(LT_YELLOW, BLACK); // C01+C09
                else if (b0 == 0x9C) Apply(LT_RED, BLACK);
                else Apply(RED, BLACK);
                return ParserState.Normal;

            // ── C06 (0xA1): LT_BLUE (magical/special) + txfes ───────────────
            // Exception: {C06}{C06}{C255} ("Something magical") → LT_BLUE only, no txfes (Clio:581-584)
            // All other variants → txfes (Clio:562-580)
            // All variants → sound(6) (Clio sound.c)
            case 0xA1:
                Apply(LT_BLUE, BLACK);
                if (!(count == 1 && b0 == 0xA1))
                    _parser.EmitOutgoing(FesSubscription);
                Sound(6);
                return ParserState.Normal;

            // ── C07 (0xA2): RED/BLACK + txfes (important messages) ──────────
            // All C07 variants trigger txfes (Clio telnet.l:587-620)
            // Sound payload: count==0 → 070000, count==1 → 07NN, count==2 → 07NNMM (Clio sound.c)
            case 0xA2:
                Apply(RED, BLACK);
                _parser.EmitOutgoing(FesSubscription);
                switch (count)
                {
                    case 0: Sound(7, 0, 0); break;
                    case 1: Sound(7, b0 - 0x9B); break;
                    case 2: Sound(7, b0 - 0x9B, b1 - 0x9B); break;
                }
                return ParserState.Normal;

            // ── C08 (0xA3): RED / WHITE / BLACK+RED (combat/death) ────────────
            // {C08}{C01}{C255}|{C08}{C03}{C255} → LT_RED/BLACK + txfes
            // {C08}{C00/C02/C04}{C255} → RED/BLACK  (plain, NO txfes — Clio:633-636)
            // {C08}{C05..C07/C09}{C255} → WHITE/BLACK
            // {C08}{C08}{C255} → BLACK/RED + txfes
            // {C08}{C10..C12}{C255} → RED/BLACK + txfes
            // {C08}{C13}{C255} → BLACK/RED
            case 0xA3:
                switch (b0)
                {
                    case 0x9C or 0x9E:         Apply(LT_RED,  BLACK); break;  // C01, C03
                    case 0x9B or 0x9D or 0x9F: Apply(RED,     BLACK); break;  // C00, C02, C04
                    case 0xA0 or 0xA1 or 0xA2 or 0xA4: Apply(WHITE, BLACK); break; // C05,C06,C07,C09
                    case 0xA3:                 Apply(BLACK,   RED);   break;  // C08
                    case 0xA5 or 0xA6 or 0xA7: Apply(RED,    BLACK); break;  // C10,C11,C12
                    case 0xA8:                 Apply(BLACK,   RED);   break;  // C13
                    default:                   Apply(RED,     BLACK); break;
                }
                // txfes: C01, C03 (Clio:623,628), C08 (Clio:641), C10/C11/C12 (Clio:645-650)
                // NOT C00, C02, C04 (Clio:633-636 — plain RED, no txfes)
                if (b0 is 0x9C or 0x9E or 0xA3 or 0xA5 or 0xA6 or 0xA7)
                    _parser.EmitOutgoing(FesSubscription);
                // Sound: C01→0801, C03→0803 (Clio sound.c)
                if (b0 == 0x9C) Sound(8, 1);
                else if (b0 == 0x9E) Sound(8, 3);
                return ParserState.Normal;

            // ── C09 (0xA4): YELLOW / LT_YELLOW ──────────────────────────────
            // {C09}{C00}{C255} → YELLOW/BLACK
            // everything else → LT_YELLOW/BLACK
            case 0xA4:
                Apply(b0 == 0x9B && count == 1 ? YELLOW : LT_YELLOW, BLACK);
                return ParserState.Normal;

            // ── C10 (0xA5): BLACK+YELLOW / LT_RED+YELLOW ─────────────────────
            // {C10}{C00/C03}{C255} → BLACK/YELLOW
            // {C10}{C01/C02/C04}{C255} → LT_RED/YELLOW (non-wireplay)
            case 0xA5:
                if (b0 == 0x9B || b0 == 0x9E) Apply(BLACK,  YELLOW);
                else                           Apply(LT_RED, YELLOW);
                return ParserState.Normal;

            // ── C11 (0xA6): LT_RED (spells/abilities) ────────────────────────
            // {C11}{C255}|{C11}{C06}{C255}|{C11}{C09}{C255}|{C11}{C14}{C255} → LT_RED, no txfes (Clio:675-685)
            // All other single-byte payload variants → LT_RED + txfes + sound(11,NN) (Clio:687-713)
            case 0xA6:
                Apply(LT_RED, BLACK);
                if (count == 1 && b0 is not (0xA1 or 0xA4 or 0xA9))
                {
                    _parser.EmitOutgoing(FesSubscription);
                    Sound(11, b0 - 0x9B);
                }
                return ParserState.Normal;

            // ── C12 (0xA7): WHITE/GREEN/YELLOW shades + FES packet ────────────
            // {C12}{C255}|{C12}{C01..C03+variants}{C255} → WHITE/BLACK
            // {C12}{C04/C05}{C255} → GREEN/BLACK
            // {C12}{C06}{C255} → YELLOW/BLACK
            // {C12}{C07}{C255} → LT_YELLOW/BLACK
            // {C12}{C08}{C01}{C255} → FES data line follows
            // {C12}{C08}{C02..C04/C09/C10}{C255} → WHITE/BLACK
            // {C12}{C08}{C05}{C255} → FEW response context: suppress display, capture names
            case 0xA7:
                if (count == 2 && b0 == 0xA3 && b1 == 0x9C)
                {
                    // FES packet: C12+C08+C01+C255, data line follows (up to '\n')
                    // Color applied after parsing (WHITE/BLACK applied in OnFesData)
                    return ParserState.FesData;
                }
                if (count == 2 && b0 == 0xA3 && b1 == 0xA0)
                {
                    // FEW response: C12+C08+C05+C255 — suppress display output; FewPlayerReady still fires.
                    // Record the pre-push stack depth so PopColor can detect when this context closes.
                    Apply(WHITE, BLACK);
                    _parser.EnterFewContext(_colorStack.Count - 1);
                    _parser.EmitFewListStarting();
                    return ParserState.Normal;
                }
                if (count == 2 && b0 == 0xA3 && b1 == 0x9D)
                {
                    // FEX response: C12+C08+C02+C255 — exit keyword lines follow until stack
                    // returns to entry depth. Each line is one direction keyword.
                    Apply(WHITE, BLACK);
                    _parser.EnterFexContext(_colorStack.Count - 1);
                    _parser.EmitFexListStarting();
                    return ParserState.Normal;
                }
                if (count == 2 && b0 == 0xA3 && b1 == 0x9E)
                {
                    // FEI response: C12+C08+C03+C255 — item lines (plain text) follow until stack
                    // returns to entry depth. "========" separates room items from carried items.
                    Apply(WHITE, BLACK);
                    _parser.EnterFeiContext(_colorStack.Count - 1);
                    _parser.EmitFeiListStarting();
                    return ParserState.Normal;
                }
                switch (b0)
                {
                    case 0x9F or 0xA0: Apply(GREEN,    BLACK); break; // C04, C05
                    case 0xA1:         Apply(YELLOW,   BLACK); break; // C06
                    case 0xA2:         Apply(LT_YELLOW,BLACK); break; // C07
                    default:           Apply(WHITE,    BLACK); break;
                }
                return ParserState.Normal;

            // ── C13 (0xA8): WHITE / BLACK+WHITE ──────────────────────────────
            // {C13}{C255}|{C13}..{C255} → WHITE/BLACK  (except wireplay: BLACK/WHITE; skip wireplay)
            // {C13}{CNi}{CNj}{C255} → sound(13,i) where i=buf[0]-0x9B (Clio sound.c)
            case 0xA8:
                Apply(WHITE, BLACK);
                if (count == 2) Sound(13, b0 - 0x9B);
                return ParserState.Normal;

            // ── C14 (0xA9): GREEN / BLACK+WHITE (weather/outdoor) + txfes ──────
            // Most variants → GREEN/BLACK + txfes; snow/frost → BLACK/WHITE (non-wireplay)
            // C01 (snow): conditional txfes when weather changed from non-snow (Clio:833-843)
            // C02 (rain): conditional txfes when weather changed from non-rain (Clio:844-850)
            // C04 variants: NO txfes (sweather only, Clio:875-885)
            case 0xA9:
            {
                bool emitTxFes = false;
                if (count == 1)
                {
                    switch (b0)
                    {
                        case 0x9B: // C00 → GREEN/BLACK + always txfes (Clio:832)
                            Apply(GREEN, BLACK);
                            emitTxFes = true;
                            break;
                        case 0x9C: // C01 → snow BLACK/WHITE + conditional txfes (Clio:833-843)
                            Apply(BLACK, WHITE);
                            emitTxFes = _parser.CurrentWeather != 'S' && _parser.CurrentWeather != 'B';
                            break;
                        case 0x9D: // C02 → rain GREEN/BLACK + conditional txfes (Clio:844-850)
                            Apply(GREEN, BLACK);
                            emitTxFes = _parser.CurrentWeather != 'R' && _parser.CurrentWeather != 'T';
                            break;
                        default:
                            Apply(GREEN, BLACK);
                            break;
                    }
                }
                else if (count == 2)
                {
                    bool isWhite = b1 == 0x9C || b1 == 0x9E; // C01 or C03 → BLACK/WHITE
                    switch (b0)
                    {
                        case 0x9E: // C03+xx → always txfes (Clio:852-873)
                            Apply(isWhite ? BLACK : GREEN, isWhite ? WHITE : BLACK);
                            emitTxFes = true;
                            break;
                        case 0x9F: // C04+xx → NO txfes (sweather, Clio:875-885)
                            Apply(isWhite ? BLACK : GREEN, isWhite ? WHITE : BLACK);
                            break;
                        case 0xA0: // C05+xx → always txfes (Clio:887-895)
                            Apply(b1 == 0x9C ? BLACK : GREEN, b1 == 0x9C ? WHITE : BLACK);
                            emitTxFes = true;
                            break;
                        case 0xA1: // C06+xx → always txfes (Clio:897-899)
                            Apply(GREEN, BLACK);
                            emitTxFes = true;
                            break;
                        default:
                            Apply(GREEN, BLACK);
                            break;
                    }
                }
                else
                {
                    Apply(GREEN, BLACK);
                }
                if (emitTxFes)
                    _parser.EmitOutgoing(FesSubscription);
                // Sound: C14+C03+C02+C255 → rain on trees 140302 (Clio sound.c)
                if (count == 2 && b0 == 0x9E && b1 == 0x9D)
                    Sound(14, 3, 2);
                return ParserState.Normal;
            }

            // ── C15 (0xAA): BLACK+CYAN (dreamword) ───────────────────────────
            // {C15}{C00}{C00}{C255}[a-z]{1,14} → dreamword set
            // {C15}{C00}{C01}{C255} → dreamword cleared
            // everything else → BLACK/CYAN
            case 0xAA:
                if (count == 2 && b0 == 0x9B && b1 == 0x9B)
                {
                    // Dreamword follows; color applied in OnDreamwordData
                    Apply(BLACK, CYAN);
                    return ParserState.DreamwordData;
                }
                if (count == 2 && b0 == 0x9B && b1 == 0x9C)
                {
                    // Dreamword cleared + txfes
                    Apply(BLACK, CYAN);
                    _parser.EmitDreamwordChanged(null);
                    _parser.EmitOutgoing(FesSubscription);
                    return ParserState.Normal;
                }
                Apply(BLACK, CYAN);
                return ParserState.Normal;

            // ── C16 (0xAB): LT_WHITE+BLUE (house messages) ───────────────────
            case 0xAB:
                Apply(LT_WHITE, BLUE);
                return ParserState.Normal;

            // ── C17 (0xAC): not explicitly listed → catchall WHITE ────────────
            case 0xAC:
                Apply(WHITE, BLACK);
                return ParserState.Normal;

            // ── C18 (0xAD): WHITE + txfes (misc system messages) ────────────
            // {C18}{C00..C06}{C255} → WHITE/BLACK + txfes + sound(18,NN) (Clio telnet.l:944-957, sound.c)
            case 0xAD:
                Apply(WHITE, BLACK);
                if (count == 1 && b0 >= 0x9B && b0 <= 0xA1) // C00..C06
                {
                    _parser.EmitOutgoing(FesSubscription);
                    Sound(18, b0 - 0x9B);
                }
                return ParserState.Normal;

            // ── C19 (0xAE): LT_WHITE+BLUE ────────────────────────────────────
            case 0xAE:
                Apply(LT_WHITE, BLUE);
                return ParserState.Normal;

            // ── C20–C21 (0xAF–0xB0): not listed → catchall ──────────────────
            case 0xAF or 0xB0:
                Apply(WHITE, BLACK);
                return ParserState.Normal;

            // ── C89 (0xF4): WHITE/BLACK (catch/display) ──────────────────────
            case 0xF4:
                Apply(WHITE, BLACK);
                return ParserState.Normal;

            // ── C90 (0xF5): catch()/throw() — color-stack save/restore ─────
            // {C90}{C255}      → colour catch: snapshot the stack depth; NO colour change
            //                    and NO push (fecodes: "90 — Colour catch. No colour change.")
            // {C90}{C01}{C255} → colour throw: restore the stack to the last catch point,
            //                    undoing multiply-deep colour changes in a single code
            //                    (e.g. the per-letter C99 colours in rainbow wiz names).
            case 0xF5:
                if (count == 0)
                {
                    _catchDepths.Push(_colorStack.Count);
                    return ParserState.Normal;
                }
                if (count == 1 && b0 == 0x9C)
                {
                    ThrowToCatch();
                    return ParserState.Normal;
                }
                Apply(WHITE, BLACK);   // unrecognised C90 variant: legacy reset behaviour
                return ParserState.Normal;

            // ── C94 (0xF9): snoop starts → WHITE/BLACK ───────────────────────
            case 0xF9:
                Apply(WHITE, BLACK);
                return ParserState.Normal;

            // ── C95 (0xFA): client-mode data block ───────────────────────────
            // {C95}{C255}           → 5 lines: licence, min-level, max-level, account, privs
            // {C95}{C02}{C255}      → account change; new 5-line Rule A block follows
            // {C95}{C03}{C255}      → account-logout notice (1 trailing line, silent)
            case 0xFA:
                if (count == 0)
                {
                    _c95LinesRemaining = 5;
                    return ParserState.C95Data;
                }
                if (count == 1 && b0 == 0x9D) // C02 → account change; collect new Rule A block
                {
                    _c95LinesRemaining = 5;
                    return ParserState.C95Data;
                }
                if (count == 1 && b0 == 0x9E) // C03 → account logout, transition to Options menu
                {
                    _parser.ExitGameMode();
                    return ParserState.C95LogoutLine;
                }
                Apply(WHITE, BLACK);
                return ParserState.Normal;

            // ── C96 (0xFB): snoop ends → WHITE/BLACK ─────────────────────────
            case 0xFB:
                Apply(WHITE, BLACK);
                return ParserState.Normal;

            // ── C97 (0xFC): not listed → catchall ────────────────────────────
            case 0xFC:
                Apply(WHITE, BLACK);
                return ParserState.Normal;

            // ── C98 (0xFD): BLACK+BLUE or BLACK+MAGENTA ──────────────────────
            // {C98}.{C255}: even payload byte → BLACK/BLUE, odd → BLACK/MAGENTA
            // Do NOT set PromptAllowed here — PromptAllowed must only be set by real game
            // '\n' bytes so that the C01 prompt gate can distinguish a real prompt (which
            // follows game content) from a FES heartbeat (which arrives with PromptAllowed=false
            // because no game '\n' has occurred since the last prompt was displayed).
            case 0xFD:
                if (count == 1)
                {
                    if (b0 % 2 == 0) Apply(BLACK, BLUE);
                    else             Apply(BLACK, MAGENTA);
                }
                else
                {
                    Apply(WHITE, BLACK);
                }
                _parser.ShowPrompt();
                return ParserState.Normal;

            // ── C99 (0xFE): arbitrary color from payload bytes ───────────────
            // {C99}{C99}{C255}    → WHITE/BLACK
            // {C99}{fg}{bg}{C255} → color (fg−155, bg−155)
            // {C99}{fg}{C255}     → color (fg−155, BLACK)
            // Color bytes are offset by 0x9B (155); index clamped to 0–15.
            case 0xFE:
                if (count == 1 && b0 == 0xFE)       // FE FE FF FF → WHITE/BLACK (special reset)
                    Apply(WHITE, BLACK);
                else if (count == 2)
                    Apply(C99Color(b0), C99Color(b1));
                else if (count == 1)
                    Apply(C99Color(b0), BLACK);
                else
                    Apply(WHITE, BLACK);
                return ParserState.Normal;

            // ── Catchall: any unrecognised C1 lead byte → WHITE/BLACK ─────────
            default:
                Apply(WHITE, BLACK);
                return ParserState.Normal;
        }
    }

    // ── FES parsing ───────────────────────────────────────────────────────────

    /// <summary>
    /// Parse the FES (Front-End Score) data line that follows C12+C08+C01+C255.
    ///
    /// Data format :
    /// Strip embedded C99 color markers (0xFE + 1 byte), then split by space:
    ///   [0]=sta [1]=msta [2]=str [3]=mstr [4]=dex [5]=mdex
    ///   [6]=mag [7]=mmag [8]=score [9]=blind [10]=deaf [11]=crippled
    ///   [12]=dumb [13]=reset(minutes) [14]=weather
    /// At least 15 fields required; score is a long (comma-free on server).
    /// </summary>
    private void ParseAndEmitFes(List<byte> rawBytes)
    {
        if (rawBytes.Count == 0) return;

        // Each numeric field in the FES payload may be prefixed by C1 color sequences
        // (e.g. C89+args+C255 or C99+1byte) that encode the display color for that stat.
        // Clio extracts scolor via strchr(yytext,'\376') (= 0xFE, the C99 marker) and then
        // finds the digit start with strpbrk (telnet.l:725-734). We do the same: build a
        // plain-ASCII copy for field splitting by extracting only printable ASCII bytes,
        // while separately capturing the C99 (0xFE + 1-byte) stamina color hint.
        byte? staColorHint = null;
        var textBytes = new List<byte>(rawBytes.Count);
        for (var i = 0; i < rawBytes.Count; i++)
        {
            var b2 = rawBytes[i];
            if (b2 == 0xFE && i + 1 < rawBytes.Count)
            {
                // C99 c marker — record first occurrence as the stamina c hint.
                // The byte following 0xFE is a C1 byte (0x9B + ANSI index); subtract 0x9B
                // to produce the 0–15 ANSI index expected by GameViewModel.AnsiToColor.
                if (staColorHint is null)
                    staColorHint = (byte)Math.Clamp(rawBytes[i + 1] - 0x9B, 0, 15);
                i++; // skip the color byte; do not emit either byte as text
                continue;
            }
            // Skip other C1 leads (>= 0x9B) and 0xFF (C255 / IAC terminator).
            // These are color-push sequences that bracket numeric values for display;
            // the numeric ASCII digits immediately follow and are kept.
            if (b2 >= 0x9B || b2 == 0xFF)
                continue;
            textBytes.Add(b2);
        }

        var text = Encoding.ASCII.GetString(textBytes.ToArray());
        var fields = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 15) return;

        int? sta   = int.TryParse(fields[0],  out int _sta)   ? _sta   : null;
        int? msta  = int.TryParse(fields[1],  out int _msta)  ? _msta  : null;
        int? str   = int.TryParse(fields[2],  out int _str)   ? _str   : null;
        int? mstr  = int.TryParse(fields[3],  out int _mstr)  ? _mstr  : null;
        int? dex   = int.TryParse(fields[4],  out int _dex)   ? _dex   : null;
        int? mdex  = int.TryParse(fields[5],  out int _mdex)  ? _mdex  : null;
        int? mag   = int.TryParse(fields[6],  out int _mag)   ? _mag   : null;
        int? mmag  = int.TryParse(fields[7],  out int _mmag)  ? _mmag  : null;
        _ = long.TryParse(fields[8], out long score);
        bool blind    = fields[9]  == "Y";
        bool deaf     = fields[10] == "Y";
        bool crippled = fields[11] == "Y";
        bool dumb     = fields[12] == "Y";
        int? reset = int.TryParse(fields[13], out int _reset) ? _reset : null;
        char weather  = fields[14].Length > 0 ? fields[14][0] : ' ';

        if (score > int.MaxValue || score < int.MinValue)
            Debug.WriteLine($"[Mud2C1Decoder] FES score {score} exceeds int32 range; clamping to {(score > int.MaxValue ? int.MaxValue : int.MinValue)}");

        var snapshot = new GameStatsSnapshot(
            Stamina:      sta,
            MaxStamina:   msta,
            Score:        (int)Math.Clamp(score, int.MinValue, int.MaxValue),
            Strength:     str,
            MaxStrength:  mstr,
            Dexterity:    dex,
            MaxDexterity: mdex,
            CurrentMagic: mag,
            MaxMagic:     mmag,
            IsBlind:      blind,
            IsDeaf:       deaf,
            IsCrippled:   crippled,
            IsDumb:       dumb,
            Weather:      weather,
            TimeToReset:  reset,
            DreamWord:    _parser.CurrentDreamword,
            PersonaSaved: false,
            AccountId:    _parser.CurrentAccountId,
            Privs:        _parser.CurrentPrivs,
            StaminaColor: staColorHint
        ) { HasFesStats = true };
        _parser.SetWeather(weather);
        _parser.EmitStatsUpdate(snapshot);
    }
}
