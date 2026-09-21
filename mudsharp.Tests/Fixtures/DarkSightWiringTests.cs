using System.Text;
using MudSharp.Combat;
using MudSharp.Session;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// A dark room anonymises every Creature line exactly as blindness does, and MUD2 says so in three
/// ways that carry no code and no FES column: its own prose. This file feeds each signal by itself
/// and asserts both the flag and what the flag did to the next anonymous blow -
/// <see cref="BlindGateWiringTests"/> does the same for the blind side.
///
/// <para>The asymmetry between the two directions is the point, and it is the operator's rule: prose
/// SETS darkness, and the corroborating signals - the FES dexterity collapse, the empty exits reply -
/// may only CLEAR a darkness whose end this client missed. Inverted, a dexterity buff or a room with
/// no exits would put the client in the dark and hand every anonymous blow to whatever it happened to
/// be fighting.</para>
/// </summary>
public sealed class DarkSightWiringTests : IDisposable
{
    // C02+C01 game-mode prompt variant - the post-character-select entry trigger.
    private static readonly byte[] GameModeEntry = [0x9D, 0x9C, 0xFF, 0xFF];
    // The frame prompt that leads every server frame (IsPartial '*'), taken from a live capture.
    private static readonly byte[] PromptBytes =
        [0x9C, 0xFF, 0xFF, 0x9C, 0x9D, 0xFF, 0xFF, 0x2A, 0xFF, 0xFF, 0xFF, 0xFF];
    // C12 C08 C01 -> a FES data line follows.
    private static readonly byte[] FesOpen = [0xA7, 0xA3, 0x9C, 0xFF, 0xFF];
    // C12 C08 C02 -> the FEX exits reply; one keyword line, then the scope pops.
    private static readonly byte[] FexOpen = [0xA7, 0xA3, 0x9D, 0xFF, 0xFF];
    private static readonly byte[] ScopeClose = [0xFF, 0xFF];

    /// <summary>A FES reply row. Field 9 is the blind flag, field 4 the EFFECTIVE dexterity.</summary>
    private static byte[] Fes(char blind, int dex)
        => [.. FesOpen, .. Encoding.Latin1.GetBytes($"81 81 94 94 {dex} 95 50 50 1785 {blind} N N N 5 S\n")];

    /// <summary>The exits reply, as the wire carries it: a lit room lists its directions, a dark one
    /// comes back with nothing at all (run 60, 4 of 4).</summary>
    private static byte[] Fex(string exits)
        => [.. FexOpen, .. Encoding.Latin1.GetBytes(exits + "\r\n"), .. ScopeClose];

    /// <summary>A coded room entry: C02+C01 at column 0, then the room's short description.</summary>
    private static byte[] RoomShort(string name)
        => [.. GameModeEntry, .. Encoding.Latin1.GetBytes(name + "\r\n")];

    private static byte[] Text(string line) => Encoding.Latin1.GetBytes(line + "\r\n");

    private readonly MudSession _session = new(new MudSessionOptions
    {
        FesHeartbeatInterval = TimeSpan.FromSeconds(600),   // no probe traffic during the test
    });
    private readonly List<CombatEvent> _events = new();

    public DarkSightWiringTests()
    {
        _session.CombatEventOccurred += _events.Add;
        // Hand-fed bytes never answer the setup batch either - see MudSession.SetupInjectEnabled.
        _session.SetupInjectEnabled = false;
        _session.Feed(GameModeEntry);
        // The entry code above leaves a room short pending until the next newline, so give it a real
        // one rather than letting the first line of a test be read as the room's name.
        _session.Feed(Text("Cellar."));
        _session.Feed(PromptBytes);
    }

    public void Dispose() => _session.Dispose();

    private string? WhoHitThePlayer()
        => _events.LastOrDefault(e => e.Kind == CombatEventKind.HitByNpc)?.NpcName;

    private string? LastSightRawText()
        => _events.LastOrDefault(e => e.Kind is CombatEventKind.SightLost or CombatEventKind.SightRegained)?.RawText;

    /// <summary>Engage a named Creature and put the player in the dark, the way the wire does.</summary>
    private void FightInTheDark()
    {
        _session.Feed(Text("You attack the rat0."));
        _session.Feed(Text("It's too dark to see now."));
    }

    // -- darkness starts: the two wordings ------------------------------------------------------

    [Fact]
    public void TheTooDarkLine_MakesTheSoleCreatureOwnAnAnonymousBlow()
    {
        FightInTheDark();
        _session.Feed(Text("Something hits you (50/60)."));
        _session.Feed(PromptBytes);

        Assert.True(_session.Combat.CannotSee);
        Assert.Equal("rat0", WhoHitThePlayer());
        Assert.Equal("(sight lost: too dark to see)", LastSightRawText());
    }

    [Fact]
    public void TheMoveInTheDarknessLine_SetsItToo()
    {
        // One per move for as long as it lasts, so it is also the only signal a player who walked
        // into the dark before this client connected will ever be given.
        _session.Feed(Text("You attack the rat0."));
        _session.Feed(Text("You move in the darkness..."));
        _session.Feed(Text("Something hits you (50/60)."));
        _session.Feed(PromptBytes);

        Assert.True(_session.Combat.CannotSee);
        Assert.Equal("rat0", WhoHitThePlayer());
        Assert.Equal("(sight lost: moving in the darkness)", LastSightRawText());
    }

    // -- darkness ends: prose, and the coded room entry behind it -------------------------------

