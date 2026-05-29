using MudSharp.Models;
using System.Diagnostics;
using System.Text;

namespace MudSharp.Protocol;

/// <summary>
/// Decoder for the MUD2 proprietary C1 binary protocol (lead bytes 0x9B–0xFE).
/// All sequences end with the C255 terminator (0xFF 0xFF).
/// Colour assignments follow Clio telnet.l exactly (non-wireplay branch).
///
/// Clio colour-constant → AnsiColor mapping (indices match 1:1):
///   BLACK=0  RED=1  GREEN=2  YELLOW=3  BLUE=4  MAGENTA=5  CYAN=6  WHITE=7
///   LT_BLACK=8  LT_RED=9  LT_GREEN=10  LT_YELLOW=11  LT_BLUE=12  LT_MAGENTA=13  LT_CYAN=14  LT_WHITE=15
/// </summary>
internal sealed class Mud2C1Decoder
{
    private readonly MudStreamParser _parser;

    // Colour stack: tracks colour history for bare FF FF pop() (Clio telnet.l:1040)
    private readonly Stack<TextStyle> _colourStack = new();

    // C95 line counter (how many newline-terminated lines remain to collect)
    private int _c95LinesRemaining;

    // After the C95-logout line's '\n', absorb the trailing 0xFF 0xFF colour-terminator
    // that the server appends before returning to Normal.
    private bool _c95LogoutSeenNewline;

    internal Mud2C1Decoder(MudStreamParser parser) { _parser = parser; }

    // ── Clio colour index constants ───────────────────────────────────────────
    private const int BLACK = 0, RED = 1, GREEN = 2, YELLOW = 3;
    private const int BLUE = 4, MAGENTA = 5, CYAN = 6, WHITE = 7;
    private const int LT_BLACK = 8, LT_RED = 9, LT_GREEN = 10, LT_YELLOW = 11;
    private const int LT_BLUE = 12, LT_MAGENTA = 13, LT_CYAN = 14, LT_WHITE = 15;

    // ── Helpers ───────────────────────────────────────────────────────────────

    // FES subscription bytes: ESC - [ F E S ESC - ]
    private static readonly byte[] FesSubscription = { 0x1B, 0x2D, 0x5B, 0x46, 0x45, 0x53, 0x1B, 0x2D, 0x5D };

    private static AnsiColor Clr(int clio)
        => (AnsiColor)Math.Clamp(clio, 0, 15);

    private static TextStyle Style(int fg, int bg = BLACK)
        => new(Clr(fg), Clr(bg));

    private void Apply(int fg, int bg = BLACK)
    {
        var style = Style(fg, bg);
        _colourStack.Push(style);
        _parser.Ansi.SetStyle(style);
    }

    private void Apply(TextStyle s)
    {
        _colourStack.Push(s);
        _parser.Ansi.SetStyle(s);
    }

