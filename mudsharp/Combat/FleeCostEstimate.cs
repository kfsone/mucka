namespace MudSharp.Combat;

/// <summary>Which measured band a flee falls in. Two of the five are absences and neither is
/// zero-priced: <see cref="Unknown"/> is "we were not told enough to say" and
/// <see cref="AboveEvidence"/> is "nobody has ever fled from here".</summary>
public enum FleeCostBand
{
    /// <summary>Stamina, max stamina or score missing. Nothing can be said, including that it is
    /// free.</summary>
    Unknown,

    /// <summary>Below the free band's ceiling. MUD2 charges nothing. This is the ONLY band that means
    /// "leaving is free", and it must never share a rendering with
    /// <see cref="AboveEvidence"/>.</summary>
    Free,

    /// <summary>The modal band: about 2.25% of score.</summary>
    Cheap,

    /// <summary>The doubled band: about 4.5% of score.</summary>
    Dear,

    /// <summary>
    /// Above the highest fraction of maximum stamina at which any flee has been measured. There IS a
    /// price - fleeing from a healthy fight has never once been free - but nothing measures it, so no
    /// figure can be quoted.
    ///
    /// <para><b>This must not render as silence.</b> Silence is what <see cref="Free"/> renders as, and
    /// a player who reads "no price shown" as "costs nothing" stays in a fight that may cost several
    /// percent of their score. The two states are opposite claims about the same blank space. Use
    /// <see cref="FleeCostEstimate.Parenthetical"/>, which returns
    /// <see cref="FleeCostEstimate.UnmeasuredMarker"/> here and null for Free, rather than deciding
    /// from <see cref="FleeCostEstimate.Points"/> alone - that method returns null for both.</para>
    ///
    /// <para>How much of the bar this covers depends on the character, and on a weak one it is most of
    /// it: the evidence tops out at 18.1% of maximum, which on a 30-maximum novice is about 5 stamina.
    /// So this is the ordinary state for a healthy player, not an edge case.</para>
    /// </summary>
    AboveEvidence,
}

/// <summary>
/// What fleeing costs, as a fraction of the player's score, banded by stamina AS A FRACTION OF
/// MAXIMUM.
///
/// <para><b>The fraction is the finding, and it replaces an absolute model that was wrong.</b> Over 40
/// flee events with score captured before and after, with zero misordering: free below about 6% of
/// maximum stamina; about 2.0-2.4% of score between 6.7% and 15.2%; about 4.1-5.0% between 16% and
/// 18%. A second, independent pass over 38 of those events found the modal charge to be 2.25% of score
/// on 36 of them, constant from a score of 7,873 to a score of 88,308, across two personas and
/// experience levels 5 to 9, with the remaining cluster at exactly twice that.</para>
///
/// <para><b>What was deleted, and why it is not sitting beside this.</b> The previous model was a
/// four-anchor interpolation on ABSOLUTE stamina: 10% at 20 or above, 4.48% at 19, about 1.2% at 7,
/// free at 6 or below. Three of those four anchors were the owner's recollection rather than
/// measurement, and the free boundary was <c>CombatTierResolver.CriticalStaminaThreshold</c> - the
/// constant 6.5. That number is now identifiable: 6.5 out of a 105-maximum persona is 6.2% of maximum,
/// i.e. the free-flee boundary as it lands on the one character it was ever observed on. It was a
/// fraction laundered into an absolute, and it read correctly for exactly one persona. The one
/// genuine measurement in the old model survives the change and lands inside the new bands: 19 out of
/// 105 is 18.1% of maximum, and its 4.48% sits inside the 4.1-5.0% band. The 1.2%-at-7 anchor does
/// not survive - 7 out of 105 is 6.7% of maximum, where 40 events say the charge is 2.0-2.4%. That
/// anchor was a memory and the measurement supersedes it.</para>
///
/// <para><b>The boundaries fall in the gaps between observations, and each is placed in the direction
/// that cannot mislead.</b> Free is stated at 6% and the cheapest paying observation is at 6.7%, so
/// the 6%-6.7% gap is priced as PAYING - telling a player a flee is free when it might not be is the
/// error that costs points unexpectedly. The cheap band's observations end at 15.2% and the dear
/// band's begin at 16%, so the gap is priced as DEAR, for the same reason. Neither gap is split down
/// the middle: a midpoint would be an invented boundary, where an edge is a stated preference.</para>
///
/// <para><b>Above 18.1% of maximum there is no price at all, and that is deliberate.</b> The old model
/// claimed a flat 10% ceiling from 20 stamina upward and said so itself: "not measured here; no flee
/// at high stamina exists in the corpus." It still does not. A flee from full health may cost 10%, or
/// 4.5%, or something the doubling pattern suggests; nothing distinguishes them, so the pill shows no
/// parenthetical up there rather than a confident wrong one.</para>
///
/// <para><b>This computes a price and nothing else.</b> <c>COMBAT-RAIL-SPEC.md</c> section 10 bans the
/// half-rail gauge that framed reaching the free band as an objective, and that ban stands; a
/// parenthetical beside a control that says GO is not that. See <see cref="FleePillResolver"/>, which
/// owns how loudly the pill is drawn and does not consult this at all.</para>
/// </summary>
public static class FleeCostEstimate
{
    /// <summary>Stamina as a fraction of maximum below which fleeing is free. The owner's band,
    /// measured at about 6% across 40 events.</summary>
    public const double FreeBelowFractionOfMax = 0.06;

