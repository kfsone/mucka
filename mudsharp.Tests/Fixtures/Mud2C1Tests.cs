using MudSharp.Models;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// Golden byte-stream tests for the MUD2 proprietary C1 protocol (lead bytes 0x9B–0xFE).
/// All sequences end with the C255 terminator (0xFF 0xFF).
/// Byte sequences derived from Clio telnet.l C00–C99 rules.
///
/// Define our own indexes for color mapping:
///   BLACK=0  RED=1  GREEN=2  YELLOW=3  BLUE=4  MAGENTA=5  CYAN=6  WHITE=7
///   LT_BLACK=8  LT_RED=9  LT_GREEN=10  LT_YELLOW=11  LT_BLUE=12  LT_MAGENTA=13  LT_CYAN=14  LT_WHITE=15
/// </summary>
public class Mud2C1Tests
{
    // ── Game-mode entry ────────────────────────────────────────────────────────

    [Fact]
    public void GameModeEntrySignal_0x9D_0x9C_FiresGameModeEntered()
    {
        // Clio telnet.l line 473–488: C02+C01+C255 (0x9D 0x9C 0xFF 0xFF)
        // → push(LT_GREEN,BLACK) + mode=GAME + txfes()
        var h = new ParserHarness();
        h.Feed(0x9D, 0x9C, 0xFF, 0xFF);
        Assert.Equal(1, h.GameModeEnteredCount);
    }

    // ── FES packet ─────────────────────────────────────────────────────────────

