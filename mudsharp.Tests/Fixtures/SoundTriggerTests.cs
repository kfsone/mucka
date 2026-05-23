namespace MudSharp.Tests.Fixtures;

/// <summary>
/// Tests for the MUD2 sound trigger system — both C1 binary protocol triggers
/// and text-line pattern matches. Expected filenames follow Clio sound.c formula.
/// </summary>
public class SoundTriggerTests
{
    // Helper: enter game mode so text triggers and game-mode-gated paths are active.
    private static ParserHarness InGameMode()
    {
        var h = new ParserHarness();
        h.Feed(0x9D, 0x9C, 0xFF, 0xFF); // C02+C01+C255 → game mode entry
        h.Sounds.Clear();                // discard any incidental sounds
        return h;
    }

    // ── C06 (0xA1) ─────────────────────────────────────────────────────────────

    [Fact]
    public void C06_Bare_EmitsSound06()
    {
        var h = new ParserHarness();
        h.Feed(0xA1, 0xFF, 0xFF);
        Assert.Single(h.Sounds);
        Assert.Equal("sounds/clio.06.wav", h.Sounds[0]);
    }

    [Fact]
    public void C06_WithPayload_EmitsSound06()
    {
        // {C06}{C06}{C255} — "Something magical" exception (no txfes) but still sound(6)
        var h = new ParserHarness();
        h.Feed(0xA1, 0xA1, 0xFF, 0xFF);
        Assert.Single(h.Sounds);
        Assert.Equal("sounds/clio.06.wav", h.Sounds[0]);
    }

    // ── C07 (0xA2) ─────────────────────────────────────────────────────────────

    [Fact]
    public void C07_Bare_EmitsSound070000()
    {
        // {C07}{C255} → count==0 → sound(7,0,0) = 070000
        var h = new ParserHarness();
        h.Feed(0xA2, 0xFF, 0xFF);
        Assert.Single(h.Sounds);
        Assert.Equal("sounds/clio.070000.wav", h.Sounds[0]);
    }

    [Fact]
    public void C07_OnePayload_EmitsSound07NN()
    {
        // {C07}{C03}{C255} → count==1, b0=0x9E → n2=0x9E-0x9B=3 → clio.0703.wav
        var h = new ParserHarness();
        h.Feed(0xA2, 0x9E, 0xFF, 0xFF);
        Assert.Single(h.Sounds);
        Assert.Equal("sounds/clio.0703.wav", h.Sounds[0]);
    }

    [Fact]
    public void C07_TwoPayloads_EmitsSound07NNMM()
    {
        // {C07}{C02}{C05}{C255} → n2=1, n3=4 → clio.070104.wav (wait: b0=0x9D→1, b1=0xA0→5)
        // 0x9D - 0x9B = 2, 0xA0 - 0x9B = 5 → clio.070205.wav
        var h = new ParserHarness();
        h.Feed(0xA2, 0x9D, 0xA0, 0xFF, 0xFF);
        Assert.Single(h.Sounds);
        Assert.Equal("sounds/clio.070205.wav", h.Sounds[0]);
    }

    // ── C08 (0xA3) ─────────────────────────────────────────────────────────────

    [Fact]
    public void C08_C01_EmitsSound0801()
    {
        // {C08}{C01}{C255} → sound(8,1) = clio.0801.wav
        var h = new ParserHarness();
        h.Feed(0xA3, 0x9C, 0xFF, 0xFF);
        Assert.Single(h.Sounds);
        Assert.Equal("sounds/clio.0801.wav", h.Sounds[0]);
    }

    [Fact]
    public void C08_C03_EmitsSound0803()
    {
        // {C08}{C03}{C255} → sound(8,3) = clio.0803.wav
        var h = new ParserHarness();
        h.Feed(0xA3, 0x9E, 0xFF, 0xFF);
        Assert.Single(h.Sounds);
        Assert.Equal("sounds/clio.0803.wav", h.Sounds[0]);
    }

    [Fact]
    public void C08_Other_NoSound()
    {
        // {C08}{C00}{C255} → plain RED, no sound
        var h = new ParserHarness();
        h.Feed(0xA3, 0x9B, 0xFF, 0xFF);
        Assert.Empty(h.Sounds);
    }

    // ── C11 (0xA6) ─────────────────────────────────────────────────────────────

    [Fact]
    public void C11_WithPayload_EmitsSound11NN()
    {
        // {C11}{C02}{C255} → b0=0x9D, n2=0x9D-0x9B=2 → clio.1102.wav
        var h = new ParserHarness();
        h.Feed(0xA6, 0x9D, 0xFF, 0xFF);
        Assert.Single(h.Sounds);
        Assert.Equal("sounds/clio.1102.wav", h.Sounds[0]);
    }

