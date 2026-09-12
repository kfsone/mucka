namespace MudSharp.Tests.Fixtures;

/// <summary>
/// Tests for GameLineAnalyzer stat extraction, exercised end-to-end through
/// MudStreamParser.  Each test feeds a text line and verifies the StatsUpdated event.
/// Mirrors Clio's scan_game_line() patterns.
/// </summary>
public class GameLineAnalyzerTests
{
    [Fact]
    public void StaminaLine_ExtractsCurrentAndMax()
    {
        // Clio: "stamina:        N      max:    M"
        var h = new ParserHarness();
        h.Feed("stamina:  81      max:  81\n");
        Assert.Single(h.Stats);
        Assert.Equal(81, h.Stats[0].Stamina);
        Assert.Equal(81, h.Stats[0].MaxStamina);
    }

    [Fact]
    public void YourStaminaIs_ExtractsStamina()
    {
        // Wake-up / rest line: "Your stamina is N."
        var h = new ParserHarness();
        h.Feed("Your stamina is 42.\n");
        Assert.Single(h.Stats);
        Assert.Equal(42, h.Stats[0].Stamina);
    }

    [Fact]
    public void CompactStamina_ExtractsFromLineStart()
    {
        // Clio: buf[0]=='(' then (N/M) -- prompt-style compact stamina
        var h = new ParserHarness();
        h.Feed("(42/100) You are standing in a clearing.\n");
        Assert.Single(h.Stats);
        Assert.Equal(42,  h.Stats[0].Stamina);
        Assert.Equal(100, h.Stats[0].MaxStamina);
    }

    [Fact]
    public void OverflowNumbers_DoNotThrowOrEmit()
    {
        // Any player can put "(N/M)" with >int.MaxValue digits in a say/shout; an
        // int.Parse OverflowException here propagated out of Feed() and dropped the
        // connection. TryParse must swallow it without emitting stats.
        var h = new ParserHarness();
        h.Feed(0x9D, 0x9C, 0xFF, 0xFF);   // game mode (combat regex runs on all lines)
        h.Feed("Ollie says \"(99999999999999999999/9)\".\n");
        h.Feed("stamina:  99999999999999999999      max:  81\n");
        h.Feed("(99999999999999999999/99999999999999999999) ouch\n");
        Assert.Empty(h.Stats);
    }

    [Fact]
    public void StrengthLine_ExtractsStrength()
    {
        // "strength:       N" -- uses raw value when no effective strength present
        var h = new ParserHarness();
        h.Feed("strength:       94\n");
        Assert.Single(h.Stats);
        Assert.Equal(94, h.Stats[0].RawStrength);
        Assert.Equal(94, h.Stats[0].Strength);
    }

    [Fact]
    public void DexterityLine_ExtractsDexterity()
    {
        // "dexterity:      N" -- uses raw value when no effective dexterity present
        var h = new ParserHarness();
        h.Feed("dexterity:      95\n");
        Assert.Single(h.Stats);
        Assert.Equal(95, h.Stats[0].RawDexterity);
        Assert.Equal(95, h.Stats[0].Dexterity);
    }

    [Fact]
    public void StrengthLine_WithEffectiveStrength_ExtractsRawAndEffective()
    {
        var h = new ParserHarness();
        h.Feed("strength:       100     effective strength:    88\n");
        Assert.Single(h.Stats);
        Assert.Equal(100, h.Stats[0].RawStrength);
        Assert.Equal(88, h.Stats[0].Strength);
    }

    [Fact]
    public void DexterityLine_WithEffectiveDexterity_ExtractsRawAndEffective()
    {
        var h = new ParserHarness();
        h.Feed("dexterity:      100     effective dexterity:    99\n");
        Assert.Single(h.Stats);
        Assert.Equal(100, h.Stats[0].RawDexterity);
        Assert.Equal(99, h.Stats[0].Dexterity);
    }

    /// <summary>Carried weight is deliberately not captured at all - see GameLineAnalyzer's
    /// score-sheet branch. This is here so a future "the sheet parser is incomplete" tidy-up trips a
    /// red test instead of quietly reintroducing an unwanted variable.</summary>
    [Theory]
    [InlineData("weight carried: 750g    max:    100kg\n")]
    [InlineData("weight carried: 2kg    max:    750g\n")]
    [InlineData("weight carried: nothing max:    100kg\n")]
    public void WeightCarriedLine_IsIgnoredEntirely(string line)
    {
        var h = new ParserHarness();
        h.Feed(line);
        Assert.Empty(h.Stats);
    }

