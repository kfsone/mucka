using MudSharp.Models;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// Golden byte-stream tests for the MUD2 proprietary C1 protocol (lead bytes 0x9B-0xFE).
/// All sequences end with the C255 terminator (0xFF 0xFF).
/// Byte sequences derived from Clio telnet.l C00-C99 rules.
///
/// Define our own indexes for color mapping:
///   BLACK=0  RED=1  GREEN=2  YELLOW=3  BLUE=4  MAGENTA=5  CYAN=6  WHITE=7
///   LT_BLACK=8  LT_RED=9  LT_GREEN=10  LT_YELLOW=11  LT_BLUE=12  LT_MAGENTA=13  LT_CYAN=14  LT_WHITE=15
/// </summary>
public class Mud2C1Tests
{
    // -- Game-mode entry --------------------------------------------------------

    [Fact]
    public void GameModeEntrySignal_0x9D_0x9C_FiresGameModeEntered()
    {
        // Clio telnet.l line 473-488: C02+C01+C255 (0x9D 0x9C 0xFF 0xFF)
        // -> push(LT_GREEN,BLACK) + mode=GAME + txfes()
        var h = new ParserHarness();
        h.Feed(0x9D, 0x9C, 0xFF, 0xFF);
        Assert.Equal(1, h.GameModeEnteredCount);
    }

    // -- FES packet -------------------------------------------------------------

    [Fact]
    public void FesPacket_ParsesStamina()
    {
        // Clio telnet.l line 728: C12+C08+C01+C255 (0xA7 0xA3 0x9C 0xFF 0xFF) -> FES data line follows
        // Data format: sta msta str mstr dex mdex mag mmag score blind deaf crippled dumb reset weather
        var h = new ParserHarness();
        h.Feed(0xA7, 0xA3, 0x9C, 0xFF, 0xFF);
        h.Feed("81 81 94 94 95 95 50 50 1785 N N N N 5 S\n");
        Assert.Single(h.Stats);
        Assert.Equal(81, h.Stats[0].Stamina);
        Assert.Equal(81, h.Stats[0].MaxStamina);
    }

    [Fact]
    public void FesPacket_ParsesTimeToReset()
    {
        // fields[13] = reset minutes to next reset
        var h = new ParserHarness();
        h.Feed(0xA7, 0xA3, 0x9C, 0xFF, 0xFF);
        h.Feed("81 81 94 94 95 95 50 50 1785 N N N N 5 S\n");
        Assert.Single(h.Stats);
        Assert.Equal(5, h.Stats[0].TimeToReset);
    }

    [Fact]
    public void FesPacket_ParsesStrengthAndDexterity()
    {
        // fields[2]=str=94, fields[4]=dex=95 in "81 81 94 94 95 95 50 50 1785 N N N N 5 S"
        var h = new ParserHarness();
        h.Feed(0xA7, 0xA3, 0x9C, 0xFF, 0xFF);
        h.Feed("81 81 94 94 95 95 50 50 1785 N N N N 5 S\n");
        Assert.Single(h.Stats);
        Assert.Equal(94, h.Stats[0].Strength);
        Assert.Equal(95, h.Stats[0].Dexterity);
    }

    [Fact]
    public void FesPacket_ParsesDreamword()
    {
        // Dreamword set by C15+C00+C00+C255 before FES packet;
        // FES snapshot should carry the current dreamword.
        var h = new ParserHarness();

        // Set dreamword "sword": C15(0xAA)+C00(0x9B)+C00(0x9B)+C255, then letters terminated by '\n'
        h.Feed(0xAA, 0x9B, 0x9B, 0xFF, 0xFF);
        h.Feed("sword\n");
        Assert.Single(h.Dreamwords);
        Assert.Equal("sword", h.Dreamwords[0]);

        // Now send FES packet
        h.Feed(0xA7, 0xA3, 0x9C, 0xFF, 0xFF);
        h.Feed("81 81 94 94 95 95 50 50 1785 N N N N 5 S\n");
        Assert.NotEmpty(h.Stats);
        Assert.Equal("sword", h.Stats.Last().DreamWord);
    }

    // -- C1 color codes --------------------------------------------------------

    [Fact]
    public void C1Color_0x9B_SetsExpectedStyle()
    {
        // C00 (0x9B) + C255 -> Clio init_stack(WHITE,BLACK)
        var h = new ParserHarness();
        h.Feed(0x9B, 0xFF, 0xFF);
        h.Feed("text\n");
        Assert.Single(h.Lines);
        var style = h.Lines[0].Spans[0].Style;
        Assert.Equal(AnsiColor.White, style.Foreground);
        Assert.Equal(AnsiColor.Black, style.Background);
    }

    [Fact]
    public void C1Color_0x9E_SetsExpectedStyle()
    {
        // C03+C01 (0x9E 0x9C) + C255 -> Clio push(CYAN,BLACK)
        var h = new ParserHarness();
        h.Feed(0x9E, 0x9C, 0xFF, 0xFF);
        h.Feed("text\n");
        Assert.Single(h.Lines);
        var style = h.Lines[0].Spans[0].Style;
        Assert.Equal(AnsiColor.Cyan, style.Foreground);
        Assert.Equal(AnsiColor.Black, style.Background);
    }

    [Fact]
    public void C1Color_0x9D_GameModeEntry_SetsLtGreen()
    {
        // C02+C01 (0x9D 0x9C) + C255 -> Clio push(LT_GREEN,BLACK) + mode=GAME
        var h = new ParserHarness();
        h.Feed(0x9D, 0x9C, 0xFF, 0xFF);
        h.Feed("text\n");
        Assert.Single(h.Lines);
        var style = h.Lines[0].Spans[0].Style;
        Assert.Equal(AnsiColor.BrightGreen, style.Foreground);
        Assert.Equal(AnsiColor.Black, style.Background);
    }

    [Fact]
    public void C1Color_0xA1_SetsLtBlue()
    {
        // C06 (0xA1) + C255 -> Clio push(LT_BLUE,BLACK)
        var h = new ParserHarness();
        h.Feed(0xA1, 0xFF, 0xFF);
        h.Feed("text\n");
        Assert.Single(h.Lines);
        var style = h.Lines[0].Spans[0].Style;
        Assert.Equal(AnsiColor.BrightBlue, style.Foreground);
        Assert.Equal(AnsiColor.Black, style.Background);
    }

    [Fact]
    public void C1Color_0xA4_WithC00_SetsYellow()
    {
        // C09+C00 (0xA4 0x9B) + C255 -> Clio push(YELLOW,BLACK)
        var h = new ParserHarness();
        h.Feed(0xA4, 0x9B, 0xFF, 0xFF);
        h.Feed("text\n");
        Assert.Single(h.Lines);
        var style = h.Lines[0].Spans[0].Style;
        Assert.Equal(AnsiColor.Yellow, style.Foreground);
        Assert.Equal(AnsiColor.Black, style.Background);
    }

    [Fact]
    public void C1Color_0xA4_WithOther_SetsLtYellow()
    {
        // C09+C01 (0xA4 0x9C) + C255 -> Clio push(LT_YELLOW,BLACK)
        var h = new ParserHarness();
        h.Feed(0xA4, 0x9C, 0xFF, 0xFF);
        h.Feed("text\n");
        Assert.Single(h.Lines);
        var style = h.Lines[0].Spans[0].Style;
        Assert.Equal(AnsiColor.BrightYellow, style.Foreground);
        Assert.Equal(AnsiColor.Black, style.Background);
    }

    // Verbatim wire shape: the 09 00 speaker frame holds the whole message and the quoted words sit
    // in an inner 09 01 (shout) / 09 02 (say) / 09 03 (tell) frame.
    private static StyledLine SpeechLine(string label, byte subcode, string words)
    {
        var h = new ParserHarness();
        h.Feed(0x9D, 0x9C, 0xFF, 0xFF);         // C02+C01: game mode, so the tell decoration runs
        h.Feed("setup\n");
        h.Feed(0x9B, 0xFF, 0xFF);               // C00
        h.ClearCounters();
        h.Feed([0xA4, 0x9B, 0xFF, 0xFF, .. System.Text.Encoding.ASCII.GetBytes(label + "\""),
                0xA4, subcode, 0xFF, 0xFF, .. System.Text.Encoding.ASCII.GetBytes(words), 0xFF, 0xFF,
                (byte)'"', (byte)'.', 0xFF, 0xFF, (byte)'\r', 0x00, (byte)'\r', (byte)'\n']);
        return h.Lines.Single(l => l.Kind == LineKind.Chat);
    }

    // Each word frame names its part of the message; the label, and the text after the inner frame
    // closes, are the outer 09 00 speaker frame. The colour comes from the part when it is drawn.
    [Theory]
    [InlineData(0x9C, SpeechPart.Shout)]   // 09 01 shouted message
    [InlineData(0x9D, SpeechPart.Say)]     // 09 02 said message
    [InlineData(0x9E, SpeechPart.Tell)]    // 09 03 told message
    public void C09_WordFrames_NameTheirPart_AndTheLabelIsTheSpeaker(byte subcode, SpeechPart part)
    {
        var line = SpeechLine("Crispyjr tells you ", subcode, "HELLO GIANTS");
        var speaker = new TextStyle(AnsiColor.Yellow, AnsiColor.Black, Speech: SpeechPart.Speaker);
        var words = line.Spans.Single(s => s.Text == "HELLO GIANTS").Style;
        Assert.Equal(new TextStyle(AnsiColor.BrightYellow, AnsiColor.Black, Speech: part), words);
        // The first span is the sender's name, which the tell decoration may underline.
        Assert.Equal(speaker, line.Spans[0].Style with { Underline = false });
        Assert.Equal(speaker, line.Spans.Single(s => s.Text == "\".").Style);
    }

