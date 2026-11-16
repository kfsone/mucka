using MudSharp.Models;

namespace Mucka.Terminal.Tests;

/// <summary><see cref="HtmlText"/> and <see cref="HtmlPageWriter"/>: a line of screen as HTML in the
/// pane's own colours, an element only where the look changes, and a page that is ASCII whatever the
/// server sent.</summary>
public sealed class HtmlTextTests
{
    private static StyledSpan Span(string text, TextStyle? style = null) => new(text, style ?? TextStyle.Default);

    private static string Format(params StyledSpan[] spans)
        => HtmlText.Format(new StyledLine(spans), ChatColorizer.DefaultPalette);

    private static string Hex(int rgb) => "#" + (rgb & 0xFFFFFF).ToString("X6", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void DefaultText_OnThePage_HasNoElement_AndALineWithNoTextIsEmpty()
    {
        Assert.Equal("hello", Format(Span("hello")));
        // MUD2's explicit white on black is the pane's default text on its page colour.
        Assert.Equal("hello", Format(Span("hello", new TextStyle(Foreground: AnsiColor.White, Background: AnsiColor.Black))));
        Assert.Equal(string.Empty, Format());
        Assert.Equal(string.Empty, Format(Span(""), Span("", new TextStyle(Foreground: AnsiColor.Red))));
    }

    [Fact]
    public void ASpan_IsDrawnInThePanesColour_BoldBeingBright_AndNeighboursOfOneLookShareAnElement()
    {
        var red = new TextStyle(Foreground: AnsiColor.Red, Bold: true);
        Assert.Equal($"a <span style=\"color:{Hex(CampbellPalette.Rgb[9])};font-weight:bold\">bc</span> d",
            Format(Span("a "), Span("b", red), Span("c", red), Span(" d")));
    }

    [Fact]
    public void ABackground_ItalicAndUnderline_AreWritten_ReverseAndBlinkAreNot()
    {
        Assert.Equal($"<span style=\"background:{Hex(CampbellPalette.Rgb[4])};font-style:italic;text-decoration:underline\">x</span>",
            Format(Span("x", new TextStyle(Background: AnsiColor.Blue, Italic: true, Underline: true))));
        Assert.Equal("x", Format(Span("x", new TextStyle(Blink: true, Reverse: true))));
    }

    [Fact]
    public void AChatSpan_IsInThePlayersChatColour()
    {
        var say = new TextStyle(Speech: SpeechPart.Say);
        Assert.Equal(Hex(CampbellPalette.ForegroundRgb(say, ChatColorizer.DefaultPalette)),
            Hex(ChatColorizer.DefaultPalette.Say));
        Assert.StartsWith($"<span style=\"color:{Hex(ChatColorizer.DefaultPalette.Say)}", Format(Span("hi", say)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Text_IsEscaped_ToAscii_WithControlCharactersDropped()
    {
        var accented = (char)0xE9;              // Latin-1, which HtmlEncode references itself
        var dash = (char)0x2014;                // outside Latin-1, which it leaves alone
        var face = char.ConvertFromUtf32(0x1F600);
        var half = (char)0xD800;
        var text = "<a & \"b\">" + accented + dash + face + half + (char)1 + (char)0x7F + (char)0x85 + (char)0x9F + "\tz";

        Assert.Equal("&lt;a &amp; &quot;b&quot;&gt;&#233;&#8212;&#128512;&#65533;\tz", HtmlText.Encode(text));
    }

    [Fact]
    public async Task ThePageWriter_WrapsWhatIsWritten_InAWholePage_AndClosesItsWriterOnce()
    {
        var inner = new CountingWriter();
        var page = new HtmlPageWriter(inner, "Ollie & co");
        await page.WriteLineAsync("one");
        page.WriteLine("two");
        await page.DisposeAsync();
        page.Dispose();

        Assert.Equal(1, inner.Disposed);
        var html = inner.ToString();
        Assert.StartsWith("<!DOCTYPE html>", html, StringComparison.Ordinal);
        Assert.Contains("<title>Ollie &amp; co</title>", html, StringComparison.Ordinal);
        Assert.EndsWith("<body><pre>" + Environment.NewLine + "one" + Environment.NewLine + "two" + Environment.NewLine
            + HtmlText.Foot + Environment.NewLine, html, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "</html>"));
        Assert.All(html, c => Assert.True(char.IsAscii(c)));
    }

    private sealed class CountingWriter : StringWriter
    {
        public int Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                Disposed++;
            base.Dispose(disposing);
        }
    }
}
