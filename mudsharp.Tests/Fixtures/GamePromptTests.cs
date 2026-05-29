using MudSharp.Models;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// Tests for the game-prompt preamble suppression:
/// all-asterisk lines (prompt preamble separators) that arrive with a trailing newline
/// in game mode must be fully suppressed — no LineReady event fires. This mirrors
/// Clio's prompt_allowed state machine (telnet.l:438-444).
///
/// Also tests the wire-format C01-based prompt preamble
/// ({C01}{C255}{C01}{C02}{C255}*{C255}{C255}) — which arrives WITHOUT a trailing
/// newline — so the '*' must never accumulate in the span buffer and leak into the
/// next real game line.
///
/// The emission rule: a wire prompt is shown only when the same received TCP packet
/// (Feed() call) contains a '\n' before the preamble. FES heartbeat responses arrive
/// as a bare prompt preamble (no '\n') and are always suppressed.
/// </summary>
public class GamePromptTests
{
    // Helper: enter game mode (0x9D 0x9C 0xFF 0xFF)
    private static ParserHarness InGameMode()
    {
        var h = new ParserHarness();
        h.Feed(0x9D, 0x9C, 0xFF, 0xFF);
        h.Lines.Clear();
        return h;
    }

    // Wire-format prompt preamble: C01+C255, C01+C02+C255, '*', C255, C255
    // Matches the server packet observed in session captures:
    //   0x9C 0xFF 0xFF  0x9C 0x9D 0xFF 0xFF  *  0xFF 0xFF  0xFF 0xFF
    private static readonly byte[] WirePromptPreamble =
    [
        0x9C, 0xFF, 0xFF,        // C01+C255  → BLUE push
        0x9C, 0x9D, 0xFF, 0xFF, // C01+C02+C255 → LT_BLUE push, game-mode
        (byte)'*',              // prompt char
        0xFF, 0xFF,             // C255 → pop LT_BLUE
        0xFF, 0xFF,             // C255 → pop BLUE
    ];

    /// <summary>
    /// Return a single byte[] combining Latin-1 encoded <paramref name="text"/> with the
    /// wire prompt preamble. This simulates a server packet that contains game output
    /// followed immediately by the prompt (the normal case: same TCP segment).
    /// </summary>
    private static byte[] WithPrompt(string text)
        => [..System.Text.Encoding.Latin1.GetBytes(text), ..WirePromptPreamble];

    [Fact]
    public void AllAsteriskLine_InGameMode_Suppressed()
    {
        var h = InGameMode();
        h.Feed("*\n");
        Assert.Empty(h.Lines);
    }

    [Fact]
    public void MultipleAsterisksNewline_InGameMode_Suppressed()
    {
        var h = InGameMode();
        h.Feed("*********\n");
        Assert.Empty(h.Lines);
    }

    [Fact]
    public void RepeatedAsterisksPrompts_InGameMode_BothSuppressed()
    {
        var h = InGameMode();
        h.Feed("*\n");
        h.Feed("*\n");
        Assert.Empty(h.Lines);
    }

    [Fact]
    public void AsterisksNewline_NotInGameMode_EmittedAsComplete()
    {
        // Before game mode is entered the * lines are NOT game prompts — display normally.
        var h = new ParserHarness();
        h.Feed("*\n");
        Assert.Single(h.Lines);
        Assert.False(h.Lines[0].IsPartial);
    }

    [Fact]
    public void AsteriskMixedLine_InGameMode_NotSuppressed()
    {
        var h = InGameMode();
        h.Feed("**wave\n");
        Assert.Single(h.Lines);
        Assert.False(h.Lines[0].IsPartial);
    }

    [Fact]
    public void MixedAsterisksAndText_InGameMode_EmittedAsComplete()
    {
        var h = InGameMode();
        h.Feed("*****You feel stronger.\n");
        Assert.Single(h.Lines);
        Assert.False(h.Lines[0].IsPartial);
    }

    [Fact]
    public void NormalGameLine_InGameMode_EmittedAsComplete()
    {
        var h = InGameMode();
        h.Feed("You see a wizard here.\n");
        Assert.Single(h.Lines);
        Assert.False(h.Lines[0].IsPartial);
    }

    [Fact]
    public void EmptyLineNewline_InGameMode_EmittedAsComplete()
    {
        // An empty line (just \n) has no spans — SpansAreAllAsterisks returns false.
        var h = InGameMode();
        h.Feed("\n");
        Assert.Single(h.Lines);
        Assert.False(h.Lines[0].IsPartial);
    }

    [Fact]
    public void AsterisksNewlineAfterGameLine_InGameMode_OnlyGameLineEmitted()
    {
        var h = InGameMode();
        h.Feed("You see a wizard here.\n");
        h.Feed("*\n");
        Assert.Single(h.Lines);
        Assert.False(h.Lines[0].IsPartial);
    }

    // ── Wire-format C01-based prompt preamble tests ───────────────────────────

    [Fact]
    public void WirePrompt_FirstAfterNewline_EmittedAsPartial()
    {
        // Newline and wire prompt arrive in the same TCP packet — the prompt is shown.
        var h = InGameMode();
        h.Feed(WithPrompt("You arrive in the tearoom.\n"));
        var line = Assert.Single(h.Lines, l => l.IsPartial);
        Assert.Equal("*", string.Concat(line.Spans.Select(s => s.Text)));
    }

