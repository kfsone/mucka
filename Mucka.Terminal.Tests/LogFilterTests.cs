using MudSharp.Models;

namespace Mucka.Terminal.Tests;

public class LogFilterTests
{
    private static StyledLine Line(string text, LineKind kind = LineKind.Normal, bool partial = false)
        => new([new StyledSpan(text, TextStyle.Default)], isPartial: partial, kind: kind);

    // A prompt, then a say finishing it; a room line; a fight starting and ending; a prompt and an echo.
    private static readonly StyledLine[] Fed =
    [
        Line("*", partial: true),
        Line("Crispybob says \"hi\".", LineKind.Chat),
        Line("Elizabethan tearoom."),
        Line("You attack the rat.", LineKind.FightStart),
        Line("The rat has fled.", LineKind.FightEnd),
        Line("*", partial: true),
        Line("look"),
    ];

    private static string[] Screen(params FilterRule[] rules)
        => TerminalBuffer.ScreenOf(LogFilter.Apply(Fed, rules)).Select(l => l.PlainText).ToArray();

    [Fact]
    public void NoFilters_IsTheWholeScreen_PromptsMergedAsTheLogHasThem()
    {
        Assert.Equal(new[] { "*Crispybob says \"hi\".", "Elizabethan tearoom.", "You attack the rat.",
            "The rat has fled.", "*look" }, Screen());
    }

    [Fact]
    public void OnlyChat_IsWhatTheGamesChatFilterShows_WithoutThePromptBeforeIt()
    {
        Assert.Equal(new[] { "Crispybob says \"hi\"." }, Screen(new FilterRule(LineCategory.Chat, FilterAction.Only)));
        Assert.All(Fed, l => Assert.Equal(PaneHistory.IsChat(l), LogFilter.Is(LineCategory.Chat, l)));
    }

    [Fact]
    public void TwoOnlys_ShowEither()
    {
        Assert.Equal(new[] { "Crispybob says \"hi\".", "You attack the rat.", "The rat has fled." },
            Screen(new(LineCategory.Chat, FilterAction.Only), new(LineCategory.FightStartsAndEnds, FilterAction.Only)));
    }

    [Fact]
    public void Hide_TakesItsCategoryAway_AndAPromptGoesOnToTheNextLineShown()
    {
        Assert.Equal(new[] { "*Elizabethan tearoom.", "You attack the rat.", "The rat has fled.", "*look" },
            Screen(new FilterRule(LineCategory.Chat, FilterAction.Hide)));
    }

    [Fact]
    public void Show_PutsItsCategoryBack_WhateverTheOrder()
    {
        FilterRule only = new(LineCategory.Chat, FilterAction.Only), show = new(LineCategory.FightStartsAndEnds, FilterAction.Show),
            hide = new(LineCategory.FightStartsAndEnds, FilterAction.Hide);
        var expected = new[] { "Crispybob says \"hi\".", "You attack the rat.", "The rat has fled." };
        Assert.Equal(expected, Screen(only, show));
        Assert.Equal(expected, Screen(show, only));
        Assert.Equal(expected, Screen(hide, show, only));
    }
}
