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
/// values — confirmed by comparing them against 'qs' quick-stats text), but it never tells the
/// player the cost directly, and some items appear to carry a str/dex modifier beyond what their
/// reported weight alone would predict (per the user's observation). Bracketing a drop/get pair
/// with a stats read on each side isolates that single item's contribution.
///
/// <para>Sequence: "look &lt;id&gt;" (description), "weigh &lt;id&gt;" (weight), then
/// "drop &lt;id&gt;" / "get &lt;id&gt;" bracketing the before/after FES reads. The get-back step
/// always runs (try/finally) even if the drop step times out or throws, so a fumbled eval never
/// leaves the item lying on the ground.</para>
///
/// <para>Requires the item to already be in the caller-supplied carried-items (FEI) list —
/// GameViewModel enforces this before calling <see cref="RunAsync"/>; eval only measures items
/// already held, never picks up something new from the room floor.</para>
///
/// <para>Not reentrant — GameViewModel guards against a second eval starting while one is
/// running (SendLine while one is mid-flight would otherwise interleave two commands sequences
/// on the same wire and hopelessly confuse the line/stats matching below).</para>
/// </summary>
public sealed class ItemEvalSession
{
    private static readonly TimeSpan LineTimeout  = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StatsTimeout = TimeSpan.FromSeconds(6);

    // "The weight of the staff is 4kg." / "...is 0.5kg." — MUD2 always names the item generically
    // ("the staff"), not by itemid, so we match on the surrounding phrase, not the noun.
    private static readonly Regex WeighRegex = new(
        @"weight of .*? is\s*(?<kg>[\d.]+)\s*kg",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly MuckaConnection _conn;
    private readonly Action<string> _report;
    private GameStatsSnapshot _lastStats = GameStatsSnapshot.Empty;
    private bool _subscribed;

    public ItemEvalSession(MuckaConnection conn, Action<string> report)
    {
        _conn = conn;
        _report = report;
    }

    public async Task RunAsync(string itemId)
    {
        EnsureSubscribed();

        var description = await SendAndCaptureLineAsync($"look {itemId}");
        if (description == null)
            _report($"[clog eval] no description line seen for 'look {itemId}' (timed out) — continuing anyway.");

        var weighLine = await SendAndCaptureLineAsync($"weigh {itemId}", WeighRegex.IsMatch);
        double? weightKg = null;
        if (weighLine != null)
        {
            var m = WeighRegex.Match(weighLine);
            if (m.Success && double.TryParse(m.Groups["kg"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var kg))
                weightKg = kg;
        }
        else
        {
            _report($"[clog eval] no weight line seen for 'weigh {itemId}' (timed out).");
        }

        var before = _lastStats;
        var afterDrop = before;
        try
        {
            afterDrop = await SendAndAwaitStatsAsync($"drop {itemId}");
        }
        finally
        {
            // Always attempt to restore the item, even if the drop step above timed out or threw —
            // an eval must never leave the character's inventory worse off than it found it.
            var afterGet = await SendAndAwaitStatsAsync($"get {itemId}");
            ReportAndLog(itemId, description, weightKg, before, afterDrop, afterGet);
        }
    }

    private void EnsureSubscribed()
    {
        if (_subscribed)
            return;
        _conn.StatsUpdated += s => _lastStats = s;
        _subscribed = true;
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

    /// <summary>Send <paramref name="command"/> and return the next FES snapshot that reports
    /// both Strength and Dexterity. Falls back to the last known snapshot on timeout (still
    /// sends the command either way — this only affects what we report/log, never what we send).</summary>
    private async Task<GameStatsSnapshot> SendAndAwaitStatsAsync(string command)
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
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(StatsTimeout));
            if (completed == tcs.Task)
                return await tcs.Task;
            _report($"[clog eval] timed out waiting for stats after '{command}' — using last known values.");
            return _lastStats;
        }
        finally
        {
            _conn.StatsUpdated -= Handler;
        }
    }

    private void ReportAndLog(
        string itemId, string? description, double? weightKg,
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