    [Fact]
    public void WirePrompt_SecondInSameFrame_Suppressed_NoLine()
    {
        // After the first wire prompt, PromptAllowed=false; subsequent ones must be swallowed.
        var h = InGameMode();
        h.Feed(WithPrompt("You arrive in the tearoom.\n")); // first → partial '*' emitted
        h.Lines.Clear();
        h.Feed(WirePromptPreamble); // second (bare, no '\n') → suppress
        Assert.Empty(h.Lines);
    }

    [Fact]
    public void WirePrompt_DoesNotLeakIntoNextRealLine()
    {
        // Core regression: '*' from the wire prompt must NOT appear at the start of the
        // next game line.  Before this fix, spans accumulated and were prepended.
        var h = InGameMode();
        h.Feed(WithPrompt("You arrive in the tearoom.\n")); // emit '*' as partial, clear spans
        h.Lines.Clear();
        h.Feed("OK, you wave.\n");  // real game output
        Assert.Single(h.Lines);
        var text = string.Concat(h.Lines[0].Spans.Select(s => s.Text));
        Assert.DoesNotContain("*", text);
        Assert.Contains("OK, you wave.", text);
    }

    [Fact]
    public void WirePrompt_Suppressed_DoesNotLeakIntoNextRealLine()
    {
        // Same regression check but via the suppressed (second) prompt path.
        var h = InGameMode();
        h.Feed(WithPrompt("You arrive in the tearoom.\n")); // first
        h.Feed(WirePromptPreamble); // second → suppressed
        h.Lines.Clear();
        h.Feed("Drizzle the wobbly mage arrives.\n");
        Assert.Single(h.Lines);
        var text = string.Concat(h.Lines[0].Spans.Select(s => s.Text));
        Assert.DoesNotContain("*", text);
        Assert.Contains("Drizzle", text);
    }

    [Fact]
    public void WirePrompt_NewlineResetsAllowed_PromptVisibleAgain()
    {
        // After a real game line (which emits \n and resets PromptAllowed), the next
        // wire prompt in the same packet must again be shown as a partial.
        var h = InGameMode();
        h.Feed(WithPrompt("You arrive in the tearoom.\n")); // first → partial
        h.Feed(WirePromptPreamble); // second → suppressed
        h.Lines.Clear();
        h.Feed(WithPrompt("OK, you wave.\n")); // newline + prompt in same packet → partial again
        var line = Assert.Single(h.Lines, l => l.IsPartial);
        Assert.Equal("*", string.Concat(line.Spans.Select(s => s.Text)));
    }

    // ── FES heartbeat suppression tests ──────────────────────────────────────

    [Fact]
    public void WirePrompt_FesHeartbeat_BarePromptPacket_Suppressed()
    {
        // FES heartbeat scenario: the last wire prompt was shown (PromptAllowed=false).
        // A subsequent bare wire prompt preamble — no preceding '\n' in this packet — must
        // be suppressed because PromptAllowed is still false from the last shown prompt.
        var h = InGameMode();
        h.Feed(WithPrompt("You arrive in the tearoom.\n")); // first prompt → shown, PromptAllowed=false
        h.Lines.Clear();
        h.Feed(WirePromptPreamble); // FES heartbeat: no '\n' before preamble → suppress
        Assert.DoesNotContain(h.Lines, l => l.IsPartial);
    }

    [Fact]
    public void WirePrompt_FesHeartbeat_DoesNotConsumePromptAllowed()
    {
        // A suppressed FES heartbeat must not consume PromptAllowed — the next real game
        // packet containing '\n' and a wire prompt in the same segment must still show the prompt.
        var h = InGameMode();
        h.Feed(WithPrompt("You arrive in the tearoom.\n")); // first prompt → shown, PromptAllowed=false
        h.Lines.Clear();
        h.Feed(WirePromptPreamble); // FES heartbeat → suppressed
        h.Lines.Clear();
        h.Feed(WithPrompt("You scored 42 points.\n")); // '\n' re-allows; prompt in same packet → shown
        var line = Assert.Single(h.Lines, l => l.IsPartial);
        Assert.Equal("*", string.Concat(line.Spans.Select(s => s.Text)));
    }

    [Fact]
    public void WirePrompt_FesHeartbeat_DoesNotLeakAsteriskIntoNextLine()
    {
        // Suppressed FES heartbeat must not leave '*' in the span buffer.
        var h = InGameMode();
        h.Feed(WithPrompt("You arrive in the tearoom.\n")); // first prompt
        h.Lines.Clear();
        h.Feed(WirePromptPreamble); // FES heartbeat → suppressed
        h.Lines.Clear();
        h.Feed("You scored 42 points.\n");
        Assert.Single(h.Lines);
        var text = string.Concat(h.Lines[0].Spans.Select(s => s.Text));
        Assert.DoesNotContain("*", text);
        Assert.Contains("42 points", text);
    }

    [Fact]
    public void WirePrompt_AfterPrompt_NextTextIsNotPromptColour()
    {
        // After the wire prompt's two C1 pops complete, subsequent text must not inherit
        // the prompt's BLUE or LT_BLUE colour.  The PopColour() stack unwind must restore
        // either the pre-prompt colour (if any) or TextStyle.Default (if the stack is empty),
        // never stale prompt blue.
        var h = InGameMode();   // pushes LT_GREEN (BrightGreen) as base game-mode colour
        h.Feed(WithPrompt("You arrive in the tearoom.\n"));
        h.Lines.Clear();
        h.Feed("go north\r\n");
        var line = Assert.Single(h.Lines);
        Assert.All(line.Spans, span => {
            Assert.NotEqual(AnsiColor.Blue,       span.Style.Foreground);
            Assert.NotEqual(AnsiColor.BrightBlue, span.Style.Foreground);
        });
    }
}
