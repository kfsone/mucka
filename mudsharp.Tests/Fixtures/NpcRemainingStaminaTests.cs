using MudSharp.Combat;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// The remaining-stamina band: the species pool estimate less this fight's damage, narrowed by the
/// creature's own health descriptor and by a <c>diagnose</c> reading.
///
/// <para>This is what the rail will eventually draw as a band on the same axis as the player's own
/// stamina, so the contract matters more than the pixels: which constraints contributed, whether any
/// of them had to be discarded, and whether the quantity is a pool at all (it is not, for a
/// regenerating creature).</para>
/// </summary>
public sealed class NpcRemainingStaminaTests
{
    /// <summary>A species band of (90, 110] - the shape a well-sampled creature produces.</summary>
    private static StaminaPoolEstimate Pool(
        double above = 90, double? atMost = 110, int fights = 5,
        PoolQuantity quantity = PoolQuantity.StaminaPool,
        PoolEvidence? evidence = null)
    {
        var interval = new StaminaInterval(above, atMost);
        return new StaminaPoolEstimate(
            "water-snake", quantity,
            evidence ?? (atMost is null ? PoolEvidence.LowerBoundOnly : PoolEvidence.Band),
            interval, fights, fights, 0, 0, 0, [interval]);
    }

    private static DamageBracket Dealt(double low, double high) => new(low, high);

    // -- Pool minus damage --------------------------------------------------------

    [Fact]
    public void SubtractingThisFightsDamageWidensTheBandByTheBracketsOwnWidth()
    {
        // Pool (90, 110], dealt 30-45. The floor drops by the HIGH end and the ceiling by the LOW end,
        // because the creature has lost at least 30 and at most 45 - so the band widens by 15, which is
        // the honest cost of MUD2 never giving the player an exact figure.
        var band = NpcRemainingStamina.Compute(Pool(), Dealt(30, 45));

        Assert.Equal(RemainingBasis.Pool, band.Basis);
        Assert.Equal(45, band.Interval.Above, 6);       // 90 - 45
        Assert.Equal(80, band.Interval.AtMost!.Value, 6); // 110 - 30
        Assert.False(band.Contradicted);
        Assert.Equal(5, band.SupportingFights);
    }

    [Fact]
    public void BeforeTheFirstBlowTheBandIsThePoolBand()
    {
        var band = NpcRemainingStamina.Compute(Pool(), DamageBracket.Zero);
        Assert.Equal(90, band.Interval.Above, 6);
        Assert.Equal(110, band.Interval.AtMost!.Value, 6);
    }

    [Fact]
    public void ADealtBracketThatOutDealtsTheWholeBand_IsUnknownRatherThanAConfidentEmptyBand()
    {
        // A fight that has already out-dealt the species band by MORE than its own width: dealt
        // 200-260 against a (90, 110] pool ages to (90-260, 110-200] = (-170, -90] - both ends
        // negative, so even the most generous end of the band already reads as "past dead" while
        // the creature is, by construction, still being observed alive (that is the only way this
        // method gets called for it). Rule 5, "an unknown must never render as a measured state",
        // means a contradiction between "damage dealt says dead" and "still standing" must discard
        // the whole reading as UNKNOWN, never round it to a measured zero.
        var band = NpcRemainingStamina.Compute(Pool(), Dealt(200, 260));
        Assert.Same(NpcStaminaBand.Unknown, band);
        Assert.False(band.HasEvidence);
    }

    [Fact]
    public void ADealtBracketThatBarelyOutDealtsOnlyTheFloor_StillLeavesAnHonestCeiling()
    {
        // The ordinary (non-contradictory) case the floor clamp exists for: dealt 95-100 against a
        // (90, 110] pool ages to (90-100, 110-95] = (-10, 15] - the floor alone goes negative
        // (clamped to 0) while the ceiling stays positive and is left alone, exactly as before.
        var band = NpcRemainingStamina.Compute(Pool(), Dealt(95, 100));
        Assert.True(band.HasEvidence);
        Assert.Equal(0, band.Interval.Above, 6);
        Assert.Equal(15, band.Interval.AtMost!.Value, 6);
    }

    [Fact]
    public void NoPoolEvidenceAndNoReadingIsUnknownRatherThanZero()
    {
        var band = NpcRemainingStamina.Compute(StaminaPoolEstimate.None, Dealt(10, 14));
        Assert.False(band.HasEvidence);
        Assert.Equal(RemainingBasis.None, band.Basis);
        Assert.Same(NpcStaminaBand.Unknown, band);
    }

