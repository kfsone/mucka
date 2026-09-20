using System.Text;
using MudSharp.Session;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// The in-combat inventory probe: a drop or a take during a fight is answered with one
/// <c>FES,FEI</c> interrupt, debounced so a burst costs one, guarded so it does not land on a
/// combat-tick boundary, and carried out in front of a player command when one is dispatched inside
/// the window.
///
/// <para>Timings are shortened; assertions poll rather than assuming exact timer firing, the same
/// way <see cref="StaleProbeTests"/> does.</para>
/// </summary>
public class InventoryProbeTests : IDisposable
{
    private const string InvProbe  = "\x1b-[FES,FEI\x1b-]";
    private const string FullProbe = "\x1b-[FES,FEW,FEI\x1b-]";

    private static readonly byte[] GameModeEntry = [0x9D, 0x9C, 0xFF, 0xFF];

    private readonly MudSession _session;
    private readonly List<string> _outgoing = new();
    private readonly object _lock = new();

    public InventoryProbeTests()
    {
        _session = new MudSession(new MudSessionOptions
        {
            FesHeartbeatInterval    = TimeSpan.FromSeconds(60),   // far enough away not to interfere
            // Also parked out of the way. Every line these tests feed is un-coded, so the parser's
            // generic Inventory hint arms the stale probe too; leaving it at its production delay
            // would have these tests measuring whichever timer happened to win rather than this one.
            // The interaction between them is its own test, below.
            StaleProbeDelay         = TimeSpan.FromSeconds(30),
            MinProbeSpacing         = TimeSpan.FromMilliseconds(50),
            InventoryProbeDebounce  = TimeSpan.FromMilliseconds(80),
        });
        _session.OutgoingBytes += b => { lock (_lock) _outgoing.Add(Encoding.Latin1.GetString(b)); };
    }

    public void Dispose()
    {
        _session.Dispose();
        GC.SuppressFinalize(this);
    }

    private void Feed(string ascii) => _session.Feed(Encoding.Latin1.GetBytes(ascii));

    private List<string> Sent()
    {
        lock (_lock) return _outgoing.ToList();
    }

    private int CountContaining(string needle)
        => Sent().Count(o => o.Contains(needle, StringComparison.Ordinal));

    private static bool WaitFor(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return condition();
    }

    /// <summary>Enter game mode, start a fight, and wait past MinProbeSpacing from the entry probe.</summary>
    private void EnterCombat()
    {
        _session.Feed(GameModeEntry);
        Assert.True(_session.InGameMode);
        Assert.Equal(1, CountContaining(FullProbe));   // game-entry heartbeat
        Feed("You attack the rat17 with the axe0.\r\n");
        Assert.True(_session.InCombat);
        Thread.Sleep(120);
        lock (_lock) _outgoing.Clear();
    }

    [Fact]
    public void ADropInCombat_SendsOneFesFeiProbe()
    {
        EnterCombat();
        Feed("Axe0 dropped.\r\n");
        Assert.True(WaitFor(() => CountContaining(InvProbe) >= 1));
        Thread.Sleep(200);
        Assert.Equal(1, CountContaining(InvProbe));
    }

    [Fact]
    public void ABurstOfDrops_CostsOneProbeNotOnePerItem()
    {
        // A bulk command delivers every line in one server frame, three items inside the same
        // millisecond. Each line restarts the quiet period, so one probe follows the last of them.
        EnterCombat();
        Feed("Clover dropped.\r\nBriefcase dropped.\r\nCarpet0 dropped.\r\nCoracle dropped.\r\n");
        Assert.True(WaitFor(() => CountContaining(InvProbe) >= 1));
        Thread.Sleep(300);
        Assert.Equal(1, CountContaining(InvProbe));
    }

    [Fact]
    public void ContainerMoves_AlsoProbe()
    {
        // A container decouples the two burdens - contents levy no dexterity cost but their weight
        // still counts - so moving an item into or out of one is the cleanest observation of the
        // burdens coming apart, and it must not be missed.
        EnterCombat();
        Feed("Baton inserted in glass bottle6.\r\n");
        Assert.True(WaitFor(() => CountContaining(InvProbe) >= 1));
    }

    [Fact]
    public void OutOfCombat_ADropDoesNotProbe()
    {
        // The routine heartbeat is soon enough outside a fight, and a player emptying a hoard would
        // otherwise spend a tick per item.
        _session.Feed(GameModeEntry);
        Thread.Sleep(120);
        lock (_lock) _outgoing.Clear();

        Assert.False(_session.InCombat);
        Feed("Axe0 dropped.\r\n");
        Thread.Sleep(300);
        Assert.Equal(0, CountContaining(InvProbe));
    }

    [Fact]
    public void ARefusedDrop_DoesNotProbe()
    {
        // "The starfish is embroiled in combat and can't be dropped." is the one line that means
        // nothing moved, and it is the near-miss the item pattern's word cap exists to reject.
        EnterCombat();
        Feed("The starfish is embroiled in combat and can't be dropped.\r\n");
        Thread.Sleep(300);
        Assert.Equal(0, CountContaining(InvProbe));
    }

