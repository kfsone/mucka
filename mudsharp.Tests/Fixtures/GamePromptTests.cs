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
        // After a newline PromptAllowed=true; the first wire prompt should appear as
        // a partial line (the visible '*' game prompt).
        var h = InGameMode();
        h.Feed("You arrive in the tearoom.\n");
        h.Lines.Clear();
        h.Feed(WirePromptPreamble);
        Assert.Single(h.Lines);
        Assert.True(h.Lines[0].IsPartial);
        Assert.Equal("*", string.Concat(h.Lines[0].Spans.Select(s => s.Text)));
    }

    [Fact]
    public void WirePrompt_SecondInSameFrame_Suppressed_NoLine()
    {
        // After the first wire prompt, PromptAllowed=false; subsequent ones must be swallowed.
        var h = InGameMode();
        h.Feed("You arrive in the tearoom.\n");
        h.Lines.Clear();
        h.Feed(WirePromptPreamble); // first → partial '*' emitted
        h.Lines.Clear();
        h.Feed(WirePromptPreamble); // second → suppress
        Assert.Empty(h.Lines);
    }

    [Fact]
    public void WirePrompt_DoesNotLeakIntoNextRealLine()
    {
        // Core regression: '*' from the wire prompt must NOT appear at the start of the
        // next game line.  Before this fix, spans accumulated and were prepended.
        var h = InGameMode();
        h.Feed("You arrive in the tearoom.\n");
        h.Lines.Clear();
        h.Feed(WirePromptPreamble); // emit '*' as partial, clear spans
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
        h.Feed("You arrive in the tearoom.\n");
        h.Lines.Clear();
        h.Feed(WirePromptPreamble); // first
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
        // wire prompt must again be shown as a partial.
        var h = InGameMode();
        h.Feed("You arrive in the tearoom.\n");
        h.Lines.Clear();
        h.Feed(WirePromptPreamble); // first → partial
        h.Feed(WirePromptPreamble); // second → suppressed
        h.Feed("OK, you wave.\n");  // newline resets PromptAllowed=true
        h.Lines.Clear();
        h.Feed(WirePromptPreamble); // next frame's prompt → partial again
        Assert.Single(h.Lines);
        Assert.True(h.Lines[0].IsPartial);
    }
}