    [Fact]
    public void ALowerBoundOnlyPoolProducesAnUnboundedBand()
    {
        var band = NpcRemainingStamina.Compute(Pool(atMost: null), Dealt(10, 14));
        Assert.True(band.HasEvidence);
        Assert.False(band.IsBounded);
        Assert.Equal(76, band.Interval.Above, 6);   // 90 - 14
    }

    // -- The rung -----------------------------------------------------------------

    [Fact]
    public void ARungReadingNarrowsTheBandToItsSeventh()
    {
        // Rung 4 says remaining is in (3/7 of the pool floor, 4/7 of its ceiling] = (38.57, 62.86].
        // Pool-minus-damage after 50-60 dealt is (30, 60]. The tightest of the two takes the rung's
        // floor and the pool's ceiling, which is the point: neither constraint dominates.
        var band = NpcRemainingStamina.Compute(
            Pool(), Dealt(50, 60), new NpcRungAnchor(4, DamageBracket.Zero));

        Assert.True(band.Basis.HasFlag(RemainingBasis.Rung));
        Assert.True(band.Basis.HasFlag(RemainingBasis.Pool));
        Assert.False(band.Contradicted);
        // pool-minus-damage is (30, 60]; the rung says (90 x 3/7, 110 x 4/7] = (38.571, 62.857].
        Assert.Equal(90 * 3 / 7.0, band.Interval.Above, 4);
        Assert.Equal(60, band.Interval.AtMost!.Value, 6);
    }

    [Fact]
    public void RungSevenAddsNothingToAnUntouchedCreature()
    {
        // "Fit" says remaining is in (6/7 of the pool floor, the pool ceiling] = (77.1, 110]. With no
        // damage dealt the pool band itself is already (90, 110], which is tighter at both ends - so
        // the rung contributes and changes nothing, which is the correct outcome rather than a bug.
        var band = NpcRemainingStamina.Compute(
            Pool(), DamageBracket.Zero, new NpcRungAnchor(7, DamageBracket.Zero));

        Assert.True(band.Basis.HasFlag(RemainingBasis.Rung));
        Assert.Equal(90, band.Interval.Above, 6);
        Assert.Equal(110, band.Interval.AtMost!.Value, 6);
    }

    [Fact]
    public void ALowRungCapsTheBandFarBelowThePoolCeiling()
    {
        // Rung 2 on a (90, 110] pool: remaining is at most 2/7 of 110 = 31.4, which is what the rung
        // buys that the pool arithmetic cannot - a creature nearly dead after damage the client only
        // partly saw.
        var band = NpcRemainingStamina.Compute(
            Pool(), Dealt(60, 90), new NpcRungAnchor(2, DamageBracket.Zero));

        Assert.True(band.Basis.HasFlag(RemainingBasis.Rung));
        Assert.Equal(110 * 2 / 7.0, band.Interval.AtMost!.Value, 4);
    }

    [Fact]
    public void ARungReadingIsAgedForwardByTheDamageDealtSinceIt()
    {
        // The reading was printed three blows ago; the creature has taken 20-30 since. Both ends of the
        // rung's own window move, and the window widens by 10.
        var fresh = NpcRemainingStamina.Compute(
            Pool(), DamageBracket.Zero, new NpcRungAnchor(5, DamageBracket.Zero));
        var stale = NpcRemainingStamina.Compute(
            Pool(), Dealt(20, 30), new NpcRungAnchor(5, Dealt(20, 30)));

        Assert.Equal(fresh.Interval.Above - 30, stale.Interval.Above, 6);
        Assert.Equal(fresh.Interval.AtMost!.Value - 20, stale.Interval.AtMost!.Value, 6);
    }

    [Fact]
    public void ARungThatContradictsThePoolBandIsDroppedAndFlagged()
    {
        // A pre-damaged creature reads healthier than the species band minus our damage allows. The
        // rung is the modelled constraint of the two, so it is the one that goes - but the caller is
        // told the band is resting on fewer legs than it looks.
        var band = NpcRemainingStamina.Compute(
            Pool(), Dealt(10, 14), new NpcRungAnchor(1, DamageBracket.Zero));

        Assert.True(band.Contradicted);
        Assert.False(band.Basis.HasFlag(RemainingBasis.Rung));
        Assert.True(band.Basis.HasFlag(RemainingBasis.Pool));
        Assert.Equal(76, band.Interval.Above, 6);   // pool-minus-damage survives untouched
    }

