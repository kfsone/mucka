using System.Text;
using MudSharp.Session;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// MudSession-level tests for the debounced stale-stats probe scheduler: C1 hints mark
/// categories stale, updates arriving within the grace period cancel the probe, and only
/// the still-stale categories are queried, always with the mandatory FES prefix.
///
/// <para>Timings are shortened, and every deadline and probe-path instant comes from
/// <see cref="VirtualSessionClock"/>: a probe lands because <c>Advance</c> crossed its deadline, so
/// the counts below are exact rather than polled.</para>
/// </summary>
public class StaleProbeTests : IDisposable
{
    private const string FesProbe     = "\x1b-[FES\x1b-]";
    private const string FesFeiProbe  = "\x1b-[FES,FEI\x1b-]";
    private const string FesFewProbe  = "\x1b-[FES,FEW\x1b-]";
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

    private static readonly TimeSpan StaleDelay = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan MinSpacing = TimeSpan.FromMilliseconds(100);
    /// <summary>Long enough that anything a hint could have armed has either fired or been dropped.</summary>
    private static readonly TimeSpan WellPastEveryDeadline = TimeSpan.FromMilliseconds(400);

    private readonly MudSession _session;
    private readonly VirtualSessionClock _clock = new();
    private readonly List<string> _outgoing = new();
    private readonly object _lock = new();

    public StaleProbeTests()
    {
        _session = new MudSession(new MudSessionOptions
        {
            FesHeartbeatInterval = TimeSpan.FromSeconds(60),   // far enough away not to interfere
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

    private int CountSent(string probe)
    {
        lock (_lock) return _outgoing.Count(o => o == probe);
    }

    /// <summary>Step the clock. Every probe the step is due lands before this returns.</summary>
    private void Advance(TimeSpan by) => _clock.Advance(by);

    /// <summary>Enter game mode and step past MinProbeSpacing from the entry probe, by which point
    /// the entry's own room hint has spent its probe and nothing is left armed.</summary>
    private void EnterGameModeAndSettle()
    {
        Feed(GameModeEntry);
        Assert.True(_session.InGameMode);
        Assert.Equal(1, CountSent(FullProbe));   // game-entry heartbeat
        Advance(MinSpacing + StaleDelay);
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
        // Stat categories are advisory: combat deltas arrive as inline text and the next routine
        // FES catches the rest. A hit alone does not justify an off-cadence probe.
        EnterGameModeAndSettle();
        Feed(C07Hit);
        Advance(WellPastEveryDeadline);
        Assert.Equal(0, CountSent(FesProbe));
        Assert.Equal(1, CountSent(FullProbe));   // entry probe only
    }

    [Fact]
    public void StaminaHint_FollowedByInlineUpdate_DoesNotProbe()
    {
        EnterGameModeAndSettle();
        Feed(C07Hit);
        Feed("The eel stings you (84/90).\r\n");   // clears the stamina flag before the deadline
        Advance(WellPastEveryDeadline);
        Assert.Equal(0, CountSent(FesProbe));
    }

    [Fact]
    public void WeaponChange_SendsFesFeiProbe()
    {
        // C08 05 marks stamina and inventory stale. Inventory warrants the reactive query, and
        // mandatory FES refreshes the stats in the same probe.
        EnterGameModeAndSettle();
        var baseline = CountSent(FesFeiProbe);
        Feed(C08WeaponChange);
        Advance(StaleDelay);
        Assert.Equal(baseline + 1, CountSent(FesFeiProbe));   // FES leads the FEI query
    }

    [Fact]
    public void PlainUncodedLine_MarksInventoryStale_SendsFesFeiProbe()
    {
        // Item-moving commands answer in plain un-coded text ("You drop the sword.") - no C1
        // code accompanies them, so the plain line itself is the FEI hint (probe-noise policy:
        // any non-coded output may have moved items).
        EnterGameModeAndSettle();
        Feed(Pop);                          // close the entry code's colour frame - the live server always pops
        Advance(MinSpacing + StaleDelay);   // drain the entry room-enter hint's own FEI probe
        var baseline = CountSent(FesFeiProbe);
        Feed("You drop the ancient scroll.\r\n");
        Advance(StaleDelay);
        Assert.Equal(baseline + 1, CountSent(FesFeiProbe));   // a FES,FEI probe follows plain un-coded output
    }

    [Fact]
    public void CodedText_InsideColourFrame_NeverPlainHints()
    {
        // Text inside any C1 colour frame is coded output - its own code's classification
        // governs. C07 (combat hit) is stats-advisory, so no reactive probe fires at all.
        EnterGameModeAndSettle();
        Feed(Pop);
        Advance(MinSpacing + StaleDelay);   // drain the entry room-enter hint's own FEI probe
        var feiBaseline = CountSent(FesFeiProbe);
        Feed(C07Hit);                          // pushes a colour frame
        Feed("The eel stings you (84/90).");
        Feed(Pop);
        Feed("\r\n");
        Advance(WellPastEveryDeadline);
        Assert.Equal(feiBaseline, CountSent(FesFeiProbe));
        Assert.Equal(0, CountSent(FesProbe));
    }

    [Fact]
    public void WhoAndInventoryStale_SendsFullProbe()
    {
        // C06 no longer hints anything; item arrival + unknown-player arrival leave exactly
        // who-list + inventory stale -> one full reactive probe.
        EnterGameModeAndSettle();
        EstablishWhoListBaseline();
        Advance(MinSpacing + StaleDelay);
        Feed(C06Magical);                  // no hint (probe-noise policy)
        Feed(C03ItemArriving);             // inventory stale
        FeedArrivalLine("Bob the warrior"); // unknown player -> who list stale (+ inventory)
        Advance(StaleDelay);
        Assert.Equal(2, CountSent(FullProbe));   // one combined FES,FEW,FEI probe on top of the entry's
    }

    [Fact]
    public void KnownPlayerArrival_DoesNotProbeWhoList()
    {
        EnterGameModeAndSettle();
        EstablishWhoListBaseline();
        Advance(MinSpacing + StaleDelay);
        FeedArrivalLine("Alice the witch");   // already on the cached list
        Advance(WellPastEveryDeadline);
        Assert.Equal(0, CountSent(FesFewProbe));
        Assert.True(CountSent(FesFeiProbe) >= 1);   // the arrival still refreshes room contents
    }

    [Fact]
    public void UnknownPlayerArrival_ProbesWhoListAndRoomContents()
    {
        // The C05 arrival marks the room contents dirty (FEI) and the missing name marks the
        // who list stale (FEW) -> one combined reactive probe.
        EnterGameModeAndSettle();
        EstablishWhoListBaseline();
        Advance(MinSpacing + StaleDelay);
        FeedArrivalLine("Bob the warrior");
        Advance(StaleDelay);
        // FES leads the FEW,FEI query for an unknown arrival.
        Assert.Equal(2, CountSent(FullProbe));
    }

    [Fact]
    public void HeartbeatDisabled_HintsNeverProbe()
    {
        _session.UpdateFesInterval(TimeSpan.Zero);
        Feed(GameModeEntry);
        Feed(C07Hit);
        Feed(C08WeaponChange);
        Advance(WellPastEveryDeadline);
        lock (_lock)
            Assert.DoesNotContain(_outgoing, o => o.StartsWith("\x1b-[FES", StringComparison.Ordinal));
    }
}
