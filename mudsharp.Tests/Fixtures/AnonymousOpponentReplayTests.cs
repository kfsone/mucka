using System.Text;
using MudSharp.Combat;
using MudSharp.Models;
using MudSharp.Session;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// Two real captures of MUD2 anonymising an opponent, replayed byte for byte through a real
/// <see cref="MudSession"/>. Same construction and same reason as
/// <see cref="WyvernPoisonDeathReplayTests"/>: the facts under test are protocol facts - which C1
/// code did or did not arrive, and in which frame - and a hand-typed transcript cannot carry them.
///
/// <para><c>invisible-player-joins.c1</c>: the player is fighting zombie5 when an unseen attacker
/// joins. Every line about the attacker says "Someone"; no fade code ever arrives, because the
/// attacker was never visible to fade. Before the resolver was gated on the player's own blindness,
/// every one of those blows was credited to zombie5 - the sole engaged Creature - and the stored
/// fight closed with 7 incoming hits where the wire shows 1. That misattribution is what this file
/// pins against: one hit on the zombie, the rest on a participant named exactly what the game
/// named it.</para>
///
/// <para><c>blind-vs-water-snake.c1</c>: the player is blinded mid-fight by their own spell
/// backfiring. From then on the snake is "something" - the animal word, not "someone" - and until
/// that alternative was accepted, these lines produced no swing events at all: the blow was on the
/// wire and the panel never saw it.</para>
/// </summary>
public sealed class AnonymousOpponentReplayTests
{
    private static string Capture(string name)
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Data", name);

    private static List<CombatEvent> Replay(string capture)
    {
        using var session = new MudSession(new MudSessionOptions
        {
            FesHeartbeatInterval = TimeSpan.FromSeconds(600),   // keep probe traffic out of the replay
        });

        var events = new List<CombatEvent>();
        long captureTs = 0;
        session.CombatClock = () => DateTimeOffset.FromUnixTimeMilliseconds(captureTs).UtcDateTime;
        session.CombatEventOccurred += events.Add;
        // A recording cannot answer this client's setup batch, so the swallow window that hides its
        // echoes would never close - see MudSession.SetupInjectEnabled.
        session.SetupInjectEnabled = false;

        foreach (var rawLine in File.ReadLines(capture, Encoding.Latin1))
        {
            if (string.IsNullOrWhiteSpace(rawLine))
                continue;
            var parts = rawLine.Split(' ', 3);
            if (parts.Length < 3 || parts[1] != "rx")
                continue;
            captureTs = long.Parse(parts[0]);
            session.Feed(CaptureBytes(parts[2]));
        }
        return events;
    }

    /// <summary>Undoes the three escapes a <c>.c1</c> payload carries - see
    /// <see cref="WyvernPoisonDeathReplayTests"/>.</summary>
    private static byte[] CaptureBytes(string payload)
    {
        var bytes = new List<byte>(payload.Length);
        for (var i = 0; i < payload.Length; i++)
        {
            if (payload[i] == '\\' && i + 1 < payload.Length)
                bytes.Add(payload[++i] switch { 'r' => (byte)0x0D, 'n' => (byte)0x0A, _ => (byte)0x5C });
            else
                bytes.Add((byte)payload[i]);
        }
        return bytes.ToArray();
    }

    // -- an invisible player joins a fight the player can see --------------------------------

    [Fact]
    public void UnseenAttacker_OpensItsOwnFight_AndTheZombieKeepsOnlyItsOwnBlow()
    {
        var events = Replay(Capture("invisible-player-joins.c1"));

        var zombieHits = events.Where(e => e.Kind == CombatEventKind.HitByNpc && e.NpcName == "zombie5").ToList();
        var someoneHits = events.Where(e => e.Kind == CombatEventKind.HitByNpc && e.NpcName == "someone").ToList();

        // The one blow the wire attributes to the zombie by name. Everything else that landed on the
        // player in this fight was said by a line beginning "Someone".
        Assert.Single(zombieHits);
        Assert.StartsWith("The zombie5 hits you", zombieHits[0].RawText);

        Assert.True(someoneHits.Count >= 6, $"expected the unseen attacker's blows, got {someoneHits.Count}");
        Assert.All(someoneHits, e => Assert.StartsWith("Someone hits you", e.RawText));
    }