    // A tell's verb is underlined in both directions, in whatever style its span already has; the
    // sender, inbound and named, is underlined as before. The labels are the four shapes seen on
    // the wire in 09 03 frames.
    [Theory]
    [InlineData("Lazlo the yeoman tells you ", "tells", "Lazlo")]
    [InlineData("Lazlo tells you ",            "tells", "Lazlo")]
    [InlineData("Someone tells you ",          "tells", null)]
    [InlineData("You tell your listeners ",    "tell",  null)]
    public void C09_Tell_UnderlinesTheVerb(string label, string verb, string? sender)
    {
        var line = SpeechLine(label, 0x9E, "tell me what he tells you");
        var underlined = line.Spans.Where(s => s.Style.Underline).Select(s => s.Text).ToArray();
        Assert.Equal(sender is null ? [verb] : new[] { sender, verb }, underlined);

        var verbSpan = line.Spans.Single(s => s.Style.Underline && s.Text == verb);
        Assert.Equal(new TextStyle(AnsiColor.Yellow, AnsiColor.Black, Underline: true, Speech: SpeechPart.Speaker),
            verbSpan.Style);
        Assert.Null(verbSpan.ClickInsertText);
        Assert.Equal(label + "\"tell me what he tells you\".", line.PlainText);
    }

    [Fact]
    public void C09_Tell_SenderKeepsItsClickInsert_AndTheVerbGetsNone()
    {
        var line = SpeechLine("Lazlo the yeoman tells you ", 0x9E, "hi");
        Assert.Equal("Lazlo ", line.Spans.Single(s => s.Text == "Lazlo").ClickInsertText);
        Assert.Single(line.Spans, s => s.ClickInsertText != null);
    }

    // The settings editor's tell examples underline exactly what the pane underlines on the same line.
    [Fact]
    public void ChatExampleText_TellExamples_UnderlineWhatThePaneUnderlines()
    {
        foreach (var example in ChatExampleText.Tell)
        {
            var line = SpeechLine(example.Lead[..^1], 0x9E, example.Quoted);
            var pane = line.Spans.Where(s => s.Style.Underline).Select(s => s.Text);
            var runs = example.LeadRuns;
            Assert.Equal(ChatExampleText.LeadRunCount, runs.Count);
            Assert.Equal(example.Lead, string.Concat(runs));
            Assert.Equal(pane, runs.Where((_, i) => i % 2 == 1 && runs[i].Length > 0));
        }
        Assert.Equal(["", "Other", " the hero ", "tells", " you \""], ChatExampleText.Tell[0].LeadRuns);
        Assert.Equal(["You ", "tell", " your listeners \"", "", ""], ChatExampleText.Tell[1].LeadRuns);
    }

    [Fact]
    public void ChatExampleText_OtherRows_HaveNoUnderline()
    {
        foreach (var example in ChatExampleText.Say.Concat(ChatExampleText.Shout).Concat(ChatExampleText.Speaker))
            Assert.Equal([example.Lead, "", "", "", ""], example.LeadRuns);
    }

    // Only a tell's label is decorated: the same prose in a say or shout, or in the told words of a
    // tell whose label is neither form, underlines nothing.
    [Theory]
    [InlineData("Crispyjr tells you ", 0x9D)]
    [InlineData("Crispyjr tells you ", 0x9C)]
    [InlineData("You tell your listeners ", 0x9D)]
    [InlineData("Crispyjr says ",      0x9E)]
    public void C09_Tell_OnlyATellLabelIsUnderlined(string label, byte subcode)
    {
        var line = SpeechLine(label, subcode, "You tell your listeners he tells you lies");
        Assert.DoesNotContain(line.Spans, s => s.Style.Underline);
    }

    // 09 04 act, 09 05 emotion (with its 00/01/02 parameter), 09 06 affection, 09 07 hello,
    // 09 08 goodbye, 09 09 congratulations, 09 10 sorry: all are drawn as the speaker.
    [Theory]
    [InlineData(new byte[] { 0x9F })]
    [InlineData(new byte[] { 0xA0, 0x9B })]
    [InlineData(new byte[] { 0xA0, 0x9D })]
    [InlineData(new byte[] { 0xA1 })]
    [InlineData(new byte[] { 0xA2 })]
    [InlineData(new byte[] { 0xA5 })]
    public void C09_ActAndEmoteFrames_AreTheSpeaker(byte[] subcodes)
    {
        var h = new ParserHarness();
        h.Feed([0xA4, 0x9B, 0xFF, 0xFF, .. System.Text.Encoding.ASCII.GetBytes("Fred "),
                0xA4, .. subcodes, 0xFF, 0xFF, .. System.Text.Encoding.ASCII.GetBytes("waves"), 0xFF, 0xFF,
                (byte)'.', 0xFF, 0xFF, (byte)'\r', (byte)'\n']);
        var line = h.Lines.Single(l => l.Kind == LineKind.Chat);
        Assert.Equal(SpeechPart.Speaker, line.Spans.Single(s => s.Text == "waves").Style.Speech);
        Assert.Equal(AnsiColor.Yellow, line.Spans.Single(s => s.Text == "waves").Style.Foreground);
    }

    [Fact]
    public void C1Color_0xA6_SetsLtRed()
    {
        // C11 (0xA6) + C255 = 11 00 "Disabling spell starts" -> push(LT_RED,BLACK)
        var h = new ParserHarness();
        h.Feed(0xA6, 0xFF, 0xFF);
        h.Feed("text\n");
        Assert.Single(h.Lines);
        var style = h.Lines[0].Spans[0].Style;
        Assert.Equal(AnsiColor.BrightRed, style.Foreground);
        Assert.Equal(AnsiColor.Black, style.Background);
    }

    [Fact]
    public void C1Color_0xA7_WithC04_SetsGreen()
    {
        // C12+C04 (0xA7 0x9F) + C255 -> Clio push(GREEN,BLACK)
        var h = new ParserHarness();
        h.Feed(0xA7, 0x9F, 0xFF, 0xFF);
        h.Feed("text\n");
        Assert.Single(h.Lines);
        var style = h.Lines[0].Spans[0].Style;
        Assert.Equal(AnsiColor.Green, style.Foreground);
        Assert.Equal(AnsiColor.Black, style.Background);
    }

    [Fact]
    public void C1Color_C99_SetsArbitraryColor()
    {
        // C99 (0xFE) + fg=0x9C + bg=0x9B + C255
        // C99Color(0x9C) = 0x9C - 0x9B = 1 = RED; C99Color(0x9B) = 0 = BLACK
        var h = new ParserHarness();
        h.Feed(0xFE, 0x9C, 0x9B, 0xFF, 0xFF);
        h.Feed("text\n");
        Assert.Single(h.Lines);
        var style = h.Lines[0].Spans[0].Style;
        Assert.Equal(AnsiColor.Red,   style.Foreground);
        Assert.Equal(AnsiColor.Black, style.Background);
    }

    // -- C98 correctness --------------------------------------------------------

    /// <summary>
    /// C98 (0xFD) + even payload byte: BLACK/BLUE, and an unconditional ShowPrompt() - but it must
    /// NOT re-arm <c>PromptAllowed</c>. Only a real game newline does that, and C98 always precedes
    /// the C01 prompt preamble bytes, so setting it here would make every FES heartbeat's
    /// end-of-frame marker display as a real prompt - a stray '*' in the terminal on every beat.
    /// The old name claimed C98 SETS the flag, which is the defect, not the behaviour.
    /// </summary>
    [Fact]
    public void C98_SetsColor_AndShowsThePrompt_WithoutReArmingPromptAllowed()
    {
        // 0x9C = 156, 156 % 2 == 0 -> Apply(BLACK, BLUE)
        var h = new ParserHarness();
        h.Feed("prompt: ");           // accumulate text
        h.Parser.PromptAllowed = false;   // as it stands after a prompt was last displayed
        h.Feed(0xFD, 0x9C, 0xFF, 0xFF); // C98 with even byte -> BLACK/BLUE + ShowPrompt()
        Assert.False(h.Parser.PromptAllowed, "C98 must not re-arm the prompt gate");

        // ShowPrompt() should have emitted the accumulated "prompt: " as a partial line
        Assert.Single(h.Lines);
        Assert.True(h.Lines[0].IsPartial);

        // Text emitted after C98 should carry the BLACK/BLUE color.
        // EmitPartialLine clears spans, so Lines[1] only has the new "after" span (Black/Blue).
        h.Feed("after\n");
        Assert.Equal(2, h.Lines.Count);
        var style = h.Lines[1].Spans[^1].Style;
        Assert.Equal(AnsiColor.Black, style.Foreground);
        Assert.Equal(AnsiColor.Blue,  style.Background);
    }

    // -- C14 weather colors ----------------------------------------------------

    [Fact]
    public void C14_WithC03C01_SetsBlackWhite()
    {
        // Clio telnet.l: {C14}{C03}{C01}{C255} -> push(BLACK,WHITE) (snow)
        // 0xA9 0x9E 0x9C 0xFF 0xFF
        var h = new ParserHarness();
        h.Feed(0xA9, 0x9E, 0x9C, 0xFF, 0xFF);
        h.Feed("text\n");
        Assert.Single(h.Lines);
        var style = h.Lines[0].Spans[0].Style;
        Assert.Equal(AnsiColor.Black, style.Foreground);
        Assert.Equal(AnsiColor.White, style.Background);
    }

    [Fact]
    public void C14_WithC03C03_SetsBlackWhite()
    {
        // Clio telnet.l: {C14}{C03}{C03}{C255} -> push(BLACK,WHITE) (snow)
        // 0xA9 0x9E 0x9E 0xFF 0xFF
        var h = new ParserHarness();
        h.Feed(0xA9, 0x9E, 0x9E, 0xFF, 0xFF);
        h.Feed("text\n");
        Assert.Single(h.Lines);
        var style = h.Lines[0].Spans[0].Style;
        Assert.Equal(AnsiColor.Black, style.Foreground);
        Assert.Equal(AnsiColor.White, style.Background);
    }

    [Fact]
    public void C14_WithC05C01_SetsBlackWhite()
    {
        // Clio telnet.l: {C14}{C05}{C01}{C255} -> push(BLACK,WHITE) (snow)
        // 0xA9 0xA0 0x9C 0xFF 0xFF
        var h = new ParserHarness();
        h.Feed(0xA9, 0xA0, 0x9C, 0xFF, 0xFF);
        h.Feed("text\n");
        Assert.Single(h.Lines);
        var style = h.Lines[0].Spans[0].Style;
        Assert.Equal(AnsiColor.Black, style.Foreground);
        Assert.Equal(AnsiColor.White, style.Background);
    }

