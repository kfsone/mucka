using MudSharp.Combat;
using Xunit.Abstractions;

namespace Mucka.Util.Tests;

/// <summary>
/// <see cref="UnseenBook"/> against the operator's worked examples in docs/unseen-presentation.md,
/// then against a randomised ground-truth world.
/// </summary>
public sealed class UnseenBookTests(ITestOutputHelper output)
{
    private static readonly UnseenActor One = UnseenActor.Anonymous(SomeKind.Someone);
    private static readonly UnseenActor Thing = UnseenActor.Anonymous(SomeKind.Something);
    private static UnseenActor N(string name) => UnseenActor.Named(name);

    private static UnseenBook Book(params (string Name, SomeKind Class)[] known)
    {
        var map = known.ToDictionary(k => k.Name, k => k.Class);
        return new UnseenBook(n => map.TryGetValue(n, out var k) ? k : null);
    }

    private static string Show(UnseenBadge b) => b.Kind switch
    {
        UnseenBadgeKind.Group => $"[unseen{(b.Name is null ? "" : " " + b.Name)}{(b.Score is { } s ? $" +{s}" : "")}{(b.Outcome is { } o ? " " + o : "")}]",
        UnseenBadgeKind.Conclusion => $"[unseen{(b.Name is null ? "" : " " + b.Name)}{(b.Score is { } s ? $" +{s}" : "")} {b.Outcome}]",
        UnseenBadgeKind.Row => $"[{b.Name}{(b.Score is { } s ? $" +{s}" : "")}{(b.Outcome is { } o ? " " + o : "")}]",
        _ => $"[unseen x{b.Participants} ()x{b.Flights}{(b.Score is { } s ? $" +{s}" : "")}]",
    };

    /// <summary>The scores announced for each ending, as the dead strip's award pairing would supply
    /// them.</summary>
    private readonly Dictionary<(UnseenBook, int), int> _scores = [];

    private Func<int, int?> ScoreOf(UnseenBook book) => id => _scores.TryGetValue((book, id), out var s) ? s : null;

    private string History(UnseenBook book) => string.Join(" ", book.Badges(ScoreOf(book)).Select(Show));

    /// <summary>The rail: the unseen group with its count when anyone is unseen, then the rows.</summary>
    private static string Rail(UnseenBook book)
        => string.Join(" ", (book.UnseenCount > 0 ? [$"[? x{book.UnseenCount}]"] : Array.Empty<string>())
            .Concat(book.Rows.Where(r => r is { Open: true, Parked: false }).Select(r => $"[{r.Name}]")));

    private int Kill(UnseenBook book, UnseenActor who, int score, UnseenOutcome outcome = UnseenOutcome.Killed)
    {
        var id = book.End(who, outcome);
        Assert.True(id >= 0);
        _scores[(book, id)] = score;
        return id;
    }

    [Fact]
    public void NobodyUnseen_RowsOnly()
    {
        var book = Book();
        book.Start(N("rat0"));
        book.Swing(N("rat0"));
        Kill(book, N("rat0"), 12);
        Assert.Equal("[rat0 +12 Killed]", History(book));
        Assert.Equal(0, book.UnseenCount);
    }

    [Fact]
    public void TenRailSteps()
    {
        var book = Book(("rat", SomeKind.Something), ("old man", SomeKind.Someone));
        // 1. Rat attacks you.
        book.Start(N("rat"));
        book.Swing(N("rat"));
        Assert.Equal("[rat]", Rail(book));
        // 2. Invisible old man attacks: a someone start does not park a rat.
        book.Start(One);
        Assert.Equal("[? x1] [rat]", Rail(book));
        // 3. You invis the rat.
        book.Faded("rat");
        Assert.Equal("[? x2]", Rail(book));
        // 4. The rat loses invisibility: the visibility line alone changes nothing; the swing brings
        // the row back with the stats it held.
        book.SightRegained();
        Assert.Equal("[? x2]", Rail(book));
        book.Swing(N("rat"));
        Assert.Equal("[? x1] [rat]", Rail(book));
        // 5. It goes dark.
        book.SightLost();
        Assert.Equal("[? x2]", Rail(book));
        // 6. The old man becomes visible, unseen by the player; 7. light returns, no swings yet.
        book.SightRegained();
        Assert.Equal("[? x2]", Rail(book));
        // 8. A swing at the old man by name: a new row, spending the someone slot.
        book.Swing(N("old man"));
        Assert.Equal("[? x1] [old man]", Rail(book));
        // 9. A swing from the rat, a hit from Someone.
        book.Swing(N("rat"));
        book.Swing(One);
        Assert.Equal("[? x1] [rat]", Rail(book));
        // 10. The old man hits again by name.
        book.Swing(N("old man"));
        Assert.Equal("[rat] [old man]", Rail(book));
        Assert.Equal(1, book.GroupBlows);
    }

