using MudSharp.Models;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// Forms the FE documents give that have not been seen on the wire. Each test pins what
/// mud2_FE4.txt / MUD-FECodes.txt say the form means:
/// <list type="bullet">
/// <item>a missing trailing parameter reads as 00 (MUD-FECodes.txt's handleCode takes the
///       param[1]==0 branch for a bare code), so a bare code behaves as its explicit-00 form;</item>
/// <item>C90: "90" (and 90 00) is a catch, anything else a throw, and a catch saves the whole
///       colour stack - "a new stack is begun; when a throw is issued, the old stack is
///       reinstated";</item>
/// <item>the snoop codes 94 / 96 / 97 / 98;</item>
/// <item>the documented 14-field FES line (the wire adds a 15th, weather);</item>
/// <item>the server's named escapes $-C, $-K, $-R, $-r, $-Q.</item>
/// </list>
/// </summary>
public class FeDocumentedFormsTests
{
    // The frame C00 leaves at the bottom of the stack in InGameMode().
    private static readonly TextStyle InitStackStyle = new(AnsiColor.White, AnsiColor.Black);

    private static ParserHarness InGameMode()
    {
        var h = new ParserHarness();
        h.Feed(0x9D, 0x9C, 0xFF, 0xFF);  // C02+C01: enter game mode
        h.Feed("setup\n");
        h.Feed(0x9B, 0xFF, 0xFF);          // C00: init_stack
        h.ClearCounters();
        return h;
    }

    private static TextStyle StyleOf(params byte[] code)
    {
        var h = new ParserHarness();
        h.Feed(code);
        h.Feed("text\n");
        return Assert.Single(h.Lines).Spans[0].Style;
    }

    // -- Missing trailing parameter = 00 -------------------------------------

    [Theory]
    [InlineData(0x9D)]  // 02 (wiz) room names
    [InlineData(0x9E)]  // 03 features appearing in a list
    [InlineData(0xA4)]  // 09 speaker of a message
    [InlineData(0xA5)]  // 10 non-priority wiz event message
    [InlineData(0xA6)]  // 11 disabling spell starts
    [InlineData(0xA3)]  // 08 fight starts
    [InlineData(0xFE)]  // 99 black on black
    public void BareCode_StylesAsItsExplicit00Form(byte lead)
        => Assert.Equal(StyleOf(lead, 0x9B, 0xFF, 0xFF), StyleOf(lead, 0xFF, 0xFF));

    [Fact]
    public void Bare02_IsBlackOnGreen()
        => Assert.Equal(new TextStyle(AnsiColor.Black, AnsiColor.Green), StyleOf(0x9D, 0xFF, 0xFF));

    [Fact]
    public void Bare03_IsFeatureGreen()
        => Assert.Equal(new TextStyle(AnsiColor.Green, AnsiColor.Black), StyleOf(0x9E, 0xFF, 0xFF));

    [Fact]
    public void Bare09_IsSpeakerYellow_AndChat()
    {
        var h = new ParserHarness();
        h.Feed(0xA4, 0xFF, 0xFF);
        h.Feed("Fred");
        h.Feed(0xFF, 0xFF);
        h.Feed(" says hello.\n");
        var line = Assert.Single(h.Lines);
        Assert.Equal(new TextStyle(AnsiColor.Yellow, AnsiColor.Black), line.Spans[0].Style);
        Assert.Equal(LineKind.Chat, line.Kind);
    }

    [Fact]
    public void Bare10_IsBlackOnYellow()
        => Assert.Equal(new TextStyle(AnsiColor.Black, AnsiColor.Yellow), StyleOf(0xA5, 0xFF, 0xFF));

    [Fact]
    public void Bare99_IsBlackOnBlack()
        => Assert.Equal(new TextStyle(AnsiColor.Black, AnsiColor.Black), StyleOf(0xFE, 0xFF, 0xFF));

    [Fact]
    public void C99C99_IsStillTheDefaultColourReset()
        => Assert.Equal(new TextStyle(AnsiColor.White, AnsiColor.Black), StyleOf(0xFE, 0xFE, 0xFF, 0xFF));

