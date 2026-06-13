namespace MudSharp.Tests.Fixtures;

/// <summary>
/// Tests for the FES/FEW heartbeat response as the server sends it to a NARROW terminal
/// (reconstructed byte-for-byte from a live Pixel 5 capture, 2026-06-12 logcat).
///
/// Two server behaviours only seen at narrow widths:
///   1. The FES data line (~51 visible chars) is hard-wrapped mid-line ("\r\0\r\n").
///   2. The FES line ends with a bare CR ("\r\0", telnet CR-NUL) and NO newline — the
///      C255 pop, prompt container and FEW response follow immediately. Terminating
///      FES collection only at '\n' swallowed all of those into the FES buffer, so the
///      FEW context never opened: names leaked as display text (or, suppressed, their
///      newlines leaked as blank lines), and the eaten prompt broke PromptAllowed gating.
/// </summary>
public class NarrowTerminalHeartbeatTests
{
    private static readonly byte[] WirePromptPreamble =
    [
        0x9C, 0xFF, 0xFF,
        0x9C, 0x9D, 0xFF, 0xFF,
        (byte)'*',
        0xFF, 0xFF,
        0xFF, 0xFF,
    ];

    // FES opener: C12+C08+C01+C255
    private static readonly byte[] FesOpen = [0xA7, 0xA3, 0x9C, 0xFF, 0xFF];

    // FEW context open: C12+C08+C05+C255
    private static readonly byte[] FewContextOpen = [0xA7, 0xA3, 0xA0, 0xFF, 0xFF];

    // C05+C00+C06+C255 -- WHO-list mortal player name follows
    private static readonly byte[] FewPlayerRedPrefix = [0xA0, 0x9B, 0xA1, 0xFF, 0xFF];

    // Telnet bare CR (CR NUL) and full line ending (CR NUL CR LF) as MUD2 sends them.
    private static readonly byte[] BareCr = [(byte)'\r', 0x00];
    private static readonly byte[] CrNulCrLf = [(byte)'\r', 0x00, (byte)'\r', (byte)'\n'];

    private static ParserHarness InGameModeAfterPrompt()
    {
        var h = new ParserHarness();
        h.Feed(0x9D, 0x9C, 0xFF, 0xFF);   // enter game mode
        h.Feed("setup\n");
        h.Feed(0x9B, 0xFF, 0xFF);          // C00: reset color stack baseline
        h.Feed(WirePromptPreamble);        // show the frame prompt: PromptAllowed -> false
        h.ClearCounters();
        return h;
    }

    /// <summary>
    /// The full unified-probe heartbeat response as captured: wrapped FES line ending in
    /// a bare CR, then pop, prompt container, FEW context with four players, two closing pops.
    /// </summary>
    private static void FeedCapturedHeartbeat(ParserHarness h)
    {
        h.Feed(FesOpen);
        // Per-field colour prefixes for the first two stats, as captured:
        // C89 variant + C99 colour + "105" + two pops + space, twice.
        h.Feed(0xF4, 0x9B, 0x9B, 0xFF, 0xFF, 0xFE, 0xA5, 0xFF, 0xFF);
        h.Feed("105");
        h.Feed(0xFF, 0xFF, 0xFF, 0xFF);
        h.Feed(" ");
        h.Feed(0xF4, 0x9B, 0x9C, 0xFF, 0xFF, 0xFE, 0xA5, 0xFF, 0xFF);
        h.Feed("105");
        h.Feed(0xFF, 0xFF, 0xFF, 0xFF);
        h.Feed(" ");
        h.Feed("100 100 100 100 105 105 31790 N N");
        h.Feed(CrNulCrLf);                 // server wrap: narrow terminal, mid-FES-line
        h.Feed("N N 7 F");
        h.Feed(BareCr);                    // end of FES line: bare CR, NO newline
        h.Feed(0xFF, 0xFF);                // pop
        h.Feed(WirePromptPreamble);        // heartbeat prompt (must be discarded)
        h.Feed(FewContextOpen);
        h.Feed(0xA7, 0x9E, 0xFF, 0xFF);    // C12+C03, as captured inside the FEW block
        foreach (var name in CapturedNames)
        {
            h.Feed(FewPlayerRedPrefix);
            h.Feed(name);
            h.Feed(0xFF, 0xFF);            // pop the name colour
            h.Feed(CrNulCrLf);             // narrow terminal: each name line is newline-terminated
        }
        h.Feed(0xFF, 0xFF);                // pop C12+C03
        h.Feed(0xFF, 0xFF);                // pop FEW context -> FewListComplete
    }

    private static readonly string[] CapturedNames =
    [
        "Ollie the necromancer",
        "Cliollipol the protector",
        "Atomicbob the warlock",
        "Drizzle the wobbly mage",
    ];

    [Fact]
    public void NarrowHeartbeat_ParsesFesStats()
    {
        var h = InGameModeAfterPrompt();
        FeedCapturedHeartbeat(h);
        Assert.Single(h.Stats);
        var s = h.Stats[0];
        Assert.Equal(105,   s.Stamina);
        Assert.Equal(105,   s.MaxStamina);
        Assert.Equal(100,   s.Strength);
        Assert.Equal(100,   s.Dexterity);
        Assert.Equal(105,   s.CurrentMagic);
        Assert.Equal(31790, s.Score);
        Assert.Equal(7,     s.TimeToReset);
        Assert.Equal('F',   s.Weather);
    }