    [Fact]
    public void DarkSnake_KilledAnonymously_FoldsIntoOneBadge()
    {
        var book = Book(("snake", SomeKind.Something));
        book.SightLost();
        book.Start(Thing);
        book.Swing(Thing);
        book.Swing(Thing);
        Kill(book, Thing, 86);
        Assert.Equal("[unseen +86 Killed]", History(book));
        Assert.Equal(0, book.UnseenCount);
    }

    [Fact]
    public void DarkSnake_KilledByName_IsTheSlotsConclusionWithTheName()
    {
        var book = Book(("snake", SomeKind.Something));
        book.SightLost();
        book.Start(Thing);
        book.Swing(Thing);
        Kill(book, N("snake"), 86);
        Assert.Equal("[unseen snake +86 Killed]", History(book));
        Assert.Null(book.Row("snake"));
    }

    [Fact]
    public void NoFold_ACountedRowKeepsItsOwnBadge_AndTheGroupTheScore()
    {
        var book = Book();
        book.Start(N("rat"));
        book.Swing(N("rat"));
        book.SightLost();
        Kill(book, One, 7);
        Assert.True(book.Row("rat") is { ByCounting: true, Outcome: UnseenOutcome.Killed });
        Assert.Equal("[unseen +7] [rat Killed]", History(book));
    }

    [Fact]
    public void NamedEnding_NoRowNoAvailableSlot_ConcludesNothing()
    {
        var book = Book();
        book.Start(N("rat0"));
        book.Swing(N("rat0"));
        var version = book.Version;
        Assert.Equal(-1, book.End(N("ogre"), UnseenOutcome.Died));
        Assert.Null(book.Row("ogre"));
        Assert.Equal(version, book.Version);
        Assert.Equal("[rat0]", History(book));
    }

    /// <summary>The slot is matched by a pending anonymous ending, so a name with no row has none
    /// left to conclude: a bystander's coded flight is not this fight's.</summary>
    [Fact]
    public void NamedEnding_OfASlotAPendingEndingMatches_ConcludesNothing()
    {
        var book = Book(("rat0", SomeKind.Something));
        book.Start(N("rat0"));
        book.Start(One);
        book.Start(One);
        book.End(One, UnseenOutcome.Fled);
        book.End(One, UnseenOutcome.Killed);
        Assert.Equal(0, book.AvailableSlots);
        Assert.Equal(-1, book.End(N("thief9"), UnseenOutcome.Fled));
        Assert.Null(book.Row("thief9"));
        Assert.Equal(2, book.PendingEndings);
    }

    [Fact]
    public void NamedEnding_NamedStartNoBlows_IsTheRowsEvenWithASlotOpen()
    {
        var book = Book(("ogre", SomeKind.Someone));
        book.Start(N("ogre"));
        book.Start(One);
        Kill(book, N("ogre"), 40, UnseenOutcome.Died);
        Assert.Equal(UnseenOutcome.Died, book.Row("ogre")!.Outcome);
        Assert.Equal(1, book.UnseenCount);
    }

    [Fact]
    public void TwoUnseens_SecondNeverNamed()
    {
        var book = Book(("rat0", SomeKind.Something));
        book.Start(One);
        book.Start(One);
        book.Start(N("rat0"));
        book.Swing(One);
        book.Swing(N("rat0"));
        Kill(book, One, 16);
        Kill(book, N("rat0"), 32);
        book.Swing(One);
        Kill(book, One, 22);
        Assert.Equal("[unseen] [unseen +16 Killed] [rat0 +32 Killed] [unseen +22 Killed]", History(book));
    }

