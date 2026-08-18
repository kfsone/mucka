namespace MudSharp.Tests.Fixtures;

/// <summary>
/// Tests for the MUD2 sound trigger system — both C1 binary protocol triggers
/// and text-line pattern matches. Expected filenames follow Clio sound.c formula.
/// C1-decoded sounds are queued on the line being accumulated and emitted when it
/// finalises (newline or prompt flush) — a self action echo ("OK, …") drops them —
/// so every C1 trigger test terminates its line before asserting.
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
        h.Feed("Something magical is happening!\n");
        Assert.Single(h.Sounds);
        Assert.Equal("sounds/clio.06.wav", h.Sounds[0]);
    }

    [Fact]
    public void C06_WithPayload_EmitsSound06()
    {
        // {C06}{C06}{C255} — "Something magical" exception (no txfes) but still sound(6)
        var h = new ParserHarness();
        h.Feed(0xA1, 0xA1, 0xFF, 0xFF);
        h.Feed("Something magical is happening!\n");
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
        h.Feed("The rat bites you!\n");
        Assert.Single(h.Sounds);
        Assert.Equal("sounds/clio.070000.wav", h.Sounds[0]);
    }

    [Fact]
    public void C07_OnePayload_EmitsSound07NN()
    {
        // {C07}{C03}{C255} → count==1, b0=0x9E → n2=0x9E-0x9B=3 → clio.0703.wav
        var h = new ParserHarness();
        h.Feed(0xA2, 0x9E, 0xFF, 0xFF);
        h.Feed("The rat bites you!\n");
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
        h.Feed("The rat bites you!\n");
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
        h.Feed("You attack the rat.\n");
        Assert.Single(h.Sounds);
        Assert.Equal("sounds/clio.0801.wav", h.Sounds[0]);
    }

    [Fact]
    public void C08_C03_EmitsSound0803()
    {
        // {C08}{C03}{C255} → sound(8,3) = clio.0803.wav
        var h = new ParserHarness();
        h.Feed(0xA3, 0x9E, 0xFF, 0xFF);
        h.Feed("The rat is dead!\n");
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

    // ── C09 (0xA4) speaker messages ───────────────────────────────────────────

    [Fact]
    public void C09_C03Tell_Default_EmitsTellAlert()
    {
        // Tell with no special prefix uses the default tell alert.
        var h = InGameMode();
        h.Feed(0xA4, 0x9E, 0xFF, 0xFF);
        h.Feed("Ollie tells you \"hello\".\n");
        Assert.Single(h.Sounds);
        Assert.Equal("sounds/tell.wav", h.Sounds[0]);
    }

    [Fact]
    public void C09_C03Tell_Someone_EmitsTellInvisAlert()
    {
        var h = InGameMode();
        h.Feed(0xA4, 0x9E, 0xFF, 0xFF);
        h.Feed("Someone tells you \"hello\".\n");
        Assert.Single(h.Sounds);
        Assert.Equal("sounds/tell-invis.wav", h.Sounds[0]);
    }

    [Fact]
    public void C09_C03Tell_SomeonePowerful_EmitsTellWizAlert()
    {
        var h = InGameMode();
        h.Feed(0xA4, 0x9E, 0xFF, 0xFF);
        h.Feed("Someone powerful tells you \"hello\".\n");
        Assert.Single(h.Sounds);
        Assert.Equal("sounds/tell-wiz.wav", h.Sounds[0]);
    }

    [Fact]
    public void C09_C03Tell_TitledSender_StaysDefaultTellAlert()
    {
        // Non-anonymous tells can include arbitrary multi-word titles; only the exact
        // "Someone" and "Someone powerful" leads are special-cased.
        var h = InGameMode();
        h.Feed(0xA4, 0x9E, 0xFF, 0xFF);
        h.Feed("Ollie the annoyingly verbose necromancer tells you \"hello\".\n");
        Assert.Single(h.Sounds);
        Assert.Equal("sounds/tell.wav", h.Sounds[0]);
    }

    [Fact]
    public void C09_C03Tell_StylesSenderAndTellPhrase()
    {
        var h = InGameMode();
        h.Feed(0xA4, 0x9E, 0xFF, 0xFF);
        h.Feed("Ollie the necromancer tells you \"hello\".\n");

        var line = Assert.Single(h.Lines);
        Assert.Contains(line.Spans, s => s.Text == "Ollie"
                                      && s.Style.Underline
                                      && s.ClickInsertText == "Ollie ");
        Assert.Contains(line.Spans, s => s.Text == "tells you" && s.Style.Italic);
        Assert.DoesNotContain(line.Spans, s => s.Text.Contains("the necromancer", StringComparison.Ordinal)
                                            && s.Style.Underline);
    }

    [Fact]
    public void C09_C03Tell_SomeoneHasNoClickableSenderSpan()
    {
        var h = InGameMode();
        h.Feed(0xA4, 0x9E, 0xFF, 0xFF);
        h.Feed("Someone tells you \"hello\".\n");

        var line = Assert.Single(h.Lines);
        Assert.DoesNotContain(line.Spans, s => s.ClickInsertText != null);
        Assert.Contains(line.Spans, s => s.Text == "tells you" && s.Style.Italic);
    }

    [Fact]
    public void C09_C03Tell_SenderSplitAcrossStyledSpans_RemainsClickable()
    {
        // Sender token "Ollie" is split across span boundaries by ANSI SGR changes.
        // The clickable insert metadata should still resolve to "Ollie ".
        var h = InGameMode();
        h.Feed(0xA4, 0x9E, 0xFF, 0xFF);
        h.Feed("Ol\x1B[31mli\x1B[0me tells you \"hello\".\n");

        var line = Assert.Single(h.Lines);
        Assert.Contains(line.Spans, s => s.Text == "Ol"
                                      && s.Style.Underline
                                      && s.ClickInsertText == "Ollie ");
        Assert.Contains(line.Spans, s => s.Text == "li"
                                      && s.Style.Underline
                                      && s.ClickInsertText == "Ollie ");
        Assert.Contains(line.Spans, s => s.Text == "tells you" && s.Style.Italic);
    }

    [Fact]
    public void C09_C03Tell_NamedSender_FiresTellReceived()
    {
        // Backs the ctrl-r reply hotkey (issue #147): a named sender must be reported so the
        // client can track "last person to tell me something" independent of the game's own
        // 're' command, which re-resolves its target at send time.
        var h = InGameMode();
        h.Feed(0xA4, 0x9E, 0xFF, 0xFF);
        h.Feed("Ollie tells you \"hello\".\n");
        Assert.Equal(["Ollie"], h.TellSenders);
    }

    [Fact]
    public void C09_C03Tell_Someone_DoesNotFireTellReceived()
    {
        var h = InGameMode();
        h.Feed(0xA4, 0x9E, 0xFF, 0xFF);
        h.Feed("Someone tells you \"hello\".\n");
        Assert.Empty(h.TellSenders);
    }

    [Fact]
    public void C09_C03Tell_SomeonePowerful_DoesNotFireTellReceived()
    {
        var h = InGameMode();
        h.Feed(0xA4, 0x9E, 0xFF, 0xFF);
        h.Feed("Someone powerful tells you \"hello\".\n");
        Assert.Empty(h.TellSenders);
    }

    [Fact]
    public void C09_C03Tell_OwnListenersSend_DoesNotFireTellReceived()
    {
        // Your own "send" to your listeners must never look like an incoming tell.
        var h = InGameMode();
        h.Feed(0xA4, 0x9E, 0xFF, 0xFF);
        h.Feed("You tell your listeners \"hello everyone\".\n");
        Assert.Empty(h.TellSenders);
    }

    [Fact]
    public void C09_C03Tell_OwnListenersSend_EmitsNoSound()
    {
        // Your own "send" to your listeners rides the tell channel but is your own output —
        // it must NOT fire a tell alert.
        var h = InGameMode();
        h.Feed(0xA4, 0x9E, 0xFF, 0xFF);
        h.Feed("You tell your listeners \"hello everyone\".\n");
        Assert.Empty(h.Sounds);
    }

    [Fact]
    public void C09_C03Tell_OwnListenersSend_ItalicisesListenersPhrase()
    {
        var h = InGameMode();
        h.Feed(0xA4, 0x9E, 0xFF, 0xFF);
        h.Feed("You tell your listeners \"hello everyone\".\n");

        var line = Assert.Single(h.Lines);
        Assert.Contains(line.Spans, s => s.Text == "your listeners" && s.Style.Italic);
        // The tell-directed-at-you decoration (clickable sender) must not apply to your own send.
        Assert.DoesNotContain(line.Spans, s => s.ClickInsertText != null);
    }

    [Fact]
    public void C09_C02Say_DoesNotEmitTellAlert()
    {
        var h = new ParserHarness();
        h.Feed(0xA4, 0x9D, 0xFF, 0xFF);
        h.Feed("Ollie says \"hello\".\n");
        Assert.Empty(h.Sounds);
    }

    // ── C11 (0xA6) ─────────────────────────────────────────────────────────────

    [Fact]
    public void C11_WithPayload_EmitsSound11NN()
    {
        // {C11}{C02}{C255} → b0=0x9D, n2=0x9D-0x9B=2 → clio.1102.wav
        var h = new ParserHarness();
        h.Feed(0xA6, 0x9D, 0xFF, 0xFF);
        h.Feed("You feel stronger.\n");
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
        h.Feed("There is a blinding flash.\n");
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
        h.Feed("Rain patters on the leaves above.\n");
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
        h.Feed("Your persona has been saved.\n");
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

    // ── Self action echoes ("OK, …") drop their line's sounds ──────────────────

    [Fact]
    public void OkActEcho_ThirdPerson_DropsItsLineSounds()
    {
        // Your own act command echoes back "OK, <name> waves." with the act's sound code
        // riding the same line — the sound announces the action to the ROOM, so your own
        // echo stays silent.
        var h = InGameMode();
        h.Feed(0xA4, 0x9F, 0xFF, 0xFF);          // C09 act — the echo rides the chat channel
        h.Feed(0xA8, 0xA0, 0x9C, 0xFF, 0xFF);    // C13 act sound (clio.1305.wav)
        h.Feed("OK, Ollie the superheroine waves.");
        h.Feed(0xFF, 0xFF);                       // pop C13
        h.Feed(0xFF, 0xFF);                       // pop C09
        h.Feed("\n");
        Assert.Empty(h.Sounds);
    }

    [Fact]
    public void OkActEcho_YouForm_DropsItsLineSounds()
    {
        var h = InGameMode();
        h.Feed(0xA8, 0xA0, 0x9C, 0xFF, 0xFF);
        h.Feed("OK, you wave.");
        h.Feed(0xFF, 0xFF);
        h.Feed("\n");
        Assert.Empty(h.Sounds);
    }

    [Fact]
    public void OtherPlayersAct_SameWireShape_StillPlaysSound()
    {
        // The identical code sequence WITHOUT the "OK, " acknowledgement (someone else's act)
        // plays normally.
        var h = InGameMode();
        h.Feed(0xA4, 0x9F, 0xFF, 0xFF);
        h.Feed(0xA8, 0xA0, 0x9C, 0xFF, 0xFF);
        h.Feed("Bob the warrior waves.");
        h.Feed(0xFF, 0xFF);
        h.Feed(0xFF, 0xFF);
        h.Feed("\n");
        Assert.Single(h.Sounds);
        Assert.Equal("sounds/clio.1305.wav", h.Sounds[0]);
    }

    [Fact]
    public void QueuedSound_FlushedByPromptWhenNoNewlineFollows()
    {
        // A sound whose line ends at a prompt (C98 show-prompt) rather than a newline still
        // plays — deferral must never lose a sound, only the completed "OK, …" echo drops them.
        var h = new ParserHarness();
        h.Feed(0xA1, 0xFF, 0xFF);          // C06 → sound 06 queued
        h.Feed("Something magical is happening");
        h.Feed(0xFD, 0x9B, 0xFF, 0xFF);    // C98 → partial-line flush
        Assert.Single(h.Sounds);
        Assert.Equal("sounds/clio.06.wav", h.Sounds[0]);
    }

    [Fact]
    public void QueuedSound_DroppedOnGameModeExit()
    {
        // Sounds queued on a line the session never finished die with the session.
        var h = InGameMode();
        h.Feed(0xA8, 0xA0, 0x9C, 0xFF, 0xFF);
        h.Feed("There is a blinding fla");
        h.Parser.Reset();                   // disconnect mid-line
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
