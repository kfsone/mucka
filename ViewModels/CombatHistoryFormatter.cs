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
        CombatHistoryContext history,
        SessionCombatTotals? session = null)
    {
        session ??= SessionCombatTotals.Empty;
        var lines = new List<ClogLine>(28);
        if (!snapshot.HasEncounter)
        {
            AppendSessionTotals(lines, session);
            return lines;
        }

        // Result banner first: once a fight is over the outcome is the thing you want to see, and it
        // now stays on screen until explicitly cleared rather than vanishing after 8 seconds.
        AppendResultBanner(lines, snapshot);
        AppendHeadline(lines, snapshot);
        AppendExchange(lines, snapshot);
        AppendDeficits(lines, deficits);
        AppendOutlook(lines, snapshot, deficits, history);

        if (history.HasAnything)
        {
            lines.Add(ClogLine.Blank);
            AppendHistoryHeading(lines, history);
            AppendFleeRisk(lines, history);
            AppendHistoryComparison(lines, snapshot, history);
            AppendWeaponTable(lines, snapshot, history);
        }

        if (!snapshot.InCombat && session.HasAnything)
        {
            lines.Add(ClogLine.Blank);
            AppendSessionTotals(lines, session);
        }

        return lines;
    }

    /// <summary>"killed zombie0  0:27  65.5 dealt" once the encounter has closed. Nothing while the
    /// fight is still live — the headline already covers that, and a verdict mid-fight would be a
    /// lie.</summary>
    private static void AppendResultBanner(List<ClogLine> lines, CombatEncounterSnapshot snapshot)
    {
        if (snapshot.InCombat)
            return;

        var decisive = ResultOf(snapshot);
        if (decisive is null)
            return;

        var (glyph, verb, tone) = decisive.Outcome switch
        {
            FightOutcome.Killed => ("✔", "killed", ClogTone.Good),
            FightOutcome.KilledByNpc => ("✘", "killed by", ClogTone.Hostile),
            FightOutcome.NpcFled => ("→", "fled:", ClogTone.Warn),
            FightOutcome.YouFled => ("←", "you fled", ClogTone.Warn),
            FightOutcome.Withdrawn => ("―", "withdrew", ClogTone.Dim),
            _ => ("·", "open", ClogTone.Dim),
        };

        lines.Add(ClogLine.Of(
            new ClogSpan($"{glyph} {verb} ", tone),
            new ClogSpan(decisive.NpcName, ClogTone.Hostile),
            new ClogSpan($"  {Duration(decisive.Duration.TotalSeconds)}", ClogTone.Dim),
            // That fight's own damage, not the encounter total: the figure sits next to a specific
            // NPC's name, so an encounter-wide number would read as having been dealt to it.
            new ClogSpan($"  {Num(decisive.ApproxDamageDone)} dealt", ClogTone.Dim)));
        lines.Add(ClogLine.Blank);
    }

    /// <summary>The fight that decided the encounter: a player death outranks everything, then a
    /// kill, else the last resolved fight.</summary>
    private static FightSnapshot? ResultOf(CombatEncounterSnapshot snapshot)
    {
        FightSnapshot? fallback = null;
        foreach (var fight in snapshot.Fights)
        {
            if (fight.Outcome == FightOutcome.KilledByNpc)
                return fight;
            if (fight.IsResolved)
                fallback = fight.Outcome == FightOutcome.Killed ? fight : fallback ?? fight;
        }

        return fallback;
    }

    /// <summary>Between-fights view: what this session has amounted to, in the same terms the live
    /// rows use so the panel reads as one thing rather than two.</summary>
    private static void AppendSessionTotals(List<ClogLine> lines, SessionCombatTotals session)
    {
        if (!session.HasAnything)
            return;

        lines.Add(ClogLine.Of(new ClogSpan("this session", ClogTone.Heading)));
        lines.Add(ClogLine.Of(
            new ClogSpan($"{"fights",-8}", ClogTone.Dim),
            new ClogSpan($"{session.Fights,7}", ClogTone.Value),
            new ClogSpan($"  in {session.Encounters} enc", ClogTone.Dim)));
        lines.Add(ClogLine.Of(
            new ClogSpan($"{"killed",-8}", ClogTone.Dim),
            new ClogSpan($"{session.Kills,7}", ClogTone.Good),
            new ClogSpan(session.Deaths > 0 ? $"  died {session.Deaths}" : string.Empty, ClogTone.Hostile),
            new ClogSpan(session.NpcFled > 0 ? $"  fled {session.NpcFled}" : string.Empty, ClogTone.Warn)));
        lines.Add(ClogLine.Of(
            new ClogSpan($"{"dealt",-8}", ClogTone.Dim),
            new ClogSpan($"{Num(session.DamageDealt),7}", ClogTone.Friendly)));
        lines.Add(ClogLine.Of(
            new ClogSpan($"{"taken",-8}", ClogTone.Dim),
            new ClogSpan($"{Num(session.DamageTaken),7}", ClogTone.Hostile)));
        lines.Add(ClogLine.Of(
            new ClogSpan($"{"fighting",-8}", ClogTone.Dim),
            new ClogSpan($"{Duration(session.TimeInCombat.TotalSeconds),7}", ClogTone.Value)));
    }

    /// <summary>Whether this opponent is likely to run. Only rendered at or above a coin flip, since
    /// below that it is not decision-changing and would just be another always-present row.</summary>
    private static void AppendFleeRisk(List<ClogLine> lines, CombatHistoryContext history)
    {
        var summary = history.Primary;
        if (summary.FleeRate is not double rate || rate < 0.5)
            return;

        lines.Add(ClogLine.Of(
            new ClogSpan("flees", ClogTone.Warn),
            new ClogSpan($"   {Percent(rate)} of the time", ClogTone.Warn),
            new ClogSpan($" ({summary.NpcFled}/{summary.FightCount})", ClogTone.Dim)));
    }

    /// <summary>The "am I going to die first" line. Silent until there is enough to say — see
    /// <see cref="CombatOutlook"/> for why an early guess is worse than none.</summary>
    private static void AppendOutlook(
        List<ClogLine> lines,
        CombatEncounterSnapshot snapshot,
        CombatStatDeficits deficits,
        CombatHistoryContext history)
    {
        if (!snapshot.InCombat)
            return;

        var primary = PrimaryFight(snapshot);
        if (primary is null)
            return;

        var outlook = CombatOutlook.Project(
            primary.Duration.TotalSeconds,
            primary.ApproxDamageDone,
            primary.ApproxDamageTaken,
            primary.YouHits,
            primary.TheyHits,
            deficits.StaminaCurrent,
            history.Primary.EstimatedStaminaPool);

        if (outlook.Verdict == OutlookVerdict.Unknown)
            return;

        var (text, tone) = outlook.Verdict switch
        {
            OutlookVerdict.Winning => ("winning", ClogTone.Good),
            OutlookVerdict.Losing => ("LOSING", ClogTone.Hostile),
            OutlookVerdict.Even => ("too close", ClogTone.Warn),
            _ => ("unhurt so far", ClogTone.Good),
        };

        var spans = new List<ClogSpan>(4)
        {
            new("outlook ", ClogTone.Dim),
            new(text, tone),
        };

        // Both projected times, because the verdict alone hides how wide the margin is.
        if (outlook.SecondsToKill is double kill)
        {
            spans.Add(new ClogSpan($"  kill {Duration(kill)}", ClogTone.Dim));
            spans.Add(new ClogSpan(
                outlook.SecondsToDie is double die ? $" / die {Duration(die)}" : " / die --",
                ClogTone.Dim));
        }

        lines.Add(new ClogLine(spans));
    }

    /// <summary>"{weapon} vs {live targets}, {dead targets}    {elapsed}". Dead targets sort to the
    /// right of the list and render struck through, so a glance says both who is left and what has
    /// already gone down without spending a line on either.</summary>
    private static void AppendHeadline(List<ClogLine> lines, CombatEncounterSnapshot snapshot)
    {
        var spans = new List<ClogSpan>(8)
        {
            new(string.IsNullOrWhiteSpace(snapshot.CurrentWeapon) ? "unarmed" : DisplayName(snapshot.CurrentWeapon),
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

        // "usual" rather than "med": the figure IS a median, but in a game with magic "med" reads as
        // medium/meditate, and the user flagged it as genuinely ambiguous. "usual" says what the
        // column is FOR without the collision. (Still a median, not a mean — one wandered-off fight
        // is a large fraction of a single-digit sample and would wreck an average.)
        lines.Add(ClogLine.Of(new ClogSpan($"{string.Empty,-8}{"now",7}{"usual",8}", ClogTone.Dim)));

        AppendPair(lines, "dealt", primary is null ? null : primary.ApproxDamageDone, summary.MedianDamageDone, Num);
        AppendPair(lines, "taken", primary is null ? null : primary.ApproxDamageTaken, summary.MedianDamageTaken, Num);
        AppendPair(lines, "you hit", primary is null ? null : HitRate(primary.YouHits, primary.YouMisses),
            summary.MedianYouHitRate, Percent);
        AppendPair(lines, "they hit", primary is null ? null : HitRate(primary.TheyHits, primary.TheyMisses),
            summary.MedianTheyHitRate, Percent);
        AppendPair(lines, "time", primary?.Duration.TotalSeconds, summary.MedianDurationSeconds, Duration);

        lines.Add(ClogLine.Of(new ClogSpan(OutcomeTally(summary), ClogTone.Dim)));

        // Labelled "to kill" rather than "pool": the user reported not understanding what "pool"
        // meant, and the plain reading of the number is "how much damage it usually takes to put one
        // of these down". Still the only route to an NPC's stamina, since MUD2 never reports it, so
        // the kill count behind the estimate is stated rather than hidden.
        if (summary.EstimatedStaminaPool is double pool)
        {
            lines.Add(ClogLine.Of(
                new ClogSpan($"{"to kill",-8}", ClogTone.Dim),
                new ClogSpan($"{"~" + Num(pool),7}", ClogTone.Hostile),
                new ClogSpan($"  dmg, over {summary.Kills} {(summary.Kills == 1 ? "kill" : "kills")}", ClogTone.Dim)));
        }
        else
        {
            lines.Add(ClogLine.Of(
                new ClogSpan($"{"to kill",-8}", ClogTone.Dim),
                new ClogSpan($"{NoValue,7}", ClogTone.Dim),
                new ClogSpan("  never killed one", ClogTone.Dim)));
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
            // Matching stays on the FULL name (DisplayName is display-only), so "a rusty pick2" in
            // history still matches the same weapon in hand.
            rows.Add((DisplayName(entry.Weapon), entry.Summary.MedianDamagePerHit, entry.Summary.FightCount,
                IsCurrentWeapon(entry.Weapon, current)));
        }

        var haveCurrentRow = rows.Any(row => row.IsCurrent);
        if (!haveCurrentRow && !string.IsNullOrWhiteSpace(current))
            rows.Add((DisplayName(current), null, 0, true));   // first outing with this weapon here

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

    /// <summary>
    /// Shortens a long item name for DISPLAY only, never for recording.
    ///
    /// <para>MUD2 item names carry descriptive prefixes ("a rusty pick2", "the ornate falchion3") but
    /// the trailing token with its instance number is the part that identifies the thing and the part
    /// the player types. So for anything over <see cref="DisplayNameThreshold"/> characters whose last
    /// word ends in a digit, that last word becomes the label — "a rusty pick2" shows as "pick2",
    /// which also stops one long name from widening the whole weapon column.</para>
    ///
    /// <para>Names whose last word does NOT end in a digit are left alone: "croquet mallet" has no
    /// instance number, so "mallet" would be a lossy guess rather than the canonical short form.
    /// FightRecord always stores the full name, so history and the offline pipeline are unaffected.</para>
    /// </summary>
    internal static string DisplayName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var trimmed = name.Trim();
        if (trimmed.Length <= DisplayNameThreshold)
            return trimmed;

        var lastSpace = trimmed.LastIndexOf(' ');
        if (lastSpace < 0 || lastSpace == trimmed.Length - 1)
            return trimmed;

        var lastWord = trimmed[(lastSpace + 1)..];
        return char.IsAsciiDigit(lastWord[^1]) ? lastWord : trimmed;
    }

    private const int DisplayNameThreshold = 10;
}
