using MudSharp.Combat;
using MudSharp.Models;

namespace Mucka.Util.Tests;

/// <summary>
/// A player as an opponent. Every line is verbatim from the operator's fight with Kickflip: the
/// lines carried the fight codes, and the tracker matched none
/// of them because every pattern in it wanted "The X", so the fight was recorded as nothing until
/// the operator went blind and "Someone misses you." opened a fight against the word.
/// </summary>
public sealed class PlayerOpponentTests
{
    private static readonly DateTime T0 = new(2026, 10, 8, 9, 19, 13, DateTimeKind.Utc);

    private static StyledLine Line(string text) => new([new StyledSpan(text, TextStyle.Default)]);

    private static StyledLine FightStartLine(string text) =>
        new([new StyledSpan(text, TextStyle.Default)], kind: LineKind.FightStart);

    private static (CombatTracker Tracker, List<CombatEvent> Seen) Opened()
    {
        var tracker = new CombatTracker();
        var seen = new List<CombatEvent>();
        tracker.EventOccurred += seen.Add;
        tracker.Observe(Line("Kickflip the warrior has just arrived."), T0);
        tracker.Observe(FightStartLine("Kickflip the warrior is snarling at you angrily."), T0);
        return (tracker, seen);
    }

    [Fact]
    public void APlayerSnarlingAtYou_OpensTheFight_UnderTheirPersona()
    {
        var (tracker, seen) = Opened();

        Assert.True(tracker.InCombat);
        var start = Assert.Single(seen, e => e.Kind == CombatEventKind.FightStart);
        Assert.Equal(CombatActor.Npc, start.Actor);
        Assert.Equal("Kickflip", start.NpcName);
    }

    [Fact]
    public void ThePlayersBlowsAndYours_AreAttributedToThePersona_NotTheTitle()
    {
        var (tracker, seen) = Opened();

        tracker.Observe(Line("Kickflip the warrior misses you."), T0.AddSeconds(1));
        tracker.Observe(Line("You hit Kickflip the warrior (1-4)."), T0.AddSeconds(2));
        tracker.Observe(Line("You miss Kickflip the warrior."), T0.AddSeconds(3));
        tracker.Observe(Line("Kickflip the warrior hits you (83/110)."), T0.AddSeconds(4));

        Assert.Equal("Kickflip", seen.Single(e => e.Kind == CombatEventKind.MissByNpc).NpcName);
        Assert.Equal("Kickflip", seen.Single(e => e.Kind == CombatEventKind.Hit).NpcName);
        Assert.Equal("Kickflip", seen.Single(e => e.Kind == CombatEventKind.Miss).NpcName);
        Assert.Equal("Kickflip", seen.Single(e => e.Kind == CombatEventKind.HitByNpc).NpcName);
    }

    [Fact]
    public void BlindedByABackfire_TheSoleNamedPlayer_TakesTheAnonymousBlows()
    {
        var (tracker, seen) = Opened();
        tracker.Observe(Line("Kickflip the warrior hits you (83/110)."), T0.AddSeconds(4));

        tracker.NoteCannotSee(true, T0.AddSeconds(8), "blinded");
        tracker.Observe(Line("Someone misses you."), T0.AddSeconds(10));
        tracker.Observe(Line("You miss someone."), T0.AddSeconds(10));

        Assert.DoesNotContain(seen, e => e.NpcName == AnonymousOpponent.Person);
        Assert.Equal("Kickflip", seen.Last(e => e.Kind == CombatEventKind.MissByNpc).NpcName);
        Assert.Equal("Kickflip", seen.Last(e => e.Kind == CombatEventKind.Miss).NpcName);
    }

    [Fact]
    public void APlayerFleeingByTryingToGo_EndsTheirFight()
    {
        var (tracker, seen) = Opened();
        tracker.Observe(Line("Kickflip the warrior misses you."), T0.AddSeconds(1));

        tracker.Observe(Line("Kickflip the warrior has fled by trying to go over."), T0.AddSeconds(60));

        Assert.Equal("Kickflip", seen.Single(e => e.Kind == CombatEventKind.NpcFleeFailed).NpcName);
        Assert.False(tracker.InCombat);
    }

