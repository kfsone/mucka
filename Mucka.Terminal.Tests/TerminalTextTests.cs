using MudSharp.Models;
using Mucka.Terminal;

namespace Mucka.Terminal.Tests;

/// <summary>Tests for <see cref="TerminalText"/> — control-char stripping + tab expansion.</summary>
public class TerminalTextTests
{
    private static StyledSpan Span(string text, TextStyle? style = null) => new(text, style ?? TextStyle.Default);
    private static StyledLine Line(bool partial, params StyledSpan[] spans) => new(spans, partial);

    [Fact]
    public void StripControls_RemovesCarriageReturnBackspaceAndDel_ButKeepsTab()
    {
        Assert.Equal("Password:", TerminalText.StripControls("Password:\r"));
        Assert.Equal("ab", TerminalText.StripControls("a\bb"));
        Assert.Equal("ab", TerminalText.StripControls("ab"));   // DEL stripped
        Assert.Equal("x\ty", TerminalText.StripControls("x\ty"));     // tab preserved (expanded later)
        Assert.Equal("z", TerminalText.StripControls("z"));
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

    // ── Tab expansion ─────────────────────────────────────────────────────────

    [Fact]
    public void ExpandTabs_AtColumnZero_FillsToFirstStop()
    {
        var line = TerminalText.ExpandTabs(Line(false, Span("\tx")));
        Assert.Equal("        x", line.Spans[0].Text);   // 8 spaces + x
    }

    [Fact]
    public void ExpandTabs_AdvancesToNextStop()
    {
        var line = TerminalText.ExpandTabs(Line(false, Span("ab\tc")));
        Assert.Equal("ab      c", line.Spans[0].Text);   // col 2 → +6 spaces → c at col 8
    }

    [Fact]
    public void ExpandTabs_OnStopBoundary_AddsFullTab()
    {
        var line = TerminalText.ExpandTabs(Line(false, Span("12345678\t")));
        Assert.Equal("12345678        ", line.Spans[0].Text);   // col 8 → +8 spaces
    }

    [Fact]
    public void ExpandTabs_TracksColumnAcrossSpans()
    {
        var line = TerminalText.ExpandTabs(Line(false, Span("ab"), Span("\tc")));
        Assert.Equal("ab      c", line.PlainText);   // running column carries across spans
    }

    [Fact]
    public void ExpandTabs_PreservesStyleAndPartial()
    {
        var red = new TextStyle(Foreground: AnsiColor.Red);
        var line = TerminalText.ExpandTabs(Line(partial: true, Span("\tx", red)));
        Assert.True(line.IsPartial);
        Assert.Equal(red, line.Spans[0].Style);
    }

    [Fact]
    public void ExpandTabs_NoTabs_ReturnsSameInstance()
    {
        var line = Line(false, Span("no tabs here"));
        Assert.Same(line, TerminalText.ExpandTabs(line));
    }
}
