using System.Text;
using MudSharp.Combat;
using MudSharp.Session;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// The three sources that tell the combat tracker the player is BLIND, each pinned on its own: the
/// coded start, the coded end, and the FES flag behind both. A dark room does the same to the wire
/// and is the other half of the same flag - see <see cref="DarkSightWiringTests"/>. The flag is the
/// one reason the tracker accepts for handing an anonymous line to a sole engaged Creature, so both
/// edges of that knowledge have to be wired - and wired as a level, not an edge.
///
/// <para>The replay fixture (<see cref="AnonymousOpponentReplayTests"/>) cannot pin either source:
/// in the capture the coded line and a FES row with the flag set both land before the first
/// anonymous swing, so deleting either hook alone leaves it green. Here each source is fed by
/// itself, and each test asserts both the flag and what the flag did to the next anonymous blow.</para>
///
/// <para>The clearing case is the one that bit. The coded &lt;11.00&gt; line sets the tracker blind
/// without writing the FES snapshot's IsBlind, so a blind shorter than one heartbeat never shows FES
/// a Y; a hook that reacted only to a CHANGE in that snapshot would see none, leave the tracker
/// blind, and credit every later anonymous line for the rest of the session to whatever the player
/// happened to be fighting. The FES flag is asserted as a level on every genuine reply instead.</para>
/// </summary>
public sealed class BlindGateWiringTests : IDisposable
{
    // C02+C01 game-mode prompt variant - the post-character-select entry trigger.
    private static readonly byte[] GameModeEntry = [0x9D, 0x9C, 0xFF, 0xFF];
    // The frame prompt that leads every server frame (IsPartial '*'), taken from a live capture.
    private static readonly byte[] PromptBytes =
        [0x9C, 0xFF, 0xFF, 0x9C, 0x9D, 0xFF, 0xFF, 0x2A, 0xFF, 0xFF, 0xFF, 0xFF];
    // C12 C08 C01 -> a FES data line follows.
    private static readonly byte[] FesOpen = [0xA7, 0xA3, 0x9C, 0xFF, 0xFF];

    /// <summary>A C11 disabling-start bracket (11 00): <c>0xA6 0x9B FF FF phrase FF FF</c>. The
    /// wire form of the blind line, verbatim phrase.</summary>
    private static byte[] DisableStart(string phrase)
        => [0xA6, 0x9B, 0xFF, 0xFF, .. Encoding.Latin1.GetBytes(phrase), 0xFF, 0xFF, 0x0D, 0x0A];

    /// <summary>The matching disabling-END bracket (11 01): <c>0xA6 0x9C FF FF phrase FF FF</c>.
    /// Shared with deaf/dumb/cripple/glow, so the phrase is the only discriminator.</summary>
    private static byte[] DisableEnd(string phrase)
        => [0xA6, 0x9C, 0xFF, 0xFF, .. Encoding.Latin1.GetBytes(phrase), 0xFF, 0xFF, 0x0D, 0x0A];

    /// <summary>A FES reply row. Field 9 is the blind flag.</summary>
    private static byte[] Fes(char blind)
        => [.. FesOpen, .. Encoding.Latin1.GetBytes($"81 81 94 94 95 95 50 50 1785 {blind} N N N 5 S\n")];

    private static byte[] Text(string line) => Encoding.Latin1.GetBytes(line + "\r\n");

    private readonly MudSession _session = new(new MudSessionOptions
    {
        FesHeartbeatInterval = TimeSpan.FromSeconds(600),   // no probe traffic during the test
    });
    private readonly List<CombatEvent> _events = new();

    public BlindGateWiringTests()
    {
        _session.CombatEventOccurred += _events.Add;
        // Hand-fed bytes never answer the setup batch either - see MudSession.SetupInjectEnabled.
        _session.SetupInjectEnabled = false;
        _session.Feed(GameModeEntry);
        _session.Feed(PromptBytes);
    }

    public void Dispose() => _session.Dispose();

    private string? WhoHitThePlayer()
        => _events.LastOrDefault(e => e.Kind == CombatEventKind.HitByNpc)?.NpcName;