    [Fact]
    public void AnotherPlayerLeavingTheRoom_DoesNotTouchTheFight()
    {
        var (tracker, seen) = Opened();

        tracker.Observe(Line("Bob the warrior has fled by going west."), T0.AddSeconds(1));

        Assert.DoesNotContain(seen, e => e.NpcName == "Bob");
        Assert.True(tracker.InCombat);
    }

    [Fact]
    public void APersonaIsNeverLearnedAsASpecies_AndIsAlwaysPersonClass()
    {
        var (tracker, _) = Opened();
        tracker.NoteCannotSee(true, T0.AddSeconds(2), "blinded");
        tracker.Observe(Line("Someone misses you."), T0.AddSeconds(3));

        Assert.Equal(SomeKind.Someone, tracker.Knowledge.Known("Kickflip"));
        Assert.Empty(tracker.Knowledge.All);
    }

    // ---- Player <-> Someone <-> Player ------------------------------------------------------

    private static (CombatTracker Tracker, List<CombatEvent> Seen) Fresh()
    {
        var tracker = new CombatTracker();
        var seen = new List<CombatEvent>();
        tracker.EventOccurred += seen.Add;
        return (tracker, seen);
    }

    /// <summary>A tracker on an install that has already learned the two kinds MUD2's own wording
    /// implies for these species: the man is "someone", a rat is "something" (this install's
    /// <c>some_kinds</c> holds rats; the man is from runs 12, 35, 36, 58 and 59).</summary>
    private static (CombatTracker Tracker, List<CombatEvent> Seen) FreshKnowingManAndRat()
    {
        var (tracker, seen) = Fresh();
        tracker.Knowledge.Restore(NpcGroups.Normalize("man"), SomeKind.Someone);
        tracker.Knowledge.Restore(NpcGroups.Normalize("rat1"), SomeKind.Something);
        return (tracker, seen);
    }

    [Fact]
    public void AnInvisibleSomeone_ThenNamedWhileSighted_IsThatPlayer()
    {
        var (tracker, seen) = Fresh();
        tracker.Observe(FightStartLine("Someone is about to attack you."), T0);
        tracker.Observe(Line("Someone hits you (50/110)."), T0.AddSeconds(1));
        Assert.Equal(1, tracker.Unseen.Someone);

        tracker.Observe(Line("Kickflip the warrior hits you (40/110)."), T0.AddSeconds(3));

        Assert.Equal(0, tracker.Unseen.Someone);
        Assert.Contains(seen, e => e.Kind == CombatEventKind.UnseenNamed && e.NpcName == AnonymousOpponent.Person);
        Assert.Equal("Kickflip", seen.Last(e => e.Kind == CombatEventKind.HitByNpc).NpcName);
        Assert.True(tracker.InCombat);
    }

    [Fact]
    public void AnInvisibleSomeoneAnnouncedWhileBlind_ThenNamedWhenSightReturns_IsThatPlayer()
    {
        var (tracker, seen) = Fresh();
        tracker.NoteCannotSee(true, T0, "blinded");
        tracker.Observe(FightStartLine("Someone is about to attack you."), T0.AddSeconds(1));
        tracker.NoteCannotSee(false, T0.AddSeconds(5), "sight returned");

        tracker.Observe(Line("Kickflip the warrior hits you (40/110)."), T0.AddSeconds(6));

        Assert.Equal(0, tracker.Unseen.Someone);
        Assert.Contains(seen, e => e.Kind == CombatEventKind.UnseenNamed);
    }

    [Fact]
    public void TwoSomeonesOpen_ANamedPlayerClaimsNeither()
    {
        var (tracker, _) = Fresh();
        tracker.Observe(FightStartLine("Someone is about to attack you."), T0);
        tracker.Observe(FightStartLine("Someone is about to attack you."), T0.AddSeconds(1));

        tracker.Observe(Line("Kickflip the warrior hits you (40/110)."), T0.AddSeconds(3));

        Assert.Equal(2, tracker.Unseen.Someone);
    }

