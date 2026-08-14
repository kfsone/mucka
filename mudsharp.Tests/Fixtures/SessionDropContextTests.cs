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

    /// <summary>
    /// A rebase dropped quit detection on the false premise that main's classifier already covered it.
    /// It does not: SessionDropReason had no Quit at all, and IsResetDrop is a pure proximity test, so
    /// a qq inside the 95-120s finish-up window classified as Reset - which auto-relogs the player
    /// straight back into the persona they just deliberately left, without even showing the picker.
    /// </summary>
    [Fact]
    public void QuitIsItsOwnReason_SoItCanOutrankTheResetTimingGuess()
    {
        var drop = new SessionDropContext(SessionDropReason.Quit, "Ollie", Lines("Cheerio!"));

        Assert.Equal("Cheerio!", drop.Headline);
        // The player knows why they left; the server's last words add nothing.
        Assert.False(drop.ShowsTailLines);
        // And it must not read as a reset, which is the value auto-relog is gated on.
        Assert.NotEqual(SessionDropReason.Reset, drop.Reason);
    }

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
