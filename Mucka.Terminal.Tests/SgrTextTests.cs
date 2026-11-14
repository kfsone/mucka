using System.Globalization;
using System.Text.RegularExpressions;
using MudSharp.Models;

namespace Mucka.Terminal.Tests;

/// <summary><see cref="SgrText"/>: a line of screen as ANSI SGR - the terminal's own default colours,
/// palette slots as standard codes (Simple) or the pane's own 24-bit colours (Fancy), an escape only
/// where the look changes.</summary>
public sealed class SgrTextTests
{
    private const char Esc = (char)27;

    private static StyledLine Line(params StyledSpan[] spans) => new(spans);

    private static StyledSpan Span(string text, TextStyle? style = null) => new(text, style ?? TextStyle.Default);

    private static string Format(SgrColours depth, params StyledSpan[] spans)
        => SgrText.Format(Line(spans), ChatColorizer.DefaultPalette, depth);

    private static string Format(SgrColours depth, ChatColorizer.Palette palette, params StyledSpan[] spans)
        => SgrText.Format(Line(spans), palette, depth);

    /// <summary>A palette slot as a foreground: its standard code in Simple, the pane's own colour in
    /// 24-bit in Fancy.</summary>
    private static string Fg(SgrColours depth, int slot) => depth == SgrColours.Simple
        ? (slot < 8 ? 30 + slot : 90 + slot - 8).ToString(CultureInfo.InvariantCulture)
        : TrueColour("38", CampbellPalette.Rgb[slot]);

    /// <summary>A palette slot as a background, as <see cref="Fg"/>.</summary>
    private static string Bg(SgrColours depth, int slot) => depth == SgrColours.Simple
        ? (slot < 8 ? 40 + slot : 100 + slot - 8).ToString(CultureInfo.InvariantCulture)
        : TrueColour("48", CampbellPalette.Rgb[slot]);

    private static string TrueColour(string plane, int rgb)
        => string.Create(CultureInfo.InvariantCulture, $"{plane};2;{(rgb >> 16) & 0xFF};{(rgb >> 8) & 0xFF};{rgb & 0xFF}");

    // ------------------------------------------------------------- the terminal's own look ----

    [Theory]
    [InlineData(SgrColours.Simple)]
    [InlineData(SgrColours.Fancy)]
    public void ADefaultOnlyLine_HasNoEscapes(SgrColours depth)
        => Assert.Equal("hello", Format(depth, Span("hello")));

    [Theory]
    [InlineData(SgrColours.Simple)]
    [InlineData(SgrColours.Fancy)]
    public void ALineWithNoText_IsEmpty(SgrColours depth)
    {
        Assert.Equal(string.Empty, Format(depth));
        Assert.Equal(string.Empty, Format(depth, Span(""), Span("", new TextStyle(Foreground: AnsiColor.Red))));
    }

    [Theory]
    [InlineData(SgrColours.Simple)]
    [InlineData(SgrColours.Fancy)]
    public void ABlinkingReversedSpan_IsPlainText_BecauseThePaneDrawsNeither(SgrColours depth)
        => Assert.Equal("x", Format(depth, Span("x", new TextStyle(Blink: true, Reverse: true))));

    [Theory]
    [InlineData(SgrColours.Simple)]
    [InlineData(SgrColours.Fancy)]
    public void ABoldSpanWithTheDefaultForeground_IsBoldAndHasNoColourCode(SgrColours depth)
        => Assert.Equal($"{Esc}[1mx{Esc}[0m", Format(depth, Span("x", new TextStyle(Bold: true))));

    [Theory]
    [InlineData(SgrColours.Simple)]
    [InlineData(SgrColours.Fancy)]
    public void GameTextsWhiteOnBlack_IsTheTerminalsOwnLook(SgrColours depth)
    {
        // MUD2 sends ordinary text as an explicit white on black (37;40): the pane's own default
        // text and page colours, so the terminal's own.
        var white = new TextStyle(Foreground: AnsiColor.White, Background: AnsiColor.Black);
        Assert.Equal("hello", Format(depth, Span("hello", white)));
        Assert.Equal($"{Esc}[1mhi{Esc}[0m", Format(depth, Span("hi", white with { Bold = true })));
        Assert.Equal($"{Esc}[{Fg(depth, 10)}mroom{Esc}[0m.",
            Format(depth, Span("room", white with { Foreground = AnsiColor.BrightGreen }), Span(".", white)));
    }

