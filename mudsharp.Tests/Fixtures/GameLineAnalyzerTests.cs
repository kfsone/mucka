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
        // Clio: buf[0]=='(' then (N/M) — prompt-style compact stamina
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
        // "strength:       N" — uses raw value when no effective strength present
        var h = new ParserHarness();
        h.Feed("strength:       94\n");
        Assert.Single(h.Stats);
        Assert.Equal(94, h.Stats[0].RawStrength);
        Assert.Equal(94, h.Stats[0].Strength);
    }

    [Fact]
    public void DexterityLine_ExtractsDexterity()
    {
        // "dexterity:      N" — uses raw value when no effective dexterity present
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
        // "score:  N,NNN points ..." — strips commas
        var h = new ParserHarness();
        h.Feed("score:  1,785 points    this game: 500\n");
        Assert.Single(h.Stats);
        Assert.Equal(1785, h.Stats[0].Score);
    }

    [Fact]
    public void PersonaSaved_SetsFlag_AndExtractsScore()
    {
        // "(Persona saved on [+N = ]M,NNN)." — sets PersonaSaved and extracts score
        var h = new ParserHarness();
        h.Feed("(Persona saved on +500 = 2,500).\n");
        Assert.Single(h.Stats);
        Assert.True(h.Stats[0].PersonaSaved);
        Assert.Equal(2500, h.Stats[0].Score);
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
        // "The rat16 hits you (89/94)." — stamina embedded mid-line
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
        // "says" is a normal player speech verb — must not trigger dreamword detection.
        // Dreamwords arrive via binary C15+C00+C00+C255 in game mode; text scanning
        // only covers verbs that are exclusively used by the MUD2 system (gasps etc.).
        var h = new ParserHarness();
        h.Feed("Gandalf passes you a note which says \"troulm\".\n");
        Assert.Empty(h.Stats);
    }

    [Fact]
    public void DreamwordLine_PlayerSays_DoesNotMatch()
    {
        // Regression: player speech via "says" must not be mis-detected as a dreamword.
        var h = new ParserHarness();
        h.Feed("Ollie the hero says \"boom\".\n");
        Assert.Empty(h.Stats);
    }

    [Fact]
    public void DreamwordLine_Gasps_ExtractsDreamword()
    {
        // `gasps "orchid"` — server uses gasps for the dreamword announcement
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