    [Fact]
    public void UnseenAttacker_IsFightingAlongsideTheZombie_NotAfterIt()
    {
        var events = Replay(Capture("invisible-player-joins.c1"));

        // Before the gate, "someone" only existed as a participant once the zombie had died and
        // there was no sole Creature left to hand its blows to. The first anonymous blow lands while
        // the zombie is still being named - so the anonymous fight has to be open at the same time.
        var firstSomeone = events.First(e => e.NpcName == "someone");
        var zombieResolved = events.First(e => e.Kind == CombatEventKind.Kill && e.NpcName == "zombie5");
        Assert.True(firstSomeone.TimestampUtc < zombieResolved.TimestampUtc,
            "the unseen attacker must be recognised while the zombie is still alive");

        // And recognised at its ANNOUNCEMENT, not its first blow: "Someone is about to attack you."
        // is coded 08.00 like every start, and it is the count of Unseen opponents.
        Assert.Equal(CombatEventKind.FightStart, firstSomeone.Kind);
        Assert.Equal(CombatActor.Npc, firstSomeone.Actor);
        Assert.Equal("Someone is about to attack you.", firstSomeone.RawText);
    }

    [Fact]
    public void UnseenAttacker_NeverBecomesTheZombie()
    {
        var events = Replay(Capture("invisible-player-joins.c1"));

        // The failure this file exists for, stated directly: no line that says "Someone" may be
        // attributed to a named Creature while the player can see.
        var leaked = events.Where(e => e.NpcName == "zombie5" && e.RawText is { } t && t.StartsWith("Someone", StringComparison.Ordinal)).ToList();
        Assert.Empty(leaked);
    }

    // -- the player is blinded while fighting an animal ---------------------------------------

    [Fact]
    public void BlindPlayer_AnimalOpponentsBlowsAreStillSwings_AndStillItsOwn()
    {
        var events = Replay(Capture("blind-vs-water-snake.c1"));

        // "Something hits you (116/120)." - the animal word. Before it was accepted this line matched
        // nothing and the blow vanished. With the player known blind and one Creature engaged, it is
        // that Creature's blow.
        var hit = Assert.Single(events, e => e.Kind == CombatEventKind.HitByNpc && e.RawText is { } t && t.StartsWith("Something hits you", StringComparison.Ordinal));
        Assert.Equal("water-snake1", hit.NpcName);

        var miss = Assert.Single(events, e => e.Kind == CombatEventKind.Miss && e.RawText == "You miss something.");
        Assert.Equal("water-snake1", miss.NpcName);

        // And no phantom participant called "something" alongside the real one.
        Assert.DoesNotContain(events, e => e.NpcName == "something");
    }

    // -- an invisible Creature strikes first ------------------------------------------------------

    /// <summary>
    /// <c>invisible-man-preemptive.c1</c>: run 59, the man already invisible from an earlier fight
    /// attacks the player in a tunnel. The only opening line is "Someone is about to attack you.",
    /// coded 08.00. Before it was recognised the fight opened 2.1 s later on the first miss, with no
    /// start event and no actor - the client did not know it had been attacked until it had been
    /// hit at.
    /// </summary>
    [Fact]
    public void UnseenCreatureStrikingFirst_OpensTheFightAtItsAnnouncement()
    {
        var events = Replay(Capture("invisible-man-preemptive.c1"));

        var start = Assert.Single(events, e => e.Kind == CombatEventKind.FightStart && e.RawText == "Someone is about to attack you.");
        Assert.Equal(CombatActor.Npc, start.Actor);
        Assert.Equal("someone", start.NpcName);

        // Everything that follows in that fight is the same participant's.
        var after = events.Where(e => e.TimestampUtc >= start.TimestampUtc && e.Kind is CombatEventKind.MissByNpc or CombatEventKind.Hit).ToList();
        Assert.NotEmpty(after);
        Assert.All(after, e => Assert.Equal("someone", e.NpcName));
    }
}
