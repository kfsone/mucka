using System.Text;
using MudSharp.Session;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// MudSession-level tests for the debounced stale-stats probe scheduler: C1 hints mark
/// categories stale, updates arriving within the grace period cancel the probe, and only
/// the still-stale categories are queried (FES / FEW / FEI subsets).
/// Uses shortened timings; assertions poll rather than assuming exact timer firing.
/// </summary>
public class StaleProbeTests : IDisposable
{
    private const string FesProbe     = "\x1b-[FES\x1b-]";
    private const string FesFeiProbe  = "\x1b-[FES,FEI\x1b-]";
    private const string FewProbe     = "\x1b-[FEW\x1b-]";
    private const string FeiProbe     = "\x1b-[FEI\x1b-]";
    private const string FewFeiProbe  = "\x1b-[FEW,FEI\x1b-]";
    private const string FullProbe    = "\x1b-[FES,FEW,FEI\x1b-]";

    private static readonly byte[] GameModeEntry     = [0x9D, 0x9C, 0xFF, 0xFF];
    private static readonly byte[] C07Hit            = [0xA2, 0xFF, 0xFF];
    private static readonly byte[] C06Magical        = [0xA1, 0xFF, 0xFF];
    private static readonly byte[] C03ItemArriving   = [0x9E, 0x9C, 0x9D, 0xFF, 0xFF];
    private static readonly byte[] C08WeaponChange   = [0xA3, 0xA0, 0xFF, 0xFF];
    private static readonly byte[] FewContextOpen    = [0xA7, 0xA3, 0xA0, 0xFF, 0xFF];
    private static readonly byte[] FewPlayerRed      = [0xA0, 0x9B, 0xA1, 0xFF, 0xFF];
    private static readonly byte[] MortalArriving    = [0xA0, 0x9B, 0x9D, 0xFF, 0xFF];
    private static readonly byte[] Pop               = [0xFF, 0xFF];

    private readonly MudSession _session;
    private readonly List<string> _outgoing = new();
    private readonly object _lock = new();

