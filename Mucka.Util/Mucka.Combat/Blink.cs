namespace Mucka.Combat;

/// <summary>
/// <b>The client's blink and pulse doctrine: what "blink" and "pulse" mean in this client, and the
/// only place either period is written down.</b>
///
/// <para><b>Two mechanisms, deliberately not one.</b> They look similar and they are not
/// interchangeable:</para>
/// <list type="bullet">
/// <item><b>PULSE</b> - a smooth opacity ramp on a WinUI Composition layer behind the Skia canvas
/// (<see cref="PulseLayer"/>). Runs on the compositor, costs the UI thread nothing, and can be as
/// fast and as smooth as it likes. Used for whole-element alarms: the panel glow, the flee pill.</item>
/// <item><b>BLINK</b> - a hard alternation between two appearances, drawn by the canvas itself.
/// Two states, no fade. Used where the thing that must alarm is a few characters of text inside a
/// canvas, which a Composition layer can only reach by duplicating that text's geometry - and every
/// such duplicate in this codebase has drifted out of sync at least once.</item>
/// </list>
///
/// <para><b>Why blink is allowed to touch the canvas at all, given Invariant #1.</b> It adds no
/// timer. The phase is a pure function of the wall clock, sampled at paint time, and the repaint that
/// makes it visible is one the panel already performs: SidePanelViewModel's 1 Hz flush
/// (TickCombatDisplay), which runs unconditionally through a fight so the duration clocks can
/// advance. A blinking element publishes its phase as frame state, so it repaints once a second
/// WHILE BLINKING and not at all otherwise. Nothing here may grow into its own ticker - that is the
/// line Invariant #1 actually draws.</para>
///
/// <para><b>The periods are harmonically related on purpose.</b> The pulse is 1200 ms and the blink
/// 2000 ms, and neither is arbitrary: every pulsing element shares one period
/// (see <see cref="PulseLayer.PeriodMilliseconds"/>, whose remarks record that several elements on
/// their own phases read as noise rather than as urgency), and the blink period is exactly ONE
/// COMBAT TICK - MUD2 resolves combat every 2000 ms, so a combat alarm beats with the thing that is
/// actually hitting you. The 1 Hz flush samples that at precisely twice per cycle, which is what
/// makes it a clean second on, second off rather than a stutter.</para>
///
/// <para><b>Blink is the ESCALATION, not a third colour.</b> Where a readout already has a scale of
/// tones, blinking is what the top of that scale does - so a ladder does not need "bad", "worse" and
/// "worst" hues as well. And a blinking element INVERTS rather than merely changing hue: the alarm
/// state swaps foreground and ground, because at two characters wide a hue change is not a signal
/// and a reversed block is.</para>
/// </summary>
public static class Blink
{
    /// <summary>The pulse period, in milliseconds - the compositor's, shared by every pulsing
    /// element. See the class remarks for why one period is not negotiable.</summary>
    public const double PulsePeriodMilliseconds = 1200.0;

    /// <summary>The blink period, in milliseconds: one MUD2 combat tick. Measured, not chosen - see
    /// Mucka.Combat.CombatTiming, which owns that measurement; this restates the figure rather than
    /// referencing it only because Rendering must not depend on Core for a constant.</summary>
    public const double BlinkPeriodMilliseconds = 2000.0;

    /// <summary>
    /// Which half of the blink cycle <paramref name="nowUtc"/> falls in - true for the "on" half.
    ///
    /// <para>Off the wall clock rather than off a counter, so every blinking element in the client
    /// agrees on the phase without anything having to co-ordinate them, and so a repaint that arrives
    /// late shows the phase it is late INTO rather than resuming a sequence.</para>
    /// </summary>
    /// <param name="inverted">Blink in antiphase. For a second element that must stay distinguishable
    /// from a first one blinking beside it - two alarms in phase read as one flashing region.</param>
    public static bool PhaseOn(DateTime nowUtc, bool inverted = false)
    {
        var period = (long)BlinkPeriodMilliseconds;
        // Unix ms rather than DateTime.Ticks/10000 so the phase is stable across a process restart and
        // has no dependence on the epoch a DateTime happens to carry.
        var ms = new DateTimeOffset(nowUtc.ToUniversalTime()).ToUnixTimeMilliseconds();
        var on = ((ms / (period / 2)) & 1L) == 0L;
        return inverted ? !on : on;
    }
}