    [Fact]
    public void Bare08_TagsFightStart_LikeC08C00()
    {
        var h = InGameMode();
        h.Feed(0xA3, 0xFF, 0xFF);
        h.Feed("The wolf is about to attack you!");
        h.Feed(0xFF, 0xFF);
        h.Feed("\n");
        Assert.Equal(LineKind.FightStart, Assert.Single(h.Lines).Kind);
    }

    [Fact]
    public void Bare11_CapturesTheDisablingStartPhrase_LikeC11C00()
    {
        var h = InGameMode();
        h.Feed(0xA6, 0xFF, 0xFF);
        h.Feed("You are glowing!");
        h.Feed(0xFF, 0xFF);
        h.Feed("\n");
        var change = Assert.Single(h.StatusEffects);
        Assert.Equal(StatusEffectKind.Glow, change.Kind);
        Assert.Equal(EffectTransition.Started, change.Transition);
        Assert.Equal("You are glowing!", Assert.Single(h.Lines).PlainText);
    }

    [Fact]
    public void C01C00_OpensThePromptContainer_LikeBareC01()
    {
        // Invisible prompt, with the outer container sent as 01 00 instead of bare 01.
        byte[] Prompt(params byte[] outer) =>
            [.. outer, (byte)'(', 0x9C, 0x9D, 0xFF, 0xFF, (byte)'*', 0xFF, 0xFF, (byte)')', 0xFF, 0xFF];

        List<string> Run(byte[] outer)
        {
            var h = InGameMode();
            h.Feed([.. "You look around.\n"u8.ToArray(), .. Prompt(outer)]);
            h.Feed("look\n");
            return h.Lines.Select(l => (l.IsPartial ? "partial:" : "line:") + l.PlainText).ToList();
        }

        var bare = Run([0x9C, 0xFF, 0xFF]);
        Assert.Equal(["line:You look around.", "partial:(*)", "line:look"], bare);
        Assert.Equal(bare, Run([0x9C, 0x9B, 0xFF, 0xFF]));
    }

    [Fact]
    public void C95C00_CollectsTheClientModeBlock_LikeBareC95()
    {
        var h = new ParserHarness();
        h.Feed(0xFA, 0x9B, 0xFF, 0xFF);
        h.Feed("Licence 1\r\n1\r\n1\r\nACCT01\r\n1\r\n");
        Assert.Single(h.ClientModeData);
    }

    // -- C90 catch / throw ------------------------------------------------------

    [Fact]
    public void C90C00_IsACatch()
    {
        var h = InGameMode();
        h.Feed(0xA0, 0x9B, 0xFF, 0xFF);        // C05+C00 -> RED
        h.Feed(0xF5, 0x9B, 0xFF, 0xFF);        // 90 00: catch
        h.Feed(0xFE, 0xA7, 0xFF, 0xFF);        // C99 -> BrightBlue
        h.Feed("blue");
        h.Feed(0xF5, 0x9C, 0xFF, 0xFF);        // 90 01: throw
        h.Feed("red\n");
        var line = Assert.Single(h.Lines);
        Assert.Equal(AnsiColor.BrightBlue, line.Spans[0].Style.Foreground);
        Assert.Equal(AnsiColor.Red, line.Spans[1].Style.Foreground);
    }

    [Fact]
    public void C90_AnyNonZeroParameter_IsAThrow()
    {
        var h = InGameMode();
        h.Feed(0xA0, 0x9B, 0xFF, 0xFF);        // RED
        h.Feed(0xF5, 0xFF, 0xFF);              // catch
        h.Feed(0xFE, 0xA7, 0xFF, 0xFF);        // BrightBlue
        h.Feed(0xFE, 0xA0, 0xFF, 0xFF);        // Magenta
        h.Feed("x");
        h.Feed(0xF5, 0x9D, 0xFF, 0xFF);        // 90 02: a throw, not a WHITE push
        h.Feed("red\n");
        var line = Assert.Single(h.Lines);
        Assert.Equal(new TextStyle(AnsiColor.Red, AnsiColor.Black), line.Spans[1].Style);
    }

