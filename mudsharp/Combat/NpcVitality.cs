namespace MudSharp.Combat;

/// <summary>What the vitality reading is standing on. Flags, not a rank: "the rung, narrowed by a
/// probe" is a different claim from "the rung, narrowed by arithmetic on a species average".</summary>
[Flags]
public enum VitalityBasis
{
    None = 0,

    /// <summary>MUD2's own wound descriptor. The primary source and the only one the PLAYER can also
    /// see, because it is the only one the game prints.</summary>
    Rung = 1,

    /// <summary>The species pool estimate and this fight's damage brackets narrowed the position
    /// inside the rung's seventh. Under the hood entirely - no absolute figure it produced ever
    /// reaches the screen.</summary>
    Narrowed = 2,

    /// <summary>A <c>diagnose</c> probe contributed. The only direct measurement MUD2 offers, so the
    /// renderer is allowed to draw the resulting edge harder than an inferred one.</summary>
    Diagnose = 4,
}

/// <summary>
/// A rung boundary the creature was driven across, and the blow that did it.
///
/// <para><b>Why a crossing is worth more than a reading.</b> A descriptor says which seventh the
/// creature is in - a whole rung wide, and on a 280-stamina giant that is 40 stamina of ignorance. A
/// CROSSING says it just passed a specific line, and the blow that pushed it over bounds how far past:
/// land a 1-4 and it is now within 4 stamina of that line; land a 20-29 and it could be anywhere in a
/// 29-wide window.</para>
///
/// <para>So many small blows resolve the ladder far better than few large ones do, at the SAME total
/// damage. That is the mechanism by which the reading sharpens with familiarity, and it costs nothing
/// but noticing which blow the descriptor followed.</para>
///
/// <para><b>What this deliberately does not do is bound the POOL.</b> A crossing also implies
/// <c>pool &lt;= 7 x dealt / (7 - k)</c>, and that upper bound is exactly the one
/// <see cref="StaminaPoolEstimator"/> refuses: a creature that arrived pre-damaged has taken more than
/// the client saw, so every dealt figure understates and a ceiling built from one is too low. The
/// crossing narrows where the creature is on ITS OWN ladder, which is what the seal draws, and leaves
/// the species pool to the constraint families that have no model in them.</para>
/// </summary>
/// <param name="Rung">The rung it dropped TO. The boundary crossed is the top of that rung, i.e.
/// <c>Rung / 7</c> of full.</param>
/// <param name="DamageSinceReading">Everything the player dealt between the previous descriptor and
/// this one, as a bracket. Deliberately the whole span rather than the last blow: attributing a drop to
/// a single blow is only sound if no landed blow in between went unreported, and understating the
/// damage makes the pool ceiling it implies too TIGHT - the unsafe direction, because it would make the
/// creature read as smaller and the fight as easier to win than the evidence supports. See
/// <c>FightAccumulator.NoteHealth</c>, and <c>StaminaPoolEstimator.MultiRungCeiling</c>, which takes
/// the same form over the corpus.</param>
/// <param name="DealtSince">Cumulative bracket dealt after the crossing, so the reading can be aged
/// forward instead of going stale.</param>
/// <param name="RungsDropped">How many rung boundaries the span carried it across. 1 is the ordinary
/// case and localises the creature; 2 or more additionally CEILINGS its pool, because damage of known
/// size was worth that many rung-widths and a rung is <c>pool/7</c> wide. See
/// <see cref="NpcVitality.PoolCeiling"/>, and <c>StaminaPoolEstimator.MultiRungCeiling</c> for the
/// derivation and for why pre-damage cannot corrupt it.</param>
public readonly record struct NpcRungCrossing(
    int Rung, DamageBracket DamageSinceReading, DamageBracket DealtSince, int RungsDropped = 1);

/// <summary>
/// How much of a creature is left, as a FRACTION of its own full health, in a band.
/// </summary>
/// <param name="Low">Lower bound, 0..1.</param>
/// <param name="High">Upper bound, 0..1, never below <paramref name="Low"/>.</param>
/// <param name="Basis">Which constraints contributed.</param>
public readonly record struct VitalityBand(double Low, double High, VitalityBasis Basis)
{
    /// <summary>How many of MUD2's seven rung steps - the player's "bubbles" - the band spans. The
    /// unit the whole readout is expressed in, so it is defined once here.</summary>
    public double StepsWide => (High - Low) * NpcHealthRungs.Rungs;
}

