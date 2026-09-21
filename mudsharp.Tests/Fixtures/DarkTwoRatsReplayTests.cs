using System.Globalization;
using System.Text;
using MudSharp.Combat;
using MudSharp.Session;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// <c>dark-two-rats.c1</c>: a lit Cellar holding "An evil, black rat (rat18) bares its razor-sharp
/// incisors at you.", then "It's too dark to see now.", then a whole fight in which MUD2 names
/// nobody - "Something is about to attack you.", "Something hits you (65/90).", "You hit something
/// (10-14)." - and which nevertheless ends on two lines that DO name their Creature: "You have
/// killed the rat18." and, thirty seconds later, "You have killed the rat19." All verbatim, replayed
/// byte for byte through a real <see cref="MudSession"/> as
/// <see cref="AnonymousOpponentReplayTests"/> and <see cref="WyvernPoisonDeathReplayTests"/> are,
/// because the facts under test are protocol facts - which C1 code arrived in which frame.
///
/// <para>The three things this capture settles, and the three this file pins:</para>
/// <list type="number">
/// <item>Two 08.00 announcements, two Unseen opponents. The second is not the first announcing
/// again.</item>
/// <item>A kill line that NAMES a Creature while every swing line was anonymous identifies one
/// Unseen opponent, closes that one, and teaches its species which word it gets.</item>
/// <item>Once the last Unseen opponent has been identified the encounter closes. Before this, the
/// stored fight was one "something" row left Unresolved with 14 swings in it, beside two zero-swing
/// kill rows for rats the client thought it had never fought.</item>
/// </list>
///
/// <para>The player is in the DARK here, not blind: FES carries blind "N" for the whole fight and
/// darkness has no code, so nothing tells the tracker the player cannot see. That is why no
/// anonymous swing is attributed to anything - which is the right answer with no named Creature
/// engaged - and it is unrelated to the three behaviours above, all of which are driven by the 08.00
/// announcements and the named kill lines.</para>
/// </summary>
public sealed class DarkTwoRatsReplayTests
{
    /// <summary>One capture frame's aftermath: what the tracker published once the frame was fed.</summary>
    private sealed record Step(
        IReadOnlyList<CombatEvent> Events,
        bool InCombat,
        UnseenState Unseen,
        SomeKind? RatKind);

    private static List<Step> Replay()
    {
        var capture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Data", "dark-two-rats.c1");
        using var session = new MudSession(new MudSessionOptions
        {
            FesHeartbeatInterval = TimeSpan.FromSeconds(600),   // keep probe traffic out of the replay
        });

        var frame = new List<CombatEvent>();
        long captureTs = 0;
        session.CombatClock = () => DateTimeOffset.FromUnixTimeMilliseconds(captureTs).UtcDateTime;
        session.CombatEventOccurred += frame.Add;
        // A recording cannot answer this client's setup batch - see MudSession.SetupInjectEnabled.
        session.SetupInjectEnabled = false;

        var steps = new List<Step>();
        foreach (var rawLine in File.ReadLines(capture, Encoding.Latin1))
        {
            if (string.IsNullOrWhiteSpace(rawLine))
                continue;
            var parts = rawLine.Split(' ', 3);
            if (parts.Length < 3 || parts[1] != "rx")
                continue;
            captureTs = long.Parse(parts[0], CultureInfo.InvariantCulture);
            frame.Clear();
            session.Feed(CaptureBytes(parts[2]));
            steps.Add(new Step(frame.ToList(), session.InCombat, session.Unseen,
                session.SomeKindKnowledge.Known("rat18")));
        }
        return steps;
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

    /// <summary>The index of the frame carrying the kill line that names <paramref name="npc"/>. An
    /// index rather than the <see cref="Step"/> itself: <see cref="Step"/> is a record, so two quiet
    /// frames compare equal and "the steps from this one on" cannot be asked by identity.</summary>
    private static int KillFrame(List<Step> steps, string npc)
        => steps.FindIndex(s => s.Events.Any(e => e.Kind == CombatEventKind.Kill && e.NpcName == npc));

    // -- 1. two announcements, two Unseen opponents -------------------------------------------

    [Fact]
    public void TwoAnonymousAnnouncements_OpenTwoUnseenOpponents()
    {
        var steps = Replay();

        var announcements = steps
            .SelectMany(s => s.Events)
            .Where(e => e.Kind == CombatEventKind.FightStart && e.RawText == "Something is about to attack you.")
            .ToList();
        Assert.Equal(2, announcements.Count);
        Assert.All(announcements, e => Assert.Equal(AnonymousOpponent.Thing, e.NpcName));
        Assert.All(announcements, e => Assert.Equal(CombatActor.Npc, e.Actor));

        // One Unseen opponent after the first, two after the second - and two for every frame from
        // there to the first kill: a swing from an opponent that has already announced itself must
        // not be counted again, and neither kill line has arrived yet to take one away.
        var counts = steps.Select(s => s.Unseen.Something).ToList();
        Assert.Equal(1, counts.First(c => c > 0));
        var second = counts.IndexOf(2);
        Assert.InRange(second, 0, KillFrame(steps, "rat18") - 1);
        for (var i = second; i < KillFrame(steps, "rat18"); i++)
            Assert.Equal(2, counts[i]);
    }

    // -- 2. the named kill identifies one of them ---------------------------------------------

    [Fact]
    public void ANamedKill_IdentifiesOneUnseenOpponent_AndTeachesItsSpeciesTheWord()
    {
        var steps = Replay();
        var at = KillFrame(steps, "rat18");
        var afterRat18 = steps[at];

        // "You have killed the rat18." names a Creature no line of this fight ever named. It is one
        // of the two Unseen opponents, and the only word with any open is "something", so it is that
        // one: one fewer Unseen, and the species has learned its word.
        Assert.Equal(1, afterRat18.Unseen.Something);
        Assert.Equal(0, afterRat18.Unseen.Someone);
        Assert.Equal(SomeKind.Something, afterRat18.RatKind);

        // The other one is still swinging, so the encounter is still open and the word keeps its row.
        Assert.True(afterRat18.InCombat);
        Assert.DoesNotContain(afterRat18.Events, e => e.Kind == CombatEventKind.UnseenNamed);
        Assert.Contains(steps.Skip(at + 1).SelectMany(s => s.Events),
            e => e.Kind == CombatEventKind.HitByNpc && e.NpcName == AnonymousOpponent.Thing);
    }

    // -- 3. the encounter closes ---------------------------------------------------------------

    [Fact]
    public void TheLastNamedKill_RetiresTheWordsRow_AndClosesTheEncounter()
    {
        var steps = Replay();
        var at = KillFrame(steps, "rat19");
        var afterRat19 = steps[at];

        Assert.Equal(0, afterRat19.Unseen.Something);
        Assert.False(afterRat19.InCombat);

        // The word's row retires exactly once, on the frame that identified the last opponent behind
        // it - not once per kill.
        var retired = Assert.Single(steps.SelectMany(s => s.Events),
            e => e.Kind == CombatEventKind.UnseenNamed);
        Assert.Equal(AnonymousOpponent.Thing, retired.NpcName);
        Assert.Contains(afterRat19.Events, e => e.Kind == CombatEventKind.UnseenNamed);

        // And it stays closed: nothing after the second kill reopens it.
        Assert.All(steps.Skip(at), s => Assert.False(s.InCombat));
    }
}
