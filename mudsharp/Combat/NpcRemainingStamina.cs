namespace MudSharp.Combat;

/// <summary>Which constraints actually contributed to a remaining-stamina band. Flags rather than a
/// rank, because the whole point is being able to say "this is a diagnose reading aged by two blows"
/// as distinct from "this is arithmetic on a species average".</summary>
[Flags]
public enum RemainingBasis
{
    /// <summary>Nothing known. Render "--", never 0.</summary>
    None = 0,

    /// <summary>The species pool band, less the damage dealt so far this fight.</summary>
    Pool = 1,

    /// <summary>A live health-descriptor reading, as a fraction of the pool band.</summary>
    Rung = 2,

    /// <summary>A <c>diagnose</c> probe reading. The only direct measurement of the number.</summary>
    Diagnose = 4,
}

/// <summary>
/// A <c>diagnose</c> probe reading, exactly as MUD2 printed it, plus what has been dealt since.
///
/// <para>"The water-snake5 has a stamina lying between 90 and 99." The two numbers are stored as
/// PRINTED. Whether the game's bracket is aligned to tens, to a tenth of the pool, or to something
/// else entirely is unresolved at n=4 observations, so nothing here rounds, snaps or re-derives them -
/// see <c>CombatTracker.NpcStaminaRead</c>. Assuming an alignment would turn four observations into a
/// rule, which is the specific way this project's records have gone wrong before.</para>
/// </summary>
/// <param name="PrintedLow">The lower number in the game's sentence, inclusive.</param>
/// <param name="PrintedHigh">The upper number, inclusive.</param>
/// <param name="DealtSince">The player's cumulative damage bracket since the reading was printed.
/// <see cref="DamageBracket.Zero"/> when the reading is the latest thing that happened.</param>
public readonly record struct NpcStaminaReading(int PrintedLow, int PrintedHigh, DamageBracket DealtSince);

/// <summary>A live health-descriptor reading and the damage dealt since it was printed.</summary>
/// <param name="Rung">1 to <see cref="NpcHealthRungs.Rungs"/>.</param>
/// <param name="DealtSince">Cumulative damage bracket dealt after the reading.</param>
public readonly record struct NpcRungAnchor(int Rung, DamageBracket DealtSince);

/// <summary>
/// How much stamina a creature has left right now, as a band.
/// </summary>
/// <param name="Interval">The band, in absolute stamina - the same axis the player's own stamina is
/// drawn on, which is the point.</param>
/// <param name="Basis">Which constraints contributed.</param>
/// <param name="Quantity">Whether the underlying species figure is a pool or damage-to-kill (see
/// <see cref="PoolQuantity"/>). For a regenerating creature this band is "how much more damage it
/// should take", not "how much stamina it has", and must not be labelled as the latter.</param>
/// <param name="SupportingFights">Carried straight through from the pool estimate, so a caller can
/// tell a band built on ten fights from one built on one.</param>
/// <param name="Contradicted">True when two constraints disagreed and one had to be dropped. Not a
/// bug: a pre-damaged creature genuinely does read healthier than its pool band allows. It means the
/// band is resting on fewer legs than <see cref="Basis"/> suggests.</param>
/// <param name="Evidence">How well the SPECIES pool underneath this band is known, carried through
/// from <see cref="StaminaPoolEstimate.Evidence"/> so that a renderer holding only the band can still
/// tell an ordinary reading from a split one.
/// <para><see cref="PoolEvidence.Split"/> is the case to watch: the species evidence divided into two
/// disjoint bands, this band is derived from their hull, and drawing it as a solid range claims
/// support across a gap that has none. <see cref="PoolEvidence.None"/> alongside a
/// <see cref="RemainingBasis.Diagnose"/> basis is not a contradiction - it says the probe is carrying
/// this band on its own, which is the strongest state there is.</para></param>
public sealed record NpcStaminaBand(
    StaminaInterval Interval,
    RemainingBasis Basis,
    PoolQuantity Quantity,
    int SupportingFights,
    bool Contradicted,
    PoolEvidence Evidence)
{
    public static readonly NpcStaminaBand Unknown = new(
        StaminaInterval.Unbounded, RemainingBasis.None, PoolQuantity.StaminaPool, 0, false,
        PoolEvidence.None);

    public bool HasEvidence => Basis != RemainingBasis.None;

    /// <summary>True when the band is closed at the top and so can be drawn as a band rather than as
    /// an arrow running off the axis.</summary>
    public bool IsBounded => Interval.AtMost is not null;
}

