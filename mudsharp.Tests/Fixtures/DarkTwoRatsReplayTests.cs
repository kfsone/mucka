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
/// <para>What this capture pins, under docs/unseen-presentation.md:</para>
/// <list type="number">
/// <item>Two 08.00 announcements, two anonymous slots. The second is not the first announcing
/// again.</item>
/// <item>A kill line that NAMES a Creature with no row while a slot is open concludes that slot: the
/// count falls by one, the name goes into the slot's conclusion, and no row opens for it.</item>
/// <item>Once the last slot is concluded the encounter closes, and the history holds the group and
/// the two named conclusions.</item>
/// </list>
///
/// <para>The player is in the DARK here, not blind: FES carries blind "N" for the whole fight, so
/// the only thing that says the player cannot see is the prose - "It's too dark to see now.",
/// followed by an empty FEX exits reply and a FES row with effective dexterity at 36 of 91. The
/// fourth test pins that.</para>
/// </summary>
public sealed class DarkTwoRatsReplayTests
{
    /// <summary>One capture frame's aftermath: the events it produced, whether a fight was open, the
    /// unseen group as the last <see cref="CombatEventKind.UnseenChanged"/> so far carried it, and
    /// whether the player could see.</summary>
    private sealed record Step(
        IReadOnlyList<CombatEvent> Events,
        bool InCombat,
        UnseenSnapshot Unseen,
        bool CannotSee);

