using MudSharp.Models;
using Mucka.Terminal;

namespace Mucka.Terminal.Tests;

/// <summary>
/// Tests for <see cref="TerminalBuffer"/> — the C# port of the partial/complete/merge/clear
/// line semantics that previously lived as JavaScript in GamePage.BuildInjectionScript.
/// </summary>
public class TerminalBufferTests
{
    private static StyledLine Complete(string text) =>
        new([new StyledSpan(text, TextStyle.Default)], isPartial: false);

    private static StyledLine Partial(string text) =>
        new([new StyledSpan(text, TextStyle.Default)], isPartial: true);

    private static StyledLine Blank() =>
        new(Array.Empty<StyledSpan>(), isPartial: false);

    // -- Plain accumulation ---------------------------------------------------

    [Fact]
    public void CompleteLines_AccumulateInOrder_WithNoPartial()
    {
        var buf = new TerminalBuffer();
        buf.Append(Complete("one"));
        buf.Append(Complete("two"));

        Assert.Null(buf.Partial);
        Assert.Equal(2, buf.Count);
        Assert.Equal(new[] { "one", "two" }, buf.Committed.Select(l => l.PlainText));
    }

    // -- Partial handling -----------------------------------------------------

    [Fact]
    public void PartialLine_SetsPartial_NotCommitted()
    {
        var buf = new TerminalBuffer();
        buf.Append(Partial("* "));

        Assert.Empty(buf.Committed);
        Assert.NotNull(buf.Partial);
        Assert.Equal("* ", buf.Partial!.PlainText);
        Assert.Equal(1, buf.Count);
    }

    [Fact]
    public void Partial_IsReplacedWholesale_ByNextPartial()
    {
        var buf = new TerminalBuffer();
        buf.Append(Partial("* "));
        buf.Append(Partial("** "));

        Assert.Empty(buf.Committed);
        Assert.Equal("** ", buf.Partial!.PlainText);
    }

    // -- Blank complete line --------------------------------------------------

    [Fact]
    public void BlankComplete_WithPartial_PromotesPartial_NoExtraBlank()
    {
        var buf = new TerminalBuffer();
        buf.Append(Partial("* "));
        buf.Append(Blank());   // user pressed Enter on a bare prompt

        Assert.Null(buf.Partial);
        Assert.Single(buf.Committed);
        Assert.Equal("* ", buf.Committed[0].PlainText);
        Assert.False(buf.Committed[0].IsPartial);   // promoted: partial flag cleared
    }

    [Fact]
    public void BlankComplete_WithoutPartial_AppendsBlankLine()
    {
        var buf = new TerminalBuffer();
        buf.Append(Complete("text"));
        buf.Append(Blank());

        Assert.Equal(2, buf.Committed.Count);
        Assert.Equal(string.Empty, buf.Committed[1].PlainText);
    }

    // -- Non-empty complete line merging --------------------------------------

    [Fact]
    public void NonEmptyComplete_WithPartial_MergesOntoOneLine()
    {
        var buf = new TerminalBuffer();
        buf.Append(Partial("* "));
        buf.Append(Complete("look"));   // prompt + echoed/echo'd content on one line

        Assert.Null(buf.Partial);
        Assert.Single(buf.Committed);
        Assert.Equal("* look", buf.Committed[0].PlainText);
        // Spans are concatenated, not flattened.
        Assert.Equal(2, buf.Committed[0].Spans.Count);
    }

    [Fact]
    public void NonEmptyComplete_WithoutPartial_Appends()
    {
        var buf = new TerminalBuffer();
        buf.Append(Complete("hello"));

        Assert.Null(buf.Partial);
        Assert.Single(buf.Committed);
        Assert.Equal("hello", buf.Committed[0].PlainText);
    }

    // -- Form-feed clear ------------------------------------------------------

    [Fact]
    public void FormFeed_ClearsCommittedAndPartial()
    {
        var buf = new TerminalBuffer();
        buf.Append(Complete("one"));
        buf.Append(Complete("two"));
        buf.Append(Partial("* "));

        buf.Append(Complete("\f"));   // clear-screen

        Assert.Empty(buf.Committed);
        Assert.Null(buf.Partial);
        Assert.Equal(0, buf.Count);
    }

    // -- Ring cap -------------------------------------------------------------

    [Fact]
    public void Committed_IsTrimmedToCapacity_DroppingOldest()
    {
        var buf = new TerminalBuffer(cap: 3);
        for (int i = 0; i < 5; i++)
            buf.Append(Complete($"line{i}"));

        Assert.Equal(3, buf.Committed.Count);
        Assert.Equal(new[] { "line2", "line3", "line4" }, buf.Committed.Select(l => l.PlainText));
    }

    [Fact]
    public void Partial_DoesNotCountAgainstCapacity()
    {
        var buf = new TerminalBuffer(cap: 2);
        buf.Append(Complete("a"));
        buf.Append(Complete("b"));
        buf.Append(Partial("* "));

        Assert.Equal(2, buf.Committed.Count);
        Assert.NotNull(buf.Partial);
    }

    // -- Snapshot -------------------------------------------------------------

    [Fact]
    public void Snapshot_IncludesPartial_AsLastLine()
    {
        var buf = new TerminalBuffer();
        buf.Append(Complete("one"));
        buf.Append(Partial("* "));

        var snap = buf.Snapshot();

        Assert.Equal(new[] { "one", "* " }, snap.Select(l => l.PlainText));
    }

    [Fact]
    public void Snapshot_IsImmutableCopy_UnaffectedByLaterAppends()
    {
        var buf = new TerminalBuffer();
        buf.Append(Complete("one"));
        var snap = buf.Snapshot();

        buf.Append(Complete("two"));   // mutate after freezing

        Assert.Single(snap);
        Assert.Equal("one", snap[0].PlainText);
    }

    [Fact]
    public void Snapshot_WithNoPartial_ReturnsCommittedOnly()
    {
        var buf = new TerminalBuffer();
        buf.Append(Complete("only"));

        var snap = buf.Snapshot();

        Assert.Single(snap);
        Assert.Equal("only", snap[0].PlainText);
    }

    // -- Constructor guard ----------------------------------------------------

    [Fact]
    public void Constructor_RejectsNonPositiveCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TerminalBuffer(cap: 0));
    }
}