    [Fact]
    public void TwoUnseens_SecondNamedBadger()
    {
        var book = Book(("rat0", SomeKind.Something), ("badger", SomeKind.Something));
        book.Start(One);
        book.Start(Thing);
        book.Start(N("rat0"));
        book.Swing(One);
        book.Swing(Thing);
        book.Swing(N("rat0"));
        Kill(book, One, 16);
        Kill(book, N("rat0"), 32);
        book.Swing(N("badger"));
        Kill(book, N("badger"), 22);
        Assert.Equal("[unseen] [unseen +16 Killed] [rat0 +32 Killed] [badger +22 Killed]", History(book));
    }

    private static UnseenBook Rats()
    {
        var book = Book(("rat0", SomeKind.Something), ("rat1", SomeKind.Something), ("rat2", SomeKind.Something));
        foreach (var r in new[] { "rat0", "rat1", "rat2" })
            book.Start(N(r));
        book.SightLost();
        book.Start(One);
        Assert.Equal(4, book.UnseenCount);
        return book;
    }

    [Fact]
    public void Rats_CountingHandsTheOutcomeToTheRows()
    {
        var book = Rats();
        for (var i = 0; i < 3; i++)
        {
            book.Swing(Thing);
            Kill(book, Thing, 5, UnseenOutcome.Died);
            Assert.Equal(3 - i, book.UnseenCount);
        }
        book.Swing(One);
        book.Swing(One);
        Kill(book, One, 10, UnseenOutcome.Died);
        var badges = book.Badges(ScoreOf(book));
        Assert.Equal("[unseen +15] [unseen +10 Died]", string.Join(" ", badges.Take(2).Select(Show)));
        Assert.Equal(["rat0", "rat1", "rat2"], badges.Skip(2).Select(b => b.Name!).Order());
        Assert.All(badges.Skip(2), b => Assert.True(b is { Counted: true, Score: null, Outcome: UnseenOutcome.Died }));
        Assert.Equal(0, book.UnseenCount);
    }

    [Fact]
    public void Rats_FewerEndingsThanParticipants_AssignNothing()
    {
        var book = Rats();
        Kill(book, Thing, 5, UnseenOutcome.Died);
        Kill(book, Thing, 5, UnseenOutcome.Died);
        Assert.Equal(2, book.UnseenCount);
        book.Close();
        Assert.Equal(0, book.UnseenCount);
        Assert.Equal("[unseen] [unseen +5 Died] [unseen +5 Died] [rat0 FateUnseen] [rat1 FateUnseen] [rat2 FateUnseen]",
            History(book));
    }

    [Fact]
    public void MixedOutcomes_AssignNothing()
    {
        var book = Book(("rat0", SomeKind.Something), ("rat1", SomeKind.Something));
        book.Start(N("rat0"));
        book.Start(N("rat1"));
        book.SightLost();
        Kill(book, Thing, 5);
        book.End(Thing, UnseenOutcome.Withdrew);
        book.Close();
        Assert.Equal("[unseen] [unseen +5 Killed] [unseen Withdrew] [rat0 FateUnseen] [rat1 FateUnseen]",
            History(book));
    }

    [Fact]
    public void PlainRoomWideEndings_NeverConcludeAParticipant()
    {
        var book = Book();
        book.Start(N("rat"));
        book.SightLost();
        Assert.Equal(-1, book.End(One, UnseenOutcome.Withdrew, ownFight: false));
        Assert.Equal(-1, book.End(Thing, UnseenOutcome.Died, ownFight: false));
        Assert.Equal(-1, book.End(N("rat"), UnseenOutcome.Died, ownFight: false));
        Assert.True(book.Row("rat")!.Open);
        Assert.Equal(1, book.UnseenCount);
        Assert.Empty(book.Conclusions);
    }

    [Fact]
    public void LeftoverEndings_GoOnlyToSlotsOfTheirOwnWord()
    {
        var book = Book(("rat", SomeKind.Something));
        book.Start(One);
        book.Start(N("rat"));
        book.SightLost();
        book.End(Thing, UnseenOutcome.Killed);
        book.End(Thing, UnseenOutcome.Killed);
        Assert.True(book.Row("rat")!.Open);
        Assert.Equal(2, book.PendingEndings);
    }

