namespace MudSharp.Combat;

/// <summary>One row of the flee-cost ladder (DESIGN_FINAL.md section 5.3).</summary>
/// <param name="Label">"now" / "at 20 sta" / "at 6.5 sta" / "below 6.5 sta".</param>
/// <param name="IsAnchor">True for a KNOWN point (the two owner-given anchors, or the free floor).
/// False only for the "now" row when the player sits strictly between the two anchors - that
/// figure is an interpolated GUESS and must render with a `~` prefix, never at the same visual
/// weight as an anchor (D6/5.2).</param>
/// <param name="IsFree">True for the "below 6.5 sta" floor row - cost is 0%, rendered as the word
/// FREE rather than a percentage (4.5).</param>
/// <param name="CostFraction">Cost as a fraction of score (0.10 = 10%), null only when IsFree.</param>
/// <param name="CostPoints">Cost in score points for this row, given the score passed to
/// <see cref="FleeCostLadder.BuildLadder"/>. Null when score is unknown or IsFree.</param>
public readonly record struct FleeLadderRow(
    string Label, bool IsAnchor, bool IsFree, double? CostFraction, double? CostPoints);

/// <summary>
/// The flee-cost ladder: DESIGN_FINAL.md section 5. Three points are known from the owner, not
/// measured (D6): sta &gt; 20 costs 10% of score, sta = 6.5 costs 2.5%, sta &lt; 6.5 costs nothing.
/// The shape between 6.5 and 20 is an explicitly-labelled linear GUESS, never shown with the same
/// visual weight as the two anchors - this class is the single place that guess is computed, so it
/// can never silently drift into looking like measured data.
///
/// <para>Pure, primitive-typed, and MAUI-independent by design so it is directly unit-testable from
/// mudsharp.Tests via the existing ProjectReference (no test-project wiring needed).</para>
/// </summary>
public static class FleeCostLadder
{
    /// <summary>The lower anchor: at or below this stamina, fleeing is free (D6/5.1).</summary>
    public const double FreeThreshold = 6.5;

    /// <summary>The upper anchor: at or above this stamina, fleeing costs the flat 10% ceiling.</summary>
    public const double CeilingThreshold = 20.0;

    private const double CeilingFraction = 0.10;
    private const double AnchorFraction = 0.025;

    /// <summary>
    /// The cost fraction at a given stamina, per the 3-anchor interpolation (5.2):
    /// <c>10% when sta &gt; 20; 2.5% + (sta-6.5)/(20-6.5)*7.5% when 6.5 &lt;= sta &lt;= 20; 0% when sta &lt; 6.5</c>.
    /// </summary>
    /// <param name="isAnchor">True when <paramref name="stamina"/> sits exactly at a known point
    /// (at or above 20, at or below 6.5) - the caller must NOT render a `~` prefix for these; false
    /// when the value is the linear-interpolation GUESS between the two anchors, which the caller
    /// MUST prefix with `~` and never show at the same visual weight as an anchor (D6).</param>
    public static double CostFraction(double stamina, out bool isAnchor)
    {
        if (stamina >= CeilingThreshold)
        {
            isAnchor = true;
            return CeilingFraction;
        }
        if (stamina <= FreeThreshold)
        {
            isAnchor = true;
            return 0.0;
        }

        isAnchor = false;
        return AnchorFraction
            + (stamina - FreeThreshold) / (CeilingThreshold - FreeThreshold) * (CeilingFraction - AnchorFraction);
    }

