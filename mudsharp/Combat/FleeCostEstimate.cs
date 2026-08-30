namespace MudSharp.Combat;

/// <summary>
/// A rough points figure for fleeing right now, for the Combat Rail's flee pill.
///
/// <para><b>Read the accuracy section before trusting or extending this.</b> It is an interpolation
/// between four anchors, exactly one of which is a measurement.</para>
///
/// <para><b>Why it exists at all</b>, given that <c>COMBAT-RAIL-SPEC.md</c> section 10 bans flee cost
/// figures and <c>FleeCostLadder</c> - the class that once computed one - was deleted outright. The ban
/// was narrowed twice by the owner, both times because a model had generalised his objection past what
/// he said. He objected to a half-rail gauge that framed reaching the free-flee band as an OBJECTIVE;
/// then, in play, he asked for the number inline on the pill. What stays banned is the gauge, and any
/// rendering that presents the cheap band as a goal or a safe place. A parenthetical beside a stamina
/// reading on a control that says GO is not that.</para>
/// </summary>
public static class FleeCostEstimate
{
    /// <summary>
    /// Fraction of current score lost, by stamina. Four anchors, and their evidence is not equal:
    ///
    /// <list type="bullet">
    /// <item><b>>= 20 stamina -> 10%.</b> The owner's stated maximum loss, flat all the way down to the
    /// survival threshold. Not measured here; no flee at high stamina exists in the corpus.</item>
    /// <item><b>19 stamina -> 4.48%.</b> The ONE measurement: score 46,416 -> 44,337, exactly -2,079 at
    /// 19/105 stamina. n=1.</item>
    /// <item><b>7 stamina -> ~1.2%.</b> The owner's estimate of 500-600 points on a ~46,000 score.
    /// Recollection, not a capture.</item>
    /// <item><b><= 6 stamina -> 0.</b> The owner's free-flee band.</item>
    /// </list>
    ///
    /// <para><b>The cliff between 19 and 20 is real and is the interesting part of the curve</b> -
    /// fleeing at 20 costs more than twice what fleeing at 19 costs, so the game charges the maximum at
    /// exactly the moment holding on is most likely to kill you. It is not smoothed here, because
    /// smoothing it would erase the one feature of the shape anybody has actually observed.</para>
    ///
    /// <para><b>Everything between 7 and 19 is straight-line interpolation between two points, one of
    /// which is a memory.</b> The owner says only that it "drops quickly" below 20. Linear is the least
    /// invented shape that honours both anchors; it is not a claim that the curve is linear.</para>
    ///
    /// <para><b>Known unresolved, and it could invalidate the whole function for other characters.</b>
    /// The published guide states these bands as PERCENTAGES OF MAXIMUM stamina; the owner's thresholds
    /// are ABSOLUTE. On a 105-max character the two nearly coincide, which is why nothing here can tell
    /// them apart, and they diverge sharply for anyone else - a 30-max character would flee free below 6
    /// on this reading and below 3 on the guide's. `verify_mechanics.py` reports flee cost as
    /// INSUFFICIENT and is right to. See MUD2-PUBLISHED-MECHANICS.md section 6, which also shows that no
    /// single base rate satisfies both the guide's modifier ladder and our one measurement, in either
    /// direction - so the guide is the weaker source rather than the tie-breaker.</para>
    ///
    /// <para><b>Consequently this is deliberately displayed coarsely</b> (see <see cref="Format"/>): to
    /// three significant figures at most, and to two above 5,000. A figure rendered to the point would
    /// claim a precision that four anchors cannot support.</para>
    /// </summary>
    private const double MaxFraction = 0.10;      // >= 20 stamina
    private const double At19Fraction = 0.0448;   // measured, n=1
    private const double At7Fraction = 0.012;     // owner's estimate

    /// <summary>Stamina at or below which fleeing is free - the owner's band, and the same number
    /// <see cref="CombatTierResolver.CriticalStaminaThreshold"/> holds for the danger reading of that
    /// band. Referenced rather than restated so the pill's loudest state and the disappearance of its
    /// price tag cannot drift apart: they are two consequences of one boundary in the game.</summary>
    private const double FreeBelow = CombatTierResolver.CriticalStaminaThreshold;

    /// <summary>Where the owner's "small loss" anchor sits.</summary>
    private const double LowAnchor = 7.0;

    /// <summary>
    /// Estimated points lost by fleeing at <paramref name="stamina"/> with <paramref name="score"/>
    /// points, or null when either input is unknown or the flee is free.
    ///
    /// <para>Null rather than 0 for the free band, deliberately: the pill renders no parenthetical for
    /// null, and "no price shown" has to mean one thing. A displayed <c>(-0)</c> would be a claim, and a
    /// zero conflated with "we do not know your score" would be a lie in the direction that gets a
    /// character killed.</para>
    /// </summary>
    public static int? Points(int? stamina, int? score)
    {
        if (stamina is not int sta || score is not int total || total <= 0)
            return null;
        if (sta <= FreeBelow)
            return null;

        var fraction = sta switch
        {
            >= 20 => MaxFraction,
            // The cliff: 19 is the measurement, 20 is the flat maximum, and nothing in between exists.
            19 => At19Fraction,
            <= 7 => At7Fraction,
            _ => At7Fraction + ((At19Fraction - At7Fraction) * ((sta - LowAnchor) / (19.0 - LowAnchor))),
        };

        var points = (int)Math.Round(total * fraction, MidpointRounding.AwayFromZero);
        // A flee inside the paying band always costs something; rounding a small score to nothing would
        // read as the free band, which is a different fact about the game.
        return Math.Max(points, 1);
    }

    /// <summary>
    /// The owner's display format: bare points under 1,000; one decimal and a <c>k</c> under 5,000;
    /// whole thousands above that. Coarse on purpose - the estimate behind it rests on four anchors and
    /// one measurement, and a figure printed to the point would dress that up as arithmetic.
    /// </summary>
    public static string Format(int points)
    {
        var c = System.Globalization.CultureInfo.InvariantCulture;
        if (points < 1000)
            return points.ToString(c);
        if (points < 5000)
            return (points / 1000.0).ToString("0.0", c) + "k";
        return (points / 1000).ToString(c) + "k";
    }
}
