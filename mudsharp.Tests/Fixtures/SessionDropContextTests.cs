using Mucka.Core.GuidedLogin;
using MudSharp.Models;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// What the guided-login overlay says about the drop that opened it. The classification itself
/// lives in GameViewModel (MAUI-bound); this covers the presentation rules it feeds.
/// </summary>
public class SessionDropContextTests
{
    private static IReadOnlyList<StyledLine> Lines(params string[] text)
        => text.Select(t => new StyledLine(new[] { new StyledSpan(t, TextStyle.Default) })).ToList();

    [Fact]
    public void ResetSaysSoAndNeedsNoTail()
    {
        var drop = new SessionDropContext(SessionDropReason.Reset, "Ollie", Lines("The world dissolves."));
        Assert.Equal("Reset In Progress", drop.Headline);
        // A reset explains itself; the last few lines are only noise on top of that.
        Assert.False(drop.ShowsTailLines);
    }

    [Fact]
    public void PermadeathNamesTheFallenAndShowsTheTail()
    {
        var drop = new SessionDropContext(SessionDropReason.Permadeath, "Ollie", Lines("You are dead."));
        Assert.Equal("Rest In Peace Ollie", drop.Headline);
        Assert.True(drop.ShowsTailLines);
    }

    [Fact]
    public void PermadeathWithNoIdentifiedPersonaStillReads()
    {
        // The setup `score` reply is what names the persona; dying before it lands is unlikely
        // but must not produce "Rest In Peace ".
        var drop = new SessionDropContext(SessionDropReason.Permadeath, null, Lines("You are dead."));
        Assert.Equal("Rest In Peace", drop.Headline);
    }

    [Fact]
    public void UnknownDropShowsTheServersLastWords()
    {
        var drop = new SessionDropContext(SessionDropReason.Unknown, "Ollie", Lines("Goodbye."));
        Assert.Equal("Oops!", drop.Headline);
        Assert.True(drop.ShowsTailLines);
    }

    [Fact]
    public void NoTailCapturedMeansNothingToShow()
    {
        var drop = new SessionDropContext(SessionDropReason.Unknown, "Ollie", Array.Empty<StyledLine>());
        Assert.Equal("Oops!", drop.Headline);
        Assert.False(drop.ShowsTailLines);
    }
}
