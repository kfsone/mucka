namespace MudSharp.Combat;

/// <summary>
/// Incremental replacement for scanning the WHOLE fight corpus on every history lookup.
///
/// <para>The failure mode this exists to fix (DESIGN_FINAL.md section 7.3): the previous approach
/// filtered <c>FightHistory.ExcludingEncounterFrom(...)</c> then ran three median-computing passes
/// over the ENTIRE loaded corpus on every cache miss, and misses happen on every fight resolution -
/// so the cost grew across a whole session. This index instead maintains a small SORTED list per
/// bucket (per npc_group, per npc_name instance, per (npc_group, weapon), per weapon-global) and
/// inserts one record at a time via binary search - O(log bucket-size) per insert, and reading a
/// median off an already-sorted list is O(1). Bucket counts stay small and roughly constant across
/// a session (bounded by distinct creature types and weapons ever fought, not by total fights),
/// which is what makes this genuinely O(1)-ish rather than just a smaller constant on the same
/// O(corpus) shape.</para>
///
/// <para><b>Self-comparison is structurally impossible, not filtered out</b> (7.3's own framing):
/// nothing calls <see cref="Insert"/> except <c>Core.FightHistoryStore.Append</c>, which only ever
/// runs once a fight has fully closed and been flushed (see FightHistoryRecorder.FlushLocked). The
/// still-open encounter currently on screen therefore cannot be in this index yet, by construction -
/// there is no runtime exclusion check to get wrong or forget to call, because there is nothing to
/// exclude. Correctness here rests entirely on that update-ordering guarantee: nothing but a closed
/// fight's own append path may ever call Insert.</para>
///
/// <para>Not thread-safe on its own - <c>Core.FightHistoryStore</c> is the single owner and
/// serializes every access (Insert from Append/LoadAsync, reads from GetHistoryContext) under its
/// own lock, exactly as it already does for its own <c>_records</c> list.</para>
/// </summary>
public sealed class HistoryIndex
{
    // A plain printable ASCII character that cannot appear in an npc_group or weapon name (both are
    // plain words - see NpcGroups.cs/reduce_combat.py's own naming), so concatenation can never
    // collide two different (group, weapon) pairs onto the same combined dictionary key.
    private const char KeySeparator = '|';

