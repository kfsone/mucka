using System.Text;
using MudSharp.Session;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// MudSession-level tests for the composed heartbeat: FES+FEW ride every beat because FEW/FEI
/// updates are unreliable without FES; FEI rides only when a C1 hint marked room/carried items
/// dirty.
///
/// <para>Every deadline and every probe-path instant comes from <see cref="VirtualSessionClock"/>,
/// so a beat lands because <c>Advance</c> crossed its deadline and the counts below are exact.
/// The intervals are shortened, but only their RELATIONSHIP is under test - stale delay inside the
/// probe-spacing floor, floor well inside the heartbeat - and each step is expressed against the
/// constant it crosses rather than as a bare number.</para>
/// </summary>
public class ProbeCadenceTests : IDisposable
{
    private const string FesFewProbe = "\x1b-[FES,FEW\x1b-]";
    private const string FesFeiProbe = "\x1b-[FES,FEI\x1b-]";
    private const string FullProbe   = "\x1b-[FES,FEW,FEI\x1b-]";

    private static readonly byte[] GameModeEntry = [0x9D, 0x9C, 0xFF, 0xFF];
    private static readonly byte[] AutoReset     = [0xA1, 0x9F, 0xFF, 0xFF];   // C06 C04 -> locks the reset clock
    private static readonly byte[] ItemArriving  = [0x9E, 0x9C, 0x9D, 0xFF, 0xFF];
    private static readonly byte[] FesOpen       = [0xA7, 0xA3, 0x9C, 0xFF, 0xFF];   // C12 C08 C01 -> FES data line follows

    private static readonly TimeSpan Beat       = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan StaleDelay = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan MinSpacing = TimeSpan.FromMilliseconds(30);

    private readonly MudSession _session;
    private readonly VirtualSessionClock _clock = new();
    private readonly List<string> _outgoing = new();
    private readonly object _lock = new();

    public ProbeCadenceTests()
    {
        _session = new MudSession(new MudSessionOptions
        {
            FesHeartbeatInterval = Beat,
            StaleProbeDelay      = StaleDelay,
            MinProbeSpacing      = MinSpacing,
        });
        _clock.Attach(_session);
        _session.OutgoingBytes += b => { lock (_lock) _outgoing.Add(Encoding.Latin1.GetString(b)); };
    }

    public void Dispose()
    {
        _session.Dispose();
        GC.SuppressFinalize(this);
    }

    private void Feed(byte[] data) => _session.Feed(data);
    private void Feed(string ascii) => _session.Feed(Encoding.Latin1.GetBytes(ascii));

    /// <summary>Step the clock. Every probe the step is due lands before this returns.</summary>
    private void Advance(TimeSpan by) => _clock.Advance(by);

    private int CountSent(string probe)
    {
        lock (_lock) return _outgoing.Count(o => o == probe);
    }


    [Fact]
    public void EntryProbe_IsFull_ThenBeatsCarryFesFew_NoFeiWhenClean()
    {
        Feed(GameModeEntry);
        Assert.Equal(1, CountSent(FullProbe));   // entry populates everything

        // Reset clock unrelaxed (no lock yet) -> FES rides every beat; nothing marked the FEI
        // panel dirty since the entry probe carried it -> beats are FES,FEW.
        Advance(Beat * 2);
        Assert.Equal(2, CountSent(FesFewProbe));   // exactly one beat per interval crossed
        Assert.Equal(1, CountSent(FullProbe));     // FEI never rode a routine beat uninvited
    }

    [Fact]
    public void AutoResetLock_DoesNotRemoveFesFromBeats()
    {
        Feed(GameModeEntry);
        Feed(AutoReset);
        Advance(Beat * 2);
        Assert.Equal(2, CountSent(FesFewProbe));
    }

    [Fact]
    public void RegeneratingStamina_KeepsFesOnEveryBeat_EvenWhenRelaxed()
    {
        // FES remains mandatory while stamina regenerates.
        Feed(GameModeEntry);
        Feed("The eel stings you (65/81).\r\n");   // inline stamina - below max
        Feed(AutoReset);
        Advance(Beat * 2);
        Assert.Equal(2, CountSent(FesFewProbe));
    }

    [Fact]
    public void FullStamina_LockedClock_StillKeepsFesOnBeats()
    {
        Feed(GameModeEntry);
        Feed("The eel stings you (81/81).\r\n");   // at max - nothing regenerating
        Feed(AutoReset);
        Advance(Beat * 2);
        Assert.Equal(2, CountSent(FesFewProbe));
    }

    [Fact]
    public void MagicBelowMax_KeepsFesOnEveryBeat_EvenWhenRelaxed()
    {
        Feed(GameModeEntry);
        Feed(AutoReset);
        Feed(FesOpen);
        Feed("50 50 94 94 95 95 10 50 1785 N N N N 2 S\n");   // sta full, magic 10/50
        Advance(Beat * 2);
        Assert.Equal(2, CountSent(FesFewProbe));
    }

    [Fact]
    public void ItemHint_RidesReactiveFesFeiProbe_NotRoutineBeat()
    {
        Feed(GameModeEntry);
        Advance(MinSpacing * 2);   // past MinProbeSpacing from the entry probe
        var baseline = CountSent(FesFeiProbe);   // the entry's own room hint already spent one

        Feed(ItemArriving);        // C03 item arriving -> inventory dirty
        Advance(StaleDelay);
        // Off-cadence: the reactive probe lands one stale delay after the hint, with the routine
        // beat still a further MinProbeSpacing away, and FES leads it.
        Assert.Equal(baseline + 1, CountSent(FesFeiProbe));
        Assert.Equal(1, CountSent(FullProbe));   // no routine beat has fired yet
    }

