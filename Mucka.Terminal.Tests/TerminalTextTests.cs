using MudSharp.Models;
using Mucka.Terminal;

namespace Mucka.Terminal.Tests;

/// <summary>Tests for <see cref="TerminalText"/> — control-character stripping (tofu prevention).</summary>
public class TerminalTextTests
{
    private static StyledSpan Span(string text, TextStyle? style = null) => new(text, style ?? TextStyle.Default);
    private static StyledLine Line(bool partial, params StyledSpan[] spans) => new(spans, partial);

    [Fact]
    public void StripControls_RemovesCarriageReturnBackspaceTabAndDel()
    {
        Assert.Equal("Password:", TerminalText.StripControls("Password:\r"));
        Assert.Equal("ab", TerminalText.StripControls("a\bb"));
        Assert.Equal("xy", TerminalText.StripControls("x\ty"));
        Assert.Equal("z", TerminalText.StripControls("z"));
    }

    [Fact]
    public void StripControls_PreservesFormFeed()
    {
        // \f must survive so TerminalBuffer can still treat it as clear-screen.
        Assert.Equal("\f", TerminalText.StripControls("\r\f\b"));
    }

    [Fact]
    public void StripControls_ReturnsSameInstance_WhenClean()
    {
        var s = "nothing to strip here";
        Assert.Same(s, TerminalText.StripControls(s));
    }

    [Fact]
    public void Sanitize_StripsTrailingCarriageReturn_PreservingStyleAndPartial()
    {
        var red = new TextStyle(Foreground: AnsiColor.Red);
        var line = Line(partial: true, Span("Password:\r", red));

        var clean = TerminalText.Sanitize(line);

        Assert.True(clean.IsPartial);
        Assert.Single(clean.Spans);
        Assert.Equal("Password:", clean.Spans[0].Text);
        Assert.Equal(red, clean.Spans[0].Style);
    }

    [Fact]
    public void Sanitize_DropsSpansThatBecomeEmpty()
    {
        var line = Line(partial: false, Span("keep"), Span("\r"), Span("end"));

        var clean = TerminalText.Sanitize(line);

        Assert.Equal(new[] { "keep", "end" }, clean.Spans.Select(s => s.Text));
    }

    [Fact]
    public void Sanitize_LoneControlChar_YieldsEmptyLine()
    {
        var clean = TerminalText.Sanitize(Line(partial: false, Span("\r")));
        Assert.Empty(clean.Spans);
    }

    [Fact]
    public void Sanitize_ReturnsSameInstance_WhenClean()
    {
        var line = Line(partial: false, Span("clean text"));
        Assert.Same(line, TerminalText.Sanitize(line));
    }
}