    [Fact]
    public void C11_ExcludedPayloads_NoSound()
    {
        // {C11}{C06}{C255} — excluded (0xA1 = C06), no txfes, no sound
        var h = new ParserHarness();
        h.Feed(0xA6, 0xA1, 0xFF, 0xFF);
        Assert.Empty(h.Sounds);
    }

    [Fact]
    public void C11_Bare_NoSound()
    {
        // {C11}{C255} → count==0, condition fails, no sound
        var h = new ParserHarness();
        h.Feed(0xA6, 0xFF, 0xFF);
        Assert.Empty(h.Sounds);
    }

    // ── C13 (0xA8) ─────────────────────────────────────────────────────────────

    [Fact]
    public void C13_TwoPayloads_EmitsSound13NN()
    {
        // {C13}{C05}{Cx}{C255} → count==2, b0=0xA0, n2=0xA0-0x9B=5 → clio.1305.wav
        var h = new ParserHarness();
        h.Feed(0xA8, 0xA0, 0x9C, 0xFF, 0xFF);
        Assert.Single(h.Sounds);
        Assert.Equal("sounds/clio.1305.wav", h.Sounds[0]);
    }

    [Fact]
    public void C13_Bare_NoSound()
    {
        var h = new ParserHarness();
        h.Feed(0xA8, 0xFF, 0xFF);
        Assert.Empty(h.Sounds);
    }

    // ── C14 (0xA9) ─────────────────────────────────────────────────────────────

    [Fact]
    public void C14_C03_C02_EmitsSound140302()
    {
        // {C14}{C03}{C02}{C255} = 0xA9 0x9E 0x9D 0xFF 0xFF → rain on trees → clio.140302.wav
        var h = new ParserHarness();
        h.Feed(0xA9, 0x9E, 0x9D, 0xFF, 0xFF);
        Assert.Single(h.Sounds);
        Assert.Equal("sounds/clio.140302.wav", h.Sounds[0]);
    }

    [Fact]
    public void C14_Other_NoSound()
    {
        // {C14}{C00}{C255} → plain GREEN/BLACK, no rain sound
        var h = new ParserHarness();
        h.Feed(0xA9, 0x9B, 0xFF, 0xFF);
        Assert.Empty(h.Sounds);
    }

    // ── C18 (0xAD) ─────────────────────────────────────────────────────────────

    [Fact]
    public void C18_InRange_EmitsSound18NN()
    {
        // {C18}{C04}{C255} → b0=0x9F, n2=0x9F-0x9B=4 → clio.1804.wav
        var h = new ParserHarness();
        h.Feed(0xAD, 0x9F, 0xFF, 0xFF);
        Assert.Single(h.Sounds);
        Assert.Equal("sounds/clio.1804.wav", h.Sounds[0]);
    }

    [Fact]
    public void C18_OutOfRange_NoSound()
    {
        // {C18}{C07}{C255} → b0=0xA2 > 0xA1, out of range, no sound
        var h = new ParserHarness();
        h.Feed(0xAD, 0xA2, 0xFF, 0xFF);
        Assert.Empty(h.Sounds);
    }

    // ── Text triggers (game mode only) ─────────────────────────────────────────

    [Fact]
    public void TextTrigger_Cannon_EmitsSound1313()
    {
        var h = InGameMode();
        h.Feed("Out from the end of the cannon flies a projectile, which smashes into you.\n");
        Assert.Single(h.Sounds);
        Assert.Equal("sounds/clio.1313.wav", h.Sounds[0]);
    }

    [Fact]
    public void TextTrigger_Dragon_EmitsSound1325()
    {
        var h = InGameMode();
        h.Feed("HAWUMPH! The dragon incinerates you with its fiery breath.\n");
        Assert.Single(h.Sounds);
        Assert.Equal("sounds/clio.1325.wav", h.Sounds[0]);
    }

    [Fact]
    public void TextTrigger_Flood_EmitsSound1326()
    {
        var h = InGameMode();
        h.Feed("You hear a near-deafening crash, as if millions of gallons of water were on the move.\n");
        Assert.Single(h.Sounds);
        Assert.Equal("sounds/clio.1326.wav", h.Sounds[0]);
    }

    [Fact]
    public void TextTrigger_NotFiredOutsideGameMode()
    {
        // Text triggers are suppressed when not in game mode
        var h = new ParserHarness();
        h.Feed("HAWUMPH! The dragon incinerates you with its fiery breath.\n");
        Assert.Empty(h.Sounds);
    }

    [Fact]
    public void TextTrigger_NoMatchOnUnrelatedLine()
    {
        var h = InGameMode();
        h.Feed("The dragon looks at you sleepily.\n");
        Assert.Empty(h.Sounds);
    }
}