    [Fact]
    public void ARungAloneWithNoPoolEvidenceSaysNothing()
    {
        // The rung is a FRACTION of the pool, so with no pool there is no number to take a fraction of.
        // Reporting the rung alone would be a scale with no units.
        var band = NpcRemainingStamina.Compute(
            StaminaPoolEstimate.None, DamageBracket.Zero, new NpcRungAnchor(3, DamageBracket.Zero));

        Assert.False(band.HasEvidence);
    }

    // -- Diagnose, the only direct measurement ------------------------------------

    [Fact]
    public void ADiagnoseReadingIsStoredExactlyAsPrintedAndConvertedWithoutWidening()
    {
        // "between 90 and 99" means 90 to 99 inclusive. Stamina is integral, so "at least 90" is
        // "more than 89" exactly - nothing is rounded to a decade or snapped to a grid, because the
        // bracket's alignment is unresolved at four observations in the whole corpus.
        var band = NpcRemainingStamina.Compute(
            StaminaPoolEstimate.None, DamageBracket.Zero,
            diagnose: new NpcStaminaReading(90, 99, DamageBracket.Zero));

        Assert.Equal(RemainingBasis.Diagnose, band.Basis);
        Assert.Equal(89, band.Interval.Above, 6);
        Assert.Equal(99, band.Interval.AtMost!.Value, 6);
        Assert.True(band.Interval.Contains(90));
        Assert.True(band.Interval.Contains(99));
        Assert.False(band.Interval.Contains(100));
    }

    [Fact]
    public void ADiagnoseReadingNarrowsAWiderPoolBand()
    {
        var band = NpcRemainingStamina.Compute(
            Pool(above: 80, atMost: 130), DamageBracket.Zero,
            diagnose: new NpcStaminaReading(90, 99, DamageBracket.Zero));

        Assert.True(band.Basis.HasFlag(RemainingBasis.Diagnose));
        Assert.True(band.Basis.HasFlag(RemainingBasis.Pool));
        Assert.False(band.Contradicted);
        Assert.Equal(89, band.Interval.Above, 6);
        Assert.Equal(99, band.Interval.AtMost!.Value, 6);
    }

    [Fact]
    public void ADiagnoseReadingIsAgedByTheDamageDealtSinceIt()
    {
        // Read at 90-99, then hit twice for 10-14 each. Remaining is 90-28 to 99-20.
        var band = NpcRemainingStamina.Compute(
            StaminaPoolEstimate.None, Dealt(20, 28),
            diagnose: new NpcStaminaReading(90, 99, Dealt(20, 28)));

        Assert.Equal(61, band.Interval.Above, 6);       // (90 - 1) - 28
        Assert.Equal(79, band.Interval.AtMost!.Value, 6);
    }

    [Fact]
    public void ADiagnoseReadingBeatsTheSpeciesBandOutrightWhenTheyConflict()
    {
        // The probe read the number off the creature. Whatever the species band says, this individual
        // is what it is - so everything else is discarded rather than blended, and the caller is told.
        var band = NpcRemainingStamina.Compute(
            Pool(above: 90, atMost: 110), DamageBracket.Zero,
            diagnose: new NpcStaminaReading(18, 27, DamageBracket.Zero));

        Assert.True(band.Contradicted);
        Assert.Equal(RemainingBasis.Diagnose, band.Basis);
        Assert.Equal(17, band.Interval.Above, 6);
        Assert.Equal(27, band.Interval.AtMost!.Value, 6);
    }

    [Fact]
    public void AllThreeConstraintsCanContributeAtOnce()
    {
        var band = NpcRemainingStamina.Compute(
            Pool(above: 80, atMost: 130), Dealt(10, 14),
            rung: new NpcRungAnchor(7, DamageBracket.Zero),
            diagnose: new NpcStaminaReading(90, 120, DamageBracket.Zero));

        Assert.True(band.Basis.HasFlag(RemainingBasis.Pool));
        Assert.True(band.Basis.HasFlag(RemainingBasis.Rung));
        Assert.True(band.Basis.HasFlag(RemainingBasis.Diagnose));
        Assert.False(band.Contradicted);
        // Tightest of: pool-minus-damage (66, 120], rung 7 (80 x 6/7 = 68.57, 130], diagnose (89, 120].
        Assert.Equal(89, band.Interval.Above, 6);
        Assert.Equal(120, band.Interval.AtMost!.Value, 6);
    }

    // -- The figure the rail actually draws ---------------------------------------

    [Fact]
    public void AnUntouchedReadingDrawsTheNumbersTheGamePrinted()
    {
        var read = new NpcStaminaReading(90, 99, DamageBracket.Zero);

        Assert.True(read.TryCurrent(out var low, out var high));
        Assert.Equal(90, low);
        Assert.Equal(99, high);
    }