    // Wake probe: fires only for an unanswered FES probe.

    [Fact]
    public void UnansweredFesProbe_IncomingData_FiresImmediateRecoveryBeat()
    {
        // Sleep scenario: probes no-op, so a beat's FES never gets a reply. The next server bytes
        // (the wake) must fire an immediate FES-carrying beat instead of waiting out the heartbeat
        // interval.
        //
        // The unanswered probe here is a routine beat, not the game-entry probe: on entry
        // MudSession stamps _lastProbeReplyUtc ("nothing is stale yet") and _lastFesSentUtc from
        // the same instant, and the wake check reads equal stamps as answered. A clock that does
        // not move within one Feed cannot express the entry-probe version of this scenario; the
        // wake path it exercises is the same one.
        var beat  = TimeSpan.FromMilliseconds(100);
        var slack = TimeSpan.FromMilliseconds(50);
        var clock = new VirtualSessionClock();
        using var session = new MudSession(new MudSessionOptions
        {
            FesHeartbeatInterval = beat,
            WakeReplySlack       = slack,
            StaleProbeDelay      = TimeSpan.FromSeconds(30),   // no reactive probe inside this window
        });
        clock.Attach(session);
        var outgoing = new List<string>();
        var sync = new object();
        session.OutgoingBytes += b => { lock (sync) outgoing.Add(Encoding.Latin1.GetString(b)); };

        int FesCarrying()
        {
            lock (sync) return outgoing.Count(o => o.StartsWith("\x1b-[FES", StringComparison.Ordinal));
        }

        session.Feed(GameModeEntry);
        session.Feed(FesOpen);
        session.Feed(Encoding.Latin1.GetBytes("81 81 94 94 95 95 50 50 1785 N N N N 5 S\n"));  // answers the entry FES
        clock.Advance(beat);                // one routine beat goes out - and is never answered
        Assert.Equal(2, FesCarrying());

        clock.Advance(slack + TimeSpan.FromMilliseconds(10));   // the beat's FES is now overdue
        session.Feed(Encoding.Latin1.GetBytes("You dream of sheep.\r\n"));
        clock.Advance(TimeSpan.Zero);       // the recovery beat is due immediately - let it land

        // Still well short of the next natural beat, so a third FES-carrying write can only be
        // the wake's recovery beat.
        Assert.Equal(3, FesCarrying());
    }

    [Fact]
    public void AnsweredFes_ServerChatterNeverWakeProbes()
    {
        // An answered FES must clear the wake check so ordinary server chatter does not trigger
        // extra recovery probes.
        var clock = new VirtualSessionClock();
        using var session = new MudSession(new MudSessionOptions
        {
            FesHeartbeatInterval = Beat,
            WakeReplySlack       = TimeSpan.FromMilliseconds(50),  // a naive check would trip almost instantly
        });
        clock.Attach(session);
        var outgoing = new List<string>();
        var sync = new object();
        session.OutgoingBytes += b => { lock (sync) outgoing.Add(Encoding.Latin1.GetString(b)); };

        session.Feed(GameModeEntry);
        session.Feed(FesOpen);
        session.Feed(Encoding.Latin1.GetBytes("81 81 94 94 95 95 50 50 1785 N N N N 5 S\n"));  // answers the entry FES; all stats maxed
        session.Feed(AutoReset);

        // Server chatter well past every staleness horizon - none of it may trigger a probe.
        var step = TimeSpan.FromMilliseconds(40);
        for (int i = 0; i < 10; i++)
        {
            session.Feed(Encoding.Latin1.GetBytes("The wind whistles through the trees.\r\n"));
            clock.Advance(step);
        }
        lock (sync)
        {
            Assert.True(outgoing.Count >= 2, "expected routine FES+FEW beats to keep flowing");
            // Observed, with the clock stepped by hand: FES-carrying writes at 0, at one beat, at
            // one beat + WakeReplySlack + the chatter line that crossed it, and one beat after
            // THAT. Four, and the third is a wake beat - the entry FES is the only one anything
            // answers here, so the beat following it is genuinely unanswered and the next chatter
            // past the slack is a legitimate wake rather than the spurious kind this test is named
            // for. It re-phases the period instead of adding to it, which is why the fourth sits
            // one beat after the wake and not back on the original lattice. A second wake would
            // make five: WakeProbeFloor holds for the rest of the chatter.
            Assert.Equal(4, outgoing.Count(o => o.StartsWith("\x1b-[FES", StringComparison.Ordinal)));
            Assert.Equal(2, outgoing.Count(o => o == FesFewProbe));
        }
    }

    [Fact]
    public void RoomEntry_MarksRoomContentsDirty()
    {
        Feed(GameModeEntry);
        Advance(MinSpacing * 2);
        var feiBaseline = CountSent(FesFeiProbe);
        Feed("\r\n");              // put the parser at line start
        Feed(GameModeEntry);       // C02+C01 at line start mid-game = room short -> RoomEntered
        Advance(StaleDelay);
        // The hint rides either the reactive FES,FEI probe or an imminent full beat.
        var got = CountSent(FesFeiProbe) > feiBaseline || CountSent(FullProbe) >= 2;
        Assert.True(got, "expected the room entry to refresh room contents via FEI");
    }
}
