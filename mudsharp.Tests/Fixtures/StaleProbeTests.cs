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
    public void StaminaHint_WithNoUpdate_SendsFesOnlyProbe()
    {
        EnterGameModeAndSettle();
        Feed(C07Hit);
        Assert.True(WaitForProbe(FesProbe), "expected a FES-only probe after the stale grace period");
        Assert.Equal(1, CountSent(FullProbe));   // routine probe untouched
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
    public void WeaponChange_SendsCombinedFesFeiProbe()
    {
        EnterGameModeAndSettle();
        Feed(C08WeaponChange);   // stamina + inventory stale
        Assert.True(WaitForProbe(FesFeiProbe), "expected a combined FES,FEI probe");
    }

    [Fact]
    public void AllCategoriesStale_SendsFullProbe()
    {
        EnterGameModeAndSettle();
        EstablishWhoListBaseline();
        Thread.Sleep(150);
        Feed(C06Magical);                  // all stats stale
        Feed(C03ItemArriving);             // inventory stale
        FeedArrivalLine("Bob the warrior"); // unknown player → who list stale
        Assert.True(WaitForProbe(FullProbe, atLeast: 2),
            "expected the full FES,FEW,FEI probe (early routine probe) when everything is stale");
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
    }

    [Fact]
    public void UnknownPlayerArrival_ProbesWhoList()
    {
        EnterGameModeAndSettle();
        EstablishWhoListBaseline();
        Thread.Sleep(150);
        FeedArrivalLine("Bob the warrior");
        Assert.True(WaitForProbe(FewProbe), "expected a FEW probe for an arrival missing from the cached who list");
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
