using MudSharp.Models;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// End-to-end integration tests simulating real MUD2 server byte sequences.
/// </summary>
public class IntegrationTests
{
    private const byte IAC  = 0xFF;
    private const byte SE   = 0xF0;
    private const byte SB   = 0xFA;
    private const byte WILL = 0xFB;
    private const byte WONT = 0xFC;
    private const byte DO   = 0xFD;
    private const byte DONT = 0xFE;

    private const byte OPT_ECHO        = 0x01;
    private const byte OPT_TTYPE       = 0x18;
    private const byte OPT_NAWS        = 0x1F;
    private const byte OPT_NEW_ENVIRON = 0x27;

    [Fact]
    public void OpeningHandshake_NegotiatesAndEntersGameMode()
    {
        // Simulate the standard MUD2 opening sequence:
        //   server → WILL ECHO, DO TTYPE, DO NAWS, DO NEW_ENVIRON
        //   server → game-mode entry signal (0x9D 0x9C 0xFF 0xFF)
        // Assert correct responses at each step, then exactly one GameModeEntered.
        var h = new ParserHarness();

        h.Feed(IAC, WILL, OPT_ECHO);        // → (no response — Clio ignores WILL ECHO)
        h.Feed(IAC, DO,   OPT_TTYPE);       // → IAC WILL TTYPE
        h.Feed(IAC, DO,   OPT_NAWS);        // → IAC WILL NAWS + NAWS subneg
        h.Feed(IAC, DO,   OPT_NEW_ENVIRON); // → IAC WONT NEW_ENVIRON (Clio telnet.l line 227–228)

        // 4 outgoing packets: WILL TTYPE, WILL NAWS, NAWS-data, WONT NEW_ENVIRON
        Assert.Equal(4, h.Outgoing.Count);
        Assert.Equal(new byte[] { IAC, WILL, OPT_TTYPE },  h.Outgoing[0]);
        Assert.Equal(new byte[] { IAC, WILL, OPT_NAWS },   h.Outgoing[1]);
        Assert.Equal(new byte[] { IAC, SB, OPT_NAWS, 0x00, 0x50, 0x00, 0x15, IAC, SE },
                     h.Outgoing[2]);
        Assert.Equal(new byte[] { IAC, WONT, OPT_NEW_ENVIRON }, h.Outgoing[3]);

        // No game mode yet
        Assert.Equal(0, h.GameModeEnteredCount);

        // Server sends game-mode entry signal
        h.Feed(0x9D, 0x9C, 0xFF, 0xFF);
        Assert.Equal(1, h.GameModeEnteredCount);
        Assert.True(h.Parser.InGameMode);
    }

    [Fact]
    public void TextLine_AfterColorCode_RendersWithCorrectStyle()
    {
        // C03+C01 (0x9E 0x9C 0xFF 0xFF) → CYAN/BLACK, then a text line
        var h = new ParserHarness();
        h.Feed(0x9E, 0x9C, 0xFF, 0xFF);
        h.Feed("Hello, MUD2!\n");

        Assert.Single(h.Lines);
        var line = h.Lines[0];
        Assert.False(line.IsPartial);
        Assert.Equal("Hello, MUD2!", line.PlainText);
        Assert.Equal(AnsiColor.Cyan,  line.Spans[0].Style.Foreground);
        Assert.Equal(AnsiColor.Black, line.Spans[0].Style.Background);
    }
}