    /// <summary>Top of the cheap band, and the highest fraction of maximum at which the 2.0-2.4%
    /// charge was observed. Above this the dear band is charged - see the class remarks on why the gap
    /// to 16% is priced upward.</summary>
    public const double CheapBandTopFractionOfMax = 0.152;

    /// <summary>
    /// Top of the dear band, and the highest fraction of maximum at which ANY flee has been measured:
    /// 19 stamina on a 105-maximum persona, the single event with an exact score delta on both sides
    /// (46,416 -&gt; 44,337, exactly -2,079). Written as the division rather than as 0.181 so the
    /// provenance cannot be rounded away.
    /// </summary>
    public const double DearBandTopFractionOfMax = 19.0 / 105.0;

    /// <summary>Modal charge in the cheap band: 2.25% of score, on 36 of 38 events.</summary>
    public const double CheapFractionOfScore = 0.0225;

    /// <summary>The dear band's charge, exactly twice the cheap one. The second cluster in the same 38
    /// events sits at 2x, and the 40-event pass reports 4.1-5.0% for this band, which contains
    /// it.</summary>
    public const double DearFractionOfScore = CheapFractionOfScore * 2;

    /// <summary>Observed spread of the cheap band, low end.</summary>
    public const double CheapFractionLow = 0.020;

    /// <summary>Observed spread of the cheap band, high end.</summary>
    public const double CheapFractionHigh = 0.024;

    /// <summary>Observed spread of the dear band, low end.</summary>
    public const double DearFractionLow = 0.041;

    /// <summary>Observed spread of the dear band, high end.</summary>
    public const double DearFractionHigh = 0.050;

    /// <summary>
    /// Which band this stamina falls in. Both stamina and its maximum are required: the whole
    /// correction here is that the boundary is a ratio, so a model that can be asked without a maximum
    /// is the bug this one replaces.
    /// </summary>
    public static FleeCostBand Band(int? stamina, int? staminaMax)
    {
        if (stamina is not int current || staminaMax is not int max || max <= 0 || current < 0)
            return FleeCostBand.Unknown;

        var fraction = current / (double)max;
        if (fraction < FreeBelowFractionOfMax)
            return FleeCostBand.Free;
        if (fraction <= CheapBandTopFractionOfMax)
            return FleeCostBand.Cheap;
        if (fraction <= DearBandTopFractionOfMax)
            return FleeCostBand.Dear;
        return FleeCostBand.AboveEvidence;
    }