    [Fact]
    public void NamedEnding_OfAClosedRow_SpendsNoSlot()
    {
        var book = Book();
        book.Start(One);
        book.Start(N("rat"));
        book.Swing(N("rat"));
        book.End(N("rat"), UnseenOutcome.Killed);
        book.End(N("rat"), UnseenOutcome.Killed);
        Assert.Equal(1, book.UnseenCount);
    }

    [Fact]
    public void ClosedRowSwingingWithoutAStart_ReopensAndSpendsNoSlot()
    {
        var book = Book();
        book.Start(One);
        book.Start(N("rat"));
        book.End(N("rat"), UnseenOutcome.FleeFailed);
        book.Swing(N("rat"));
        Assert.Equal(1, book.UnseenCount);
        Assert.Equal(2, book.Rows.Count);
        Assert.True(book.Row("rat")!.Open);
    }

    [Fact]
    public void Restart_OpensAFreshRow_AndScoresStayWithTheirOwnFight()
    {
        var book = Book();
        book.Start(N("rat"));
        book.Swing(N("rat"));
        var first = book.End(N("rat"), UnseenOutcome.Withdrew);
        book.Start(N("rat"));
        _scores[(book, first)] = 3;
        book.Swing(N("rat"));
        Kill(book, N("rat"), 9);
        Assert.Equal("[rat +3 Withdrew] [rat +9 Killed]", History(book));
    }

    [Fact]
    public void SixUnseenBadges_LumpIntoOne_FiveDoNot()
    {
        foreach (var n in new[] { 4, 5 })
        {
            var book = Book(("rat0", SomeKind.Something));
            book.Start(N("rat0"));
            book.Swing(N("rat0"));
            for (var i = 0; i < n; i++)
                book.Start(One);
            for (var i = 0; i < n; i++)
            {
                book.Swing(One);
                Kill(book, One, 10);
            }
            Kill(book, N("rat0"), 3);
            var history = History(book);
            if (n == 4)
                Assert.Equal("[unseen] [unseen +10 Killed] [unseen +10 Killed] [unseen +10 Killed] [unseen +10 Killed] [rat0 +3 Killed]", history);
            else
                Assert.Equal("[unseen x5 ()x0 +50] [rat0 +3 Killed]", history);
        }
    }

    [Fact]
    public void Lump_TakesACountedRowAndItsScore()
    {
        var book = Book(("rat", SomeKind.Something));
        for (var i = 0; i < 5; i++)
            book.Start(One);
        book.Start(N("rat"));
        book.SightLost();
        for (var i = 0; i < 5; i++)
            Kill(book, One, 10);
        Kill(book, Thing, 4);
        Assert.True(book.Row("rat") is { ByCounting: true });
        Assert.Equal("[unseen x6 ()x0 +54]", History(book));
    }

    [Fact]
    public void Lump_CountsFlightsConcludedAgainstTheUnseen()
    {
        var book = Book();
        for (var i = 0; i < 6; i++)
            book.Start(One);
        for (var i = 0; i < 6; i++)
            book.End(N("man" + i), i < 4 ? UnseenOutcome.Fled : UnseenOutcome.FleeFailed);
        Assert.Equal(0, book.UnseenCount);
        Assert.Equal("[unseen x6 ()x6]", History(book));
    }

    [Fact]
    public void PvpSightInAndOut_RowResumes_NoMicroBadges()
    {
        var book = Book(("bob", SomeKind.Someone));
        book.Start(N("bob"));
        book.Swing(N("bob"));
        for (var i = 0; i < 10; i++)
        {
            book.SightLost();
            Assert.Equal(1, book.UnseenCount);
            book.Swing(One);
            book.Swing(One);
            book.SightRegained();
            Assert.Equal(1, book.UnseenCount);
            book.Swing(N("bob"));
            Assert.Equal(0, book.UnseenCount);
        }
        book.Close();
        Assert.Equal("[unseen] [bob]", History(book));
        Assert.Equal(20, book.GroupBlows);
    }

    [Fact]
    public void AnonymousLines_NeverCreditANamedRow()
    {
        var book = Book();
        book.Start(N("rat"));
        book.Swing(N("rat"));
        book.Swing(Thing);
        book.Swing(Thing);
        Assert.Equal(2, book.GroupBlows);
        Assert.True(book.Row("rat")!.Parked);
    }

