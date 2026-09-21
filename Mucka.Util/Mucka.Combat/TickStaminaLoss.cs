namespace Mucka.Combat;

/// <summary>
/// How much stamina the player lost on the most recent combat tick, so the rail can tint the slice of
/// their ring that has just gone.
///
/// <para>The just-lost segment of the player's stamina ring is tinted red - the same gray, tinted.</para>
///
/// <para><b>Grouped by arrival, not by wall-clock bucket - and that IS the server tick.</b> MUD2
/// resolves combat on a 2000 ms tick (median residual 26 ms over 68 sessions), so every blow belonging
/// to one tick reaches the client within a few tens of milliseconds of the others while consecutive
/// ticks are two seconds apart. Grouping losses that arrive inside <see cref="SameTickWindow"/> - a
/// quarter of a tick - therefore separates ticks cleanly with two orders of magnitude of margin.</para>
///
/// <para>Bucketing on absolute 2000 ms boundaries instead would be worse, not better: a tick whose
/// blows straddle a boundary would be split into two slices, which happens roughly 26/2000 of the
/// time. Deliberately nothing here is keyed to a render frame - the canvas repaints on state change
/// and never on a timer.</para>
///
/// <para><b>Several blows in one tick make ONE slice.</b> They accumulate into the same burst, so a
/// pack landing four hits shows a single combined segment rather than four stacked ones.</para>
///
/// <para><b>It fades over one tick, then holds at zero until the next loss replaces it.</b> The tint
/// is not meant to signal an amount still outstanding, only the moment the loss happened, so it must
/// not stand at full strength indefinitely. This class tracks only the ARC's magnitude
/// (<see cref="LostThisTick"/>) and when it arrived (<see cref="LastLossUtc"/>) - <see cref="FadeFactor"/>
/// is a pure function of those two values plus "now", sampled by the renderer at paint time, so
/// nothing here becomes stateful or timer-driven. The render cadence that makes a 2000 ms fade visible
/// at all (rather than one visible step) is a question for the caller, not for this class - see
/// CombatRailView's own remarks on why no new timer was added for it.</para>
///
/// <para>Pure and MAUI-free; tested from Mucka.Util.Tests.</para>
/// </summary>
public sealed class TickStaminaLoss
{
    /// <summary>Losses arriving within this of each other belong to the same tick. A quarter of the
    /// 2000 ms tick: far above the ~26 ms spread within one tick, far below the gap between two.</summary>
    public static readonly TimeSpan SameTickWindow =
        TimeSpan.FromMilliseconds(CombatTiming.TickMilliseconds / 4.0);

    /// <summary>
    /// The just-lost slice's tint strength at the instant it arrives, before <see cref="FadeFactor"/>
    /// brings it down toward 0.
    ///
    /// <para><b>The slice persisting, not its peak brightness, is what reads as wrong.</b> Without
    /// <see cref="FadeFactor"/> decaying it to nothing inside one tick, a bright red arc would sit on
    /// the ring long after the moment it described, reading as a live part of the gauge rather than a
    /// fading mark. Once brightness no longer implies permanence, the peak is free to stay high.</para>
    ///
    /// <para><b>Why a lower peak reads as invisible rather than merely subtle.</b>
    /// <c>CombatRailView.SealTrack</c> is #767676 dimmed to 30%, so about (35,35,35). Tinted toward the
    /// hostile red (70,42,44) on a thin arc over a #101618 panel, a low tint strength is barely
    /// distinguishable from the untinted track; 0.35 (about (104,48,53)) is.</para>
    /// </summary>
    public const float PeakTintStrength = 0.35f;

    private int? _lastStamina;
    private DateTime _lastLossUtc;
    private double _lostThisTick;

    /// <summary>Stamina lost on the most recent tick that took any, or 0 when nothing has. Never
    /// negative, and never a total across ticks.</summary>
    public double LostThisTick => _lostThisTick;