/// <summary>
/// Turns what MUD2 has SAID about a creature into a fraction of its own full health.
///
/// <para><b>A LADDER POSITION, not a stamina figure.</b> Every creature has the same seven rungs; a
/// bigger creature does not get more of them, its rungs are simply worth more stamina and it labels
/// them with different words. So this returns where on that ladder the creature stands, as a fraction
/// of its own full, and every absolute figure the estimator produces stays under the hood - the
/// player is never asked to do math.</para>
///
/// <para><b>The rung is the source; the estimator only narrows it.</b> A descriptor puts the creature
/// inside one seventh and nothing else is needed to draw that. What <see cref="StaminaPoolEstimator"/>
/// and <see cref="NpcRemainingStamina"/> add is POSITION INSIDE that seventh: they know how much damage
/// has landed since the descriptor printed, and dividing it by the species pool turns it into a
/// fraction the rung band can be intersected with. Subtle and internal - it makes the fill sit better
/// than seven discrete steps could, and it is not allowed to do anything else. When there is no pool on
/// file the seventh stands on its own, which is exactly the state MUD2 leaves an unfamiliar creature
/// in.</para>
///
/// <para><b>The intersection is what makes this safe.</b> Dividing one interval by another widens it,
/// so the estimator's fraction is usually LOOSER than the rung's seventh and the intersection simply
/// keeps the seventh - no harm done. It is tighter exactly when it has something the rung does not:
/// damage landed since the reading, or a <c>diagnose</c> probe. Narrowing therefore happens only where
/// it is earned.</para>
///
/// <para><b>Regenerating creatures read low, and that is the safe direction.</b> For a species whose
/// estimate is <see cref="PoolQuantity.DamageToKill"/> (zombies, measured) the denominator exceeds the
/// real pool, so the fraction understates how much is left - the fight looks longer than it is. The
/// alternative error would be a panel telling the player a zombie is nearly finished.</para>
///
/// <para>Pure and MAUI-free; exercised directly by mudsharp.Tests.</para>
/// </summary>
public static class NpcVitality
{
    /// <summary>
    /// This creature's remaining fraction, or null when the game has said nothing that supports one.
    /// </summary>
    /// <param name="rung">The latest wound descriptor, 1..7, or null when none has printed. Null does
    /// not mean the game is being stingy: a descriptor follows every landed blow that does not kill
    /// (3,559 against 3,561 such hits over 1,197 fights, instrumented window from 2026-08-11). It means
    /// the player has not landed on this creature yet, which in a pack is the ordinary state of every
    /// row but one.</param>
    /// <param name="pool">The species estimate. Used only as a denominator; never shown.</param>
    /// <param name="remaining">The absolute remaining band from <see cref="NpcRemainingStamina"/>.
    /// Used only as a numerator; never shown.</param>
    /// <param name="crossing">The latest rung boundary this creature was driven across, if any. The
    /// tightest constraint available on a big creature - see <see cref="NpcRungCrossing"/>.</param>
    /// <param name="dealtThisFight">Everything dealt this fight. Its LOW end is a live floor under the
    /// pool, because the creature is still standing, and on a first encounter it is the only
    /// denominator a crossing has.</param>
    /// <param name="atMax">The descriptor was one of the at-max words - see
    /// <see cref="NpcHealthRungs.IsAtMax"/>. An exact equality, not a band, so it settles the whole
    /// estimate on its own and nothing may narrow it afterwards: there is nothing left to narrow.
    /// Only ql and examine can produce it, never a combat line, because combat describes a creature
    /// only after damage.</param>
    public static VitalityBand? Estimate(
        int? rung,
        StaminaPoolEstimate? pool,
        NpcStaminaBand? remaining,
        NpcRungCrossing? crossing = null,
        DamageBracket dealtThisFight = default,
        bool atMax = false)
    {
        double low, high;
        var basis = VitalityBasis.None;

        // cur == max, measured 328/328 and agreed by the protocol (the C1 code on the stamina value is
        // 99.10 only and exactly at max). Taken before the rung so it cannot be widened back into a
        // seventh by the descriptor that carried it - "full of life" IS rung 7, and rung 7 alone would
        // say the creature might be a seventh down when the game has just said it is untouched.
        if (atMax)
            return new VitalityBand(1.0, 1.0, VitalityBasis.Rung);

        if (rung is int k && k >= 1 && k <= NpcHealthRungs.Rungs)
        {
            low = (k - 1) / (double)NpcHealthRungs.Rungs;
            high = k / (double)NpcHealthRungs.Rungs;
            basis = VitalityBasis.Rung;
        }
        else
        {
            low = 0.0;
            high = 1.0;
        }

        if (TryDerive(pool, remaining, out var derivedLow, out var derivedHigh))
        {
            var mergedLow = Math.Max(low, derivedLow);
            var mergedHigh = Math.Min(high, derivedHigh);
            if (mergedLow <= mergedHigh)
            {
                // Only count it as narrowing if it actually narrowed. A derived band wider than the
                // seventh contributes nothing and must not be advertised as evidence.
                if (mergedLow > low || mergedHigh < high)
                {
                    basis |= VitalityBasis.Narrowed;
                    if (remaining is { } r && (r.Basis & RemainingBasis.Diagnose) != 0)
                        basis |= VitalityBasis.Diagnose;
                }
                low = mergedLow;
                high = mergedHigh;
            }
            // An empty intersection means the species figure and this individual's descriptor
            // disagree, which a pre-damaged creature genuinely causes. The descriptor is the direct
            // observation of THIS creature, so it is the one that survives.
        }

        if (TryCross(crossing, pool, dealtThisFight, out var crossLow, out var crossHigh))
        {
            var mergedLow = Math.Max(low, crossLow);
            var mergedHigh = Math.Min(high, crossHigh);
            if (mergedLow <= mergedHigh)
            {
                if (mergedLow > low || mergedHigh < high)
                    basis |= VitalityBasis.Narrowed;
                low = mergedLow;
                high = mergedHigh;
            }
            // An empty intersection means the crossing and the descriptor disagree, which regeneration
            // between the two readings genuinely causes. The descriptor is the later statement about
            // this creature, so it is the one that survives - the same rule the pool disagreement takes.
        }

        if (basis == VitalityBasis.None)
            return null;

        return new VitalityBand(Math.Clamp(low, 0.0, 1.0), Math.Clamp(high, 0.0, 1.0), basis);
    }

