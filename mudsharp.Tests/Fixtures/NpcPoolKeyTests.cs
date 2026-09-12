using MudSharp.Combat;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// The bucket a stamina-pool measurement belongs to. The one thing this must never do is what
/// <see cref="NpcGroups"/> does - collapse "large rat0" and "rat0" onto one name, when the two run to
/// roughly 100 and roughly 25 stamina respectively.
/// </summary>
public sealed class NpcPoolKeyTests
{
    [Theory]
    [InlineData("rat0", "rat")]
    [InlineData("rat", "rat")]
    [InlineData("rat18", "rat")]
    [InlineData("large rat0", "large rat")]
    [InlineData("water-snake5", "water-snake")]
    [InlineData("giant cave bat3", "giant cave bat")]
    [InlineData("Large Rat0", "large rat")]
    [InlineData("  large   rat0  ", "large rat")]
    [InlineData("billy goat", "billy goat")]
    public void StripsTheInstanceNumberAndNothingElse(string name, string expected)
        => Assert.Equal(expected, NpcPoolKey.For(name));

    [Fact]
    public void SizeVariantsDoNotShareABucket()
    {
        // The whole reason this type exists, stated as an assertion. Their NpcGroups name is identical.
        Assert.NotEqual(NpcPoolKey.For("large rat0"), NpcPoolKey.For("rat0"));
        Assert.Equal(NpcGroups.Normalize("large rat0"), NpcGroups.Normalize("rat0"));
    }

    [Fact]
    public void InstancesOfOneSpeciesDoShareABucket()
    {
        // MUD2 numbers instances of one spawn class and the bestiary assigns stamina per class, so
        // the instance number is what buys the sample size and dropping it is the point.
        Assert.Equal(NpcPoolKey.For("rat3"), NpcPoolKey.For("rat7"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("42")]
    public void BlankAndAllDigitNamesHaveNoKey(string? name)
        => Assert.Equal(string.Empty, NpcPoolKey.For(name));

    [Theory]
    [InlineData("large rat", "rat")]
    [InlineData("rat", "rat")]
    [InlineData("water-snake", "snake")]
    [InlineData("giant cave bat", "bat")]
    [InlineData("", "")]
    public void SpeciesWordDropsTheAdjectives(string key, string expected)
        => Assert.Equal(expected, NpcPoolKey.SpeciesWordOf(key));
}
