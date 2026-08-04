namespace MudSharp.Combat;

/// <summary>
/// Aggregate of prior fights matching some filter. Every field is null when there is no data to
/// support it — callers must render "--" rather than 0, because "no samples" and "measured zero"
/// mean completely different things when the whole point is judging confidence.
/// </summary>
public sealed record FightHistorySummary
{
    /// <summary>Rows contributing to the medians below: resolved fights WITH parsed per-swing
    /// detail. Deliberately distinct from <see cref="FightCount"/> so the display can show the
    /// honest basis for a median rather than implying every recorded fight informed it.</summary>
    public int SampleSize { get; init; }

    /// <summary>All matching rows, including narrative-mode ones with no swing detail. Outcome
    /// counts use this, because an outcome is real evidence even when the swings were not
    /// parseable.</summary>
    public int FightCount { get; init; }

    public int Kills { get; init; }
    public int Deaths { get; init; }
    public int NpcFled { get; init; }
    public int YouFled { get; init; }
    public int Withdrawn { get; init; }
    public int Unresolved { get; init; }

    public double? MedianDamageDone { get; init; }
    public double? MedianDamageTaken { get; init; }
    public double? MedianDurationSeconds { get; init; }
    public double? MedianYouHitRate { get; init; }
    public double? MedianTheyHitRate { get; init; }

    /// <summary>Median damage per landed blow — the axis a hidden per-weapon modifier would show
    /// up on, and more comparable across fights than a total (which just tracks fight length).</summary>
    public double? MedianDamagePerHit { get; init; }

    /// <summary>Median total damage dealt across fights that ENDED IN A KILL — an empirical
    /// estimate of this NPC group's stamina pool, and the only route to one, since MUD2 never
    /// reports NPC stamina. Null until at least one kill is on record. Non-kills are deliberately
    /// excluded: a survivor only proves its pool exceeds what we dealt (a censored observation),
    /// so folding those in biases the estimate low. See STATS_DESIGN.md.</summary>
    public double? EstimatedStaminaPool { get; init; }

    /// <summary>Kills as a fraction of all matching fights, or null with no fights at all.</summary>
    public double? KillRate => FightCount == 0 ? null : Kills / (double)FightCount;

    /// <summary>How often this opponent has run away, as a fraction of all recorded fights. Some
    /// MUD2 NPCs are strongly flight-prone (per the user, water snakes almost always flee), which
    /// matters BEFORE committing: a fleeing target has to be chased through rooms or the kill is
    /// lost. Null with no fights on record.</summary>
    public double? FleeRate => FightCount == 0 ? null : NpcFled / (double)FightCount;

    public static readonly FightHistorySummary Empty = new();
}

/// <summary>A weapon's record against one NPC group, for the "which weapon works on this thing"
/// comparison.</summary>
public sealed record WeaponHistorySummary(string Weapon, FightHistorySummary Summary);

/// <summary>
/// Queries a loaded set of <see cref="FightRecord"/> rows. Pure and synchronous: the caller owns
/// loading and any locking (see Core/FightHistoryStore).
///
/// <para>Medians, not means, throughout. One fight where the player wandered off mid-encounter
/// (long duration, few swings) destroys a mean and barely moves a median — and at the sample sizes
/// realistically available, a single such outlier is a large fraction of the data.</para>
///
/// <para>Deliberately NOT provided: significance tests, confidence intervals, or any single
/// "weapon A beats weapon B" verdict. At n in the single digits those would be false precision.
/// The display shows n and the spread and lets the reader judge. Revisit if a group ever reaches
/// n in the hundreds.</para>
/// </summary>
public static class FightHistory
{
    /// <summary>Aggregates the fights matching <paramref name="npcGroup"/>, optionally narrowed to
    /// a single weapon. Pass <paramref name="weapon"/> null for "any weapon".</summary>
    /// <summary>
    /// Drops rows belonging to the encounter currently on screen.
    ///
    /// <para>Essential, not cosmetic: <c>FightHistoryRecorder</c> appends a finished encounter's rows
    /// to the store BEFORE the view model rebuilds its readout, so without this filter the panel
    /// compares the fight the player just had against itself — which makes "now" and "usual"
    /// identical by construction at one sample, and biases the baseline toward the current fight at
    /// any sample size.</para>
    /// </summary>
    public static IEnumerable<FightRecord> ExcludingEncounterFrom(
        IEnumerable<FightRecord> records,
        DateTime? encounterStartUtc)
    {
        if (encounterStartUtc is not DateTime start)
            return records;

        var cutoffMs = new DateTimeOffset(start, TimeSpan.Zero).ToUnixTimeMilliseconds();
        return records.Where(record => record.StartedAtMs < cutoffMs);
    }

    public static FightHistorySummary Summarize(
        IEnumerable<FightRecord> records,
        string npcGroup,
        string? weapon = null)
    {
        if (string.IsNullOrWhiteSpace(npcGroup))
            return FightHistorySummary.Empty;

        var matching = new List<FightRecord>();
        foreach (var record in records)
        {
            if (!string.Equals(record.NpcGroup, npcGroup, StringComparison.OrdinalIgnoreCase))
                continue;
            if (weapon is not null && !string.Equals(record.WeaponUsed ?? string.Empty, weapon, StringComparison.OrdinalIgnoreCase))
                continue;
            matching.Add(record);
        }

        return Summarize(matching);
    }

