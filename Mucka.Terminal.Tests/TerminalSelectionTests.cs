using MudSharp.Models;
using Mucka.Terminal;

namespace Mucka.Terminal.Tests;

/// <summary>Tests for <see cref="TerminalSelection"/> - plain-text extraction over visual rows.</summary>
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
    public void ClearRuleRowInRange_CopiesAsABlankLine_NotAFormFeed()
    {
        IReadOnlyList<StyledLine> rows = [Row("before"), TerminalBuffer.ClearRule, Row("after")];
        Assert.Equal("before\n\nafter", TerminalSelection.Extract(rows, (0, 0), (2, 5)));
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

    // -- Soft breaks ---------------------------------------------------------------
    //
    // One long client line ("Recording started. File: C:\...") wrapped over three rows, then a
    // separate line under it. Copying must give back the path unbroken - a wrap is a property of
    // the pane, and pasting it into a shell with a newline through it is the bug this prevents.

    private static readonly IReadOnlyList<StyledLine> Wrapped =
    [
        Row("aaaa"),
        Row("bbbb"),
        Row("cc"),
        Row("next"),
    ];
    private static readonly IReadOnlyList<bool> WrappedContinues = [false, true, true, false];

    [Fact]
    public void SoftBreaks_AreNotNewlines_ButHardBreaksStillAre()
    {
        Assert.Equal("aaaabbbbcc\nnext",
            TerminalSelection.Extract(Wrapped, (0, 0), (3, 4), WrappedContinues));
    }

    [Fact]
    public void SelectionStartingMidContinuation_HasNoLeadingNewline()
    {
        Assert.Equal("bbcc", TerminalSelection.Extract(Wrapped, (1, 2), (2, 2), WrappedContinues));
    }

    [Fact]
    public void WithoutFlags_EveryRowBoundaryIsStillHard()
    {
        // The old behaviour, kept for any caller that has no wrap information.
        Assert.Equal("aaaa\nbbbb\ncc\nnext", TerminalSelection.Extract(Wrapped, (0, 0), (3, 4)));
    }

    [Theory]
    [InlineData("hello world", 2, 0, 5)]
    [InlineData("hello world", 5, 5, 6)]
    [InlineData("hello world", 10, 6, 11)]
    [InlineData("a_b-c", 1, 0, 3)]
    [InlineData("say \"hi\"!", 4, 4, 5)]
    public void WordBounds_RunOfTheSameClass(string text, int col, int start, int end)
        => Assert.Equal((start, end), TerminalSelection.WordBounds(text, col));

    [Fact]
    public void WordBounds_PastTheEnd_IsNull()
    {
        Assert.Null(TerminalSelection.WordBounds("abc", 3));
        Assert.Null(TerminalSelection.WordBounds("", 0));
    }

    [Fact]
    public void LineRows_SpansSoftWrappedRows()
    {
        bool[] continues = [false, true, true, false, true];
        Assert.Equal((0, 2), TerminalSelection.LineRows(continues, 5, 1));
        Assert.Equal((0, 2), TerminalSelection.LineRows(continues, 5, 2));
        Assert.Equal((3, 4), TerminalSelection.LineRows(continues, 5, 3));
        Assert.Equal((1, 1), TerminalSelection.LineRows(null, 5, 1));
    }

    [Fact]
    public void LineRows_NoRows_IsZeroZero()
        => Assert.Equal((0, 0), TerminalSelection.LineRows(null, 0, 3));
}
