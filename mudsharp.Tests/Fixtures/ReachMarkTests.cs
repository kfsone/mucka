using MudSharp.Combat;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// Reach marks: how far a creature has been SEEN to hit, which is a floor under its true maximum and
/// never the maximum. The behaviour that matters is monotonicity - a reach can rise and can never
/// fall, however the index is reloaded.
/// </summary>
public sealed class ReachMarkTests
{
    [Fact]
    public void NoEvidenceIsDistinguishableFromAMeasuredZero()
    {
        // MUD2 lands blows that take nothing off, so 0 from one blow is a measurement about a creature
        // and 0 from none is silence. Comparing ReachesAtLeast against 0 cannot tell them apart, which
        // is why HasEvidence exists.
        Assert.False(ReachMark.None.HasEvidence);
        Assert.True(ReachMark.None.Observe(0).HasEvidence);
        Assert.Equal(0, ReachMark.None.Observe(0).ReachesAtLeast);
    }

    [Fact]
    public void ObservingRaisesTheReachAndAlwaysCountsTheBlow()
    {
        var mark = ReachMark.None.Observe(4).Observe(7).Observe(2);
        Assert.Equal(7, mark.ReachesAtLeast);
        Assert.Equal(3, mark.Blows);
    }

    [Fact]
    public void MergeTakesTheHigherReachAndSumsTheBlows()
    {
        var merged = new ReachMark(7, 873).Merge(new ReachMark(5, 20));
        Assert.Equal(7, merged.ReachesAtLeast);
        Assert.Equal(893, merged.Blows);
    }

    [Fact]
    public void TheIndexKeysOnSpeciesAndSizeNotOnTheInstance()
    {
        var index = new ReachMarkIndex();
        index.Observe("rat0", 4);
        index.Observe("rat7", 7);
        index.Observe("large rat0", 22);

        // rat0 and rat7 share a mark...
        Assert.Equal(7, index.Lookup("rat0").ReachesAtLeast);
        Assert.Equal(2, index.Lookup("rat7").Blows);
        // ...and a large rat does not, which is the whole reason this is keyed on NpcPoolKey. Their
        // NpcGroups name is the same.
        Assert.Equal(22, index.Lookup("large rat0").ReachesAtLeast);
        Assert.Equal(1, index.Lookup("large rat3").Blows);
    }

    [Fact]
    public void LoadingTheCorpusCannotLowerAReachAlreadyObserved()
    {
        // The database's own aggregate is authoritative about the corpus and stale about this session.
        // A reach that only ever grows must not be un-grown by a reload - SwingDamageIndex.LoadProfiles
        // replaces, and this deliberately does not.
        var index = new ReachMarkIndex();
        index.Observe("ogre0", 26);
        index.Load([("ogre0", 19, 40)]);

        Assert.Equal(26, index.Lookup("ogre0").ReachesAtLeast);
        Assert.Equal(41, index.Lookup("ogre0").Blows);
    }

    [Fact]
    public void LoadingFoldsSeveralInstancesIntoOneSpeciesMark()
    {
        var index = new ReachMarkIndex();
        index.Load([("rat0", 5, 100), ("rat7", 7, 200), ("large rat0", 22, 30)]);

        Assert.Equal(7, index.Lookup("rat0").ReachesAtLeast);
        Assert.Equal(300, index.Lookup("rat0").Blows);
        Assert.Equal(22, index.Lookup("large rat0").ReachesAtLeast);
    }

    [Fact]
    public void RowsWithNoBlowsBehindThemAreNotLoaded()
    {
        var index = new ReachMarkIndex();
        index.Load([("rat0", 99, 0)]);
        Assert.False(index.Lookup("rat0").HasEvidence);
    }

    [Fact]
    public void NegativeDamageIsNotABlow()
    {
        // A regen tick outrunning a hit produces one. It is a bookkeeping artefact, not a reading.
        var index = new ReachMarkIndex();
        index.Observe("rat0", -3);
        Assert.False(index.Lookup("rat0").HasEvidence);
    }

    [Fact]
    public void AnUnknownOrBlankCreatureHasNoMark()
    {
        var index = new ReachMarkIndex();
        index.Observe("rat0", 7);

        Assert.False(index.Lookup("dragon").HasEvidence);
        Assert.False(index.Lookup(null).HasEvidence);
        Assert.False(index.Lookup("   ").HasEvidence);
    }

    [Fact]
    public void SnapshotIsACopyRatherThanALiveView()
    {
        var index = new ReachMarkIndex();
        index.Observe("rat0", 4);
        var snapshot = index.Snapshot();
        index.Observe("rat0", 9);

        Assert.Equal(4, snapshot["rat"].ReachesAtLeast);
        Assert.Equal(9, index.Lookup("rat0").ReachesAtLeast);
    }
}