    [Fact]
    public void C14_WithC00_SetsGreenBlack()
    {
        // Clio telnet.l: {C14}{C00}{C255} -> push(GREEN,BLACK) (fine weather)
        // 0xA9 0x9B 0xFF 0xFF
        var h = new ParserHarness();
        h.Feed(0xA9, 0x9B, 0xFF, 0xFF);
        h.Feed("text\n");
        Assert.Single(h.Lines);
        var style = h.Lines[0].Spans[0].Style;
        Assert.Equal(AnsiColor.Green, style.Foreground);
        Assert.Equal(AnsiColor.Black, style.Background);
    }

    // -- Dreamword -------------------------------------------------------------

    [Fact]
    public void DreamwordSequence_EmitsDreamwordChanged()
    {
        // Clio telnet.l line 904-915: C15+C00+C00+C255 followed by [a-z]{1,14}
        // 0xAA 0x9B 0x9B 0xFF 0xFF -> DreamwordData state; letters until non-[a-z]
        var h = new ParserHarness();
        h.Feed(0xAA, 0x9B, 0x9B, 0xFF, 0xFF);
        h.Feed("sword\n"); // '\n' terminates the dreamword and is replayed as newline
        Assert.Single(h.Dreamwords);
        Assert.Equal("sword", h.Dreamwords[0]);
    }

    [Fact]
    public void DreamwordClear_EmitsNullDreamword()
    {
        // Clio telnet.l line 916-925: C15+C00+C01+C255 -> dreamword cleared (memset to 0)
        // 0xAA 0x9B 0x9C 0xFF 0xFF -> EmitDreamwordChanged(null)
        var h = new ParserHarness();
        h.Feed(0xAA, 0x9B, 0x9C, 0xFF, 0xFF);
        Assert.Single(h.Dreamwords);
        Assert.Null(h.Dreamwords[0]);
    }

    // -- C95 client mode --------------------------------------------------------

    [Fact]
    public void C95_EmitsClientModeData()
    {
        // Clio telnet.l line 975-1015: C95+C255 -> collect 5 newline-terminated lines
        // (licence, min-level, max-level, account, privs)
        var h = new ParserHarness();
        h.Feed(0xFA, 0xFF, 0xFF); // C95 (0xFA) + C255
        h.Feed("LIC001\nmin1\nmax9\naccount\nprivs\n");
        Assert.Single(h.ClientModeData);
        Assert.Equal("LIC001\nmin1\nmax9\naccount\nprivs\n", h.ClientModeData[0]);
    }

    [Fact]
    public void C1Color_0x9C_NoPayload_SetsBlue()
    {
        // C01 (0x9C) + C255 -> b0==0, not in {0x9C,0x9D,0x9E} -> BLUE/BLACK
        var h = new ParserHarness();
        h.Feed(0x9C, 0xFF, 0xFF);
        h.Feed("text\n");
        Assert.Single(h.Lines);
        var style = h.Lines[0].Spans[0].Style;
        Assert.Equal(AnsiColor.Blue,  style.Foreground);
        Assert.Equal(AnsiColor.Black, style.Background);
    }

    [Fact]
    public void C1Color_0x9F_WithC01_SetsLtMagenta()
    {
        // C04+C01 (0x9F 0x9C) + C255 -> b0==0x9C -> LT_MAGENTA/BLACK
        var h = new ParserHarness();
        h.Feed(0x9F, 0x9C, 0xFF, 0xFF);
        h.Feed("text\n");
        Assert.Single(h.Lines);
        var style = h.Lines[0].Spans[0].Style;
        Assert.Equal(AnsiColor.BrightMagenta, style.Foreground);
        Assert.Equal(AnsiColor.Black,         style.Background);
    }

    [Fact]
    public void C1Color_0x9F_NoC01_SetsMagenta()
    {
        // C04+C00 (0x9F 0x9B) + C255 -> b0!=0x9C -> MAGENTA/BLACK
        var h = new ParserHarness();
        h.Feed(0x9F, 0x9B, 0xFF, 0xFF);
        h.Feed("text\n");
        Assert.Single(h.Lines);
        var style = h.Lines[0].Spans[0].Style;
        Assert.Equal(AnsiColor.Magenta, style.Foreground);
        Assert.Equal(AnsiColor.Black,   style.Background);
    }

    [Fact]
    public void C1Color_0xA0_WithC01_SetsLtRed()
    {
        // C05+C01 (0xA0 0x9C) + C255 -> b0==0x9C, count==1 -> LT_RED/BLACK
        var h = new ParserHarness();
        h.Feed(0xA0, 0x9C, 0xFF, 0xFF);
        h.Feed("text\n");
        Assert.Single(h.Lines);
        var style = h.Lines[0].Spans[0].Style;
        Assert.Equal(AnsiColor.BrightRed, style.Foreground);
        Assert.Equal(AnsiColor.Black,     style.Background);
    }

    [Fact]
    public void C1Color_0xA0_WithC00_SetsRed()
    {
        // C05+C00 (0xA0 0x9B) + C255 -> b0==0x9B -> RED/BLACK
        var h = new ParserHarness();
        h.Feed(0xA0, 0x9B, 0xFF, 0xFF);
        h.Feed("text\n");
        Assert.Single(h.Lines);
        var style = h.Lines[0].Spans[0].Style;
        Assert.Equal(AnsiColor.Red,   style.Foreground);
        Assert.Equal(AnsiColor.Black, style.Background);
    }

    [Fact]
    public void C1Color_0xA2_SetsRed()
    {
        // C07 (0xA2) + C255 -> RED/BLACK (important messages)
        var h = new ParserHarness();
        h.Feed(0xA2, 0xFF, 0xFF);
        h.Feed("text\n");
        Assert.Single(h.Lines);
        var style = h.Lines[0].Spans[0].Style;
        Assert.Equal(AnsiColor.Red,   style.Foreground);
        Assert.Equal(AnsiColor.Black, style.Background);
    }

    [Fact]
    public void C1Color_0xA3_WithC08_SetsBlackOnRed()
    {
        // C08+C08 (0xA3 0xA3) + C255 -> b0==0xA3 -> BLACK/RED (death/combat)
        var h = new ParserHarness();
        h.Feed(0xA3, 0xA3, 0xFF, 0xFF);
        h.Feed("text\n");
        Assert.Single(h.Lines);
        var style = h.Lines[0].Spans[0].Style;
        Assert.Equal(AnsiColor.Black, style.Foreground);
        Assert.Equal(AnsiColor.Red,   style.Background);
    }

    [Fact]
    public void C1Color_0xA5_WithC00_SetsBlackOnYellow()
    {
        // C10+C00 (0xA5 0x9B) + C255 -> b0==0x9B -> BLACK/YELLOW
        var h = new ParserHarness();
        h.Feed(0xA5, 0x9B, 0xFF, 0xFF);
        h.Feed("text\n");
        Assert.Single(h.Lines);
        var style = h.Lines[0].Spans[0].Style;
        Assert.Equal(AnsiColor.Black,  style.Foreground);
        Assert.Equal(AnsiColor.Yellow, style.Background);
    }

    [Fact]
    public void C1Color_0xA8_SetsWhite()
    {
        // C13 (0xA8) + C255 -> WHITE/BLACK (system messages)
        var h = new ParserHarness();
        h.Feed(0xA8, 0xFF, 0xFF);
        h.Feed("text\n");
        Assert.Single(h.Lines);
        var style = h.Lines[0].Spans[0].Style;
        Assert.Equal(AnsiColor.White, style.Foreground);
        Assert.Equal(AnsiColor.Black, style.Background);
    }

    [Fact]
    public void C1Color_0xAB_SetsLtWhiteOnBlue()
    {
        // C16 (0xAB) + C255 -> LT_WHITE/BLUE (house messages)
        var h = new ParserHarness();
        h.Feed(0xAB, 0xFF, 0xFF);
        h.Feed("text\n");
        Assert.Single(h.Lines);
        var style = h.Lines[0].Spans[0].Style;
        Assert.Equal(AnsiColor.BrightWhite, style.Foreground);
        Assert.Equal(AnsiColor.Blue,        style.Background);
    }

    [Fact]
    public void C1Color_0xA9_SetsGreenOnBlack()
    {
        // C14 (0xA9) + C255 -> GREEN/BLACK (weather/outdoor default)
        var h = new ParserHarness();
        h.Feed(0xA9, 0xFF, 0xFF);
        h.Feed("text\n");
        Assert.Single(h.Lines);
        var style = h.Lines[0].Spans[0].Style;
        Assert.Equal(AnsiColor.Green, style.Foreground);
        Assert.Equal(AnsiColor.Black, style.Background);
    }

    // -- Reset ------------------------------------------------------------------

    [Fact]
    public void Reset_ClearsGameMode_FiresGameModeExited()
    {
        var h = new ParserHarness();
        h.Feed(0x9D, 0x9C, 0xFF, 0xFF); // enter game mode
        Assert.Equal(1, h.GameModeEnteredCount);
        Assert.True(h.Parser.InGameMode);

        h.Parser.Reset();
        Assert.Equal(1, h.GameModeExitedCount);
        Assert.False(h.Parser.InGameMode);
    }

    // -- FES 15-field parsing ---------------------------------------------------

    [Fact]
    public void FesPacket_ParsesAllNewFields()
    {
        // fields: 93 94 100 100 100 100 0 94 4724 N N N N 2 R
        //         sta msta str mstr dex mdex mag mmag score blind deaf crip dumb reset weather
        var h = new ParserHarness();
        h.Feed(0xA7, 0xA3, 0x9C, 0xFF, 0xFF);
        h.Feed("93 94 100 100 100 100 0 94 4724 N N N N 2 R\n");
        Assert.Single(h.Stats);
        var s = h.Stats[0];
        Assert.Equal(93,   s.Stamina);
        Assert.Equal(94,   s.MaxStamina);
        Assert.Equal(100,  s.Strength);
        Assert.Equal(100,  s.MaxStrength);
        Assert.Equal(100,  s.Dexterity);
        Assert.Equal(100,  s.MaxDexterity);
        Assert.Equal(0,    s.CurrentMagic);
        Assert.Equal(94,   s.MaxMagic);
        Assert.Equal(4724, s.Score);
        Assert.False(s.IsBlind);
        Assert.False(s.IsDeaf);
        Assert.False(s.IsCrippled);
        Assert.False(s.IsDumb);
        Assert.Equal(2,   s.TimeToReset);
        Assert.Equal('R', s.Weather);
    }

