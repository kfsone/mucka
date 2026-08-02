using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MudSharp.Models;

namespace Mucka.Core;

/// <summary>
/// Drives the "$clog eval &lt;itemid&gt;" sequence (see GameViewModel.HandleClogCommand):
/// looks at and weighs a carried item, then drops and re-gets it to measure its otherwise
/// unreported effect on effective strength/dexterity. MUD2's FES stats already reflect an
/// item's weight cost (Strength/Dexterity in GameStatsSnapshot are the *effective*, post-load
/// values — confirmed against the 'sc'/full-status command's "effective strength"/"effective
/// dexterity" text, which is what GameLineAnalyzer actually parses; MUD2's terser 'qs' quick-stats
/// reply looks similar to a human eye but isn't recognised by that parser), but it never tells the
/// player the cost directly, and some items appear to carry a str/dex modifier beyond what their
/// reported weight alone would predict (per the user's observation). Bracketing a drop/get pair
/// with a stats read on each side isolates that single item's contribution.
///
/// <para>Sequence: "look &lt;id&gt;" (description), "weigh &lt;id&gt;" (weight), then
/// "drop &lt;id&gt;" / "get &lt;id&gt;" bracketing the before/after FES reads. The get-back step
/// always runs (try/finally) even if the drop step times out or throws, so a fumbled eval never
/// leaves the item lying on the ground.</para>
///
/// <para>GameViewModel does a cheap local sanity check against the last carried-items (FEI)
/// snapshot before calling <see cref="RunAsync"/>, but that check is only a heuristic — FEI shows
/// an item's display name/label (e.g. "croquet mallet"), which need not equal the short id a
/// player can type for it (e.g. "mallet"). The authoritative check is the "identify &lt;id&gt;"
/// step below: MUD2 replies "The X is referred to as X when identification numbers are
/// requested." for a single carried/visible match, naming its canonical display text, which is
/// what we then use for the rest of the sequence (look/weigh/drop/get all accept it directly —
/// confirmed live). If the id instead names a whole weapon *class* (e.g. "axe" while carrying a
/// falchion and a halberd), MUD2 replies once per matching item — we detect that (more than one
/// match) and abort rather than guess, logging the matches as a class-membership observation
/// since that's independently useful research data.</para>
///
/// <para>Not reentrant — GameViewModel guards against a second eval starting while one is
/// running (SendLine while one is mid-flight would otherwise interleave two commands sequences
/// on the same wire and hopelessly confuse the line/stats matching below).</para>
/// </summary>
public sealed class ItemEvalSession
{
    private static readonly TimeSpan LineTimeout  = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StatsTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan IdentifyQuietPeriod = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan IdentifyTimeout = TimeSpan.FromSeconds(4);