    [Fact]
    public void C90_ThrowWithoutCatch_ChangesNothing()
    {
        var h = InGameMode();
        h.Feed(0xA0, 0x9B, 0xFF, 0xFF);        // RED
        h.Feed(0xF5, 0x9C, 0xFF, 0xFF);        // throw, no catch
        h.Feed("red");
        h.Feed(0xFF, 0xFF);                    // pops RED - the throw pushed nothing
        h.Feed("white\n");
        var line = Assert.Single(h.Lines);
        Assert.Equal(AnsiColor.Red, line.Spans[0].Style.Foreground);
        Assert.Equal(InitStackStyle, line.Spans[1].Style);
    }

    [Fact]
    public void C90_SurplusPopsInsideACatch_CannotUnwindTheSavedStack()
    {
        var h = InGameMode();
        h.Feed(0xA0, 0x9B, 0xFF, 0xFF);        // RED (outer, pushed before the catch)
        h.Feed(0xF5, 0xFF, 0xFF);              // catch
        h.Feed(0xFE, 0xA7, 0xFF, 0xFF);        // BrightBlue
        h.Feed(0xFF, 0xFF);                    // pop BrightBlue
        h.Feed(0xFF, 0xFF, 0xFF, 0xFF);        // two surplus pops: nothing above the catch point
        h.Feed("inside");
        h.Feed(0xF5, 0x9C, 0xFF, 0xFF);        // throw: the old stack is reinstated
        h.Feed("outer");
        h.Feed(0xFF, 0xFF);                    // the outer pop now pops RED
        h.Feed("white\n");

        var line = Assert.Single(h.Lines);
        Assert.Equal(AnsiColor.Red, line.Spans[0].Style.Foreground);   // "inside"
        Assert.Equal(AnsiColor.Red, line.Spans[1].Style.Foreground);   // "outer"
        Assert.Equal(InitStackStyle, line.Spans[2].Style);             // "white"
    }

    [Fact]
    public void C90_SurplusPopsInsideACatch_CannotCloseAScopeOpenedBeforeIt()
    {
        var h = InGameMode();
        h.Feed(0xA7, 0xA3, 0xA0, 0xFF, 0xFF);  // FEW response context (scope opened before the catch)
        h.Feed(0xF5, 0xFF, 0xFF);              // catch
        h.Feed(0xFF, 0xFF, 0xFF, 0xFF);        // surplus pops
        Assert.Equal(0, h.FewListCompleteCount);
        h.Feed("hidden\n");                    // still inside the FEW response: not displayed
        Assert.Empty(h.Lines);
        h.Feed(0xF5, 0x9C, 0xFF, 0xFF);        // throw
        h.Feed(0xFF, 0xFF);                    // the FEW context's own pop
        Assert.Equal(1, h.FewListCompleteCount);
        h.Feed("shown\n");
        Assert.Equal("shown", Assert.Single(h.Lines).PlainText);
    }

    [Fact]
    public void C90_NestedCatches_EachThrowRestoresItsOwnCatchPoint()
    {
        var h = InGameMode();
        h.Feed(0xA0, 0x9B, 0xFF, 0xFF);        // RED
        h.Feed(0xF5, 0xFF, 0xFF);              // catch 1
        h.Feed(0xFE, 0xA7, 0xFF, 0xFF);        // BrightBlue
        h.Feed(0xF5, 0xFF, 0xFF);              // catch 2
        h.Feed(0xFE, 0xA0, 0xFF, 0xFF);        // Magenta
        h.Feed("a");
        h.Feed(0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF);  // one real pop, two surplus
        h.Feed("b");                           // catch 2's floor holds: BrightBlue
        h.Feed(0xF5, 0x9C, 0xFF, 0xFF);        // throw 2 -> BrightBlue
        h.Feed("c");
        h.Feed(0xF5, 0x9C, 0xFF, 0xFF);        // throw 1 -> RED
        h.Feed("d\n");

        var line = Assert.Single(h.Lines);
        Assert.Equal(["a", "b", "c", "d"], line.Spans.Select(s => s.Text));
        Assert.Equal(AnsiColor.Magenta,    line.Spans[0].Style.Foreground);
        Assert.Equal(AnsiColor.BrightBlue, line.Spans[1].Style.Foreground);
        Assert.Equal(AnsiColor.BrightBlue, line.Spans[2].Style.Foreground);
        Assert.Equal(AnsiColor.Red,        line.Spans[3].Style.Foreground);
    }