    [Fact]
    public void VisibilitySignalsAndNamedStarts_NeverPromote()
    {
        var book = Book();
        book.Start(N("rat"));
        book.Faded("rat");
        book.SightRegained();
        book.Start(N("rat"));
        Assert.True(book.Row("rat")!.Parked);
        Assert.Equal(1, book.UnseenCount);
    }

    [Fact]
    public void MissedStartAndMissedFade_CostOnlyDisplay()
    {
        var book = Book(("rat", SomeKind.Something));
        book.Swing(N("rat"));
        Assert.Equal(0, book.UnseenCount);
        book.Swing(Thing);
        Assert.True(book.Row("rat")!.Parked);
        Assert.Equal(1, book.GroupBlows);
    }

    /// <summary>The thief's start is missed after a wake-up, the man starts, and "You have killed
    /// someone" is the thief's: without the wake-up rule counting hands it to the man. Slot spending
    /// stays on: a name with no row still spends an available slot.</summary>
    [Fact]
    public void WokenUp_TurnsCountingOff_AndLeavesSlotSpendingOn()
    {
        var book = Book(("man", SomeKind.Someone), ("thief", SomeKind.Someone));
        book.WokenUp();
        book.Start(N("man"));
        book.End(One, UnseenOutcome.Killed);
        Assert.True(book.Row("man") is { Open: true, ByCounting: false });

        var spend = Book(("man", SomeKind.Someone));
        spend.Start(One);
        spend.WokenUp();
        spend.Swing(N("man"));
        Assert.Equal(0, spend.UnseenCount);
    }

    /// <summary>Random seed 1045, shrunk: rat1 withdraws unseen, then starts again by name.</summary>
    [Fact]
    public void StartForAnOpenRowWhileEndingsPend_TurnsCountingOff()
    {
        var book = Book(("rat2", SomeKind.Something));
        book.Start(N("rat2"));
        book.Start(N("rat1"));
        book.SightLost();
        book.End(Thing, UnseenOutcome.Withdrew);
        book.SightRegained();
        book.Start(N("rat1"));
        book.End(N("rat2"), UnseenOutcome.Killed);
        Assert.True(book.Row("rat1") is { Open: true, ByCounting: false });
    }

    [Fact]
    public void EachBadgeNamesTheEndingsItSums_AndNoEndingTwice()
    {
        var book = Book(("rat", SomeKind.Something));
        book.Start(N("rat"));
        book.Start(One);
        book.SightLost();
        var slot = book.End(One, UnseenOutcome.Killed);
        var counted = book.End(Thing, UnseenOutcome.Killed);
        var badges = book.Badges();
        Assert.Equal([counted], badges[0].ScoredBy);
        Assert.Equal([slot], badges.Single(b => b.Kind == UnseenBadgeKind.Conclusion).ScoredBy);
        Assert.Equal(counted, badges.Single(b => b is { Kind: UnseenBadgeKind.Row, Counted: true }).PlacedBy);
        Assert.All(badges, b => Assert.Null(b.Score));
    }

    [Fact]
    public void AfterClose_InputsThrow()
    {
        var book = Book();
        book.Close();
        Assert.Throws<InvalidOperationException>(() => book.Start(One));
        Assert.Throws<InvalidOperationException>(() => book.Swing(One));
        Assert.Throws<InvalidOperationException>(() => book.End(One, UnseenOutcome.Killed));
        Assert.Throws<InvalidOperationException>(() => book.SightLost());
        Assert.Throws<InvalidOperationException>(() => book.Close());
        Assert.Throws<InvalidOperationException>(() => book.WokenUp());
    }

    [Fact]
    public void EmptyOrNullNames_Throw()
    {
        var book = Book();
        Assert.ThrowsAny<ArgumentException>(() => UnseenActor.Named(null!));
        Assert.ThrowsAny<ArgumentException>(() => UnseenActor.Named(""));
        Assert.ThrowsAny<ArgumentException>(() => book.Swing(new UnseenActor("", SomeKind.Someone)));
        Assert.ThrowsAny<ArgumentException>(() => book.Faded(null!));
    }

    [Fact]
    public void BlowsToAnOpenRow_AfterItsFirst_LeaveTheVersionAlone()
    {
        var book = Book(("rat", SomeKind.Something));
        book.Start(N("rat"));
        book.Swing(N("rat"));
        var version = book.Version;
        book.Swing(N("rat"));
        book.Swing(N("rat"));
        Assert.Equal(version, book.Version);
        book.Swing(Thing);
        Assert.NotEqual(version, version = book.Version);
        book.Swing(Thing);
        Assert.Equal(version, book.Version);
    }