    [Fact]
    public void ObjectsCarriedLine_ParsesCounts()
    {
        var h = new ParserHarness();
        h.Feed("objects carried:        1       max:    12\n");
        Assert.Single(h.Stats);
        Assert.Equal(1, h.Stats[0].ObjectsCarried);
        Assert.Equal(12, h.Stats[0].MaxObjectsCarried);
    }

    [Fact]
    public void LevelLine_ParsesLevel()
    {
        var h = new ParserHarness();
        h.Feed("level:  7       champion\n");
        Assert.Single(h.Stats);
        Assert.Equal(7, h.Stats[0].Level);
    }

    [Fact]
    public void GamesPlayedLine_ParsesCount()
    {
        var h = new ParserHarness();
        h.Feed("games played:   18\n");
        Assert.Single(h.Stats);
        Assert.Equal(18, h.Stats[0].GamesPlayed);
    }

    [Fact]
    public void ScoreLine_ExtractsScore()
    {
        // "score:  N,NNN points ..." -- strips commas
        var h = new ParserHarness();
        h.Feed("score:  1,785 points    this game: 500\n");
        Assert.Single(h.Stats);
        Assert.Equal(1785, h.Stats[0].Score);
    }

    [Fact]
    public void PersonaSaved_SetsFlag_AndExtractsScore()
    {
        // "(Persona saved on [+N = ]M,NNN)." -- sets PersonaSaved and extracts score
        var h = new ParserHarness();
        h.Feed("(Persona saved on +500 = 2,500).\n");
        Assert.Single(h.Stats);
        Assert.True(h.Stats[0].PersonaSaved);
        Assert.Equal(2500, h.Stats[0].Score);
    }

    /// <summary>
    /// The signed delta. It is the only place MUD2 states what an event was WORTH: a kill's award and
    /// a flee's cost arrive here and nowhere else. All three forms, with the counts they have across
    /// the 877 occurrences in the raw recordings.
    /// </summary>
    [Theory]
    [InlineData("(Persona saved on +38 = 19,214).", 38, 19214)]      // a gain; 802 of them
    [InlineData("(Persona saved on -872 = 18,382).", -872, 18382)]   // a flee cost; 15 of them
    [InlineData("(Persona saved on +200 = 200).", 200, 200)]         // no comma grouping either side
    [InlineData("(Persona saved on -102 = 98).", -102, 98)]
    public void PersonaSaved_CarriesTheSignedDelta(string line, int expectedDelta, int expectedTotal)
    {
        var h = new ParserHarness();
        h.Feed(line + "\n");

        var save = Assert.Single(h.ScoreSaves);
        Assert.Equal(expectedDelta, save.Delta);
        Assert.Equal(expectedTotal, save.Total);
        Assert.Equal(line, save.RawText);

        // The stat path still sees exactly what it always did.
        Assert.Equal(expectedTotal, h.Stats[0].Score);
        Assert.True(h.Stats[0].PersonaSaved);
    }

    /// <summary>
    /// The 60 total-only lines. Delta must be NULL and never 0: these are the world reset and the
    /// shell-exit save, where nothing was scored - which is a different fact from an event that
    /// happened to be worth nothing, and only one of the two occurs.
    /// </summary>
    [Fact]
    public void PersonaSaved_WithNoDelta_ReportsNullRatherThanZero()
    {
        var h = new ParserHarness();
        h.Feed("(Persona saved on 45,691).\n");

        var save = Assert.Single(h.ScoreSaves);
        Assert.Null(save.Delta);
        Assert.Equal(45691, save.Total);
    }