    public StaleProbeTests()
    {
        _session = new MudSession(new MudSessionOptions
        {
            FesHeartbeatInterval = TimeSpan.FromSeconds(60),   // far enough away not to interfere
            StaleProbeDelay      = TimeSpan.FromMilliseconds(60),
            MinProbeSpacing      = TimeSpan.FromMilliseconds(100),
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

    private bool WaitForProbe(string probe, int atLeast = 1, int timeoutMs = 2000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (CountSent(probe) >= atLeast) return true;
            Thread.Sleep(10);
        }
        return CountSent(probe) >= atLeast;
    }

    /// <summary>Enter game mode and wait out MinProbeSpacing from the entry probe.</summary>
    private void EnterGameModeAndSettle()
    {
        Feed(GameModeEntry);
        Assert.True(_session.InGameMode);
        Assert.Equal(1, CountSent(FullProbe));   // game-entry heartbeat
        Thread.Sleep(150);
    }

    /// <summary>Feed a complete FEW response naming one online player, "Alice the witch".</summary>
    private void EstablishWhoListBaseline()
    {
        Feed(FewContextOpen);
        Feed(FewPlayerRed);
        Feed("Alice the witch\n");
        Feed(Pop);   // closes the name colour
        Feed(Pop);   // closes the FEW context
    }

    private void FeedArrivalLine(string personaName)
    {
        Feed(MortalArriving);
        Feed(personaName);
        Feed(Pop);
        Feed(" has just arrived.\r\n");
    }

    [Fact]
    public void StatsHint_NeverFiresOffCadenceProbe()
    {
        // Probe-noise policy (2026-07-25): stat categories are advisory — combat deltas arrive as
        // inline text and the timed FES sweep catches the rest. No reactive FES on a hit.
        EnterGameModeAndSettle();
        Feed(C07Hit);
        Thread.Sleep(400);
        Assert.Equal(0, CountSent(FesProbe));
        Assert.Equal(1, CountSent(FullProbe));   // entry probe only
    }

    [Fact]
    public void StaminaHint_FollowedByInlineUpdate_DoesNotProbe()
    {
        EnterGameModeAndSettle();
        Feed(C07Hit);
        Feed("The eel stings you (84/90).\r\n");   // clears the stamina flag before the deadline
        Thread.Sleep(400);
        Assert.Equal(0, CountSent(FesProbe));
    }

    [Fact]
    public void WeaponChange_SendsFeiOnlyProbe()
    {
        // C08 05 marks stamina AND inventory stale; only the inventory half warrants a reactive
        // probe — the stats half rides the timed FES sweep.
        EnterGameModeAndSettle();
        Feed(C08WeaponChange);
        Assert.True(WaitForProbe(FeiProbe), "expected a FEI-only probe for the weapon change");
        Assert.Equal(0, CountSent(FesFeiProbe));
    }

    [Fact]
    public void PlainUncodedLine_MarksInventoryStale_SendsFeiProbe()
    {
        // Item-moving commands answer in plain un-coded text ("You drop the sword.") — no C1
        // code accompanies them, so the plain line itself is the FEI hint (probe-noise policy,
        // 2026-07-25: any non-coded output may have moved items).
        EnterGameModeAndSettle();
        Feed(Pop);           // close the entry code's colour frame — the live server always pops
        Thread.Sleep(200);   // drain the entry room-enter hint's own FEI probe
        var baseline = CountSent(FeiProbe);
        Feed("You drop the ancient scroll.\r\n");
        Assert.True(WaitForProbe(FeiProbe, atLeast: baseline + 1),
            "expected a FEI-only probe after plain un-coded output");
    }

    [Fact]
    public void CodedText_InsideColourFrame_NeverPlainHints()
    {
        // Text inside any C1 colour frame is coded output — its own code's classification
        // governs. C07 (combat hit) is stats-advisory, so no reactive probe fires at all.
        EnterGameModeAndSettle();
        Feed(Pop);
        Thread.Sleep(200);   // drain the entry room-enter hint's own FEI probe
        var feiBaseline = CountSent(FeiProbe);
        Feed(C07Hit);                          // pushes a colour frame
        Feed("The eel stings you (84/90).");
        Feed(Pop);
        Feed("\r\n");
        Thread.Sleep(400);
        Assert.Equal(feiBaseline, CountSent(FeiProbe));
        Assert.Equal(0, CountSent(FesProbe));
    }

    [Fact]
    public void WhoAndInventoryStale_SendsCombinedFewFeiProbe()
    {
        // C06 no longer hints anything; item arrival + unknown-player arrival leave exactly
        // who-list + inventory stale → one combined FEW,FEI reactive probe, never FES.
        EnterGameModeAndSettle();
        EstablishWhoListBaseline();
        Thread.Sleep(150);
        Feed(C06Magical);                  // no hint (probe-noise policy)
        Feed(C03ItemArriving);             // inventory stale
        FeedArrivalLine("Bob the warrior"); // unknown player → who list stale (+ inventory)
        Assert.True(WaitForProbe(FewFeiProbe), "expected a combined FEW,FEI probe");
        Assert.Equal(1, CountSent(FullProbe));   // entry probe only — stats never re-probed
    }

    [Fact]
    public void KnownPlayerArrival_DoesNotProbeWhoList()
    {
        EnterGameModeAndSettle();
        EstablishWhoListBaseline();
        Thread.Sleep(150);
        FeedArrivalLine("Alice the witch");   // already on the cached list
        Thread.Sleep(400);
        Assert.Equal(0, CountSent(FewProbe));
        Assert.Equal(0, CountSent(FewFeiProbe));
        Assert.True(CountSent(FeiProbe) >= 1);   // the arrival still refreshes room contents
    }

    [Fact]
    public void UnknownPlayerArrival_ProbesWhoListAndRoomContents()
    {
        // The C05 arrival marks the room contents dirty (FEI) and the missing name marks the
        // who list stale (FEW) → one combined reactive probe.
        EnterGameModeAndSettle();
        EstablishWhoListBaseline();
        Thread.Sleep(150);
        FeedArrivalLine("Bob the warrior");
        Assert.True(WaitForProbe(FewFeiProbe), "expected a FEW,FEI probe for an arrival missing from the cached who list");
    }

    [Fact]
    public void HeartbeatDisabled_HintsNeverProbe()
    {
        _session.UpdateFesInterval(TimeSpan.Zero);
        Feed(GameModeEntry);
        Feed(C07Hit);
        Feed(C08WeaponChange);
        Thread.Sleep(400);
        lock (_lock)
            Assert.DoesNotContain(_outgoing, o => o.StartsWith("\x1b-[FES", StringComparison.Ordinal));
    }
}