    // Randomised ground truth.

    private enum Act
    {
        Start, PlayerStart, Swing, PlayerSwing, Kill, Died, Withdraw, Flee, FleeFail, Fade, Regain, Blind, Sighted,
        Bystander, Revive, Wake,
    }

    private readonly record struct Step(Act Kind, int Who, bool Hit, int Score);

    private static readonly (string Name, SomeKind Class)[] Cast =
    [
        ("man", SomeKind.Someone), ("thief", SomeKind.Someone),
        ("rat1", SomeKind.Something), ("rat2", SomeKind.Something), ("snake1", SomeKind.Something),
    ];

    private sealed class Creature(string name, SomeKind cls)
    {
        public readonly string Name = name;
        public readonly SomeKind Class = cls;
        public bool InRoom = true, Engaged, Invisible;
        public UnseenOutcome? LastEnd;
    }

    /// <summary>Starts the book never sees: none, or only after a wake-up line it is told
    /// about.</summary>
    private enum Missed { None, AfterWake }

    /// <summary>Counted: row outcomes counting assigned. Anonymous: own-fight anonymous endings fed.
    /// ToSlots: those counting placed on a slot. Unplaced: those never placed by the close.</summary>
    private sealed record Result(string? Failure, string Transcript, int Counted = 0, int Anonymous = 0, int ToSlots = 0, int Unplaced = 0);

    private static List<Step> Generate(int seed)
    {
        var rng = new Random(seed);
        var steps = new List<Step>();
        for (int i = 0, n = rng.Next(10, 60); i < n; i++)
        {
            var kind = rng.Next(100) switch
            {
                < 14 => Act.Start, < 19 => Act.PlayerStart, < 34 => Act.Swing, < 43 => Act.PlayerSwing,
                < 53 => Act.Kill, < 56 => Act.Died, < 62 => Act.Withdraw, < 66 => Act.Flee, < 69 => Act.FleeFail,
                < 76 => Act.Fade, < 80 => Act.Regain, < 86 => Act.Blind, < 90 => Act.Sighted,
                < 95 => Act.Bystander, < 98 => Act.Revive, _ => Act.Wake,
            };
            steps.Add(new Step(kind, rng.Next(Cast.Length), rng.Next(3) != 0, rng.Next(4) == 0 ? -1 : rng.Next(1, 60)));
        }
        return steps;
    }