    /// <summary>
    /// The same three forms as they really arrive, C1 frames and all.
    ///
    /// <para>On the wire the TOTAL is wrapped (<c>F4 9C FF FF FE 9x FF FF</c> ... <c>FF FF FF FF</c>,
    /// the inner code varying with the colour the server wants for a gain, a loss or a plain save)
    /// while the signed delta sits outside it as plain text. Everything above tests the parse against
    /// already-decoded text; this tests the assumption that lets it - that by the time the analyzer
    /// runs, the decoder has left ordinary ASCII behind. Bytes transcribed from the raw session
    /// recordings, and matching the frame WorldResetEndsCombatTests pins independently.</para>
    /// </summary>
    [Theory]
    [InlineData("+38 = ", "19,214", (byte)0x9D, 38, 19214)]
    [InlineData("-872 = ", "18,382", (byte)0x9C, -872, 18382)]
    [InlineData("", "45,691", (byte)0x9E, null, 45691)]
    public void PersonaSaved_SurvivesTheC1FrameAroundTheTotal(
        string delta, string total, byte colour, int? expectedDelta, int expectedTotal)
    {
        var h = new ParserHarness();
        h.Feed(0x9D, 0x9C, 0xFF, 0xFF);   // game mode
        h.Feed(System.Text.Encoding.Latin1.GetBytes("(Persona saved on " + delta));
        h.Feed(0xF4, 0x9C, 0xFF, 0xFF, 0xFE, colour, 0xFF, 0xFF);
        h.Feed(System.Text.Encoding.Latin1.GetBytes(total));
        h.Feed(0xFF, 0xFF, 0xFF, 0xFF);
        h.Feed(System.Text.Encoding.Latin1.GetBytes(")."));
        h.Feed(0x0D, 0x00, 0x0D, 0x0A);

        var save = Assert.Single(h.ScoreSaves);
        Assert.Equal(expectedDelta, save.Delta);
        Assert.Equal(expectedTotal, save.Total);
    }

    /// <summary>
    /// The two wordings of the task line, both from recordings. Only the first-time one states a
    /// count; the repeat wording states none and must still produce the event, because the event's
    /// job is the payout that follows it and both wordings are followed by one.
    /// </summary>
    [Theory]
    [InlineData("You have completed a Task. This makes a total of 1.", 1)]
    [InlineData("You have completed a Task. This makes a total of 8.", 8)]
    [InlineData("You have completed a Task which you have done before.", null)]
    public void TaskCompleted_IsReadFromEitherWording(string line, int? expectedTotal)
    {
        var h = new ParserHarness();
        h.Feed(line + "\n");

        var task = Assert.Single(h.TaskCompletions);
        Assert.Equal(expectedTotal, task.TotalCompleted);
        Assert.Equal(line, task.RawText);
    }

    /// <summary>Anchored at column 0, so another player quoting the words cannot raise the event and
    /// swallow the next award. Chat arrives with the speaker and verb ahead of the quote.</summary>
    [Fact]
    public void TaskPhraseInsideChat_RaisesNothing()
    {
        var h = new ParserHarness();
        h.Feed("Bob shouts \"You have completed a Task. This makes a total of 1.\"\n");

        Assert.Empty(h.TaskCompletions);
    }

    /// <summary>
    /// A task discharged BY A KILL, with the task's flat payout printed FIRST and the creature's own
    /// award second.
    ///
    /// <para>Bytes transcribed from a session recording. The task line carries NO C1 code at all (it
    /// is bare ASCII between two 0D 00 0D 0A breaks, unlike the framed line above it), so prose is the
    /// only handle there is; and the task event is raised BEFORE either score event, so a consumer
    /// pairing awards to kills must skip the first rise.</para>
    /// </summary>
    [Fact]
    public void TaskCompletedByAKill_RaisesTheTaskBeforeBothScoreLines()
    {
        var h = new ParserHarness();
        h.Feed(0x9D, 0x9C, 0xFF, 0xFF);   // game mode
        h.Feed(System.Text.Encoding.Latin1.GetBytes("You have completed a Task. This makes a total of 1."));
        h.Feed(0x0D, 0x00, 0x0D, 0x0A);
        h.Feed(System.Text.Encoding.Latin1.GetBytes("(Persona saved on +100 = "));
        h.Feed(0xF4, 0x9C, 0xFF, 0xFF, 0xFE, 0x9D, 0xFF, 0xFF);
        h.Feed(System.Text.Encoding.Latin1.GetBytes("5,144"));
        h.Feed(0xFF, 0xFF, 0xFF, 0xFF);
        h.Feed(System.Text.Encoding.Latin1.GetBytes(")."));
        h.Feed(0x0D, 0x00, 0x0D, 0x0A);
        h.Feed(System.Text.Encoding.Latin1.GetBytes("(Persona saved on +976 = "));
        h.Feed(0xF4, 0x9C, 0xFF, 0xFF, 0xFE, 0x9D, 0xFF, 0xFF);
        h.Feed(System.Text.Encoding.Latin1.GetBytes("6,120"));
        h.Feed(0xFF, 0xFF, 0xFF, 0xFF);
        h.Feed(System.Text.Encoding.Latin1.GetBytes(")."));
        h.Feed(0x0D, 0x00, 0x0D, 0x0A);

        Assert.Equal(["task", "+100", "+976"], h.ScoringOrder);
        Assert.Equal(1, Assert.Single(h.TaskCompletions).TotalCompleted);
        Assert.Equal([100, 976], h.ScoreSaves.Select(s => s.Delta));
        Assert.Equal([5144, 6120], h.ScoreSaves.Select(s => s.Total));
    }