    [Fact]
    public void APlayerWhoGoesInvisible_KeepsTheirName_ForTheAnonymousBlowsThatFollow()
    {
        var (tracker, seen) = Opened();
        tracker.Observe(Line("Kickflip the warrior hits you (83/110)."), T0.AddSeconds(1));

        tracker.Observe(Line("Kickflip the warrior fades from view."), T0.AddSeconds(2));
        tracker.Observe(Line("Someone hits you (60/110)."), T0.AddSeconds(3));
        tracker.Observe(Line("You hit someone (9)."), T0.AddSeconds(4));
        tracker.Observe(Line("Someone misses you."), T0.AddSeconds(5));

        Assert.Equal("Kickflip", seen.Single(e => e.Kind == CombatEventKind.NpcTurnedInvisible).NpcName);
        Assert.DoesNotContain(seen, e => e.NpcName == AnonymousOpponent.Person);
        Assert.Equal(0, tracker.Unseen.Someone);
        Assert.Equal("Kickflip", seen.Last(e => e.Kind == CombatEventKind.HitByNpc).NpcName);
        Assert.Equal("Kickflip", seen.Last(e => e.Kind == CombatEventKind.Hit).NpcName);
    }

    [Fact]
    public void APlayerWhoRegainsVisibility_IsNamedAgain_AndNoLongerTheAnonymousTarget()
    {
        var (tracker, seen) = Opened();
        tracker.Observe(Line("Kickflip the warrior hits you (83/110)."), T0.AddSeconds(1));
        tracker.Observe(Line("Kickflip the warrior fades from view."), T0.AddSeconds(2));
        tracker.Observe(Line("Kickflip the warrior has regained his visibleness!"), T0.AddSeconds(4));

        tracker.Observe(Line("Someone hits you (60/110)."), T0.AddSeconds(5));

        // Sighted and nothing faded: the word has no explanation, so it stays the word.
        Assert.Equal(AnonymousOpponent.Person, seen.Last(e => e.Kind == CombatEventKind.HitByNpc).NpcName);
    }

    // ---- Verbatim from 2026-10-08 (run 271) --------------------------------------------------

    private static StyledLine FightEndLine(string text) =>
        new([new StyledSpan(text, TextStyle.Default)], kind: LineKind.FightEnd);

    /// <summary>10:29:39-40. He drank a potion, faded (05.00.05), and the next frame ended the fight
    /// with the anonymous word in the sentence: <c>[A3][A7]You can fight someone no longer.</c></summary>
    [Fact]
    public void APlayerWhoFadesAndEndsTheFight_IsClosedUnderTheirName_NotAsSomeone()
    {
        var (tracker, seen) = Fresh();
        tracker.Observe(Line("Kickflip the swordsman has just arrived."), T0);
        tracker.Observe(FightStartLine("Kickflip the swordsman is rushing at you fiercely."), T0);
        tracker.Observe(Line("Kickflip the swordsman hits you (107/110)."), T0.AddSeconds(1));
        tracker.Observe(Line("Kickflip the swordsman misses you."), T0.AddSeconds(30));

        tracker.Observe(Line("Kickflip the swordsman has drunk some potion2."), T0.AddSeconds(32));
        tracker.Observe(Line("Kickflip the swordsman fades from view."), T0.AddSeconds(32));
        tracker.Observe(FightEndLine("You can fight someone no longer."), T0.AddSeconds(33));

        Assert.Equal("Kickflip", seen.Single(e => e.Kind == CombatEventKind.NpcTurnedInvisible).NpcName);
        Assert.False(tracker.InCombat);
        Assert.DoesNotContain(seen, e => e.NpcName == AnonymousOpponent.Person);
        Assert.Equal(0, tracker.Unseen.Someone);
    }

    /// <summary>11:05:21-39. Invisible and hunting the operator: announced as "Someone", struck three
    /// times, made visible (05.00.04, "has suddenly become visible!"), and first named by a blow six
    /// seconds later.</summary>
    [Fact]
    public void TheElevenOhFiveFight_AnnouncedAsSomeone_IsKickflipFromTheFirstNamedBlow()
    {
        var (tracker, seen) = Fresh();
        tracker.Observe(FightStartLine("Someone is about to attack you."), T0);
        tracker.Observe(Line("Someone has started to use something to fight!"), T0.AddSeconds(2));
        tracker.Observe(Line("Someone hits you (87/110)."), T0.AddSeconds(3));
        tracker.Observe(Line("Someone hits you (69/110)."), T0.AddSeconds(10));
        tracker.Observe(Line("Someone hits you (44/110)."), T0.AddSeconds(12));
        tracker.Observe(Line("You miss someone."), T0.AddSeconds(12));
        tracker.Observe(Line("Kickflip the hero has suddenly become visible!"), T0.AddSeconds(12));
        tracker.Observe(Line("Kickflip the hero has eaten a wafer2."), T0.AddSeconds(14));
        Assert.Equal(1, tracker.Unseen.Someone);

        tracker.Observe(Line("Kickflip the hero hits you (1/110)."), T0.AddSeconds(18));

        Assert.Equal(0, tracker.Unseen.Someone);
        Assert.Contains(seen, e => e.Kind == CombatEventKind.UnseenNamed);
        Assert.Equal("Kickflip", seen.Last(e => e.Kind == CombatEventKind.HitByNpc).NpcName);
    }

