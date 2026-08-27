using System.Text;
using MudSharp.Session;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// Fight ends at SESSION level: the poisoned-wyvern frame framed as the server frames it - prompt
/// bytes glued to the front of a frame's first line, which the death line is one of - and the
/// room-change backstop behind it.
///
/// <para>The lines here are the owner's own paste of an earlier occurrence (pitchfork, 5,201 points)
/// re-fed through the byte path to test the prompt gluing; the wording is his, the framing is
/// synthesised. For a fight that is real bytes end to end, see WyvernPoisonDeathReplayTests.</para>
///
/// <para>The backstop: in MUD2 you cannot walk out of a fight (owner) — movement is refused
/// while fighting, and leaving costs a flee, which prints its own line. So a room change proves the
/// fight is over regardless of which sentences the parser managed to match, which makes it the
/// backstop for the whole recurring class of bug in this area (a fight end phrased in a way nothing
/// in <c>CombatTracker</c> recognises, leaving the client "in combat" until logout).</para>
///
/// <para>These tests drive a real <see cref="MudSession"/> with protocol bytes, because the wiring
/// under test IS the session's: <c>RoomShortReady</c> → compare with the last room short →
/// <c>CombatTracker.NoteRoomChanged</c>. Testing the tracker method alone (CombatTrackerTests does
/// that) would not catch the case that matters most here, which is a `look` closing a live fight.</para>
/// </summary>
public class CombatSessionFightEndTests : IDisposable
{
    // C02+C01: enters game mode, and thereafter (at line start) opens a room-short line.
    private static readonly byte[] RoomShortCode = [0x9D, 0x9C, 0xFF, 0xFF];
    // The frame prompt that leads every server frame, taken from a live capture.
    private static readonly byte[] PromptBytes =
        [0x9C, 0xFF, 0xFF, 0x9C, 0x9D, 0xFF, 0xFF, 0x2A, 0xFF, 0xFF, 0xFF, 0xFF];

    // C08+C12 = "Fight ends - other", verbatim from the wire: 0xA3 0xA7 are the code components
    // (byte - 155 = 08, 12) and the doubled 0xFF is one telnet-escaped C1 terminator. Confirmed
    // in session-rec.mud2.co.uk.20260826-134435 in front of "You can fight the wyvern no
    // longer.", and in the older captures in front of the "him"/"her"/"it" forms.
    private static readonly byte[] FightEndOtherCode = [0xA3, 0xA7, 0xFF, 0xFF];
    private static readonly byte[] Pop = [0xFF, 0xFF];

    private const string Echoes = "auto fex\r\nscore\r\n";
    private const string AutoFexReply =
        "You will now get an automatic FEEXITS command performed every time you issue a movement command.\r\n";
    private const string ScoreSheet = "name:          Ollie\r\n";

    private readonly MudSession _session;
    private readonly List<bool> _inCombat = new();

    public CombatSessionFightEndTests()
    {
        _session = new MudSession(new MudSessionOptions
        {
            FesHeartbeatInterval = TimeSpan.FromSeconds(60),   // keep the heartbeat out of the way
        });
        _session.InCombatChanged += v => _inCombat.Add(v);

        // Enter game mode and run the post-select setup batch to completion, so its swallow window
        // is closed and ordinary game lines reach the combat tracker (mirrors PostSelectSetupTests).
        Feed(RoomShortCode);
        Prompt(); Feed(Echoes);
        Prompt(); Feed(AutoFexReply);
        Prompt(); Feed(ScoreSheet);
        Prompt();
    }

    public void Dispose() => _session.Dispose();

    private void Feed(byte[] data) => _session.Feed(data);
    private void Feed(string ascii) => _session.Feed(Encoding.Latin1.GetBytes(ascii));
    private void Prompt() => _session.Feed(PromptBytes);

    /// <summary>A room short description arriving at column 0, exactly as movement or `look` draws it.</summary>
    private void FeedRoomShort(string shortDescription)
    {
        Feed("\r\n");             // guarantee line start
        Feed(RoomShortCode);      // C02+C01 at line start → this line is a room short
        Feed(shortDescription + "\r\n");
    }

    private void StartFight()
    {
        Feed("You attack the wyvern, using the pitchfork as a weapon.\r\n");
        Assert.True(_session.InCombat);
    }

