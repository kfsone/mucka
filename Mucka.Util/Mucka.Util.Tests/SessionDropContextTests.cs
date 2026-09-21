using MudSharp.Models;
using Mucka.Commands;

namespace Mucka.Util.Tests;

/// <summary>
/// What the guided-login overlay says about the drop that opened it. The classification itself
/// lives in GameViewModel (MAUI-bound); this covers the presentation rules it feeds.
/// </summary>
public class SessionDropContextTests
{
    private static List<StyledLine> Lines(params string[] text)
        => text.Select(t => new StyledLine(new[] { new StyledSpan(t, TextStyle.Default) })).ToList();

    /// <summary>
    /// A deliberate quit gets its own headline, not the server's last line, and shows no tail.
    ///
    /// <para>The precedence this test used to claim in its name - that a <c>qq</c> inside the reset
    /// finish-up window still classifies as Quit rather than Reset - is decided by
    /// <c>GameViewModel.ClassifyDrop</c>, in the MAUI assembly, which nothing here calls. Asserting
    /// <c>Reason != Reset</c> on a context the test itself constructed with <c>Quit</c> reads like a
    /// check on that rule and is not one.</para>
    /// </summary>
    [Fact]
    public void QuitHasItsOwnHeadline_AndShowsNoTail()
    {
        // Deliberately NOT "Cheerio!" in the tail: the headline is a fixed word for the reason, and
        // a tail line that happened to match it would hide a headline reading the last line back.
        var drop = new SessionDropContext(SessionDropReason.Quit, "Ollie", Lines("The world fades."));

        Assert.Equal("Cheerio!", drop.Headline);
        // The player knows why they left; the server's last words add nothing.
        Assert.False(drop.ShowsTailLines);
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