    private static List<Step> Replay()
    {
        var capture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Data", "dark-two-rats.c1");
        using var session = new MudSession(new MudSessionOptions
        {
            FesHeartbeatInterval = TimeSpan.FromSeconds(600),   // keep probe traffic out of the replay
        });

        var frame = new List<CombatEvent>();
        long captureTs = 0;
        var unseen = UnseenSnapshot.Empty;
        var cannotSee = false;
        session.CombatClock = () => DateTimeOffset.FromUnixTimeMilliseconds(captureTs).UtcDateTime;
        session.CombatEventOccurred += e =>
        {
            frame.Add(e);
            if (e.Unseen is { } u)
                unseen = u;
            if (e.Kind == CombatEventKind.SightLost)
                cannotSee = true;
            if (e.Kind == CombatEventKind.SightRegained)
                cannotSee = false;
        };
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
            steps.Add(new Step(frame.ToList(), session.InCombat, unseen, cannotSee));
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

    /// <summary>The index of the frame carrying the kill line that names <paramref name="npc"/>.</summary>
    private static int KillFrame(List<Step> steps, string npc)
        => steps.FindIndex(s => s.Events.Any(e => e.Kind == CombatEventKind.Kill && e.RawText == $"You have killed the {npc}."));

    // -- 1. two announcements, two slots ------------------------------------------------------

    [Fact]
    public void TwoAnonymousAnnouncements_OpenTwoSlots()
    {
        var steps = Replay();

        var announcements = steps
            .SelectMany(s => s.Events)
            .Where(e => e.Kind == CombatEventKind.FightStart && e.RawText == "Something is about to attack you.")
            .ToList();
        Assert.Equal(2, announcements.Count);
        Assert.All(announcements, e => Assert.Equal(AnonymousOpponent.Group, e.NpcName));
        Assert.All(announcements, e => Assert.Equal(CombatActor.Npc, e.Actor));

        // One unseen participant after the first, two after the second - and two for every frame
        // from there to the first kill: a swing from an opponent that has already announced itself
        // is not counted again, and neither kill line has arrived yet to take one away.
        var counts = steps.Select(s => s.Unseen.Count).ToList();
        Assert.Equal(1, counts.First(c => c > 0));
        var second = counts.IndexOf(2);
        Assert.InRange(second, 0, KillFrame(steps, "rat18") - 1);
        for (var i = second; i < KillFrame(steps, "rat18"); i++)
            Assert.Equal(2, counts[i]);
        // Every anonymous blow is the group's.
        Assert.All(steps.SelectMany(s => s.Events).Where(e => e.RawText.StartsWith("Something hits you", StringComparison.Ordinal)),
            e => Assert.Equal(AnonymousOpponent.Group, e.NpcName));
    }

    // -- 2. the named kill concludes one slot ---------------------------------------------------

    [Fact]
    public void ANamedKill_OfACreatureWithNoRow_ConcludesOneSlot_AndOpensNoRow()
    {
        var steps = Replay();
        var at = KillFrame(steps, "rat18");
        var afterRat18 = steps[at];

        // "You have killed the rat18." names a Creature no line of this fight ever named, while a
        // slot is open: it is that slot's conclusion. The event names the group, so no rat18 fight
        // is opened anywhere downstream; the name travels in the conclusion.
        var kill = Assert.Single(afterRat18.Events, e => e.Kind == CombatEventKind.Kill);
        Assert.Equal(AnonymousOpponent.Group, kill.NpcName);
        Assert.Equal(1, afterRat18.Unseen.Count);
        Assert.Contains(afterRat18.Unseen.Entries, e => e is { Kind: UnseenBadgeKind.Conclusion, Name: "rat18" });

        // The other one is still swinging, so the encounter is still open.
        Assert.True(afterRat18.InCombat);
        Assert.Contains(steps.Skip(at + 1).SelectMany(s => s.Events),
            e => e.Kind == CombatEventKind.HitByNpc && e.NpcName == AnonymousOpponent.Group);
    }

    // -- 3. the encounter closes ---------------------------------------------------------------

    [Fact]
    public void TheLastNamedKill_ClosesTheEncounter_WithTheGroupAndBothConclusionsInTheHistory()
    {
        var steps = Replay();
        var at = KillFrame(steps, "rat19");
        var afterRat19 = steps[at];

        Assert.Equal(0, afterRat19.Unseen.Count);
        Assert.False(afterRat19.InCombat);
        Assert.Equal(
            [(UnseenBadgeKind.Group, (string?)null), (UnseenBadgeKind.Conclusion, "rat18"), (UnseenBadgeKind.Conclusion, "rat19")],
            afterRat19.Unseen.Entries.Select(e => (e.Kind, e.Name)));
        Assert.All(afterRat19.Unseen.Entries.Skip(1), e => Assert.Equal(FightOutcome.Kill, e.Outcome));
        // Each conclusion's award is keyed by its own ending.
        Assert.All(afterRat19.Unseen.Entries.Skip(1), e => Assert.Single(e.Awards));

        // And it stays closed: nothing after the second kill reopens it.
        Assert.All(steps.Skip(at), s => Assert.False(s.InCombat));
    }

    // -- 4. the client knows it cannot see, from the prose alone -------------------------------

    [Fact]
    public void TheTooDarkLine_TellsTheTrackerThePlayerCannotSee_ForTheRestOfTheCapture()
    {
        var steps = Replay();

        // The lit Cellar frame that opens the capture: a coded room short, a non-empty exits reply,
        // and the player can see.
        Assert.False(steps[0].CannotSee);

        // "It's too dark to see now." is the whole of the signal. Every FES row after it carries
        // blind "N" and every exits reply after it is empty, so a flag fed from IsBlind alone would
        // read false for the entire fight - which is what it did.
        var dark = steps.FindIndex(s => s.Events.Any(
            e => e.Kind == CombatEventKind.SightLost && e.RawText == "(sight lost: too dark to see)"));
        Assert.InRange(dark, 1, KillFrame(steps, "rat18") - 1);
        Assert.All(steps.Skip(dark), s => Assert.True(s.CannotSee));

        // Nothing in the capture ever says the light came back, so nothing claims it did.
        Assert.DoesNotContain(steps.SelectMany(s => s.Events),
            e => e.Kind == CombatEventKind.SightRegained);
    }
}
