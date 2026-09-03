using MudSharp.Combat;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// MUD2's experience-level table, hard-coded from the game's own `levels` dump.
///
/// <para>The table cannot drift - MUD2 is a frozen artifact - so these tests are not guarding against
/// the game changing. They guard against a TRANSCRIPTION error in
/// <see cref="PersonaRanks.Table"/>, which is the only way a wrong number can get in. Both columns
/// happen to carry an exact closed form, so a mistyped digit cannot hide.</para>
/// </summary>
public sealed class PersonaRanksTests
{
    [Fact]
    public void PointsDoubleFromTwoHundred()
    {
        Assert.Equal(0, PersonaRanks.Table[0].Points);

        var expected = 200;
        for (var level = 1; level < PersonaRanks.Table.Count; level++)
        {
            Assert.Equal(expected, PersonaRanks.Table[level].Points);
            expected *= 2;
        }
    }

    [Fact]
    public void MpkslIsPointsOverFivePlusSeventyFive()
    {
        // Holds for every row the game prints an MPKSL for - 0 -> 75, 200 -> 115, 25,600 -> 5,195,
        // 102,400 -> 20,555. Level 11 prints none, and that absence is part of the table.
        foreach (var rank in PersonaRanks.Table)
        {
            if (rank.Level == 11)
            {
                Assert.Null(rank.Mpksl);
                continue;
            }

            Assert.Equal((rank.Points / 5) + 75, rank.Mpksl);
        }
    }

    [Fact]
    public void TheRowsAreInLevelOrderAndIndexedByLevel()
    {
        for (var i = 0; i < PersonaRanks.Table.Count; i++)
            Assert.Equal(i, PersonaRanks.Table[i].Level);

        Assert.Equal(12, PersonaRanks.Table.Count);
    }

    [Theory]
    // The named rows this project has actually seen on the wire, so a reordering is caught by name
    // and not only by arithmetic.
    [InlineData(0, "novice")]
    [InlineData(1, "protector")]
    [InlineData(2, "yeoman")]
    [InlineData(3, "warrior")]
    [InlineData(8, "guardian")]
    public void TheMaleUnprotectedNamesMatchTheCapture(int level, string expected)
        => Assert.Equal(expected, PersonaRanks.Table[level].MaleNormal);

    [Theory]
    // Bracketed by real level-crossing events in the corpus: a `protector -> novice` demotion landed
    // on 98, and a `warrior -> yeoman` demotion on 735.
    [InlineData(0, 0)]
    [InlineData(98, 0)]
    [InlineData(199, 0)]
    [InlineData(200, 1)]
    [InlineData(735, 2)]
    [InlineData(800, 3)]
    [InlineData(25599, 7)]
    [InlineData(25600, 8)]
    public void LevelForScore(long score, int expected)
        => Assert.Equal(expected, PersonaRanks.LevelForScore(score));

    [Fact]
    public void AScoreAboveTheTopRowStaysAtTheTopRow()
    {
        // Deliberately not extrapolated: the game prints twelve rows and the client must not invent a
        // thirteenth for a score that exceeds them.
        Assert.Equal(11, PersonaRanks.LevelForScore(204_800));
        Assert.Equal(11, PersonaRanks.LevelForScore(50_000_000));
    }

    [Fact]
    public void AnOutOfRangeLevelHasNoMpkslRatherThanZero()
    {
        // Zero would read as "killing this is free", which is a measurement. Absent is not.
        Assert.Null(PersonaRanks.MpkslFor(-1));
        Assert.Null(PersonaRanks.MpkslFor(12));
        Assert.Equal(5195, PersonaRanks.MpkslFor(8));
    }
}