    // ----------------------------------------------------------------------- palette slots ----

    [Theory]
    [InlineData(SgrColours.Simple)]
    [InlineData(SgrColours.Fancy)]
    public void ABoldRedSpan_IsBrightRed_AndBold(SgrColours depth)
        => Assert.Equal($"{Esc}[{Fg(depth, 9)};1mOuch{Esc}[0m",
            Format(depth, Span("Ouch", new TextStyle(Foreground: AnsiColor.Red, Bold: true))));

    [Theory]
    [InlineData(SgrColours.Simple)]
    [InlineData(SgrColours.Fancy)]
    public void ASlot_IsItsStandardCodeInSimple_AndThePanesColourInFancy(SgrColours depth)
    {
        Assert.Equal($"{Esc}[{Fg(depth, 1)}mx{Esc}[0m", Format(depth, Span("x", new TextStyle(Foreground: AnsiColor.Red))));
        Assert.Equal($"{Esc}[{Fg(depth, 9)}mx{Esc}[0m", Format(depth, Span("x", new TextStyle(Foreground: AnsiColor.BrightRed))));
        // A bright slot is not promoted again.
        Assert.Equal($"{Esc}[{Fg(depth, 9)};1mx{Esc}[0m",
            Format(depth, Span("x", new TextStyle(Foreground: AnsiColor.BrightRed, Bold: true))));
    }

    [Theory]
    [InlineData(SgrColours.Simple)]
    [InlineData(SgrColours.Fancy)]
    public void AnExplicitBackground_IsWritten_AndTheDefaultOneNever(SgrColours depth)
    {
        Assert.Equal($"{Esc}[{Bg(depth, 4)};4mx{Esc}[0m",
            Format(depth, Span("x", new TextStyle(Background: AnsiColor.Blue, Underline: true))));
        Assert.Equal($"{Esc}[{Fg(depth, 1)};{Bg(depth, 12)}mx{Esc}[0m",
            Format(depth, Span("x", new TextStyle(Foreground: AnsiColor.Red, Background: AnsiColor.BrightBlue))));
    }

    [Theory]
    [InlineData(SgrColours.Simple)]
    [InlineData(SgrColours.Fancy)]
    public void TheFacesTheSpanCarries_AreBoldItalicAndUnderline(SgrColours depth)
        => Assert.Equal($"{Esc}[1;3;4mx{Esc}[0m",
            Format(depth, Span("x", new TextStyle(Bold: true, Italic: true, Underline: true))));