    [Fact]
    public void C00_ClearsAnOpenCatch()
    {
        var h = InGameMode();
        h.Feed(0xF5, 0xFF, 0xFF);              // catch, never thrown
        h.Feed(0x9B, 0xFF, 0xFF);              // C00: init_stack
        h.Feed(0xA0, 0x9B, 0xFF, 0xFF);        // RED
        h.Feed("red");
        h.Feed(0xFF, 0xFF);                    // pops RED -> WHITE (C00's frame)
        h.Feed(0xFF, 0xFF);                    // pops WHITE -> default: no stale catch floor
        h.Feed("plain\n");
        var line = Assert.Single(h.Lines);
        Assert.Equal(AnsiColor.Red, line.Spans[0].Style.Foreground);
        Assert.Equal(TextStyle.Default, line.Spans[1].Style);
    }

    // -- Snoop codes 94 / 96 / 97 / 98 -------------------------------------------

    [Theory]
    [InlineData(new byte[] { 0xF9, 0x9B, 0x9C, 0x9D })]  // 94 oo nn mm: snoops commenced (brackets the name)
    [InlineData(new byte[] { 0xFC, 0x9B, 0x9C })]        // 97 oo nn: snooped material
    public void SnoopCode_PushesWhite_AndItsPopRestores(byte[] code)
    {
        var h = InGameMode();
        h.Feed(0xA0, 0x9B, 0xFF, 0xFF);        // RED
        h.Feed("a");
        h.Feed([.. code, 0xFF, 0xFF]);
        h.Feed("snooped");
        h.Feed(0xFF, 0xFF);
        h.Feed("b\n");
        var line = Assert.Single(h.Lines);
        Assert.Equal(AnsiColor.Red, line.Spans[0].Style.Foreground);
        Assert.Equal(new TextStyle(AnsiColor.White, AnsiColor.Black), line.Spans[1].Style);
        Assert.Equal(AnsiColor.Red, line.Spans[2].Style.Foreground);
    }

    [Fact]
    public void C96_ImmediatelyTerminated_LeavesTheStyleAsItWas()
    {
        // "Note that this code is immediately followed by its termination".
        var h = InGameMode();
        h.Feed(0xA0, 0x9B, 0xFF, 0xFF);        // RED
        h.Feed(0xFB, 0x9B, 0x9C, 0x9D, 0xFF, 0xFF, 0xFF, 0xFF);
        h.Feed("red\n");
        Assert.Equal(AnsiColor.Red, Assert.Single(h.Lines).Spans[0].Style.Foreground);
    }

    [Fact]
    public void C98_SnoopPrefix_FlushesTheLineSoFarAsAPartial_AndPopsBalanced()
    {
        var h = InGameMode();
        h.Feed("before");
        h.Feed(0xFD, 0x9C, 0xFF, 0xFF);        // 98 01: snoop 1's line prefix
        h.Feed("|1|");
        h.Feed(0xFF, 0xFF);
        h.Feed(" after\n");

        Assert.Equal(2, h.Lines.Count);
        Assert.True(h.Lines[0].IsPartial);
        Assert.Equal("before", h.Lines[0].PlainText);
        var line = h.Lines[1];
        Assert.Equal(new TextStyle(AnsiColor.Black, AnsiColor.Blue), line.Spans[0].Style);  // "|1|"
        Assert.Equal(InitStackStyle, line.Spans[1].Style);                               // " after"
    }

    // -- FES with the documented 14 fields ------------------------------------

    private static readonly byte[] FesOpen = [0xA7, 0xA3, 0x9C, 0xFF, 0xFF];
    private static readonly byte[] CrNulCrLf = [(byte)'\r', 0x00, (byte)'\r', (byte)'\n'];
    private static readonly byte[] WirePromptPreamble =
        [0x9C, 0xFF, 0xFF, 0x9C, 0x9D, 0xFF, 0xFF, (byte)'*', 0xFF, 0xFF, 0xFF, 0xFF];