    /// <summary>A line that merely mentions the phrase without a parsable total produces no event.
    /// The stat path's PersonaSaved flag is deliberately left as it was - it keys on the phrase, and
    /// changing that is a different question from capturing the numbers.</summary>
    [Fact]
    public void PersonaSavedWithNoNumber_ProducesNoScoreEvent()
    {
        var h = new ParserHarness();
        h.Feed("(Persona saved on nothing at all).\n");

        Assert.Empty(h.ScoreSaves);
        Assert.True(h.Stats[0].PersonaSaved);
    }

    [Fact]
    public void UnrelatedLine_ReturnsNull()
    {
        // A line with no stat markers must not emit a StatsUpdated event
        var h = new ParserHarness();
        h.Feed("You are standing in a misty clearing.\n");
        Assert.Empty(h.Stats);
    }

    [Fact]
    public void CombatHitLine_ExtractsStamina()
    {
        // "The rat16 hits you (89/94)." -- stamina embedded mid-line
        var h = new ParserHarness();
        h.Feed("The rat16 hits you (89/94).\n");
        Assert.Single(h.Stats);
        Assert.Equal(89, h.Stats[0].Stamina);
        Assert.Equal(94, h.Stats[0].MaxStamina);
    }

    [Fact]
    public void CombatHitLine_UsesLastStaminaOccurrence()
    {
        // When multiple (N/M) appear, use the last one (the stamina update)
        var h = new ParserHarness();
        h.Feed("You (1/2) hit the rat (42/50) hard.\n");
        Assert.Single(h.Stats);
        Assert.Equal(42, h.Stats[0].Stamina);
        Assert.Equal(50, h.Stats[0].MaxStamina);
    }

    [Fact]
    public void DreamwordLine_Says_DoesNotMatch()
    {
        // "says" is a normal player speech verb -- must not trigger dreamword detection.
        // Dreamwords arrive via binary C15+C00+C00+C255 in game mode; text scanning
        // only covers verbs that are exclusively used by the MUD2 system (gasps etc.).
        var h = new ParserHarness();
        h.Feed("Gandalf passes you a note which says \"troulm\".\n");
        Assert.Empty(h.Stats);
    }

    [Fact]
    public void DreamwordLine_PlayerSays_DoesNotMatch()
    {
        // Player speech via "says" must not be mis-detected as a dreamword.
        var h = new ParserHarness();
        h.Feed("Ollie the hero says \"boom\".\n");
        Assert.Empty(h.Stats);
    }

    [Fact]
    public void DreamwordLine_Gasps_ExtractsDreamword()
    {
        // `gasps "orchid"` -- server uses gasps for the dreamword announcement
        var h = new ParserHarness();
        h.Feed("The wanderer gasps \"orchid\".\n");
        Assert.Single(h.Stats);
        Assert.Equal("orchid", h.Stats[0].DreamWord);
    }

    [Fact]
    public void DreamwordLine_Whispers_ExtractsDreamword()
    {
        var h = new ParserHarness();
        h.Feed("Someone whispers \"shadow\".\n");
        Assert.Single(h.Stats);
        Assert.Equal("shadow", h.Stats[0].DreamWord);
    }

    [Fact]
    public void DreamwordLine_Shouts_ExtractsDreamword()
    {
        var h = new ParserHarness();
        h.Feed("A voice shouts \"lotus\" from afar.\n");
        Assert.Single(h.Stats);
        Assert.Equal("lotus", h.Stats[0].DreamWord);
    }

    [Fact]
    public void DreamwordLine_DoesNotMatchUppercase()
    {
        // Dreamword is always lowercase; uppercase words inside quotes must not match
        var h = new ParserHarness();
        h.Feed("Someone says \"HELLO\".\n");
        Assert.Empty(h.Stats);
    }

    [Fact]
    public void DreamwordLine_DoesNotMatchTooLong()
    {
        // More than 14 lowercase letters is not a dreamword
        var h = new ParserHarness();
        h.Feed("Someone says \"abcdefghijklmno\".\n");
        Assert.Empty(h.Stats);
    }
}