    // "The weight of the staff is 4kg." / "...is 0.5kg." — MUD2 always names the item generically
    // ("the staff"), not by itemid, so we match on the surrounding phrase, not the noun.
    private static readonly Regex WeighRegex = new(
        @"weight of .*? is\s*(?<kg>[\d.]+)\s*kg",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "The croquet mallet is referred to as the croquet mallet when identification numbers are
    // requested." — group 1 is the canonical display name for whatever id/keyword we sent.
    private static readonly Regex IdentifyRegex = new(
        @"^(?:The\s+)?(?<name>.+?)\s+is referred to as\s+.+?\s+when identification numbers are requested\.?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly MuckaConnection _conn;
    private readonly Action<string> _report;

    public ItemEvalSession(MuckaConnection conn, Action<string> report)
    {
        _conn = conn;
        _report = report;
    }

    public async Task RunAsync(string itemId)
    {
        var identified = await SendAndCollectIdentifyAsync(itemId);
        if (identified.Count == 0)
        {
            _report($"[clog eval] '{itemId}' — 'identify' returned no match (not carried/visible, or unknown id). Aborting.");
            return;
        }
        if (identified.Count > 1)
        {
            _report($"[clog eval] '{itemId}' matched {identified.Count} items via 'identify' ({string.Join(", ", identified)})"
                + " — that looks like a weapon-class keyword, not one specific item. Re-run eval naming one of those directly. Aborting.");
            AppendLog(new
            {
                type = "identify_class",
                ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                token = itemId,
                matches = identified,
            });
            return;
        }

        var resolvedName = identified[0];
        if (!string.Equals(resolvedName, itemId, StringComparison.OrdinalIgnoreCase))
            _report($"[clog eval] '{itemId}' resolved to '{resolvedName}' via 'identify'.");

        var description = await SendAndCaptureLineAsync($"look {resolvedName}");
        if (description == null)
            _report($"[clog eval] no description line seen for 'look {resolvedName}' (timed out) — continuing anyway.");

        var weighLine = await SendAndCaptureLineAsync($"weigh {resolvedName}", WeighRegex.IsMatch);
        double? weightKg = null;
        if (weighLine != null)
        {
            var m = WeighRegex.Match(weighLine);
            if (m.Success && double.TryParse(m.Groups["kg"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var kg))
                weightKg = kg;
        }
        else
        {
            _report($"[clog eval] no weight line seen for 'weigh {resolvedName}' (timed out).");
        }

        // Baseline: read the live merged snapshot directly (MudSession.CurrentStats) rather than
        // sending 'qs' — MUD2's 'qs' reply ("eff str 45  eff dex 61  ...") is a different, terser
        // format than GameLineAnalyzer parses (it only recognises the 'sc'/full-status
        // "strength: N  effective strength: M" line), so a prior version that awaited a
        // StatsUpdated event after sending 'qs' always timed out and silently fell back to a
        // stale/empty snapshot — the reported "str: ? -> 45" bug. CurrentStats is always fresh
        // thanks to the client's periodic FES heartbeat, no round trip required.
        var before = _conn.CurrentStats;
        var afterDrop = before;
        try
        {
            afterDrop = await SendAndAwaitStatsAsync($"drop {resolvedName}", before);
        }
        finally
        {
            // Always attempt to restore the item, even if the drop step above timed out or threw —
            // an eval must never leave the character's inventory worse off than it found it.
            var afterGet = await SendAndAwaitStatsAsync($"get {resolvedName}", afterDrop);
            ReportAndLog(itemId, resolvedName, description, weightKg, before, afterDrop, afterGet);
        }
    }

    /// <summary>Send "identify &lt;token&gt;" and collect every matching reply line until a quiet
    /// period elapses (a class keyword like "axe" produces one reply per matching carried item,
    /// with no other terminator) or the hard timeout is hit. Returns the canonical display
    /// name(s) named in each reply — zero, one, or several.</summary>
    private async Task<List<string>> SendAndCollectIdentifyAsync(string token)
    {
        var names = new List<string>();
        var lastLineAt = DateTime.UtcNow;
        var echo = $"identify {token}";

        void Handler(StyledLine line)
        {
            var text = line.PlainText?.Trim();
            if (string.IsNullOrEmpty(text))
                return;
            if (string.Equals(text, echo, StringComparison.OrdinalIgnoreCase))
                return; // the server's echo of our own command
            var m = IdentifyRegex.Match(text);
            if (!m.Success)
                return;
            names.Add(m.Groups["name"].Value.Trim());
            lastLineAt = DateTime.UtcNow;
        }

        _conn.LineReady += Handler;
        try
        {
            _conn.SendLine(echo);
            var deadline = DateTime.UtcNow + IdentifyTimeout;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(150);
                if (names.Count > 0 && DateTime.UtcNow - lastLineAt > IdentifyQuietPeriod)
                    break;
            }
        }
        finally
        {
            _conn.LineReady -= Handler;
        }
        return names;
    }

    /// <summary>Send <paramref name="command"/> and return the first subsequent line accepted by
    /// <paramref name="accept"/> (default: first non-blank line that isn't the command's own
    /// echo). Returns null on timeout — the caller carries on best-effort.</summary>
    private async Task<string?> SendAndCaptureLineAsync(string command, Func<string, bool>? accept = null)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(StyledLine line)
        {
            var text = line.PlainText?.Trim();
            if (string.IsNullOrEmpty(text))
                return;
            if (string.Equals(text, command, StringComparison.OrdinalIgnoreCase))
                return; // the server's echo of our own command
            if (accept != null && !accept(text))
                return;
            tcs.TrySetResult(text);
        }

        _conn.LineReady += Handler;
        try
        {
            _conn.SendLine(command);
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(LineTimeout));
            return completed == tcs.Task ? await tcs.Task : null;
        }
        finally
        {
            _conn.LineReady -= Handler;
        }
    }