    /// <summary>When the current <see cref="LostThisTick"/> burst arrived - <see cref="DateTime.MinValue"/>
    /// (the field's default) whenever nothing has ever been lost. Surfaced so the renderer can compute
    /// elapsed-since-loss itself at paint time; see <see cref="FadeFactor"/>. Meaningless on its own
    /// when <see cref="LostThisTick"/> is 0 - a stale timestamp from a slice a later STAMINA GAIN
    /// already cleared - so callers must gate on LostThisTick first, exactly as the renderer already
    /// does to decide whether to draw a slice at all.</summary>
    public DateTime LastLossUtc => _lastLossUtc;

    /// <summary>
    /// The just-lost slice's tint strength "right now": <see cref="PeakTintStrength"/> at the instant
    /// <paramref name="lastLossUtc"/> is, fading LINEARLY to 0 over one whole
    /// <see cref="CombatTiming.TickMilliseconds"/> tick, then holding at 0.
    ///
    /// <para>A pure function of the two timestamps rather than a method on an instance, because the
    /// renderer that needs this (<c>CombatRailView.DrawPlayerBar</c>) only ever has the timestamp carried
    /// through <c>CombatLiveView.StaminaLossUtc</c> - a value published once per refresh - and not a
    /// live reference to whichever <see cref="TickStaminaLoss"/> instance produced it.</para>
    ///
    /// <para>Never negative and never fabricated: a caller with no timestamp at all (should not
    /// happen while a slice is being drawn, but CLAUDE.md forbids inventing one regardless) should
    /// treat that as "no tint" rather than call this with a guessed time - see the call site's own
    /// remarks.</para>
    /// </summary>
    public static float FadeFactor(DateTime lastLossUtc, DateTime nowUtc)
    {
        var elapsedMs = (nowUtc - lastLossUtc).TotalMilliseconds;
        // Clock skew guard, the same shape Observe's own remarks apply elsewhere in this file: the
        // timestamp and "now" can come from different reads a dispatch hop apart.
        if (elapsedMs < 0)
            elapsedMs = 0;
        if (elapsedMs >= CombatTiming.TickMilliseconds)
            return 0f;

        return (float)(PeakTintStrength * (1.0 - (elapsedMs / CombatTiming.TickMilliseconds)));
    }

    /// <summary>
    /// Folds in a stamina reading.
    /// </summary>
    /// <param name="stamina">The player's stamina as just observed, or null when unknown - which is
    /// ignored rather than treated as zero, since a missing reading is not a loss of everything.</param>
    /// <param name="atUtc">When it was observed.</param>
    public void Observe(int? stamina, DateTime atUtc)
    {
        if (stamina is not int now)
            return;

        if (_lastStamina is not int previous)
        {
            _lastStamina = now;
            return;
        }

        _lastStamina = now;
        var delta = previous - now;

        if (delta > 0)
        {
            // Same tick, or the start of a new one.
            _lostThisTick = atUtc - _lastLossUtc <= SameTickWindow ? _lostThisTick + delta : delta;
            _lastLossUtc = atUtc;
            return;
        }

        if (delta < 0)
        {
            // Stamina went UP - regen, a wafer, a heal. The slice is defined as the segment sitting
            // immediately behind the current boundary, and a gain moves that boundary away from it, so
            // the slice no longer marks anything real. Cleared rather than left stranded.
            _lostThisTick = 0;
        }
    }

    /// <summary>Forgets everything. For an encounter boundary, where carrying a slice across from the
    /// last fight would mark a loss that belongs to a different one - called from
    /// <c>SidePanelViewModel.OnInCombatChanged</c>'s <c>inCombat</c> (start-of-encounter) branch. Not
    /// merely defensive: <c>CombatTracker.Begin</c> documents that MUD2 can close one encounter and
    /// open the next in the SAME frame, so without this the last blow of a just-finished fight could
    /// still be drawn tinted over the new encounter's opening frames.</summary>
    public void Reset()
    {
        _lastStamina = null;
        _lostThisTick = 0;
        _lastLossUtc = default;
    }
}
