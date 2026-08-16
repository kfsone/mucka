using MudSharp.Combat;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// The health-descriptor ladder - MUD2's only report of how hurt a creature is.
///
/// <para>The ordering assertions here are transcribed from real fights in the capture corpus, not from
/// intuition, and they exist because intuition got it wrong once already: a hand-written draft placed
/// "covered in wounds" below "seriously injured", where the corpus (counted within reducer-segmented
/// fights, so a reused instance name cannot splice two fights into one sequence) shows 4 transitions
/// from "covered in wounds" to "seriously injured" and none the other way. Two rungs of error, in the
/// direction that reads a dying creature as healthier than it is - so these sequences are the
/// regression that keeps the scale honest.</para>
///
/// <para>No published source covers this. The MUD2 strategy guide gives damage formulas and per-
/// creature stamina pools and says nothing at all about the wound descriptions - see
/// tools/combat/MUD2-PUBLISHED-MECHANICS.md.</para>
/// </summary>
public sealed class NpcHealthRungTests
{
    private static int Rung(string line)
    {
        Assert.True(NpcHealthRungs.TryParse(line, out _, out var rung, out _), line);
        return rung;
    }

    [Fact]
    public void TryParse_ReadsNameAndPhraseVerbatim()
    {
        Assert.True(NpcHealthRungs.TryParse(
            "The large rat0 looks covered in wounds.", out var npc, out var rung, out var phrase));

        Assert.Equal("large rat0", npc);
        Assert.Equal("covered in wounds", phrase);
        Assert.Equal(4, rung);
    }

    /// <summary>The living vocabulary, in the order the corpus establishes. One ram's fight supplies
    /// all of it except the "minor injuries" rung, which comes from the thief and rat fights either
    /// side of it (superficially -> minor injuries -> covered in wounds, both directions attested).</summary>
    [Fact]
    public void LivingLadder_DescendsInTheOrderTheCorpusShows()
    {
        int[] sequence =
        [
            Rung("The ram looks fit."),
            Rung("The ram looks superficially injured."),
            Rung("The ram looks to have minor injuries."),
            Rung("The ram looks covered in wounds."),
            Rung("The ram looks seriously injured."),
            Rung("The ram looks critically injured."),
            Rung("The ram looks close to death."),
        ];

        Assert.Equal([7, 6, 5, 4, 3, 2, 1], sequence);
    }

    /// <summary>The undead vocabulary lines up rung for rung with the living one, substituting
    /// "moderately damaged" for the living-only "covered in wounds" at rung 4 - which is why a single
    /// seven-rung scale can serve both.</summary>
    [Fact]
    public void UndeadLadder_MapsOntoTheSameSevenRungs()
    {
        int[] sequence =
        [
            Rung("The zombie6 looks strong."),
            Rung("The zombie6 looks superficially damaged."),
            Rung("The zombie6 looks to have minor damage."),
            Rung("The zombie6 looks moderately damaged."),
            Rung("The zombie6 looks seriously damaged."),
            Rung("The zombie6 looks critically damaged."),
            Rung("The zombie6 looks close to expiry."),
        ];

        Assert.Equal([7, 6, 5, 4, 3, 2, 1], sequence);
    }

    /// <summary>A banshee is "drained" rather than injured or damaged - a third vocabulary, observed
    /// mixing "superficially damaged" with "moderately/seriously drained" in one fight.</summary>
    [Fact]
    public void DrainedVocabulary_LandsOnTheSameScale()
    {
        Assert.Equal(6, Rung("The banshee looks slightly weakened."));
        Assert.Equal(4, Rung("The banshee looks moderately drained."));
        Assert.Equal(3, Rung("The banshee looks seriously drained."));
        Assert.Equal(1, Rung("The banshee looks to be fading rapidly."));
    }

    /// <summary>A vocabulary nobody has fought yet still lands correctly off its severity word alone.
    /// Without this, an unseen creature family would read as "no information" for every rung it has -
    /// silently, and for as long as it took someone to notice.</summary>
    [Fact]
    public void UnknownVocabulary_FallsBackToTheSeverityWord()
    {
        Assert.Equal(2, Rung("The golem4 looks critically corroded."));
        Assert.Equal(4, Rung("The wraith looks moderately dissipated."));
        Assert.Equal(6, Rung("The slime looks superficially scorched."));
    }

    /// <summary>Lines that look like a health reading and are not. Object condition and aggro poses
    /// share the "The X looks ..." shape exactly, and matching either would put a phantom health bar
    /// on something the player is not fighting.</summary>
    [Theory]
    [InlineData("The coracle looks to be in relatively good condition.")]
    [InlineData("The rat looks at you furiously.")]
    [InlineData("The thief looks approaching you furiously.")]
    [InlineData("The door is locked shut.")]
    [InlineData("The passage is open.")]
    [InlineData("You hit the zombie6 (20-29).")]
    [InlineData("The zombie6 hits you (86/105).")]
    [InlineData("The zombie6 has started to use the fork1 to fight!")]
    public void TryParse_RejectsEverythingThatIsNotAHealthReading(string line)
        => Assert.False(NpcHealthRungs.TryParse(line, out _, out _, out _), line);

    [Theory]
    [InlineData("to have minor injuries", "minor injuries")]
    [InlineData("to have minor damage", "minor damage")]
    [InlineData("to be fading rapidly", "fading rapidly")]
    [InlineData("covered in wounds", "covered in wounds")]
    [InlineData("critically injured", "critically injured")]
    public void Label_StripsTheGamesGrammaticalFiller(string phrase, string expected)
        => Assert.Equal(expected, NpcHealthRungs.Label(phrase));

    /// <summary>Rung count and pip count are the same number by design, not by coincidence - each of
    /// the game's vocabularies has exactly seven words, which is why the rail draws seven pips.</summary>
    [Fact]
    public void Rungs_MatchesTheVocabularySize()
    {
        Assert.Equal(7, NpcHealthRungs.Rungs);
        Assert.Equal(NpcHealthRungs.Rungs, NpcHealthRungs.Unhurt);
    }
}
