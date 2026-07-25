using MudSharp.Models;
using Mucka.Terminal;

namespace Mucka.Terminal.Tests;

/// <summary>Tests for <see cref="LineWrapper"/> — naive fixed-column wrapping with style preservation.</summary>
public class LineWrapperTests
{
    private static readonly TextStyle Red = new(Foreground: AnsiColor.Red);
    private static readonly TextStyle Blue = new(Foreground: AnsiColor.Blue);

    private static StyledLine Line(params StyledSpan[] spans) => new(spans, isPartial: false);
    private static StyledSpan Span(string text, TextStyle? style = null) => new(text, style ?? TextStyle.Default);

    // -- No-op when already within the column count -----------------------------

    [Fact]
    public void ShortLine_ProducesSingleRow_Unchanged()
    {
        var rows = LineWrapper.Wrap(Line(Span("hello")), columns: 80);

        Assert.Single(rows);
        Assert.Equal("hello", rows[0].PlainText);
    }

    // -- Splitting within one span ----------------------------------------------

    [Fact]
    public void LongSpan_IsHardBrokenAtColumn()
    {
        var rows = LineWrapper.Wrap(Line(Span("abcdefghij")), columns: 4);

        Assert.Equal(new[] { "abcd", "efgh", "ij" }, rows.Select(r => r.PlainText));
    }

    [Fact]
    public void ExactMultiple_ProducesNoTrailingEmptyRow()
    {
        var rows = LineWrapper.Wrap(Line(Span("abcdefgh")), columns: 4);

        Assert.Equal(new[] { "abcd", "efgh" }, rows.Select(r => r.PlainText));
    }

    // -- Splitting across a span boundary, preserving styles --------------------

    [Fact]
    public void SplitAcrossSpans_PreservesStyles()
    {
        // "abc"(red) + "defghij"(blue), wrapped at 5 → ["ab cde"->row1: abc+de], [fghij]
        var rows = LineWrapper.Wrap(Line(Span("abc", Red), Span("defghij", Blue)), columns: 5);

        Assert.Equal(new[] { "abcde", "fghij" }, rows.Select(r => r.PlainText));

        // Row 0: "abc" red, "de" blue.
        Assert.Equal(2, rows[0].Spans.Count);
        Assert.Equal(("abc", Red), (rows[0].Spans[0].Text, rows[0].Spans[0].Style));
        Assert.Equal(("de", Blue), (rows[0].Spans[1].Text, rows[0].Spans[1].Style));

        // Row 1: "fghij" blue.
        Assert.Single(rows[1].Spans);
        Assert.Equal(("fghij", Blue), (rows[1].Spans[0].Text, rows[1].Spans[0].Style));
    }

    // -- ClickInsertText survives the wrap (clickable names stay clickable) ------

    [Fact]
    public void SplitSpan_PreservesClickInsertText_InEveryPiece()
    {
        // A clickable underlined name "Frederick" split across a wrap boundary must keep its
        // insert payload in both pieces, or TryActivateSpanInsert can't fire on the tail row.
        var name = new StyledSpan("Frederick", Red, ClickInsertText: "Frederick ");
        var rows = LineWrapper.Wrap(Line(name), columns: 4);

        Assert.Equal(new[] { "Fred", "eric", "k" }, rows.Select(r => r.PlainText));
        Assert.All(rows, r => Assert.Equal("Frederick ", r.Spans[0].ClickInsertText));
    }

    // -- Blank lines -------------------------------------------------------------

    [Fact]
    public void BlankLine_ProducesOneEmptyRow()
    {
        var rows = LineWrapper.Wrap(Line(), columns: 10);

        Assert.Single(rows);
        Assert.Empty(rows[0].Spans);
        Assert.Equal(string.Empty, rows[0].PlainText);
    }

    // -- WrapAll -----------------------------------------------------------------

    [Fact]
    public void WrapAll_FlattensRowsInOrder()
    {
        var lines = new[]
        {
            Line(Span("abcdef")),   // -> "abc","def"
            Line(Span("gh")),       // -> "gh"
        };

        var rows = LineWrapper.WrapAll(lines, columns: 3);

        Assert.Equal(new[] { "abc", "def", "gh" }, rows.Select(r => r.PlainText));
    }

    // -- Guard -------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void NonPositiveColumns_Throws(int columns)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LineWrapper.Wrap(Line(Span("x")), columns));
    }
}
