using System.Globalization;
using MudSharp.Combat;

namespace Mucka.ViewModels;

/// <summary>
/// Builds the clog window's readout as styled lines (see <see cref="ClogLine"/>).
///
/// <para>Layout intent, per the user's review of the first pass: labels were eating most of the
/// width, and the panel was full of things that do not help mid-swing (absolute Sta/Str/Dex/Mag,
/// carry, level, games played, a redundant "Combat" heading under a window already titled Clog).
/// Colour now carries what labels used to — friendly for the player's side, hostile for the NPC's —
/// and only stat DEFICITS are shown, since a penalty is actionable where a raw number is not.</para>
///
/// <para>Pure string/span building with no MAUI types, so it is linked into mudsharp.Tests.</para>
/// </summary>
internal static class CombatHistoryFormatter
{
    private const string NoValue = "--";

    /// <summary>Assembles the whole readout, top to bottom.</summary>
    public static List<ClogLine> Build(
        CombatEncounterSnapshot snapshot,
        CombatStatDeficits deficits,
        CombatHistoryContext history)
    {
        var lines = new List<ClogLine>(24);
        if (!snapshot.HasEncounter)
            return lines;

        AppendHeadline(lines, snapshot);
        AppendExchange(lines, snapshot);
        AppendDeficits(lines, deficits);

        if (!history.HasAnything)
            return lines;

        lines.Add(ClogLine.Blank);
        AppendHistoryHeading(lines, history);
        AppendHistoryComparison(lines, snapshot, history);
        AppendWeaponTable(lines, snapshot, history);
        return lines;
    }

    /// <summary>"{weapon} vs {live targets}, {dead targets}    {elapsed}". Dead targets sort to the
    /// right of the list and render struck through, so a glance says both who is left and what has
    /// already gone down without spending a line on either.</summary>
    private static void AppendHeadline(List<ClogLine> lines, CombatEncounterSnapshot snapshot)
    {
        var spans = new List<ClogSpan>(8)
        {
            new(string.IsNullOrWhiteSpace(snapshot.CurrentWeapon) ? "unarmed" : snapshot.CurrentWeapon!,
                ClogTone.Friendly),
            new(" vs ", ClogTone.Dim),
        };

        var written = 0;
        // Live first, then resolved — the ordering IS the information here.
        foreach (var fight in OrderedTargets(snapshot.Fights))
        {
            if (written++ > 0)
                spans.Add(new ClogSpan(", ", ClogTone.Dim));
            spans.Add(new ClogSpan(fight.NpcName, ClogTone.Hostile, Strike: fight.IsResolved));
        }

        if (written == 0)
            spans.Add(new ClogSpan("--", ClogTone.Dim));

        spans.Add(new ClogSpan("  " + Duration(snapshot.Duration.TotalSeconds), ClogTone.Dim));
        lines.Add(new ClogLine(spans));
    }

    private static IEnumerable<FightSnapshot> OrderedTargets(IReadOnlyList<FightSnapshot> fights)
    {
        foreach (var fight in fights)
        {
            if (!fight.IsResolved)
                yield return fight;
        }

        foreach (var fight in fights)
        {
            if (fight.IsResolved)
                yield return fight;
        }
    }

    /// <summary>Two symmetrical lines, one per side. The first pass reported the player's dps but not
    /// the NPC's, which is exactly half of the question "am I winning this".</summary>
    private static void AppendExchange(List<ClogLine> lines, CombatEncounterSnapshot snapshot)
    {
        lines.Add(ExchangeLine("you ", ClogTone.Friendly, snapshot.YouHits, snapshot.YouMisses,
            snapshot.YouHitRate, snapshot.ApproxDamageDone, snapshot.ApproxDps));
        lines.Add(ExchangeLine("them", ClogTone.Hostile, snapshot.TheyHits, snapshot.TheyMisses,
            snapshot.TheyHitRate, snapshot.ApproxDamageTaken, snapshot.TheirApproxDps));
    }

