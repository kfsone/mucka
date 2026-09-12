using MudSharp.Combat;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// Turning the two stored streams - the swing ledger and the fight rollup - into pool observations.
///
/// <para>The rung attribution rule is the part worth pinning. The ledger stamps every swing row with
/// the creature's rung as it stood BEFORE that swing, so a value that does not change is
/// indistinguishable from a value that was re-read and came back the same. Crediting the last row
/// carrying a value would attribute a stale reading to a larger cumulative damage total and inflate
/// the pool floor - the direction that invents pool - so only a CHANGE is attributed, and it is
/// credited with the blows landed strictly before it.</para>
/// </summary>
public sealed class PoolObservationBuilderTests
{
    private static PoolSwingRow Out(long ts, double low, double high, int? rung = null)
        => new("rat0", ts, IsOutgoing: true, Hit: true, low, high, rung);

    private static PoolSwingRow OutMiss(long ts, int? rung = null)
        => new("rat0", ts, IsOutgoing: true, Hit: false, null, null, rung);

    private static PoolSwingRow In(long ts, int? rung = null)
        => new("rat0", ts, IsOutgoing: false, Hit: true, null, null, rung);

    private static PoolFightRow Fight(bool kill = false, long start = 0, long end = 10_000)
        => new("rat0", start, end, kill);

    [Fact]
    public void CollectsTheLandedBlowsInOrderAndSkipsMisses()
    {
        var observation = PoolObservationBuilder.Build(Fight(kill: true),
        [
            Out(1, 10, 14),
            OutMiss(2),
            Out(3, 5, 9),
            In(4),
        ]);

        Assert.NotNull(observation);
        Assert.Equal(2, observation!.Blows.Count);
        Assert.Equal(new DamageBracket(10, 14), observation.Blows[0]);
        Assert.Equal(new DamageBracket(5, 9), observation.Blows[1]);
        Assert.True(observation.EndedInKill);
    }

    [Fact]
    public void ARungIsCreditedWithTheBlowsThatLandedBeforeItsRow()
    {
        // Row 2 carries rung 6, meaning the creature read "superficially injured" after blow 1.
        var observation = PoolObservationBuilder.Build(Fight(),
        [
            Out(1, 10, 14),                 // no rung yet - nothing has been read
            Out(2, 10, 14, rung: 6),        // the reading that followed blow 1
            Out(3, 10, 14, rung: 5),        // the reading that followed blow 2
        ]);

        Assert.Equal([new RungReading(6, 1), new RungReading(5, 2)], observation!.Rungs);
    }

    [Fact]
    public void ARepeatedRungIsAttributedOnceAtItsEarliestSighting()
    {
        // Four rows all reading "fit". Only the first is attributable: after that, a re-read that came
        // back the same and a stale carry look identical in this column, and crediting the last row
        // would claim the creature survived 30 low damage at rung 7 when the evidence only supports 10.
        var observation = PoolObservationBuilder.Build(Fight(),
        [
            Out(1, 10, 14),
            Out(2, 10, 14, rung: 7),
            Out(3, 10, 14, rung: 7),
            Out(4, 10, 14, rung: 7),
        ]);

        Assert.Equal([new RungReading(7, 1)], observation!.Rungs);
    }

    [Fact]
    public void ARungThatFallsAndReturnsIsAttributedBothTimes()
    {
        // Creatures regenerate - a zombie in the corpus climbs back a rung mid-fight - so a value
        // returning to one it held before is a genuine second reading, not a repeat.
        var observation = PoolObservationBuilder.Build(Fight(),
        [
            Out(1, 10, 14),
            Out(2, 10, 14, rung: 6),
            Out(3, 10, 14, rung: 5),
            Out(4, 10, 14, rung: 6),
        ]);

        Assert.Equal(
            [new RungReading(6, 1), new RungReading(5, 2), new RungReading(6, 3)],
            observation!.Rungs);
    }

    [Fact]
    public void ARungCarriedOnAnIncomingRowStillCounts()
    {
        // The ledger stamps both directions, and a reading that arrived between two of the player's
        // blows is still a reading about the same cumulative damage.
        var observation = PoolObservationBuilder.Build(Fight(),
        [
            Out(1, 10, 14),
            In(2, rung: 7),
            Out(3, 10, 14),
        ]);

        Assert.Equal([new RungReading(7, 1)], observation!.Rungs);
    }

