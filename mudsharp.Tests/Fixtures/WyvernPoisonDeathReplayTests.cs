using System.Text;
using System.Text.Json;
using MudSharp.Combat;
using MudSharp.Models;
using MudSharp.Session;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// The poisoned-wyvern fight replayed from its own wire bytes, through the production
/// <see cref="MudSession"/> (real parser, real tracker, real wiring).
///
/// <para>Extracted verbatim from session-rec.mud2.co.uk.20260826-134435.jsonl (records 2905-3034 of
/// the owner's session, 2026-08-26): the wyvern turns on the player after a herb is fed to it, they
/// trade blows for ninety seconds, and then it dies of the poison with no kill line at all. Before
/// the fix this frame left the client "in combat" for the rest of the session.</para>
///
/// <para>Kept as bytes rather than as the hand-typed lines in <c>CombatTrackerTests</c> because the
/// two facts that make this frame hard are both protocol facts, and neither survives a transcript:
/// the death lines carry NO C1 code at all (bare text at base scope), while the trailing
/// "You can fight the wyvern no longer." is wrapped in 08.12 — the one coded statement in the whole
/// frame that a fight ended.</para>
/// </summary>
public sealed class WyvernPoisonDeathReplayTests
{
    private static readonly string CaptureFile =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Data", "wyvern-poison-death.jsonl");

    private static (List<bool> inCombat, List<CombatEvent> events, List<StyledLine> lines) Replay()
    {
        using var session = new MudSession(new MudSessionOptions
        {
            FesHeartbeatInterval = TimeSpan.FromSeconds(600),   // keep probe traffic out of the replay
        });

        var inCombat = new List<bool>();
        var events = new List<CombatEvent>();
        var lines = new List<StyledLine>();
        long captureTs = 0;

        // The capture's own timestamps, not the wall clock: an in-memory replay of a 90-second fight
        // finishes in milliseconds (see MudSession.CombatClock).
        session.CombatClock = () => DateTimeOffset.FromUnixTimeMilliseconds(captureTs).UtcDateTime;
        session.InCombatChanged += inCombat.Add;
        session.CombatEventOccurred += events.Add;
        session.LineReady += lines.Add;

        foreach (var rawLine in File.ReadLines(CaptureFile))
        {
            if (string.IsNullOrWhiteSpace(rawLine))
                continue;
            using var doc = JsonDocument.Parse(rawLine);
            var arr = doc.RootElement;
            if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() < 3 || arr[1].GetString() != "rx")
                continue;
            captureTs = arr[0].GetInt64();
            session.Feed(Encoding.Latin1.GetBytes(arr[2].GetString() ?? string.Empty));
        }

        return (inCombat, events, lines);
    }

    [Fact]
    public void TheFightOpensAndCloses_WithTheDeathAttributedToTheWyvern()
    {
        var (inCombat, events, _) = Replay();

        // Exactly one encounter, opened and CLOSED. The closing half is the whole bug: nothing in
        // this frame is a kill line, so before the fix the second element here did not exist.
        Assert.Equal([true, false], inCombat);

        var start = Assert.Single(events, e => e.Kind == CombatEventKind.FightStart);
        Assert.Equal("wyvern", start.NpcName);
        Assert.Equal(CombatActor.Npc, start.Actor);   // "The wyvern is staring at you ferociously."

        var died = Assert.Single(events, e => e.Kind == CombatEventKind.NpcDied);
        Assert.Equal("wyvern", died.NpcName);
        Assert.Equal("The wyvern drops dead, poisoned...", died.RawText);
        Assert.DoesNotContain(events, e => e.Kind == CombatEventKind.Kill);
    }

    [Fact]
    public void TheDeathLinesCarryNoC1Code_ButTheTrailingFightEndIs0812()
    {
        var (_, _, lines) = Replay();

        // Why the prose matchers cannot be retired in favour of the codes: MUD2 states the death
        // itself in untagged text. Both death lines come through as LineKind.Normal.
        foreach (var text in new[] { "The wyvern drops dead, poisoned...", "The wyvern has just passed on." })
        {
            var line = Assert.Single(lines, l => l.PlainText == text);
            Assert.Equal(LineKind.Normal, line.Kind);
        }

        // And why the codes cannot be retired in favour of the prose: this line is the only thing in
        // the frame that says, in the protocol rather than in English, that a fight has ended.
        var fightEnd = Assert.Single(lines, l => l.PlainText == "You can fight the wyvern no longer.");
        Assert.Equal(LineKind.FightEnd, fightEnd.Kind);
    }

    [Fact]
    public void TheVenomousStingIsNotAFightHit_AndIsStillUncounted()
    {
        // Recorded, not fixed. "The wyvern stings you with its venomous tail." is C07.02.00 - an
        // ISOLATED hit (the 07 "stings by objects of class STINGER" family), not a fight hit (08.03),
        // and it is followed by "Stamina=64/99." rather than the "(cur/max)" parenthetical the
        // combat-hit lines use. It landed twice in this fight for 15 and 20 stamina.
        //
        // So the encounter's damage-taken total does see it (the C89 stamina reading moves the
        // baseline), but no HitByNpc event is attributed to the wyvern, which means TheyHits and the
        // per-fight damage-taken bucket both understate what this creature actually did. Pinned here
        // so the gap is a known quantity rather than a surprise: the whole 07 family (bites, stings,
        // kicks, throws) is unparsed, and closing it is its own change.
        var (_, events, lines) = Replay();

        Assert.Equal(2, lines.Count(l => l.PlainText == "The wyvern stings you with its venomous tail."));
        Assert.DoesNotContain(events, e => e.RawText.Contains("venomous tail", StringComparison.Ordinal));
    }
}