    [Fact]
    public void NarrowHeartbeat_FewContextOpensAndAllNamesCaptured()
    {
        var h = InGameModeAfterPrompt();
        FeedCapturedHeartbeat(h);
        Assert.Equal(1, h.FewListStartingCount);
        Assert.Equal(1, h.FewListCompleteCount);
        Assert.Equal(CapturedNames, h.FewPlayers);
    }

    [Fact]
    public void NarrowHeartbeat_EmitsNoLines()
    {
        // The whole heartbeat is invisible: no blank lines from the per-name newlines,
        // no leaked names, and the heartbeat prompt is discarded (PromptAllowed=false).
        var h = InGameModeAfterPrompt();
        FeedCapturedHeartbeat(h);
        Assert.Empty(h.Lines);
    }

    [Fact]
    public void NarrowHeartbeat_RepeatedHeartbeatsStaySilent()
    {
        // The suppressed FEW newlines must not tick PromptAllowed, or the NEXT
        // heartbeat's prompt displays as a stray '*' every probe interval.
        var h = InGameModeAfterPrompt();
        FeedCapturedHeartbeat(h);
        FeedCapturedHeartbeat(h);
        FeedCapturedHeartbeat(h);
        Assert.Empty(h.Lines);
        Assert.Equal(3, h.Stats.Count);
        Assert.Equal(3, h.FewListCompleteCount);
    }

    [Fact]
    public void NarrowHeartbeat_RealPromptStillDisplaysAfterGameOutput()
    {
        var h = InGameModeAfterPrompt();
        FeedCapturedHeartbeat(h);
        h.Feed("You feel rested.");
        h.Feed(CrNulCrLf);                 // real game output -> PromptAllowed=true
        h.Feed(WirePromptPreamble);
        Assert.Equal(2, h.Lines.Count);
        Assert.Equal("You feel rested.", h.Lines[0].PlainText);
        Assert.Equal("*", h.Lines[1].PlainText);
    }

    [Fact]
    public void FesLine_FullCrLfEnding_DoesNotEatFollowingText()
    {
        // Wide-terminal ending "\r\0\r\n": the tail absorber must stop at the '\n'.
        var h = new ParserHarness();
        h.Feed(FesOpen);
        h.Feed("81 81 94 94 95 95 50 50 1785 N N N N 5 S");
        h.Feed(CrNulCrLf);
        h.Feed("hello\n");
        Assert.Single(h.Stats);
        Assert.Equal(81, h.Stats[0].Stamina);
        Assert.Single(h.Lines);
        Assert.Equal("hello", h.Lines[0].PlainText);
    }

    [Fact]
    public void FesLine_WrapWithoutNul_StillSplitsAndParses()
    {
        // Defensive: if a wrap ever arrives as plain CRLF (no telnet CR-NUL), the
        // dropped line ending must still separate the adjacent fields, or the fields
        // merge and FES collection wedges, swallowing subsequent output.
        var h = new ParserHarness();
        h.Feed(FesOpen);
        h.Feed("81 81 94 94 95 95 50 50 1785 N N");
        h.Feed("\r\n");
        h.Feed("N N 5 S");
        h.Feed(CrNulCrLf);
        h.Feed("hello\n");
        Assert.Single(h.Stats);
        Assert.Equal(81,  h.Stats[0].Stamina);
        Assert.Equal(5,   h.Stats[0].TimeToReset);
        Assert.Equal('S', h.Stats[0].Weather);
        Assert.Single(h.Lines);
        Assert.Equal("hello", h.Lines[0].PlainText);
    }

    // -- The visible WHO list (login "Players:" section, manual qw/who) ----------
    // Outside a FEW response context the WHO-list colour codes bracket names that are
    // part of the normal display output: they must be shown AND captured.

    [Fact]
    public void LoginPlayersList_NamesAreDisplayed()
    {
        var h = new ParserHarness();
        h.Feed("Players:");
        h.Feed(CrNulCrLf);
        h.Feed(FewPlayerRedPrefix);
        h.Feed("Cliollipol the protector");
        h.Feed(0xFF, 0xFF);
        h.Feed(CrNulCrLf);
        h.Feed(FewPlayerRedPrefix);
        h.Feed("Atomicbob the warlock");
        h.Feed(0xFF, 0xFF);
        h.Feed(CrNulCrLf);
        Assert.Equal(3, h.Lines.Count);
        Assert.Equal("Players:", h.Lines[0].PlainText);
        Assert.Equal("Cliollipol the protector", h.Lines[1].PlainText);
        Assert.Equal("Atomicbob the warlock", h.Lines[2].PlainText);
    }

    [Fact]
    public void LoginPlayersList_NamesAlsoCapturedForWhoPanel()
    {
        var h = new ParserHarness();
        h.Feed(FewPlayerRedPrefix);
        h.Feed("Cliollipol the protector");
        h.Feed(0xFF, 0xFF);
        h.Feed(CrNulCrLf);
        Assert.Equal(["Cliollipol the protector"], h.FewPlayers);
    }
}
