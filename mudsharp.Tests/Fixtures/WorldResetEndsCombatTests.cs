using System.Text;
using MudSharp.Session;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// What a MUD2 world reset actually does to an open fight - replayed from the wire, not reasoned
/// about.
///
/// <para><b>The evidence.</b> Three complete reset observations exist in the capture corpus. All
/// three run the same way:</para>
/// <list type="number">
/// <item>C06 C04 <c>"Auto-reset initiated, you have 120 seconds to finish up. No further warnings
///   will be issued!"</c> - and the game means it: 120 seconds of completely ordinary play follow,
///   with no second broadcast of any kind (0 occurrences across 3 full countdowns). This is why
///   force-ending combat HERE is wrong.</item>
/// <item>At warning + 119.995 s and + 119.997 s in the two captures that timed both - i.e. exactly
///   +120 s - C06 C06 <c>"Something magical is happening."</c> arrives, followed in the same frame
///   by <c>(Persona saved on N).</c> and a prompt. That is the last in-world line, always.</item>
/// <item>~200 ms later (measured: 197 ms and 200 ms) the server prints
///   <c>Option (H for help): </c>. <b>The TCP connection is never dropped</b> - the socket simply
///   carries the outer MUD-Shell menu from that point on.</item>
/// </list>
///
/// <para>So the reset IS observable, and the claim in <c>MudSession</c>'s auto-reset comment - that
/// GameModeExited covers the real transition - is correct rather than assumed: the parser's
/// option-menu matcher fires on step 3, which force-ends the encounter. This test pins that chain
/// against the verbatim bytes so it cannot rot, because nothing else in the client watches for a
/// reset landing and the whole behaviour therefore rests on an incidental string match in a
/// different subsystem.</para>
///
/// <para><b>Bytes below are transcribed literally from mud2-multi-combat.jsonl</b>, records
/// ts=1785614794156 and ts=1785614794353. Do not "tidy" them: the stray leading space, the bare CR
/// + NUL, and the C1 code 00 (0x9B, Bartle: "Initialise ... whenever a program is forked or
/// terminated") sitting between the newline and the word "Option" are all really there, and the
/// last of those is exactly the sort of thing that could stop the column-0 matcher arming.</para>
/// </summary>
public class WorldResetEndsCombatTests : IDisposable
{
    private static readonly byte[] GameModeEntry = [0x9D, 0x9C, 0xFF, 0xFF];

    /// <summary>The reset instant: C06 C06 + the persona save + a prompt. One frame, verbatim.</summary>
    private static byte[] ResetLandedFrame()
    {
        var b = new List<byte> { 0xA1, 0xA1, 0xFF, 0xFF };
        b.AddRange(Encoding.Latin1.GetBytes("Something magical is happening."));
        b.AddRange([0x0D, 0x00, 0x0D, 0x0A, 0xFF, 0xFF]);
        b.AddRange(Encoding.Latin1.GetBytes("(Persona saved on "));
        b.AddRange([0xF4, 0x9C, 0xFF, 0xFF, 0xFE, 0x9E, 0xFF, 0xFF]);
        b.AddRange(Encoding.Latin1.GetBytes("18,402"));
        b.AddRange([0xFF, 0xFF, 0xFF, 0xFF]);
        b.AddRange(Encoding.Latin1.GetBytes(")."));
        b.AddRange([0x0D, 0x00, 0x0D, 0x0A, 0x9C, 0xFF, 0xFF, 0x9C, 0x9D, 0xFF, 0xFF]);
        b.AddRange(Encoding.Latin1.GetBytes("*"));
        b.AddRange([0xFF, 0xFF, 0xFF, 0xFF]);
        return b.ToArray();
    }

    /// <summary>~200 ms later: the shell menu prompt. One frame, verbatim.</summary>
    private static byte[] ShellEjectionFrame()
    {
        var b = new List<byte>();
        b.AddRange(Encoding.Latin1.GetBytes(" "));
        b.AddRange([0x0D, 0x0A, 0x9B, 0xFF, 0xFF]);
        b.AddRange(Encoding.Latin1.GetBytes("Option (H for help): "));
        return b.ToArray();
    }

