using System.Text;
using System.Text.Json;
using MudSharp.Combat;
using MudSharp.Session;
using Xunit.Abstractions;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// Replays the real RESEARCH/mud2-multi-combat.jsonl capture (4.2MB, ~16.8k lines, a full
/// MUD2 session covering dozens of combat encounters, one auto-reset, and a session-rec.jsonl
/// style [ts,"rx"|"tx"|"an",data] wire log — same format SessionCapture writes) through the
/// PRODUCTION MudSession (real MudStreamParser + real CombatTracker wiring, not a hand-built
/// harness), and cross-checks the live detector's counts against the offline ground truth
/// independently established in tools/combat/reduce_combat.py + NOTES.md/SUMMARY.md (58
/// encounters: 24 you-killed-them / 33 npc-fled / 1 withdrawn; 77 fights: 42 killed / 30
/// npc-fled / 4 your-fled / 1 withdrawn / 0 passes).
///
/// This is what actually caught the "closing event silently dropped" bug (see
/// CombatTrackerTests.Kill_EmitsBeforeInCombatFlipsFalse for the isolated regression) — replaying
/// the whole capture is the only way to be confident the live C# detector agrees with the
/// independently-verified Python reducer across a real, messy session (multi-NPC assists, a
/// mid-session auto-reset, weapon breaks/switches, NPC-initiated aggro, etc.), not just the
/// hand-picked lines in CombatTrackerTests.
/// </summary>
public sealed class CombatCaptureReplayTests(ITestOutputHelper output)
{
    private const string CaptureFile = @"G:\Source\mucka\RESEARCH\mud2-multi-combat.jsonl";

    [Fact]
    public void Replay_MatchesOfflineReducerGroundTruth()
    {
        if (!File.Exists(CaptureFile))
        {
            output.WriteLine($"SKIPPED: capture not present at {CaptureFile}");
            return;
        }

        using var session = new MudSession();

        int encounterStarts = 0, encounterEnds = 0;
        var eventCounts = new Dictionary<CombatEventKind, int>();
        var startCaptureTimestamps = new List<long>();
        bool justStarted = false;
        long currentCaptureTs = 0;

        // See MudSession.CombatClock: a fast in-memory replay's real elapsed time bears no
        // relation to the many-hour session the capture recorded, so CombatTracker's 5-second
        // post-kill grace window must be driven by the capture's own original timestamps here,
        // not the real wall clock the production default uses.
        session.CombatClock = () => DateTimeOffset.FromUnixTimeMilliseconds(currentCaptureTs).UtcDateTime;

        session.InCombatChanged += v =>
        {
            if (v) { encounterStarts++; justStarted = true; } else encounterEnds++;
        };
        session.CombatEventOccurred += e =>
        {
            if (e.Kind == CombatEventKind.FightStart && justStarted)
            {
                startCaptureTimestamps.Add(currentCaptureTs);
                justStarted = false;
            }
            eventCounts.TryGetValue(e.Kind, out var n);
            eventCounts[e.Kind] = n + 1;
        };

        foreach (var rawLine in File.ReadLines(CaptureFile))
        {
            if (string.IsNullOrWhiteSpace(rawLine))
                continue;

            using var doc = JsonDocument.Parse(rawLine);
            var arr = doc.RootElement;
            if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() < 3)
                continue;

            if (arr[1].GetString() != "rx")
                continue;

            currentCaptureTs = arr[0].GetInt64();
            var payload = arr[2].GetString() ?? string.Empty;
            session.Feed(Encoding.Latin1.GetBytes(payload));
        }

        output.WriteLine($"encounter starts (InCombatChanged true):  {encounterStarts}");
        output.WriteLine($"start timestamps: {string.Join(", ", startCaptureTimestamps)}");
        output.WriteLine($"encounter ends   (InCombatChanged false): {encounterEnds}");
        foreach (var (kind, count) in eventCounts.OrderBy(kv => kv.Key.ToString()))
            output.WriteLine($"  {kind,-14} {count}");

        // Every open encounter must close — no stuck "in combat" state by end of capture.
        Assert.Equal(encounterStarts, encounterEnds);

        // Ground truth from tools/combat (independently derived via reduce_combat.py against
        // the same capture): 58 encounters total.
        Assert.Equal(58, encounterStarts);

        // Fight-level outcome counts (tools/combat/SUMMARY.md v_summary_total).
        Assert.Equal(42, eventCounts.GetValueOrDefault(CombatEventKind.Kill));
        Assert.Equal(30, eventCounts.GetValueOrDefault(CombatEventKind.NpcFled));
        Assert.Equal(1, eventCounts.GetValueOrDefault(CombatEventKind.Withdrawn));
        // 4 your-fled FIGHT outcomes collapse to 3 literal flee EVENTS (one flee command
        // closed two concurrent rat fights at once) — confirmed during the offline analysis.
        Assert.Equal(3, eventCounts.GetValueOrDefault(CombatEventKind.YouFled));
        Assert.Equal(0, eventCounts.GetValueOrDefault(CombatEventKind.KilledByNpc));
    }
}