    /// <summary>
    /// Builds the fixed 4-row ladder (5.3): "now" first (always, whatever the current stamina is),
    /// then the two known anchors, then the free floor. Never more than four rows - a fifth row of
    /// resolution would defeat the point of a decision aid (open question 5).
    /// </summary>
    /// <param name="currentStamina">The player's current stamina.</param>
    /// <param name="scoreTotal">Current total score, or null if not yet known - cost fractions are
    /// still returned, just with <see cref="FleeLadderRow.CostPoints"/> left null.</param>
    public static IReadOnlyList<FleeLadderRow> BuildLadder(double currentStamina, double? scoreTotal)
    {
        var nowFraction = CostFraction(currentStamina, out var nowIsAnchor);
        var nowIsFree = currentStamina < FreeThreshold;
        // "now" always renders FIRST (5.3) and always shows the true live figure - IsAnchor only
        // controls whether the caller must render it with a `~` prefix (false = interpolated guess).
        var rows = new List<FleeLadderRow>(4)
        {
            new("now", nowIsAnchor, nowIsFree,
                nowIsFree ? null : nowFraction,
                nowIsFree ? null : Points(nowFraction, scoreTotal)),
        };

        rows.Add(new FleeLadderRow("at 20 sta", true, false, CeilingFraction, Points(CeilingFraction, scoreTotal)));
        rows.Add(new FleeLadderRow("at 6.5 sta", true, false, AnchorFraction, Points(AnchorFraction, scoreTotal)));
        rows.Add(new FleeLadderRow("below 6.5 sta", true, true, null, null));
        return rows;
    }

    private static double? Points(double fraction, double? scoreTotal)
        => scoreTotal is double score ? fraction * score : null;

    /// <summary>Result of <see cref="HitsToNextBand"/> (5.4). <see cref="Suppressed"/> means render
    /// nothing - either the sample is too thin to trust, or the player is already at the free floor
    /// and there is no cheaper band to count down to.</summary>
    public readonly record struct NextBandResult(
        bool Suppressed, int Hits, double TargetStamina, bool CostChangesAtTarget);

    /// <summary>
    /// "How many more incoming hits until the flee cost drops to the next band" (5.4). Pure
    /// translation of the design's own pseudocode - deliberately never advice, never a coefficient,
    /// just the arithmetic.
    /// </summary>
    /// <param name="currentStamina">Current stamina.</param>
    /// <param name="incomingDamagePerHit">Average damage per landed opponent hit this fight.</param>
    /// <param name="opponentLandedHitsThisFight">Opponent's landed hits this fight - the thin-sample
    /// guard reuses <see cref="CombatOutlook.MinimumOwnHits"/> as its threshold (2), the same minimum
    /// CombatOutlook already gates its own projection on, rather than inventing a second number.</param>
    public static NextBandResult HitsToNextBand(
        double currentStamina, double? incomingDamagePerHit, int opponentLandedHitsThisFight)
    {
        if (opponentLandedHitsThisFight < CombatOutlook.MinimumOwnHits
            || incomingDamagePerHit is not double rate || rate <= 0)
            return new NextBandResult(true, 0, 0, false);

        if (currentStamina < FreeThreshold)
            // Already free - see 4.4/D7: never show a "next band" line here, it would reintroduce
            // cost-framing exactly where survival must be the only signal on screen.
            return new NextBandResult(true, 0, 0, false);

        // Branch 1 (> 20 sta): counting down to where the curve STARTS to fall - the cost does not
        // actually change yet (cost(20) == cost(20+epsilon) == 10%), so this is honest about there
        // being no saving until the NEXT band after that.
        // Branch 2 (6.5 <= sta <= 20): counting down to the free-threshold anchor, a real cheaper
        // number (2.5%).
        var targetStamina = currentStamina <= CeilingThreshold ? FreeThreshold : CeilingThreshold;
        var staminaToLose = currentStamina - targetStamina;
        var hits = (int)Math.Ceiling(staminaToLose / rate);
        // True for branch 2 only (target lands on the 2.5% anchor). Branch 1's target is the
        // ceiling itself, where the cost is unchanged (cost(20) == cost(20+epsilon) == 10%) - a
        // ">=" or "<=" comparison against CeilingThreshold here would wrongly call THAT a change too.
        var costChangesAtTarget = targetStamina == FreeThreshold;
        return new NextBandResult(false, Math.Max(hits, 0), targetStamina, costChangesAtTarget);
    }
}
