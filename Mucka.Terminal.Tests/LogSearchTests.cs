using MudSharp.Models;

namespace Mucka.Terminal.Tests;

public class LogSearchTests
{
    private static StyledLine Line(string text) => new([new StyledSpan(text, TextStyle.Default)], isPartial: false);

    private static readonly StyledLine[] Log =
    [
        Line("The rat bites you."),       // 0
        Line("nothing here"),             // 1
        TerminalBuffer.ClearRule,         // 2
        Line("A RAT, and another rat."),  // 3
        Line("the end"),                  // 4
    ];

    [Fact]
    public void Forward_WalksEachMatchInTurn_IgnoringCase_AndCarriesOnFromTheTop()
    {
        TextMatch? at = null;
        var found = new List<TextMatch>();
        for (var i = 0; i < 4; i++)
        {
            at = LogSearch.Find(Log, "rat", at, 0, backward: false);
            Assert.NotNull(at);
            found.Add(at.Value);
        }

        Assert.Equal(new[] { new TextMatch(0, 4, 3), new(3, 2, 3), new(3, 19, 3), new(0, 4, 3) }, found);
    }

    [Fact]
    public void Backward_WalksTheOtherWay_AndCarriesOnFromTheBottom()
    {
        TextMatch? at = new TextMatch(3, 19, 3);
        var found = new List<TextMatch>();
        for (var i = 0; i < 3; i++)
        {
            at = LogSearch.Find(Log, "rat", at, 0, backward: true);
            Assert.NotNull(at);
            found.Add(at.Value);
        }

        Assert.Equal(new[] { new TextMatch(3, 2, 3), new(0, 4, 3), new(3, 19, 3) }, found);
    }

    [Fact]
    public void WithNoMatchYet_ItStartsOnTheLineGiven_ThatLineIncluded()
    {
        Assert.Equal(new TextMatch(3, 2, 3), LogSearch.Find(Log, "rat", null, 3, backward: false));
        Assert.Equal(new TextMatch(3, 19, 3), LogSearch.Find(Log, "rat", null, 3, backward: true));
        Assert.Equal(new TextMatch(0, 4, 3), LogSearch.Find(Log, "rat", null, 1, backward: true));
    }

    [Fact]
    public void ALoneMatch_IsFoundAgain_AndTextThatIsNowhere_IsNull()
    {
        var end = new TextMatch(4, 4, 3);
        Assert.Equal(end, LogSearch.Find(Log, "end", end, 0, backward: false));
        Assert.Equal(end, LogSearch.Find(Log, "end", end, 0, backward: true));
        Assert.Null(LogSearch.Find(Log, "wolf", null, 0, backward: false));
        Assert.Null(LogSearch.Find(Log, "", null, 0, backward: false));
        Assert.Null(LogSearch.Find([], "rat", null, 0, backward: false));
    }

    [Fact]
    public void AClearScreenRule_HoldsNoMatch()
    {
        Assert.Null(LogSearch.Find(Log, "\f", null, 0, backward: false));
        Assert.Equal((0, 0), LogSearch.Place(Log, "\f", null));
    }

    [Fact]
    public void Place_CountsEveryMatch_AndSaysWhichOneThisIs()
    {
        Assert.Equal((2, 3), LogSearch.Place(Log, "RAT", new TextMatch(3, 2, 3)));
        Assert.Equal((0, 3), LogSearch.Place(Log, "rat", null));
        Assert.Equal((0, 0), LogSearch.Place(Log, "wolf", null));
    }
}
