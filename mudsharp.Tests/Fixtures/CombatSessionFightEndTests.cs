using System.Text;
using MudSharp.Session;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// Fight ends at SESSION level: the poisoned-wyvern frame framed as the server frames it - prompt
/// bytes glued to the front of a frame's first line, which the death line is one of - and the
/// room-change backstop behind it.
///
/// <para>The lines here replay an earlier occurrence (pitchfork, 5,201 points) through the byte
/// path to test the prompt gluing; the framing is synthesised. For a fight that is real bytes end
/// to end, see WyvernPoisonDeathReplayTests.</para>
///
/// <para>The backstop: in MUD2 you cannot walk out of a fight - movement is refused while
/// fighting, and leaving costs a flee, which prints its own line. So a room change proves the
/// fight is over regardless of which sentences the parser managed to match, which makes it the
/// backstop for a fight end phrased in a way nothing in <c>CombatTracker</c> recognises, which
/// would otherwise leave the client "in combat" until logout.</para>
///
/// <para>These tests drive a real <see cref="MudSession"/> with protocol bytes, because the wiring
/// under test IS the session's: <c>RoomShortReady</c> -> compare with the last room short ->
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
    // C04+C00+C05 = "Normal creatures becoming invisible", verbatim from wire.db session 12 in front
    // of "The man fades from view."
    private static readonly byte[] CreatureInvisibleCode = [0x9F, 0x9B, 0xA0, 0xFF, 0xFF];
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

    public void Dispose()
    {
        _session.Dispose();
        GC.SuppressFinalize(this);
    }

    private void Feed(byte[] data) => _session.Feed(data);
    private void Feed(string ascii) => _session.Feed(Encoding.Latin1.GetBytes(ascii));
    private void Prompt() => _session.Feed(PromptBytes);

    /// <summary>A room short description arriving at column 0, exactly as movement or `look` draws it.</summary>
    private void FeedRoomShort(string shortDescription)
    {
        Feed("\r\n");             // guarantee line start
        Feed(RoomShortCode);      // C02+C01 at line start -> this line is a room short
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
    /// hand-typed strings. The wording and prompt bytes are both taken from a capture.
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
    /// Exercises the C08.12 fight-end fallback through the REAL decoder rather than a hand-built
    /// StyledLine: C08.12 on the wire, wrapped around a sentence no regex in CombatTracker knows,
    /// with one creature engaged.
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

        // No fight-end line at all - the poisoned-wyvern case, or any end phrased in a way nothing
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
        // do it constantly while fighting, and it must not be read as movement - which is why the
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

    /// <summary>
    /// The fight that produced all of this, through the real decoder: a man turns invisible under
    /// C04.00.05 and MUD2 stops naming him, so every line to the end of the fight - the end included
    /// - says "someone" instead. The client that met it never closed the encounter.
    ///
    /// <para>Driven end to end rather than at the tracker, because the wiring is the point: code
    /// bytes through the decoder, SetPendingKind, StyledLine.Kind, the tracker's anonymous
    /// resolution, out to the session's InCombat. The withdraw is fed as plain text on purpose -
    /// this is the PROSE path, the one that was missing, and if it ever passes on the strength of
    /// the fight-end code instead the test has stopped testing what it says it does.</para>
    /// </summary>
    [Fact]
    public void AnInvisibleOpponentsWithdraw_ClosesTheEncounter()
    {
        FeedRoomShort("Badly-paved road");

        Feed("The man is moving towards you ferociously.\r\n");
        Feed("The man misses you.\r\n");
        Feed("You hit the man (1-4).\r\n");
        Assert.True(_session.InCombat);

        Prompt();
        Feed("The man makes some magical gestures.\r\n");
        Feed(CreatureInvisibleCode);
        Feed("The man fades from view.");
        Feed(Pop);
        Feed("\r\n");

        // Same fight, same creature, no name.
        Prompt();
        Feed("Someone hits you (103/105).\r\n");
        Feed("You miss someone.\r\n");
        Feed("Someone offers to withdraw from your fight if you do likewise.\r\n");
        Assert.True(_session.InCombat);

        Prompt();
        Feed("You withdraw from your fight with someone, and that person does too.\r\n");

        Assert.False(_session.InCombat);
        Assert.Equal([true, false], _inCombat);
    }

    /// <summary>
    /// And the floor under it. Whatever ended an anonymous fight - a wording nobody has seen, a wiz
    /// moving the player, anything - the player standing somewhere else is proof it ended, because
    /// in MUD2 you cannot walk out of a fight: "You can't just leave in the middle of a fight! You
    /// have to flee!" is what trying earns, verbatim from the same session.
    /// </summary>
    [Fact]
    public void WalkingOutOfAnInvisibleFight_ClosesIt_WhateverEndedIt()
    {
        FeedRoomShort("Badly-paved road");

        Feed("The man is moving towards you ferociously.\r\n");
        Feed(CreatureInvisibleCode);
        Feed("The man fades from view.");
        Feed(Pop);
        Feed("\r\n");
        Feed("Someone hits you (103/105).\r\n");
        Assert.True(_session.InCombat);

        FeedRoomShort("Entrance to badger's sett");

        Assert.False(_session.InCombat);
        Assert.Equal([true, false], _inCombat);
    }
}