    [Theory]
    [InlineData("Kickflip the hero has suddenly become visible!")]
    [InlineData("Kickflip the hero has regained his visibleness!")]
    public void EitherVisibilityWording_ClearsTheFade(string visibleLine)
    {
        var (tracker, seen) = Opened();
        tracker.Observe(Line("Kickflip the warrior hits you (83/110)."), T0.AddSeconds(1));
        tracker.Observe(Line("Kickflip the warrior fades from view."), T0.AddSeconds(2));
        tracker.Observe(Line("Someone hits you (60/110)."), T0.AddSeconds(3));
        Assert.Equal("Kickflip", seen.Last(e => e.Kind == CombatEventKind.HitByNpc).NpcName);

        tracker.Observe(Line(visibleLine), T0.AddSeconds(4));
        tracker.Observe(Line("Someone hits you (50/110)."), T0.AddSeconds(5));

        Assert.Equal(AnonymousOpponent.Person, seen.Last(e => e.Kind == CombatEventKind.HitByNpc).NpcName);
    }

    [Fact]
    public void ACreatureBecomingVisible_ByTheSecondWording_ClearsItsFadeToo()
    {
        var (tracker, seen) = Fresh();
        tracker.Observe(Line("You attack the man."), T0);
        tracker.Observe(Line("The man fades from view."), T0.AddSeconds(1));
        tracker.Observe(Line("Someone hits you (40/61)."), T0.AddSeconds(2));
        Assert.Equal("man", seen.Last(e => e.Kind == CombatEventKind.HitByNpc).NpcName);

        tracker.Observe(Line("The man has suddenly become visible!"), T0.AddSeconds(3));
        tracker.Observe(Line("Someone hits you (30/61)."), T0.AddSeconds(4));

        Assert.Equal(AnonymousOpponent.Person, seen.Last(e => e.Kind == CombatEventKind.HitByNpc).NpcName);
    }

    // ---- The operator's three dark-room scenarios --------------------------------------------

    private static List<string?> HitNames(List<CombatEvent> seen)
        => seen.Where(e => e.Kind == CombatEventKind.HitByNpc).Select(e => e.NpcName).ToList();

    /// <summary>A man and a rat both rush the operator; the room goes dark; a third opponent
    /// announces itself; light returns. Each blow is attributed exactly when one candidate remains.</summary>
    [Fact]
    public void DarkRoom_ManAndRat_ThenAThirdUnknown_ThenLight()
    {
        var (tracker, seen) = FreshKnowingManAndRat();
        tracker.Observe(FightStartLine("The man is rushing at you furiously."), T0);
        tracker.Observe(FightStartLine("The rat1 is rushing at you furiously."), T0);
        tracker.NoteCannotSee(true, T0.AddSeconds(1), "dark");

        tracker.Observe(Line("Something hits you (95/110)."), T0.AddSeconds(2));   // rat1
        tracker.Observe(Line("Someone hits you (85/110)."), T0.AddSeconds(3));     // the man
        tracker.Observe(FightStartLine("Something is about to attack you."), T0.AddSeconds(4));
        tracker.Observe(Line("Something hits you (80/110)."), T0.AddSeconds(5));   // rat1 or the unknown
        tracker.NoteCannotSee(false, T0.AddSeconds(6), "light");
        tracker.Observe(Line("The man hits you (75/110)."), T0.AddSeconds(7));
        tracker.Observe(Line("The rat1 hits you (65/110)."), T0.AddSeconds(8));
        tracker.Observe(Line("The rat4 hits you (60/110)."), T0.AddSeconds(9));

        Assert.Equal(
            new string?[] { "rat1", "man", "something", "man", "rat1", "rat4" },
            HitNames(seen));
        Assert.Equal(0, tracker.Unseen.Something);
        Assert.Contains(seen, e => e.Kind == CombatEventKind.UnseenNamed && e.NpcName == AnonymousOpponent.Thing);
    }

