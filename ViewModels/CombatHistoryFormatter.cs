using System.Globalization;
using System.Text;
using MudSharp.Combat;

namespace Mucka.ViewModels;

/// <summary>
/// Builds the fixed-width text blocks the clog window renders for the per-fight breakdown and the
/// "vs history" comparison.
///
/// <para>Pure string building, no MAUI types, so it is linked into mudsharp.Tests — column
/// alignment and "no data" handling are exactly the sort of thing that silently rots otherwise.</para>
///
/// <para>Rendered as pre-formatted multi-line text into a single monospace Label rather than as a
/// bound list, deliberately: rebuilding a list re-templates native views on the UI thread every
/// refresh, and this refreshes on every combat event plus a 1 Hz tick (Invariant #1). One string
/// assignment costs a text remeasure and nothing more.</para>
///
/// <para>Presentation rules, per tools/combat/STATS_DESIGN.md: sample size is always stated,
/// medians rather than means, and "no samples" always renders as "--" and never as 0 — at these
/// sample sizes conflating the two would be actively misleading.</para>
/// </summary>
internal static class CombatHistoryFormatter
{
    private const string NoValue = "--";

    /// <summary>One line per NPC engaged this encounter, in first-engaged order, resolved ones
    /// included so a multi-NPC encounter shows how each ended rather than dropping the finished.</summary>
    public static string FormatFightRows(IReadOnlyList<FightSnapshot> fights)
    {
        if (fights.Count <= 1)
            return string.Empty;   // the single-NPC case is already fully described by the totals above

        var nameWidth = 0;
        foreach (var fight in fights)
            nameWidth = Math.Max(nameWidth, fight.NpcName.Length);
        nameWidth = Math.Min(nameWidth, 16);

        var builder = new StringBuilder();
        for (var i = 0; i < fights.Count; i++)
        {
            var fight = fights[i];
            if (i > 0)
                builder.Append('\n');

            var name = Truncate(fight.NpcName, nameWidth).PadRight(nameWidth);
            var swings = $"{fight.YouHits}h/{fight.YouMisses}m";
            builder.Append(CultureInfo.InvariantCulture, $"{name}  {swings,-7} {Num(fight.ApproxDamageDone),6} dealt {Num(fight.ApproxDamageTaken),6} taken  {DescribeOutcome(fight)}");
        }

        return builder.ToString();
    }

    /// <summary>Header for the history block, e.g. "rats: 7 prior fights, 5 with detail". Always
    /// leads with the count — it is the reader's only cue for how much weight the medians deserve.</summary>
    public static string FormatHistoryHeader(string npcGroup, FightHistorySummary summary)
    {
        if (string.IsNullOrWhiteSpace(npcGroup))
            return string.Empty;

        if (summary.FightCount == 0)
            return $"{npcGroup}: no prior fights on record";

        var fights = summary.FightCount == 1 ? "1 prior fight" : $"{summary.FightCount} prior fights";
        return summary.SampleSize == summary.FightCount
            ? $"{npcGroup}: {fights}"
            : $"{npcGroup}: {fights}, {summary.SampleSize} with detail";
    }

    /// <summary>The comparison body: this fight's live figures against the historical medians, the
    /// outcome tally, the derived stamina-pool estimate, and a per-weapon breakdown.</summary>
    public static string FormatHistoryRows(
        FightSnapshot? current,
        FightHistorySummary summary,
        IReadOnlyList<WeaponHistorySummary> byWeapon)
    {
        if (summary.FightCount == 0)
            return string.Empty;

        var builder = new StringBuilder();

        if (current is not null)
        {
            AppendComparison(builder, "dmg dealt", Num(current.ApproxDamageDone), Num(summary.MedianDamageDone));
            AppendComparison(builder, "dmg taken", Num(current.ApproxDamageTaken), Num(summary.MedianDamageTaken));
            AppendComparison(builder, "your hits", Percent(HitRate(current.YouHits, current.YouMisses)), Percent(summary.MedianYouHitRate));
            AppendComparison(builder, "their hits", Percent(HitRate(current.TheyHits, current.TheyMisses)), Percent(summary.MedianTheyHitRate));
            AppendComparison(builder, "duration", Duration(current.Duration.TotalSeconds), Duration(summary.MedianDurationSeconds));
        }
        else
        {
            AppendLine(builder, $"{"dmg dealt",-11}median {Num(summary.MedianDamageDone)}");
            AppendLine(builder, $"{"dmg taken",-11}median {Num(summary.MedianDamageTaken)}");
            AppendLine(builder, $"{"your hits",-11}median {Percent(summary.MedianYouHitRate)}");
            AppendLine(builder, $"{"duration",-11}median {Duration(summary.MedianDurationSeconds)}");
        }

        AppendLine(builder, FormatOutcomeTally(summary));

        // The pool estimate is the whole reason history is worth keeping: MUD2 never reports NPC
        // stamina, so prior kills are the only route to one. State the kill count it rests on.
        if (summary.EstimatedStaminaPool is double pool)
        {
            var kills = summary.Kills == 1 ? "1 kill" : $"{summary.Kills} kills";
            AppendLine(builder, $"pool est   ~{Num(pool)} (from {kills})");
        }
        else
        {
            AppendLine(builder, "pool est   -- (no kills on record)");
        }

        if (byWeapon.Count > 0)
        {
            AppendLine(builder, "-- by weapon --");
            var weaponWidth = 0;
            foreach (var entry in byWeapon)
                weaponWidth = Math.Max(weaponWidth, entry.Weapon.Length);
            weaponWidth = Math.Min(weaponWidth, 14);

            foreach (var entry in byWeapon)
            {
                var name = Truncate(entry.Weapon, weaponWidth).PadRight(weaponWidth);
                var perHit = entry.Summary.MedianDamagePerHit is double value
                    ? $"{Num(value)}/hit"
                    : $"{NoValue}/hit";
                AppendLine(builder,
                    $"{name} n={entry.Summary.FightCount,-3} {perHit,10} {Percent(entry.Summary.MedianYouHitRate),5} {entry.Summary.Kills}k");
            }
        }

        return builder.ToString();
    }

    private static void AppendComparison(StringBuilder builder, string label, string now, string median)
        => AppendLine(builder, $"{label,-11}{now,7} now vs {median,7} med");

    private static void AppendLine(StringBuilder builder, string line)
    {
        if (builder.Length > 0)
            builder.Append('\n');
        builder.Append(line);
    }

    private static string FormatOutcomeTally(FightHistorySummary summary)
    {
        var parts = new List<string>(5) { $"killed {summary.Kills}/{summary.FightCount}" };
        if (summary.Deaths > 0)
            parts.Add($"you died {summary.Deaths}");
        if (summary.NpcFled > 0)
            parts.Add($"it fled {summary.NpcFled}");
        if (summary.YouFled > 0)
            parts.Add($"you fled {summary.YouFled}");
        if (summary.Withdrawn > 0)
            parts.Add($"withdrew {summary.Withdrawn}");
        if (summary.Unresolved > 0)
            parts.Add($"unresolved {summary.Unresolved}");
        return string.Join(", ", parts);
    }

    private static string DescribeOutcome(FightSnapshot fight) => fight.Outcome switch
    {
        FightOutcome.Killed => "kill",
        FightOutcome.KilledByNpc => "died",
        FightOutcome.NpcFled => "it fled",
        FightOutcome.YouFled => "you fled",
        FightOutcome.Withdrawn => "withdrew",
        _ => "live",
    };

    private static double? HitRate(int hits, int misses)
    {
        var attempts = hits + misses;
        return attempts == 0 ? null : hits / (double)attempts;
    }

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