    /// <summary>
    /// A live floor under the creature's whole pool: whatever the species estimate knows, or - on a
    /// first encounter, where it knows nothing - the fact that this creature is still standing after
    /// everything already dealt to it.
    ///
    /// <para>Shared with <see cref="DamagePrediction.AfterBlows"/> so the two consumers cannot drift
    /// about what a floor is. The still-standing half is the same constraint family
    /// <see cref="StaminaPoolEstimator"/>'s kill bound uses, and it is the only denominator available
    /// the first time a creature is met.</para>
    /// </summary>
    public static double PoolFloor(StaminaPoolEstimate? pool, DamageBracket dealtThisFight)
        => Math.Max(pool?.Interval.Above ?? 0.0, dealtThisFight.Low);

    /// <summary>
    /// A live CEILING over the creature's whole pool: whatever the species estimate closed it at, and -
    /// if a stretch of this fight drove it down two or more rungs at once - what that stretch implies.
    ///
    /// <para>The mirror of <see cref="PoolFloor"/>, and the live half of the constraint
    /// <c>StaminaPoolEstimator.MultiRungCeiling</c> applies to the corpus - the same form, deliberately,
    /// so the two cannot disagree about what a ceiling is. Damage of at most <c>hi</c> that was worth N
    /// rung-widths means <c>pool &lt; 7 x hi / (N-1)</c> for N of 2 or more: damage that crosses several
    /// boundaries proves the creature is SMALL, where the same damage crossing none only proves it is
    /// big. It is available from the very first fight, before anything has been folded into the corpus,
    /// and it is immune to the creature having arrived pre-damaged because a rung is <c>pool/7</c> wide
    /// however hurt it already was.</para>
    ///
    /// <para>A split species contributes no ceiling of its own - its hull spans a gap nothing supports
    /// - but a crossing observed against it still does, because a crossing is an observation of the
    /// individual in front of the player rather than a species figure.</para>
    /// </summary>
    public static double? PoolCeiling(StaminaPoolEstimate? pool, NpcRungCrossing? crossing)
    {
        double? ceiling = pool is { Evidence: PoolEvidence.Band or PoolEvidence.LowerBoundOnly }
            ? pool.Interval.AtMost
            : null;

        if (crossing is { RungsDropped: >= 2 } cross && cross.DamageSinceReading.High > 0)
        {
            var implied = cross.DamageSinceReading.High * NpcHealthRungs.Rungs / (double)(cross.RungsDropped - 1);
            if (ceiling is null || implied < ceiling)
                ceiling = implied;
        }

        return ceiling;
    }

