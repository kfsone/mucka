using Mucka.Combat;
using Mucka.Commands;
using Mucka.Store;

namespace Mucka.Util.Tests;

/// <summary>
/// How a login ended, as decided by the one classifier the live path, the guided-login overlay and
/// the wire-log backfill all share.
///
/// <para>Every line quoted is verbatim from the operator's wire log - two real deaths, a real
/// permadeath, real quits, and a real in-game chat line containing the farewell word. None is
/// invented to be convenient, because the question under test is which of these MUD2 distinguishes.
/// This drives <see cref="SessionEndWatcher"/> itself; an earlier version of these tests restated
/// the rule in a local helper and so could not fail on the bug it was meant to catch.</para>
/// </summary>
public sealed class SessionEndWatcherTests
{
    private static SessionDropReason Classify(params string[] lines)
    {
        var watcher = new SessionEndWatcher();
        watcher.Begin();
        foreach (var line in lines)
            watcher.NoteLine(line);
        return watcher.Reason;
    }

    /// <summary>Drowned at sea returning from the dragon island.</summary>
    [Fact]
    public void AnOrdinaryDeath_IsASummaryWithNoFarewell()
        => Assert.Equal(SessionDropReason.Died, Classify(
            "AN UNSEEN STRONG CURRENT SUDDENLY DRAGS HOLD OF YOUR LITTLE CRAFT AND SUCKS IT "
            + "UNDERWATER. YOU SPLASH ABOUT, BUT EVENTUALLY DROWN. SIGH.",
            "(Persona saved on -55 = 2,321).",
            "Coracle dropped.",
            "Overall, you scored 2,126 points this game."));

    /// <summary>The marsh-gas death on the other host - same machinery, different cause.</summary>
    [Fact]
    public void ADeathOnTheOtherHost_ClassifiesTheSame()
        => Assert.Equal(SessionDropReason.Died, Classify(
            "THE VOLATILE MARSH GASES IGNITE FROM YOUR OPEN FLAME, AND IN AN INSTANT YOU ARE A "
            + "CHARRED CORPSE WHICH IS SUCKED UNMERCIFULLY INTO THE DEEP.",
            "You have changed experience level from protector to novice.",
            "(Persona saved on -11 = 189).",
            "Overall, you scored 189 points this game."));

    /// <summary>The verb is not the discriminator: "scored" appears on a quit as readily as on a
    /// death, because it tracks whether the session netted a gain. Keying on it was tried and the
    /// wire corpus disproved it.</summary>
    [Theory]
    [InlineData("Overall, you scored 7,342 points this game.")]
    [InlineData("Overall, you lost 5 points this game.")]
    public void AQuit_IsAQuitWhicheverWayTheSessionWent(string summary)
        => Assert.Equal(SessionDropReason.Quit, Classify("Cheerio!", summary));

    /// <summary>
    /// THE one that matters: another player saying the farewell word must not end your login.
    ///
    /// <para>The corpus already holds <c>Alexander the necromancer says "Cheerio".</c> - it missed
    /// tripping a substring match only because he left off the exclamation mark. "Cheerio!" is the
    /// game's own farewell, so a player typing it exactly is when-not-if, and under first-writer-wins
    /// one such line would permanently record a death or a reset as a quit.</para>
    /// </summary>
    [Theory]
    [InlineData("Alexander the necromancer says \"Cheerio\".")]
    [InlineData("Alexander the necromancer says \"Cheerio!\".")]
    [InlineData("Ollie says \"Cheerio!\"")]
    [InlineData("Ollie tells you \"Cheerio! see you tomorrow\"")]
    [InlineData("You say \"Cheerio!\"")]
    public void AnotherPlayerSayingTheFarewell_IsNotAQuit(string chatter)
        => Assert.Equal(SessionDropReason.Unknown, Classify(chatter));

    /// <summary>And the same for the summary: it is matched as a whole line, so it cannot be quoted
    /// into existence either.</summary>
    [Theory]
    [InlineData("Ollie says \"Overall, you scored 200 points this game.\"")]
    [InlineData("A sign reads: Overall, you scored 200 points this game. Well done.")]
    public void AQuotedSummary_IsNotADeath(string chatter)
        => Assert.Equal(SessionDropReason.Unknown, Classify(chatter));

    /// <summary>A level change says score crossed a threshold and nothing more. MUD2 stores no level
    /// - it stores score and computes the level - so this accompanies a death, a flee charge and
    /// ordinary play alike.</summary>
    [Theory]
    [InlineData("You have changed experience level from protector to novice.")]
    [InlineData("You hit the rat0 (15-19).")]
    [InlineData("(Persona saved on +38 = 19,214).")]
    public void OrdinaryLines_DecideNothing(string line)
        => Assert.Equal(SessionDropReason.Unknown, Classify(line));

    /// <summary>A code outranks prose and arrives first. The permadeath narrative and the
    /// "Not updating persona." line beside it are not what decides this - C08+C13 is.</summary>
    [Fact]
    public void PermadeathComesFromTheCode_NotTheProse()
    {
        var watcher = new SessionEndWatcher();
        watcher.Begin();
        watcher.NoteLine("A FEELING OF UNBELIEVABLE POWER SURGES THROUGH YOUR BODY. ... YOU COLLAPSE AND DIE.");
        watcher.NoteLine("Not updating persona.");
        Assert.Equal(SessionDropReason.Unknown, watcher.Reason);

        watcher.NotePersonaWiped();
        Assert.Equal(SessionDropReason.Permadeath, watcher.Reason);
    }

    /// <summary>A reset takes the world down and logs everyone out, so the exit belongs to it even
    /// though the farewell-less summary that follows looks like a death.</summary>
    [Fact]
    public void AResetOutranksTheSummaryThatFollowsIt()
    {
        var watcher = new SessionEndWatcher();
        watcher.Begin();
        watcher.NoteWorldResetLanded();
        watcher.NoteLine("Overall, you scored 1,263 points this game.");
        Assert.Equal(SessionDropReason.Reset, watcher.Reason);
    }

    /// <summary>Begin() starts a fresh login: the previous one's ending must not leak into it, or
    /// every session after a quit would read as a quit.</summary>
    [Fact]
    public void BeginClearsThePreviousLogin()
    {
        var watcher = new SessionEndWatcher();
        watcher.Begin();
        watcher.NoteLine("Cheerio!");
        Assert.Equal(SessionDropReason.Quit, watcher.Reason);

        watcher.Begin();
        Assert.Equal(SessionDropReason.Unknown, watcher.Reason);
    }

    /// <summary>The stored vocabulary is derived from the one classification, so the column and the
    /// overlay can never name the same event differently.</summary>
    [Theory]
    [InlineData(SessionDropReason.Reset, PersonaSessionEnd.Reset)]
    [InlineData(SessionDropReason.Quit, PersonaSessionEnd.Quit)]
    [InlineData(SessionDropReason.Died, PersonaSessionEnd.Died)]
    [InlineData(SessionDropReason.Permadeath, PersonaSessionEnd.Permadeath)]
    [InlineData(SessionDropReason.Unknown, null)]
    public void EveryReasonMapsToItsStoredWord(SessionDropReason reason, string? expected)
        => Assert.Equal(expected, PersonaSessionEndNote.For(reason));
}
