using MudSharp.Models;
using Mucka.Terminal;

namespace Mucka.Terminal.Tests;

/// <summary>
/// Tests for <see cref="TerminalBuffer"/> - the partial/complete/merge/clear line semantics that
/// decide where one line of screen ends.
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

    // What the live screen shows: the committed lines from LiveStart on.
    private static string[] Live(TerminalBuffer buf) =>
        buf.Committed.Skip(buf.LiveStart).Select(l => l.PlainText).ToArray();

    // What scrollback shows, with the rule written as "---".
    private static string[] History(TerminalBuffer buf) =>
        buf.Snapshot().Select(l => TerminalBuffer.IsClearRule(l) ? "---" : l.PlainText).ToArray();

    [Fact]
    public void FormFeed_EmptiesTheLiveScreen_AndScrollbackKeepsEverythingAboveARule()
    {
        var buf = new TerminalBuffer();
        buf.Append(Complete("one"));
        buf.Append(Complete("two"));
        buf.Append(Partial("* "));

        buf.Append(Complete("\f"));   // clear-screen

        Assert.Empty(Live(buf));
        Assert.Null(buf.Partial);
        // The prompt that was live at the clear is scrollback now, not lost.
        Assert.Equal(new[] { "one", "two", "* ", "---" }, History(buf));
    }

    [Fact]
    public void FormFeed_TextBeforeItOnTheLine_MergesIntoThePrompt_AndTextAfterItStartsTheNewScreen()
    {
        var buf = new TerminalBuffer();
        buf.Append(Partial("*"));
        buf.Append(Complete("half\fnew"));

        Assert.Equal(new[] { "new" }, Live(buf));
        Assert.Equal(new[] { "*half", "---", "new" }, History(buf));
    }

    [Fact]
    public void FormFeed_RepeatedClears_DrawOneRule()
    {
        var buf = new TerminalBuffer();
        buf.Append(Complete("before"));
        buf.Append(Complete("\f"));
        buf.Append(Complete("\f\f"));
        buf.Append(Complete("after"));

        Assert.Equal(new[] { "before", "---", "after" }, History(buf));
    }

    [Fact]
    public void FormFeed_WithNothingAbove_DrawsNoRule()
    {
        var buf = new TerminalBuffer();
        buf.Append(Complete("\f"));
        buf.Append(Complete("first"));

        Assert.Equal(new[] { "first" }, History(buf));
        Assert.Equal(new[] { "first" }, Live(buf));
    }

    [Fact]
    public void FormFeed_LiveStartFollowsTheCapTrim()
    {
        var buf = new TerminalBuffer(cap: 3);
        buf.Append(Complete("old"));
        buf.Append(Complete("\f"));          // old, rule  (LiveStart 2)
        buf.Append(Complete("a"));           // old, rule, a
        Assert.Equal(new[] { "a" }, Live(buf));

        buf.Append(Complete("b"));           // rule, a, b
        Assert.Equal(new[] { "a", "b" }, Live(buf));

        buf.Append(Complete("c"));           // a, b, c - the rule itself trimmed away
        buf.Append(Complete("d"));
        Assert.Equal(0, buf.LiveStart);
        Assert.Equal(new[] { "b", "c", "d" }, Live(buf));
    }

    [Fact]
    public void FormFeed_TheRuleIsCommitted_SoTheRecorderSeesIt()
    {
        var buf = new TerminalBuffer();
        var seen = new List<StyledLine>();
        buf.LineCommitted += seen.Add;
        buf.Append(Complete("x"));
        buf.Append(Complete("\f"));

        Assert.Equal(2, seen.Count);
        Assert.True(TerminalBuffer.IsClearRule(seen[1]));
    }

    [Fact]
    public void Clear_IsTheClientsWipe_AndTakesScrollbackWithIt()
    {
        var buf = new TerminalBuffer();
        buf.Append(Complete("one"));
        buf.Append(Complete("\f"));
        buf.Append(Complete("two"));

        buf.Clear();

        Assert.Empty(buf.Snapshot());
        Assert.Equal(0, buf.LiveStart);
    }

    [Fact]
    public void ClearRule_SurvivesWrapping()
    {
        // The renderer and the copy path see wrapped rows, never the buffer's instance.
        var rows = LineWrapper.WrapAll(new[] { TerminalBuffer.ClearRule }, 5);
        Assert.Single(rows);
        Assert.True(TerminalBuffer.IsClearRule(rows[0]));
    }

    // -- Inject above the live partial ($f<n> annotation) ---------------------

    [Fact]
    public void InjectAbovePartial_WithPartial_CommitsAboveAndRestoresPrompt()
    {
        var buf = new TerminalBuffer();
        buf.Append(Complete("you are here"));
        buf.Append(Partial("* "));            // sitting at a prompt

        buf.InjectAbovePartial(Complete("// north"));

        // The note commits above; the prompt is restored as the live partial below it.
        Assert.Equal(new[] { "you are here", "// north" }, buf.Committed.Select(l => l.PlainText));
        Assert.NotNull(buf.Partial);
        Assert.Equal("* ", buf.Partial!.PlainText);
    }

    [Fact]
    public void InjectAbovePartial_WithoutPartial_JustCommits()
    {
        var buf = new TerminalBuffer();
        buf.Append(Complete("plain"));

        buf.InjectAbovePartial(Complete("// note"));

        Assert.Null(buf.Partial);
        Assert.Equal(new[] { "plain", "// note" }, buf.Committed.Select(l => l.PlainText));
    }

    [Fact]
    public void InjectAbovePartial_DoesNotMergeIntoPrompt()
    {
        var buf = new TerminalBuffer();
        buf.Append(Partial("* "));

        buf.InjectAbovePartial(Complete("// note"));

        // Distinct from Append's merge behaviour: the note is its own line, prompt untouched.
        Assert.Single(buf.Committed);
        Assert.Equal("// note", buf.Committed[0].PlainText);
        Assert.Equal("* ", buf.Partial!.PlainText);
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

    [Fact]
    public void ServerClearScreenEscape_ThroughTheParser_ClearsTheLiveScreen_AndScrollbackKeepsWhatCameBefore()
    {
        // The server's ESC-C reaches the buffer in order with the text: the live screen keeps only
        // what follows it; scrollback keeps everything before it, the part-line it interrupted
        // included, above the rule.
        var parser = new MudSharp.Protocol.MudStreamParser();
        var buf = new TerminalBuffer();
        parser.LineReady += buf.Append;
        parser.Feed("old one\nold two\nhalf\x1B-Cnew\n"u8);

        Assert.Equal(new[] { "new" }, Live(buf));
        Assert.Equal(new[] { "old one", "old two", "half", "---", "new" }, History(buf));
        Assert.Null(buf.Partial);
    }
}