    /// <summary>Send <paramref name="command"/>, then force a fresh parseable stats reply with
    /// "sc" (MUD2's full-status/score command — its "strength: N  effective strength: M" /
    /// "dexterity: N  effective dexterity: M" lines are what GameLineAnalyzer actually parses;
    /// 'qs' looks similar to a human but is a different, unparsed format), and return the next
    /// merged snapshot that reports both Strength and Dexterity. Falls back to
    /// <paramref name="fallback"/> on timeout (both commands are still sent either way — this
    /// only affects what we report/log, never what we send).</summary>
    private async Task<GameStatsSnapshot> SendAndAwaitStatsAsync(string command, GameStatsSnapshot fallback)
    {
        var tcs = new TaskCompletionSource<GameStatsSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(GameStatsSnapshot s)
        {
            if (s.Strength.HasValue && s.Dexterity.HasValue)
                tcs.TrySetResult(s);
        }

        _conn.StatsUpdated += Handler;
        try
        {
            _conn.SendLine(command);
            _conn.SendLine("sc");
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(StatsTimeout));
            if (completed == tcs.Task)
                return await tcs.Task;
            _report($"[clog eval] timed out waiting for stats after '{command}' — using last known values.");
            return fallback;
        }
        finally
        {
            _conn.StatsUpdated -= Handler;
        }
    }

    private void ReportAndLog(
        string itemId, string resolvedName, string? description, double? weightKg,
        GameStatsSnapshot before, GameStatsSnapshot afterDrop, GameStatsSnapshot afterGet)
    {
        int? strCost = before.Strength.HasValue && afterDrop.Strength.HasValue
            ? before.Strength.Value - afterDrop.Strength.Value : null;
        int? dexCost = before.Dexterity.HasValue && afterDrop.Dexterity.HasValue
            ? before.Dexterity.Value - afterDrop.Dexterity.Value : null;
        var restored = afterGet.Strength == before.Strength && afterGet.Dexterity == before.Dexterity;

        _report($"[clog eval] {itemId}"
            + (description != null ? $" — {description}" : string.Empty));
        _report($"[clog eval]   weight: {(weightKg.HasValue ? $"{weightKg.Value:0.###}kg" : "unknown")}");
        _report($"[clog eval]   str: {before.Strength?.ToString() ?? "?"} -> {afterDrop.Strength?.ToString() ?? "?"}"
            + (strCost.HasValue ? $"  ({(strCost.Value >= 0 ? "-" : "+")}{Math.Abs(strCost.Value)} while carried)" : string.Empty));
        _report($"[clog eval]   dex: {before.Dexterity?.ToString() ?? "?"} -> {afterDrop.Dexterity?.ToString() ?? "?"}"
            + (dexCost.HasValue ? $"  ({(dexCost.Value >= 0 ? "-" : "+")}{Math.Abs(dexCost.Value)} while carried)" : string.Empty));
        _report(restored
            ? "[clog eval]   restored (str/dex back to baseline after 'get')."
            : $"[clog eval]   WARNING: stats did not fully restore after 'get' (str {afterGet.Strength}, dex {afterGet.Dexterity} vs baseline {before.Strength}/{before.Dexterity}) — check inventory.");

        AppendLog(new
        {
            type = "item_eval",
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            itemId,
            resolvedName,
            description,
            weightKg,
            strBefore = before.Strength,
            strAfterDrop = afterDrop.Strength,
            strCost,
            dexBefore = before.Dexterity,
            dexAfterDrop = afterDrop.Dexterity,
            dexCost,
            strAfterGet = afterGet.Strength,
            dexAfterGet = afterGet.Dexterity,
            restored,
        });
    }

    private void AppendLog(object entry)
    {
        if (!_conn.ClogEnabled)
            return; // eval is only reachable via $clog while clog is on, but guard anyway
        try
        {
            var dir = ClogWriter.GetClogDirectory();
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "items.jsonl");
            File.AppendAllText(path, JsonSerializer.Serialize(entry) + Environment.NewLine, new System.Text.UTF8Encoding(false));
        }
        catch
        {
            // Best-effort — never disrupt play over eval-log I/O failures.
        }
    }
}
