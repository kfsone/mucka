namespace Mucka.Core;

/// <summary>
/// Timing constants shared across the combat-tracking classes, hoisted so the classes that used to
/// each carry their own copy can no longer independently drift.
/// </summary>
internal static class CombatTiming
{
    /// <summary>The one MUD2 combat tick duration, shared by <see cref="Mucka.Audio.CombatMetronome"/>
    /// (the click) and <c>Mucka.Rendering.TickSweep</c> (the bar - Windows-only, so not a resolvable
    /// cref from a cross-platform doc comment). Measured, not chosen: swing gaps across the whole
    /// capture corpus are exact multiples of this, with 76-94% of a session's swings landing in a
    /// single 20 ms bin.</summary>
    public const double TickMilliseconds = 2000.0;

    /// <summary>
    /// Milliseconds from <paramref name="nowUtc"/> to the next tick boundary on the lattice defined by
    /// <paramref name="anchorUtc"/>.
    ///
    /// <para><b>One implementation, on purpose.</b> Both renderings of the tick - the bar
    /// (<c>Mucka.Rendering.TickSweep</c>) and the click
    /// (<see cref="Mucka.Audio.CombatMetronome"/>) - locate the rollover through this method and
    /// nothing else. They each used to do the modulo themselves, and while the two expressions agreed
    /// algebraically, "the bar and the click disagree" is the single most reported fault in this
    /// feature's history and it is not worth leaving two places where that could become true.</para>
    ///
    /// <para>Returns a full tick, never zero, when <paramref name="nowUtc"/> lands exactly on a
    /// boundary: the question is "how long until the NEXT one", and a bar told it has 0 ms left would
    /// restart from empty while a click told the same would fire immediately on a rollover it has
    /// already marked.</para>
    /// </summary>
    public static double MillisecondsToNextBoundary(DateTime anchorUtc, DateTime nowUtc)
    {
        var intoCycle = (nowUtc - anchorUtc).TotalMilliseconds % TickMilliseconds;
        // A negative modulo happens whenever now precedes the anchor, which is not a hypothetical: the
        // anchor is a feed-thread timestamp and the caller reads its own clock after a dispatch hop, so
        // a few ms of inversion is normal at the moment a fight's phase first arrives.
        if (intoCycle < 0)
            intoCycle += TickMilliseconds;
        return TickMilliseconds - intoCycle;
    }

    /// <summary>
    /// How long a weapon-equip line seen just before the client noticed a new fight may still be
    /// carried into it, shared by <c>SwingLedger</c>, <c>FightHistoryRecorder</c>, and
    /// <c>CombatStatsAggregator</c>. Deliberately SHORT: MUD2's wielded weapon is per-fight, not
    /// persistent - it is dropped at fight end and <c>wield</c> is refused outside a fight - so an
    /// equip more than a few seconds old says nothing about the fight starting now.
    ///
    /// <para>Sharing this constant does NOT mean the three classes resolve it the same way - they
    /// deliberately don't (each has its own reason, documented at its own use site: one resolves at
    /// encounter-open against the event's own timestamp, one at encounter-open against wall-clock
    /// time, one lazily against the first post-open event). Only the tolerance itself is shared.</para>
    /// </summary>
    public static readonly TimeSpan PendingWeaponWindow = TimeSpan.FromSeconds(5);
}