    /// <summary>
    /// The band a boundary crossing implies, aged forward by whatever has landed since.
    ///
    /// <para>Right after crossing into rung k the creature is at or just below <c>k/7</c> of full, and
    /// no further below than the damage since the previous reading could have carried it:
    /// <c>(k/7 - spanHigh/poolFloor, k/7]</c>. Using the span rather than the last blow only widens
    /// this band, which is the safe direction here as well - the blow that actually crossed the line is
    /// part of the span, so the bound still holds. Dividing by the FLOOR gives the widest fractional
    /// loss, likewise safe.</para>
    ///
    /// <para>Ageing subtracts the largest possible fractional loss from the bottom and the smallest
    /// from the top. With no pool ceiling the smallest possible fractional loss is zero, so the top
    /// does not move - the honest consequence of not knowing how big the creature is.</para>
    /// </summary>
    private static bool TryCross(
        NpcRungCrossing? crossing, StaminaPoolEstimate? pool, DamageBracket dealtThisFight,
        out double low, out double high)
    {
        low = 0.0;
        high = 1.0;

        if (crossing is not { } cross || cross.Rung < 1 || cross.Rung > NpcHealthRungs.Rungs)
            return false;

        var floor = PoolFloor(pool, dealtThisFight);
        if (floor <= 0)
            return false;

        high = cross.Rung / (double)NpcHealthRungs.Rungs;
        low = high - (cross.DamageSinceReading.High / floor);

        low -= cross.DealtSince.High / floor;
        if (PoolCeiling(pool, crossing) is double ceiling && ceiling > 0)
            high -= cross.DealtSince.Low / ceiling;

        // Validate BEFORE clamping into [0,1], not after: clamping first can turn a genuinely
        // inverted (high < low) result into a degenerate [0,0] that passes the check by accident -
        // e.g. high=-0.2, low=0 is invalid pre-clamp but both round to 0 post-clamp, which
        // satisfies high>=low despite the underlying arithmetic having contradicted itself (see
        // NpcRemainingStamina.Compute's own remarks on the same failure shape). A contradiction
        // must surface as no contribution from this constraint, never as a confident zero.
        if (high < low)
            return false;

        low = Math.Clamp(low, 0.0, 1.0);
        high = Math.Clamp(high, 0.0, 1.0);
        return true;
    }

    /// <summary>
    /// The fraction implied by dividing the absolute remaining band by the absolute pool band. Interval
    /// division for positive quantities: the smallest fraction is the least remaining over the largest
    /// pool, the largest is the most remaining over the smallest pool.
    /// </summary>
    private static bool TryDerive(
        StaminaPoolEstimate? pool, NpcStaminaBand? remaining, out double low, out double high)
    {
        low = 0.0;
        high = 1.0;

        if (pool is not { } p || !p.HasEvidence || remaining is not { } r || !r.HasEvidence)
            return false;

        // A split species is two disjoint pools and no way to choose, so it is no denominator at all -
        // dividing by the hull would produce a fraction supported by nothing in the gap. The rung's own
        // seventh stands alone there, which is the honest reading.
        if (p.Evidence == PoolEvidence.Split)
            return false;

        if (p.Interval.AtMost is double poolTop && poolTop > 0)
            low = Math.Max(0.0, r.Interval.Above) / poolTop;

        // pool.Above is EXCLUSIVE and can be 0 ("no lower bound worth stating"), which would divide by
        // zero and, worse, claim an unbounded fraction as a number. Left at 1.0 there.
        if (r.Interval.AtMost is double top && p.Interval.Above > 0)
            high = top / p.Interval.Above;

        // Validate BEFORE clamping into [0,1], not after - see TryCross's own remarks on why
        // clamping first can mask a genuinely inverted (high < low) result as a degenerate [0,0]
        // that passes the check by accident. This is precisely how a contradictory `remaining` band
        // (Above clamped to 0 by NpcRemainingStamina.Compute but AtMost left negative - the bug that
        // method's own IsEmpty guard now catches at the source) would otherwise have been accepted
        // here too as a confident empty reading.
        if (high < low)
            return false;

        low = Math.Clamp(low, 0.0, 1.0);
        high = Math.Clamp(high, 0.0, 1.0);
        return true;
    }
}
