namespace MudSharp.Tests.Fixtures;

/// <summary>
/// Golden byte-stream tests for Telnet option negotiation.
/// Byte sequences derived from Clio telnet.l lines 206–275.
/// </summary>
public class TelnetNegotiationTests
{
    // Telnet control bytes (Clio telnet.l lines 162–176, decimal)
    private const byte IAC  = 0xFF; // 255 \377
    private const byte SE   = 0xF0; // 240 \360
    private const byte SB   = 0xFA; // 250 \372
    private const byte WILL = 0xFB; // 251 \373
    private const byte WONT = 0xFC; // 252 \374
    private const byte DO   = 0xFD; // 253 \375
    private const byte DONT = 0xFE; // 254 \376

    // Option codes (decimal)
    private const byte OPT_ECHO        = 0x01; //  1
    private const byte OPT_SGA         = 0x03; //  3
    private const byte OPT_TTYPE       = 0x18; // 24  \030
    private const byte OPT_NAWS        = 0x1F; // 31  \037
    private const byte OPT_NEW_ENVIRON = 0x27; // 39

    // Sub-negotiation codes (RFC 1091)
    private const byte ENV_IS    = 0x00;
    private const byte ENV_SEND  = 0x01;

    [Fact]
    public void WillEcho_Ignored()
    {
        // Clio telnet.l line 248–249: server WILL ECHO → client sends nothing (break)
        var h = new ParserHarness();
        h.Feed(IAC, WILL, OPT_ECHO);
        Assert.Empty(h.Outgoing);
    }

    [Fact]
    public void WontEcho_Ignored()
    {
        // Clio telnet.l line 265–267: WONT is silently ignored
        var h = new ParserHarness();
        h.Feed(IAC, WONT, OPT_ECHO);
        Assert.Empty(h.Outgoing);
    }

    [Fact]
    public void DoSga_RespondsWillSga()
    {
        // Clio telnet.l line 250–254: server DO SGA → client WILL SGA
        var h = new ParserHarness();
        h.Feed(IAC, DO, OPT_SGA);
        Assert.Single(h.Outgoing);
        Assert.Equal(new byte[] { IAC, WILL, OPT_SGA }, h.Outgoing[0]);
    }

    [Fact]
    public void DoTtype_RespondsWillTtype_ThenSendsTypeName()
    {
        // Clio telnet.l line 215–219, 271: server DO TERMINAL_TYPE → client WILL TERMINAL_TYPE
        // Then: IAC SB TTYPE SEND IAC SE → client sends type name "ansi" (matches Clio 1.8a).
        var h = new ParserHarness();

        h.Feed(IAC, DO, OPT_TTYPE);
        Assert.Single(h.Outgoing);
        Assert.Equal(new byte[] { IAC, WILL, OPT_TTYPE }, h.Outgoing[0]);

        // Server sub-negotiates: IAC SB TTYPE SEND IAC SE
        h.Feed(IAC, SB, OPT_TTYPE, ENV_SEND, IAC, SE);
        Assert.Equal(2, h.Outgoing.Count);
        var expected = new byte[]
        {
            IAC, SB, OPT_TTYPE, ENV_IS,
            (byte)'a', (byte)'n', (byte)'s', (byte)'i',
            IAC, SE,
        };
        Assert.Equal(expected, h.Outgoing[1]);
    }

    [Fact]
    public void DoNaws_RespondsWillNaws_ThenSendsWindowSize()
    {
        // Clio telnet.l line 221–227: server DO NAWS → WILL NAWS + window size (80×21)
        var h = new ParserHarness();
        h.Feed(IAC, DO, OPT_NAWS);

        // Two packets: WILL NAWS, then NAWS subnegotiation
        Assert.Equal(2, h.Outgoing.Count);
        Assert.Equal(new byte[] { IAC, WILL, OPT_NAWS }, h.Outgoing[0]);

        // Width 80 = 0x0050, Height 21 = 0x0015
        var expectedNaws = new byte[] { IAC, SB, OPT_NAWS, 0x00, 0x50, 0x00, 0x15, IAC, SE };
        Assert.Equal(expectedNaws, h.Outgoing[1]);
    }

    [Fact]
    public void DoNewEnviron_RespondsWont()
    {
        // Option 39 (NEW_ENVIRON) has no explicit case; falls through to the default WONT handler.
        // Wire behaviour is identical to Clio telnet.l line 227–228.
        var h = new ParserHarness();
        h.Feed(IAC, DO, OPT_NEW_ENVIRON);
        Assert.Single(h.Outgoing);
        Assert.Equal(new byte[] { IAC, WONT, OPT_NEW_ENVIRON }, h.Outgoing[0]);
    }

