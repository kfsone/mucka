namespace MudSharp.Tests.Fixtures;

/// <summary>
/// Tests for frame-start tracking and the RoomEntered event.
///
/// A "frame" is the content between two prompt sequences. The game '*' prompt marks the
/// end of one frame and the start of the next. A C02+C01 sequence (0x9D 0x9C 0xFF 0xFF)
/// at frame start means the player is at (or has just entered) a room; the same sequence
/// mid-frame indicates exits or look-around output.
///
/// Also covers FEW-response suppression: C12+C08+C05+C255 (0xA7 0xA3 0xA0 0xFF 0xFF)
/// wraps the WHO-list interrupt response. Text within that context is suppressed (not shown
/// to the user) but player names are still captured via FewPlayerReady.
/// </summary>
public class FrameTrackingTests
{
    // Wire-format prompt preamble (same as GamePromptTests)
    private static readonly byte[] WirePromptPreamble =
    [
        0x9C, 0xFF, 0xFF,
        0x9C, 0x9D, 0xFF, 0xFF,
        (byte)'*',
        0xFF, 0xFF,
        0xFF, 0xFF,
    ];

    private static ParserHarness InGameMode()
    {
        var h = new ParserHarness();
        h.Feed(0x9D, 0x9C, 0xFF, 0xFF);
        h.Lines.Clear();
        return h;
    }

    private static byte[] WithPrompt(string text)
        => [..System.Text.Encoding.Latin1.GetBytes(text), ..WirePromptPreamble];

    // C02+C01 = room-short sequence: 0x9D 0x9C 0xFF 0xFF
    private static readonly byte[] RoomShort = [0x9D, 0x9C, 0xFF, 0xFF];

    // ── Frame start tracking ──────────────────────────────────────────────────

    [Fact]
    public void RoomEntered_FiresWhenRoomShortFollowsPrompt()
    {
        var h = InGameMode();
        h.Feed(WithPrompt("You look around.\n"));
        h.Feed(RoomShort);
        Assert.Equal(1, h.RoomEnteredCount);
    }

    [Fact]
    public void RoomEntered_NotFiredAtGameModeEntry()
    {
        // The initial 0x9D 0x9C that enters game mode is NOT a frame-start room short.
        var h = new ParserHarness();
        h.Feed(0x9D, 0x9C, 0xFF, 0xFF);
        Assert.Equal(0, h.RoomEnteredCount);
    }

    [Fact]
    public void RoomEntered_NotFiredMidFrame()
    {
        // C02+C01 that arrives without a preceding game prompt is mid-frame exits/look-around.
        var h = InGameMode();
        h.Feed("Some game text\n");
        h.Feed(RoomShort);
        Assert.Equal(0, h.RoomEnteredCount);
    }

    [Fact]
    public void RoomEntered_NotFiredAfterSuppressedPrompt()
    {
        // A bare wire prompt (FES heartbeat — no '\n' in packet) is suppressed; it must NOT
        // set the frame-start flag, so a subsequent room short fires no RoomEntered.
        // Use WithPrompt to show a real prompt first (PromptAllowed=false, _atFrameStart=true),
        // then feed a bare prompt preamble — no preceding '\n' so PromptAllowed stays false
        // and the prompt is suppressed, no SetFrameStart. The first C01 dispatch inside
        // the heartbeat packet clears the existing frame-start flag, so RoomShort is mid-frame.
        var h = InGameMode();
        h.Feed(WithPrompt("You gain a point.\n")); // real prompt → shown, PromptAllowed=false
        h.Feed(WirePromptPreamble);               // FES heartbeat: bare prompt → suppressed, no SetFrameStart
        h.Feed(RoomShort);                        // _atFrameStart cleared by heartbeat → must NOT fire
        Assert.Equal(0, h.RoomEnteredCount);
    }

    [Fact]
    public void RoomEntered_FiresOnlyOncePerPrompt()
    {
        // After the prompt (SetFrameStart), the first Dispatch clears the flag.
        // A second room short in the same frame must not fire again.
        var h = InGameMode();
        h.Feed(WithPrompt("You enter the room.\n"));
        h.Feed(RoomShort);  // frame start → fires
        h.Feed(RoomShort);  // mid-frame → does not fire
        Assert.Equal(1, h.RoomEnteredCount);
    }

    [Fact]
    public void RoomEntered_FiresAgainAfterNextPrompt()
    {
        // Each real game prompt resets the frame-start flag.
        var h = InGameMode();
        h.Feed(WithPrompt("Content.\n"));
        h.Feed(RoomShort);  // frame 1 → fires
        h.Feed(WithPrompt("More content.\n"));
        h.Feed(RoomShort);  // frame 2 → fires again
        Assert.Equal(2, h.RoomEnteredCount);
    }

    [Fact]
    public void RoomEntered_NotFiredWhenTextArrivesFirstAfterPrompt()
    {
        // If plain text arrives before a C02+C01, we're already into frame content;
        // the room short is mid-frame (exits/look-around), not a "where you are".
        var h = InGameMode();
        h.Feed(WithPrompt("You attack.\n"));
        h.Feed("You miss!\n");  // text clears frame-start flag
        h.Feed(RoomShort);
        Assert.Equal(0, h.RoomEnteredCount);
    }

    [Fact]
    public void RoomEntered_NotFiredWhenOtherC1ArrivesFirstAfterPrompt()
    {
        // A non-C02+C01 dispatch at frame start clears the flag but fires no event.
        var h = InGameMode();
        h.Feed(WithPrompt("You look around.\n"));
        h.Feed(0xA0, 0x9B, 0xFF, 0xFF);  // C05+C00 (some other color)
        h.Feed(RoomShort);               // now mid-frame — no event
        Assert.Equal(0, h.RoomEnteredCount);
    }

