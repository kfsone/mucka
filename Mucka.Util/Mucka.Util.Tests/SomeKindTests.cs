using MudSharp.Combat;

namespace Mucka.Util.Tests;

/// <summary>
/// <see cref="SomeKindKnowledge"/>: the read-only seeds the unseen book consults, and nothing
/// learned.
/// </summary>
public sealed class SomeKindTests
{
    [Fact]
    public void AnUnknownCreatureHasNoKnownWord()
    {
        Assert.Null(SomeKindKnowledge.Known("podipooper3"));
        Assert.Equal(SomeKind.Something, SomeKinds.Default);
    }

    [Theory]
    [InlineData("man")]
    [InlineData("thief")]
    [InlineData("goblin4")]
    [InlineData("dwarf2")]
    [InlineData("giant1")]
    public void ThePersonShapedSpeciesTheOperatorNamed_AreSomeone(string name)
    {
        Assert.Equal(SomeKind.Someone, SomeKindKnowledge.Known(name));
    }

    [Theory]
    [InlineData("ghoul1")]
    [InlineData("zombie5")]
    [InlineData("skeleton3")]
    public void SpeciesNotYetTested_AreUnknown(string name)
    {
        Assert.Null(SomeKindKnowledge.Known(name));
    }

    [Fact]
    public void APlayerIsSomeone()
    {
        Assert.Equal(SomeKind.Someone, SomeKindKnowledge.Known("Kickflip"));
    }

    [Fact]
    public void TheAnonymousWordsAndTheGroupAreNeverASpecies()
    {
        Assert.Null(SomeKindKnowledge.Known(AnonymousOpponent.Person));
        Assert.Null(SomeKindKnowledge.Known(AnonymousOpponent.Thing));
        Assert.Null(SomeKindKnowledge.Known(AnonymousOpponent.Group));
    }

    [Theory]
    [InlineData("rat0")]
    [InlineData("large rat2")]
    [InlineData("water-snake5")]
    [InlineData("fox")]
    [InlineData("eagle1")]
    [InlineData("basilisk")]
    public void TheSpeciesObservedAsSomething_AreSomething(string name)
    {
        Assert.Equal(SomeKind.Something, SomeKindKnowledge.Known(name));
    }
}
