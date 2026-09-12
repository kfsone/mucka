namespace Mucka.Combat;

/// <summary>
/// Timing constants shared across the combat-tracking classes, hoisted so the classes that used to
/// each carry their own copy can no longer independently drift.
/// </summary>
public static class CombatTiming
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
    /// nothing else, so the two cannot independently drift out of agreement.</para>
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
    /// The next metronome beat on the bracketed lattice: beats sit at <c>boundary - offset</c> and
    /// <c>boundary + offset</c> for every boundary defined by <paramref name="anchorUtc"/>.
    ///
    /// <para><b>Every delay is derived from the anchor, not accumulated from constant legs.</b>
    /// <c>System.Threading.Timer</c> lateness is one-sided - a timer never fires early - so re-arming
    /// from a fixed offset off the previous callback lets lateness accumulate with nothing to correct
    /// it until a beat crosses to the wrong side of its boundary. Deriving each delay from the anchor
    /// instead makes every beat self-correcting: one beat's lateness is absorbed by the next delay
    /// instead of being added to every delay after it.</para>
    ///
    /// <para><b>A beat that is already too late is SKIPPED, not fired late.</b> If this callback ran so
    /// far behind that the next lattice position has passed, the result is the one after it. A click in
    /// the wrong place is worse than a missing click: the player is using it to feel where the boundary
    /// is, and a late one moves the boundary.</para>
    ///
    /// <para>Returns the alternation as well as the delay, because that is a property of WHERE the next
    /// beat falls rather than of what the last beat was - a toggled flag would go on alternating even
    /// after a skip and would then have every subsequent click on the wrong sample.</para>
    /// </summary>
    /// <param name="afterOffsetMs">How far PAST a boundary the after-tick beat sounds.</param>
    /// <param name="preLeadMs">How far BEFORE a boundary the pre-tick beat is STARTED. Deliberately
    /// independent of <paramref name="afterOffsetMs"/> rather than its mirror: the pre-click is started
    /// early by its own clip length so that it FINISHES at the bracket's edge instead of starting there,
    /// which is what leaves the boundary as silence between the two sounds rather than having a 170 ms
    /// sample still ringing when its partner begins. See CombatMetronome.PreTickLeadMilliseconds.</param>
    /// <param name="minDelayMs">A beat closer than this is treated as already gone. Guards against
    /// scheduling a timer for ~0 ms, which would fire immediately and merge audibly with the beat
    /// currently sounding.</param>
    public static (double Delay, bool AfterTick) NextBeat(
        DateTime anchorUtc, DateTime nowUtc, double afterOffsetMs, double preLeadMs, double minDelayMs = 2.0)
    {
        if (afterOffsetMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(afterOffsetMs), afterOffsetMs,
                "The after-tick beat must sound after the boundary.");
        if (preLeadMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(preLeadMs), preLeadMs,
                "The pre-tick beat must start before the boundary.");
        if (afterOffsetMs + preLeadMs >= TickMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(preLeadMs), preLeadMs,
                "The two beats cross: a cycle cannot hold an after-tick and the next pre-tick.");

        // Position within the current cycle. Same normalisation as MillisecondsToNextBoundary, and for
        // the same reason: now can precede the anchor by a few ms at the moment a fight's phase arrives.
        var intoCycle = (nowUtc - anchorUtc).TotalMilliseconds % TickMilliseconds;
        if (intoCycle < 0)
            intoCycle += TickMilliseconds;

        // Two beats per cycle, in order: the after-tick of the boundary just gone, then the pre-tick of
        // the boundary coming up.
        if (intoCycle + minDelayMs <= afterOffsetMs)
            return (afterOffsetMs - intoCycle, true);
        if (intoCycle + minDelayMs <= TickMilliseconds - preLeadMs)
            return (TickMilliseconds - preLeadMs - intoCycle, false);

        // Past both: the after-tick of the next boundary.
        return (TickMilliseconds + afterOffsetMs - intoCycle, true);
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