    [Fact]
    public void FesPacket_ParsesYnFlags_WhenSet()
    {
        // All status flags Y, weather S
        var h = new ParserHarness();
        h.Feed(0xA7, 0xA3, 0x9C, 0xFF, 0xFF);
        h.Feed("50 100 80 80 70 70 30 50 9999 Y Y Y Y 10 S\n");
        Assert.Single(h.Stats);
        var s = h.Stats[0];
        Assert.True(s.IsBlind);
        Assert.True(s.IsDeaf);
        Assert.True(s.IsCrippled);
        Assert.True(s.IsDumb);
        Assert.Equal('S', s.Weather);
    }

    // -- C08 stale-stats hints (debounced replacement for Clio's txfes) --------
    // The parser no longer probes directly; it emits ProbeHintReceived and the
    // session schedules the probe. No C1 code may produce OutgoingBytes any more.

    [Fact]
    public void C08_Bare_HintsStamina_LikeC08C00()
    {
        // 0xA3 0xFF 0xFF = bare C08 (fight starts). A missing parameter reads as 00, so this is
        // the 08 00 the wire carries, and hints exactly as it does.
        var h = new ParserHarness();
        h.Feed(0xA3, 0xFF, 0xFF);
        Assert.Equal([StaleStats.Stamina], h.ProbeHints);
        Assert.Empty(h.Outgoing);
    }

    [Fact]
    public void C08_C00_FightStarts_HintsStamina()
    {
        // 0xA3 0x9B 0xFF 0xFF = C08+C00, the form every fight start takes on the wire
        var h = new ParserHarness();
        h.Feed(0xA3, 0x9B, 0xFF, 0xFF);
        Assert.Equal([StaleStats.Stamina], h.ProbeHints);
        Assert.Empty(h.Outgoing);
    }

    [Fact]
    public void C08_C01_YouHitThem_DoesNotHint()
    {
        // 0xA3 0x9C 0xFF 0xFF = C08+C01, you hit them - YOUR stats are unchanged.
        // (Clio txfes'd this, which spammed a probe on every swing of a fight.)
        var h = new ParserHarness();
        h.Feed(0xA3, 0x9C, 0xFF, 0xFF);
        Assert.Empty(h.ProbeHints);
        Assert.Empty(h.Outgoing);
    }

    [Fact]
    public void C08_C02_DoesNotHint()
    {
        // 0xA3 0x9D 0xFF 0xFF = C08+C02 (telnet.l:635) -> plain RED, no hint
        var h = new ParserHarness();
        h.Feed(0xA3, 0x9D, 0xFF, 0xFF);
        Assert.Empty(h.ProbeHints);
    }

    [Fact]
    public void C08_C03_HintsStamina()
    {
        // 0xA3 0x9E 0xFF 0xFF = C08+C03, they hit you (telnet.l:628)
        var h = new ParserHarness();
        h.Feed(0xA3, 0x9E, 0xFF, 0xFF);
        Assert.Equal([StaleStats.Stamina], h.ProbeHints);
    }

    [Fact]
    public void C08_C04_DoesNotHint()
    {
        // 0xA3 0x9F 0xFF 0xFF = C08+C04 (telnet.l:636) -> plain RED, no hint
        var h = new ParserHarness();
        h.Feed(0xA3, 0x9F, 0xFF, 0xFF);
        Assert.Empty(h.ProbeHints);
    }

    [Fact]
    public void C08_C05_WeaponChange_HintsStaminaAndInventory()
    {
        // 0xA3 0xA0 0xFF 0xFF = C08+C05 (weapon change) -> stamina + the carried-weapon
        // line in the FEI list may both have changed
        var h = new ParserHarness();
        h.Feed(0xA3, 0xA0, 0xFF, 0xFF);
        Assert.Equal([StaleStats.Stamina | StaleStats.Inventory], h.ProbeHints);
    }

    [Fact]
    public void C08_C06_DroppedGuard_HintsInventory()
    {
        // 0xA3 0xA1 0xFF 0xFF = C08+C06 (dropped guard) -> inventory stale
        var h = new ParserHarness();
        h.Feed(0xA3, 0xA1, 0xFF, 0xFF);
        Assert.Equal([StaleStats.Inventory], h.ProbeHints);
    }

    [Fact]
    public void C08_C08_HintsStamina()
    {
        // 0xA3 0xA3 0xFF 0xFF = C08+C08, you killed them (telnet.l:641)
        var h = new ParserHarness();
        h.Feed(0xA3, 0xA3, 0xFF, 0xFF);
        Assert.Equal([StaleStats.Stamina], h.ProbeHints);
    }

    [Fact]
    public void C08_C13_PersonaWiped_ZeroesStatsWithoutHinting()
    {
        // 0xA3 0xA8 0xFF 0xFF = C08+C13 ("Not updating persona") -> score/sta/str/dex/mag
        // are zeroed locally; no probe can bring them back, so no hint.
        var h = new ParserHarness();
        h.Feed(0xA3, 0xA8, 0xFF, 0xFF);
        Assert.Empty(h.ProbeHints);
        var s = Assert.Single(h.Stats);
        Assert.Equal(0, s.Stamina);
        Assert.Equal(0, s.Score);
        Assert.Equal(0, s.Strength);
        Assert.Equal(0, s.Dexterity);
        Assert.Equal(0, s.CurrentMagic);
    }

    [Fact]
    public void C08_C13_PersonaWiped_FiresPersonaWipedEvent()
    {
        // Same wire sequence as above (C08+C13, "Not updating persona") also fires the
        // dedicated PersonaWiped event - the unambiguous protocol-level signal consumers should
        // key off instead of pattern-matching the rendered line text.
        var h = new ParserHarness();
        h.Feed(0xA3, 0xA8, 0xFF, 0xFF);
        Assert.Equal(1, h.PersonaWipedCount);
    }

    [Fact]
    public void C00InitialiseFrame_DoesNotMakeTextCoded()
    {
        // Wire order: C00 at the options menu stays at the bottom of the stack through game entry.
        // Text under that frame alone is un-coded output and still gives the plain-line FEI hint;
        // text inside any code's frame above it does not.
        var h = new ParserHarness();
        h.Feed(0x9B, 0xFF, 0xFF);               // C00 initialise
        h.Feed(0x9D, 0x9C, 0xFF, 0xFF);         // C02+C01: game entry, room short name
        h.Feed("Elizabethan tearoom.");
        h.Feed(0xFF, 0xFF);
        h.Feed("\r\n");
        h.ClearCounters();

        h.Feed("You drop the ancient scroll.\r\n");
        Assert.Equal([StaleStats.Inventory], h.ProbeHints);

        h.ClearCounters();
        h.Feed(0xA1, 0xFF, 0xFF);               // C06
        h.Feed("For your information: there are 150 mobiles still alive.");
        h.Feed(0xFF, 0xFF);
        h.Feed("\r\n");
        Assert.Empty(h.ProbeHints);
    }

    // -- C95 Rule A: account block ---------------------------------------------

    [Fact]
    public void C95_RuleA_PopulatesAccountIdAndPrivs_InSubsequentFesSnapshot()
    {
        // C95 Rule A wire: 0xFA 0xFF 0xFF + 5 fields + the 0xFF 0xFF that closes the block
        // Fields: licence, minclient, maxclient, account, privs
        var h = new ParserHarness();
        h.Feed(0xFA, 0xFF, 0xFF);
        h.Feed("57009120\r\n1\r\n1\r\nz00012305\r\n1\r\n");

        // C95 Rule A emits ClientModeData
        Assert.Single(h.ClientModeData);

        // FES snapshot after C95 should carry AccountId and Privs
        h.Feed(0xA7, 0xA3, 0x9C, 0xFF, 0xFF);
        h.Feed("81 81 94 94 95 95 50 50 1785 N N N N 5 S\n");
        Assert.NotEmpty(h.Stats);
        var snapshot = h.Stats.Last();
        Assert.Equal("z00012305", snapshot.AccountId);
        Assert.Equal(1, snapshot.Privs);
    }

    [Fact]
    public void C95_RuleA_TrimsTrailingNulsFromAccountFields()
    {
        var h = new ParserHarness();
        h.Feed(0xFA, 0xFF, 0xFF);
        h.Feed("57009120\r\n1\r\n1\r\nz00012305\r\0\n1\r\0\n");

        h.Feed(0xA7, 0xA3, 0x9C, 0xFF, 0xFF);
        h.Feed("81 81 94 94 95 95 50 50 1785 N N N N 5 S\n");
        var snapshot = h.Stats.Last();
        Assert.Equal("z00012305", snapshot.AccountId);
        Assert.Equal(1, snapshot.Privs);
    }

    // -- C95 Rule C: account logout --------------------------------------------

    [Fact]
    public void C95_RuleC_IsConsumedWithoutError()
    {
        // C95 Rule C: 0xFA 0x9E 0xFF 0xFF + account-name line + (0xFF 0xFF consumed in Normal)
        var h = new ParserHarness();
        h.Feed(0xFA, 0x9E, 0xFF, 0xFF);
        h.Feed("Z00012305\r\n");
        // Should produce no lines and no stats; the stream continues without error
        Assert.Empty(h.Lines);
        Assert.Empty(h.Stats);

        // Normal text after logout still works
        h.Feed("hello\n");
        Assert.Single(h.Lines);
    }

    // -- C95 closing pops, and 95 02 account change ------------------------------

    [Fact]
    public void C95_BlockCloser_IsAbsorbed_NotPoppedOffTheColourStack()
    {
        // Verbatim wire: every bare 95 block ends "1\r\n" FF FF. C95 pushes no frame, so that pop
        // must not unwind one pushed before it.
        var h = new ParserHarness();
        h.Feed(0x9B, 0xFF, 0xFF);               // C00
        h.Feed(0xA1, 0xFF, 0xFF);               // C06: LT_BLUE
        h.Feed(0xFA, 0xFF, 0xFF);
        h.Feed("57009120\r\n1\r\n1\r\nz00012863\r\n1\r\n");
        h.Feed(0xFF, 0xFF);
        h.Feed("after\n");

        Assert.Single(h.ClientModeData);
        var line = Assert.Single(h.Lines);
        Assert.Equal("after", line.PlainText);
        Assert.Equal(AnsiColor.BrightBlue, line.Spans[0].Style.Foreground);
    }

