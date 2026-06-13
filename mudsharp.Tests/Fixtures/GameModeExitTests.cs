namespace MudSharp.Tests.Fixtures;

/// <summary>
/// Tests for game-to-option-menu transition detection.
///
/// The MUD2 server sends NO binary exit signal when the player quits (qq). It just
/// resets to WHITE/BLACK ({C00}{C255} = 0x9B 0xFF 0xFF) and outputs the option-menu
/// prompt as plain text. The parser must detect "Option (H for help)" in the text
/// stream and call ExitGameMode() before the FES heartbeat misfires into the menu.
///
/// Contrast with the {C95}{C03}{C255} path which handles account-level logout — that
/// path is tested via C95 dispatch tests. This tests the qq-to-option-menu path where
/// no C95 signal is present.
/// </summary>
public class GameModeExitTests
{
    // Wire-format prompt preamble
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
        h.Feed(0x9D, 0x9C, 0xFF, 0xFF); // Enter game mode
        h.Feed("setup\n");
        h.Feed(0x9B, 0xFF, 0xFF);        // {C00}{C255}: color reset
        h.ClearCounters();
        return h;
    }

    // Exact byte sequence captured from a live session after sending qq.
    // "qq\r\n" echo, "Cheerio!\r\x00\r\n", blank " \r\n", {C00}{C255}, "Option (H for help): "
    private static readonly byte[] QuitServerBytes =
        [
            (byte)'q', (byte)'q', (byte)'\r', (byte)'\n',
            (byte)'C', (byte)'h', (byte)'e', (byte)'e', (byte)'r', (byte)'i', (byte)'o', (byte)'!',
            (byte)'\r', 0x00, (byte)'\r', (byte)'\n',
            (byte)' ', (byte)'\r', (byte)'\n',
            0x9B, 0xFF, 0xFF,  // {C00}{C255}
            (byte)'O', (byte)'p', (byte)'t', (byte)'i', (byte)'o', (byte)'n',
            (byte)' ', (byte)'(', (byte)'H', (byte)' ', (byte)'f', (byte)'o',
            (byte)'r', (byte)' ', (byte)'h', (byte)'e', (byte)'l', (byte)'p', (byte)')',
            (byte)':', (byte)' ',
        ];

    [Fact]
    public void QuitSequence_ExitsGameMode()
    {
        var h = InGameMode();
        h.Feed(QuitServerBytes);
        Assert.Equal(1, h.GameModeExitedCount);
        Assert.False(h.Parser.InGameMode);
    }

    [Fact]
    public void QuitSequence_ExitsGameModeBeforeOptionMenuTextIsComplete()
    {
        // ExitGameMode should fire as soon as "Option (H for help)" is matched —
        // before the trailing ": " arrives — so the FES timer stops immediately.
        var h = InGameMode();

        // Feed only up through "Option (H for help)" (without ": ")
        var upToMatch = QuitServerBytes[..^2]; // strip the trailing ": "
        h.Feed(upToMatch);

        Assert.Equal(1, h.GameModeExitedCount);
        Assert.False(h.Parser.InGameMode);
    }

    [Fact]
    public void QuitSequence_CheerioLineEmittedBeforeExit()
    {
        var h = InGameMode();
        h.Feed(QuitServerBytes);

        // "Cheerio!" must be emitted as a complete line before game mode exits.
        var cheerio = h.Lines.FirstOrDefault(l => l.PlainText.Contains("Cheerio!"));
        Assert.NotNull(cheerio);
    }

    [Fact]
    public void GameMode_NotExitedOnUnrelatedText()
    {
        // "Options are available" should not trigger the exit — only the exact prefix matters.
        var h = InGameMode();
        h.Feed("Options are available.\n");
        Assert.Equal(0, h.GameModeExitedCount);
        Assert.True(h.Parser.InGameMode);
    }

    [Fact]
    public void GameMode_NotExitedOnPartialPrefix()
    {
        // "Option X" doesn't match "Option (H for help)".
        var h = InGameMode();
        h.Feed("Option X: something\n");
        Assert.Equal(0, h.GameModeExitedCount);
        Assert.True(h.Parser.InGameMode);
    }

    [Fact]
    public void GameMode_NotExitedByMidLineSpeech()
    {
        // The real menu prompt always starts its line. A player quoting it mid-line
        // ('say Option (H for help)') must not kick the client out of game mode —
        // that stopped the heartbeat and sent a stray 'auto fex' on re-entry.
        var h = InGameMode();
        h.Feed("Ollie says \"Option (H for help)\".\n");
        Assert.Equal(0, h.GameModeExitedCount);
        Assert.True(h.Parser.InGameMode);
    }

    [Fact]
    public void ExitDetection_ResetsAcrossLines()
    {
        // Partial match on one line must not carry over to the next line.
        var h = InGameMode();
        h.Feed("Option (H for hel\nOption (H for help)");  // second line completes the match
        Assert.Equal(1, h.GameModeExitedCount);
    }

    [Fact]
    public void C95Logout_StillExitsGameMode()
    {
        // The existing C95+C03+C255 path must still work independently.
        var h = InGameMode();
        h.Feed(0xFA, 0x9E, 0xFF, 0xFF); // {C95}{C03}{C255}
        Assert.Equal(1, h.GameModeExitedCount);
        Assert.False(h.Parser.InGameMode);
    }
}
