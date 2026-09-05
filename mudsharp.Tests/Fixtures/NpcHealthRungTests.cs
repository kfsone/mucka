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
        Assert.Equal(5, Rung("The banshee looks slightly weakened."));
        Assert.Equal(4, Rung("The banshee looks moderately drained."));
        Assert.Equal(3, Rung("The banshee looks seriously drained."));
        Assert.Equal(2, Rung("The banshee looks to be fading rapidly."));
    }

    /// <summary>The banshee's seven words occupy seven distinct rungs, like every other vocabulary.
    /// This is the assertion that would have caught the old table, which had two words at 6, nothing
    /// at 5, an unobserved word at 2 and two words at 1 - so eight of the twenty-five word-changes
    /// across seven recorded fights moved the creature's description without moving the rail.</summary>
    [Fact]
    public void TheBansheesSevenWords_OccupySevenDistinctRungs()
    {
        string[] ladder =
        [
            "strong", "superficially damaged", "slightly weakened", "moderately drained",
            "seriously drained", "to be fading rapidly", "faint",
        ];
        var rungs = ladder.Select(w => Rung($"The banshee looks {w}.")).ToArray();
        Assert.Equal([7, 6, 5, 4, 3, 2, 1], rungs);
    }

    /// <summary>An object's wear is not a creature's health, and the two share the "The X looks ..."
    /// shape exactly. The severity fallback read "close to disintegration" as "close to" and returned
    /// rung 1 - a weapon reporting itself as about to die, on the rail, if it shares a name with the
    /// creature you are fighting. All observed in the corpus off a `ql` on a carried item.</summary>
    [Theory]
    [InlineData("The rolling-pin1 looks close to disintegration.")]
    [InlineData("The coracle looks to be in relatively good condition.")]
    [InlineData("The lamp looks to be in very bad condition.")]
    public void ObjectCondition_IsNotAHealthReading(string line)
        => Assert.False(NpcHealthRungs.TryParse(line, out _, out _, out _), line);

    /// <summary>The other half, and the honest limit of this parser: an object's wear reuses the
    /// creature vocabulary exactly, so "The broadsword looks to be seriously damaged." is
    /// indistinguishable BY PHRASE from a zombie on rung 3. These still parse, and must - rejecting
    /// them would mean rejecting the creature readings that share their words.
    ///
    /// <para>What contains them is the caller: CombatTracker only consults a reading for a name
    /// already in its active set, so an object has to share an engaged creature's name to land.
    /// Pinned so nobody "fixes" TryRung to reject these and silently blinds the rail to three
    /// rungs of every undead vocabulary.</para></summary>
    [Theory]
    [InlineData("The broadsword looks to be seriously damaged.", 3)]
    [InlineData("The well-maintained pick2 looks to be superficially damaged.", 6)]
    public void ObjectWearSharingTheCreatureVocabulary_StillParses_AndIsTheCallersProblem(
        string line, int expected)
    {
        Assert.True(NpcHealthRungs.TryParse(line, out _, out var rung, out _), line);
        Assert.Equal(expected, rung);
    }

    /// <summary>Two entries were deleted as unobserved-and-invented ("critically drained",
    /// "moderately injured" - both zero occurrences corpus-wide). This does NOT guard the deletion:
    /// it passes against the old table too, because the explicit entries carried the same values the
    /// fallback now supplies. That is the point being pinned - both deletions are behavioural
    /// no-ops, so a phrase nobody has seen still degrades to its adverb rather than vanishing.</summary>
    [Theory]
    [InlineData("The banshee looks critically drained.", 2)]
    [InlineData("The rat3 looks moderately injured.", 4)]
    public void DeletedInventedPhrases_StillDegradeToTheirAdverb(string line, int expected)
        => Assert.Equal(expected, Rung(line));

    /// <summary>The severity fallback must agree with the phrase table, or it generalises a value the
    /// table has disproved. Both of these moved in the 2026-09-04 remap and the fallback moved with
    /// them; "slightly weakened" and the banshee's "fading" phrase are the only observed users of
    /// either adverb.</summary>
    [Theory]
    [InlineData("The wraith2 looks slightly dimmed.", 5)]
    [InlineData("The spectre looks to be fading fast.", 2)]
    public void TheSeverityFallback_AgreesWithThePhraseTable(string line, int expected)
        => Assert.Equal(expected, Rung(line));

    /// <summary>The banshee's terminal word, and the only descriptor in the corpus with no severity
    /// adverb - so it missed the phrase table AND the severity fallback, and TryParse returned false.
    /// The cost was invisible: every one of the 27 non-killing player hits in the whole corpus with no
    /// descriptor after it was a banshee. Regression, not a nicety.</summary>
    [Fact]
    public void Faint_TheBansheesTerminalReading_IsRungOneAndNotSilence()
    {
        Assert.True(NpcHealthRungs.TryParse(
            "The banshee looks faint.", out var npc, out var rung, out var phrase));
        Assert.Equal("banshee", npc);
        Assert.Equal(1, rung);
        Assert.Equal("faint", phrase);
    }

    /// <summary>Seen once, on a zombie4 examine rather than in combat, and with no severity word - so
    /// it fell through exactly as "faint" did. Asserted in the run-on form the game actually printed,
    /// inventory tail and all, because that comma branch is the only place it has ever been observed
    /// and a bare "looks full of energy." is a wording nobody has seen.</summary>
    [Fact]
    public void FullOfEnergy_ReadsAsUnhurt()
        => Assert.Equal(7, Rung(
            "The zombie4 looks full of energy, and is holding the following:"));

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
