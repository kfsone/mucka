using MudSharp.Models;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// Tests for <see cref="SelfChatColorizer"/>: self-authored chat lines (name-prefix or "You "
/// self-forms, chat kind only) get the label painted in the name colour and the quoted speech in
/// the speech colour, while other lines pass through untouched.
/// </summary>
public class SelfChatColorizerTests
{
    private const int NameRgb = 0x0026ff;
    private const int SpeechRgb = 0x0094ff;

    private static StyledLine Chat(string text) =>
        new(new[] { new StyledSpan(text, TextStyle.Default) }, isPartial: false, kind: LineKind.Chat);

    private static StyledLine Normal(string text) =>
        new(new[] { new StyledSpan(text, TextStyle.Default) }, isPartial: false, kind: LineKind.Normal);

    [Theory]
    [InlineData("#0026ff", 0x0026ff)]
    [InlineData("0026ff", 0x0026ff)]
    [InlineData(" 0094FF ", 0x0094ff)]
    [InlineData("nope", null)]
    [InlineData("12345", null)]
    [InlineData("", null)]
    public void TryParseRgb_ParsesOrRejects(string input, int? expected)
        => Assert.Equal(expected, SelfChatColorizer.TryParseRgb(input));

    [Fact]
    public void NameLed_SayLine_ColoursLabelAndSpeech()
    {
        var line = SelfChatColorizer.Apply(
            Chat("Ollie the swordsman says \"hello there\"."), "Ollie", NameRgb, SpeechRgb);

        // Label portions carry the name colour; the quoted run carries the speech colour.
        Assert.Contains(line.Spans, s => s.Text == "Ollie the swordsman says " && s.Style.ForegroundRgb == NameRgb);
        Assert.Contains(line.Spans, s => s.Text == "\"hello there\"" && s.Style.ForegroundRgb == SpeechRgb);
        Assert.Contains(line.Spans, s => s.Text == "." && s.Style.ForegroundRgb == NameRgb);
    }

    [Fact]
    public void YouSelfForm_TellLine_IsColoured()
    {
        var line = SelfChatColorizer.Apply(
            Chat("You tell your listeners \"hi\"."), myName: "Ollie", NameRgb, SpeechRgb);

        Assert.Contains(line.Spans, s => s.Text == "You tell your listeners " && s.Style.ForegroundRgb == NameRgb);
        Assert.Contains(line.Spans, s => s.Text == "\"hi\"" && s.Style.ForegroundRgb == SpeechRgb);
    }

    [Fact]
    public void OthersTell_NotColoured()
    {
        // Starts with someone else's name (and "You" only as a substring of "Youssef").
        var line = Chat("Youssef tells you \"hi\".");
        var result = SelfChatColorizer.Apply(line, "Ollie", NameRgb, SpeechRgb);
        Assert.Same(line, result);
        Assert.All(result.Spans, s => Assert.Null(s.Style.ForegroundRgb));
    }

    [Fact]
    public void NonChatLine_NotColoured()
    {
        var line = Normal("Ollie the swordsman says \"hi\".");   // same text, but not a chat line
        var result = SelfChatColorizer.Apply(line, "Ollie", NameRgb, SpeechRgb);
        Assert.Same(line, result);
    }

    [Fact]
    public void WrappedTell_ContinuationLine_StaysSpeechColoured()
    {
        // The server soft-wraps a long tell into two chat lines; only the first starts with "You".
        // The open quote must carry so the whole continuation renders in the speech colour.
        bool carry = false;
        var first = SelfChatColorizer.Apply(
            Chat("You tell your listeners \"hello there this is"), "Ollie", NameRgb, SpeechRgb, ref carry);
        Assert.True(carry);   // quote left open at the end of the first line
        Assert.Contains(first.Spans, s => s.Text == "You tell your listeners " && s.Style.ForegroundRgb == NameRgb);
        Assert.Contains(first.Spans, s => s.Text.StartsWith('"') && s.Style.ForegroundRgb == SpeechRgb);

        var second = SelfChatColorizer.Apply(
            Chat("a rather long message\"."), "Ollie", NameRgb, SpeechRgb, ref carry);
        Assert.False(carry);  // quote closes on the second line
        Assert.Contains(second.Spans, s => s.Text == "a rather long message\"" && s.Style.ForegroundRgb == SpeechRgb);
        Assert.Contains(second.Spans, s => s.Text == "." && s.Style.ForegroundRgb == NameRgb);
    }

    [Fact]
    public void CarryQuote_DoesNotLeakToNonChatOrClosedQuote()
    {
        // A fully-closed self line leaves the carry off, so a following chat line is untouched.
        bool carry = false;
        SelfChatColorizer.Apply(Chat("You say \"done\"."), "Ollie", NameRgb, SpeechRgb, ref carry);
        Assert.False(carry);

        var next = Chat("Bob says \"unrelated\".");
        var result = SelfChatColorizer.Apply(next, "Ollie", NameRgb, SpeechRgb, ref carry);
        Assert.Same(next, result);

        // Even with an open carry, a non-chat line is never treated as a continuation.
        carry = true;
        var normal = Normal("some room description");
        Assert.Same(normal, SelfChatColorizer.Apply(normal, "Ollie", NameRgb, SpeechRgb, ref carry));
        Assert.False(carry);
    }

    [Fact]
    public void SpeechColour_SplitsAcrossExistingSpans_PreservingStyle()
    {
        // A tell decorated with an italic phrase: the italic must survive the recolour.
        var spans = new[]
        {
            new StyledSpan("You ", TextStyle.Default),
            new StyledSpan("say", TextStyle.Default with { Italic = true }),
            new StyledSpan(" \"hi\".", TextStyle.Default),
        };
        var line = new StyledLine(spans, isPartial: false, kind: LineKind.Chat);

        var result = SelfChatColorizer.Apply(line, "Ollie", NameRgb, SpeechRgb);
        Assert.Contains(result.Spans, s => s.Text == "say" && s.Style.Italic && s.Style.ForegroundRgb == NameRgb);
        Assert.Contains(result.Spans, s => s.Text == "\"hi\"" && s.Style.ForegroundRgb == SpeechRgb);
    }
}