    // Clamp a raw C99 colour byte to a Clio index: byte - 0x9B (C00 base)
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
    /// Pop the colour stack (Clio pop() on bare FF FF).
    /// Restores the style that was active before the last push, if any.
    /// Also handles the two prompt-preamble flags set by the C01 game-mode dispatch.
    /// </summary>
    internal void PopColour()
    {
        _parser.FlushSpan();
        if (_colourStack.Count > 0)
            _colourStack.Pop();
        if (_colourStack.Count > 0)
            _parser.Ansi.SetStyle(_colourStack.Peek());
        // If stack is empty after pop, silently ignore (Clio: if (pop() == -1) { /* commented out */ })

        // Exit FEW-response suppression when the colour stack returns to the depth it was at
        // before the C12+C08+C05 context push. The nested pushes (C12+C03, C05+C00+C06 etc.)
        // all pop before this point, so the final pop to FewContextDepth ends the context.
        if (_parser.InFewResponseContext && _colourStack.Count <= _parser.FewContextDepth)
        {
            _parser.ExitFewContext();
            _parser.EmitFewListComplete();
        }

        // Handle prompt-preamble flags set by the C01 game-mode dispatch (Clio telnet.l:438-444).
        // Both flags are cleared after the first pop that follows the '*' prompt text.
        if (_parser.EmitPartialOnPop)
        {
            _parser.EmitPartialLine();  // show '*' as live partial-line prompt; clears spans internally
            _parser.EmitPartialOnPop = false;
            _parser.SetFrameStart();    // game prompt shown: next C1 dispatch may be a room short
        }
        _parser.SuppressNextText = false;

        // Restore style to default when the C1 colour stack is fully unwound.
        // Clio ignores this case (pop() == -1, commented out), but we must explicitly reset
        // Ansi.CurrentStyle or subsequent text inherits the last C1 colour (e.g. BLUE from
        // the wire prompt preamble, making command echoes appear in prompt colour).
        if (_colourStack.Count == 0)
            _parser.Ansi.SetStyle(TextStyle.Default);
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
        _colourStack.Clear();
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
        if (buf.Count > 0)
        {
            var name = Encoding.ASCII.GetString(buf.ToArray()).Trim();
            buf.Clear();
            if (name.Length > 0)
                _parser.EmitFewPlayer(name);
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
        // Colour (BLACK/CYAN) was already pushed onto the stack when we entered DreamwordData;
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
            // Absorb trailing 0xFF bytes (colour-terminator) the server appends after the line.
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
        bool wasAtFrameStart = _parser.AtFrameStart;
        _parser.ClearFrameStart();

        int count = buf.Count;
        byte b0 = count > 0 ? buf[0] : (byte)0;
        byte b1 = count > 1 ? buf[1] : (byte)0;

        switch (lead)
        {
            // ── C00 (0x9B): init_stack → reset to WHITE/BLACK ─────────────────
            case 0x9B:
                Apply(WHITE, BLACK);
                return ParserState.Normal;

            // ── C01 (0x9C): BLUE or LT_BLUE prompt/location colours ───────────
            // {C01}{C255}|{C01}{C04}{C255}|{C01}{C05}{C255} → BLUE/BLACK
            // {C01}{C01}{C255}|{C01}{C02}{C255}|{C01}{C03}{C255} → LT_BLUE/BLACK + enter game mode (Clio lines 454–468)
            //
            // When this game-mode variant arrives while already in game mode it is the
            // inner half of the prompt preamble  ({C01}{C255}{C01}{C02}{C255}*{C255}{C255}).
            // Mirror Clio's prompt_allowed gate (telnet.l:438-444):
            //   PromptAllowed=true  → show the '*' as a partial-line prompt once, then clear spans
            //   PromptAllowed=false → suppress the '*' text entirely (end-of-frame marker)
            case 0x9C:
            {
                bool isGameModeVariant = b0 is 0x9C or 0x9D or 0x9E;
                bool wasAlreadyInGameMode = _parser.InGameMode;
                Apply(isGameModeVariant ? LT_BLUE : BLUE, BLACK);
                if (isGameModeVariant)
                {
                    _parser.EnterGameMode();
                    if (wasAlreadyInGameMode)
                    {
                        // Gate prompt emission on Clio's prompt_allowed flag (telnet.l:438-444).
                        // PromptAllowed is set true by every real game '\n' and cleared when the
                        // prompt is shown; it persists across TCP packet boundaries.
                        // FES heartbeat prompt preambles arrive when PromptAllowed is still false
                        // (no real '\n' since the last prompt display), so they are suppressed.
                        if (_parser.PromptAllowed)
                        {
                            _parser.PromptAllowed = false;
                            _parser.EmitPartialOnPop = true;
                        }
                        else
                        {
                            _parser.SuppressNextText = true;
                        }
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
                        if (wasAtFrameStart) _parser.EmitRoomEntered();
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
                    // Colour applied after parsing (WHITE/BLACK applied in OnFesData)
                    return ParserState.FesData;
                }
                if (count == 2 && b0 == 0xA3 && b1 == 0xA0)
                {
                    // FEW response: C12+C08+C05+C255 — suppress display output; FewPlayerReady still fires.
                    // Record the pre-push stack depth so PopColour can detect when this context closes.
                    Apply(WHITE, BLACK);
                    _parser.EnterFewContext(_colourStack.Count - 1);
                    _parser.EmitFewListStarting();
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
                    // Dreamword follows; colour applied in OnDreamwordData
                    Apply(BLACK, CYAN);
                    return ParserState.DreamwordData;
                }
                if (count == 2 && b0 == 0x9B && b1 == 0x9C)
                {
                    // Dreamword cleared + txfes (Clio telnet.l:916-925)
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

            // ── C90 (0xF5): catch()/throw() — colour-stack save/restore ─────
            // Clio telnet.l uses these to bracket sections where a colour change must
            // be reverted; catch() snapshots the stack and throw() restores it.
            // Full save/restore requires knowing whether this is catch or throw from
            // the payload — deferred until we can verify against the server wire format.
            // Current behaviour: treat as a colour reset (WHITE/BLACK), which at least
            // avoids colour corruption from unbalanced push/pop sequences.
            case 0xF5:
                Apply(WHITE, BLACK);
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

            // ── C99 (0xFE): arbitrary colour from payload bytes ───────────────
            // {C99}{C99}{C255}    → WHITE/BLACK (Clio telnet.l:1030, non-wireplay)
            // {C99}{fg}{bg}{C255} → colour (fg−155, bg−155)
            // {C99}{fg}{C255}     → colour (fg−155, BLACK)
            // Colour bytes are offset by 0x9B (155); index clamped to 0–15.
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
    /// Data format (from Clio ParseFesPayload, adopted from Mucka):
    /// Strip embedded C99 colour markers (0xFE + 1 byte), then split by space:
    ///   [0]=sta [1]=msta [2]=str [3]=mstr [4]=dex [5]=mdex
    ///   [6]=mag [7]=mmag [8]=score [9]=blind [10]=deaf [11]=crippled
    ///   [12]=dumb [13]=reset(minutes) [14]=weather
    /// At least 15 fields required; score is a long (comma-free on server).
    /// </summary>
    private void ParseAndEmitFes(List<byte> rawBytes)
    {
        if (rawBytes.Count == 0) return;

        // Each numeric field in the FES payload may be prefixed by C1 colour sequences
        // (e.g. C89+args+C255 or C99+1byte) that encode the display colour for that stat.
        // Clio extracts scolour via strchr(yytext,'\376') (= 0xFE, the C99 marker) and then
        // finds the digit start with strpbrk (telnet.l:725-734). We do the same: build a
        // plain-ASCII copy for field splitting by extracting only printable ASCII bytes,
        // while separately capturing the C99 (0xFE + 1-byte) stamina colour hint.
        byte? staColourHint = null;
        var textBytes = new List<byte>(rawBytes.Count);
        for (var i = 0; i < rawBytes.Count; i++)
        {
            var b2 = rawBytes[i];
            if (b2 == 0xFE && i + 1 < rawBytes.Count)
            {
                // C99 colour marker — record first occurrence as the stamina colour hint.
                // The byte following 0xFE is a C1 byte (0x9B + ANSI index); subtract 0x9B
                // to produce the 0–15 ANSI index expected by GameViewModel.AnsiToColor.
                if (staColourHint is null)
                    staColourHint = (byte)Math.Clamp(rawBytes[i + 1] - 0x9B, 0, 15);
                i++; // skip the colour byte; do not emit either byte as text
                continue;
            }
            // Skip other C1 leads (>= 0x9B) and 0xFF (C255 / IAC terminator).
            // These are colour-push sequences that bracket numeric values for display;
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
            StaminaColor: staColourHint
        ) { HasFesStats = true };
        _parser.SetWeather(weather);
        _parser.EmitStatsUpdate(snapshot);
    }
}