    private static ClogLine ExchangeLine(
        string label, ClogTone tone, int hits, int misses, double hitRate, double damage, double dps)
        => ClogLine.Of(
            new ClogSpan(label, tone),
            new ClogSpan($" {hits,2}h", ClogTone.Value),
            new ClogSpan($" {misses,2}m", ClogTone.Dim),
            new ClogSpan($" {Percent(hits + misses == 0 ? null : hitRate),4}", ClogTone.Value),
            new ClogSpan($" {Num(damage),6}", ClogTone.Value),
            new ClogSpan($" {Num(dps)}/s", ClogTone.Dim));

    /// <summary>Only rendered when something is actually costing the player stats. A line that always
    /// reads "0" is a line that stops being read.</summary>
    private static void AppendDeficits(List<ClogLine> lines, CombatStatDeficits deficits)
    {
        if (!deficits.HasStatDelta && !deficits.HasLoad)
            return;

        var spans = new List<ClogSpan>(6) { new("load", ClogTone.Dim) };

        if (deficits.StrengthDelta is int strength && strength != 0)
            spans.Add(new ClogSpan($" str {Signed(strength)}", strength < 0 ? ClogTone.Warn : ClogTone.Good));
        if (deficits.DexterityDelta is int dexterity && dexterity != 0)
            spans.Add(new ClogSpan($" dex {Signed(dexterity)}", dexterity < 0 ? ClogTone.Warn : ClogTone.Good));

        // The cause, and the fix: dropping it is standard practice before a real fight.
        if (deficits.WeightCarriedGrams is int grams && grams > 0)
            spans.Add(new ClogSpan($"  {grams}g", ClogTone.Dim));
        if (deficits.ObjectsCarried is int objects && objects > 0)
            spans.Add(new ClogSpan($" {objects}obj", ClogTone.Dim));

        if (spans.Count > 1)
            lines.Add(new ClogLine(spans));
    }

    /// <summary>Names what the medians below describe, and says when the instance is speaking for
    /// itself rather than borrowing its group's numbers.</summary>
    private static void AppendHistoryHeading(List<ClogLine> lines, CombatHistoryContext history)
    {
        if (history.PreferInstance)
        {
            lines.Add(ClogLine.Of(
                new ClogSpan(history.InstanceName, ClogTone.Heading),
                new ClogSpan($" {Fights(history.Instance.FightCount)}", ClogTone.Dim),
                new ClogSpan($"  ({history.GroupName} {history.Group.FightCount})", ClogTone.Dim)));
            return;
        }

        var detail = history.Group.SampleSize == history.Group.FightCount
            ? string.Empty
            : $", {history.Group.SampleSize} detailed";
        lines.Add(ClogLine.Of(
            new ClogSpan(history.GroupName, ClogTone.Heading),
            new ClogSpan($" {Fights(history.Group.FightCount)}{detail}", ClogTone.Dim)));
    }

    private static void AppendHistoryComparison(
        List<ClogLine> lines, CombatEncounterSnapshot snapshot, CombatHistoryContext history)
    {
        var summary = history.Primary;
        var primary = PrimaryFight(snapshot);

        // Without this the rows below are two bare numbers with no clue which is the live figure and
        // which is the historical median.
        lines.Add(ClogLine.Of(new ClogSpan($"{string.Empty,-8}{"now",7}{"med",8}", ClogTone.Dim)));

        AppendPair(lines, "dealt", primary is null ? null : primary.ApproxDamageDone, summary.MedianDamageDone, Num);
        AppendPair(lines, "taken", primary is null ? null : primary.ApproxDamageTaken, summary.MedianDamageTaken, Num);
        AppendPair(lines, "you hit", primary is null ? null : HitRate(primary.YouHits, primary.YouMisses),
            summary.MedianYouHitRate, Percent);
        AppendPair(lines, "they hit", primary is null ? null : HitRate(primary.TheyHits, primary.TheyMisses),
            summary.MedianTheyHitRate, Percent);
        AppendPair(lines, "time", primary?.Duration.TotalSeconds, summary.MedianDurationSeconds, Duration);

        lines.Add(ClogLine.Of(new ClogSpan(OutcomeTally(summary), ClogTone.Dim)));

        // The pool estimate is the only route to an NPC's stamina — MUD2 never reports it — so the
        // kill count it rests on is stated rather than hidden behind a bare number.
        if (summary.EstimatedStaminaPool is double pool)
        {
            lines.Add(ClogLine.Of(
                new ClogSpan("pool", ClogTone.Dim),
                new ClogSpan($" ~{Num(pool)}", ClogTone.Hostile),
                new ClogSpan($" ({summary.Kills}k)", ClogTone.Dim)));
        }
        else
        {
            lines.Add(ClogLine.Of(new ClogSpan("pool  -- (never killed one)", ClogTone.Dim)));
        }
    }