    [Fact]
    public void TheCodedBlindLine_AloneMakesTheSoleCreatureOwnAnAnonymousBlow()
    {
        _session.Feed(Text("You attack the rat0."));
        _session.Feed(DisableStart("You have suddenly and magically gone blind!"));
        _session.Feed(Text("Someone hits you (50/60)."));
        _session.Feed(PromptBytes);

        Assert.True(_session.Combat.CannotSee);
        Assert.Equal("rat0", WhoHitThePlayer());
    }

    [Fact]
    public void AFesRowWithTheFlagClear_ClearsABlindTheCodedLineSet()
    {
        _session.Feed(Text("You attack the rat0."));
        _session.Feed(DisableStart("You have suddenly and magically gone blind!"));
        // Sight returned inside one heartbeat: FES never saw a Y. The row says N, and that has to
        // be enough - reacting to N -> N as "no change" would leave the tracker blind.
        _session.Feed(Fes('N'));
        _session.Feed(Text("Someone hits you (50/60)."));
        _session.Feed(PromptBytes);

        Assert.False(_session.Combat.CannotSee);
        Assert.Equal(AnonymousOpponent.Person, WhoHitThePlayer());
    }

    [Fact]
    public void TheCodedSightLine_ClearsABlind_WithoutWaitingForAHeartbeat()
    {
        // "You have suddenly and magically regained your sight!" arrives under 11 01, which is
        // shared with deaf/dumb/cripple/glow, so the phrase is what identifies it. It is the
        // earliest statement that sight is back; the FES flag agrees up to a heartbeat later, and
        // in the gap MUD2 is already naming Creatures again.
        _session.Feed(Text("You attack the rat0."));
        _session.Feed(DisableStart("You have suddenly and magically gone blind!"));
        _session.Feed(DisableEnd("You have suddenly and magically regained your sight!"));
        _session.Feed(Text("Someone hits you (50/60)."));
        _session.Feed(PromptBytes);

        Assert.False(_session.Combat.CannotSee);
        Assert.Equal(AnonymousOpponent.Person, WhoHitThePlayer());
        Assert.Equal("(sight regained: sight returned)",
            _events.Last(e => e.Kind is CombatEventKind.SightLost or CombatEventKind.SightRegained).RawText);
    }

    /// <summary>
    /// The 11 01 code says only "a disabling effect ended" - it is shared with deaf, dumb, cripple
    /// and glow, and it also carries lines about OTHER Creatures. Three of those reach this test as
    /// separate guards, each of which alone would be enough to keep the blind set:
    ///
    /// <list type="bullet">
    /// <item>"The man has regained his visibleness!" - verbatim, a Creature's line, stopped by the
    /// decoder's target gate before any phrase table is consulted.</item>
    /// <item>"You have suddenly and magically regained your original state of not glowing!" -
    /// verbatim, the player's own, and glow's case is ahead of blindness's.</item>
    /// <item>A phrase nothing has ever sent. Synthetic on purpose, and it is the only one of the
    /// three that pins the "sight" needle itself: no other self-phrase 11 01 wording is on file, so
    /// without it the needle could be widened to "any end" and every test would stay green.</item>
    /// </list>
    /// </summary>
    [Theory]
    [InlineData("The man has regained his visibleness!")]
    [InlineData("You have suddenly and magically regained your original state of not glowing!")]
    [InlineData("You have suddenly and magically regained your hearing!")]
    public void ADisablingEffectThatIsNotBlindnessEnding_DoesNotClearABlind(string phrase)
    {
        _session.Feed(Text("You attack the rat0."));
        _session.Feed(DisableStart("You have suddenly and magically gone blind!"));
        _session.Feed(DisableEnd(phrase));
        _session.Feed(Text("Someone hits you (50/60)."));
        _session.Feed(PromptBytes);

        Assert.True(_session.Combat.CannotSee);
        Assert.Equal("rat0", WhoHitThePlayer());
    }

    [Fact]
    public void AFesRowWithTheFlagSet_AloneMakesTheSoleCreatureOwnAnAnonymousBlow()
    {
        // The relog-into-an-already-blind-persona case: no coded line ever arrives, only the flag.
        _session.Feed(Text("You attack the rat0."));
        _session.Feed(Fes('Y'));
        _session.Feed(Text("Someone hits you (50/60)."));
        _session.Feed(PromptBytes);

        Assert.True(_session.Combat.CannotSee);
        Assert.Equal("rat0", WhoHitThePlayer());
    }
}