    // ── FEW response suppression ──────────────────────────────────────────────

    // C12+C08+C05+C255 — opens the FEW (WHO-list interrupt) response context
    private static readonly byte[] FewContextOpen = [0xA7, 0xA3, 0xA0, 0xFF, 0xFF];

    // C04+C00+C06+C255 — WHO-list mortal player name follows (RED color)
    private static readonly byte[] FewPlayerPrefix = [0x9F, 0x9B, 0xA1, 0xFF, 0xFF];

    // C05+C00+C06+C255 — WHO-list mortal player name follows (RED color)
    private static readonly byte[] FewPlayerRedPrefix = [0xA0, 0x9B, 0xA1, 0xFF, 0xFF];

    [Fact]
    public void FewResponse_PlayerNamesEmittedAsFewPlayerReady()
    {
        // C12+C08+C05 opens context; names inside fire FewPlayerReady regardless.
        var h = InGameMode();
        h.Feed(FewContextOpen);
        h.Feed(FewPlayerRedPrefix);
        h.Feed("Gandalf");         // player name bytes; terminated by next C1 or non-ASCII
        h.Feed(0xFF, 0xFF);        // end of player-name subcontext (pop)
        Assert.Contains("Gandalf", h.FewPlayers);
    }

    [Fact]
    public void FewResponse_PlayerNamesNotEmittedAsLines()
    {
        // While inside C12+C08+C05 context, display output is suppressed.
        var h = InGameMode();
        var linesBefore = h.Lines.Count;
        h.Feed(FewContextOpen);
        h.Feed(FewPlayerRedPrefix);
        h.Feed("Gandalf");
        h.Feed(0xFF, 0xFF);
        Assert.Equal(linesBefore, h.Lines.Count);
    }

    [Fact]
    public void FewResponse_FewListStartingFiredOnContextOpen()
    {
        var h = InGameMode();
        h.Feed(FewContextOpen);
        Assert.Equal(1, h.FewListStartingCount);
    }

    [Fact]
    public void FewResponse_MultiplePlayersAllCaptured()
    {
        var h = InGameMode();
        h.Feed(FewContextOpen);
        h.Feed(FewPlayerRedPrefix);
        h.Feed("Alice");
        h.Feed(0xFF, 0xFF);
        h.Feed(FewPlayerRedPrefix);
        h.Feed("Bob");
        h.Feed(0xFF, 0xFF);
        Assert.Contains("Alice", h.FewPlayers);
        Assert.Contains("Bob", h.FewPlayers);
    }

    [Fact]
    public void FewResponse_AfterContextCloses_DisplayResumes()
    {
        // After the FEW context stack unwinds, normal display resumes.
        var h = InGameMode();
        h.Feed(FewContextOpen);
        h.Feed(0xFF, 0xFF);  // pop C05 → stack returns to entry depth → context exits
        h.Feed(0xFF, 0xFF);  // pop C12+C08 wrapper
        h.Lines.Clear();
        h.Feed("Normal text\n");
        Assert.Single(h.Lines);
    }

    // ── FewListComplete ───────────────────────────────────────────────────────

    [Fact]
    public void FewListComplete_FiredWhenContextCloses()
    {
        var h = InGameMode();
        h.Feed(FewContextOpen);
        Assert.Equal(0, h.FewListCompleteCount);
        h.Feed(0xFF, 0xFF);  // pop back to entry depth → ExitFewContext → FewListComplete
        Assert.Equal(1, h.FewListCompleteCount);
    }

    [Fact]
    public void FewListComplete_FiredAfterAllNamesDelivered()
    {
        // FewListComplete must come AFTER all FewPlayerReady events for the same response.
        // Each player name is wrapped in its own color push+pop (C05+C00+C06 / C255).
        // Those pops only unwind the per-name color entry, not the outer C12+C08+C05 wrapper.
        // A final C255 (0xFF 0xFF) pops the WHITE/BLACK pushed by FewContextOpen, which
        // returns the stack to its entry depth and fires ExitFewContext → FewListComplete.
        var h = InGameMode();
        var completeSeenAfterNames = false;
        h.Parser.FewListComplete += () =>
            completeSeenAfterNames = h.FewPlayers.Count > 0;

        h.Feed(FewContextOpen);
        h.Feed(FewPlayerRedPrefix);
        h.Feed("Alice");
        h.Feed(0xFF, 0xFF);  // ends player data, pops per-name color — context still open
        h.Feed(FewPlayerRedPrefix);
        h.Feed("Bob");
        h.Feed(0xFF, 0xFF);  // ends player data, pops per-name color — context still open
        h.Feed(0xFF, 0xFF);  // pops FEW context wrapper (WHITE/BLACK) → ExitFewContext → Complete

        Assert.True(completeSeenAfterNames);
        Assert.Equal(2, h.FewPlayers.Count);
    }

    [Fact]
    public void FewListComplete_FiredOncePerResponse()
    {
        // Opening and closing the context twice should fire Complete twice.
        var h = InGameMode();
        h.Feed(FewContextOpen);
        h.Feed(0xFF, 0xFF);
        h.Feed(FewContextOpen);
        h.Feed(0xFF, 0xFF);
        Assert.Equal(2, h.FewListCompleteCount);
    }

    [Fact]
    public void FewListComplete_NotFiredWithoutFewContext()
    {
        // Bare pops outside of a FEW context must not trigger FewListComplete.
        var h = InGameMode();
        h.Feed(0x9B, 0xFF, 0xFF);   // C00+C255 → push/pop cycle, no FEW context
        h.Feed(0xFF, 0xFF);
        Assert.Equal(0, h.FewListCompleteCount);
    }
}
