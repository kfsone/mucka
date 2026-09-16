using Mucka.Store;

namespace Mucka.Util.Tests;

/// <summary>
/// How a login ended, classified from what MUD2 actually printed.
///
/// <para>Every line quoted here is verbatim from the operator's own wire log - two real deaths (a
/// drowning and a marsh-gas explosion), a real permadeath at the touchstone, and real quits. None is
/// invented to be convenient, because the whole question is which of these the server distinguishes
/// and how.</para>
///
/// <para>The classifier itself lives in <c>MuckaConnection.NotePersonaSessionEnd</c>, which is in the
/// MAUI assembly and unreachable from here. What is under test is the rule it implements, stated as
/// the same two comparisons - so this pins the DISCRIMINATOR against real lines, which is the part
/// that was got wrong.</para>
/// </summary>
public class PersonaSessionEndVocabularyTests
{
    /// <summary>The rule, as MuckaConnection applies it: first writer wins, Cheerio beats the
    /// summary.</summary>
    private static string? Classify(params string[] lines)
    {
        string? note = null;
        foreach (var text in lines)
        {
            if (note is not null)
                break;
            if (text.Contains("Cheerio!", StringComparison.Ordinal))
                note = PersonaSessionEnd.Quit;
            else if (text.Contains("Overall, you ", StringComparison.Ordinal)
                  && text.Contains(" points this game.", StringComparison.Ordinal))
                note = PersonaSessionEnd.Died;
        }
        return note;
    }

    /// <summary>Drowned at sea, returning from the dragon island.</summary>
    [Fact]
    public void AnOrdinaryDeath_HasASummaryAndNoCheerio()
        => Assert.Equal(PersonaSessionEnd.Died, Classify(
            "AN UNSEEN STRONG CURRENT SUDDENLY DRAGS HOLD OF YOUR LITTLE CRAFT AND SUCKS IT "
            + "UNDERWATER. YOU SPLASH ABOUT, BUT EVENTUALLY DROWN. SIGH.",
            "(Persona saved on -55 = 2,321).",
            "Coracle dropped.",
            "Overall, you scored 2,126 points this game."));

    /// <summary>The marsh-gas death on the other host. Same machinery, different cause - and it also
    /// carries the level-change line, which is NOT a death marker.</summary>
    [Fact]
    public void ADeathOnTheOtherHost_ClassifiesTheSame()
        => Assert.Equal(PersonaSessionEnd.Died, Classify(
            "THE VOLATILE MARSH GASES IGNITE FROM YOUR OPEN FLAME, AND IN AN INSTANT YOU ARE A "
            + "CHARRED CORPSE WHICH IS SUCKED UNMERCIFULLY INTO THE DEEP.",
            "You have changed experience level from protector to novice.",
            "(Persona saved on -11 = 189).",
            "Overall, you scored 189 points this game."));

    /// <summary>
    /// A quit, and the reason the summary alone cannot be the signal.
    ///
    /// <para><b>The verb is not the discriminator.</b> "scored" appears on a quit as readily as on a
    /// death - it tracks whether the session netted a gain. That reading was tried and the corpus
    /// disproved it: of every "Overall, you ..." line in the wire log, all but two are paired with a
    /// Cheerio, and those two are the deaths above.</para>
    /// </summary>
    [Theory]
    [InlineData("Overall, you scored 7,342 points this game.")]
    [InlineData("Overall, you lost 5 points this game.")]
    public void AQuit_IsAQuitWhicheverWayTheSessionWent(string summary)
        => Assert.Equal(PersonaSessionEnd.Quit, Classify("Cheerio!", summary));

    /// <summary>A level change says score crossed a threshold, and nothing more. MUD2 stores no
    /// level - it stores score and computes the level from it - so this line accompanies a death, a
    /// flee charge and ordinary play alike. It is a real statement about score, just not about
    /// dying.</summary>
    [Fact]
    public void ALevelChangeAlone_IsNotADeath()
        => Assert.Null(Classify("You have changed experience level from protector to novice."));

    /// <summary>Ordinary play says nothing about an ending.</summary>
    [Theory]
    [InlineData("You hit the rat0 (15-19).")]
    [InlineData("(Persona saved on +38 = 19,214).")]
    [InlineData("The thief sneaks away.")]
    public void OrdinaryLines_LeaveTheNoteUnset(string line)
        => Assert.Null(Classify(line));

    /// <summary>Permadeath is not classified here at all: it has a code of its own (C08+C13, via
    /// MudSession.PersonaWiped) and is taken from that rather than from the "Not updating persona."
    /// line beside it. This asserts the prose alone does NOT produce a note, so the two paths cannot
    /// quietly both fire and race.</summary>
    [Fact]
    public void PermadeathProse_IsNotWhatSetsTheNote()
        => Assert.Null(Classify(
            "A FEELING OF UNBELIEVABLE POWER SURGES THROUGH YOUR BODY. ... AND YOU COLLAPSE AND DIE.",
            "Not updating persona."));
}
