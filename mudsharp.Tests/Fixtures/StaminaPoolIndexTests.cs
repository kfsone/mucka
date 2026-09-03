using MudSharp.Combat;

namespace mudsharp.Tests.Fixtures;

/// <summary>The per-species estimate cache: bucketing by pool key, and the "no data" contract that
/// still names the creature it has no data for.</summary>
public sealed class StaminaPoolIndexTests
{
    private static PoolFightObservation Kill(string name, double low, double high)
        => new(name, 0, 1000, true, [new DamageBracket(low, high)], []);

    [Fact]
    public void BucketsBySpeciesAndSizeSoALargeRatIsNotARat()
    {
        var index = new StaminaPoolIndex();
        index.Load([
            Kill("rat0", 20, 25),
            Kill("rat7", 20, 25),
            Kill("large rat0", 90, 100),
        ]);

        Assert.Equal(25, index.Lookup("rat0").Interval.AtMost);
        Assert.Equal(25, index.Lookup("rat3").Interval.AtMost);     // an instance nobody has fought
        Assert.Equal(100, index.Lookup("large rat0").Interval.AtMost);
    }

    [Fact]
    public void AnUnknownCreatureStillReportsItsKeyAndItsRegenerationLabel()
    {
        // So a readout can say "no data for large zombies", worded correctly, before the first kill -
        // rather than "no data" with no idea what the number would even have been measuring.
        var estimate = new StaminaPoolIndex().Lookup("large zombie2");

        Assert.False(estimate.HasEvidence);
        Assert.Equal("large zombie", estimate.PoolKey);
        Assert.Equal(PoolQuantity.DamageToKill, estimate.Quantity);
    }

    [Fact]
    public void ABlankNameHasNoEstimateAndNoKey()
    {
        var index = new StaminaPoolIndex();
        Assert.Equal(string.Empty, index.Lookup(null).PoolKey);
        Assert.Equal(string.Empty, index.Lookup("  ").PoolKey);
    }

    [Fact]
    public void LoadingReplacesRatherThanAccumulating()
    {
        // An estimate is a function of the WHOLE observation set for its key, so a partial fold would
        // produce a figure that is not an estimate of anything - and a second warm over the same corpus
        // must not double the fight counts.
        var index = new StaminaPoolIndex();
        index.Load([Kill("rat0", 20, 25), Kill("rat7", 20, 25)]);
        index.Load([Kill("rat0", 20, 25), Kill("rat7", 20, 25)]);

        Assert.Equal(2, index.Lookup("rat0").ContributingFights);
    }

    [Fact]
    public void ObservationsWithNoResolvableKeyAreSkipped()
    {
        var index = new StaminaPoolIndex();
        index.Load([Kill("42", 10, 20), Kill("rat0", 20, 25)]);

        Assert.Single(index.Snapshot());
        Assert.True(index.Snapshot().ContainsKey("rat"));
    }
}