    private readonly Dictionary<string, IncrementalFightBucket> _byGroup =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IncrementalFightBucket> _byInstance =
        new(StringComparer.OrdinalIgnoreCase);
    // Keyed "group|weaponKey" where weaponKey uses FightHistory.NoWeaponKey for an unarmed/blank
    // weapon - mirrors FightHistory.SummarizeByWeapon's own bucketing exactly.
    private readonly Dictionary<string, IncrementalFightBucket> _byGroupWeapon =
        new(StringComparer.OrdinalIgnoreCase);
    // Keyed by weapon alone, using "" (not NoWeaponKey) for unarmed - mirrors
    // FightHistory.SummarizeWeaponGlobal's own (different!) convention exactly. Two separate
    // null-weapon spellings already existed in the codebase before this index; preserving both
    // distinctly here (rather than "fixing" them to agree) keeps every existing caller's output
    // identical to what it was before this index replaced the corpus scan.
    private readonly Dictionary<string, IncrementalFightBucket> _byWeaponGlobal =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Folds one completed fight into every bucket it belongs to. O(log n) per bucket,
    /// where n is that bucket's own sample count (small and roughly constant across a session), not
    /// the size of the whole corpus. Call exactly once per fight, only once it has been fully
    /// flushed - see the class remarks on why that is what keeps self-comparison impossible.</summary>
    public void Insert(FightRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.NpcGroup))
        {
            GetOrAdd(_byGroup, record.NpcGroup).Insert(record);

            var byWeaponKey = record.NpcGroup + KeySeparator +
                (string.IsNullOrWhiteSpace(record.WeaponUsed) ? FightHistory.NoWeaponKey : record.WeaponUsed);
            GetOrAdd(_byGroupWeapon, byWeaponKey).Insert(record);
        }

        if (!string.IsNullOrWhiteSpace(record.NpcName))
            GetOrAdd(_byInstance, record.NpcName).Insert(record);

        var globalKey = string.IsNullOrWhiteSpace(record.WeaponUsed) ? string.Empty : record.WeaponUsed;
        GetOrAdd(_byWeaponGlobal, globalKey).Insert(record);
    }

    /// <summary>Aggregates for one specific NPC instance (e.g. "rat0"). Empty (not null) when
    /// nothing is on file yet - mirrors FightHistory.SummarizeInstance's contract exactly.</summary>
    public FightHistorySummary GetInstanceSummary(string instanceName)
        => !string.IsNullOrWhiteSpace(instanceName) && _byInstance.TryGetValue(instanceName, out var bucket)
            ? bucket.ToSummary()
            : FightHistorySummary.Empty;

    /// <summary>Aggregates for one NPC group (e.g. "rats"). Mirrors FightHistory.Summarize's
    /// group-only overload.</summary>
    public FightHistorySummary GetGroupSummary(string groupName)
        => !string.IsNullOrWhiteSpace(groupName) && _byGroup.TryGetValue(groupName, out var bucket)
            ? bucket.ToSummary()
            : FightHistorySummary.Empty;

    /// <summary>Per-weapon breakdown against one NPC group, best-evidenced first - mirrors
    /// FightHistory.SummarizeByWeapon's contract (including its sort order) exactly. Enumerates only
    /// the DISTINCT (group, weapon) pairs on file, not the fight corpus - bounded by how many
    /// weapons have ever been used against this group, which stays small even after a long
    /// session.</summary>
    public IReadOnlyList<WeaponHistorySummary> GetByWeapon(string groupName)
    {
        if (string.IsNullOrWhiteSpace(groupName))
            return [];

        var prefix = groupName + KeySeparator;
        var result = new List<WeaponHistorySummary>();
        foreach (var (key, bucket) in _byGroupWeapon)
        {
            if (key.Length <= prefix.Length || !key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            result.Add(new WeaponHistorySummary(key[prefix.Length..], bucket.ToSummary()));
        }

        result.Sort((a, b) =>
        {
            var bySample = b.Summary.FightCount.CompareTo(a.Summary.FightCount);
            return bySample != 0 ? bySample : string.Compare(a.Weapon, b.Weapon, StringComparison.OrdinalIgnoreCase);
        });
        return result;
    }

    /// <summary>One weapon's record against every creature on file, ungrouped - mirrors
    /// FightHistory.SummarizeWeaponGlobal's contract exactly (including its own "" unarmed
    /// convention, distinct from GetByWeapon's NoWeaponKey).</summary>
    public FightHistorySummary GetWeaponGlobalSummary(string? weapon)
    {
        var key = string.IsNullOrWhiteSpace(weapon) ? string.Empty : weapon;
        return _byWeaponGlobal.TryGetValue(key, out var bucket) ? bucket.ToSummary() : FightHistorySummary.Empty;
    }

    /// <summary>
    /// Whether this name has EVER been recorded as a weapon the player fought with.
    ///
    /// <para>The only weapon vocabulary this client has, and it is earned rather than guessed: MUD2
    /// never says "this object is a weapon", so the alternate-weapon offer (the rail's Ctrl+W chip)
    /// decides what in the player's pack is swappable by asking which carried items are already on
    /// file as having been wielded in a fight. A hand-written noun list would guess, and guessing
    /// wrong here costs a wield attempt - which in MUD2 drops your guard and hands the opponent a
    /// free swing.</para>
    ///
    /// <para>One dictionary probe, no allocation: this is called once per carried item on the
    /// combat refresh path (Invariant #1). The unarmed bucket's "" key can never be reached, since
    /// a blank name is rejected up front.</para>
    /// </summary>
    public bool IsKnownWeapon(string? name)
        => !string.IsNullOrWhiteSpace(name) && _byWeaponGlobal.ContainsKey(name);

    private static IncrementalFightBucket GetOrAdd(Dictionary<string, IncrementalFightBucket> map, string key)
    {
        if (!map.TryGetValue(key, out var bucket))
            map[key] = bucket = new IncrementalFightBucket();
        return bucket;
    }
}

/// <summary>
/// One bucket's running aggregate: outcome counts (updated O(1)) plus a handful of SORTED value
/// lists (updated O(log n) via binary-search insertion) for the medians <see cref="FightHistorySummary"/>
/// needs. <see cref="Insert"/> mirrors <c>FightHistory.Summarize</c>'s per-record logic line for
/// line - any change to what a fight contributes to a median must be made in BOTH places or the
/// live incremental path and the offline/test corpus-scan path would silently disagree.
/// </summary>
internal sealed class IncrementalFightBucket
{
    private int _kills, _deaths, _cFled, _cFledFail, _uFled, _uFledFail, _withdraw, _noMore,
        _endOther, _unresolved, _fightCount;