    [Fact]
    public void ABoldSpansColour_IsTheBrightSlotThePaneDraws()
    {
        foreach (var bold in new[] { false, true })
            for (var slot = 0; slot < 16; slot++)
            {
                if (slot == CampbellPalette.DefaultForegroundSlot)
                    continue;   // the terminal's own colour: GameTextsWhiteOnBlack_IsTheTerminalsOwnLook
                var style = new TextStyle(Foreground: (AnsiColor)slot, Bold: bold);
                var rgb = CampbellPalette.ForegroundRgb(style, ChatColorizer.DefaultPalette);
                var simple = SgrText.Format(Line(Span("x", style)), ChatColorizer.DefaultPalette, SgrColours.Simple);
                var code = int.Parse(Regex.Match(simple, @"\[(\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
                var drawn = code is >= 30 and <= 37 ? code - 30 : code - 90 + 8;
                Assert.Equal(rgb, CampbellPalette.Rgb[drawn]);
                var fancy = SgrText.Format(Line(Span("x", style)), ChatColorizer.DefaultPalette, SgrColours.Fancy);
                Assert.StartsWith($"{Esc}[{TrueColour("38", rgb)}", fancy);
            }
    }

    // ----------------------------------------------------------------------- chat colours ----

    [Fact]
    public void AChatSpan_IsThePlayersColourIn24Bit_InFancy()
    {
        var palette = ChatColorizer.DefaultPalette with { Say = 0x123456 };
        Assert.Equal($"{Esc}[38;2;18;52;86mhi{Esc}[0m",
            Format(SgrColours.Fancy, palette, Span("hi", new TextStyle(Speech: SpeechPart.Say))));
    }

    [Fact]
    public void AChatSpan_IsTheNearestOfThe16Slots_InSimple()
    {
        // Within 7, 8 and 6 of slot 9 (0xE74856), and nearer to it than to any other slot.
        var palette = ChatColorizer.DefaultPalette with { Say = 0xE04050 };
        var said = Span("hi", new TextStyle(Speech: SpeechPart.Say));
        Assert.Equal($"{Esc}[91mhi{Esc}[0m", Format(SgrColours.Simple, palette, said));
        Assert.Equal($"{Esc}[38;2;224;64;80mhi{Esc}[0m", Format(SgrColours.Fancy, palette, said));
    }

    [Fact]
    public void TheDefaultChatColours_AreApproximatedToTheirNearestSlots_InSimple()
    {
        // Lamplight: emote 0xBFA36A is nearest slot 8, say 0xEDE6D0 slot 15, shout 0xFFA033 slot 3,
        // tell 0x7FB8FF slot 14.
        Assert.Equal($"{Esc}[90mx{Esc}[0m", Format(SgrColours.Simple, Span("x", new TextStyle(Speech: SpeechPart.Speaker))));
        Assert.Equal($"{Esc}[97mx{Esc}[0m", Format(SgrColours.Simple, Span("x", new TextStyle(Speech: SpeechPart.Say))));
        Assert.Equal($"{Esc}[33mx{Esc}[0m", Format(SgrColours.Simple, Span("x", new TextStyle(Speech: SpeechPart.Shout))));
        Assert.Equal($"{Esc}[96mx{Esc}[0m", Format(SgrColours.Simple, Span("x", new TextStyle(Speech: SpeechPart.Tell))));
    }

    [Fact]
    public void EachPaletteColour_AsAChatColour_IsItsOwnSlot_InSimple()
    {
        for (var slot = 0; slot < 16; slot++)
        {
            var palette = ChatColorizer.DefaultPalette with { Say = CampbellPalette.Rgb[slot] };
            var text = Format(SgrColours.Simple, palette, Span("x", new TextStyle(Speech: SpeechPart.Say)));
            var expected = slot < 8 ? 30 + slot : 90 + slot - 8;
            Assert.Equal($"{Esc}[{expected}mx{Esc}[0m", text);
        }
    }

    [Fact]
    public void ADimChatSpan_IsApproximatedFromItsDimmedColour()
    {
        // 0xE74856 is slot 9 exactly; its Dim (0x8F3038) is nearest slot 1.
        var palette = ChatColorizer.DefaultPalette with
        {
            Say = 0xE74856,
            Faces = ChatFaces.Default with { SayOther = ChatFace.Dim },
        };
        var said = Span("hi", new TextStyle(Speech: SpeechPart.Say));
        Assert.Equal(0x8F3038, ChatColorizer.Dim(0xE74856));
        Assert.Equal($"{Esc}[31mhi{Esc}[0m", Format(SgrColours.Simple, palette, said));
        Assert.Equal($"{Esc}[38;2;143;48;56mhi{Esc}[0m", Format(SgrColours.Fancy, palette, said));
    }

    [Fact]
    public void ABoldChatSpan_IsNotPromotedToABrightSlot()
    {
        // The pane draws a chat colour exactly; only a span with no chat colour is promoted.
        var palette = ChatColorizer.DefaultPalette with
        {
            Say = CampbellPalette.Rgb[1],
            Faces = ChatFaces.Default with { SayOther = ChatFace.Bold },
        };
        Assert.Equal($"{Esc}[31;1mhi{Esc}[0m",
            Format(SgrColours.Simple, palette, Span("hi", new TextStyle(Speech: SpeechPart.Say))));
    }

    [Fact]
    public void ThePersonasOwnSay_IsInItsOwnFace()
    {
        var palette = ChatColorizer.DefaultPalette with { Faces = ChatFaces.Default with { SayOwn = ChatFace.Italic } };
        var said = Span("hi", new TextStyle(Speech: SpeechPart.Say, Own: true));
        Assert.Equal($"{Esc}[38;2;237;230;208;3mhi{Esc}[0m", Format(SgrColours.Fancy, palette, said));
        Assert.Equal($"{Esc}[97;3mhi{Esc}[0m", Format(SgrColours.Simple, palette, said));
    }

    [Theory]
    [InlineData(SgrColours.Simple)]
    [InlineData(SgrColours.Fancy)]
    public void ARoomNameThenAFullStop_IsOneEscapeAndOneReset(SgrColours depth)
        => Assert.Equal($"{Esc}[{Fg(depth, 10)}mOvergrown grounds of mausoleum{Esc}[0m.",
            Format(depth, Span("Overgrown grounds of mausoleum", new TextStyle(Foreground: AnsiColor.BrightGreen)),
                Span(".")));

    [Fact]
    public void ASayLine_ChangesLookAtItsWords_AndBackAfterThem()
    {
        var own = new TextStyle(Speech: SpeechPart.Speaker, Own: true);
        var spans = new[]
        {
            Span("Ollie says \"", own),
            Span("hello", new TextStyle(Speech: SpeechPart.Say, Own: true)),
            Span("\".", own),
        };
        Assert.Equal(
            $"{Esc}[38;2;191;163;106mOllie says \"{Esc}[38;2;237;230;208;3mhello{Esc}[0;38;2;191;163;106m\".{Esc}[0m",
            Format(SgrColours.Fancy, spans));
        Assert.Equal($"{Esc}[90mOllie says \"{Esc}[97;3mhello{Esc}[0;90m\".{Esc}[0m", Format(SgrColours.Simple, spans));
    }

    // -------------------------------------------------------------- escapes only on change ----

    [Theory]
    [InlineData(SgrColours.Simple)]
    [InlineData(SgrColours.Fancy)]
    public void TwoAdjacentSpansOfOneLook_ShareOneEscape(SgrColours depth)
    {
        var red = new TextStyle(Foreground: AnsiColor.Red);
        Assert.Equal($"{Esc}[{Fg(depth, 1)}mab{Esc}[0m", Format(depth, Span("a", red), Span("b", red)));
        // A difference the pane does not draw is not a change of look.
        Assert.Equal($"{Esc}[{Fg(depth, 1)}mab{Esc}[0m", Format(depth, Span("a", red), Span("b", red with { Blink = true })));
    }

    [Theory]
    [InlineData(SgrColours.Simple)]
    [InlineData(SgrColours.Fancy)]
    public void AStyledSpanThenDefaultText_IsReset_AndTheLineNeedsNoMore(SgrColours depth)
        => Assert.Equal($"{Esc}[{Fg(depth, 1)}ma{Esc}[0mb",
            Format(depth, Span("a", new TextStyle(Foreground: AnsiColor.Red)), Span("b")));

    [Theory]
    [InlineData(SgrColours.Simple)]
    [InlineData(SgrColours.Fancy)]
    public void DefaultTextThenAStyledSpan_IsEscapedAtTheSpan_AndResetAtTheEnd(SgrColours depth)
        => Assert.Equal($"a{Esc}[{Fg(depth, 1)}mb{Esc}[0m",
            Format(depth, Span("a"), Span("b", new TextStyle(Foreground: AnsiColor.Red))));

    [Theory]
    [InlineData(SgrColours.Simple)]
    [InlineData(SgrColours.Fancy)]
    public void AttributesAddedOrChanged_AreWrittenAlone_AndOnlyAnAttributeSwitchedOffResets(SgrColours depth)
    {
        var red = new TextStyle(Foreground: AnsiColor.Red);
        // Colour to colour, and an attribute added: nothing is switched off, so no reset.
        Assert.Equal($"{Esc}[{Fg(depth, 1)}ma{Esc}[{Fg(depth, 2)}mb{Esc}[0m",
            Format(depth, Span("a", red), Span("b", new TextStyle(Foreground: AnsiColor.Green))));
        Assert.Equal($"{Esc}[{Fg(depth, 1)}ma{Esc}[4mb{Esc}[0m",
            Format(depth, Span("a", red), Span("b", red with { Underline = true })));
        // Bold red to red switches bold off, so everything the new look sets follows a reset.
        Assert.Equal($"{Esc}[{Fg(depth, 9)};1ma{Esc}[0;{Fg(depth, 1)}mb{Esc}[0m",
            Format(depth, Span("a", red with { Bold = true }), Span("b", red)));
        // A background dropped is a reset too, and the foreground is set again.
        Assert.Equal($"{Esc}[{Fg(depth, 1)};{Bg(depth, 4)}ma{Esc}[0;{Fg(depth, 1)}mb{Esc}[0m",
            Format(depth, Span("a", red with { Background = AnsiColor.Blue }), Span("b", red)));
    }
}