    [Fact]
    public void APlayerCommandInsideTheWindow_CarriesTheProbeOutInFrontOfIt()
    {
        EnterCombat();
        Feed("Axe0 dropped.\r\n");
        _session.SendLine("k rat17");   // dispatched well inside the 80ms window

        var combined = Assert.Single(Sent(), o => o.Contains(InvProbe, StringComparison.Ordinal));
        Assert.Equal(InvProbe + "k rat17\r\n", combined);

        // And the pending probe was consumed, not duplicated by the timer afterwards.
        Thread.Sleep(300);
        Assert.Equal(1, CountContaining(InvProbe));
    }

    [Fact]
    public void ACommaComboIsNeverPrependedTo_SoItsFirstElementKeepsItsTick()
    {
        // A probe in front of "e,feed coal to dragon" pushes every element back one tick, which is
        // exactly how that combo is known to fail.
        EnterCombat();
        Feed("Axe0 dropped.\r\n");
        _session.SendLine("e,feed coal to dragon");

        Assert.Contains("e,feed coal to dragon\r\n", Sent());
        Assert.DoesNotContain(Sent(), o => o.StartsWith(InvProbe, StringComparison.Ordinal) && o.Length > InvProbe.Length);

        // The probe is not lost - it follows on its own timer, by which point it is BEHIND the
        // combo in the server's queue.
        Assert.True(WaitFor(() => Sent().Contains(InvProbe)));
    }

    [Fact]
    public void AGenericProbeSentAfterTheChange_MakesThePendingOneRedundant()
    {
        // Combat prints un-coded lines constantly, so the parser's generic Inventory hint often has
        // the stale timer already part-way through its delay when a drop lands. If that probe goes
        // out AFTER the drop line, its reply is post-drop and the pending one would be a second tick
        // spent on the same question. Here the stale delay is short enough to win the race.
        using var session = new MudSession(new MudSessionOptions
        {
            FesHeartbeatInterval   = TimeSpan.FromSeconds(60),
            StaleProbeDelay        = TimeSpan.FromMilliseconds(30),
            MinProbeSpacing        = TimeSpan.FromMilliseconds(20),
            InventoryProbeDebounce = TimeSpan.FromMilliseconds(300),
        });
        var sent = new List<string>();
        var gate = new object();
        session.OutgoingBytes += b => { lock (gate) sent.Add(Encoding.Latin1.GetString(b)); };

        session.Feed(GameModeEntry);
        session.Feed(Encoding.Latin1.GetBytes("You attack the rat17 with the axe0.\r\n"));
        Thread.Sleep(150);
        lock (gate) sent.Clear();

        session.Feed(Encoding.Latin1.GetBytes("Axe0 dropped.\r\n"));
        Thread.Sleep(800);   // past the stale delay AND past the inventory debounce

        lock (gate)
            Assert.Equal(1, sent.Count(o => o == InvProbe));
    }

    [Fact]
    public void WhenTheNextTickIsInsideTheGuard_TheProbeIsDeferredPastIt()
    {
        // The tick guard: a probe landing immediately before a boundary competes for the slot the
        // player's own action wanted, so it is moved to the far side. Here the boundary is always
        // "150ms away", so the probe must wait at least 150 + 50 rather than the plain 80.
        using var session = new MudSession(new MudSessionOptions
        {
            FesHeartbeatInterval        = TimeSpan.FromSeconds(60),
            StaleProbeDelay             = TimeSpan.FromSeconds(30),
            MinProbeSpacing             = TimeSpan.FromMilliseconds(50),
            InventoryProbeDebounce      = TimeSpan.FromMilliseconds(80),
            InventoryProbeTickGuard     = TimeSpan.FromMilliseconds(200),
            InventoryProbeTickClearance = TimeSpan.FromMilliseconds(50),
        })
        {
            MillisecondsToNextCombatTick = () => 150,
        };
        var sent = new List<(long At, string Text)>();
        var gate = new object();
        session.OutgoingBytes += b => { lock (gate) sent.Add((Environment.TickCount64, Encoding.Latin1.GetString(b))); };

        session.Feed(GameModeEntry);
        session.Feed(Encoding.Latin1.GetBytes("You attack the rat17 with the axe0.\r\n"));
        Thread.Sleep(120);
        lock (gate) sent.Clear();

        var droppedAt = Environment.TickCount64;
        session.Feed(Encoding.Latin1.GetBytes("Axe0 dropped.\r\n"));

        var deadline = Environment.TickCount64 + 2000;
        (long At, string Text) probe = default;
        while (Environment.TickCount64 < deadline)
        {
            lock (gate) probe = sent.FirstOrDefault(s => s.Text == InvProbe);
            if (probe.Text is not null) break;
            Thread.Sleep(5);
        }
        Assert.NotNull(probe.Text);
        // Generous lower bound: timers only ever fire late, so the only thing worth asserting is
        // that it did NOT fire at the un-guarded 80ms.
        Assert.True(probe.At - droppedAt >= 150,
            $"probe fired {probe.At - droppedAt}ms after the drop; the tick guard should have held it past 150ms");
    }
}
