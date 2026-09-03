using MudSharp.Models;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// Telling a creature from an object in the FEI "here" list.
///
/// <para>Every sentence quoted below is verbatim from a session recording under
/// <c>%LOCALAPPDATA%\Temp\mucka</c>, extracted from the C1 04.00.01 ("normal creatures here") scope.
/// They are the shapes the rule has to survive: a numbered instance named only inside parentheses, an
/// unnumbered mob named bare in the middle of a clause, a hyphenated name, a two-word name, and a
/// sentence the server wrapped mid-name.</para>
/// </summary>
public sealed class RoomCreaturesTests
{
    private static RoomCreatures WithRoom(params string[] sentences)
    {
        var creatures = new RoomCreatures();
        foreach (var sentence in sentences)
            creatures.Observe(sentence);
        return creatures;
    }

    [Fact]
    public void NothingSeen_ClassifiesNothing()
    {
        // The honest state on a mid-room attach: the game has not said, so nothing is claimed.
        Assert.False(new RoomCreatures().IsCreature("rat17"));
    }

    [Fact]
    public void NumberedInstance_NamedOnlyInParentheses()
    {
        var creatures = WithRoom("An evil, black rat (rat17) bares its razor-sharp incisors at you.");
        Assert.True(creatures.IsCreature("rat17"));
    }

    [Fact]
    public void SiblingInstanceIsNotClassified()
    {
        // The sentence named rat17. rat19 may well be a rat, but it was not described in this room,
        // and inferring it from the species would be the guess this class exists to avoid.
        var creatures = WithRoom("An evil, black rat (rat17) bares its razor-sharp incisors at you.");
        Assert.False(creatures.IsCreature("rat19"));
    }

    [Fact]
    public void UnnumberedMob_NamedBare()
    {
        var creatures = WithRoom(
            "A dark, bedraggled coot sits here, miserably.",
            "A large, black raven swoops about your head.",
            "A mist-swathed banshee floats nebulously before you.");
        Assert.True(creatures.IsCreature("coot"));
        Assert.True(creatures.IsCreature("raven"));
        Assert.True(creatures.IsCreature("banshee"));
    }

    [Fact]
    public void HyphenatedAndMultiWordNames()
    {
        var creatures = WithRoom(
            "A slimy, slithering water-snake surfaces in front of you.",
            "A mean-looking billy goat here scowls at you, disapprovingly.");
        Assert.True(creatures.IsCreature("water-snake"));
        Assert.True(creatures.IsCreature("billy goat"));
    }

    [Fact]
    public void WrappedSentence_StillMatches()
    {
        // The server wraps at the terminal width and did so mid-name in this observed frame; the
        // parser turns the wrap into a space, so the capture arrives already joined.
        var creatures = WithRoom("Beating its wings above you is an incensed dragonfly!");
        Assert.True(creatures.IsCreature("dragonfly"));
    }

    [Fact]
    public void CaseIsIgnored()
    {
        var creatures = WithRoom("Tally ho! A rabid-looking Fox glares at you!");
        Assert.True(creatures.IsCreature("fox"));
        Assert.True(creatures.IsCreature("FOX"));
    }

    [Fact]
    public void ObjectsInTheSameRoomAreNotCreatures()
    {
        // The room list that produced this test verbatim: rat21, rat19, key1, rat16, rat17, brand40.
        var creatures = WithRoom("An evil, black rat (rat21) bares its razor-sharp incisors at you.");
        Assert.False(creatures.IsCreature("key1"));
        Assert.False(creatures.IsCreature("brand40"));
    }

    [Fact]
    public void RoomChange_ForgetsEverything()
    {
        var creatures = WithRoom("A small mouse squeaks on the floor nearby.");
        Assert.True(creatures.IsCreature("mouse"));
        creatures.Clear();
        Assert.False(creatures.IsCreature("mouse"));
    }

    [Fact]
    public void OldestSentenceIsDiscardedFirstPastTheCap()
    {
        // The cap only exists so a pathological stream cannot grow this without bound. What it drops
        // is the oldest, which is the one most likely to describe something that has since left.
        var creatures = new RoomCreatures();
        creatures.Observe("A small mouse squeaks on the floor nearby.");
        for (var i = 0; i < 40; i++)
            creatures.Observe($"An evil, black rat (rat{i}) bares its razor-sharp incisors at you.");
        Assert.False(creatures.IsCreature("mouse"));
        Assert.True(creatures.IsCreature("rat39"));
    }

    [Fact]
    public void BlankObservationsAndLookupsAreIgnored()
    {
        var creatures = new RoomCreatures();
        creatures.Observe(null);
        creatures.Observe("   ");
        Assert.False(creatures.IsCreature(null));
        Assert.False(creatures.IsCreature(""));
    }

    // ── The boundary rule on its own ──────────────────────────────────────────

    [Theory]
    [InlineData("a small mouse squeaks", "mouse", true)]
    [InlineData("(rat17) bares its teeth", "rat17", true)]
    [InlineData("a water-snake surfaces", "water-snake", true)]
    [InlineData("a mean-looking billy goat here", "billy goat", true)]
    // A prefix is not a token: "bat0" in the here list must not be matched by a sentence about a
    // "bat01", and "rat" must not match "ratchet".
    [InlineData("an evil, black ratchet", "rat", false)]
    [InlineData("a giant cave bat01 flaps", "bat0", false)]
    // A hyphen is a word character, so a species word alone does not match a hyphenated name - the
    // same split NpcPoolKey makes between "water-snake" and "snake".
    [InlineData("a slimy water-snake surfaces", "snake", false)]
    [InlineData("swings a bat0-shaped club", "bat0", false)]
    [InlineData("", "mouse", false)]
    [InlineData("a mouse", "", false)]
    public void ContainsToken(string haystack, string needle, bool expected)
        => Assert.Equal(expected, RoomCreatures.ContainsToken(haystack, needle));

    // ── The row's icon state ──────────────────────────────────────────────────

    [Theory]
    [InlineData(false, false, HereSwords.Hidden)]
    // Grey and present, not absent: this is the affordance a future click-to-attack hangs on, and an
    // icon that only appeared once a fight had started could never start one.
    [InlineData(true, false, HereSwords.Idle)]
    [InlineData(true, true, HereSwords.Engaged)]
    [InlineData(false, true, HereSwords.Engaged)]
    public void SwordsFor(bool isCreature, bool isEngaged, HereSwords expected)
        => Assert.Equal(expected, HereRow.SwordsFor(isCreature, isEngaged));
}
