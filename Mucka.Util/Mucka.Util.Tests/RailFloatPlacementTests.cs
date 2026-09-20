using Mucka.Combat;

namespace Mucka.Util.Tests;

/// <summary>
/// Where a granted float is drawn. The arithmetic sat mid-handler in the MAUI page while its sibling
/// <see cref="RailFloatBudget"/>, which hands out the cluster, was already here and already covered.
/// </summary>
public sealed class RailFloatPlacementTests
{
    private static readonly RailRect Origin = new(Left: 40, Top: 200, Width: 92, Height: 24);

    /// <summary>
    /// The relation the whole effect rests on, and until this test it was asserted only by a comment.
    ///
    /// <para>A cluster steps up by LESS than a float's own height, so the numbers of one tick overlap
    /// rather than stacking clear. The overlap is the point: several figures piling over one pane is
    /// the sense of being hit by a pack. Raise the step past the height, or lower the height to fit
    /// the base font, and the pack-flurry reading dies with the only detector being the operator
    /// noticing during a pack fight.</para>
    /// </summary>
    [Fact]
    public void ClusterStepIsLessThanTheFloatHeightSoAClusterOverlaps()
        => Assert.True(
            RailFloatPlacement.CombatFloatClusterStepDp < RailFloatPlacement.CombatFloatHeightDp,
            "A cluster must overlap, not stack clear.");

    /// <summary>Cluster 0 - a new exchange - starts exactly beside the name it belongs to, every
    /// time. Catches a zig applied to the first float, which would make the anchor for one blow
    /// differ from the anchor for the next exchange's first blow.</summary>
    [Fact]
    public void ClusterZeroSitsExactlyOnTheOrigin()
        => Assert.Equal((Origin.Left, Origin.Top), RailFloatPlacement.For(0, Origin));

    /// <summary>Further blows of the same tick alternate sideways: odd clusters one way, even
    /// clusters the other. Catches a step that walks in one direction, which marches the flurry off
    /// the pane instead of piling it over one.</summary>
    [Fact]
    public void FurtherClustersAlternateSideways()
    {
        Assert.Equal(Origin.Left + RailFloatPlacement.CombatFloatZigDp, RailFloatPlacement.For(1, Origin).X);
        Assert.Equal(Origin.Left - RailFloatPlacement.CombatFloatZigDp, RailFloatPlacement.For(2, Origin).X);
        Assert.Equal(Origin.Left + RailFloatPlacement.CombatFloatZigDp, RailFloatPlacement.For(3, Origin).X);
    }

    /// <summary>Each further cluster sits one step HIGHER - a smaller Y, since the origin is a
    /// top-left. Catches a sign flip, which would drive the flurry down over the tile below instead
    /// of up off the one it belongs to.</summary>
    [Fact]
    public void FurtherClustersStepUpwardsByOneStepEach()
    {
        Assert.Equal(
            Origin.Top - RailFloatPlacement.CombatFloatClusterStepDp,
            RailFloatPlacement.For(1, Origin).Y);
        Assert.Equal(
            Origin.Top - (3 * RailFloatPlacement.CombatFloatClusterStepDp),
            RailFloatPlacement.For(3, Origin).Y);
    }

    /// <summary>Every float of a cluster shares ONE column, however far it has zig-zagged: the widest
    /// excursion is a single zig either side of the origin, never a cumulative drift. Catches a zig
    /// that accumulates with the cluster.</summary>
    [Fact]
    public void TheWholeClusterStaysWithinOneZigOfTheOrigin()
    {
        for (var cluster = 0; cluster < RailFloatBudget.MaxInFlight; cluster++)
        {
            var offset = Math.Abs(RailFloatPlacement.For(cluster, Origin).X - Origin.Left);
            Assert.True(offset <= RailFloatPlacement.CombatFloatZigDp);
        }
    }
}