    [Fact]
    public void FesPacket_ParsesStamina()
    {
        // Clio telnet.l line 728: C12+C08+C01+C255 (0xA7 0xA3 0x9C 0xFF 0xFF) → FES data line follows
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

    // ── C1 color codes ────────────────────────────────────────────────────────

    [Fact]
    public void C1Color_0x9B_SetsExpectedStyle()
    {
        // C00 (0x9B) + C255 → Clio init_stack(WHITE,BLACK)
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
        // C03+C01 (0x9E 0x9C) + C255 → Clio push(CYAN,BLACK)
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
        // C02+C01 (0x9D 0x9C) + C255 → Clio push(LT_GREEN,BLACK) + mode=GAME
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
        // C06 (0xA1) + C255 → Clio push(LT_BLUE,BLACK)
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
        // C09+C00 (0xA4 0x9B) + C255 → Clio push(YELLOW,BLACK)
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
        // C09+C01 (0xA4 0x9C) + C255 → Clio push(LT_YELLOW,BLACK)
        var h = new ParserHarness();
        h.Feed(0xA4, 0x9C, 0xFF, 0xFF);
        h.Feed("text\n");
        Assert.Single(h.Lines);
        var style = h.Lines[0].Spans[0].Style;
        Assert.Equal(AnsiColor.BrightYellow, style.Foreground);
        Assert.Equal(AnsiColor.Black, style.Background);
    }

    [Fact]
    public void C1Color_0xA6_SetsLtRed()
    {
        // C11 (0xA6) + C255 → Clio push(LT_RED,BLACK) (FOD/WHERE/SUMMON spells)
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
        // C12+C04 (0xA7 0x9F) + C255 → Clio push(GREEN,BLACK)
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

    // ── C98 correctness ────────────────────────────────────────────────────────

    [Fact]
    public void C98_SetsColor_AndSetsPromptAllowed()
    {
        // C98 (0xFD) + even payload byte → BLACK/BLUE; also fires ShowPrompt()
        // 0x9C = 156, 156 % 2 == 0 → Apply(BLACK, BLUE)
        var h = new ParserHarness();
        h.Feed("prompt: ");           // accumulate text
        h.Feed(0xFD, 0x9C, 0xFF, 0xFF); // C98 with even byte → BLACK/BLUE + ShowPrompt()

        // ShowPrompt() should have emitted the accumulated "prompt: " as a partial line
        Assert.Single(h.Lines);
        Assert.True(h.Lines[0].IsPartial);

        // Text emitted after C98 should carry the BLACK/BLUE color.
        // EmitPartialLine retains spans, so Lines[1] has: Spans[0]="prompt: " (Default),
        // Spans[1]="after" (Black/Blue from C98's Apply).
        h.Feed("after\n");
        Assert.Equal(2, h.Lines.Count);
        var style = h.Lines[1].Spans.Last().Style;
        Assert.Equal(AnsiColor.Black, style.Foreground);
        Assert.Equal(AnsiColor.Blue,  style.Background);
    }

    // ── C14 weather colors ────────────────────────────────────────────────────

    [Fact]
    public void C14_WithC03C01_SetsBlackWhite()
    {
        // Clio telnet.l: {C14}{C03}{C01}{C255} → push(BLACK,WHITE) (snow)
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
        // Clio telnet.l: {C14}{C03}{C03}{C255} → push(BLACK,WHITE) (snow)
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
        // Clio telnet.l: {C14}{C05}{C01}{C255} → push(BLACK,WHITE) (snow)
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
        // Clio telnet.l: {C14}{C00}{C255} → push(GREEN,BLACK) (fine weather)
        // 0xA9 0x9B 0xFF 0xFF
        var h = new ParserHarness();
        h.Feed(0xA9, 0x9B, 0xFF, 0xFF);
        h.Feed("text\n");
        Assert.Single(h.Lines);
        var style = h.Lines[0].Spans[0].Style;
        Assert.Equal(AnsiColor.Green, style.Foreground);
        Assert.Equal(AnsiColor.Black, style.Background);
    }

    // ── Dreamword ─────────────────────────────────────────────────────────────    [Fact]
    public void DreamwordSequence_EmitsDreamwordChanged()
    {
        // Clio telnet.l line 904–915: C15+C00+C00+C255 followed by [a-z]{1,14}
        // 0xAA 0x9B 0x9B 0xFF 0xFF → DreamwordData state; letters until non-[a-z]
        var h = new ParserHarness();
        h.Feed(0xAA, 0x9B, 0x9B, 0xFF, 0xFF);
        h.Feed("sword\n"); // '\n' terminates the dreamword and is replayed as newline
        Assert.Single(h.Dreamwords);
        Assert.Equal("sword", h.Dreamwords[0]);
    }

    [Fact]
    public void DreamwordClear_EmitsNullDreamword()
    {
        // Clio telnet.l line 916–925: C15+C00+C01+C255 → dreamword cleared (memset to 0)
        // 0xAA 0x9B 0x9C 0xFF 0xFF → EmitDreamwordChanged(null)
        var h = new ParserHarness();
        h.Feed(0xAA, 0x9B, 0x9C, 0xFF, 0xFF);
        Assert.Single(h.Dreamwords);
        Assert.Null(h.Dreamwords[0]);
    }

    // ── C95 client mode ────────────────────────────────────────────────────────

    [Fact]
    public void C95_EmitsClientModeData()
    {
        // Clio telnet.l line 975–1015: C95+C255 → collect 5 newline-terminated lines
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
        // C01 (0x9C) + C255 → b0==0, not in {0x9C,0x9D,0x9E} → BLUE/BLACK
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
        // C04+C01 (0x9F 0x9C) + C255 → b0==0x9C → LT_MAGENTA/BLACK
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
        // C04+C00 (0x9F 0x9B) + C255 → b0!=0x9C → MAGENTA/BLACK
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
        // C05+C01 (0xA0 0x9C) + C255 → b0==0x9C, count==1 → LT_RED/BLACK
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
        // C05+C00 (0xA0 0x9B) + C255 → b0==0x9B → RED/BLACK
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
        // C07 (0xA2) + C255 → RED/BLACK (important messages)
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
        // C08+C08 (0xA3 0xA3) + C255 → b0==0xA3 → BLACK/RED (death/combat)
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
        // C10+C00 (0xA5 0x9B) + C255 → b0==0x9B → BLACK/YELLOW
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
        // C13 (0xA8) + C255 → WHITE/BLACK (system messages)
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
        // C16 (0xAB) + C255 → LT_WHITE/BLUE (house messages)
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
        // C14 (0xA9) + C255 → GREEN/BLACK (weather/outdoor default)
        var h = new ParserHarness();
        h.Feed(0xA9, 0xFF, 0xFF);
        h.Feed("text\n");
        Assert.Single(h.Lines);
        var style = h.Lines[0].Spans[0].Style;
        Assert.Equal(AnsiColor.Green, style.Foreground);
        Assert.Equal(AnsiColor.Black, style.Background);
    }

    // ── Reset ──────────────────────────────────────────────────────────────────

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

    // ── FES 15-field parsing ───────────────────────────────────────────────────

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

    // ── C08 txfes triggers ────────────────────────────────────────────────────

    [Fact]
    public void C08_C00_DoesNotEmitFesSubscription()
    {
        // 0xA3 0x9B 0xFF 0xFF = C08+C00 (telnet.l:634) → plain RED, NO txfes
        var h = new ParserHarness();
        h.Feed(0xA3, 0x9B, 0xFF, 0xFF);
        Assert.Empty(h.Outgoing);
    }

    [Fact]
    public void C08_C01_EmitsFesSubscription()
    {
        // 0xA3 0x9C 0xFF 0xFF = C08+C01 (telnet.l:623)
        var h = new ParserHarness();
        h.Feed(0xA3, 0x9C, 0xFF, 0xFF);
        Assert.Single(h.Outgoing);
        Assert.Equal(new byte[] { 0x1B, 0x2D, 0x5B, 0x46, 0x45, 0x53, 0x1B, 0x2D, 0x5D }, h.Outgoing[0]);
    }

    [Fact]
    public void C08_C02_DoesNotEmitFesSubscription()
    {
        // 0xA3 0x9D 0xFF 0xFF = C08+C02 (telnet.l:635) → plain RED, NO txfes
        var h = new ParserHarness();
        h.Feed(0xA3, 0x9D, 0xFF, 0xFF);
        Assert.Empty(h.Outgoing);
    }

    [Fact]
    public void C08_C03_EmitsFesSubscription()
    {
        // 0xA3 0x9E 0xFF 0xFF = C08+C03 (telnet.l:628)
        var h = new ParserHarness();
        h.Feed(0xA3, 0x9E, 0xFF, 0xFF);
        Assert.Single(h.Outgoing);
        Assert.Equal(new byte[] { 0x1B, 0x2D, 0x5B, 0x46, 0x45, 0x53, 0x1B, 0x2D, 0x5D }, h.Outgoing[0]);
    }

    [Fact]
    public void C08_C04_DoesNotEmitFesSubscription()
    {
        // 0xA3 0x9F 0xFF 0xFF = C08+C04 (telnet.l:636) → plain RED, NO txfes
        var h = new ParserHarness();
        h.Feed(0xA3, 0x9F, 0xFF, 0xFF);
        Assert.Empty(h.Outgoing);
    }

    [Fact]
    public void C08_C08_EmitsFesSubscription()
    {
        // 0xA3 0xA3 0xFF 0xFF = C08+C08 (telnet.l:641)
        var h = new ParserHarness();
        h.Feed(0xA3, 0xA3, 0xFF, 0xFF);
        Assert.Single(h.Outgoing);
        Assert.Equal(new byte[] { 0x1B, 0x2D, 0x5B, 0x46, 0x45, 0x53, 0x1B, 0x2D, 0x5D }, h.Outgoing[0]);
    }

    [Fact]
    public void C08_NonTxfesVariant_DoesNotEmitFesSubscription()
    {
        // 0xA3 0xA0 0xFF 0xFF = C08+C05 — NOT a txfes trigger → no outgoing bytes
        var h = new ParserHarness();
        h.Feed(0xA3, 0xA0, 0xFF, 0xFF);
        Assert.Empty(h.Outgoing);
    }

    // ── C95 Rule A: account block ─────────────────────────────────────────────

    [Fact]
    public void C95_RuleA_PopulatesAccountIdAndPrivs_InSubsequentFesSnapshot()
    {
        // C95 Rule A wire: 0xFA 0xFF 0xFF + 5 fields + (trailing 0xFF 0xFF consumed silently)
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

    // ── C95 Rule C: account logout ────────────────────────────────────────────

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

    // ── Gap 1: bare FF FF pops color stack ───────────────────────────────────

    [Fact]
    public void BareC255_PopsColorStack()
    {
        // Push WHITE/BLACK (C00), then LT_BLUE (C06), then bare FF FF restores WHITE/BLACK
        // Clio telnet.l:1040: {C255} → pop()
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
    public void BareC255_OnEmptyStack_IsIgnored()
    {
        // Bare FF FF with no prior pushes — should not crash or emit junk
        var h = new ParserHarness();
        h.Feed(0xFF, 0xFF);
        h.Feed("text\n");
        Assert.Single(h.Lines);
    }

    // ── Gap 2: C89 (0xF4) non-terminated wire format ─────────────────────────

    [Fact]
    public void C89_F4_9C_SetsWhiteBlack()
    {
        // Clio telnet.l:968: {C89}{C01} → push(WHITE,BLACK); NO FF FF terminator
        var h = new ParserHarness();
        h.Feed(0xF4, 0x9C);                 // F4 9C — no terminator
        h.Feed("text\n");
        Assert.Single(h.Lines);
        var style = h.Lines[0].Spans[0].Style;
        Assert.Equal(AnsiColor.White, style.Foreground);
        Assert.Equal(AnsiColor.Black, style.Background);
    }

    [Fact]
    public void C89_F4_9B_9B_SetsWhiteBlack()
    {
        // Clio telnet.l:966: {C89}{C00}{C00} → push(WHITE,BLACK); NO FF FF terminator
        var h = new ParserHarness();
        h.Feed(0xF4, 0x9B, 0x9B);           // F4 9B 9B — no terminator
        h.Feed("text\n");
        Assert.Single(h.Lines);
        var style = h.Lines[0].Spans[0].Style;
        Assert.Equal(AnsiColor.White, style.Foreground);
        Assert.Equal(AnsiColor.Black, style.Background);
    }

    [Fact]
    public void C89_F4_9B_9C_SetsWhiteBlack()
    {
        // Clio telnet.l:967: {C89}{C00}{C01} → push(WHITE,BLACK); NO FF FF terminator
        var h = new ParserHarness();
        h.Feed(0xF4, 0x9B, 0x9C);           // F4 9B 9C — no terminator
        h.Feed("text\n");
        Assert.Single(h.Lines);
        var style = h.Lines[0].Spans[0].Style;
        Assert.Equal(AnsiColor.White, style.Foreground);
        Assert.Equal(AnsiColor.Black, style.Background);
    }

    [Fact]
    public void C89_F4_UnrecognisedPayload_WaitsForTerminator()
    {
        // F4 9D xx FF FF — first payload byte is not 0x9B or 0x9C, so must wait for FF FF
        // The sequence should NOT dispatch early; text after FF FF should be in the applied color.
        var h = new ParserHarness();
        h.Feed(0xF4, 0x9D, 0x9B, 0xFF, 0xFF);  // unrecognised C89 variant, terminated normally
        h.Feed("text\n");
        Assert.Single(h.Lines);                 // parser recovered and emitted text
    }

    // ── Gap 3: FE FE FF FF special reset ──────────────────────────────────────

    [Fact]
    public void C99_FE_FE_ResetsToWhiteBlack()
    {
        // Clio telnet.l:1030: {C99}{C99}{C255} → push(WHITE,BLACK) (not LT_WHITE from clamped index 99)
        var h = new ParserHarness();
        h.Feed(0xFE, 0xFE, 0xFF, 0xFF);
        h.Feed("text\n");
        Assert.Single(h.Lines);
        var style = h.Lines[0].Spans[0].Style;
        Assert.Equal(AnsiColor.White,  style.Foreground);
        Assert.Equal(AnsiColor.Black, style.Background);
    }

    // ── Gap 4: missing txfes triggers ─────────────────────────────────────────

    [Fact]
    public void C06_Bare_EmitsFesSubscription()
    {
        // C06+C255 (0xA1 FF FF) → LT_BLUE + txfes (Clio telnet.l:562-580)
        var h = new ParserHarness();
        h.Feed(0xA1, 0xFF, 0xFF);
        Assert.Single(h.Outgoing);
    }

    [Fact]
    public void C06_WithC00_EmitsFesSubscription()
    {
        // C06+C00+C255 (0xA1 0x9B FF FF) → LT_BLUE + txfes
        var h = new ParserHarness();
        h.Feed(0xA1, 0x9B, 0xFF, 0xFF);
        Assert.Single(h.Outgoing);
    }

    [Fact]
    public void C06_C06_DoesNotEmitFesSubscription()
    {
        // C06+C06+C255 (0xA1 0xA1 FF FF) → "Something magical" — sound only, NO txfes (Clio:581-584)
        var h = new ParserHarness();
        h.Feed(0xA1, 0xA1, 0xFF, 0xFF);
        Assert.Empty(h.Outgoing);
    }

    [Fact]
    public void C07_Bare_EmitsFesSubscription()
    {
        // C07+C255 (0xA2 FF FF) → RED + txfes (Clio telnet.l:587-590)
        var h = new ParserHarness();
        h.Feed(0xA2, 0xFF, 0xFF);
        Assert.Single(h.Outgoing);
    }

    [Fact]
    public void C07_WithPayload_EmitsFesSubscription()
    {
        // C07+C00+C00+C255 → RED + txfes (all C07 variants, Clio:592-609)
        var h = new ParserHarness();
        h.Feed(0xA2, 0x9B, 0x9B, 0xFF, 0xFF);
        Assert.Single(h.Outgoing);
    }

    [Fact]
    public void C11_WithTxfesVariant_EmitsFesSubscription()
    {
        // C11+C00 (0xA6 0x9B FF FF) → LT_RED + txfes (Clio telnet.l:687-713)
        var h = new ParserHarness();
        h.Feed(0xA6, 0x9B, 0xFF, 0xFF);
        Assert.Single(h.Outgoing);
    }

    [Fact]
    public void C11_BareOrC06_DoesNotEmitFesSubscription()
    {
        // C11+C06 (0xA6 0xA1 FF FF) → LT_RED only — FOD/WHERE/SUMMON (Clio:675-685)
        var h = new ParserHarness();
        h.Feed(0xA6, 0xA1, 0xFF, 0xFF);
        Assert.Empty(h.Outgoing);
    }

    [Fact]
    public void C11_Bare_DoesNotEmitFesSubscription()
    {
        // C11+C255 (0xA6 FF FF) → LT_RED only (Clio:675)
        var h = new ParserHarness();
        h.Feed(0xA6, 0xFF, 0xFF);
        Assert.Empty(h.Outgoing);
    }

    [Fact]
    public void C14_WithC00_EmitsFesSubscription()
    {
        // C14+C00 (0xA9 0x9B FF FF) → GREEN/BLACK + always txfes (Clio telnet.l:832)
        var h = new ParserHarness();
        h.Feed(0xA9, 0x9B, 0xFF, 0xFF);
        Assert.Single(h.Outgoing);
    }

    [Fact]
    public void C14_WithC04_DoesNotEmitFesSubscription()
    {
        // C14+C04+C00 (0xA9 0x9F 0x9B FF FF) → sweather only, NO txfes (Clio telnet.l:875-885)
        var h = new ParserHarness();
        h.Feed(0xA9, 0x9F, 0x9B, 0xFF, 0xFF);
        Assert.Empty(h.Outgoing);
    }

    [Fact]
    public void C14_WithC03C00_EmitsFesSubscription()
    {
        // C14+C03+C00 (0xA9 0x9E 0x9B FF FF) → GREEN/BLACK + txfes (Clio telnet.l:852)
        var h = new ParserHarness();
        h.Feed(0xA9, 0x9E, 0x9B, 0xFF, 0xFF);
        Assert.Single(h.Outgoing);
    }

    [Fact]
    public void C15_DreamwordClear_EmitsFesSubscription()
    {
        // C15+C00+C01+C255 (0xAA 0x9B 0x9C FF FF) → dreamword cleared + txfes (Clio telnet.l:916-925)
        var h = new ParserHarness();
        h.Feed(0xAA, 0x9B, 0x9C, 0xFF, 0xFF);
        Assert.Single(h.Outgoing);
    }

    [Fact]
    public void C18_WithC00_EmitsFesSubscription()
    {
        // C18+C00+C255 (0xAD 0x9B FF FF) → WHITE/BLACK + txfes (Clio telnet.l:944-957)
        var h = new ParserHarness();
        h.Feed(0xAD, 0x9B, 0xFF, 0xFF);
        Assert.Single(h.Outgoing);
    }

    [Fact]
    public void C18_Bare_DoesNotEmitFesSubscription()
    {
        // C18+C255 (0xAD FF FF) → WHITE/BLACK only, no payload → no txfes
        var h = new ParserHarness();
        h.Feed(0xAD, 0xFF, 0xFF);
        Assert.Empty(h.Outgoing);
    }
}
