using Mucka.Core;

namespace Mucka.ViewModels;

/// <summary>
/// Caches the <see cref="CombatHistoryContext"/> for the "current" primary target/weapon/encounter,
/// backed by <see cref="FightHistoryStore.GetHistoryContext"/> - an O(bucket-size) incremental index
/// lookup (see <c>MudSharp.Combat.HistoryIndex</c>) rather than the O(corpus) scan-and-filter this
/// replaces (DESIGN_FINAL.md section 7.3).
///
/// <para>Extracted out of <c>SidePanelViewModel</c> (which owns the only production instance) purely
/// so this - and specifically the self-comparison-exclusion invariant below - is unit-testable
/// without the MAUI runtime. See <c>mudsharp.Tests.Fixtures.CombatHistoryCacheTests</c> for the
/// regression this class exists to make provable: a live encounter's own fight rows must never enter
/// its own comparison baseline.</para>
///
/// <para><b>Why the cache key has no row-count/version component</b> (unlike the code this
/// replaces, which keyed on <c>FightHistoryStore.Snapshot().Count</c> specifically so it WOULD
/// re-query whenever the corpus grew, then relied on <c>FightHistory.ExcludingEncounterFrom</c> to
/// filter the result): <c>HistoryIndex.Insert</c> only ever runs once a fight has fully closed and
/// been flushed (<c>FightHistoryRecorder.FlushLocked</c> -&gt; <c>FightHistoryStore.Append</c>),
/// which happens at the exact moment the ENCOUNTER on screen closes - not per individual fight
/// within a pack encounter (FlushLocked writes every fight of an encounter together). If this cache
/// re-queried the index every time it changed, the very first re-query after an encounter ends would
/// pick up that encounter's own just-flushed rows - exactly the "now" and "usual" becoming identical
/// at n=1 bug the old runtime filter existed to prevent. Keying purely on (instance, weapon,
/// encounter start) instead means the cache is filled AT MOST ONCE per encounter, strictly BEFORE
/// that encounter's own flush can happen (Resolve is only ever called while <c>_hasCombatData</c> is
/// true, which starts becoming true on the FIRST combat event, long before any close), and is never
/// invalidated by anything except a genuinely different encounter/target/weapon. It therefore cannot
/// observe its own just-added rows - self-comparison is impossible by construction, not filtered
/// out, exactly as <c>HistoryIndex</c>'s own class remarks describe for the index side of this same
/// guarantee.</para>
/// </summary>
public sealed class CombatHistoryCache
{
    private string? _cachedInstance;
    private string? _cachedWeapon;
    private DateTime? _cachedEncounterStart;
    private CombatHistoryContext _cached = CombatHistoryContext.Empty;

    /// <summary>Returns the cached context when (instance, weapon, encounter start) all match the
    /// last call; otherwise queries <paramref name="store"/> once (O(bucket-size), never a corpus
    /// scan) and caches the fresh result under the new key.</summary>
    public CombatHistoryContext Resolve(
        FightHistoryStore store,
        string instanceName,
        string groupName,
        string? currentWeapon,
        DateTime? encounterStartUtc)
    {
        if (string.Equals(_cachedInstance, instanceName, StringComparison.OrdinalIgnoreCase)
            && _cachedEncounterStart == encounterStartUtc
            && string.Equals(_cachedWeapon, currentWeapon, StringComparison.OrdinalIgnoreCase))
        {
            return _cached;
        }

        var (instance, group, byWeapon, weaponGlobal) =
            store.GetHistoryContext(instanceName, groupName, currentWeapon);

        _cachedInstance = instanceName;
        _cachedEncounterStart = encounterStartUtc;
        _cachedWeapon = currentWeapon;
        _cached = new CombatHistoryContext(instanceName, groupName, instance, group, byWeapon, weaponGlobal);
        return _cached;
    }
}
