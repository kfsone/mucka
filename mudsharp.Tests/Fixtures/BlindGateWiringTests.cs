using System.Text;
using MudSharp.Combat;
using MudSharp.Session;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// The two sources that tell the combat tracker the PLAYER cannot see, each pinned on its own. Both
/// are blindness today (a dark room does the same to the wire and is wired next). The tracker's
/// anonymous-opponent rule hands a "Someone" line to the sole engaged Creature only while the player
/// is known unable to see, so both edges of that knowledge have to be wired - and wired as a level,
/// not an edge.
///
/// <para>The replay fixture (<see cref="AnonymousOpponentReplayTests"/>) cannot pin either source:
/// in the capture the coded line and a FES row with the flag set both land before the first
/// anonymous swing, so deleting either hook alone leaves it green. Here each source is fed by
/// itself.</para>
///
/// <para>The clearing case is the one that bit. The coded &lt;11.00&gt; line sets the tracker blind
/// without writing the FES snapshot's IsBlind, so a blind shorter than one heartbeat never shows FES
/// a Y; an EDGE test on that snapshot then sees no transition, never clears the tracker, and every
/// later anonymous line for the rest of the session is credited to whatever the player happens to
/// be fighting - the run-49 misattribution, silently reinstated. The FES flag is asserted as a
/// level on every genuine reply instead.</para>
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

        Assert.Equal("rat0", WhoHitThePlayer());
    }

    [Fact]
    public void AFesRowWithTheFlagClear_ClearsABlindTheCodedLineSet()
    {
        _session.Feed(Text("You attack the rat0."));
        _session.Feed(DisableStart("You have suddenly and magically gone blind!"));
        // Sight returned inside one heartbeat: FES never saw a Y. The row says N, and that has to
        // be enough - an edge test on the snapshot would see N -> N and leave the tracker blind.
        _session.Feed(Fes('N'));
        _session.Feed(Text("Someone hits you (50/60)."));
        _session.Feed(PromptBytes);

        Assert.Equal(AnonymousOpponent.Person, WhoHitThePlayer());
    }

    [Fact]
    public void AFesRowWithTheFlagSet_AloneMakesTheSoleCreatureOwnAnAnonymousBlow()
    {
        // The relog-into-an-already-blind-persona case: no coded line ever arrives, only the flag.
        _session.Feed(Text("You attack the rat0."));
        _session.Feed(Fes('Y'));
        _session.Feed(Text("Someone hits you (50/60)."));
        _session.Feed(PromptBytes);

        Assert.Equal("rat0", WhoHitThePlayer());
    }
}