    /// <summary>
    /// Aggregates prior fights against one specific NPC INSTANCE (e.g. "rat0", not "rats").
    ///
    /// <para>MUD2 instances of the same creature are not equivalent opponents: rat0 is far more
    /// dangerous than the other rats, and dwarf48 harder than most dwarves. Difficulty figures —
    /// damage, duration, outcomes, stamina pool — therefore belong to the instance once it has
    /// samples of its own. Weapon susceptibility does NOT: dwarf48 is still a dwarf and still takes
    /// extra from a pick, so <see cref="SummarizeByWeapon"/> stays keyed on the group, which is also
    /// where sample counts actually accumulate.</para>
    /// </summary>
    public static FightHistorySummary SummarizeInstance(IEnumerable<FightRecord> records, string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName))
            return FightHistorySummary.Empty;

        var matching = new List<FightRecord>();
        foreach (var record in records)
        {
            if (string.Equals(record.NpcName, npcName, StringComparison.OrdinalIgnoreCase))
                matching.Add(record);
        }

        return Summarize(matching);
    }

    /// <summary>Per-weapon breakdown against one NPC group, ordered by sample size descending then
    /// weapon name, so the best-evidenced weapon reads first.</summary>
    public static IReadOnlyList<WeaponHistorySummary> SummarizeByWeapon(
        IEnumerable<FightRecord> records,
        string npcGroup)
    {
        if (string.IsNullOrWhiteSpace(npcGroup))
            return [];

        var byWeapon = new Dictionary<string, List<FightRecord>>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            if (!string.Equals(record.NpcGroup, npcGroup, StringComparison.OrdinalIgnoreCase))
                continue;
            // Unarmed and unknown-weapon fights are real data (MUD2 lets you fight bare-handed),
            // so they get their own bucket rather than being dropped.
            var key = string.IsNullOrWhiteSpace(record.WeaponUsed) ? "(none)" : record.WeaponUsed;
            if (!byWeapon.TryGetValue(key, out var list))
                byWeapon[key] = list = [];
            list.Add(record);
        }

        var result = new List<WeaponHistorySummary>(byWeapon.Count);
        foreach (var (key, list) in byWeapon)
            result.Add(new WeaponHistorySummary(key, Summarize(list)));

        result.Sort((a, b) =>
        {
            var bySample = b.Summary.FightCount.CompareTo(a.Summary.FightCount);
            return bySample != 0 ? bySample : string.Compare(a.Weapon, b.Weapon, StringComparison.OrdinalIgnoreCase);
        });
        return result;
    }

    private static FightHistorySummary Summarize(List<FightRecord> matching)
    {
        if (matching.Count == 0)
            return FightHistorySummary.Empty;

        var kills = 0;
        var deaths = 0;
        var npcFled = 0;
        var youFled = 0;
        var withdrawn = 0;
        var unresolved = 0;

        var damageDone = new List<double>();
        var damageTaken = new List<double>();
        var durations = new List<double>();
        var youRates = new List<double>();
        var theyRates = new List<double>();
        var damagePerHit = new List<double>();
        var killDamage = new List<double>();

        foreach (var record in matching)
        {
            switch (record.Outcome)
            {
                case nameof(FightOutcome.Killed): kills++; break;
                case nameof(FightOutcome.KilledByNpc): deaths++; break;
                case nameof(FightOutcome.NpcFled): npcFled++; break;
                case nameof(FightOutcome.YouFled): youFled++; break;
                case nameof(FightOutcome.Withdrawn): withdrawn++; break;
                default: unresolved++; break;
            }

            // Pool estimate requires swing detail as well as a kill: the cumulative damage figure
            // comes from the reported per-hit ranges, and narrative mode reports none at all, so a
            // narrative kill would contribute a spurious near-zero pool estimate.
            if (record.IsKill && record.HasSwingDetail && record.ApproxDamageDone > 0)
                killDamage.Add(record.ApproxDamageDone);

            if (!record.HasSwingDetail)
                continue;

            damageDone.Add(record.ApproxDamageDone);
            damageTaken.Add(record.ApproxDamageTaken);
            if (record.DurationMs > 0)
                durations.Add(record.DurationMs / 1000.0);

            var yourAttempts = record.YouHits + record.YouMisses;
            if (yourAttempts > 0)
                youRates.Add(record.YouHits / (double)yourAttempts);

            var theirAttempts = record.TheyHits + record.TheyMisses;
            if (theirAttempts > 0)
                theyRates.Add(record.TheyHits / (double)theirAttempts);

            if (record.YouHits > 0 && record.ApproxDamageDone > 0)
                damagePerHit.Add(record.ApproxDamageDone / record.YouHits);
        }

        return new FightHistorySummary
        {
            SampleSize = damageDone.Count,
            FightCount = matching.Count,
            Kills = kills,
            Deaths = deaths,
            NpcFled = npcFled,
            YouFled = youFled,
            Withdrawn = withdrawn,
            Unresolved = unresolved,
            MedianDamageDone = Median(damageDone),
            MedianDamageTaken = Median(damageTaken),
            MedianDurationSeconds = Median(durations),
            MedianYouHitRate = Median(youRates),
            MedianTheyHitRate = Median(theyRates),
            MedianDamagePerHit = Median(damagePerHit),
            EstimatedStaminaPool = Median(killDamage),
        };
    }

    /// <summary>Median of the values, or null when empty. Sorts a copy — callers pass short-lived
    /// per-query lists, so this never mutates anything the store holds.</summary>
    internal static double? Median(List<double> values)
    {
        if (values.Count == 0)
            return null;

        values.Sort();
        var middle = values.Count / 2;
        return values.Count % 2 == 1
            ? values[middle]
            : (values[middle - 1] + values[middle]) / 2.0;
    }
}