    [Fact]
    public void TheLightEnoughLine_ClearsIt()
    {
        FightInTheDark();
        _session.Feed(Text("It's light enough to see now!"));
        _session.Feed(Text("Something hits you (50/60)."));
        _session.Feed(PromptBytes);

        Assert.False(_session.Combat.CannotSee);
        // Sighted with nothing faded, the word is unexplained and stays the word - the blow is not
        // handed to the rat.
        Assert.Equal(AnonymousOpponent.Thing, WhoHitThePlayer());
        Assert.Equal("(sight regained: light enough to see)", LastSightRawText());
    }

    [Fact]
    public void ACodedRoomEntry_ClearsIt()
    {
        // A lit room always sends its coded short description and a dark one never does, so this is
        // the backstop for a darkness whose "It's light enough to see now!" went unmatched.
        FightInTheDark();
        _session.Feed(RoomShort("Before gate"));
        _session.Feed(PromptBytes);

        Assert.False(_session.Combat.CannotSee);
        Assert.Equal("(sight regained: room entry)", LastSightRawText());
    }

    // -- the FES heartbeat says nothing about darkness ------------------------------------------

    [Fact]
    public void AFesRowWithTheBlindFlagClear_LeavesADarkRoomDark()
    {
        // The discriminating case for the whole feature. FES carries blind "N" for the entire dark
        // fight (run 60) - it has no darkness column at all - so a single flag fed from IsBlind
        // would be cleared by the first heartbeat and every later anonymous blow would stop being
        // attributed. Blind and dark are separate reasons and only their OR reaches the tracker.
        FightInTheDark();
        _session.Feed(Fes('N', 35));
        _session.Feed(Text("Something hits you (50/60)."));
        _session.Feed(PromptBytes);

        Assert.True(_session.Combat.CannotSee);
        Assert.Equal("rat0", WhoHitThePlayer());
    }

    [Fact]
    public void AFesDexterityCollapse_NeverSetsDark()
    {
        // The dexterity drop accompanies darkness (224 of 276 starts) but it is corroboration, not
        // detection: read the other way, an ordinary clumsify would blind the client to its own
        // opponent and teach that species the wrong word for good.
        _session.Feed(Text("You attack the rat0."));
        _session.Feed(Fes('N', 95));
        _session.Feed(Fes('N', 35));
        _session.Feed(Text("Something hits you (50/60)."));
        _session.Feed(PromptBytes);

        Assert.False(_session.Combat.CannotSee);
        Assert.Equal(AnonymousOpponent.Thing, WhoHitThePlayer());
    }

    [Fact]
    public void AFesDexterityRecovery_ClearsAStaleDark()
    {
        FightInTheDark();
        _session.Feed(Fes('N', 35));
        _session.Feed(Fes('N', 95));
        _session.Feed(PromptBytes);

        Assert.False(_session.Combat.CannotSee);
        Assert.Equal("(sight regained: dexterity restored)", LastSightRawText());
    }

    // -- the exits reply ------------------------------------------------------------------------

    [Fact]
    public void AnEmptyExitsReply_NeverSetsDark()
    {
        _session.Feed(Text("You attack the rat0."));
        _session.Feed(Fex(string.Empty));
        _session.Feed(Text("Something hits you (50/60)."));
        _session.Feed(PromptBytes);

        Assert.False(_session.Combat.CannotSee);
        Assert.Equal(AnonymousOpponent.Thing, WhoHitThePlayer());
    }

    [Fact]
    public void ANonEmptyExitsReply_ClearsAStaleDark()
    {
        FightInTheDark();
        _session.Feed(Fex("north south up"));
        _session.Feed(PromptBytes);

        Assert.False(_session.Combat.CannotSee);
        Assert.Equal("(sight regained: exits reply)", LastSightRawText());
    }

    [Fact]
    public void AnEmptyExitsReply_LeavesADarkRoomDark()
    {
        FightInTheDark();
        _session.Feed(Fex(string.Empty));
        _session.Feed(Text("Something hits you (50/60)."));
        _session.Feed(PromptBytes);

        Assert.True(_session.Combat.CannotSee);
        Assert.Equal("rat0", WhoHitThePlayer());
    }

    // -- the two reasons are independent --------------------------------------------------------

    /// <summary>A C11 disabling-end bracket (11 01): <c>0xA6 0x9C FF FF phrase FF FF</c>.</summary>
    private static byte[] DisableEnd(string phrase)
        => [0xA6, 0x9C, 0xFF, 0xFF, .. Encoding.Latin1.GetBytes(phrase), 0xFF, 0xFF, 0x0D, 0x0A];

    [Fact]
    public void SightReturningWhileStillInTheDark_LeavesThePlayerUnsighted()
    {
        _session.Feed(Text("You attack the rat0."));
        _session.Feed([0xA6, 0x9B, 0xFF, 0xFF,
            .. Encoding.Latin1.GetBytes("You have suddenly and magically gone blind!"), 0xFF, 0xFF, 0x0D, 0x0A]);
        _session.Feed(Text("It's too dark to see now."));
        _session.Feed(DisableEnd("You have suddenly and magically regained your sight!"));
        _session.Feed(Text("Something hits you (50/60)."));
        _session.Feed(PromptBytes);

        // Blindness ended; the room is still dark, so the anonymity has not.
        Assert.True(_session.Combat.CannotSee);
        Assert.Equal("rat0", WhoHitThePlayer());
    }
}