    private static Result Run(IReadOnlyList<Step> steps, int seed, Missed missed)
    {
        var rng = new Random(seed ^ 0x5eed);
        var known = Cast.Where(_ => rng.Next(2) == 0).ToDictionary(c => c.Name, c => c.Class);
        var book = new UnseenBook(n => known.TryGetValue(n, out var k) ? k : null);
        var world = Cast.Select(c => new Creature(c.Name, c.Class)).ToList();
        var cannotSee = false;
        var woken = false;
        var log = new List<string> { "known: " + string.Join(",", known.Keys) };
        var named = new Dictionary<UnseenRow, int>();
        var anonymous = 0;
        var scored = 0;
        var scores = new Dictionary<int, int>();
        var checkedRows = new HashSet<UnseenRow>();
        var counted = 0;
        string? refused = null;

        UnseenActor As(Creature c) => cannotSee || c.Invisible ? UnseenActor.Anonymous(c.Class) : UnseenActor.Named(c.Name);
        string Say(UnseenActor a) => a.Name ?? (a.Word == SomeKind.Someone ? "someone" : "something");
        void Ending(Creature c, UnseenActor who, UnseenOutcome outcome, int score)
        {
            c.LastEnd = outcome;
            // A named ending for a name the book has no row for, with no slot available, concludes
            // nothing by rule; every other own-fight ending must be taken.
            var mustTake = who.Name is null || book.Row(who.Name) is not null;
            var id = book.End(who, outcome);
            log.Add($"End {Say(who)} {outcome} (truth {c.Name})");
            if (id < 0 && mustTake)
                refused ??= $"the book refused {c.Name}'s own-fight {outcome}";
            // Only the endings MUD2 scores carry an award (Mucka.Combat.KillAwardLedger.Awards): a
            // kill and a creature's flight, failed or not.
            if (score > 0 && id >= 0
                && outcome is UnseenOutcome.Killed or UnseenOutcome.Fled or UnseenOutcome.FleeFailed
                && scores.TryAdd(id, score))
            {
                scored += score;
                log.Add($"  score +{score}");
            }
            // The plain room line that can follow a coded kill.
            if (outcome == UnseenOutcome.Killed && score % 3 == 0)
            {
                book.End(who, UnseenOutcome.Died, ownFight: false);
                log.Add($"End {Say(who)} Died (plain)");
            }
        }

        foreach (var s in steps)
        {
            var c = world[s.Who];
            var who = As(c);
            switch (s.Kind)
            {
                case Act.Start or Act.PlayerStart when c.InRoom && !c.Engaged:
                    c.Engaged = true;
                    if (missed == Missed.AfterWake && woken && s.Score % 3 == 0)
                    {
                        log.Add($"(start of {c.Name} missed)");
                        break;
                    }
                    book.Start(who);
                    log.Add($"Start {Say(who)} (truth {c.Name})");
                    break;
                case Act.Swing or Act.PlayerSwing when c.InRoom && c.Engaged:
                    book.Swing(who);
                    if (who.Name is null) anonymous++;
                    else
                        named[book.Row(c.Name)!] = named.GetValueOrDefault(book.Row(c.Name)!) + 1;
                    log.Add($"Swing {Say(who)} {(s.Kind == Act.PlayerSwing ? "by you" : "at you")} (truth {c.Name})");
                    break;
                case Act.Kill or Act.Died when c.InRoom && c.Engaged:
                    c.InRoom = c.Engaged = false;
                    Ending(c, who, s.Kind == Act.Kill ? UnseenOutcome.Killed : UnseenOutcome.Died, s.Score);
                    break;
                case Act.Withdraw when c.InRoom && c.Engaged:
                    c.Engaged = false;
                    Ending(c, who, UnseenOutcome.Withdrew, s.Score);
                    break;
                case Act.Flee when c.InRoom && c.Engaged && who.Name is not null:
                    c.InRoom = c.Engaged = false;
                    Ending(c, who, UnseenOutcome.Fled, s.Score);
                    break;
                case Act.FleeFail when c.InRoom && c.Engaged && who.Name is not null:
                    c.Engaged = false;
                    Ending(c, who, UnseenOutcome.FleeFailed, s.Score);
                    break;
                case Act.Bystander when c.InRoom && !c.Engaged:
                    // NPCs fighting each other: plain room lines about a creature not fighting the player.
                    var outcome = (s.Score & 3) switch { 0 => UnseenOutcome.Died, 1 => UnseenOutcome.Fled, _ => UnseenOutcome.Withdrew };
                    book.End(who, outcome, ownFight: false);
                    log.Add($"End {Say(who)} {outcome} (plain, bystander {c.Name})");
                    if (outcome != UnseenOutcome.Withdrew)
                        c.InRoom = false;
                    break;
                case Act.Revive when !c.InRoom:
                    c.InRoom = true;
                    c.Invisible = false;
                    log.Add($"({c.Name} is back)");
                    break;
                case Act.Wake when missed == Missed.AfterWake && !woken:
                    woken = true;
                    book.WokenUp();
                    log.Add("WokenUp");
                    break;
                case Act.Fade when c.InRoom && !c.Invisible:
                    c.Invisible = true;
                    if (!cannotSee)
                    {
                        book.Faded(c.Name);
                        log.Add($"Faded {c.Name}");
                    }
                    else log.Add($"({c.Name} fades unwitnessed)");
                    break;
                case Act.Regain when c.InRoom && c.Invisible:
                    c.Invisible = false;
                    log.Add($"({c.Name} visible again)");
                    break;
                case Act.Blind when !cannotSee:
                    cannotSee = true;
                    book.SightLost();
                    log.Add("SightLost");
                    break;
                case Act.Sighted when cannotSee:
                    cannotSee = false;
                    book.SightRegained();
                    log.Add("SightRegained");
                    break;
            }
            var fail = refused;
            if (book.UnseenCount < 0)
                fail = "negative unseen count";
            foreach (var row in book.Rows)
            {
                if (!row.ByCounting || !checkedRows.Add(row))
                    continue;
                counted++;
                var truth = world.First(w => w.Name == row.Name);
                if (truth.Engaged || truth.LastEnd != row.Outcome)
                    fail ??= $"counting gave {row.Name} {row.Outcome}; truth engaged={truth.Engaged} last={truth.LastEnd}";
            }
            if (fail is not null)
                return new Result(fail, string.Join("\n", log));
        }
        var unplaced = book.PendingEndings;
        book.Close();
        log.Add("Close");
        string? failure = null;
        foreach (var row in book.Rows)
            if (row.HasSwung != named.ContainsKey(row))
                failure ??= $"{row.Name} swung={row.HasSwung}, {named.GetValueOrDefault(row)} lines named it";
        if (book.GroupBlows != anonymous)
            failure ??= $"group holds {book.GroupBlows} blows, {anonymous} anonymous lines fed";
        if (book.UnseenCount != 0)
            failure ??= "unseen count not zero at close";
        var badges = book.Badges(id => scores.TryGetValue(id, out var s) ? s : null);
        var unseenBadges = badges.Count(b => b.Kind != UnseenBadgeKind.Row || b.Counted);
        if (unseenBadges > UnseenBook.MaxUnseenBadges)
            failure ??= $"{unseenBadges} unseen badges";
        var shown = badges.Sum(b => b.Score ?? 0);
        if (shown != scored)
            failure ??= $"badges show {shown} points, {scored} attached";
        var anonymousEnds = book.Conclusions.Count(c => c.Name is null);
        return new Result(failure, string.Join("\n", log), counted, anonymousEnds,
            anonymousEnds - unplaced - book.Conclusions.Count(c => c.HandedToRow), unplaced);
    }

