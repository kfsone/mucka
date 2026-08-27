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

    /// <summary>Fights the creature ESCAPED from ("has fled by going &lt;dir&gt;").</summary>
    public int CFled { get; init; }

    /// <summary>Fights the creature tried to escape from and FAILED ("has fled by trying to go
    /// &lt;dir&gt;") - it stayed in the room, but the fight ended anyway and had to be re-opened.
    /// Counted separately from <see cref="CFled"/> on purpose: folding the two together is what made
    /// the corpus record water snakes at 0 flees from 6 fights, when in truth they attempt it
    /// constantly and rarely succeed.</summary>
    public int CFledFail { get; init; }

    /// <summary>Fights the player fled from successfully.</summary>
    public int UFled { get; init; }

    /// <summary>Fights the player TRIED to flee from and failed - still charged points, still ended
    /// every fight, still left the player in the room.</summary>
    public int UFledFail { get; init; }

    public int Withdraw { get; init; }

    /// <summary>Fights the creature DIED in without the player's blow finishing it ("The X drops
    /// dead, poisoned..."). Counted apart from <see cref="Kills"/> because that is what the evidence
    /// supports - the line states the cause, not the agent - and apart from
    /// <see cref="Unresolved"/> because the fight most certainly did resolve. See
    /// <see cref="FightOutcome.NoMore"/>.</summary>
    public int NoMore { get; init; }

    /// <summary>Fights MUD2 closed with a coded "you can fight X no longer" and no stated reason, with
    /// nothing else having resolved them first. Expected to be near-zero; a real count here means a
    /// terminator upstream is going unmatched. See <see cref="FightOutcome.EndOther"/>.</summary>
    public int EndOther { get; init; }

    /// <summary>Fights that never resolved: the encounter ended without any terminator this client
    /// could attribute to them. A BUG COUNT, kept clean of the two outcomes above precisely so it can
    /// be read as one.</summary>
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
    /// estimate of this NPC group's stamina pool. Null until at least one kill is on record.
    /// Non-kills are deliberately excluded: a survivor only proves its pool exceeds what we dealt
    /// (a censored observation), so folding those in biases the estimate low. See STATS_DESIGN.md.
    ///
    /// <para>This used to be described as "the only route" to an NPC's pool, on the grounds that
    /// MUD2 never reports NPC stamina. False either way: the protocol DOES report it, on demand,
    /// via a `diagnose` probe (see <see cref="CombatEventKind.NpcStaminaRead"/>) - it just needs a
    /// stethoscope and a typed command, so it is not always on hand. And every creature's stamina is
    /// separately PUBLISHED (tools/combat/bestiary.tsv, 143 rows), agreeing closely where the two can
    /// be compared - zombies 40-50 published against a 49.0 median here, water-snakes 90 against
    /// 100.5, rams 100 against 98.5. A lookup or a probe reading would be exact and available on a
    /// first encounter, where this estimate needs a kill first. Replacing it is an open scope
    /// decision, not an oversight - see MUD2-PUBLISHED-MECHANICS.md section 10.</para></summary>
    public double? EstimatedStaminaPool { get; init; }

    /// <summary>Kills as a fraction of all matching fights, or null with no fights at all.</summary>
    public double? KillRate => FightCount == 0 ? null : Kills / (double)FightCount;

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
    /// <summary>Bucket key for fights fought bare-handed. Shared between
    /// <see cref="SummarizeByWeapon"/> (which writes it) and the clog formatter (which renders it
    /// as "unarmed" - a single literal in two places is how that mapping silently drifted apart
    /// before; a shared constant makes drifting apart a compile error instead.</summary>
    public const string NoWeaponKey = "(none)";

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
            var key = string.IsNullOrWhiteSpace(record.WeaponUsed) ? NoWeaponKey : record.WeaponUsed;
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

    /// <summary>
    /// A weapon's record against EVERY creature on file, not scoped to one NPC group.
    ///
    /// <para><see cref="SummarizeByWeapon"/> is deliberately group-scoped (susceptibility is a
    /// property of the creature type), which means the weapon table's "usual" column only ever
    /// answers "how does this weapon do against THIS group" - it cannot say whether the group in
    /// front of you is unusually generous or stingy for that weapon overall. This is the ungrouped
    /// counterpart that answers that second question, rendered as the table's "vs all" row.</para>
    /// </summary>
    public static FightHistorySummary SummarizeWeaponGlobal(IEnumerable<FightRecord> records, string? weapon)
    {
        var key = string.IsNullOrWhiteSpace(weapon) ? string.Empty : weapon;
        var matching = new List<FightRecord>();
        foreach (var record in records)
        {
            var recordKey = string.IsNullOrWhiteSpace(record.WeaponUsed) ? string.Empty : record.WeaponUsed;
            if (string.Equals(recordKey, key, StringComparison.OrdinalIgnoreCase))
                matching.Add(record);
        }

        return Summarize(matching);
    }

    private static FightHistorySummary Summarize(List<FightRecord> matching)
    {
        if (matching.Count == 0)
            return FightHistorySummary.Empty;

        var kills = 0;
        var deaths = 0;
        var cFled = 0;
        var cFledFail = 0;
        var uFled = 0;
        var uFledFail = 0;
        var withdraw = 0;
        var noMore = 0;
        var endOther = 0;
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
                case nameof(FightOutcome.Kill): kills++; break;
                case nameof(FightOutcome.Died): deaths++; break;
                case nameof(FightOutcome.CFled): cFled++; break;
                case nameof(FightOutcome.CFledFail): cFledFail++; break;
                case nameof(FightOutcome.UFled): uFled++; break;
                case nameof(FightOutcome.UFledFail): uFledFail++; break;
                case nameof(FightOutcome.Withdraw): withdraw++; break;
                case nameof(FightOutcome.NoMore): noMore++; break;
                case nameof(FightOutcome.EndOther): endOther++; break;
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
            CFled = cFled,
            CFledFail = cFledFail,
            UFled = uFled,
            UFledFail = uFledFail,
            Withdraw = withdraw,
            NoMore = noMore,
            EndOther = endOther,
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