    [Fact]
    public void C95_BlockWithoutACloser_HandsTheNextByteBack()
    {
        var h = new ParserHarness();
        h.Feed(0xFA, 0xFF, 0xFF);
        h.Feed("57009120\r\n1\r\n1\r\nz00012863\r\n1\r\nafter\n");

        Assert.Single(h.ClientModeData);
        Assert.Equal("after", Assert.Single(h.Lines).PlainText);
    }

    [Fact]
    public void C95C02_AccountChange_ReadsTheTwoBracketedLines_AndSwallowsNothingAfter()
    {
        // Verbatim wire (every 95 02 instance): the new account ID and priv level, then the pop,
        // then the server's own output.
        byte[] wire =
        [
            0xFA, 0x9D, 0xFF, 0xFF, .. "Z00012863\r\n1\r\n"u8, 0xFF, 0xFF,
            0x1B, (byte)'-', (byte)'C', .. "Z00012863 logged in on pts/2.\r\n"u8,
        ];
        var h = new ParserHarness();
        h.Feed(0x9B, 0xFF, 0xFF);               // C00
        h.Feed(0xA1, 0xFF, 0xFF);               // C06: LT_BLUE
        h.Feed(wire);
        h.Feed("after\n");

        var loggedIn = h.Lines.Single(l => l.PlainText.EndsWith("logged in on pts/2.", StringComparison.Ordinal));
        Assert.Equal(AnsiColor.BrightBlue, loggedIn.Spans[^1].Style.Foreground);
        var after = h.Lines.Single(l => l.PlainText == "after");
        Assert.Equal(AnsiColor.BrightBlue, after.Spans[0].Style.Foreground);

        h.Feed(0xA7, 0xA3, 0x9C, 0xFF, 0xFF);
        h.Feed("81 81 94 94 95 95 50 50 1785 N N N N 5 S\n");
        var snapshot = h.Stats.Last();
        Assert.Equal("Z00012863", snapshot.AccountId);
        Assert.Equal(1, snapshot.Privs);
    }

    // -- Gap 1: bare FF FF pops color stack -----------------------------------

    [Fact]
    public void BareC255_PopsColorStack()
    {
        // Push WHITE/BLACK (C00), then LT_BLUE (C06), then bare FF FF restores WHITE/BLACK
        // Clio telnet.l:1040: {C255} -> pop()
        var h = new ParserHarness();
        h.Feed(0x9B, 0xFF, 0xFF);           // C00: push WHITE/BLACK
        h.Feed(0xA1, 0xFF, 0xFF);           // C06: push LT_BLUE/BLACK
        h.Feed("before\n");                 // line in LT_BLUE
        h.Feed(0xFF, 0xFF);                 // bare C255: pop LT_BLUE, restore WHITE/BLACK
        h.Feed("after\n");
        Assert.Equal(2, h.Lines.Count);
        var afterStyle = h.Lines[1].Spans[0].Style;
        Assert.Equal(AnsiColor.White, afterStyle.Foreground);
        Assert.Equal(AnsiColor.Black, afterStyle.Background);
    }

    [Fact]
    public void C00_InitStack_ResetsStackRatherThanPushingAnotherFrame()
    {
        var h = new ParserHarness();
        h.Feed(0x9B, 0xFF, 0xFF);           // baseline WHITE/BLACK
        h.Feed(0xA1, 0xFF, 0xFF);           // LT_BLUE/BLACK
        h.Feed(0x9B, 0xFF, 0xFF);           // next frame init_stack should reset, not push
        h.Feed(0xFF, 0xFF);                 // pop reset baseline
        h.Feed("after\n");
        var line = Assert.Single(h.Lines);
        var style = line.Spans[0].Style;
        Assert.Equal(AnsiColor.Default, style.Foreground);
        Assert.Equal(AnsiColor.Default, style.Background);
    }

    [Fact]
    public void BareC255_OnEmptyStack_IsIgnored()
    {
        // Bare FF FF with no prior pushes - should not crash or emit junk
        var h = new ParserHarness();
        h.Feed(0xFF, 0xFF);
        h.Feed("text\n");
        Assert.Single(h.Lines);
    }

    // -- C89 (0xF4) stamina / max stamina / score ----------------------------
    // MUD-FECodes.txt: every code is "terminating in 255". On the wire every C89 is, and it
    // brackets a C99 colour and the number: F4 9B 9B FF FF FE 9D FF FF "97" FF FF FF FF.

    [Theory]
    [InlineData(new byte[] { 0xF4, 0x9B, 0x9B })]   // 89 00 00 stamina
    [InlineData(new byte[] { 0xF4, 0x9B, 0x9C })]   // 89 00 01 maximum stamina
    [InlineData(new byte[] { 0xF4, 0x9C })]         // 89 01 score
    public void C89_WaitsForItsTerminator_ThenPushesWhite(byte[] code)
    {
        // Printable bytes before the FF FF are still the code's payload: an early dispatch leaks
        // them as text.
        var h = new ParserHarness();
        h.Feed(0x9B, 0xFF, 0xFF);               // C00
        h.Feed(0xA1, 0xFF, 0xFF);               // C06: LT_BLUE
        h.Feed(code);
        h.Feed("SWALLOWED");
        h.Feed(0xFF, 0xFF);
        h.Feed("text");
        h.Feed(0xFF, 0xFF);                     // the C89's own pop
        h.Feed(" after\n");

        var line = Assert.Single(h.Lines);
        Assert.Equal("text after", line.PlainText);
        Assert.Equal(new TextStyle(AnsiColor.White, AnsiColor.Black), line.Spans[0].Style);
        Assert.Equal(AnsiColor.BrightBlue, line.Spans[^1].Style.Foreground);
    }

    [Fact]
    public void C89_InlineStaminaInsideAHitLine_KeepsTheHitLineColour()
    {
        // Verbatim wire: "The rat22 hits you (97/100)." inside 08 03.
        byte[] wire =
        [
            0xA3, 0x9E, 0xFF, 0xFF, .. "The rat22 hits you ("u8,
            0xF4, 0x9B, 0x9B, 0xFF, 0xFF, 0xFE, 0x9D, 0xFF, 0xFF, .. "97"u8, 0xFF, 0xFF, 0xFF, 0xFF,
            (byte)'/',
            0xF4, 0x9B, 0x9C, 0xFF, 0xFF, 0xFE, 0xA5, 0xFF, 0xFF, .. "100"u8, 0xFF, 0xFF, 0xFF, 0xFF,
            .. ")."u8, 0xFF, 0xFF, (byte)'\r', 0x00, (byte)'\r', (byte)'\n',
        ];
        var h = new ParserHarness();
        h.Feed(0x9B, 0xFF, 0xFF);               // C00
        h.Feed(wire);
        h.Feed("after\n");

        var hit = h.Lines.Single(l => l.PlainText.StartsWith("The rat22", StringComparison.Ordinal));
        Assert.Equal("The rat22 hits you (97/100).", hit.PlainText);
        AnsiColor ColourOf(string text) => hit.Spans.Single(s => s.Text == text).Style.Foreground;
        Assert.Equal(AnsiColor.BrightRed,   ColourOf("The rat22 hits you ("));
        Assert.Equal(AnsiColor.Green,       ColourOf("97"));
        Assert.Equal(AnsiColor.BrightRed,   ColourOf("/"));
        Assert.Equal(AnsiColor.BrightGreen, ColourOf("100"));
        Assert.Equal(AnsiColor.BrightRed,   ColourOf(")."));
        var after = h.Lines.Single(l => l.PlainText == "after");
        Assert.Equal(new TextStyle(AnsiColor.White, AnsiColor.Black), after.Spans[0].Style);
    }

    [Fact]
    public void C89Score_InAPersonaSave_KeepsTheEnclosingColour()
    {
        // Verbatim wire: 89 01 around the saved score, with no enclosing code but C00's.
        byte[] wire =
        [
            .. "(Persona saved on +25 = "u8,
            0xF4, 0x9C, 0xFF, 0xFF, 0xFE, 0x9D, 0xFF, 0xFF, .. "26,345"u8, 0xFF, 0xFF, 0xFF, 0xFF,
            .. ").\r"u8, 0x00, (byte)'\r', (byte)'\n',
        ];
        var h = new ParserHarness();
        h.Feed(0x9B, 0xFF, 0xFF);               // C00
        h.Feed(wire);

        var line = h.Lines.Single(l => l.PlainText.StartsWith("(Persona", StringComparison.Ordinal));
        Assert.Equal("(Persona saved on +25 = 26,345).", line.PlainText);
        var white = new TextStyle(AnsiColor.White, AnsiColor.Black);
        Assert.Equal(white, line.Spans.Single(s => s.Text == "(Persona saved on +25 = ").Style);
        Assert.Equal(AnsiColor.Green, line.Spans.Single(s => s.Text == "26,345").Style.Foreground);
        Assert.Equal(white, line.Spans.Single(s => s.Text == ").").Style);
        var save = Assert.Single(h.ScoreSaves);
        Assert.Equal(25, save.Delta);
    }

    [Fact]
    public void C89_InAScoreBlock_LeavesTheStackBalanced()
    {
        // Verbatim wire: the stamina line of a 12 02 SCORE block, then the block's own pop.
        byte[] wire =
        [
            0xA7, 0x9D, 0xFF, 0xFF, .. "stamina:        "u8,
            0xF4, 0x9B, 0x9B, 0xFF, 0xFF, 0xFE, 0xA5, 0xFF, 0xFF, .. "100"u8, 0xFF, 0xFF, 0xFF, 0xFF,
            .. "     max:    "u8,
            0xF4, 0x9B, 0x9C, 0xFF, 0xFF, 0xFE, 0xA5, 0xFF, 0xFF, .. "100"u8, 0xFF, 0xFF, 0xFF, 0xFF,
            (byte)'\r', 0x00, (byte)'\r', (byte)'\n',
            .. "games played:   9\r"u8, 0x00, (byte)'\r', (byte)'\n',
            0xFF, 0xFF,
        ];
        var h = new ParserHarness();
        h.Feed(0x9B, 0xFF, 0xFF);               // C00
        h.Feed(0xA1, 0xFF, 0xFF);               // C06: LT_BLUE
        h.Feed(wire);
        h.Feed("after\n");

        var after = h.Lines.Single(l => l.PlainText == "after");
        Assert.Equal(AnsiColor.BrightBlue, after.Spans[0].Style.Foreground);
    }