/// <summary>
/// Turns what is known about a species into what is known about the creature in front of the player:
/// the pool band, less this fight's damage, narrowed by whatever the game has said about this
/// individual.
///
/// <para><b>Three constraints, and they are not equal.</b></para>
/// <list type="number">
/// <item><b>Pool minus damage.</b> Always available once the species has been killed once. Weakest -
/// it is a species figure, and this creature may have walked in already hurt.</item>
/// <item><b>Rung.</b> The health descriptor puts the remaining fraction inside one seventh of the
/// pool. Better than nothing and free on every landed blow, but it inherits the pool band's width and
/// the equal-sevenths model with it.</item>
/// <item><b>Diagnose.</b> A direct reading of the number, in a ten-wide bracket. Strongest by a long
/// way and the only one with no model in it - so when it disagrees with the other two, it wins and
/// they are discarded, not averaged.</item>
/// </list>
///
/// <para><b>Why the rung's UPPER bound is used here when
/// <see cref="StaminaPoolEstimator"/> refuses it.</b> There, the pool is the unknown and a rung
/// reading from a pre-damaged creature would corrupt it. Here the pool band is an INPUT and the
/// individual is the unknown, which is the case the reading is actually about: a creature reading
/// rung 3 has at most three sevenths of a pool left whether or not it arrived hurt. The direction of
/// inference is reversed, so the contamination is too.</para>
///
/// <para>Pure and MAUI-free, exercised directly by mudsharp.Tests.</para>
/// </summary>
public static class NpcRemainingStamina
{
    /// <summary>
    /// The creature's remaining stamina band.
    /// </summary>
    /// <param name="pool">The species estimate from <see cref="StaminaPoolEstimator.Estimate"/>.</param>
    /// <param name="dealtThisFight">Cumulative bracket the player has dealt this fight. Pass
    /// <see cref="DamageBracket.Zero"/> before the first landed blow.</param>
    /// <param name="rung">The latest health descriptor and the damage dealt since it, or null when
    /// none has been printed.</param>
    /// <param name="diagnose">The latest <c>diagnose</c> reading and the damage dealt since it, or
    /// null. This is the strongest constraint available and overrides the others outright when they
    /// conflict.</param>
    public static NpcStaminaBand Compute(
        StaminaPoolEstimate pool,
        DamageBracket dealtThisFight,
        NpcRungAnchor? rung = null,
        NpcStaminaReading? diagnose = null)
    {
        var basis = RemainingBasis.None;
        var contradicted = false;
        var band = StaminaInterval.Unbounded;

        if (pool.HasEvidence)
        {
            band = Age(pool.Interval, dealtThisFight);
            basis |= RemainingBasis.Pool;

            if (rung is NpcRungAnchor anchor && anchor.Rung >= 1 && anchor.Rung <= NpcHealthRungs.Rungs)
            {
                // Rung k means the remaining fraction was in ((k-1)/7, k/7] of the pool when it was
                // printed. Each end takes the matching end of the pool band, which is what keeps the
                // result a band and not a point wearing error bars.
                var lowFraction = (anchor.Rung - 1) / (double)NpcHealthRungs.Rungs;
                var highFraction = anchor.Rung / (double)NpcHealthRungs.Rungs;
                var atReading = new StaminaInterval(
                    pool.Interval.Above * lowFraction,
                    pool.Interval.AtMost is double top ? top * highFraction : null);

                var aged = Age(atReading, anchor.DealtSince);
                var merged = band.Intersect(aged);
                if (merged.IsEmpty)
                {
                    // The rung is the modelled constraint of the two, so it is the one that goes. A
                    // creature that arrived pre-damaged reads healthier than its own pool band minus
                    // our damage allows, and that is the ordinary cause of this.
                    contradicted = true;
                }
                else
                {
                    band = merged;
                    basis |= RemainingBasis.Rung;
                }
            }
        }

        if (diagnose is NpcStaminaReading reading)
        {
            var aged = Age(StaminaInterval.FromInclusive(reading.PrintedLow, reading.PrintedHigh), reading.DealtSince);
            var merged = band.Intersect(aged);
            if (merged.IsEmpty)
            {
                // A measurement beats two inferences. Everything else is discarded rather than blended:
                // the probe read the number off the creature, and whatever the species band says, this
                // individual is what it is.
                band = aged;
                basis = RemainingBasis.Diagnose;
                contradicted = true;
            }
            else
            {
                band = merged;
                basis |= RemainingBasis.Diagnose;
            }
        }

        if (basis == RemainingBasis.None)
            return NpcStaminaBand.Unknown;

        // A creature that is still standing has more than nothing left. Arithmetic can drive the floor
        // negative (a fight that has already out-dealt the species band) and a negative floor would
        // draw below the axis.
        if (band.Above < 0)
            band = band with { Above = 0 };

        // The ceiling can be driven negative too - not just the floor - when the damage dealt so far
        // out-dealts even the band's own upper end (the floor clamp above only fires when Above itself
        // went negative; it says nothing about AtMost, which is "real information" and left alone in
        // the ordinary case - see ADealtBracketThatBarelyOutDealtsOnlyTheFloor in the test fixture).
        // When THAT happens, the result is
        // self-contradictory: arithmetic says this creature should already be dead, but it is being
        // observed alive (that is the only way this method gets called for it at all). Clamping AtMost
        // up to 0 alongside the already-zeroed floor would manufacture a confident [0,0] reading - a
        // creature that is still fighting rendered as a measured EMPTY band, which is rule 5's violation
        // in its worst direction ("an unknown must never render as a measured state"). A contradiction
        // between constraints is exactly what IsEmpty already means for this type (see StaminaInterval's
        // own remarks) - so once the floor is clamped, if the band is STILL empty (AtMost <= Above), the
        // honest response is to discard the whole reading as unknown, never to round it to zero.
        if (band.IsEmpty)
            return NpcStaminaBand.Unknown;

        return new NpcStaminaBand(
            band, basis, pool.Quantity, pool.SupportingFights, contradicted, pool.Evidence);
    }

    /// <summary>
    /// Carries an interval forward past a bracket of damage. The remaining stamina falls by at least
    /// the bracket's low and at most its high, so the band's floor drops by the HIGH end and its
    /// ceiling by the LOW end - the band widens by the bracket's own width, which is the honest cost
    /// of not being told the exact figure.
    /// </summary>
    internal static StaminaInterval Age(StaminaInterval interval, DamageBracket dealt)
        => new(
            interval.Above - dealt.High,
            interval.AtMost is double top ? top - dealt.Low : null);
}
