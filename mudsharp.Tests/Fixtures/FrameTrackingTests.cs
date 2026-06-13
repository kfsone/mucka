namespace MudSharp.Tests.Fixtures;

/// <summary>
/// Tests for line-start tracking and the RoomEntered / RoomShortReady events.
///
/// The MUD2 room-short rule (Clio telnet.l:1218-1226): bold+GREEN (C02+C01) at column 0
/// of a display line = the player is in this room. Column 0 means no text characters have
/// been output on that line yet. Pure C1 color sequences do not advance the column.
///
/// "At line start" is set:
///   - When the game '*' prompt partial line is emitted (SetLineStart from PopColor).
///   - After each real '\n' line is emitted.
/// "At line start" is cleared:
///   - When any text character is appended (_atLineStart = false).
///   - When C02+C01 consumes line start (ClearLineStart), preventing double-fire.
///
/// Contrast with "look around" or mid-sentence room names, where the room short C02+C01
/// arrives mid-line (text precedes it without a newline) -- those do not fire RoomEntered.
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
        h.Feed(0x9D, 0x9C, 0xFF, 0xFF);  // Enter game mode (fires RoomEntered, sets _pendingRoomShort)
        h.Feed("setup\n");                 // flush _pendingRoomShort (fires RoomShortReady), restore _atLineStart
        h.Feed(0x9B, 0xFF, 0xFF);          // C00: init_stack -> reset color to WHITE/BLACK
        h.ClearCounters();                 // discard setup noise so tests start from clean state
        return h;
    }

    private static byte[] WithPrompt(string text)
        => [..System.Text.Encoding.Latin1.GetBytes(text), ..WirePromptPreamble];

    // C02+C01 = room-short sequence: 0x9D 0x9C 0xFF 0xFF
    private static readonly byte[] RoomShort = [0x9D, 0x9C, 0xFF, 0xFF];

    // -- RoomEntered: line-start tracking -------------------------------------

    [Fact]
    public void RoomEntered_FiresWhenRoomShortFollowsPrompt()
    {
        var h = InGameMode();
        h.Feed(WithPrompt("You look around.\n"));
        h.Feed(RoomShort);
        Assert.Equal(1, h.RoomEnteredCount);
    }

    [Fact]
    public void RoomEntered_FiredAtGameModeEntry()
    {
        // The first C02+C01 from the server establishes game mode AND is the room-short
        // line for the room the player logged into. RoomEntered fires immediately,
        // and RoomShortReady fires when the '\n' for that line arrives.
        var h = new ParserHarness();
        h.Feed(0x9D, 0x9C, 0xFF, 0xFF);
        Assert.Equal(1, h.RoomEnteredCount);
    }

    [Fact]
    public void RoomEntered_FiredAtLineStartMidFrame()
    {
        // A room short that arrives at the start of a NEW LINE mid-frame is a valid
        // room transition. E.g.: "It's light enough to see now!\n" followed by the
        // room short line -- the room short is at column 0 of the second line.
        var h = InGameMode();
        h.Feed(WithPrompt("Some text.\n"));
        // "Some game text\n": 'S' clears _atLineStart, '\n' restores it to true
        h.Feed("Some game text\n");
        h.Feed(RoomShort);  // at column 0 of new line -> must fire
        Assert.Equal(1, h.RoomEnteredCount);
    }

    [Fact]
    public void RoomEntered_NotFiredForMidLineRoomShort()
    {
        // A room short that arrives AFTER text on the same line (no preceding \n)
        // is a mid-line mention (e.g. "look around" exit descriptions).
        var h = InGameMode();
        h.Feed(WithPrompt("You attack.\n"));
        h.Feed(System.Text.Encoding.Latin1.GetBytes("You see "));  // text, no \n -> _atLineStart=false
        h.Feed(RoomShort);  // mid-line -> must NOT fire
        Assert.Equal(0, h.RoomEnteredCount);
    }

    [Fact]
    public void RoomEntered_FiresOnlyOncePerLine()
    {
        // C02+C01 consumes _atLineStart (ClearLineStart); a second consecutive
        // C02+C01 with no text or \n between them does not re-fire.
        var h = InGameMode();
        h.Feed(WithPrompt("You enter the room.\n"));
        h.Feed(RoomShort);  // line start -> fires, clears _atLineStart
        h.Feed(RoomShort);  // _atLineStart still false -> no fire
        Assert.Equal(1, h.RoomEnteredCount);
    }

    [Fact]
    public void RoomEntered_FiresAgainAfterNextPrompt()
    {
        // Each real game prompt (via SetLineStart) restores _atLineStart for the next line.
        var h = InGameMode();
        h.Feed(WithPrompt("Content.\n"));
        h.Feed(RoomShort);  // frame 1 -> fires
        h.Feed(WithPrompt("More content.\n"));
        h.Feed(RoomShort);  // frame 2 -> fires again
        Assert.Equal(2, h.RoomEnteredCount);
    }

    [Fact]
    public void RoomEntered_FiresForRoomShortAfterSuppressedPrompt()
    {
        // A suppressed FES heartbeat prompt is transparent to _atLineStart (no text
        // is emitted). A room short following it sees _atLineStart=true (still at
        // line start from the preceding real \n) and fires.
        var h = InGameMode();
        h.Feed(WithPrompt("You gain a point.\n")); // real prompt -> shown
        h.Feed(WirePromptPreamble);                // FES heartbeat: suppressed, no text -> transparent
        h.Feed(RoomShort);                         // _atLineStart still true -> fires
        Assert.Equal(1, h.RoomEnteredCount);
    }

    [Fact]
    public void RoomEntered_FiredWhenC00InitStackPrecedesRoomShort()
    {
        // Clio sends C00+C255 (init_stack) at the start of every game-output frame,
        // immediately before the room short C02+C01+C255. C1 sequences do not advance
        // the display column, so _atLineStart remains true for the room short.
        var h = InGameMode();
        h.Feed(WithPrompt("You go north.\n"));
        h.Feed(0x9B, 0xFF, 0xFF);  // C00 (init_stack) -- transparent, preserves _atLineStart
        h.Feed(RoomShort);         // C02+C01 at line start -> must still fire
        Assert.Equal(1, h.RoomEnteredCount);
    }

    [Fact]
    public void RoomEntered_FiredEvenWhenOtherC1ArrivesFirst()
    {
        // Any C1 color sequence that is not C02+C01 is transparent to _atLineStart.
        // A room short following it still sees column 0.
        var h = InGameMode();
        h.Feed(WithPrompt("You look around.\n"));
        h.Feed(0xA0, 0x9B, 0xFF, 0xFF);  // C05+C00 (some color) -- transparent
        h.Feed(RoomShort);               // still at column 0 -> must fire
        Assert.Equal(1, h.RoomEnteredCount);
    }

    // -- RoomEntered: "too dark" ----------------------------------------------

    [Fact]
    public void RoomEntered_FiredForTooDarkMessage()
    {
        // "It's too dark to see now!" signals a dark-room entry.
        // The Here list must be cleared even though no room-short color line follows.
        var h = InGameMode();
        h.Feed(WithPrompt("You go north.\n"));
        h.Feed("It's too dark to see now!\n");
        Assert.Equal(1, h.RoomEnteredCount);
    }

    [Fact]
    public void RoomShortReady_NotFiredForTooDarkMessage()
    {
        // "Too dark" fires RoomEntered but not RoomShortReady (no room name is known).
        var h = InGameMode();
        h.Feed(WithPrompt("You go north.\n"));
        h.Feed("It's too dark to see now!\n");
        Assert.Empty(h.RoomShorts);
    }

    // -- RoomShortReady (line-start color-based) -----------------------------

    // A room short line: C02+C01 (LT_GREEN) set before text, then text + '\n'
    private static byte[] RoomShortLine(string name)
        => [..RoomShort, ..System.Text.Encoding.Latin1.GetBytes(name + "\n"), 0xFF, 0xFF];

    [Fact]
    public void RoomShortReady_FiredForLtGreenLine()
    {
        var h = InGameMode();
        h.Feed(RoomShortLine("Elizabethan tearoom"));
        Assert.Equal(["Elizabethan tearoom"], h.RoomShorts);
    }

    [Fact]
    public void RoomShortReady_FiredAfterCommandEchoClearsLineStart()
    {
        // Regression: command echo ('n\n') clears _atLineStart, but the '\n' restores
        // it so the room short on the next line still fires.
        var h = InGameMode();
        h.Feed(WithPrompt("You go north.\n"));
        h.Feed("n\n");                           // echo: 'n' clears flag, '\n' restores it
        h.Feed(RoomShortLine("Dark forest"));
        Assert.Equal(["Dark forest"], h.RoomShorts);
    }

    [Fact]
    public void RoomShortReady_NotFiredForNonGreenLine()
    {
        // Normal white game-mode text must not trigger RoomShortReady.
        var h = InGameMode();
        h.Feed(WithPrompt("You swing your sword.\n"));
        Assert.Empty(h.RoomShorts);
    }

    [Fact]
    public void RoomShortReady_UpdatesOnEachMove()
    {
        var h = InGameMode();
        h.Feed(RoomShortLine("Room A"));
        h.Feed(RoomShortLine("Room B"));
        Assert.Equal(["Room A", "Room B"], h.RoomShorts);
    }

    [Fact]
    public void RoomShortReady_NotFiredForMidLineRoomShort()
    {
        // A room short embedded mid-line (look-around exits) must NOT fire RoomShortReady.
        var h = InGameMode();
        h.Feed(WithPrompt("Some text.\n"));
        // Build: "You see " + C02+C01 + "Some room\n" + pop
        var line = new System.Collections.Generic.List<byte>();
        line.AddRange(System.Text.Encoding.Latin1.GetBytes("You see "));
        line.AddRange(RoomShort);
        line.AddRange(System.Text.Encoding.Latin1.GetBytes("Some room\n"));
        line.AddRange([0xFF, 0xFF]);
        h.Feed([.. line]);
        Assert.Empty(h.RoomShorts);
    }

    // -- Heartbeat FEI/FEX blocks must be transparent to line-start tracking ---
    // Their text is invisible (surfaced via events), so it must not clear
    // _atLineStart: a room short arriving right after a heartbeat would otherwise
    // fire neither RoomEntered nor RoomShortReady.

    [Fact]
    public void RoomEntered_FiresAfterFeiHeartbeatBlock()
    {
        var h = InGameMode();
        h.Feed(WithPrompt("You set off.\n"));      // frame ends: prompt shown, at line start
        h.Feed(0xA7, 0xA3, 0x9E, 0xFF, 0xFF);      // FEI response opens (heartbeat)
        h.Feed("a brass lamp\n========\na sword\n");
        h.Feed(0xFF, 0xFF);                         // FEI closes
        h.ClearCounters();
        h.Feed(RoomShort);
        Assert.Equal(1, h.RoomEnteredCount);
    }

    [Fact]
    public void RoomEntered_FiresAfterFexBlock()
    {
        var h = InGameMode();
        h.Feed(WithPrompt("You set off.\n"));
        h.Feed(0xA7, 0xA3, 0x9D, 0xFF, 0xFF);      // FEX response opens (auto fex)
        h.Feed("north\neast\n");
        h.Feed(0xFF, 0xFF);                         // FEX closes
        h.ClearCounters();
        h.Feed(RoomShort);
        Assert.Equal(1, h.RoomEnteredCount);
    }

    // -- FEW response suppression ---------------------------------------------

    // C12+C08+C05+C255 -- opens the FEW (WHO-list interrupt) response context
    private static readonly byte[] FewContextOpen = [0xA7, 0xA3, 0xA0, 0xFF, 0xFF];

    // C05+C00+C06+C255 -- WHO-list mortal player name follows (RED color)
    private static readonly byte[] FewPlayerRedPrefix = [0xA0, 0x9B, 0xA1, 0xFF, 0xFF];

    [Fact]
    public void FewResponse_PlayerNamesEmittedAsFewPlayerReady()
    {
        var h = InGameMode();
        h.Feed(FewContextOpen);
        h.Feed(FewPlayerRedPrefix);
        h.Feed("Gandalf");
        h.Feed(0xFF, 0xFF);
        Assert.Contains("Gandalf", h.FewPlayers);
    }

    [Fact]
    public void FewResponse_PlayerNamesNotEmittedAsLines()
    {
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
        var h = InGameMode();
        h.Feed(FewContextOpen);
        h.Feed(0xFF, 0xFF);
        h.Feed(0xFF, 0xFF);
        h.Lines.Clear();
        h.Feed("Normal text\n");
        Assert.Single(h.Lines);
    }

    // -- FewListComplete ------------------------------------------------------

    [Fact]
    public void FewListComplete_FiredWhenContextCloses()
    {
        var h = InGameMode();
        h.Feed(FewContextOpen);
        Assert.Equal(0, h.FewListCompleteCount);
        h.Feed(0xFF, 0xFF);
        Assert.Equal(1, h.FewListCompleteCount);
    }

    [Fact]
    public void FewListComplete_FiredAfterAllNamesDelivered()
    {
        var h = InGameMode();
        var completeSeenAfterNames = false;
        h.Parser.FewListComplete += () =>
            completeSeenAfterNames = h.FewPlayers.Count > 0;

        h.Feed(FewContextOpen);
        h.Feed(FewPlayerRedPrefix);
        h.Feed("Alice");
        h.Feed(0xFF, 0xFF);
        h.Feed(FewPlayerRedPrefix);
        h.Feed("Bob");
        h.Feed(0xFF, 0xFF);
        h.Feed(0xFF, 0xFF);

        Assert.True(completeSeenAfterNames);
        Assert.Equal(2, h.FewPlayers.Count);
    }

    [Fact]
    public void FewListComplete_FiredOncePerResponse()
    {
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
        var h = InGameMode();
        h.Feed(0x9B, 0xFF, 0xFF);
        h.Feed(0xFF, 0xFF);
        Assert.Equal(0, h.FewListCompleteCount);
    }

    // -- C90 colour catch/throw -------------------------------------------------

    // C05+C01+C06+C255 -- WHO-list wiz player name follows (LT_RED color)
    private static readonly byte[] FewPlayerWizPrefix = [0xA0, 0x9C, 0xA1, 0xFF, 0xFF];

    /// <summary>
    /// Wire bytes for a "rainbow" wiz name as captured from a live session:
    /// "Heiach the {catch}{c12}ch{c5}im{c15}er{c5}ic{c12}al{pop}{throw} wizard"
    /// — C90 colour catch, per-letter C99 colours, one bare pop, C90+C01 colour throw.
    /// </summary>
    private static void FeedRainbowName(ParserHarness h)
    {
        h.Feed(FewPlayerWizPrefix);
        h.Feed("Heiach the ");
        h.Feed(0xF5, 0xFF, 0xFF);              // C90: colour catch
        h.Feed(0xFE, 0xA7, 0xFF, 0xFF); h.Feed("ch");
        h.Feed(0xFE, 0xA0, 0xFF, 0xFF); h.Feed("im");
        h.Feed(0xFE, 0xAA, 0xFF, 0xFF); h.Feed("er");
        h.Feed(0xFE, 0xA0, 0xFF, 0xFF); h.Feed("ic");
        h.Feed(0xFE, 0xA7, 0xFF, 0xFF); h.Feed("al");
        h.Feed(0xFF, 0xFF);                    // bare pop
        h.Feed(0xF5, 0x9C, 0xFF, 0xFF);        // C90+C01: colour throw (restore to catch)
        h.Feed(" wizard");
        h.Feed(0xFF, 0xFF);                    // end-of-name pop
        h.Feed("\r\n");
    }

    [Fact]
    public void ColourCatchThrow_RestoresStyle()
    {
        var h = InGameMode();
        h.Feed(0xA0, 0x9B, 0xFF, 0xFF);        // C05+C00 → RED
        h.Feed("red");
        h.Feed(0xF5, 0xFF, 0xFF);              // catch: no colour change
        h.Feed("still");
        h.Feed(0xFE, 0xA7, 0xFF, 0xFF);        // C99 → BrightBlue
        h.Feed("blue");
        h.Feed(0xF5, 0x9C, 0xFF, 0xFF);        // throw: restore to catch point
        h.Feed("after\n");

        var line = Assert.Single(h.Lines);
        Assert.Equal(4, line.Spans.Count);
        Assert.Equal(MudSharp.Models.AnsiColor.Red,        line.Spans[0].Style.Foreground); // "red"
        Assert.Equal(MudSharp.Models.AnsiColor.Red,        line.Spans[1].Style.Foreground); // "still" (catch is colour-neutral)
        Assert.Equal(MudSharp.Models.AnsiColor.BrightBlue, line.Spans[2].Style.Foreground); // "blue"
        Assert.Equal(MudSharp.Models.AnsiColor.Red,        line.Spans[3].Style.Foreground); // "after" (throw restored)
    }

    [Fact]
    public void FewResponse_RainbowName_ContextClosesAndDisplayResumes()
    {
        var h = InGameMode();
        h.Feed(FewContextOpen);
        FeedRainbowName(h);
        h.Feed(FewPlayerRedPrefix);
        h.Feed("Folly the warlock");
        h.Feed(0xFF, 0xFF);
        h.Feed("\r\n");
        h.Feed(0xFF, 0xFF);                    // closing pop → context must end here

        Assert.Equal(1, h.FewListCompleteCount);

        h.Lines.Clear();
        h.Feed("Normal text\n");
        Assert.Single(h.Lines);                // display no longer suppressed
    }

    [Fact]
    public void FewResponse_RainbowName_FullNameCaptured()
    {
        var h = InGameMode();
        h.Feed(FewContextOpen);
        FeedRainbowName(h);
        h.Feed(0xFF, 0xFF);
        Assert.Contains("Heiach the chimerical wizard", h.FewPlayers);
    }

    [Fact]
    public void FewResponse_RainbowName_ContextCloseFinalizesPendingContinuation()
    {
        var h = InGameMode();
        h.Feed(FewContextOpen);
        h.Feed(FewPlayerWizPrefix);
        h.Feed("Heiach the ");
        h.Feed(0xF5, 0xFF, 0xFF);              // C90: colour catch
        h.Feed(0xFE, 0xA7, 0xFF, 0xFF); h.Feed("ch");
        h.Feed(0xFE, 0xA0, 0xFF, 0xFF); h.Feed("im");
        h.Feed(0xFE, 0xAA, 0xFF, 0xFF); h.Feed("er");
        h.Feed(0xFE, 0xA0, 0xFF, 0xFF); h.Feed("ic");
        h.Feed(0xFE, 0xA7, 0xFF, 0xFF); h.Feed("al");
        h.Feed(0xFF, 0xFF);                    // bare pop
        h.Feed(0xF5, 0x9C, 0xFF, 0xFF);        // C90+C01: colour throw (restore to catch)
        h.Feed(" wizard");
        h.Feed(0xFF, 0xFF);                    // end-of-name pop
        h.Feed(0xFF, 0xFF);                    // FEW context closes with lost newline

        Assert.Equal(["Heiach the chimerical wizard"], h.FewPlayers);
        Assert.Equal(1, h.FewListCompleteCount);

        h.Feed("Normal text\n");
        Assert.Single(h.Lines);
        Assert.Equal("Normal text", h.Lines[0].PlainText);
        Assert.Single(h.FewPlayers);
    }

    [Fact]
    public void FewResponse_RainbowName_NotEmittedAsLines()
    {
        var h = InGameMode();
        h.Feed(FewContextOpen);
        FeedRainbowName(h);
        h.Feed(0xFF, 0xFF);
        Assert.Empty(h.Lines);
    }

    // -- FEI response capture -------------------------------------------------

    // C12+C08+C03+C255 -- opens the FEI (inventory) response context
    private static readonly byte[] FeiContextOpen = [0xA7, 0xA3, 0x9E, 0xFF, 0xFF];

    // C12+C08+C02+C255 -- opens the FEX (exits) response context
    private static readonly byte[] FexContextOpen = [0xA7, 0xA3, 0x9D, 0xFF, 0xFF];

    [Fact]
    public void FeiResponse_FeiListStartingFiredOnContextOpen()
    {
        var h = InGameMode();
        h.Feed(FeiContextOpen);
        Assert.Equal(1, h.FeiListStartingCount);
    }

    [Fact]
    public void FeiResponse_ItemLinesEmittedAsFeiItemReady()
    {
        var h = InGameMode();
        h.Feed(FeiContextOpen);
        h.Feed("bouncy-ball\r\n");
        h.Feed("postcard\r\n");
        Assert.Contains("bouncy-ball", h.FeiItems);
        Assert.Contains("postcard", h.FeiItems);
    }

    [Fact]
    public void FeiResponse_SeparatorEmittedAsItem()
    {
        var h = InGameMode();
        h.Feed(FeiContextOpen);
        h.Feed("========\r\n");
        Assert.Contains("========", h.FeiItems);
    }

    [Fact]
    public void FeiResponse_ItemsNotEmittedAsLines()
    {
        var h = InGameMode();
        var linesBefore = h.Lines.Count;
        h.Feed(FeiContextOpen);
        h.Feed("brand19\r\n");
        h.Feed("========\r\n");
        Assert.Equal(linesBefore, h.Lines.Count);
    }

    [Fact]
    public void FeiResponse_FeiListCompleteFiredWhenContextCloses()
    {
        var h = InGameMode();
        h.Feed(FeiContextOpen);
        h.Feed(0xFF, 0xFF);
        Assert.Equal(1, h.FeiListCompleteCount);
    }

    [Fact]
    public void FeiResponse_PartialItemFlushedWhenContextCloses()
    {
        var h = InGameMode();
        h.Feed(FeiContextOpen);
        h.Feed("sword");
        h.Feed(0xFF, 0xFF);
        Assert.Equal(["sword"], h.FeiItems);
        Assert.Equal(1, h.FeiListCompleteCount);
    }

    [Fact]
    public void FeiResponse_AllItemsDeliveredBeforeComplete()
    {
        var h = InGameMode();
        var completeSeenAfterItems = false;
        h.Parser.FeiListComplete += () => completeSeenAfterItems = h.FeiItems.Count > 0;
        h.Feed(FeiContextOpen);
        h.Feed("sword\r\n");
        h.Feed("========\r\n");
        h.Feed("shield\r\n");
        h.Feed(0xFF, 0xFF);
        Assert.True(completeSeenAfterItems);
        Assert.Equal(3, h.FeiItems.Count);
    }

    [Fact]
    public void FeiResponse_AfterContextCloses_DisplayResumes()
    {
        var h = InGameMode();
        h.Feed(FeiContextOpen);
        h.Feed(0xFF, 0xFF);
        h.Lines.Clear();
        h.Feed("Normal text\n");
        Assert.Single(h.Lines);
    }

    [Fact]
    public void FeiResponse_NullBytesIgnored()
    {
        // Wire format uses \r\0\r\n; NUL bytes must not appear in emitted items.
        var h = InGameMode();
        h.Feed(FeiContextOpen);
        h.Feed(new byte[] { (byte)'s', (byte)'w', (byte)'o', (byte)'r', (byte)'d', 0x0D, 0x00, 0x0D, 0x0A });
        Assert.Single(h.FeiItems);
        Assert.Equal("sword", h.FeiItems[0]);
    }

    [Fact]
    public void FexResponse_PartialItemFlushedWhenContextCloses()
    {
        var h = InGameMode();
        h.Feed(FexContextOpen);
        h.Feed("north");
        h.Feed(0xFF, 0xFF);
        Assert.Equal(["north"], h.FexItems);
        Assert.Equal(1, h.FexListCompleteCount);
    }
}