    [Fact]
    public void Fes_WithC89WrappedFields_Parses_AndLeavesTheStackBalanced()
    {
        // Verbatim wire: the FES line carries its stamina and max stamina inside 89 00 00 / 89 00 01.
        byte[] wire =
        [
            0xA7, 0xA3, 0x9C, 0xFF, 0xFF,
            0xF4, 0x9B, 0x9B, 0xFF, 0xFF, 0xFE, 0xA5, 0xFF, 0xFF, .. "100"u8, 0xFF, 0xFF, 0xFF, 0xFF,
            (byte)' ',
            0xF4, 0x9B, 0x9C, 0xFF, 0xFF, 0xFE, 0xA5, 0xFF, 0xFF, .. "100"u8, 0xFF, 0xFF, 0xFF, 0xFF,
            .. " 99 99 99 99 0 100 5262 N N N N 90 F\r"u8, 0x00, (byte)'\r', (byte)'\n',
            0xFF, 0xFF,
        ];
        var h = new ParserHarness();
        h.Feed(0x9B, 0xFF, 0xFF);               // C00
        h.Feed(0xA1, 0xFF, 0xFF);               // C06: LT_BLUE
        h.Feed(wire);
        h.Feed("after\n");

        var s = Assert.Single(h.Stats);
        Assert.Equal(100, s.Stamina);
        Assert.Equal(100, s.MaxStamina);
        Assert.Equal((byte)10, s.StaminaColor);
        Assert.Equal(5262, s.Score);
        Assert.Equal(90, s.TimeToReset);
        Assert.Equal('F', s.Weather);
        var after = h.Lines.Single(l => l.PlainText == "after");
        Assert.Equal(AnsiColor.BrightBlue, after.Spans[0].Style.Foreground);
    }

    // -- Gap 3: FE FE FF FF special reset --------------------------------------

    [Fact]
    public void C99_FE_FE_ResetsToWhiteBlack()
    {
        // Clio telnet.l:1030: {C99}{C99}{C255} -> push(WHITE,BLACK) (not LT_WHITE from clamped index 99)
        var h = new ParserHarness();
        h.Feed(0xFE, 0xFE, 0xFF, 0xFF);
        h.Feed("text\n");
        Assert.Single(h.Lines);
        var style = h.Lines[0].Spans[0].Style;
        Assert.Equal(AnsiColor.White,  style.Foreground);
        Assert.Equal(AnsiColor.Black, style.Background);
    }

    // -- Stale-stats hints from the Clio txfes trigger set ---------------------
    // These codes previously sent an instant FES probe (txfes); they now emit
    // debounced ProbeHintReceived events instead, and never OutgoingBytes.

    [Fact]
    public void C06_Bare_DoesNotHint()
    {
        // C06+C255 (0xA1 FF FF) -> LT_BLUE. Clio txfes'd all C06 variants (telnet.l:562-580) but
        // the probe-noise policy classifies C06 announcements as needing no probe.
        var h = new ParserHarness();
        h.Feed(0xA1, 0xFF, 0xFF);
        Assert.Empty(h.ProbeHints);
        Assert.Empty(h.Outgoing);
    }

    [Fact]
    public void C06_WithC00_DoesNotHint()
    {
        // C06+C00+C255 (0xA1 0x9B FF FF) -> LT_BLUE, no probe (probe-noise policy)
        var h = new ParserHarness();
        h.Feed(0xA1, 0x9B, 0xFF, 0xFF);
        Assert.Empty(h.ProbeHints);
    }

    [Fact]
    public void C06_C06_DoesNotHint()
    {
        // C06+C06+C255 (0xA1 0xA1 FF FF) -> "Something magical" - sound only (Clio:581-584)
        var h = new ParserHarness();
        h.Feed(0xA1, 0xA1, 0xFF, 0xFF);
        Assert.Empty(h.ProbeHints);
    }

    [Fact]
    public void C07_Bare_HintsStamina()
    {
        // C07+C255 (0xA2 FF FF) -> RED + stamina hint (Clio txfes, telnet.l:587-590)
        var h = new ParserHarness();
        h.Feed(0xA2, 0xFF, 0xFF);
        Assert.Equal([StaleStats.Stamina], h.ProbeHints);
        Assert.Empty(h.Outgoing);
    }

    [Fact]
    public void C07_WithPayload_HintsStamina()
    {
        // C07+C00+C00+C255 -> RED + stamina hint (all C07 variants, Clio:592-609)
        var h = new ParserHarness();
        h.Feed(0xA2, 0x9B, 0x9B, 0xFF, 0xFF);
        Assert.Equal([StaleStats.Stamina], h.ProbeHints);
    }

    [Fact]
    public void C11_WithTxfesVariant_HintsAllStats()
    {
        // C11+C00 (0xA6 0x9B FF FF) -> LT_RED + hint (Clio txfes, telnet.l:687-713)
        var h = new ParserHarness();
        h.Feed(0xA6, 0x9B, 0xFF, 0xFF);
        Assert.Equal([StaleStats.AllStats], h.ProbeHints);
    }

    [Fact]
    public void C11_BareOrC06_DoesNotHint()
    {
        // C11+C06 (0xA6 0xA1 FF FF) -> LT_RED only - FOD/WHERE/SUMMON (Clio:675-685)
        var h = new ParserHarness();
        h.Feed(0xA6, 0xA1, 0xFF, 0xFF);
        Assert.Empty(h.ProbeHints);
    }

    [Fact]
    public void C11_Bare_HintsAllStats_LikeC11C00()
    {
        // C11+C255 (0xA6 FF FF) is 11 00 "Disabling spell starts" with its parameter missing, so it
        // hints as 11 00 does - Clio's rule grouping bare C11 with FOD/WHERE/SUMMON is not followed.
        var h = new ParserHarness();
        h.Feed(0xA6, 0xFF, 0xFF);
        Assert.Equal([StaleStats.AllStats], h.ProbeHints);
    }

    [Fact]
    public void C14_WithC00_HintsAllStats()
    {
        // C14+C00 (0xA9 0x9B FF FF) -> GREEN/BLACK + hint (Clio txfes, telnet.l:832)
        var h = new ParserHarness();
        h.Feed(0xA9, 0x9B, 0xFF, 0xFF);
        Assert.Equal([StaleStats.AllStats], h.ProbeHints);
    }

    [Fact]
    public void C14_WithC04_DoesNotHint()
    {
        // C14+C04+C00 (0xA9 0x9F 0x9B FF FF) -> sweather only (Clio telnet.l:875-885)
        var h = new ParserHarness();
        h.Feed(0xA9, 0x9F, 0x9B, 0xFF, 0xFF);
        Assert.Empty(h.ProbeHints);
    }

    [Fact]
    public void C14_WithC03C00_HintsAllStats()
    {
        // C14+C03+C00 (0xA9 0x9E 0x9B FF FF) -> GREEN/BLACK + hint (Clio telnet.l:852)
        var h = new ParserHarness();
        h.Feed(0xA9, 0x9E, 0x9B, 0xFF, 0xFF);
        Assert.Equal([StaleStats.AllStats], h.ProbeHints);
    }

    [Fact]
    public void C15_DreamwordClear_DoesNotHint()
    {
        // C15+C00+C01+C255 (0xAA 0x9B 0x9C FF FF) -> dreamword cleared. The C15 code itself carries
        // the change, so no probe (probe-noise policy; Clio txfes'd it).
        var h = new ParserHarness();
        h.Feed(0xAA, 0x9B, 0x9C, 0xFF, 0xFF);
        Assert.Empty(h.ProbeHints);
    }

    [Fact]
    public void C18_WithC00_HintsAllStats()
    {
        // C18+C00+C255 (0xAD 0x9B FF FF) -> WHITE/BLACK + hint (Clio telnet.l:944-957)
        var h = new ParserHarness();
        h.Feed(0xAD, 0x9B, 0xFF, 0xFF);
        Assert.Equal([StaleStats.AllStats], h.ProbeHints);
    }

    [Fact]
    public void C18_Bare_DoesNotHint()
    {
        // C18+C255 (0xAD FF FF) -> WHITE/BLACK only, no payload -> no hint
        var h = new ParserHarness();
        h.Feed(0xAD, 0xFF, 0xFF);
        Assert.Empty(h.ProbeHints);
    }

    // -- C03/C04 inventory hints (items/creatures changing the room contents) --

    [Fact]
    public void C03_ItemArriving_HintsInventory()
    {
        // 03 01 02 (non-treasure arriving): 0x9E 0x9C 0x9D FF FF
        var h = new ParserHarness();
        h.Feed(0x9E, 0x9C, 0x9D, 0xFF, 0xFF);
        Assert.Equal([StaleStats.Inventory], h.ProbeHints);
    }

    [Fact]
    public void C03_ItemDeparting_HintsInventory()
    {
        // 03 03 03 (treasure departing): 0x9E 0x9E 0x9E FF FF
        var h = new ParserHarness();
        h.Feed(0x9E, 0x9E, 0x9E, 0xFF, 0xFF);
        Assert.Equal([StaleStats.Inventory], h.ProbeHints);
    }

    [Fact]
    public void C03_ItemHere_DoesNotHint()
    {
        // 03 01 01 (non-treasure here - part of a look, not a change): no hint
        var h = new ParserHarness();
        h.Feed(0x9E, 0x9C, 0x9C, 0xFF, 0xFF);
        Assert.Empty(h.ProbeHints);
    }

    [Fact]
    public void C04_CreatureArriving_HintsInventory()
    {
        // 04 00 02 (normal creature arriving): 0x9F 0x9B 0x9D FF FF
        var h = new ParserHarness();
        h.Feed(0x9F, 0x9B, 0x9D, 0xFF, 0xFF);
        Assert.Equal([StaleStats.Inventory], h.ProbeHints);
    }

    [Fact]
    public void C04_CreatureHere_DoesNotHint()
    {
        // 04 00 01 (normal creature here): no hint
        var h = new ParserHarness();
        h.Feed(0x9F, 0x9B, 0x9C, 0xFF, 0xFF);
        Assert.Empty(h.ProbeHints);
    }

    // -- C05 presence-name capture (who-list staleness check) -----------------