    private static void AppendPair(
        List<ClogLine> lines, string label, double? now, double? median, Func<double?, string> format)
    {
        // Skip entirely when neither side has anything to say, rather than printing "-- / --".
        if (now is null && median is null)
            return;

        lines.Add(ClogLine.Of(
            new ClogSpan($"{label,-8}", ClogTone.Dim),
            new ClogSpan($"{format(now),7}", ClogTone.Value),
            new ClogSpan($" {format(median),7}", ClogTone.Dim)));
    }

    /// <summary>
    /// Per-weapon table for the NPC's GROUP (never the instance — susceptibility is a property of
    /// the creature type, and the group is where samples accumulate).
    ///
    /// <para>Sorted best-per-hit first, and the weapon currently in hand is always present even with
    /// no history at all, marked and tinted against the best on record: green when it is beating the
    /// best, amber when it is not. That live over/under signal is the point — it is what makes
    /// experimenting with weapons mid-fight legible.</para>
    /// </summary>
    private static void AppendWeaponTable(
        List<ClogLine> lines, CombatEncounterSnapshot snapshot, CombatHistoryContext history)
    {
        var current = snapshot.CurrentWeapon;
        var rows = new List<(string Weapon, double? Hist, int Count, bool IsCurrent)>();

        double? best = null;
        foreach (var entry in history.ByWeapon)
        {
            if (entry.Summary.MedianDamagePerHit is double perHit && (best is null || perHit > best))
                best = perHit;
            rows.Add((entry.Weapon, entry.Summary.MedianDamagePerHit, entry.Summary.FightCount,
                IsCurrentWeapon(entry.Weapon, current)));
        }

        var haveCurrentRow = rows.Any(row => row.IsCurrent);
        if (!haveCurrentRow && !string.IsNullOrWhiteSpace(current))
            rows.Add((current!, null, 0, true));   // first outing with this weapon against this group

        if (rows.Count == 0)
            return;

        // Best first; unmeasured weapons sink to the bottom rather than sorting as zero.
        rows.Sort((a, b) =>
        {
            if (a.Hist is null && b.Hist is null)
                return string.Compare(a.Weapon, b.Weapon, StringComparison.OrdinalIgnoreCase);
            if (a.Hist is null)
                return 1;
            if (b.Hist is null)
                return -1;
            var byHist = b.Hist.Value.CompareTo(a.Hist.Value);
            return byHist != 0 ? byHist : string.Compare(a.Weapon, b.Weapon, StringComparison.OrdinalIgnoreCase);
        });

        var liveNow = LivePerHit(snapshot, history);
        var width = Math.Min(rows.Max(row => row.Weapon.Length), 15);

        // Header widths are derived from the same column widths the rows use below, so a long weapon
        // name ("croquet mallet") cannot slide the values out from under their labels.
        lines.Add(ClogLine.Of(
            new ClogSpan(" " + "weapon".PadRight(width), ClogTone.Heading),
            new ClogSpan($" {"now",5} {"hist",6}", ClogTone.Dim)));
        foreach (var row in rows)
        {
            var marker = row.IsCurrent ? "»" : " ";
            var nowText = row.IsCurrent && liveNow is not null ? Num(liveNow) : NoValue;
            var tone = row.IsCurrent ? CurrentWeaponTone(liveNow, best) : ClogTone.Value;

            lines.Add(ClogLine.Of(
                new ClogSpan(marker, ClogTone.Dim),
                new ClogSpan(Truncate(row.Weapon, width).PadRight(width), tone),
                new ClogSpan($" {nowText,5}", tone),
                new ClogSpan($" {(row.Hist is null ? NoValue : Num(row.Hist)),6}", ClogTone.Dim),
                new ClogSpan(row.Count > 0 ? $" [{row.Count}x]" : " [new]", ClogTone.Dim)));
        }
    }