    [Fact]
    public void ARowWithNoBracketDoesNotCountAsABlow()
    {
        // Narrative mode: the row records that a blow landed and carries no figure. Counting it would
        // add a zero-damage blow to the prefix sums and understate every bound.
        var observation = PoolObservationBuilder.Build(Fight(),
        [
            new PoolSwingRow("rat0", 1, IsOutgoing: true, Hit: true, null, null, null),
            Out(2, 10, 14),
        ]);

        Assert.Single(observation!.Blows);
    }

    [Fact]
    public void AFightWithNoBracketedBlowConstrainsNothingAndIsDropped()
    {
        Assert.Null(PoolObservationBuilder.Build(Fight(kill: true), [OutMiss(1), In(2)]));
        Assert.Null(PoolObservationBuilder.Build(Fight(kill: true), []));
    }

    [Fact]
    public void AnInvertedOrNegativeBracketIsRejected()
    {
        var observation = PoolObservationBuilder.Build(Fight(),
        [
            Out(1, 20, 10),     // high below low
            Out(2, -5, 9),      // negative low
            Out(3, 10, 14),
        ]);

        Assert.Single(observation!.Blows);
        Assert.Equal(new DamageBracket(10, 14), observation.Blows[0]);
    }

    // -- Matching fights to their swings ------------------------------------------

    [Fact]
    public void SwingsAreMatchedByNameAndTimeWindowWithTheEndInclusive()
    {
        // The end stamp is the killing blow's own stamp, so an exclusive end would drop it and turn
        // every kill into a survivor.
        var fights = new[] { new PoolFightRow("rat0", 100, 200, EndedInKill: true) };
        var swings = new[]
        {
            Out(99, 1, 1),      // before the fight opened
            Out(100, 10, 14),
            Out(200, 10, 14),   // the killing blow, on the end stamp
            Out(201, 1, 1),     // after
        };

        var built = PoolObservationBuilder.BuildAll(fights, swings);

        Assert.Single(built);
        Assert.Equal(2, built[0].Blows.Count);
    }

    [Fact]
    public void SwingsAgainstOtherCreaturesAreNotFoldedIn()
    {
        var fights = new[] { new PoolFightRow("rat0", 0, 1000, EndedInKill: true) };
        var swings = new[]
        {
            Out(10, 10, 14),
            new PoolSwingRow("rat1", 20, IsOutgoing: true, Hit: true, 99, 99, null),
        };

        var built = PoolObservationBuilder.BuildAll(fights, swings);

        Assert.Single(built);
        Assert.Single(built[0].Blows);
        Assert.Equal(new DamageBracket(10, 14), built[0].Blows[0]);
    }

    [Fact]
    public void UnsortedSwingsAreOrderedBeforeTheyAreWalked()
    {
        // The rung attribution depends entirely on the order, so this is not a tidiness concern.
        var fights = new[] { new PoolFightRow("rat0", 0, 1000, EndedInKill: false) };
        var swings = new[]
        {
            Out(30, 10, 14, rung: 5),
            Out(10, 10, 14),
            Out(20, 10, 14, rung: 6),
        };

        var built = PoolObservationBuilder.BuildAll(fights, swings);

        Assert.Equal([new RungReading(6, 1), new RungReading(5, 2)], built[0].Rungs);
    }

    [Fact]
    public void AFightWithNoSwingsAtAllProducesNoObservation()
    {
        var built = PoolObservationBuilder.BuildAll(
            [new PoolFightRow("banshee", 0, 1000, EndedInKill: true)],
            [Out(10, 10, 14)]);

        Assert.Empty(built);
    }

    [Fact]
    public void TwoSequentialFightsAgainstOneNameGetTheirOwnSwings()
    {
        // A respawned "rat0" later in the session is a second fight with its own window; a builder that
        // handed both windows every swing would double-count and invent a huge pool.
        var fights = new[]
        {
            new PoolFightRow("rat0", 0, 100, EndedInKill: true),
            new PoolFightRow("rat0", 1000, 1100, EndedInKill: true),
        };
        var swings = new[] { Out(10, 10, 14), Out(1010, 20, 24), Out(1020, 20, 24) };

        var built = PoolObservationBuilder.BuildAll(fights, swings);

        Assert.Equal(2, built.Count);
        Assert.Single(built[0].Blows);
        Assert.Equal(2, built[1].Blows.Count);
    }
}
