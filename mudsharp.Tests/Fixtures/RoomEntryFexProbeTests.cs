using System.Text;
using MudSharp.Session;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// Recovery probe for spell-driven relocations (resite/supersite): the server sends
/// a room description with no accompanying auto-fex FEEXITS block, because auto commands only
/// fire on real movement. MudSession arms a one-shot timer on RoomEntered; FexListStarting
/// (ordinary auto-fex-covered movement) cancels it, otherwise it fires the same explicit FEX
/// probe used at game-mode entry.
///
/// <para>The timer comes from <see cref="VirtualSessionClock"/>, which owns every deadline and
/// every probe-path instant this session reads. So the assertions are about the probe's deadline
/// arithmetic - what was armed, what replaced it, what it was still holding when the step crossed
/// it - and not about what a loaded machine got round to within a margin. The delay under test is
/// the shipped <see cref="MudSessionOptions.RoomEntryFexProbeDelay"/>, not a test-only short
/// one.</para>
/// </summary>
public class RoomEntryFexProbeTests : IDisposable
{
    private const string FexProbe = "\x1b-[FEX\x1b-]";

    // C02+C01 game-mode prompt variant - the post-character-select entry trigger, and (once
    // already in game mode, at line start) the generic room-short trigger for RoomEntered.
    private static readonly byte[] GameModeEntry = [0x9D, 0x9C, 0xFF, 0xFF];
    // C95+C03 account-logout -> ExitGameMode.
    private static readonly byte[] AccountLogout = [0xFA, 0x9E, 0xFF, 0xFF];
    // The frame prompt that leads every server frame (IsPartial '*'), taken from a live capture.
    private static readonly byte[] PromptBytes =
        [0x9C, 0xFF, 0xFF, 0x9C, 0x9D, 0xFF, 0xFF, 0x2A, 0xFF, 0xFF, 0xFF, 0xFF];
    // C12+C08+C02+C255 - opens the FEX response scope (fires FexListStarting).
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

    /// <summary>The delay the probe actually ships with; every step below is expressed against it.</summary>
    private static readonly TimeSpan ProbeDelay = new MudSessionOptions().RoomEntryFexProbeDelay;
    /// <summary>Smallest step that takes the virtual clock strictly past a deadline.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(1);

    private readonly MudSession _session;
    private readonly List<string> _outgoing = new();
    private readonly object _lock = new();
    private readonly VirtualSessionClock _clock = new();

    public RoomEntryFexProbeTests()
    {
        _session = new MudSession(new MudSessionOptions
        {
            FesHeartbeatInterval = TimeSpan.FromSeconds(60),   // keep the heartbeat out of the way
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
    private void Prompt() => _session.Feed(PromptBytes);

    private int CountSent(string probe)
    {
        lock (_lock) return _outgoing.Count(o => o == probe);
    }

    private void ClearOutgoing()
    {
        lock (_lock) _outgoing.Clear();
    }

    /// <summary>
    /// Step the session's clock. Any probe the step is due lands synchronously, before this
    /// returns, so the assertion that follows needs no waiting.
    /// </summary>
    private void Advance(TimeSpan by) => _clock.Advance(by);

    /// <summary>
    /// Enter game mode and run the post-select setup batch to completion, closing the setup
    /// window (_setupWindowActive) - mirrors PostSelectSetupTests. Until this window closes the
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
        Feed(GameModeEntry);   // C02+C01 at line start, already in game mode -> RoomEntered only
    }

    [Fact]
    public void RoomEntered_WithNoFexFollowing_SendsExplicitProbeAfterDelay()
    {
        EnterAndCloseSetupWindow();
        FeedRoomEntry();

        Advance(ProbeDelay - Tick);
        Assert.Equal(0, CountSent(FexProbe));   // the window has not elapsed yet

        Advance(Tick);
        Assert.Equal(1, CountSent(FexProbe));
    }

    [Fact]
    public void RoomEntered_FollowedByFexList_NeverSendsExplicitProbe()
    {
        EnterAndCloseSetupWindow();
        FeedRoomEntry();

        // Ordinary auto-fex-covered movement: the FEX list starts arriving inside the window.
        Feed(FexContextOpen);
        Feed("north\n");
        Feed(Pop);   // closes the FEX response scope

        Advance(ProbeDelay * 10);
        Assert.Equal(0, CountSent(FexProbe));
    }

    [Fact]
    public void OverlappingRoomEntries_ResetTimer_OnlyOneProbeFires()
    {
        var half = ProbeDelay / 2;

        EnterAndCloseSetupWindow();
        FeedRoomEntry();               // arms a deadline one full delay out
        Advance(half);
        Assert.Equal(0, CountSent(FexProbe));

        FeedRoomEntry();               // a second entry must REPLACE that deadline, not keep it

        Advance(half + Tick);          // now strictly past the FIRST entry's deadline
        Assert.Equal(0, CountSent(FexProbe));

        Advance(ProbeDelay);           // past the replacement deadline
        Assert.Equal(1, CountSent(FexProbe));

        Advance(ProbeDelay * 10);      // one-shot: the deadline does not come round again
        Assert.Equal(1, CountSent(FexProbe));
    }

    /// <summary>
    /// Smoke test for a pair of defences, not a pin on either: the exit path stops the timer AND
    /// OnRoomFexProbeDeadline returns early when not InGameMode. Removing either one on its own
    /// leaves this passing; only removing both fails it. Anyone tightening one of them needs
    /// another test, because this one will not notice.
    /// </summary>
    [Fact]
    public void GameModeExit_BeforeDeadline_SuppressesProbe()
    {
        EnterAndCloseSetupWindow();
        FeedRoomEntry();          // arms a deadline one full delay out
        Feed(AccountLogout);      // exits game mode well before it

        Advance(ProbeDelay * 10);
        Assert.Equal(0, CountSent(FexProbe));
    }

}
