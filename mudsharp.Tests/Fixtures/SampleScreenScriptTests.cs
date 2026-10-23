using MudSharp.Models;
using MudSharp.Protocol;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// <see cref="SampleScreenScript"/>, the Text size page's example screen. The lines go through the
/// real parser, so what is asserted here is what the example pane draws: the screen the operator
/// specified, with each chat line in its channel's colour part. The parser starts each message on
/// its own row rather than after the standing prompt, so the operator's leading '*'s are not here.
/// </summary>
public class SampleScreenScriptTests
{
    private static List<StyledLine> Complete() => SampleScreenScript.Lines().Where(l => !l.IsPartial).ToList();

    [Fact]
    public void Screen_ReadsAsTheOperatorSpecified()
        => Assert.Equal(
            [
                "Mucky tearoom.",
                "You are in a small, warm tearoom that smells of fresh scones. A door leads out to",
                "the north, and a narrow staircase climbs to the floor above. ",
                "Ollie the amateur is here holding a cup of tea.",
                "Drizzle the wobbly mage is out with other humans.",
                "Lady Polly the witch asks \"Where did I leave my dragon?\".",
                "A male voice in the distance shouts \"I found the ram :(\".",
                "Drizzle the wobbly mage tells you \"He could have bought cheap ram from me\".",
                "Mucka acts like it's waiting for your selection.",
            ],
            Complete().Select(l => l.PlainText));

    [Fact]
    public void ChatLines_AreChat_EachInItsOwnChannel()
    {
        var chat = Complete().Where(l => l.Kind == LineKind.Chat).ToList();
        Assert.Equal(4, chat.Count);
        Assert.Contains(chat[0].Spans, s => s.Style.Speech == SpeechPart.Say);
        Assert.Contains(chat[1].Spans, s => s.Style.Speech == SpeechPart.Shout);
        Assert.Contains(chat[2].Spans, s => s.Style.Speech == SpeechPart.Tell);
        Assert.All(chat, l => Assert.All(l.Spans, s => Assert.False(s.Style.Own)));
    }

    [Fact]
    public void Screen_EndsOnItsLastLine_WithNoPromptBelow()
        => Assert.Equal("Mucka acts like it's waiting for your selection.", SampleScreenScript.Lines()[^1].PlainText);
}
