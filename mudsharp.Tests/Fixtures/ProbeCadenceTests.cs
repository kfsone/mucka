using System.Text;
using MudSharp.Session;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// MudSession-level tests for the composed heartbeat (probe-noise policy, 2026-07-25):
/// FEW rides every beat (who-list vigilance + keep-alive); FES rides every beat while the reset
/// clock still needs cadence OR any stat is silently regenerating (stamina/magic below max),
/// otherwise at most once per FesSweepInterval; FEI rides only when a C1 hint marked
/// room/carried items dirty. Real timers with short intervals; assertions poll rather than
/// assuming exact firing.
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
    private static readonly byte[] FesOpen       = [0xA7, 0xA3, 0x9C, 0xFF, 0xFF];   // C12 C08 C01 → FES data line follows

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
    public void RegeneratingStamina_KeepsFesOnEveryBeat_EvenWhenRelaxed()
    {
        // Stamina below max regenerates over time with no inline announcement, so FES must ride
        // the beat regardless of the relaxed reset-clock cadence.
        Feed(GameModeEntry);
        Feed("The eel stings you (65/81).\r\n");   // inline stamina — below max
        Feed(AutoReset);                            // reset clock locks → cadence relaxes
        Assert.True(WaitForProbe(FesFewProbe, atLeast: 2), "expected FES to keep riding the beats while stamina regenerates");
        Assert.Equal(0, CountSent(FewProbe));       // no beat ever dropped FES
    }

    [Fact]
    public void FullStamina_RelaxedClock_DropsFesFromBeats()
    {
        Feed(GameModeEntry);
        Feed("The eel stings you (81/81).\r\n");   // at max — nothing regenerating
        Feed(AutoReset);
        Assert.True(WaitForProbe(FewProbe), "expected FEW-only beats at full stats once the clock relaxed");
    }

    [Fact]
    public void MagicBelowMax_KeepsFesOnEveryBeat_EvenWhenRelaxed()
    {
        Feed(GameModeEntry);
        Feed(AutoReset);                            // lock first so only the regen rule keeps FES riding
        Feed(FesOpen);
        Feed("50 50 94 94 95 95 10 50 1785 N N N N 2 S\n");   // sta full, magic 10/50
        Assert.True(WaitForProbe(FesFewProbe, atLeast: 2), "expected FES to keep riding the beats while magic regenerates");
    }

    [Fact]
    public void ItemHint_RidesReactiveFeiProbe_NotRoutineBeat()
    {
        Feed(GameModeEntry);
        Thread.Sleep(60);          // past MinProbeSpacing from the entry probe
        Feed(ItemArriving);        // C03 item arriving → inventory dirty
        Assert.True(WaitForProbe(FeiProbe), "expected a reactive FEI-only probe for the item event");
    }

    // ── Wake probe: fires only for an UNANSWERED FES probe, never for quiet FEW beats ──────

    [Fact]
    public void UnansweredFesProbe_IncomingData_FiresImmediateRecoveryBeat()
    {
        // Sleep scenario: probes no-op, so the entry probe's FES never gets a reply. The next
        // server bytes (the wake) must fire an immediate FES-carrying beat instead of waiting
        // out the heartbeat interval.
        using var session = new MudSession(new MudSessionOptions
        {
            FesHeartbeatInterval = TimeSpan.FromSeconds(60),   // no natural beat during the test
            WakeReplySlack       = TimeSpan.FromMilliseconds(50),
        });
        var outgoing = new List<string>();
        var sync = new object();
        session.OutgoingBytes += b => { lock (sync) outgoing.Add(Encoding.Latin1.GetString(b)); };

        session.Feed(GameModeEntry);        // entry FullProbe goes out — never answered
        Thread.Sleep(120);                  // past WakeReplySlack
        session.Feed(Encoding.Latin1.GetBytes("You dream of sheep.\r\n"));

        bool woke = false;
        var deadline = Environment.TickCount64 + 2000;
        while (Environment.TickCount64 < deadline)
        {
            lock (sync) woke = outgoing.Count(o => o.StartsWith("\x1b-[FES", StringComparison.Ordinal)) >= 2;
            if (woke) break;
            Thread.Sleep(10);
        }
        Assert.True(woke, "expected the wake to fire an immediate FES-carrying recovery beat");
    }

    [Fact]
    public void AnsweredFes_QuietFewBeats_ServerChatterNeverWakeProbes()
    {
        // Regression (doubled-FEW capture, 2026-07-25): FEW-only beats legitimately draw nothing
        // but a prompt back — the server pushes the who list only when it changed. That silence
        // must not read as sleep: the old reply-age staleness check fired an extra beat on every
        // incoming packet, and the extra FEW's own silent reply kept the staleness alive.
        using var session = new MudSession(new MudSessionOptions
        {
            FesHeartbeatInterval = TimeSpan.FromMilliseconds(150),
            WakeReplySlack       = TimeSpan.FromMilliseconds(50),  // old check would trip almost instantly
            FesSweepInterval     = TimeSpan.FromSeconds(10),       // no sweep-FES inside the test window
        });
        var outgoing = new List<string>();
        var sync = new object();
        session.OutgoingBytes += b => { lock (sync) outgoing.Add(Encoding.Latin1.GetString(b)); };

        session.Feed(GameModeEntry);
        session.Feed(FesOpen);
        session.Feed(Encoding.Latin1.GetBytes("81 81 94 94 95 95 50 50 1785 N N N N 5 S\n"));  // answers the entry FES; all stats maxed
        session.Feed(AutoReset);            // clock locks → beats relax to FEW-only

        // Server chatter well past every staleness horizon — none of it may trigger a probe.
        for (int i = 0; i < 10; i++)
        {
            session.Feed(Encoding.Latin1.GetBytes("The wind whistles through the trees.\r\n"));
            Thread.Sleep(40);
        }
        lock (sync)
        {
            Assert.Equal(1, outgoing.Count(o => o.StartsWith("\x1b-[FES", StringComparison.Ordinal)));  // entry probe only
            Assert.True(outgoing.Count >= 2, "expected routine FEW beats to keep flowing");
        }
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