    /// <summary>Green when this weapon is currently out-hitting the best on record, amber when it is
    /// falling short, plain when there is nothing yet to compare against.</summary>
    private static ClogTone CurrentWeaponTone(double? liveNow, double? best)
    {
        if (liveNow is null || best is null)
            return ClogTone.Value;
        return liveNow.Value >= best.Value ? ClogTone.Good : ClogTone.Warn;
    }

    /// <summary>Damage per landed blow so far in the current fight, which is what the historical
    /// per-hit figures are comparable against. Taken from the primary fight rather than the whole
    /// encounter so a second target of a different species cannot pollute it.</summary>
    private static double? LivePerHit(CombatEncounterSnapshot snapshot, CombatHistoryContext history)
    {
        var primary = PrimaryFight(snapshot);
        if (primary is null || primary.YouHits == 0 || primary.ApproxDamageDone <= 0)
            return null;
        if (history.GroupName.Length > 0
            && !string.Equals(primary.NpcGroup, history.GroupName, StringComparison.OrdinalIgnoreCase))
            return null;
        return primary.ApproxDamageDone / primary.YouHits;
    }

    private static bool IsCurrentWeapon(string weapon, string? current)
        => !string.IsNullOrWhiteSpace(current) && string.Equals(weapon, current, StringComparison.OrdinalIgnoreCase);

    /// <summary>The fight the comparison describes: the first still-unresolved one, falling back to
    /// the first of the encounter so the block survives the post-kill grace window.</summary>
    internal static FightSnapshot? PrimaryFight(CombatEncounterSnapshot snapshot)
    {
        FightSnapshot? first = null;
        foreach (var fight in snapshot.Fights)
        {
            first ??= fight;
            if (!fight.IsResolved)
                return fight;
        }

        return first;
    }

    private static string OutcomeTally(FightHistorySummary summary)
    {
        var parts = new List<string>(5) { $"killed {summary.Kills}/{summary.FightCount}" };
        if (summary.Deaths > 0)
            parts.Add($"died {summary.Deaths}");
        if (summary.NpcFled > 0)
            parts.Add($"it fled {summary.NpcFled}");
        if (summary.YouFled > 0)
            parts.Add($"you fled {summary.YouFled}");
        if (summary.Withdrawn > 0)
            parts.Add($"withdrew {summary.Withdrawn}");
        if (summary.Unresolved > 0)
            parts.Add($"open {summary.Unresolved}");
        return string.Join(", ", parts);
    }

    private static string Fights(int count) => count == 1 ? "1 fight" : $"{count} fights";

    private static double? HitRate(int hits, int misses)
    {
        var attempts = hits + misses;
        return attempts == 0 ? null : hits / (double)attempts;
    }

    private static string Signed(int value)
        => value > 0
            ? "+" + value.ToString(CultureInfo.InvariantCulture)
            : value.ToString(CultureInfo.InvariantCulture);

    private static string Num(double? value)
        => value is null ? NoValue : value.Value.ToString("0.0", CultureInfo.InvariantCulture);

    private static string Percent(double? value)
        => value is null
            ? NoValue
            : Math.Round(value.Value * 100, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "%";

    private static string Duration(double? seconds)
    {
        if (seconds is null)
            return NoValue;
        var total = (int)Math.Round(seconds.Value, MidpointRounding.AwayFromZero);
        return $"{total / 60}:{total % 60:00}";
    }

    private static string Truncate(string value, int width)
        => value.Length <= width ? value : value[..width];
}
