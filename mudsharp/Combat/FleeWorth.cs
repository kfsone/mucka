namespace MudSharp.Combat;

/// <summary>
/// What leaving a fight costs, from MUD2's own arithmetic.
///
/// <para><b>This is Bartle's formula, supplied by him directly</b> (email to the owner, 2026-09-10),
/// with his caveat: "I haven't checked any of this by plugging in numbers, so it could well be wrong".
/// It is the rule, not a fit; the corpus is used only to confirm it. That is why this class may exist
/// where the estimator before it was deleted: the pill prints a price when the price is known and
/// nothing when it is not.</para>
///
/// <code>
///   pct       = 100 * stamina / maxStamina
///   fleeworth = 0                 when pct &lt; 6
///             = worth * 4 / d     otherwise
///                 d = 1 (pct 100) | 2 (76..99) | 4 (16..75) | 8 (&lt;16)
///   worth     = 75 + (points + bounties) / 5
///   cost      = 2/9 * fleeworth
/// </code>
///
/// <para><b>Evidence</b>, all from <c>~/.mucka/combat/mucka.db</c>. <c>score_events</c> (the signed
/// fall; <c>total - delta</c> is the score before it) began on 2026-09-05; before that the only
/// instrument is a fight's own <c>score_at_start</c>/<c>score_at_end</c>, which is exact when nothing
/// else moved the score during the fight and noise otherwise.</para>
/// <list type="bullet">
/// <item><b>Every flight with a recorded score fall replays exactly</b> -
/// <c>FleeWorthTests.RecordedFlights</c>, bands d=1, 4 and 8. Band d=2 has one sample, from before
/// <c>score_events</c>: fight 631, 92/115 stamina, 74,824 to 68,140 = 6,684 exactly.</item>
/// <item><b>The threshold is a PERCENTAGE:</b> fight 2162, stamina 6 of 82 (7.3%), was charged 26;
/// fight 1126, a failed flee at 6 of 120 (5.0%), left the score at 93,823. Same absolute 6.</item>
/// <item><b>A free flight is SILENT</b> - no <c>-0</c> line. Only one sub-6% flight falls inside the
/// <c>score_events</c> era (fight 2160, 2 of 82) and it produced no score event; the pre-era sub-6%
/// flights all had rises during the fight, so they say nothing either way.</item>
/// <item><b>Strict at 6:</b> Bartle's wording is "&lt; 6". The corpus has one flee at exactly 6.0%
/// (fight 112, pre-era) and its score did fall, but by an amount the fight-start score cannot
/// confirm, so this rests on his wording.</item>
/// <item><b>Truncated, not rounded:</b> every recorded cost is the floor (573.4 to 573, 285.7 to 285).
/// Written in floating point; every sample also reproduces under integer division at each step, so
/// the corpus does not say which MUD2 does.</item>
/// </list>
///
/// <para>Bounties are out of scope (owner, 2026-09-10) and the term is dropped; a bountied persona is
/// under-priced by exactly <c>2/9 * 4/d * bounty/5</c>.</para>
/// </summary>
public static class FleeWorth
{
    /// <summary>Below this percentage of maximum stamina a flight is free. Strict: 6.0% is charged.</summary>
    public const double FreeBelowPercent = 6.0;

    /// <summary>The divisor for a stamina percentage - the four bands, verbatim.</summary>
    public static int Divisor(double percent) => percent switch
    {
        >= 100 => 1,
        >= 76 => 2,
        >= 16 => 4,
        _ => 8,
    };

    /// <summary>
    /// What it would cost <paramref name="points"/> to leave right now, or null when an input is
    /// missing. Null is not zero: zero means MUD2 will let this flight go free, which is worth printing;
    /// null means we do not know, which must print nothing.
    /// </summary>
    /// <param name="points">Score BEFORE the deduction - the charge is printed ahead of the flee
    /// line, so this is simply the current score.</param>
    /// <param name="stamina">Current stamina.</param>
    /// <param name="maxStamina">Maximum stamina - MUD2's maximum, which a potion can raise past 100.</param>
    public static int? Cost(int? points, int? stamina, int? maxStamina)
    {
        if (points is not int p || p < 0 || stamina is not int s || maxStamina is not int m || m <= 0)
            return null;

        var percent = 100.0 * s / m;
        if (percent < FreeBelowPercent)
            return 0;

        var worth = 75 + (p / 5.0);
        var fleeworth = worth * 4.0 / Divisor(percent);
        return (int)(2.0 / 9.0 * fleeworth);   // floor - see the class
    }
}
