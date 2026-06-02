using MudSharp.Models;
using Mucka.Terminal;

namespace Mucka.Terminal.Tests;

/// <summary>Tests for <see cref="TerminalSelection"/> — plain-text extraction over visual rows.</summary>
public class TerminalSelectionTests
{
    private static StyledLine Row(string text) =>
        new(text.Length == 0 ? Array.Empty<StyledSpan>() : [new StyledSpan(text, TextStyle.Default)], isPartial: false);

    private static readonly IReadOnlyList<StyledLine> Rows =
    [
        Row("hello world"),
        Row("second line"),
        Row("third"),
    ];

    [Fact]
    public void SingleRow_PartialRange()
    {
        Assert.Equal("ello", TerminalSelection.Extract(Rows, (0, 1), (0, 5)));
    }

    [Fact]
    public void SingleRow_ReversedEndpoints_NormalizeSame()
    {
        // Anchor after caret must yield the same text.
        Assert.Equal("ello", TerminalSelection.Extract(Rows, (0, 5), (0, 1)));
    }

    [Fact]
    public void MultiRow_FirstPartial_MiddleFull_LastPartial()
    {
        // from row0 col6 ("world") through row2 col5 ("third")
        var text = TerminalSelection.Extract(Rows, (0, 6), (2, 5));
        Assert.Equal("world\nsecond line\nthird", text);
    }

    [Fact]
    public void ColumnsBeyondLineLength_AreClamped()
    {
        Assert.Equal("hello world", TerminalSelection.Extract(Rows, (0, 0), (0, 999)));
    }

    [Fact]
    public void BlankRowInRange_ContributesEmptyLine()
    {
        IReadOnlyList<StyledLine> rows = [Row("a"), Row(""), Row("b")];
        Assert.Equal("a\n\nb", TerminalSelection.Extract(rows, (0, 0), (2, 1)));
    }

    [Fact]
    public void EmptyRowList_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, TerminalSelection.Extract(Array.Empty<StyledLine>(), (0, 0), (3, 3)));
    }

    [Fact]
    public void ZeroWidthSelection_OnOneRow_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, TerminalSelection.Extract(Rows, (1, 3), (1, 3)));
    }
}
