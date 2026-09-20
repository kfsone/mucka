using System.Globalization;
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
/// <para>Origin: session-rec.mud2.co.uk.20260826-134435, records 2905-3034, from a 2026-08-26
/// session. The wyvern turns on the player after a herb is fed to it, they trade blows for ninety
/// seconds, and then it dies of the poison with no kill line at all - so a fight-end detector relying
/// on a kill line alone would leave the client "in combat" for the rest of the session.</para>
///
/// <para><b>The fixture is a redaction of that, not a copy of it.</b> Six <c>rx</c> frames are kept,
/// byte-for-byte and in their original order, and everything else in those 130 records is gone: every
/// <c>tx</c> record of what the player typed, the FES stat rows and FEI carry lists, the FEW who-list
/// and the other personas on it, a line of speech, and the potion/wafer/urn business that has nothing
/// to do with the fight. The death frame itself is truncated at its own prompt, dropping the carry
/// list that followed it in the same record. What is left is six frames of one creature fighting one
/// player, which is exactly and only what the three tests below read.</para>
///
/// <para>The six, in order: the aggro line that opens the encounter (08.00); one ordinary
/// miss-for-miss exchange; the two venomous stings (07.02.00) that
/// <see cref="TheVenomousStingIsNotAFightHit_AndIsStillUncounted"/> exists for; an exchange carrying a
/// real fight hit (08.03) and a real player hit (08.01), so the no-event-for-the-sting assertion is
/// measured against a pipeline visibly capable of producing one; and the death frame. The ninety
/// seconds of further blows between them were dropped: they change none of the three results, and
/// every one of them is a slab of somebody's play session.</para>
///
/// <para><b>Why the second frame is not optional.</b> A session's FIRST prompt is never emitted as a
/// partial line - the capture is still open when the feed runs dry, and the next frame's arrival
/// spills the captured '*' into the head of its first line. So whichever frame follows the aggro line
/// arrives as "*&lt;that line&gt;" and does not parse. In the full capture that landed on a command echo
/// nobody reads; here it would land on the first sting and cost that test its second occurrence. The
/// miss-for-miss frame is real bytes from the same fight, holds nothing but the two creatures, and
/// puts the artefact back where it does no harm. (The swallowed prompt is pre-existing parser
/// behaviour, not an artefact of this fixture - noted so the next person to trim this file does not
/// lose an hour to it.)</para>
///
/// <para>Kept as bytes rather than as the hand-typed lines in <c>CombatTrackerTests</c> because the
/// two facts that make this frame hard are both protocol facts, and neither survives a transcript:
/// the death lines carry NO C1 code at all (bare text at base scope), while the trailing
/// "You can fight the wyvern no longer." is wrapped in 08.12 - the one coded statement in the whole
/// frame that a fight ended. That is also why the file is committed rather than gitignored with the
/// tests skipping when it is absent: a protocol regression test that silently passes on a fresh clone
/// is not a test.</para>
/// </summary>
public sealed class WyvernPoisonDeathReplayTests
{
    private static readonly string CaptureFile =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Data", "wyvern-poison-death.c1");

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
        // A recording cannot answer this client's setup batch, so the swallow window that hides its
        // echoes would never close - see MudSession.SetupInjectEnabled.
        session.SetupInjectEnabled = false;
        session.InCombatChanged += inCombat.Add;
        session.CombatEventOccurred += events.Add;
        session.LineReady += lines.Add;

        // One record per line: ts_ms, direction, then the server's own bytes - see CaptureBytes.
        foreach (var rawLine in File.ReadLines(CaptureFile, Encoding.Latin1))
        {
            if (string.IsNullOrWhiteSpace(rawLine))
                continue;
            var parts = rawLine.Split(' ', 3);
            if (parts.Length < 3 || parts[1] != "rx")
                continue;
            captureTs = long.Parse(parts[0], CultureInfo.InvariantCulture);
            session.Feed(CaptureBytes(parts[2]));
        }

        return (inCombat, events, lines);
    }

    /// <summary>Undoes the three escapes a <c>.c1</c> payload carries: a backslash, and the CR and
    /// LF that would otherwise split a record. Latin-1, so a char IS its byte.</summary>
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

    [Fact]
    public void TheFightOpensAndCloses_WithTheDeathAttributedToTheWyvern()
    {
        var (inCombat, events, _) = Replay();

        // Exactly one encounter, opened and CLOSED. The closing half is the hard part: nothing in
        // this frame is a kill line, so a detector relying on one would never produce the second
        // element here.
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
        // combat-hit lines use. It landed twice in this fight, for 15 and 20 stamina - figures read
        // off the FULL session, whose intervening stat rows the fixture no longer carries; both
        // sting frames themselves are here byte-for-byte, which is what this test reads.
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
