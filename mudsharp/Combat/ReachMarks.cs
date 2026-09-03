namespace MudSharp.Combat;

/// <summary>
/// How far a creature has been SEEN to reach with one blow, and over how many blows.
///
/// <para><b>This is a floor under the creature's true maximum, never the maximum itself.</b> Nothing
/// in MUD2 publishes a per-creature damage cap to the client, so every figure here is the largest
/// thing that has happened so far. It can only ever rise: one more swing can raise it, no number of
/// swings can close it, and a creature that has hit for 7 across 873 blows has still not proved it
/// cannot hit for 8.</para>
///
/// <para>The type is named for what it means rather than for how it is computed, because the
/// computation ("a max") is exactly what invites the misreading. There is deliberately no
/// <c>Max</c> member: a caller that wants a ceiling has to notice it is being handed a floor.</para>
///
/// <para>Measured floors as of the corpus this was built against - rat 7 over 873 blows, water-snake
/// 11, ram 13, zombie 17, thief 19, goblin 22, ogre 26. Those are observations, not a bestiary; they
/// are here as a sense of scale and are not hard-coded anywhere.</para>
/// </summary>
/// <param name="ReachesAtLeast">The largest single blow observed. 0 with no blows on file, which is
/// "unknown" and must never be rendered as "harmless".</param>
/// <param name="Blows">Landed blows this rests on. A high figure from three blows and the same figure
/// from three hundred are different claims, so the count travels with it.</param>
public readonly record struct ReachMark(double ReachesAtLeast, int Blows)
{
    public static readonly ReachMark None = new(0, 0);

    /// <summary>False when nothing has been observed. A caller must test this rather than compare
    /// <see cref="ReachesAtLeast"/> against 0 - a creature really can land a blow that takes nothing
    /// off, and 0 from one blow is a measurement where 0 from none is silence.</summary>
    public bool HasEvidence => Blows > 0;

    /// <summary>This mark with one more blow folded in. Monotone by construction: the reach never
    /// falls, whatever the new blow was.</summary>
    public ReachMark Observe(double damage)
        => new(damage > ReachesAtLeast ? damage : ReachesAtLeast, Blows + 1);

    /// <summary>The stronger of two marks - the higher reach, and the blow counts added. Used to fold
    /// a freshly loaded corpus figure into whatever this session has already seen.</summary>
    public ReachMark Merge(ReachMark other)
        => new(Math.Max(ReachesAtLeast, other.ReachesAtLeast), Blows + other.Blows);
}

/// <summary>
/// Per-species reach marks, keyed on <see cref="NpcPoolKey"/> - so "large rat" and "rat" keep
/// separate marks, which they must, since the whole reason the key exists is that they are different
/// creatures.
///
/// <para>Threading matches <see cref="SwingDamageIndex"/>, which it sits beside: folds arrive on the
/// session Feed thread or a background warm task, lookups run on the UI thread, and the lock is only
/// ever held for a dictionary probe or a single insert.</para>
/// </summary>
public sealed class ReachMarkIndex
{
    private readonly object _lock = new();
    private readonly Dictionary<string, ReachMark> _marks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Folds in one blow the player took from <paramref name="npcName"/>. Negative damage is
    /// ignored - it is not a blow, it is a bookkeeping artefact of a regen tick outrunning a hit.</summary>
    public void Observe(string? npcName, double damage)
    {
        var key = NpcPoolKey.For(npcName);
        if (key.Length == 0 || damage < 0)
            return;

        lock (_lock)
            _marks[key] = Get(key).Observe(damage);
    }

    /// <summary>
    /// Folds a set of per-instance figures - as read straight off the database's own GROUP BY - into
    /// the per-species marks.
    ///
    /// <para>MERGES rather than replaces, unlike <see cref="SwingDamageIndex.LoadProfiles"/>. A reach
    /// that only ever grows cannot be un-grown by a reload, and a warm arriving after live play has
    /// already folded in blows must not lower what those blows established. The blow counts do add, so
    /// warming twice over the same corpus would double-count them - call it once, as the ledger
    /// does.</para>
    /// </summary>
    public void Load(IEnumerable<(string NpcName, double ReachesAtLeast, int Blows)> perInstance)
    {
        lock (_lock)
        {
            foreach (var (npcName, reach, blows) in perInstance)
            {
                var key = NpcPoolKey.For(npcName);
                if (key.Length == 0 || blows <= 0)
                    continue;
                _marks[key] = Get(key).Merge(new ReachMark(reach, blows));
            }
        }
    }

    /// <summary>This creature's species mark, or <see cref="ReachMark.None"/> when nothing is on
    /// file.</summary>
    public ReachMark Lookup(string? npcName)
    {
        var key = NpcPoolKey.For(npcName);
        if (key.Length == 0)
            return ReachMark.None;

        lock (_lock)
            return Get(key);
    }

    /// <summary>Every mark on file, for an analysis view or a test. A copy - the caller cannot hold a
    /// reference into the index and read it without the lock.</summary>
    public IReadOnlyDictionary<string, ReachMark> Snapshot()
    {
        lock (_lock)
            return new Dictionary<string, ReachMark>(_marks, StringComparer.OrdinalIgnoreCase);
    }

    private ReachMark Get(string key) => _marks.TryGetValue(key, out var mark) ? mark : ReachMark.None;
}