    [Fact]
    public void C05_MortalArriving_EmitsPresenceName_AndDisplaysText()
    {
        var h = new ParserHarness();
        h.Feed(0x9D, 0x9C, 0xFF, 0xFF);   // C02+C01 -> game mode
        h.ClearCounters();
        // 05 00 02 (mortal arriving): 0xA0 0x9B 0x9D FF FF brackets the name
        h.Feed(0xA0, 0x9B, 0x9D, 0xFF, 0xFF);
        h.Feed("Polly the witch");
        h.Feed(0xFF, 0xFF);               // pop closes the bracket
        h.Feed(" has just arrived.\r\n");
        Assert.Equal(["Polly the witch"], h.PresenceNames);
        // An arrival changes the room's visible occupants -> FEI dirty. The who-list membership
        // check stays session policy, so no WhoList hint from the parser itself.
        Assert.Equal([StaleStats.Inventory], h.ProbeHints);
        var line = Assert.Single(h.Lines);
        Assert.Equal("Polly the witch has just arrived.", line.PlainText);
    }

    [Fact]
    public void C05_PresenceCode_OutsideGameMode_DoesNotCapture()
    {
        var h = new ParserHarness();
        h.Feed(0xA0, 0x9B, 0x9D, 0xFF, 0xFF);
        h.Feed("Polly");
        h.Feed(0xFF, 0xFF);
        h.Feed("\r\n");
        Assert.Empty(h.PresenceNames);
    }

    [Fact]
    public void C05_WhoListVariant_StillCapturesFewPlayer()
    {
        // 05 00 06 (mortal on WHO list) must keep flowing through FewPlayerReady,
        // not the presence-check path.
        var h = new ParserHarness();
        h.Feed(0x9D, 0x9C, 0xFF, 0xFF);   // game mode
        h.ClearCounters();
        h.Feed(0xA0, 0x9B, 0xA1, 0xFF, 0xFF);
        h.Feed("Polly the witch\n");
        Assert.Equal(["Polly the witch"], h.FewPlayers);
        Assert.Empty(h.PresenceNames);
    }

    // -- C02.02 long-description context (LongDescLineReady) ------------------

    [Fact]
    public void C02_02_SingleLine_FiresLongDescLineReady()
    {
        // C02.02 = 0x9D 0x9D 0xFF 0xFF -> GREEN/BLACK; text until pop fires LongDescLineReady.
        var h = new ParserHarness();
        h.Feed(0x9D, 0x9C, 0xFF, 0xFF);          // enter game mode (C02.01)
        h.Feed("Room Name\n");                    // room short - clears C02.01 push
        h.Feed(0xFF, 0xFF);                       // pop C02.01
        h.ClearCounters();
        h.Feed(0x9D, 0x9D, 0xFF, 0xFF);          // C02.02: enter long-desc context
        h.Feed("A winding path through the trees.\n");
        h.Feed(0xFF, 0xFF);                       // pop C02.02
        Assert.Equal(["A winding path through the trees."], h.LongDescLines);
    }

    [Fact]
    public void C02_02_MultiLine_FiresOneEventPerLine()
    {
        // Long descriptions can span multiple server-wrapped lines.
        var h = new ParserHarness();
        h.Feed(0x9D, 0x9C, 0xFF, 0xFF);
        h.Feed("Room Name\n");
        h.Feed(0xFF, 0xFF);
        h.ClearCounters();
        h.Feed(0x9D, 0x9D, 0xFF, 0xFF);
        h.Feed("First line of the description.\n");
        h.Feed("Second line of the description.\n");
        h.Feed(0xFF, 0xFF);
        Assert.Equal(2, h.LongDescLines.Count);
        Assert.Equal("First line of the description.",  h.LongDescLines[0]);
        Assert.Equal("Second line of the description.", h.LongDescLines[1]);
    }

    [Fact]
    public void C02_02_InnerColourNest_DoesNotEndScopeEarly()
    {
        // An inner colour push/pop inside the description (e.g. a highlighted word) returns the
        // stack to the C02.02 frame's depth - the scope must survive that and keep firing
        // LongDescLineReady until the C02.02 frame ITSELF pops. Scope lifetime is frame lifetime
        // (C1Scope), not stack depth.
        var h = new ParserHarness();
        h.Feed(0x9D, 0x9C, 0xFF, 0xFF);
        h.Feed("Room Name\n");
        h.Feed(0xFF, 0xFF);
        h.ClearCounters();
        h.Feed(0x9D, 0x9D, 0xFF, 0xFF);          // C02.02: long-desc scope opens
        h.Feed("A path past a ");
        h.Feed(0xFE, 0x9E, 0xFF, 0xFF);          // inner C99 colour (highlighted word)
        h.Feed("shrine");
        h.Feed(0xFF, 0xFF);                       // inner pop - back to the C02.02 frame depth
        h.Feed(".\n");
        h.Feed("Second line after the nest.\n");
        h.Feed(0xFF, 0xFF);                       // C02.02's own pop - scope ends
        h.Feed("Not part of the description.\n");
        Assert.Equal(2, h.LongDescLines.Count);
        Assert.Equal("A path past a shrine.",        h.LongDescLines[0]);
        Assert.Equal("Second line after the nest.",  h.LongDescLines[1]);
    }

    [Fact]
    public void C02_02_AlsoFiresNormalLineReady()
    {
        // LongDescLineReady fires in addition to LineReady, not instead.
        var h = new ParserHarness();
        h.Feed(0x9D, 0x9C, 0xFF, 0xFF);
        h.Feed("Room Name\n");
        h.Feed(0xFF, 0xFF);
        h.ClearCounters();
        h.Feed(0x9D, 0x9D, 0xFF, 0xFF);
        h.Feed("The long description.\n");
        h.Feed(0xFF, 0xFF);
        Assert.Single(h.LongDescLines);
        Assert.Single(h.Lines);
        Assert.Equal("The long description.", h.LongDescLines[0]);
        Assert.Equal("The long description.", h.Lines[0].PlainText);
    }

    [Fact]
    public void C02_01_RoomShort_DoesNotFireLongDescLineReady()
    {
        // Room short description (C02.01 = 0x9D 0x9C) must NOT fire LongDescLineReady.
        var h = new ParserHarness();
        h.Feed(0x9D, 0x9C, 0xFF, 0xFF);
        h.Feed("Foothills\n");
        h.Feed(0xFF, 0xFF);
        Assert.Empty(h.LongDescLines);
        Assert.Equal(["Foothills"], h.RoomShorts);
    }

    [Fact]
    public void C02_02_OutsideGameMode_DoesNotFireLongDescLineReady()
    {
        // LongDescLineReady must only fire in game mode.
        var h = new ParserHarness();
        h.Feed(0x9D, 0x9D, 0xFF, 0xFF);   // C02.02 before game mode entry
        h.Feed("pre-game text\n");
        h.Feed(0xFF, 0xFF);
        Assert.Empty(h.LongDescLines);
    }

    // -- ExitLineReady (exits-verb output parsing) -----------------------------

    [Fact]
    public void ExitLine_NorthFormat_FiresExitLineReady()
    {
        // Exits-verb line: "north: {C02.01}Foothills{/C02.01}." - direction word +
        // BrightGreen room name span.
        var h = new ParserHarness();
        h.Feed(0x9D, 0x9C, 0xFF, 0xFF);   // game mode
        h.ClearCounters();
        // Emit "north: " then C02.01 room name then ".\n"
        h.Feed("north: ");
        h.Feed(0x9D, 0x9C, 0xFF, 0xFF);   // C02.01 -> BrightGreen
        h.Feed("Foothills");
        h.Feed(0xFF, 0xFF);                // pop C02.01
        h.Feed(".\n");
        Assert.Single(h.ExitLines);
        Assert.Equal("north",     h.ExitLines[0].Dir);
        Assert.Equal("Foothills", h.ExitLines[0].Dest);
    }

    [Fact]
    public void ExitLine_MultipleDirections_FiresOneEventEach()
    {
        var h = new ParserHarness();
        h.Feed(0x9D, 0x9C, 0xFF, 0xFF);
        h.ClearCounters();

        void ExitFeed(string dir, string dest)
        {
            h.Feed($"{dir}: ");
            h.Feed(0x9D, 0x9C, 0xFF, 0xFF);
            h.Feed(dest);
            h.Feed(0xFF, 0xFF);
            h.Feed(".\n");
        }

        ExitFeed("north",     "Foothills");
        ExitFeed("northeast", "Foothills");
        ExitFeed("east",      "East pasture");

        Assert.Equal(3, h.ExitLines.Count);
        Assert.Equal(("north",     "Foothills"),    h.ExitLines[0]);
        Assert.Equal(("northeast", "Foothills"),    h.ExitLines[1]);
        Assert.Equal(("east",      "East pasture"), h.ExitLines[2]);
    }

    [Fact]
    public void ExitLine_SwampwardDirection_IsRecognised()
    {
        var h = new ParserHarness();
        h.Feed(0x9D, 0x9C, 0xFF, 0xFF);
        h.ClearCounters();
        h.Feed("swampward: ");
        h.Feed(0x9D, 0x9C, 0xFF, 0xFF);
        h.Feed("Rapids");
        h.Feed(0xFF, 0xFF);
        h.Feed(".\n");
        Assert.Single(h.ExitLines);
        Assert.Equal("swampward", h.ExitLines[0].Dir);
        Assert.Equal("Rapids",    h.ExitLines[0].Dest);
    }

    [Fact]
    public void ExitLine_OutsideGameMode_DoesNotFireExitLineReady()
    {
        // ExitLineReady must not fire before game mode is entered.
        var h = new ParserHarness();
        Assert.False(h.Parser.InGameMode);
        h.Feed("north: Foothills.\n");
        Assert.False(h.Parser.InGameMode);
        Assert.Empty(h.ExitLines);
    }

    [Fact]
    public void ExitLine_InLongDescContext_DoesNotFireExitLineReady()
    {
        // Inside a C02.02 long-description context, "direction: name." patterns
        // must not be misread as exit lines.
        var h = new ParserHarness();
        h.Feed(0x9D, 0x9C, 0xFF, 0xFF);
        h.ClearCounters();
        h.Feed(0x9D, 0x9D, 0xFF, 0xFF);   // C02.02: long-desc context
        h.Feed("north: the path leads onward.\n");
        h.Feed(0xFF, 0xFF);
        Assert.Single(h.LongDescLines);
        Assert.Empty(h.ExitLines);
    }

