using MudSharp.Combat;

namespace Mucka.Util.Tests;

/// <summary>
/// The operator's worked cases for learning a species' <see cref="SomeKind"/> from an unseen
/// episode, under made-up names so no real Creature's real kind leaks into the logic being tested.
/// Blorfs and quazzles are person-shaped in these fights; snurfles and podipoopers are not - or
/// rather, nothing here knows that until the episode says so.
/// </summary>
public sealed class SomeKindTests
{
    private static SomeKindKnowledge Fresh() => new();

    private static SomeKindKnowledge Knowing(params (string name, SomeKind kind)[] known)
    {
        var k = new SomeKindKnowledge();
        foreach (var (name, kind) in known) k.Learn(name, kind);
        return k;
    }

    // -- the default ----------------------------------------------------------------------------

    [Fact]
    public void AnUnknownCreatureIsWrittenAboutAsSomething()
    {
        var k = Fresh();
        Assert.Null(k.Known("podipooper3"));
        Assert.Equal(SomeKind.Something, k.Assumed("podipooper3"));
    }

    [Fact]
    public void KnowledgeIsPerSpecies_NotPerInstance()
    {
        var k = Fresh();
        k.Learn("blorf7", SomeKind.Someone);
        Assert.Equal(SomeKind.Someone, k.Known("blorf2"));
        Assert.Equal(SomeKind.Someone, k.Known("blorf"));
    }

    [Fact]
    public void ARestoredRow_NeverDisplacesAKindLearnedLive_AndRaisesNoReveal()
    {
        var k = Fresh();
        var revealed = new List<(string, SomeKind)>();
        k.Revealed += (s, kind) => revealed.Add((s, kind));

        k.Learn("blorf7", SomeKind.Someone);
        k.Restore(NpcGroups.Normalize("blorf7"), SomeKind.Something);   // an older run's row, arriving late
        k.Restore(NpcGroups.Normalize("snurfle"), SomeKind.Something);

        Assert.Equal(SomeKind.Someone, k.Known("blorf7"));
        Assert.Equal(SomeKind.Something, k.Known("snurfle2"));
        Assert.Equal([(NpcGroups.Normalize("blorf7"), SomeKind.Someone)], revealed);
    }

    [Fact]
    public void TheAnonymousWordsThemselvesAreNeverASpecies()
    {
        var k = Fresh();
        k.Learn(AnonymousOpponent.Person, SomeKind.Someone);
        k.Learn(AnonymousOpponent.Thing, SomeKind.Something);
        Assert.Empty(k.All);
    }

    // -- one Creature, the torch case ------------------------------------------------------------

    /// <summary>Fighting one podipooper in the dark; it hits. Whatever word it used is its kind.</summary>
    [Fact]
    public void OneUnknownCreature_TheWordItUsesIsItsKind()
    {
        var k = Fresh();
        var ep = new UnseenEpisode(["podipooper3"], k);
        ep.NoteAnonymousSwing(SomeKind.Something);

        Assert.Equal(SomeKind.Something, k.Known("podipooper3"));
    }

    // -- the operator's cases, in his order ------------------------------------------------------

    /// <summary>Fresh install. Blorf and quazzle attack. Light goes out. "Someone hits you."
    /// -> both revealed: a single kind seen, nothing known, nothing else engaged.</summary>
    [Fact]
    public void Case1_TwoUnknowns_OneKindSeen_RevealsBoth()
    {
        var k = Fresh();
        var ep = new UnseenEpisode(["blorf1", "quazzle2"], k);
        ep.NoteAnonymousSwing(SomeKind.Someone);
        // Nothing yet: this is also how a two-word episode begins. The heuristic waits for the close.
        Assert.Empty(k.All);

        ep.Close();
        Assert.Equal(SomeKind.Someone, k.Known("blorf1"));
        Assert.Equal(SomeKind.Someone, k.Known("quazzle2"));
    }

    /// <summary>Fresh. Blorf and snurfle attack. Light out. "Someone hits you." "Something hits
    /// you." -> no reveal. "You have killed something." -> no reveal. Light on. "The blorf hits
    /// you." -> the killed one was the snurfle (Something), so the Someone was the blorf.</summary>
    [Fact]
    public void Case2_TwoUnknowns_TwoKinds_ResolvedByTheKillAndTheSurvivor()
    {
        var k = Fresh();
        var ep = new UnseenEpisode(["blorf1", "snurfle4"], k);
        ep.NoteAnonymousSwing(SomeKind.Someone);
        ep.NoteAnonymousSwing(SomeKind.Something);
        Assert.Empty(k.All);

        ep.NoteAnonymousEnd(SomeKind.Something);
        Assert.Empty(k.All);

        ep.NoteNamed("blorf1");
        Assert.Equal(SomeKind.Something, k.Known("snurfle4"));
        Assert.Equal(SomeKind.Someone, k.Known("blorf1"));
    }

