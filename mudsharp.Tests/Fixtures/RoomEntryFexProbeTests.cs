using System.Text;
using MudSharp.Session;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// Recovery probe for spell-driven relocations (resite/supersite, issue #136): the server sends
/// a room description with no accompanying auto-fex FEEXITS block, because auto commands only
/// fire on real movement. MudSession arms a one-shot timer on RoomEntered; FexListStarting
/// (ordinary auto-fex-covered movement) cancels it, otherwise it fires the same explicit FEX
/// probe used at game-mode entry. Real timers with a short delay; assertions poll rather than
/// assuming exact firing.
/// </summary>
public class RoomEntryFexProbeTests : IDisposable
{
    private const string FexProbe = "\x1b-[FEX\x1b-]";

    // C02+C01 game-mode prompt variant — the post-character-select entry trigger, and (once
    // already in game mode, at line start) the generic room-short trigger for RoomEntered.
    private static readonly byte[] GameModeEntry = [0x9D, 0x9C, 0xFF, 0xFF];
    // C95+C03 account-logout → ExitGameMode.
    private static readonly byte[] AccountLogout = [0xFA, 0x9E, 0xFF, 0xFF];
    // The frame prompt that leads every server frame (IsPartial '*'), taken from a live capture.
    private static readonly byte[] PromptBytes =
        [0x9C, 0xFF, 0xFF, 0x9C, 0x9D, 0xFF, 0xFF, 0x2A, 0xFF, 0xFF, 0xFF, 0xFF];
    // C12+C08+C02+C255 — opens the FEX response scope (fires FexListStarting).
    private static readonly byte[] FexContextOpen = [0xA7, 0xA3, 0x9D, 0xFF, 0xFF];
    private static readonly byte[] Pop = [0xFF, 0xFF];

    private const string Echoes = "auto fex\r\nscore\r\n";
    private const string AutoFexReply =
        "You will now get an automatic FEEXITS command performed every time you issue a movement command.\r\n" +
        "To cancel it, use UNAUTO FEEXITS.\r\n";
    private const string ScoreSheet =
        "name:          Ollie\r\n" +
        "sex:            male\r\n" +
        "score:  47,297 points   this game:      0 points        value:  9,534 points\r\n" +
        "games played:   144\r\n";

    private readonly MudSession _session;
    private readonly List<string> _outgoing = new();
    private readonly object _lock = new();

    private RoomEntryFexProbeTests(TimeSpan roomEntryFexProbeDelay)
    {
        _session = new MudSession(new MudSessionOptions
        {
            FesHeartbeatInterval    = TimeSpan.FromSeconds(60),   // keep the heartbeat out of the way
            RoomEntryFexProbeDelay  = roomEntryFexProbeDelay,
        });
        _session.OutgoingBytes += b => { lock (_lock) _outgoing.Add(Encoding.Latin1.GetString(b)); };
    }

    public RoomEntryFexProbeTests() : this(TimeSpan.FromMilliseconds(80)) { }

    public void Dispose() => _session.Dispose();

    private void Feed(byte[] data) => _session.Feed(data);
    private void Feed(string ascii) => _session.Feed(Encoding.Latin1.GetBytes(ascii));
    private void Prompt() => _session.Feed(PromptBytes);

    private int CountSent(string probe)
    {
        lock (_lock) return _outgoing.Count(o => o == probe);
    }

    private void ClearOutgoing()
    {
        lock (_lock) _outgoing.Clear();
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

    /// <summary>
    /// Enter game mode and run the post-select setup batch to completion, closing the setup
    /// window (_setupWindowActive) — mirrors PostSelectSetupTests. Until this window closes the
    /// entry-time explicit FEX probe already covers room entry, so a later RoomEntered in this
    /// window would (correctly) not arm a recovery timer; these tests target the general,
    /// post-setup case (any subsequent room entry, not just resite/supersite specifically).
    /// </summary>
    private void EnterAndCloseSetupWindow()
    {
        Feed(GameModeEntry);
        Prompt(); Feed(Echoes);
        Prompt(); Feed(AutoFexReply);
        Prompt(); Feed(ScoreSheet);
        Prompt();   // closes the setup window (score frame's closing prompt)
        ClearOutgoing();
    }

    /// <summary>Simulate a room-short arriving mid-game (RoomEntered), at column 0.</summary>
    private void FeedRoomEntry()
    {
        Feed("\r\n");   // guarantee AtLineStart
        Feed(GameModeEntry);   // C02+C01 at line start, already in game mode → RoomEntered only
    }

    [Fact]
    public void RoomEntered_WithNoFexFollowing_SendsExplicitProbeAfterDelay()
    {
        EnterAndCloseSetupWindow();
        FeedRoomEntry();
        Assert.True(WaitForProbe(FexProbe), "expected the explicit FEX probe once the recovery window elapsed");
    }

    [Fact]
    public void RoomEntered_FollowedByFexList_NeverSendsExplicitProbe()
    {
        EnterAndCloseSetupWindow();
        FeedRoomEntry();

        // Ordinary auto-fex-covered movement: the FEX list starts arriving well inside the window.
        Feed(FexContextOpen);
        Feed("north\n");
        Feed(Pop);   // closes the FEX response scope

        Thread.Sleep(300);   // well past the 80ms recovery window
        Assert.Equal(0, CountSent(FexProbe));
    }

    [Fact]
    public void OverlappingRoomEntries_ResetTimer_OnlyOneProbeFires()
    {
        EnterAndCloseSetupWindow();
        FeedRoomEntry();               // arms a timer due in ~80ms
        Thread.Sleep(50);              // before it elapses...
        FeedRoomEntry();               // ...a second entry replaces it with a fresh ~80ms timer

        // The FIRST timer's original deadline (~30ms from now) must NOT have fired.
        Thread.Sleep(20);
        Assert.Equal(0, CountSent(FexProbe));

        // The second timer eventually fires exactly once.
        Assert.True(WaitForProbe(FexProbe), "expected the reset timer to still fire once");
        Thread.Sleep(150);   // give a stray duplicate time to show up, if any
        Assert.Equal(1, CountSent(FexProbe));
    }

    [Fact]
    public void GameModeExit_BeforeDeadline_SuppressesProbe()
    {
        EnterAndCloseSetupWindow();
        FeedRoomEntry();          // arms a timer due in ~80ms
        Feed(AccountLogout);      // exits game mode well before the deadline

        Thread.Sleep(300);        // past the original deadline
        Assert.Equal(0, CountSent(FexProbe));
    }
}