    private static List<Step> Shrink(List<Step> steps, int seed, Missed missed)
    {
        for (var changed = true; changed;)
        {
            changed = false;
            for (var i = 0; i < steps.Count && !changed; i++)
            {
                var without = steps.Where((_, ix) => ix != i).ToList();
                if (Run(without, seed, missed).Failure is not null)
                {
                    steps = without;
                    changed = true;
                }
            }
        }
        return steps;
    }

    /// <summary>Runs the seeds; returns the failures, the first one shrunk.</summary>
    private List<string> Fuzz(Missed missed, int seeds)
    {
        int counted = 0, anonymous = 0, toSlots = 0, unplaced = 0;
        var failures = new List<string>();
        for (var seed = 1; seed <= seeds; seed++)
        {
            var steps = Generate(seed);
            var r = Run(steps, seed, missed);
            if (r.Failure is not null)
            {
                if (failures.Count == 0)
                {
                    var small = Run(Shrink(steps, seed, missed), seed, missed);
                    failures.Add($"seed {seed}: {small.Failure}\nshrunk transcript:\n{small.Transcript}");
                }
                else
                    failures.Add($"seed {seed}: {r.Failure}");
                continue;
            }
            counted += r.Counted;
            anonymous += r.Anonymous;
            toSlots += r.ToSlots;
            unplaced += r.Unplaced;
        }
        output.WriteLine($"missed starts {missed}, {seeds} seeds, {failures.Count} failing: {counted} row outcomes assigned by counting; "
            + $"{anonymous} own-fight anonymous endings, {toSlots} placed on a slot by counting, {unplaced} never placed");
        return failures;
    }

    private const int Seeds = 10000;

    // What Run asserts, per seed: every outcome counting gives a row is that Creature's true last
    // ending and is never given while it still fights; every own-fight ending the book must take is
    // taken; the group holds exactly the anonymous blows fed and a row has swung exactly when a named
    // line named it; nobody is unseen after the close; at most MaxUnseenBadges unseen badges; the
    // badges show exactly the points attached. It does not check which row a pending ending was
    // about when counting places nothing.

    [Fact]
    public void RandomWorld_CountingIsSound()
    {
        var failures = Fuzz(Missed.None, Seeds);
        Assert.True(failures.Count == 0, failures.FirstOrDefault());
    }

    [Fact]
    public void RandomWorld_MissedStartsAfterAWakeUp_CountingIsSound()
    {
        var failures = Fuzz(Missed.AfterWake, Seeds);
        Assert.True(failures.Count == 0, failures.FirstOrDefault());
    }
}