    private readonly List<double> _damageDone = [];
    private readonly List<double> _damageTaken = [];
    private readonly List<double> _durations = [];
    private readonly List<double> _youRates = [];
    private readonly List<double> _theyRates = [];
    private readonly List<double> _damagePerHit = [];
    private readonly List<double> _killDamage = [];

    public void Insert(FightRecord record)
    {
        _fightCount++;
        switch (record.Outcome)
        {
            case nameof(FightOutcome.Kill): _kills++; break;
            case nameof(FightOutcome.Died): _deaths++; break;
            case nameof(FightOutcome.CFled): _cFled++; break;
            case nameof(FightOutcome.CFledFail): _cFledFail++; break;
            case nameof(FightOutcome.UFled): _uFled++; break;
            case nameof(FightOutcome.UFledFail): _uFledFail++; break;
            case nameof(FightOutcome.Withdraw): _withdraw++; break;
            // Kept out of the default bucket on purpose: "the creature died of poison" and "we lost
            // track of this fight" are very different evidence - see FightOutcome.NoMore.
            case nameof(FightOutcome.NoMore): _noMore++; break;
            case nameof(FightOutcome.EndOther): _endOther++; break;
            default: _unresolved++; break;
        }

        // Pool estimate requires swing detail as well as a kill - see FightHistory.Summarize's
        // identical guard for why (a narrative kill has no per-hit ranges to sum).
        if (record.IsKill && record.HasSwingDetail && record.ApproxDamageDone > 0)
            InsertSorted(_killDamage, record.ApproxDamageDone);

        if (!record.HasSwingDetail)
            return;

        InsertSorted(_damageDone, record.ApproxDamageDone);
        InsertSorted(_damageTaken, record.ApproxDamageTaken);
        if (record.DurationMs > 0)
            InsertSorted(_durations, record.DurationMs / 1000.0);

        var yourAttempts = record.YouHits + record.YouMisses;
        if (yourAttempts > 0)
            InsertSorted(_youRates, record.YouHits / (double)yourAttempts);

        var theirAttempts = record.TheyHits + record.TheyMisses;
        if (theirAttempts > 0)
            InsertSorted(_theyRates, record.TheyHits / (double)theirAttempts);

        if (record.YouHits > 0 && record.ApproxDamageDone > 0)
            InsertSorted(_damagePerHit, record.ApproxDamageDone / record.YouHits);
    }

    public FightHistorySummary ToSummary() => new()
    {
        SampleSize = _damageDone.Count,
        FightCount = _fightCount,
        Kills = _kills,
        Deaths = _deaths,
        CFled = _cFled,
        CFledFail = _cFledFail,
        UFled = _uFled,
        UFledFail = _uFledFail,
        Withdraw = _withdraw,
        NoMore = _noMore,
        EndOther = _endOther,
        Unresolved = _unresolved,
        MedianDamageDone = MedianOfSorted(_damageDone),
        MedianDamageTaken = MedianOfSorted(_damageTaken),
        MedianDurationSeconds = MedianOfSorted(_durations),
        MedianYouHitRate = MedianOfSorted(_youRates),
        MedianTheyHitRate = MedianOfSorted(_theyRates),
        MedianDamagePerHit = MedianOfSorted(_damagePerHit),
        EstimatedStaminaPool = MedianOfSorted(_killDamage),
    };

    /// <summary>Binary-search insertion into an already-sorted list - O(log n) search, O(n) worst
    /// case for the shift (same as List&lt;T&gt;.Insert always costs), but n here is one bucket's
    /// own sample count, not the corpus.</summary>
    private static void InsertSorted(List<double> sorted, double value)
    {
        var index = sorted.BinarySearch(value);
        sorted.Insert(index < 0 ? ~index : index, value);
    }

    /// <summary>O(1) median read - the list is maintained sorted by construction, so this never
    /// re-sorts (unlike FightHistory.Median, which sorts a freshly-built per-query list).</summary>
    private static double? MedianOfSorted(List<double> sorted)
    {
        if (sorted.Count == 0)
            return null;
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }
}