    [Fact]
    public void TheDrawnFigureFallsByTheDamageLandedSinceTheProbe()
    {
        // Read at 90-99, then hit twice for 10-14 each: the creature now has between 90-28 and 99-20.
        // The band widens by the bracket's own width, which is the honest cost of not being told the
        // exact figure - and it is the same arithmetic the seal beside it is filling to.
        var read = new NpcStaminaReading(90, 99, Dealt(20, 28));

        Assert.True(read.TryCurrent(out var low, out var high));
        Assert.Equal(62, low);
        Assert.Equal(79, high);
    }

    [Fact]
    public void AFigureDrivenBelowZeroKeepsAFloorOfOneRatherThanReadingEmpty()
    {
        // Out-dealt at the bottom end only: the creature may have nothing measurable left, but it is
        // still standing, and a standing creature has more than zero. The floor clamps; the ceiling is
        // real information and is left alone.
        var read = new NpcStaminaReading(90, 99, Dealt(40, 120));

        Assert.True(read.TryCurrent(out var low, out var high));
        Assert.Equal(1, low);
        Assert.Equal(59, high);
    }

    [Fact]
    public void AFigureTheFightHasAlreadyOutDealtEntirelyIsRefusedRatherThanDrawnAsZero()
    {
        // The player has dealt more than even the probe's upper end, and the creature is demonstrably
        // still alive - that is the only way this is being asked at all. Arithmetic and observation
        // contradict each other, so the arithmetic produces nothing. A confident 0-0 on a creature that
        // is still swinging is the worst lie this panel can tell; the caller falls back to the sentence
        // MUD2 actually printed.
        var read = new NpcStaminaReading(90, 99, Dealt(120, 140));

        Assert.False(read.TryCurrent(out _, out _));
        Assert.Null(read.Current);
    }

    [Fact]
    public void TheDrawnFigureAndTheSealAgreeBecauseTheyShareTheSameAgeing()
    {
        // The row's number and the ring's fill must never describe different creatures, so both go
        // through NpcRemainingStamina.Age. With no species pool on file, Compute's band IS the diagnose
        // leg, and the two must land on the same interval.
        var read = new NpcStaminaReading(90, 99, Dealt(20, 28));
        var band = NpcRemainingStamina.Compute(StaminaPoolEstimate.None, Dealt(20, 28), diagnose: read);

        Assert.Equal(band.Interval.Above, read.Current!.Value.Above, 6);
        Assert.Equal(band.Interval.AtMost!.Value, read.Current!.Value.AtMost!.Value, 6);
    }

    // -- The species evidence state travels with the band -------------------------

    [Fact]
    public void TheBandCarriesHowWellTheSpeciesPoolIsKnown()
    {
        // A renderer holding only the band still has to be able to tell an ordinary reading from a
        // split one, because a split pool's band is derived from a HULL and drawing it as a solid
        // range claims support across a gap that has none.
        var ordinary = NpcRemainingStamina.Compute(Pool(), Dealt(10, 14));
        Assert.Equal(PoolEvidence.Band, ordinary.Evidence);

        var split = NpcRemainingStamina.Compute(
            Pool(evidence: PoolEvidence.Split), Dealt(10, 14));
        Assert.Equal(PoolEvidence.Split, split.Evidence);
    }

    [Fact]
    public void ADiagnoseOnlyBandReportsNoSpeciesEvidenceWithoutBeingUnknown()
    {
        // Not a contradiction: the probe is carrying this band on its own, which is the strongest state
        // there is. Basis says Diagnose; Evidence says the species figure contributed nothing.
        var band = NpcRemainingStamina.Compute(
            StaminaPoolEstimate.None, DamageBracket.Zero,
            diagnose: new NpcStaminaReading(90, 99, DamageBracket.Zero));

        Assert.Equal(RemainingBasis.Diagnose, band.Basis);
        Assert.Equal(PoolEvidence.None, band.Evidence);
        Assert.True(band.HasEvidence);
    }

    // -- The regeneration label travels with the band -----------------------------

    [Fact]
    public void ARegeneratingSpeciesBandIsLabelledAsDamageToKill()
    {
        // For a zombie the species figure is "how much more damage it should take", not "how much
        // stamina it has", and a readout must not word it as the latter.
        var band = NpcRemainingStamina.Compute(
            Pool(quantity: PoolQuantity.DamageToKill), Dealt(10, 14));

        Assert.Equal(PoolQuantity.DamageToKill, band.Quantity);
    }
}