    /// <summary>
    /// The death line as the first line of its frame, which is where MUD2 glues the prompt on: this
    /// is the test that the prose matcher survives a prompt-prefixed frame rather than only matching
    /// hand-typed strings. Wording from the owner's paste; the prompt bytes are real, taken from a
    /// capture.
    /// </summary>
    [Fact]
    public void PoisonDeath_ArrivingBehindAFramePrompt_ClosesTheEncounter()
    {
        FeedRoomShort("A dank cave");

        Prompt();
        Feed("The wyvern hits you (41/99).\r\n");
        Feed("You hit the wyvern (10-14).\r\n");
        Feed("The pitchfork breaks to bits.\r\n");
        Feed("You cannot use the pitchfork to fight now!\r\n");
        Feed("The wyvern looks covered in wounds.\r\n");
        Assert.True(_session.InCombat);

        Prompt();
        Feed("The wyvern drops dead, poisoned...\r\n");

        Assert.False(_session.InCombat);
        Assert.Equal([true, false], _inCombat);
    }

    /// <summary>
    /// The fallback the whole LineKind.FightEnd change exists for, exercised through the REAL decoder
    /// rather than a hand-built StyledLine: C08.12 on the wire, wrapped around a sentence no regex in
    /// CombatTracker knows, with one creature engaged.
    ///
    /// <para>The code bytes are captured; the wording is invented, and has to be - the point is a
    /// phrasing nobody has observed, so there is nothing to quote. If MUD2 ever prints a real one it
    /// arrives coded exactly like this, closes the fight, and lands in the clog verbatim for somebody
    /// to add a pattern for.</para>
    ///
    /// <para>Without this the path is only covered by CombatTrackerTests constructing a tagged line
    /// directly: the capture's own 08.12 line trails a death the prose already matched, so it closes
    /// nothing there and the wiring - decoder to SetPendingKind to StyledLine.Kind to the tracker -
    /// would go unexercised end to end.</para>
    /// </summary>
    [Fact]
    public void CodedFightEnd_WithAWordingNothingMatches_ClosesTheFight()
    {
        FeedRoomShort("A dank cave");
        StartFight();

        Feed(FightEndOtherCode);
        Feed("The wyvern turns away, bored.");
        Feed(Pop);
        Feed("\r\n");

        Assert.False(_session.InCombat);
        Assert.Equal([true, false], _inCombat);
    }

    /// <summary>The same sentence WITHOUT the code must close nothing - otherwise the test above
    /// would be passing on some prose path and proving nothing about the tag.</summary>
    [Fact]
    public void TheSameWordingWithoutTheCode_ClosesNothing()
    {
        FeedRoomShort("A dank cave");
        StartFight();

        Feed("The wyvern turns away, bored.\r\n");

        Assert.True(_session.InCombat);
        Assert.Equal([true], _inCombat);
    }

    [Fact]
    public void MovingToADifferentRoom_ClosesAnEncounterTheParserFailedToClose()
    {
        FeedRoomShort("A dank cave");
        StartFight();

        // No fight-end line at all — the poisoned-wyvern case, or any end phrased in a way nothing
        // matches yet. The player walks out, which in MUD2 they could only do because the fight was
        // already over.
        FeedRoomShort("A dark forest");

        Assert.False(_session.InCombat);
        Assert.Equal([true, false], _inCombat);
    }

    [Fact]
    public void LookingAtTheSameRoom_DoesNotCloseALiveFight()
    {
        FeedRoomShort("A dank cave");
        StartFight();

        // `look` mid-fight reprints the room the player is already standing in. It is free, players
        // do it constantly while fighting, and it must not be read as movement — which is why the
        // backstop triggers on the room short CHANGING rather than merely arriving.
        FeedRoomShort("A dank cave");
        FeedRoomShort("A dank cave");

        Assert.True(_session.InCombat);
        Assert.Equal([true], _inCombat);
    }

    [Fact]
    public void MovingWhileNotFighting_IsASilentNoOp()
    {
        FeedRoomShort("A dank cave");
        FeedRoomShort("A dark forest");
        FeedRoomShort("A dank cave");

        Assert.False(_session.InCombat);
        Assert.Empty(_inCombat);   // the player walks between rooms all day; none of it is combat news
    }
}
