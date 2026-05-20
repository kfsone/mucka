using System.Text;
using System.Text.Json;
using MudSharp.Models;
using Xunit.Abstractions;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// Integration replay test: feeds a real captured MUD2 session through MudStreamParser
/// and asserts protocol-correctness invariants.
/// </summary>
public sealed class ReplayTests(ITestOutputHelper output)
{
    private const string SessionFile =
        @"C:\Users\oliver.smith\AppData\Local\Temp\mucka\session-rec.mud2.co.uk.20260522-122208.jsonl";

    [Fact]
    public void SessionReplay_PassesProtocolInvariants()
    {
        if (!File.Exists(SessionFile))
        {
            output.WriteLine($"SKIPPED: session capture not present at {SessionFile}");
            return; // file not present; test is a no-op rather than a failure
        }

        var h = new ParserHarness();
        long totalRxBytes = 0;

        foreach (var rawLine in File.ReadLines(SessionFile))
        {
            if (string.IsNullOrWhiteSpace(rawLine)) continue;

            using var doc = JsonDocument.Parse(rawLine);
            var arr = doc.RootElement;
            if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() < 3) continue;

            var kind = arr[1].GetString();
            if (kind != "rx") continue;

            var payload = arr[2].GetString() ?? string.Empty;
            var bytes = Encoding.Latin1.GetBytes(payload);
            totalRxBytes += bytes.Length;
            h.Feed(bytes);
        }

        // ── Observation counters ──────────────────────────────────────────────
        int zeroStatsCount = h.Stats.Count(s => s.Stamina == 0 && s.MaxStamina == 0);

        int c1LeakLineCount = h.Lines.Count(l =>
            l.Spans.Any(sp => sp.Text.Any(c => c >= '\x9B' && c <= '\xFE')));

        int iacLeakLineCount = h.Lines.Count(l =>
            l.Spans.Any(sp => sp.Text.Any(c => c == '\xFF')));

        // Lines whose first span text starts with '*' after game mode was entered —
        // symptom of the prompt-preamble leak. Pre-game lines (e.g. login menu) may
        // legitimately start with '*' and are excluded.
        var gameModeLineStart = h.GameModeEnteredAtLineIndex >= 0 ? h.GameModeEnteredAtLineIndex : h.Lines.Count;
        int promptLeakLineCount = h.Lines.Skip(gameModeLineStart).Count(l =>
            !l.IsPartial && l.Spans.Count > 0 && l.Spans[0].Text.StartsWith('*'));

        output.WriteLine($"Total rx bytes fed:               {totalRxBytes}");
        output.WriteLine($"Total LineReadyEvent count:       {h.Lines.Count}");
        output.WriteLine($"Total StatsUpdatedEvent count:    {h.Stats.Count}");
        output.WriteLine($"Total OutgoingBytesEvent count:   {h.Outgoing.Count}");
        output.WriteLine($"Total GameModeEnteredEvent count: {h.GameModeEnteredCount}");
        output.WriteLine($"StatsUpdated with Sta=0&&MaxSta=0:{zeroStatsCount}");
        output.WriteLine($"Lines with C1 byte leak (9B-FE):  {c1LeakLineCount}");
        output.WriteLine($"Lines with IAC byte leak (FF):    {iacLeakLineCount}");
        output.WriteLine($"Lines with prompt-preamble leak:  {promptLeakLineCount}");
        output.WriteLine($"AccountId (C95 Rule A):           {h.Parser.CurrentAccountId ?? "<not set>"}");

        // ── Hard assertions ───────────────────────────────────────────────────
        Assert.True(h.GameModeEnteredCount >= 1,
            "Expected at least one GameModeEnteredEvent");

        Assert.True(h.Stats.Any(s => s.Stamina > 0),
            "Expected at least one StatsUpdatedEvent with Stamina > 0");

        Assert.True(h.Stats.Any(s => s.Score > 0),
            "Expected at least one StatsUpdatedEvent with Score > 0");

        Assert.True(h.Lines.Count >= 1,
            "Expected at least one LineReadyEvent");

        Assert.True(h.Outgoing.Count >= 1,
            "Expected at least one OutgoingBytesEvent (FES subscription)");

        Assert.True(h.Stats.Any(s => s.AccountId != null),
            "Expected at least one StatsUpdatedEvent with AccountId set (C95 Rule A)");

        Assert.Equal(0, c1LeakLineCount);
        Assert.Equal(0, iacLeakLineCount);
        Assert.Equal(0, promptLeakLineCount);
    }
}
