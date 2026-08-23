using MudSharp.Models;
using MudSharp.Protocol;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// Golden tests for ANSI SGR (Select Graphic Rendition) parsing.
/// </summary>
public class AnsiSgrTests
{
    [Fact]
    public void Reset_ClearsStyle()
    {
        // ESC[1;31m = Bold + Red; ESC[m = reset to Default
        var h = new ParserHarness();
        h.Feed("\x1B[1;31m");
        h.Feed("\x1B[m");
        h.Feed("text\n");
        Assert.Single(h.Lines);
        Assert.Equal(TextStyle.Default, h.Lines[0].Spans[0].Style);
    }

    [Fact]
    public void Bold_SetsBold()
    {
        // SGR 1 — ansi_bold = 1
        var h = new ParserHarness();
        h.Feed("\x1B[1m");
        h.Feed("text\n");
        Assert.Single(h.Lines);
        Assert.True(h.Lines[0].Spans[0].Style.Bold);
    }

    [Fact]
    public void ForegroundRed_SetsFgRed()
    {
        // SGR 31 -> red foreground
        var h = new ParserHarness();
        h.Feed("\x1B[31m");
        h.Feed("text\n");
        Assert.Single(h.Lines);
        Assert.Equal(AnsiColor.Red, h.Lines[0].Spans[0].Style.Foreground);
    }

    [Fact]
    public void BrightCyan_SetsBrightCyan()
    {
        // SGR 96 -> bright cyan foreground
        var h = new ParserHarness();
        h.Feed("\x1B[96m");
        h.Feed("text\n");
        Assert.Single(h.Lines);
        Assert.Equal(AnsiColor.BrightCyan, h.Lines[0].Spans[0].Style.Foreground);
    }

    [Fact]
    public void Background_Yellow()
    {
        // SGR 43 -> yellow background
        var h = new ParserHarness();
        h.Feed("\x1B[43m");
        h.Feed("text\n");
        Assert.Single(h.Lines);
        Assert.Equal(AnsiColor.Yellow, h.Lines[0].Spans[0].Style.Background);
    }

    [Fact]
    public void MultiParam_BoldRed()
    {
        // ESC[1;31m — Bold + Red; MudSharp applies bold flag + Red fg independently (Bold=true, Foreground=Red)
        var h = new ParserHarness();
        h.Feed("\x1B[1;31m");
        h.Feed("text\n");
        Assert.Single(h.Lines);
        var style = h.Lines[0].Spans[0].Style;
        Assert.True(style.Bold);
        Assert.Equal(AnsiColor.Red, style.Foreground);
    }

    [Fact]
    public void TextAfterColor_UsesCorrectStyle()
    {
        // SGR 32 = Green foreground; verify text is emitted with that style
        var h = new ParserHarness();
        h.Feed("\x1B[32m");
        h.Feed("hello\n");
        Assert.Single(h.Lines);
        var span = h.Lines[0].Spans[0];
        Assert.Equal("hello", span.Text);
        Assert.Equal(AnsiColor.Green, span.Style.Foreground);
    }

    [Fact]
    public void TerminalWidth_EscDashNW_FiresConfirmedEvent()
    {
        // ESC-80W = server confirming terminal width 80.
        // Should fire TerminalWidthConfirmed(80) and suppress all display output.
        var h = new ParserHarness();
        h.Feed("\x1B-80W[New terminal width is 80]\r\n");
        Assert.Single(h.ConfirmedWidths);
        Assert.Equal(80, h.ConfirmedWidths[0]);
        Assert.Empty(h.Lines);
    }

    [Fact]
    public void TerminalWidth_EscDashNW_SwallowsAnnotationText()
    {
        // Text after ESC-<n>W (the "[New terminal width is N]" annotation) must not
        // appear as a display line. Anything after the annotation \n resumes normally.
        var h = new ParserHarness();
        h.Feed("\x1B-80W[New terminal width is 80]\r\nsome text\n");
        Assert.Single(h.ConfirmedWidths);
        Assert.Single(h.Lines);
        Assert.Equal("some text", h.Lines[0].PlainText);
    }

    [Fact]
    public void TerminalWidth_EscDashNW_StopsSwallowingWhenBinaryTrafficStarts()
    {
        var h = new ParserHarness();
        h.Feed(0x1B, (byte)'-', (byte)'8', (byte)'0', (byte)'W', 0x9B, 0xFF, 0xFF, (byte)'x', (byte)'\n');
        Assert.Single(h.ConfirmedWidths);
        var line = Assert.Single(h.Lines);
        Assert.Equal("x", line.PlainText);
    }

    [Fact]
    public void TerminalWidth_TextLine_FiresConfirmedEvent()
    {
        // Plain "[New terminal width is N]" line (mud-mode, no ESC- prefix):
        // should fire TerminalWidthConfirmed(N) and suppress LineReady.
        var h = new ParserHarness();
        h.Feed("[New terminal width is 80]\r\n");
        Assert.Single(h.ConfirmedWidths);
        Assert.Equal(80, h.ConfirmedWidths[0]);
        Assert.Empty(h.Lines);
    }

    [Fact]
    public void TerminalWidth_TextLine_OnlyInPreGame()
    {
        // In game mode the "[New terminal width is N]" pattern is not suppressed —
        // extremely unusual to receive in-game, and we must not silently eat game output.
        var h = new ParserHarness();
        // Enter game mode via C02+C01+C255
        h.Feed("\x9D\x9C\xFF\xFF");
        h.ClearCounters();
        h.Feed("[New terminal width is 80]\r\n");
        Assert.Empty(h.ConfirmedWidths);
        Assert.Single(h.Lines);
    }

    [Fact]
    public void TerminalWidth_EscDash_OtherLetter_Consumed()
    {
        // ESC-C (a named server command letter) must be silently consumed; the text
        // that follows is unrelated and should be displayed normally.
        var h = new ParserHarness();
        h.Feed("\x1B-Chello\n");
        Assert.Single(h.Lines);
        Assert.Equal("hello", h.Lines[0].PlainText);
        Assert.Empty(h.ConfirmedWidths);
    }
}
