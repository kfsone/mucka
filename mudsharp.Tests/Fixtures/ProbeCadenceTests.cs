using System.Text;
using MudSharp.Session;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// MudSession-level tests for the composed heartbeat (probe-noise policy, 2026-07-25):
/// FEW rides every beat (who-list vigilance + keep-alive); FES rides every beat only until the
/// reset clock relaxes (locked / post-reset re-converged), then at most once per FesSweepInterval;
/// FEI rides only when a C1 hint marked room/carried items dirty. Real timers with short
/// intervals; assertions poll rather than assuming exact firing.
/// </summary>
public class ProbeCadenceTests : IDisposable
{
    private const string FesFewProbe = "\x1b-[FES,FEW\x1b-]";
    private const string FewProbe    = "\x1b-[FEW\x1b-]";
    private const string FeiProbe    = "\x1b-[FEI\x1b-]";
    private const string FewFeiProbe = "\x1b-[FEW,FEI\x1b-]";
    private const string FullProbe   = "\x1b-[FES,FEW,FEI\x1b-]";

    private static readonly byte[] GameModeEntry = [0x9D, 0x9C, 0xFF, 0xFF];
    private static readonly byte[] AutoReset     = [0xA1, 0x9F, 0xFF, 0xFF];   // C06 C04 → locks the reset clock
    private static readonly byte[] ItemArriving  = [0x9E, 0x9C, 0x9D, 0xFF, 0xFF];

    private readonly MudSession _session;
    private readonly List<string> _outgoing = new();
    private readonly object _lock = new();

    public ProbeCadenceTests()
    {
        _session = new MudSession(new MudSessionOptions
        {
            FesHeartbeatInterval = TimeSpan.FromMilliseconds(150),
            StaleProbeDelay      = TimeSpan.FromMilliseconds(40),
            MinProbeSpacing      = TimeSpan.FromMilliseconds(30),
            FesSweepInterval     = TimeSpan.FromMilliseconds(700),
        });
        _session.OutgoingBytes += b => { lock (_lock) _outgoing.Add(Encoding.Latin1.GetString(b)); };
    }

    public void Dispose() => _session.Dispose();

    private void Feed(byte[] data) => _session.Feed(data);
    private void Feed(string ascii) => _session.Feed(Encoding.Latin1.GetBytes(ascii));

    private int CountSent(string probe)
    {
        lock (_lock) return _outgoing.Count(o => o == probe);
    }

    private bool WaitForProbe(string probe, int atLeast = 1, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (CountSent(probe) >= atLeast) return true;
            Thread.Sleep(10);
        }
        return CountSent(probe) >= atLeast;
    }

    [Fact]
    public void EntryProbe_IsFull_ThenBeatsCarryFesFew_NoFeiWhenClean()
    {
        Feed(GameModeEntry);
        Assert.Equal(1, CountSent(FullProbe));   // entry populates everything

        // Reset clock unrelaxed (no lock yet) → FES rides every beat; nothing marked the FEI
        // panel dirty since the entry probe carried it → beats are FES,FEW.
        Assert.True(WaitForProbe(FesFewProbe, atLeast: 2), "expected FES,FEW routine beats");
        Assert.Equal(1, CountSent(FullProbe));   // FEI never rode a routine beat uninvited
    }

    [Fact]
    public void AutoResetLock_RelaxesBeatsToFewOnly_ThenFesSweepRides()
    {
        Feed(GameModeEntry);
        Feed(AutoReset);   // C06 C04 locks the reset clock instantly → cadence relaxes

        // Beats drop FES immediately (last FES was the entry probe, sweep not yet due).
        Assert.True(WaitForProbe(FewProbe), "expected FEW-only beats once the reset clock locked");

        // After FesSweepInterval (700 ms) elapses, one beat carries FES again.
        Assert.True(WaitForProbe(FesFewProbe), "expected the timed FES sweep to ride a beat");
    }

    [Fact]
    public void ItemHint_RidesReactiveFeiProbe_NotRoutineBeat()
    {
        Feed(GameModeEntry);
        Thread.Sleep(60);          // past MinProbeSpacing from the entry probe
        Feed(ItemArriving);        // C03 item arriving → inventory dirty
        Assert.True(WaitForProbe(FeiProbe), "expected a reactive FEI-only probe for the item event");
    }

    [Fact]
    public void RoomEntry_MarksRoomContentsDirty()
    {
        Feed(GameModeEntry);
        Thread.Sleep(60);
        Feed("\r\n");              // put the parser at line start
        Feed(GameModeEntry);       // C02+C01 at line start mid-game = room short → RoomEntered
        // The hint rides either the reactive FEI probe or an imminent beat as FES,FEW,FEI.
        var got = WaitForProbe(FeiProbe) || CountSent(FullProbe) >= 2;
        Assert.True(got, "expected the room entry to refresh room contents via FEI");
    }
}
