using Mucka.Ui;

namespace Mucka.Util.Tests;

/// <summary>
/// The Carrying and Here lists' arrival tracking: what counts as an arrival, and the order the
/// list is shown in. The worked example is the operator's: walk in, drop the shell, take the sack.
/// </summary>
public sealed class ArrivalLedgerTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static string[] Names(IReadOnlyList<ArrivalSlot> rows) => rows.Select(r => r.Name).ToArray();

    [Fact]
    public void FirstListIsABaseline_NothingArrived_GameOrderKept()
    {
        var ledger = new ArrivalLedger();
        var rows = ledger.Reconcile(["ring", "sack"], T0);

        Assert.Equal(["ring", "sack"], Names(rows));
        Assert.All(rows, r => Assert.Null(r.ArrivedUtc));
    }

    [Fact]
    public void NewObjectArrivesOnTop_AndOthersKeepGameOrder()
    {
        var ledger = new ArrivalLedger();
        ledger.Reconcile(["ring", "sack"], T0);

        var rows = ledger.Reconcile(["ring", "sack", "shell"], T0.AddSeconds(5));

        Assert.Equal(["shell", "ring", "sack"], Names(rows));
        Assert.Equal(T0.AddSeconds(5), rows[0].ArrivedUtc);
        Assert.Null(rows[1].ArrivedUtc);
    }

    [Fact]
    public void NewestArrivalIsAboveOlderArrival()
    {
        var ledger = new ArrivalLedger();
        ledger.Reconcile(["axe"], T0);
        ledger.Reconcile(["axe", "shell"], T0.AddSeconds(1));
        var rows = ledger.Reconcile(["axe", "shell", "sack"], T0.AddSeconds(3));

        Assert.Equal(["sack", "shell", "axe"], Names(rows));
    }

    [Fact]
    public void UnchangedList_ReturnsTheSameSlotInstances()
    {
        var ledger = new ArrivalLedger();
        var first = ledger.Reconcile(["axe", "bottle"], T0);
        var second = ledger.Reconcile(["axe", "bottle"], T0.AddSeconds(1));

        Assert.Same(first[0], second[0]);
        Assert.Same(first[1], second[1]);
    }

    [Fact]
    public void ArrivalFallsBackToGameOrderAfterPinWindow()
    {
        var ledger = new ArrivalLedger();
        ledger.Reconcile(["ring", "sack"], T0);
        ledger.Reconcile(["ring", "sack", "shell"], T0.AddSeconds(1));

        var pinned = ledger.Reconcile(["ring", "sack", "shell"], T0.AddSeconds(1) + ArrivalLedger.PinFor - TimeSpan.FromMilliseconds(1));
        var aged = ledger.Reconcile(["ring", "sack", "shell"], T0.AddSeconds(1) + ArrivalLedger.PinFor);

        Assert.Equal(["shell", "ring", "sack"], Names(pinned));
        Assert.Equal(["ring", "sack", "shell"], Names(aged));
    }

    [Fact]
    public void ResetMakesTheNextListABaseline()
    {
        var ledger = new ArrivalLedger();
        ledger.Reconcile(["ring"], T0);
        ledger.Reconcile(["ring", "sack"], T0.AddSeconds(1));

        ledger.Reset();
        var rows = ledger.Reconcile(["ox", "stick"], T0.AddSeconds(2));

        Assert.Equal(["ox", "stick"], Names(rows));
        Assert.All(rows, r => Assert.Null(r.ArrivedUtc));
    }

    [Fact]
    public void ObjectThatLeavesAndReturnsArrivesAgain()
    {
        var ledger = new ArrivalLedger();
        ledger.Reconcile(["axe", "shell"], T0);
        ledger.Reconcile(["axe"], T0.AddSeconds(1));
        var rows = ledger.Reconcile(["axe", "shell"], T0.AddSeconds(2));

        Assert.Equal(["shell", "axe"], Names(rows));
        Assert.Equal(T0.AddSeconds(2), rows[0].ArrivedUtc);
    }

    [Fact]
    public void DuplicateNames_OneLeaves_TheOldestIsTheOneDropped()
    {
        var ledger = new ArrivalLedger();
        ledger.Reconcile(["coin"], T0);
        var two = ledger.Reconcile(["coin", "coin"], T0.AddSeconds(1));
        var newer = two.Single(s => s.ArrivedUtc is not null);

        var one = ledger.Reconcile(["coin"], T0.AddSeconds(2));

        Assert.Same(newer, Assert.Single(one));
    }

    [Fact]
    public void GlowOpacity_StartsBright_HoldsThroughTheFirstHalf_EndsClear()
    {
        Assert.Equal(1f, ArrivalLedger.GlowOpacity(TimeSpan.Zero));
        Assert.True(ArrivalLedger.GlowOpacity(ArrivalLedger.GlowFor / 4) > 0.9f);
        Assert.True(ArrivalLedger.GlowOpacity(ArrivalLedger.GlowFor / 2) > 0.8f);
        Assert.Equal(0f, ArrivalLedger.GlowOpacity(ArrivalLedger.GlowFor));
        Assert.Equal(0f, ArrivalLedger.GlowOpacity(ArrivalLedger.GlowFor * 3));
    }

    [Fact]
    public void OperatorsWalkthrough()
    {
        var carrying = new ArrivalLedger();
        var here = new ArrivalLedger();

        // Walk into a room.
        Assert.Equal(["axe", "shell", "bottle"], Names(carrying.Reconcile(["axe", "shell", "bottle"], T0)));
        Assert.Equal(["ring", "sack"], Names(here.Reconcile(["ring", "sack"], T0)));

        // Drops the shell: carrying loses it, here gains it on top.
        var t = T0.AddSeconds(3);
        Assert.Equal(["axe", "bottle"], Names(carrying.Reconcile(["axe", "bottle"], t)));
        Assert.Equal(["shell", "ring", "sack"], Names(here.Reconcile(["ring", "sack", "shell"], t)));

        // Takes the sack and leaves: carrying glows the sack; here is a new room, a baseline.
        t = T0.AddSeconds(5);
        var carried = carrying.Reconcile(["axe", "bottle", "sack"], t);
        Assert.Equal(["sack", "axe", "bottle"], Names(carried));
        Assert.NotNull(carried[0].ArrivedUtc);
        here.Reset();
        var room = here.Reconcile(["ox", "stick"], t);
        Assert.Equal(["ox", "stick"], Names(room));
        Assert.All(room, r => Assert.Null(r.ArrivedUtc));
    }
}