    // -- C09 speaker messages -> LineKind.Chat (chat-view filter) -----------------
    // C1 codes are byte-0x9B: C09 (speaker) = 0xA4, sub-codes C00=0x9B..C01(shout)=0x9C,
    // C02(say)=0x9D, C03(tell)=0x9E. C255 terminator = 0xFF 0xFF; a bare 0xFF 0xFF pops colour.

    [Fact]
    public void C09ShoutLine_IsTaggedChat()
    {
        // Faithful to a live capture: the line opens with C09+C00 (speaker) and the shouted word
        // is bracketed by C09+C01, each colour popped before the newline.
        //   A4 9B FF FF "...shouts \"" A4 9C FF FF "Hello" FF FF "\"." FF FF \n
        var h = new ParserHarness();
        h.Feed(0xA4, 0x9B, 0xFF, 0xFF);
        h.Feed("A male voice in the distance shouts \"");
        h.Feed(0xA4, 0x9C, 0xFF, 0xFF);
        h.Feed("Hello");
        h.Feed(0xFF, 0xFF);
        h.Feed("\".");
        h.Feed(0xFF, 0xFF);
        h.Feed("\n");
        var line = Assert.Single(h.Lines);
        Assert.Equal(LineKind.Chat, line.Kind);
    }

    [Fact]
    public void C09SayLine_IsTaggedChat()
    {
        // C09+C02 (said). From capture: 'Ollie the necromancer says "oippoo".'
        var h = new ParserHarness();
        h.Feed(0xA4, 0x9B, 0xFF, 0xFF);
        h.Feed("Ollie the necromancer says \"");
        h.Feed(0xA4, 0x9D, 0xFF, 0xFF);
        h.Feed("oippoo");
        h.Feed(0xFF, 0xFF);
        h.Feed("\".");
        h.Feed(0xFF, 0xFF);
        h.Feed("\n");
        Assert.Equal(LineKind.Chat, Assert.Single(h.Lines).Kind);
    }

    [Fact]
    public void NonSpeakerLine_IsTaggedNormal()
    {
        var h = new ParserHarness();
        h.Feed("You walk north into the foothills.\n");
        var line = Assert.Single(h.Lines);
        Assert.Equal(LineKind.Normal, line.Kind);
    }

    [Fact]
    public void C09WrappedMessage_TagsContinuationLinesChat()
    {
        // MUD2 wraps long output server-side across '\n' without re-emitting the introducing code
        // (same as room long-descriptions). While the C09 colour is still pushed, continuation
        // lines must stay Chat; once it pops, the next line is Normal again.
        var h = new ParserHarness();
        h.Feed(0xA4, 0x9C, 0xFF, 0xFF);   // shouted colour pushed, not yet popped
        h.Feed("this is a very long shout that the server has\n");
        h.Feed("split across two lines without repeating the code\n");
        h.Feed(0xFF, 0xFF);               // message ends - colour pops, chat context closes
        h.Feed("and now a normal room line.\n");
        Assert.Equal(3, h.Lines.Count);
        Assert.Equal(LineKind.Chat,   h.Lines[0].Kind);
        Assert.Equal(LineKind.Chat,   h.Lines[1].Kind);
        Assert.Equal(LineKind.Normal, h.Lines[2].Kind);
    }

    [Fact]
    public void C09WrappedMessage_WithInnerColour_KeepsContinuationChat()
    {
        // A wrapped speaker message whose first physical line completes an inner C09 colour scope
        // (e.g. a highlighted/quoted word) must still tag later continuation lines Chat. Regression
        // for the context-close comparison: '<=' closes at the inner pop (one level early) and would
        // drop line 1 to Normal; '<' holds the context until the C09's own colour pops.
        var h = new ParserHarness();
        h.Feed(0xA4, 0x9C, 0xFF, 0xFF);          // outer shout colour pushed
        h.Feed("a long shout with a ");
        h.Feed(0xA4, 0x9C, 0xFF, 0xFF);          // inner nested colour (highlighted word)
        h.Feed("word");
        h.Feed(0xFF, 0xFF);                       // inner pop - back to the C09 base depth
        h.Feed(" and more that wraps\n");         // line 0
        h.Feed("onto a second wrapped line\n");   // line 1 - after the inner pop; must stay Chat
        h.Feed(0xFF, 0xFF);                       // outer pop - message ends
        h.Feed("a normal room line.\n");          // line 2
        Assert.Equal(3, h.Lines.Count);
        Assert.Equal(LineKind.Chat,   h.Lines[0].Kind);
        Assert.Equal(LineKind.Chat,   h.Lines[1].Kind);
        Assert.Equal(LineKind.Normal, h.Lines[2].Kind);
    }

    [Fact]
    public void C09WrappedMessage_MarksContinuationLines()
    {
        // ContinuesChat is the parser's "same message as the previous line" fact: false on the
        // line that carries the C09 code (the scope opens mid-line, after the line started),
        // true on every server-wrapped row while the scope stays open, and never set once the
        // colour pops. This is what downstream per-message state (the chat colouring's whose-message verdict) keys on.
        var h = new ParserHarness();
        h.Feed(0xA4, 0x9C, 0xFF, 0xFF);
        h.Feed("a long shout that the server wraps\n");
        h.Feed("across a second line\n");
        h.Feed("and a third\n");
        h.Feed(0xFF, 0xFF);
        h.Feed("a normal room line.\n");
        Assert.Equal(4, h.Lines.Count);
        Assert.False(h.Lines[0].ContinuesChat);   // message start - C09 arrived on this line
        Assert.True(h.Lines[1].ContinuesChat);
        Assert.True(h.Lines[2].ContinuesChat);
        Assert.False(h.Lines[3].ContinuesChat);   // scope popped before this line
    }

    [Fact]
    public void C09WrappedMessage_PopBeforeFinalNewline_LastLineStaysChatContinuation()
    {
        // Live `say` shape: the closing pop arrives BEFORE the final line's newline, exactly as
        // it does for single-line messages ("speaker messages pop their colour before their own
        // newline"). The last line's TEXT was emitted inside the scope, so it is still Chat and
        // still a continuation - checking the scope only at the '\n' would drop exactly the final
        // wrapped row out of Chat, an N-line say losing the self colours on line N.
        var h = new ParserHarness();
        h.Feed(0xA4, 0x9B, 0xFF, 0xFF);
        h.Feed("Ollie says \"a long message that the server\n");
        h.Feed("wraps and wraps\n");
        h.Feed("onto a final line\".");
        h.Feed(0xFF, 0xFF);               // pop BEFORE the final newline
        h.Feed("\n");
        h.Feed("A rat bites you.\n");
        Assert.Equal(4, h.Lines.Count);
        Assert.Equal(LineKind.Chat,   h.Lines[0].Kind);
        Assert.Equal(LineKind.Chat,   h.Lines[1].Kind);
        Assert.Equal(LineKind.Chat,   h.Lines[2].Kind);
        Assert.Equal(LineKind.Normal, h.Lines[3].Kind);
        Assert.False(h.Lines[0].ContinuesChat);
        Assert.True(h.Lines[1].ContinuesChat);
        Assert.True(h.Lines[2].ContinuesChat);
        Assert.False(h.Lines[3].ContinuesChat);
    }

    [Fact]
    public void C09BackToBackMessages_DoNotChainContinuation()
    {
        // Two complete single-line messages in a row: the second is a fresh message (its own C09,
        // scope closed in between), never a continuation of the first.
        var h = new ParserHarness();
        h.Feed(0xA4, 0x9B, 0xFF, 0xFF);
        h.Feed("Ollie says \"one\".");
        h.Feed(0xFF, 0xFF);
        h.Feed("\n");
        h.Feed(0xA4, 0x9B, 0xFF, 0xFF);
        h.Feed("Bob says \"two\".");
        h.Feed(0xFF, 0xFF);
        h.Feed("\n");
        Assert.Equal(2, h.Lines.Count);
        Assert.False(h.Lines[0].ContinuesChat);
        Assert.False(h.Lines[1].ContinuesChat);
    }

    [Fact]
    public void C09WrappedMessage_WithInnerColour_StillMarksContinuation()
    {
        // Same shape as the inner-nest Kind regression: an inner C09 scope completing on line 0
        // must not make line 1 look like a message start - it is still a continuation.
        var h = new ParserHarness();
        h.Feed(0xA4, 0x9C, 0xFF, 0xFF);          // outer shout colour pushed
        h.Feed("a long shout with a ");
        h.Feed(0xA4, 0x9C, 0xFF, 0xFF);          // inner nested colour (highlighted word)
        h.Feed("word");
        h.Feed(0xFF, 0xFF);                       // inner pop - back to the C09 base depth
        h.Feed(" and more that wraps\n");         // line 0
        h.Feed("onto a second wrapped line\n");   // line 1
        h.Feed(0xFF, 0xFF);                       // outer pop - message ends
        Assert.Equal(2, h.Lines.Count);
        Assert.False(h.Lines[0].ContinuesChat);
        Assert.True(h.Lines[1].ContinuesChat);
    }

    [Fact]
    public void ChatKind_DoesNotLeakToNextLine()
    {
        // A single-line shout (colour popped before its newline, as MUD2 sends it) must not tag
        // the following unrelated line.
        var h = new ParserHarness();
        h.Feed(0xA4, 0x9B, 0xFF, 0xFF);
        h.Feed("Someone shouts \"oi\".");
        h.Feed(0xFF, 0xFF);
        h.Feed("\n");
        h.Feed("A rat bites you.\n");
        Assert.Equal(2, h.Lines.Count);
        Assert.Equal(LineKind.Chat,   h.Lines[0].Kind);
        Assert.Equal(LineKind.Normal, h.Lines[1].Kind);
    }

    [Fact]
    public void PendingChatKind_ClearedByReset()
    {
        // A C09 seen with no completing newline (e.g. a disconnect mid-message) must not survive
        // a parser Reset and tag the first line of the next session.
        var h = new ParserHarness();
        h.Feed(0xA4, 0x9C, 0xFF, 0xFF);   // C09 seen, no newline yet
        h.Parser.Reset();
        h.Feed("first line of a fresh session.\n");
        Assert.Equal(LineKind.Normal, Assert.Single(h.Lines).Kind);
    }
}
