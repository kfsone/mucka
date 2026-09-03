namespace MudSharp.Combat;

/// <summary>
/// The estimates for every pool key on file, computed once from the corpus and read per panel
/// refresh.
///
/// <para><b>Why the estimates are precomputed rather than derived on lookup.</b>
/// <see cref="StaminaPoolEstimator.Estimate"/> sorts, filters and sweeps a species' whole observation
/// set - cheap in absolute terms, and completely unsuited to the UI thread on a refresh path
/// (Invariant #1). The corpus only changes when an encounter closes, so the work belongs at load and
/// at that boundary, exactly where <see cref="SwingDamageIndex"/> puts its own.</para>
///
/// <para>Threading matches its siblings: <see cref="Load"/> runs on a background warm task or the
/// session Feed thread, <see cref="Lookup"/> on the UI thread, and the lock is only ever held for a
/// dictionary probe or a bulk replace.</para>
/// </summary>
public sealed class StaminaPoolIndex
{
    private readonly object _lock = new();
    private Dictionary<string, StaminaPoolEstimate> _byKey =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Recomputes every estimate from the supplied observations, bucketing them by
    /// <see cref="NpcPoolKey"/>. Replaces rather than merges - an estimate is a function of the whole
    /// observation set for its key, so folding in a partial set would produce a figure that is not an
    /// estimate of anything.
    /// </summary>
    public void Load(IEnumerable<PoolFightObservation> observations)
    {
        var buckets = new Dictionary<string, List<PoolFightObservation>>(StringComparer.OrdinalIgnoreCase);
        foreach (var observation in observations)
        {
            var key = NpcPoolKey.For(observation.NpcName);
            if (key.Length == 0)
                continue;
            if (!buckets.TryGetValue(key, out var list))
                buckets[key] = list = [];
            list.Add(observation);
        }

        var estimates = new Dictionary<string, StaminaPoolEstimate>(buckets.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, list) in buckets)
            estimates[key] = StaminaPoolEstimator.Estimate(key, list);

        lock (_lock)
            _byKey = estimates;
    }

    /// <summary>
    /// This creature's species estimate, or <see cref="StaminaPoolEstimate.None"/> when nothing is on
    /// file. The returned record still carries the pool key and the regeneration label even when there
    /// is no evidence, so a caller can say "no data for large rats" rather than "no data".
    /// </summary>
    public StaminaPoolEstimate Lookup(string? npcName)
    {
        var key = NpcPoolKey.For(npcName);
        if (key.Length == 0)
            return StaminaPoolEstimate.None;

        lock (_lock)
        {
            if (_byKey.TryGetValue(key, out var estimate))
                return estimate;
        }

        return StaminaPoolEstimate.None with { PoolKey = key, Quantity = StaminaPoolEstimator.QuantityFor(key) };
    }

    /// <summary>Every estimate on file, for an analysis view or a test. Returned by reference rather
    /// than copied, unlike <see cref="ReachMarkIndex.Snapshot"/>: <see cref="Load"/> REPLACES the
    /// dictionary instead of mutating it, so what a caller is handed can never change under it.</summary>
    public IReadOnlyDictionary<string, StaminaPoolEstimate> Snapshot()
    {
        lock (_lock)
            return _byKey;
    }
}