    /// <summary>
    /// Estimated points lost by fleeing right now, or null when no figure can be quoted.
    ///
    /// <para><b>Null is three-way ambiguous and this method must not be used to decide whether to draw
    /// anything.</b> It returns null for missing inputs, for a genuinely free flee, and for a stamina
    /// above anything ever measured - and the last two are OPPOSITE claims. A caller that branches on
    /// <c>Points is int</c> alone renders "costs nothing" and "costs an unknown amount" as the same
    /// blank space, which is the one reading that gets a character killed. Use
    /// <see cref="Parenthetical"/>, or pair this with <see cref="Band"/>.</para>
    ///
    /// <para>Null rather than 0 for the free band, deliberately: a displayed <c>(-0)</c> is a claim,
    /// and a zero standing in for "we do not know your score" would be a lie in the same
    /// direction.</para>
    /// </summary>
    public static int? Points(int? stamina, int? staminaMax, int? score)
    {
        if (score is not int total || total <= 0)
            return null;

        var fraction = Band(stamina, staminaMax) switch
        {
            FleeCostBand.Cheap => CheapFractionOfScore,
            FleeCostBand.Dear => DearFractionOfScore,
            _ => (double?)null,
        };

        if (fraction is not double share)
            return null;

        var points = (int)Math.Round(total * share, MidpointRounding.AwayFromZero);
        // A flee inside a paying band always costs something; rounding a small score to nothing would
        // read as the free band, which is a different fact about the game.
        return Math.Max(points, 1);
    }

    /// <summary>
    /// The observed SPREAD of the charge in points, low to high, or null on the same three no-price
    /// conditions as <see cref="Points"/>. The pill shows the single figure; anything reporting on the
    /// model itself should show this instead, because the band is 2.0-2.4% and printing only the
    /// 2.25% mode hides that.
    /// </summary>
    public static (int Low, int High)? PointsRange(int? stamina, int? staminaMax, int? score)
    {
        if (score is not int total || total <= 0)
            return null;

        var (low, high) = Band(stamina, staminaMax) switch
        {
            FleeCostBand.Cheap => (CheapFractionLow, CheapFractionHigh),
            FleeCostBand.Dear => (DearFractionLow, DearFractionHigh),
            _ => (0.0, 0.0),
        };

        if (high <= 0)
            return null;

        return (
            Math.Max((int)Math.Round(total * low, MidpointRounding.AwayFromZero), 1),
            Math.Max((int)Math.Round(total * high, MidpointRounding.AwayFromZero), 1));
    }

    /// <summary>
    /// What belongs inside the pill's parenthetical, or null when the pill must show none.
    ///
    /// <para>The one function that maps the model onto what is drawn, so the mapping cannot be made
    /// twice and differently. Three outcomes, and the middle one is why this exists:</para>
    /// <list type="bullet">
    /// <item><see cref="FleeCostBand.Cheap"/>/<see cref="FleeCostBand.Dear"/> - the formatted figure,
    /// e.g. <c>"2.1k"</c>.</item>
    /// <item><see cref="FleeCostBand.AboveEvidence"/> - <see cref="UnmeasuredMarker"/>, so the pill
    /// reads <c>(-?)</c>: there is a price and we cannot name it.</item>
    /// <item><see cref="FleeCostBand.Free"/>/<see cref="FleeCostBand.Unknown"/> - null, draw
    /// nothing.</item>
    /// </list>
    ///
    /// <para>Taking the BAND rather than the raw stamina is deliberate: a caller cannot reach the label
    /// without having named which band it is in, so the Free-versus-AboveEvidence distinction is made
    /// at the call site by construction rather than being available to skip.</para>
    /// </summary>
    /// <param name="band">From <see cref="Band"/>.</param>
    /// <param name="points">From <see cref="Points"/>. Ignored for the bands that quote no figure.</param>
    public static string? Parenthetical(FleeCostBand band, int? points) => band switch
    {
        FleeCostBand.Cheap or FleeCostBand.Dear => points is int p && p > 0 ? Format(p) : UnmeasuredMarker,
        FleeCostBand.AboveEvidence => UnmeasuredMarker,
        _ => null,
    };

    /// <summary>
    /// Stands in for a figure that cannot be quoted, so that "we do not know what this costs" occupies
    /// the same slot a price would and cannot be mistaken for "this costs nothing".
    ///
    /// <para>A shape for ignorance, in other words. The absence of a marker is itself a claim on this
    /// panel - it is what the free band looks like - so an unmeasured price has to draw
    /// something.</para>
    /// </summary>
    public const string UnmeasuredMarker = "?";

    /// <summary>
    /// The owner's display format: bare points under 1,000; one decimal and a <c>k</c> under 5,000;
    /// whole thousands above that. Coarse on purpose - the charge is a modal figure inside a band that
    /// spans a fifth of its own value, and a figure printed to the point would dress that up as
    /// arithmetic.
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