    /// <summary>Fresh. Blorf and snurfle attack, light out. Someone and Something hit -> no reveal.
    /// "Something is about to attack you." Light on. Blorf, snurfle and a guacamole hit you -> no
    /// reveals: the newcomer's Something covers the Something seen, so either of the first two
    /// could be the Someone, and the newcomer is not matched to the new name.</summary>
    [Fact]
    public void Case3_ANewcomerOfTheSameWord_LeavesEverythingUnknown()
    {
        var k = Fresh();
        var ep = new UnseenEpisode(["blorf1", "snurfle4"], k);
        ep.NoteAnonymousSwing(SomeKind.Someone);
        ep.NoteAnonymousSwing(SomeKind.Something);
        ep.NoteAnonymousStart(SomeKind.Something);
        ep.NoteNamed("blorf1");
        ep.NoteNamed("snurfle4");
        ep.NoteNamed("guacamole9");

        Assert.Empty(k.All);
    }

    /// <summary>Blorf known Someone. Blorf and snurfle attack, light out. Someone and Something
    /// hit -> the snurfle is revealed: the blorf explains the Someone, nothing else can be the
    /// Something.</summary>
    [Fact]
    public void Case4_OneKnown_TheOtherWordRevealsTheUnknown()
    {
        var k = Knowing(("blorf", SomeKind.Someone));
        var ep = new UnseenEpisode(["blorf1", "snurfle4"], k);
        ep.NoteAnonymousSwing(SomeKind.Someone);
        Assert.Null(k.Known("snurfle4"));

        ep.NoteAnonymousSwing(SomeKind.Something);
        Assert.Equal(SomeKind.Something, k.Known("snurfle4"));
    }

    /// <summary>Blorf known Someone. Blorf and quazzle attack, light out. "Someone hits you." -> no
    /// reveal: the blorf explains it. "You have killed someone." Light on. "The blorf hits you."
    /// -> the killed one was the quazzle, and it was a Someone.</summary>
    [Fact]
    public void Case5_AKnownCreatureExplainsTheWord_UntilTheKillAndTheSurvivorSayOtherwise()
    {
        var k = Knowing(("blorf", SomeKind.Someone));
        var ep = new UnseenEpisode(["blorf1", "quazzle2"], k);
        ep.NoteAnonymousSwing(SomeKind.Someone);
        Assert.Null(k.Known("quazzle2"));   // the single-kind heuristic must NOT fire here

        ep.NoteAnonymousEnd(SomeKind.Someone);
        Assert.Null(k.Known("quazzle2"));

        ep.NoteNamed("blorf1");
        Assert.Equal(SomeKind.Someone, k.Known("quazzle2"));
    }

    // -- edges the cases imply -------------------------------------------------------------------

    /// <summary>Two unknowns, two words, no end, light on with both named: still nothing - which
    /// was which is not on the wire.</summary>
    [Fact]
    public void TwoUnknowns_TwoKinds_BothSurvive_NothingIsLearned()
    {
        var k = Fresh();
        var ep = new UnseenEpisode(["blorf1", "snurfle4"], k);
        ep.NoteAnonymousSwing(SomeKind.Someone);
        ep.NoteAnonymousSwing(SomeKind.Something);
        ep.NoteNamed("blorf1");
        ep.NoteNamed("snurfle4");

        Assert.Empty(k.All);
    }

    /// <summary>Two Creatures removed by two ends of different words, one survivor: nothing about
    /// the removed pair can be split, and the survivor's kind is not implied either.</summary>
    [Fact]
    public void TwoEndsOfDifferentWords_RevealNothing()
    {
        var k = Fresh();
        var ep = new UnseenEpisode(["blorf1", "snurfle4", "quazzle2"], k);
        ep.NoteAnonymousSwing(SomeKind.Someone);
        ep.NoteAnonymousSwing(SomeKind.Something);
        ep.NoteAnonymousEnd(SomeKind.Someone);
        ep.NoteAnonymousEnd(SomeKind.Something);
        ep.NoteNamed("quazzle2");

        Assert.Empty(k.All);
    }

    /// <summary>Knowledge that contradicts the episode - a blorf known Someone alone in the dark,
    /// and a Something hits - learns nothing and throws nothing. The contradiction is real evidence
    /// against the stored kind, but this class does not decide what to believe about it.</summary>
    [Fact]
    public void AContradictedKnownKind_LearnsNothingAndDoesNotThrow()
    {
        var k = Knowing(("blorf", SomeKind.Someone));
        var ep = new UnseenEpisode(["blorf1"], k);
        ep.NoteAnonymousSwing(SomeKind.Something);

        Assert.Equal(SomeKind.Someone, k.Known("blorf1"));
        Assert.Single(k.All);
    }

    [Fact]
    public void RevealedFiresOncePerSpecies_AndAgainOnlyIfTheKindChanges()
    {
        var k = Fresh();
        var fired = new List<(string, SomeKind)>();
        k.Revealed += (s, kind) => fired.Add((s, kind));

        k.Learn("blorf1", SomeKind.Someone);
        k.Learn("blorf2", SomeKind.Someone);
        k.Learn("blorf3", SomeKind.Something);

        Assert.Equal(2, fired.Count);
        Assert.Equal(SomeKind.Something, fired[1].Item2);
    }
}