    /// <summary>Two somethings and a someone announce in the dark; the named kill of the rat thins
    /// the somethings to one; light names it.</summary>
    [Fact]
    public void DarkRoom_TwoSomethings_AKillThinsThem_LightNamesTheLast()
    {
        var (tracker, seen) = FreshKnowingManAndRat();
        tracker.NoteCannotSee(true, T0, "dark");
        tracker.Observe(FightStartLine("Someone is about to attack you."), T0.AddSeconds(1));
        tracker.Observe(FightStartLine("Something is about to attack you."), T0.AddSeconds(2));
        tracker.Observe(FightStartLine("Something is about to attack you."), T0.AddSeconds(3));
        Assert.Equal(2, tracker.Unseen.Something);

        tracker.Observe(Line("Something hits you (95/110)."), T0.AddSeconds(4));   // two candidates
        tracker.Observe(Line("You have killed the rat1."), T0.AddSeconds(5));
        Assert.Equal(1, tracker.Unseen.Something);
        tracker.Observe(Line("Something hits you (85/110)."), T0.AddSeconds(6));   // one candidate, unnamed
        tracker.NoteCannotSee(false, T0.AddSeconds(7), "light");
        tracker.Observe(Line("The rat4 hits you (80/110)."), T0.AddSeconds(8));

        Assert.Equal(0, tracker.Unseen.Something);
        Assert.Equal(1, tracker.Unseen.Someone);
        Assert.Contains(seen, e => e.Kind == CombatEventKind.UnseenNamed && e.NpcName == AnonymousOpponent.Thing);
        Assert.Equal("rat4", seen.Last(e => e.Kind == CombatEventKind.HitByNpc).NpcName);
    }

    /// <summary>One someone and one something in the dark, light returns, the man hits: the
    /// someone was the man.</summary>
    [Fact]
    public void DarkRoom_OneSomeoneOneSomething_LightNamesBoth()
    {
        var (tracker, seen) = FreshKnowingManAndRat();
        tracker.NoteCannotSee(true, T0, "dark");
        tracker.Observe(FightStartLine("Someone is about to attack you."), T0.AddSeconds(1));
        tracker.Observe(FightStartLine("Something is about to attack you."), T0.AddSeconds(2));
        tracker.NoteCannotSee(false, T0.AddSeconds(5), "light");

        tracker.Observe(Line("The man hits you (75/110)."), T0.AddSeconds(6));
        tracker.Observe(Line("The rat1 misses you."), T0.AddSeconds(7));

        Assert.Equal(0, tracker.Unseen.Someone);
        Assert.Equal(0, tracker.Unseen.Something);
        Assert.Equal(2, seen.Count(e => e.Kind == CombatEventKind.UnseenNamed));
    }

    [Fact]
    public void APlayerInTheDark_KeepsTheirName_ForTheAnonymousBlowsThatFollow()
    {
        var (tracker, seen) = Opened();
        tracker.Observe(Line("Kickflip the warrior hits you (83/110)."), T0.AddSeconds(1));

        tracker.NoteCannotSee(true, T0.AddSeconds(2), "dark");
        tracker.Observe(Line("Someone hits you (60/110)."), T0.AddSeconds(3));

        Assert.Equal("Kickflip", seen.Last(e => e.Kind == CombatEventKind.HitByNpc).NpcName);
        Assert.Equal(0, tracker.Unseen.Someone);
    }

    [Fact]
    public void CreatureLines_AreStillNamedWithoutTheArticle()
    {
        var tracker = new CombatTracker();
        var seen = new List<CombatEvent>();
        tracker.EventOccurred += seen.Add;

        tracker.Observe(Line("You attack the zombie4."), T0);
        tracker.Observe(Line("The zombie4 hits you (90/110)."), T0.AddSeconds(1));

        Assert.All(seen.Where(e => e.NpcName is not null), e => Assert.Equal("zombie4", e.NpcName));
    }
}