    [Fact]
    public void Fes14Fields_ParsesWithoutWeather_AndSwallowsNothing()
    {
        // mud2_FE4.txt's own example line, then the pop and prompt container the wire sends.
        var h = InGameMode();
        h.Feed("You look around.\n");
        h.ClearCounters();
        h.Feed(FesOpen);
        h.Feed("99 99 100 100 100 100 99 99 187325 N N N N 98");
        h.Feed(CrNulCrLf);
        h.Feed(0xFF, 0xFF);
        h.Feed(WirePromptPreamble);
        h.Feed("Normal text\n");

        var s = Assert.Single(h.Stats);
        Assert.Equal(99, s.Stamina);
        Assert.Equal(187325, s.Score);
        Assert.Equal(98, s.TimeToReset);
        Assert.Equal(' ', s.Weather);
        Assert.Equal("Normal text", h.Lines[^1].PlainText);
        Assert.DoesNotContain(h.Lines, l => l.PlainText.Contains("187325"));
    }

    [Fact]
    public void Fes14Fields_EndingAtABareCr_BeforeTheNextCode_Parses()
    {
        var h = InGameMode();
        h.Feed(FesOpen);
        h.Feed("99 99 100 100 100 100 99 99 187325 N N N N 98\r\0");
        h.Feed(0xFF, 0xFF);
        h.Feed("Normal text\n");
        Assert.Single(h.Stats);
        Assert.Equal("Normal text", Assert.Single(h.Lines).PlainText);
    }

    [Fact]
    public void Fes15Fields_WrappedJustBeforeTheWeather_StillParsesTheWeather()
    {
        var h = InGameMode();
        h.Feed(FesOpen);
        h.Feed("99 99 100 100 100 100 99 99 187325 N N N N 98");
        h.Feed(CrNulCrLf);                     // the server's wrap, after field 14
        h.Feed("S");
        h.Feed(CrNulCrLf);
        h.Feed(0xFF, 0xFF);
        h.Feed("Normal text\n");

        var s = Assert.Single(h.Stats);
        Assert.Equal('S', s.Weather);
        Assert.Equal(98, s.TimeToReset);
        Assert.Equal("Normal text", Assert.Single(h.Lines).PlainText);
    }

    // -- Server escapes $-C $-K $-R $-r $-Q --------------------------------------

    [Fact]
    public void EscDashR_TurnsReverseOn_AndEscDashLowerR_TurnsItOff()
    {
        var h = new ParserHarness();
        h.Feed("a\x1B-Rb\x1B-rc\n");
        var spans = Assert.Single(h.Lines).Spans;
        Assert.Equal(["a", "b", "c"], spans.Select(s => s.Text));
        Assert.False(spans[0].Style.Reverse);
        Assert.True(spans[1].Style.Reverse);
        Assert.False(spans[2].Style.Reverse);
    }

    [Fact]
    public void EscDashC_EmitsAFormFeedLine_InOrderWithTheText()
    {
        var h = new ParserHarness();
        h.Feed("old\nhalf\x1B-Cnew\n");
        Assert.Equal(3, h.Lines.Count);
        Assert.Equal("old", h.Lines[0].PlainText);
        Assert.Contains('\f', h.Lines[1].PlainText);
        Assert.False(h.Lines[1].IsPartial);
        Assert.Equal("new", h.Lines[2].PlainText);
    }

    [Fact]
    public void EscDashK_RaisesTheEvent_AndLeavesTheTextAlone()
    {
        var h = new ParserHarness();
        int erased = 0;
        h.Parser.EraseToEndOfLineReceived += () => erased++;
        h.Feed("abc\x1B-Kdef\n");
        Assert.Equal(1, erased);
        Assert.Equal("abcdef", Assert.Single(h.Lines).PlainText);
    }

    [Fact]
    public void EscDashQ_RaisesEndOfSession()
    {
        var h = new ParserHarness();
        int ended = 0;
        h.Parser.EndOfSessionReceived += () => ended++;
        h.Feed("bye\x1B-Q");
        Assert.Equal(1, ended);
    }

    [Fact]
    public void EscDashWidth_StillConfirmsTheWidth()
    {
        var h = new ParserHarness();
        h.Feed("\x1B-70W");
        Assert.Equal([70], h.ConfirmedWidths);
    }
}
