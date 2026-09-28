using MudSharp.Models;
using Mucka.Terminal;

namespace Mucka.Terminal.Tests;

/// <summary>
/// Tests for <see cref="PaneHistory"/>: replaying its main snapshot into an empty pane must leave
/// the pane exactly as it would stand had the chat filter never hidden it - prompts included.
/// </summary>
public class PaneHistoryTests
{
    private static StyledLine Complete(string text, LineKind kind = LineKind.Normal) =>
        new([new StyledSpan(text, TextStyle.Default)], isPartial: false, kind);

    private static StyledLine Partial(string text) =>
        new([new StyledSpan(text, TextStyle.Default)], isPartial: true);

    private static StyledLine Blank() =>
        new(Array.Empty<StyledSpan>(), isPartial: false);

    // The pane's per-line transform (TerminalView.AppendLines / InjectAnnotation).
    private static StyledLine PaneForm(StyledLine l) => TerminalText.ExpandTabs(TerminalText.Sanitize(l));

    // One step of the stream: a line from the server, or a client annotation.
    private sealed record Step(StyledLine Line, bool Annotation = false);

    private static Step Note(string text) => new(Complete(text), Annotation: true);

    // Feeds the stream to a pane that never left non-chat mode, and to a PaneHistory whose snapshot
    // is then replayed into an empty pane - the chat-mode round trip.
    private static (TerminalBuffer Direct, TerminalBuffer Restored) RoundTrip(params Step[] steps)
    {
        var direct = new TerminalBuffer(cap: 500);
        var history = new PaneHistory(mainCap: 1000, chatCap: 3000);
        foreach (var s in steps)
        {
            if (s.Annotation)
            {
                direct.InjectAbovePartial(PaneForm(s.Line));
                history.InjectAbovePartial(s.Line);
            }
            else
            {
                direct.Append(PaneForm(s.Line));
                history.Append(s.Line);
            }
        }
        var restored = new TerminalBuffer(cap: 500);
        foreach (var l in history.MainSnapshot()) restored.Append(PaneForm(l));
        return (direct, restored);
    }

    private static void AssertSamePane(TerminalBuffer expected, TerminalBuffer actual)
    {
        Assert.Equal(expected.Committed.Select(l => l.PlainText), actual.Committed.Select(l => l.PlainText));
        Assert.All(actual.Committed, l => Assert.False(l.IsPartial));
        Assert.Equal(expected.LiveStart, actual.LiveStart);
        Assert.Equal(expected.Partial?.PlainText, actual.Partial?.PlainText);
        Assert.Equal(expected.Partial?.IsPartial, actual.Partial?.IsPartial);
    }

    private static Step S(StyledLine l) => new(l);

    [Fact]
    public void PromptReplacedFiveTimes_ThenInput_RestoresOneMergedLine_AndTheLivePrompt()
    {
        var (direct, restored) = RoundTrip(
            S(Complete("You are in a small room.")),
            S(Partial("*")), S(Partial("(*)")), S(Partial("*")), S(Partial("(*)")), S(Partial("*")),
            S(Complete("look")),
            S(Complete("It is dark.")),
            S(Partial("*")));

        AssertSamePane(direct, restored);
        Assert.Equal(new[] { "You are in a small room.", "*look", "It is dark." },
            restored.Committed.Select(l => l.PlainText));
        Assert.Equal("*", restored.Partial?.PlainText);
    }

    [Fact]
    public void PromptThenBlankLine_IsPromoted_WithNoExtraBlank()
    {
        var (direct, restored) = RoundTrip(
            S(Partial("*")), S(Blank()),
            S(Partial("*")), S(Blank()),
            S(Complete("text")));

        AssertSamePane(direct, restored);
        Assert.Equal(new[] { "*", "*", "text" }, restored.Committed.Select(l => l.PlainText));
    }

    [Fact]
    public void ClearScreen_WithPromptPending_KeepsRuleAndLiveStart()
    {
        var (direct, restored) = RoundTrip(
            S(Complete("before")),
            S(Partial("*")),
            S(Complete("\f")),
            S(Complete("after the clear")),
            S(Partial("*")),
            S(Complete("tail\fhead")),
            S(Partial("*")));

        AssertSamePane(direct, restored);
        Assert.Equal(2, restored.Committed.Count(TerminalBuffer.IsClearRule));
        Assert.True(restored.LiveStart > 0);
    }

    [Fact]
    public void AnnotationAboveLivePrompt_IsRestoredAboveIt()
    {
        var (direct, restored) = RoundTrip(
            S(Complete("room")),
            S(Partial("*")),
            Note("// kill rat"),
            S(Complete("kill rat")));

        AssertSamePane(direct, restored);
        Assert.Equal(new[] { "room", "// kill rat", "*kill rat" }, restored.Committed.Select(l => l.PlainText));
    }

    [Fact]
    public void TabInInputAfterPrompt_ExpandsAtTheSameColumnAsThePane()
    {
        var (direct, restored) = RoundTrip(
            S(Partial("*")),
            S(Complete("\tx")));

        AssertSamePane(direct, restored);
    }

    [Fact]
    public void ChatRing_KeepsFinishedChatLines_ClassifiedOnTheIncomingLine()
    {
        var history = new PaneHistory(mainCap: 1000, chatCap: 3000);
        history.Append(Complete("Fred shouts \"hi\"\r", LineKind.Chat));   // \r: sanitising drops Kind
        history.Append(Complete("You are in a room."));
        history.Append(new StyledLine([new StyledSpan("partial chat", TextStyle.Default)], isPartial: true, LineKind.Chat));

        Assert.Equal(new[] { "Fred shouts \"hi\"\r" }, history.ChatSnapshot().Select(l => l.PlainText));
    }

    [Fact]
    public void ChatRing_IsCappedIndependentlyOfMainHistory()
    {
        var history = new PaneHistory(mainCap: 2, chatCap: 3);
        for (int i = 0; i < 5; i++) history.Append(Complete($"chat {i}", LineKind.Chat));

        Assert.Equal(new[] { "chat 2", "chat 3", "chat 4" }, history.ChatSnapshot().Select(l => l.PlainText));
        Assert.Equal(new[] { "chat 3", "chat 4" }, history.Committed.Select(l => l.PlainText));
    }
}
