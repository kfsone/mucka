namespace MudSharp.Combat;

/// <summary>
/// The one-shot stamina-baseline relay shared (as a pattern, not a shared instance) by
/// <c>Core.SwingLedger</c>, <c>Core.FightHistoryRecorder</c>, and
/// <c>ViewModels.CombatStatsAggregator</c>. Each owns its own private instance - this is a small,
/// pure, MAUI-independent helper (matching <see cref="CombatTierResolver"/>'s own pattern in this
/// folder), not a piece of shared mutable state, since two of those three
/// consumers run on the Feed thread and one runs on the UI thread.
///
/// <para><b>The problem this solves.</b> An NPC hit line like "The zombie hits you (95/100)." is
/// parsed TWICE for the SAME line: once generically by <c>GameLineAnalyzer</c> (fires
/// <c>StatsUpdated -&gt; Observe(95)</c> FIRST, since <c>MudStreamParser</c> raises
/// <c>StatsUpdated</c> before <c>LineReady</c>/the combat tracker's own regex for the same line),
/// and once by the combat tracker's own hit regex (<c>RangeLow=95</c>, reaching
/// <see cref="ResolveDelta"/> SECOND). Without relaying the value <see cref="LastKnown"/> held
/// immediately before the first parse, it would already equal 95 by the time the second parse
/// computes a delta, making every hit's delta compute to exactly 0 - a real, previously-shipped bug
/// (confirmed live: damage taken always showed 0.0, most visible on single-hit fights since NPCs
/// miss often).</para>
/// </summary>
public sealed class StaminaDeltaRelay
{
    private int? _lastKnown;
    private int? _pendingPreUpdate;

    /// <summary>The most recently observed stamina reading, for consumers that need the raw running
    /// value alongside the relay (e.g. seeding a new per-NPC accumulator with "stamina right now").</summary>
    public int? LastKnown => _lastKnown;

    /// <summary>Feeds one external stamina reading (a qs/heartbeat probe, natural regen, a
    /// dreamword/heal, etc.) into the running baseline, stashing the PRIOR value as the one-shot
    /// relay for the very next <see cref="ResolveDelta"/> call.</summary>
    public void Observe(int? currentStamina)
    {
        if (currentStamina is null)
            return;

        _pendingPreUpdate = _lastKnown;
        _lastKnown = currentStamina.Value;
    }

    /// <summary>
    /// Resolves one incoming blow into a stamina delta AND the pre-hit baseline it was measured
    /// against. Both come out together because they are the two halves of one attribution -
    /// returning only the delta and letting the caller reconstruct the baseline would reinvite the
    /// exact arithmetic this type exists to centralise.
    /// </summary>
    public (int? Delta, int? Baseline) ResolveDelta(int? currentStamina)
    {
        if (currentStamina is null)
            return (null, null);

        // Trust the relay ONLY when the last Observe call was for this exact value, i.e. it really
        // did fire for this same line. A stale relay left over from an unrelated earlier update must
        // not outrank an already-correct _lastKnown; and when they differ (e.g. a blow to exactly 0
        // stamina, which the compact-stamina scan does not fire for at all) _lastKnown was never
        // touched by this line and already holds the pre-hit baseline directly.
        var baseline = _pendingPreUpdate is not null && _lastKnown == currentStamina
            ? _pendingPreUpdate
            : _lastKnown;

        int? delta = null;
        if (baseline is not null)
        {
            var d = baseline.Value - currentStamina.Value;
            // A negative delta means stamina went UP across the blow (regen or a heal landing in the
            // same tick outran it); there is no honest damage figure to record for that, and a
            // clamped 0 would read as "armour soaked it" - which is a different fact.
            if (d >= 0)
                delta = d;
        }

        _lastKnown = currentStamina.Value;
        _pendingPreUpdate = null;
        return (delta, baseline);
    }
}