    /// <summary>The real finish-up period is 120 s, which no unit test can sit through. Compressing
    /// it is the ONE liberty taken with the captured sequence: the countdown's anchor
    /// (<c>ResetClock.NoteAutoResetInitiated</c>) is "warning + FinishUpDuration" either way, so at
    /// 300 ms the C06 C06 line still arrives at the projected instant exactly as it does on the
    /// wire, and the corroboration under test is the same arithmetic.</summary>
    private static readonly TimeSpan FinishUp = TimeSpan.FromMilliseconds(300);

    private readonly MudSession _session = new(new MudSessionOptions
    {
        FesHeartbeatInterval   = TimeSpan.FromSeconds(60),
        StaleProbeDelay        = TimeSpan.FromSeconds(30),
        MinProbeSpacing        = TimeSpan.FromMilliseconds(50),
        InventoryProbeDebounce = TimeSpan.FromMilliseconds(400),
        ResetClock             = new ResetClockOptions { FinishUpDuration = FinishUp },
    });

    public void Dispose() => _session.Dispose();

    /// <summary>C06 C04 - the warning, verbatim wording from all 10 corpus occurrences (note the
    /// HYPHEN: the string is "Auto-reset", never "Auto reset").</summary>
    private void FeedWarning()
    {
        _session.Feed([0xA1, 0x9F, 0xFF, 0xFF]);
        _session.Feed(Encoding.Latin1.GetBytes(
            "Auto-reset initiated, you have 120 seconds to finish up. No further warnings will be issued!\r\n"));
    }

    private void OpenAFight()
    {
        _session.Feed(GameModeEntry);
        _session.Feed(Encoding.Latin1.GetBytes("You attack the zombie5, using the axe0 as a weapon.\r\n"));
        Assert.True(_session.InCombat, "the fight did not open - fixture problem, not a reset problem");
        Assert.True(_session.InGameMode);
    }

    [Fact]
    public void TheAutoResetWarning_DoesNotEndTheFight()
    {
        // C06 C04. 120 seconds of play still to come; ending here would silently drop weapon-equip
        // and other events for the rest of the fight, and the corpus is unambiguous that nothing
        // else happens for two minutes.
        OpenAFight();
        FeedWarning();
        Assert.True(_session.InCombat);
        // And it is still going a moment later - the warning starts no clock that closes it.
        Thread.Sleep(50);
        Assert.True(_session.InCombat);
    }

    [Fact]
    public void TheShellEjection_EndsTheFight()
    {
        // The whole reset transition, both frames, in order.
        OpenAFight();
        _session.Feed(ResetLandedFrame());
        _session.Feed(ShellEjectionFrame());
        Assert.False(_session.InGameMode);
        Assert.False(_session.InCombat);
    }

    [Fact]
    public void TheResetInstantItself_EndsTheFight_WithoutWaitingForTheShellPrompt()
    {
        // The captured order, compressed: warning, the finish-up period, then C06 C06 at the
        // projected instant. Ending here rather than only on the shell prompt closes the ~200 ms
        // window in which the client is still feeding a live encounter with lines from a world that
        // no longer exists.
        OpenAFight();
        FeedWarning();
        Thread.Sleep(FinishUp);
        _session.Feed(ResetLandedFrame());
        Assert.False(_session.InCombat);
    }

    [Fact]
    public void AfterTheResetInstant_TheShellPromptStillExitsGameModeCleanly()
    {
        // Belt and braces: ending the encounter early must not disturb the existing exit path.
        OpenAFight();
        FeedWarning();
        Thread.Sleep(FinishUp);
        _session.Feed(ResetLandedFrame());
        _session.Feed(ShellEjectionFrame());
        Assert.False(_session.InGameMode);
        Assert.False(_session.InCombat);
    }

    [Fact]
    public void SomethingMagical_WithNoResetDue_DoesNotEndTheFight()
    {
        // The guard that keeps a thinly-observed signal from ending a fight prematurely.
        // C06 C06 with no countdown anchored anywhere near now: the projection has nothing to
        // corroborate it with, so nothing happens and the fight stays open. Bartle's gloss for
        // 06 06 is generic ("Something magical is happening."), and the corpus has only two
        // occurrences of the line - both at a reset, but n=2 is not licence to close a fight on it
        // alone.
        OpenAFight();
        _session.Feed(ResetLandedFrame());
        Assert.True(_session.InCombat);
    }
}
