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
    public void CampbellHex_AllSixteenColorsPresent()
    {
        // Every standard AnsiColor (0–15) must appear in the Campbell palette dictionary.
        var map = AnsiSgrState.CampbellHex;
        Assert.Equal(16, map.Count);

        var standardColors = new[]
        {
            AnsiColor.Black,        AnsiColor.Red,          AnsiColor.Green,        AnsiColor.Yellow,
            AnsiColor.Blue,         AnsiColor.Magenta,      AnsiColor.Cyan,         AnsiColor.White,
            AnsiColor.BrightBlack,  AnsiColor.BrightRed,    AnsiColor.BrightGreen,  AnsiColor.BrightYellow,
            AnsiColor.BrightBlue,   AnsiColor.BrightMagenta,AnsiColor.BrightCyan,   AnsiColor.BrightWhite,
        };

        foreach (var color in standardColors)
        {
            Assert.True(map.ContainsKey(color), $"CampbellHex missing entry for {color}");
            Assert.Matches(@"^#[0-9A-Fa-f]{6}$", map[color]);
        }
    }
}