    [Fact]
    public void UnknownWill_RespondsDont()
    {
        // Clio telnet.l line 259–261: unrecognised server WILL → client DONT
        var h = new ParserHarness();
        h.Feed(IAC, WILL, 0x05); // STATUS option — not handled
        Assert.Single(h.Outgoing);
        Assert.Equal(new byte[] { IAC, DONT, 0x05 }, h.Outgoing[0]);
    }

    [Fact]
    public void UnknownDo_RespondsWont()
    {
        // Clio telnet.l line 236–238: unrecognised server DO → client WONT
        var h = new ParserHarness();
        h.Feed(IAC, DO, 0x63); // option 99 — not handled
        Assert.Single(h.Outgoing);
        Assert.Equal(new byte[] { IAC, WONT, 0x63 }, h.Outgoing[0]);
    }

    [Fact]
    public void DoTtype_SecondRequest_Ignored()
    {
        // Clio telnet.l line 210: to.terminal_type guard — second DO TTYPE sends nothing
        var h = new ParserHarness();
        h.Feed(IAC, DO, OPT_TTYPE);
        Assert.Single(h.Outgoing);
        h.Feed(IAC, DO, OPT_TTYPE);
        Assert.Single(h.Outgoing); // still only one packet
    }

    [Fact]
    public void DoNaws_SecondRequest_Ignored()
    {
        // Clio telnet.l line 215: to.naws guard — second DO NAWS sends nothing
        var h = new ParserHarness();
        h.Feed(IAC, DO, OPT_NAWS);
        Assert.Equal(2, h.Outgoing.Count);
        h.Feed(IAC, DO, OPT_NAWS);
        Assert.Equal(2, h.Outgoing.Count); // still only the original two packets
    }

    [Fact]
    public void Reset_ClearsOneShotGuards()
    {
        // After Reset(), TTYPE and NAWS guards re-arm and respond again
        var h = new ParserHarness();
        h.Feed(IAC, DO, OPT_TTYPE);
        h.Feed(IAC, DO, OPT_NAWS);
        Assert.Equal(3, h.Outgoing.Count);

        h.Reset();
        h.Feed(IAC, DO, OPT_TTYPE);
        h.Feed(IAC, DO, OPT_NAWS);
        Assert.Equal(6, h.Outgoing.Count);
    }

    [Fact]
    public void DoNaws_WithCustomSize_SendsCustomSize()
    {
        // SetWindowSize before DO NAWS stores the size; DO NAWS uses it.
        var h = new ParserHarness();
        h.Parser.SetWindowSize(120, 24);
        h.Feed(IAC, DO, OPT_NAWS);

        Assert.Equal(2, h.Outgoing.Count);
        // Width 120 = 0x0078, Height 24 = 0x0018
        var expected = new byte[] { IAC, SB, OPT_NAWS, 0x00, 0x78, 0x00, 0x18, IAC, SE };
        Assert.Equal(expected, h.Outgoing[1]);
    }

    [Fact]
    public void SetWindowSize_AfterNaws_SendsUpdatePacket()
    {
        // SetWindowSize after negotiation sends a new NAWS subneg without re-sending WILL NAWS.
        var h = new ParserHarness();
        h.Feed(IAC, DO, OPT_NAWS);
        Assert.Equal(2, h.Outgoing.Count);

        h.Parser.SetWindowSize(60, 21);
        Assert.Equal(3, h.Outgoing.Count);
        // Width 60 = 0x003C, Height 21 = 0x0015
        var expected = new byte[] { IAC, SB, OPT_NAWS, 0x00, 0x3C, 0x00, 0x15, IAC, SE };
        Assert.Equal(expected, h.Outgoing[2]);
    }

    [Fact]
    public void SetWindowSize_BeforeNaws_NoExtraPacket()
    {
        // SetWindowSize before server sends DO NAWS does not trigger any outgoing packet.
        var h = new ParserHarness();
        h.Parser.SetWindowSize(120, 24);
        Assert.Empty(h.Outgoing);
    }

    [Fact]
    public void Reset_PreservesConfiguredWindowSize()
    {
        // After Reset() the stored window size is preserved; the next DO NAWS uses it.
        var h = new ParserHarness();
        h.Parser.SetWindowSize(100, 30);
        h.Feed(IAC, DO, OPT_NAWS);
        Assert.Equal(2, h.Outgoing.Count);

        h.Reset();
        h.Feed(IAC, DO, OPT_NAWS);
        Assert.Equal(4, h.Outgoing.Count);
        // Width 100 = 0x0064, Height 30 = 0x001E
        var expected = new byte[] { IAC, SB, OPT_NAWS, 0x00, 0x64, 0x00, 0x1E, IAC, SE };
        Assert.Equal(expected, h.Outgoing[3]);
    }
}
