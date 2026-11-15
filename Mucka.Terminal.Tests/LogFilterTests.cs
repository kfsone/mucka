using MudSharp.Models;

namespace Mucka.Terminal.Tests;

public class LogFilterTests
{
    private static StyledLine Line(string text, LineKind kind = LineKind.Normal, bool partial = false,
        params C1Code[] codes)
        => new([new StyledSpan(text, TextStyle.Default)], isPartial: partial, kind: kind, codes: codes);

    private static StyledLine Said(string speaker, string words, SpeechPart part, bool continues = false)
        => new([new StyledSpan(speaker, TextStyle.Default with { Speech = SpeechPart.Speaker, Channel = part }),
            new StyledSpan(words, TextStyle.Default with { Speech = part, Channel = part })],
            kind: LineKind.Chat, continuesChat: continues);

    private static readonly C1Code RoomName = new(2, 1), RoomLong = new(2, 2), Looking = new(12, 0);

    // A prompt then a say finishing it; a room; a glimpse of the next room; a fight; a prompt and an echo.
    private static readonly StyledLine[] Fed =
    [
        Line("*", partial: true),
        Said("Crispybob says ", "\"hi\".", SpeechPart.Say),
        Line("Stony beach.", codes: RoomName),
        Line("This is a beach covered in stones.", codes: RoomLong),
        Line("Looking westward, you see a place known as \"railway track\".", codes: [Looking, RoomName]),
        Line("You hit the rat21 (20-29).", codes: new C1Code(8, 1)),
        Line("The rat21 misses you.", codes: new C1Code(8, 4)),
        Line("You have killed the rat21.", codes: new C1Code(8, 8)),
        Line("*", partial: true),
        Line("look"),
    ];

    private static string[] Screen(params FilterRule[] rules)
        => TerminalBuffer.ScreenOf(LogFilter.Apply(Fed, rules)).Select(l => l.PlainText).ToArray();

    [Fact]
    public void NoRules_IsTheWholeScreen_PromptsMergedAsTheLogHasThem()
    {
        Assert.Equal(10 - 2, Screen().Length);
        Assert.Equal("*Crispybob says \"hi\".", Screen()[0]);
    }

    [Fact]
    public void AShowingChatRule_IsWhatTheGamesChatFilterShows_WithoutThePromptBeforeIt()
    {
        Assert.Equal(new[] { "Crispybob says \"hi\"." }, Screen(new FilterRule(RuleKind.Chat)));
        Assert.All(Fed, l => Assert.Equal(PaneHistory.IsChat(l), LogFilter.Matches(new FilterRule(RuleKind.Chat), l)));
    }

    [Fact]
    public void AHidingRule_TakesWhatItMatchesAway_AndAPromptGoesOnToTheNextLineShown()
    {
        var shown = Screen(new FilterRule(RuleKind.RoomText, LogFilter.DefaultRoomSub, Show: false));
        Assert.Equal(new[] { "*Crispybob says \"hi\".", "You hit the rat21 (20-29).", "The rat21 misses you.",
            "You have killed the rat21.", "*look" }, shown);
    }

    [Fact]
    public void ShowingRules_ShowEither_AndAHidingRuleWinsOverThem()
    {
        FilterRule chat = new(RuleKind.Chat), combat = new(RuleKind.Combat), noKills = new(RuleKind.Kills, Show: false);
        Assert.Equal(new[] { "Crispybob says \"hi\".", "You hit the rat21 (20-29).", "The rat21 misses you.",
            "You have killed the rat21." }, Screen(chat, combat));
        Assert.Equal(new[] { "Crispybob says \"hi\".", "You hit the rat21 (20-29).", "The rat21 misses you." },
            Screen(noKills, chat, combat));
    }

    [Theory]
    [InlineData(RoomPart.Names, new[] { "Stony beach." })]
    [InlineData(RoomPart.NamesAndDescriptions, new[] { "Stony beach.", "This is a beach covered in stones." })]
    [InlineData(RoomPart.Descriptions, new[] { "This is a beach covered in stones." })]
    [InlineData(RoomPart.All, new[] { "Stony beach.", "This is a beach covered in stones.",
        "Looking westward, you see a place known as \"railway track\"." })]
    public void RoomText_PicksTheRoomPartsBySubCode(RoomPart part, string[] expected)
        => Assert.Equal(expected, Screen(new FilterRule(RuleKind.RoomText, (int)part)));

    [Fact]
    public void CombatSides_AndKills_ReadTheCodes()
    {
        Assert.Equal(new[] { "You hit the rat21 (20-29)." }, Screen(new FilterRule(RuleKind.Combat, 1)));
        Assert.Equal(new[] { "The rat21 misses you." }, Screen(new FilterRule(RuleKind.Combat, 2)));
        Assert.Equal(new[] { "You have killed the rat21." }, Screen(new FilterRule(RuleKind.Kills, 1)));
        Assert.Empty(Screen(new FilterRule(RuleKind.Kills, 2)));
        Assert.True(LogFilter.Matches(new FilterRule(RuleKind.Flees), Line("The rat has fled.", codes: new C1Code(8, 11))));
        Assert.True(LogFilter.Matches(new FilterRule(RuleKind.Combat, 2), Line("A bee stings you.", codes: new C1Code(7, 2))));
    }

    [Fact]
    public void AName_MatchesAsAWord_ACreaturesNumberAllowed()
    {
        Assert.Equal(3, LogFilter.Count(Fed, new FilterRule(RuleKind.Combat, Pattern: "rat")));
        Assert.Equal(0, LogFilter.Count(Fed, new FilterRule(RuleKind.Combat, Pattern: "ra")));
        Assert.False(LogFilter.Matches(new FilterRule(RuleKind.Combat, Pattern: "rat"),
            Line("The rattlesnake bites you.", codes: new C1Code(8, 3))));
    }

    [Fact]
    public void AChatPattern_IsTheSpeaker_NotTheWords_AndAWrappedRowFollowsItsMessage()
    {
        StyledLine[] lines =
        [
            Said("Gwen tells you ", "\"Crispybob is late", SpeechPart.Tell),
            new([new StyledSpan("and so am I\".", TextStyle.Default with { Speech = SpeechPart.Tell, Channel = SpeechPart.Tell })],
                kind: LineKind.Chat, continuesChat: true),
            Said("Crispybob shouts ", "\"HELLO\"", SpeechPart.Shout),
        ];
        var fromGwen = new FilterRule(RuleKind.Chat, 3, "gwen");
        Assert.Equal(2, LogFilter.Count(lines, fromGwen));
        Assert.Equal(1, LogFilter.Count(lines, new FilterRule(RuleKind.Chat, Pattern: "Crispybob")));
        Assert.Equal(1, LogFilter.Count(lines, new FilterRule(RuleKind.Chat, 2)));
    }

    [Fact]
    public void APrompt_MatchesNoRule()
    {
        var prompt = Line("*", partial: true, codes: new C1Code(8, 1));
        Assert.All(Enum.GetValues<RuleKind>(), k => Assert.False(LogFilter.Matches(new FilterRule(k), prompt)));
    }

    [Fact]
    public void Text_IsAnyLineContainingThePattern()
        => Assert.Equal(new[] { "Stony beach.", "This is a beach covered in stones." },
            Screen(new FilterRule(RuleKind.Text, Pattern: "BEACH")));
}
