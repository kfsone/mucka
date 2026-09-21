using MudSharp.Combat;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// Coverage for what an opponent's seal actually says: <see cref="NpcVitality"/> (the fraction),
/// <see cref="DamagePrediction"/> (rDPT), <see cref="StaminaSeal"/> (the geometry) and
/// <see cref="ReachAggregate"/> (folding a pack down to one mark).
///
/// <para>The property under test throughout is that the NPC readout is a LADDER POSITION - a fraction
/// of the creature's own full - and that no absolute stamina figure escapes to it. The player is
/// guided by text: "looks fit" means close to full and does not mean 50, or 75, or 150. The
/// estimator's absolute band exists only to place the fill more finely inside the seventh the game
/// named.</para>
///
/// <para><b>A note on how these are written.</b> <see cref="NpcVitality.Estimate"/> takes three
/// arguments and short-circuits on two of them, so a test can pass an elaborate pool that is never
/// read and assert an outcome that would hold for any pool at all. Every test below that supplies a
/// pool or a remaining band therefore also pins something that CHANGES when that argument changes -
/// a narrowed basis flag, or a band strictly tighter than the bare seventh - so a green result means
/// the argument was consumed and not merely accepted.</para>
/// </summary>
public sealed class StaminaSealTests
{
    private static StaminaPoolEstimate Pool(
        double above, double? atMost, PoolEvidence evidence = PoolEvidence.Band,
        IReadOnlyList<StaminaInterval>? runs = null)
        => new(
            "rat", PoolQuantity.StaminaPool, evidence, new StaminaInterval(above, atMost),
            SupportingFights: 3, ContributingFights: 3, LinkedEngagements: 0, DroppedInFolds: 0,
            ContradictoryFights: 0, TiedRuns: runs ?? [new StaminaInterval(above, atMost)]);

    private const double Seventh = 1.0 / 7.0;

    /// <summary>The whole seventh a bare rung-4 descriptor gives, as the yardstick every narrowing
    /// test measures against.</summary>
    private static readonly VitalityBand BareRungFour = NpcVitality.Estimate(4, null, null)!.Value;

    // -- the fraction ------------------------------------------------------------

    [Fact]
    public void ARungOnItsOwn_IsExactlyOneSeventh()
    {
        // All the game said. Rung 4 of 7 means "somewhere in the fourth seventh" and nothing narrower.
        Assert.Equal(3 * Seventh, BareRungFour.Low, 6);
        Assert.Equal(4 * Seventh, BareRungFour.High, 6);
        Assert.Equal(VitalityBasis.Rung, BareRungFour.Basis);
        Assert.Equal(1.0, BareRungFour.StepsWide, 6);
    }

    [Fact]
    public void ARatAndAGiantAtTheSameRung_ReadTheSame_ThroughTheNarrowingPath()
    {
        // A rat and a giant at the same rung must read the same, and this has to be tested THROUGH
        // the estimator rather than around it: passing remaining:null to both sides would make
        // NpcVitality.TryDerive return before the pool is ever read, so the two wildly different
        // pools would never be consulted and the test would pass for any pair of numbers at all.
        //
        // Here both creatures go through the full path with proportionally identical evidence: a
        // well-pinned pool, a rung-4 descriptor, and damage scaled 10x with the giant. The giant is ten
        // times the rat in every absolute figure and the two must still draw the same ring.
        var ratPool = Pool(24, 25);
        var rat = NpcVitality.Estimate(4, ratPool, NpcRemainingStamina.Compute(
            ratPool, new DamageBracket(13.5, 13.5), new NpcRungAnchor(4, new DamageBracket(1.5, 1.5))))!.Value;

        var giantPool = Pool(240, 250);
        var giant = NpcVitality.Estimate(4, giantPool, NpcRemainingStamina.Compute(
            giantPool, new DamageBracket(135, 135), new NpcRungAnchor(4, new DamageBracket(15, 15))))!.Value;

        Assert.Equal(rat.Low, giant.Low, 6);
        Assert.Equal(rat.High, giant.High, 6);

        // And the arguments were demonstrably consumed: both narrowed to about a third of a rung step,
        // which is a state neither could reach with the pool or the remaining band ignored.
        Assert.True(rat.Basis.HasFlag(VitalityBasis.Narrowed));
        Assert.True(giant.Basis.HasFlag(VitalityBasis.Narrowed));
        Assert.True(rat.StepsWide < BareRungFour.StepsWide / 2.0);
        Assert.Equal(rat.StepsWide, giant.StepsWide, 6);
    }

    [Fact]
    public void NoRungAndNothingOnFile_IsNoReadingAtAll()
    {
        // A descriptor follows every non-killing landed hit, so this state means the player has not hit
        // this creature yet - the ordinary case for every row but one in a pack. Null, so the seal can
        // draw its own unmet state - never a zero, which would draw as "nearly dead".
        Assert.Null(NpcVitality.Estimate(rung: null, pool: null, remaining: null));
        Assert.Null(NpcVitality.Estimate(rung: 0, pool: null, remaining: null));
        Assert.Null(NpcVitality.Estimate(rung: 9, pool: null, remaining: null));
    }

    /// <summary>"full of life" is cur == max EXACTLY (328/328 paired readings, and the C1 colour code
    /// on the stamina value is 99.10 only and exactly at max), not merely the top seventh. Rung 7
    /// alone says the creature may be up to a seventh down, so a seal fed only the rung can never
    /// draw full - which is why an untouched opponent always looked slightly wounded.</summary>
    [Fact]
    public void AtMax_FillsTheSeal_WhereRungSevenAloneCannot()
    {
        var atMax = NpcVitality.Estimate(7, null, null, atMax: true)!.Value;
        Assert.Equal(1.0, atMax.Low);
        Assert.Equal(1.0, atMax.High);
        // The whole ring is CERTAIN, so there is no soft arc to fade and the fill reaches the end.
        Assert.Equal(0f, StaminaSeal.BoundaryFor(atMax.Low));

        // The same descriptor's rung, without the at-max point, floors the certain fill at 6/7 - the
        // defect this closes. Pinned so the two cannot silently converge.
        var rungOnly = NpcVitality.Estimate(7, null, null)!.Value;
        Assert.Equal(6 / 7.0, rungOnly.Low, 6);
        Assert.True(StaminaSeal.BoundaryFor(rungOnly.Low) > 0f);
    }

    /// <summary>An exact equality cannot be narrowed, so a pool and a remaining band - which would
    /// otherwise merge and could only make the answer WORSE than the truth - must not touch it.</summary>
    [Fact]
    public void AtMax_OutranksEveryNarrowingSource()
    {
        var pool = Pool(above: 10, atMost: 12);
        var narrowed = NpcVitality.Estimate(
            7, pool, NpcRemainingStamina.Compute(pool, new DamageBracket(5, 9)), atMax: true)!.Value;

        Assert.Equal(1.0, narrowed.Low);
        Assert.Equal(1.0, narrowed.High);
        Assert.Equal(VitalityBasis.None, narrowed.Basis & VitalityBasis.Narrowed);
    }

    [Fact]
    public void APoolWithNoEvidence_IsNoDenominator_EvenWithARealRemainingBand()
    {
        // Reaches the pool-evidence guard rather than stopping at the remaining:null one before it: the
        // band handed in is a real, narrowing-capable one (it narrows to a third of a rung against a
        // pool that HAS evidence, two tests up), and it is the absent species pool that stops it here.
        var capable = NpcRemainingStamina.Compute(
            Pool(24, 25), new DamageBracket(13.5, 13.5), new NpcRungAnchor(4, new DamageBracket(1.5, 1.5)));

        var band = NpcVitality.Estimate(4, Pool(0, null, PoolEvidence.None), capable)!.Value;

        Assert.Equal(BareRungFour.Low, band.Low, 6);
        Assert.Equal(BareRungFour.High, band.High, 6);
        Assert.Equal(VitalityBasis.Rung, band.Basis);
    }

    [Fact]
    public void DamageLandedSinceTheReading_NarrowsThePositionInsideTheSeventh()
    {
        // The whole job of the under-the-hood estimator. Against a pool known to be 80-100: 60 dealt
        // this fight, of which 20 landed AFTER the "covered in wounds" line printed. The creature can no
        // longer be at the top of its fourth seventh, because 20 more points have come off since it was.
        var pool = Pool(80, 100);
        var remaining = NpcRemainingStamina.Compute(
            pool, new DamageBracket(60, 60), new NpcRungAnchor(4, new DamageBracket(20, 20)));

        var narrowed = NpcVitality.Estimate(4, pool, remaining)!.Value;

        Assert.True(narrowed.StepsWide < BareRungFour.StepsWide);
        // It narrowed from the TOP - damage removes the possibility of being nearly full, not the
        // possibility of being nearly dead.
        Assert.Equal(BareRungFour.Low, narrowed.Low, 6);
        Assert.True(narrowed.High < BareRungFour.High);
        Assert.True(narrowed.Basis.HasFlag(VitalityBasis.Narrowed));
    }

    [Fact]
    public void ALoosePoolLearnsNothing_WhereATightOneNarrows()
    {
        // Interval division widens, so a species whose pool is only known to 80-100 produces a fraction
        // looser than the rung's own seventh and the intersection simply keeps the seventh. The result
        // must not advertise a narrowing that did not happen.
        //
        // Paired with the tight-pool case deliberately: an assertion that "nothing changed" is exactly
        // the assertion that would also pass if the argument were never read, so the same rung and the
        // same shape of evidence is run against a well-pinned pool to show the path is live and that it
        // is the LOOSENESS doing the refusing.
        var loose = Pool(80, 100);
        var looseBand = NpcVitality.Estimate(4, loose, NpcRemainingStamina.Compute(
            loose, DamageBracket.Zero, new NpcRungAnchor(4, DamageBracket.Zero)))!.Value;

        var tight = Pool(24, 25);
        var tightBand = NpcVitality.Estimate(4, tight, NpcRemainingStamina.Compute(
            tight, new DamageBracket(13.5, 13.5), new NpcRungAnchor(4, new DamageBracket(1.5, 1.5))))!.Value;

        Assert.Equal(BareRungFour.High, looseBand.High, 6);
        Assert.Equal(VitalityBasis.Rung, looseBand.Basis);

        Assert.True(tightBand.High < looseBand.High);
        Assert.True(tightBand.Basis.HasFlag(VitalityBasis.Narrowed));
    }

    [Fact]
    public void ADiagnoseProbe_NarrowsAndIsMarkedAsAMeasurement()
    {
        // "The water-snake5 has a stamina lying between 90 and 99", against a pool known to be 95-100.
        var pool = Pool(95, 100);
        var remaining = NpcRemainingStamina.Compute(
            pool, DamageBracket.Zero, rung: null,
            diagnose: new NpcStaminaReading(90, 99, DamageBracket.Zero));

        var band = NpcVitality.Estimate(rung: null, pool, remaining)!.Value;

        Assert.True(band.Basis.HasFlag(VitalityBasis.Diagnose));
        Assert.True(band.Basis.HasFlag(VitalityBasis.Narrowed));
        Assert.True(band.Low > 0.85);
        Assert.True(band.High <= 1.0);
    }

    [Fact]
    public void ASplitPool_IsNoDenominator_EvenWhenItsHullIsTight()
    {
        // Two disjoint runs at 24-25 and 26-27. The hull is 24-27, which is tight enough that a NORMAL
        // band of exactly those bounds narrows the reading by two thirds of a rung step - so splitness,
        // and nothing else about the numbers, is what has to do the refusing here. Dividing by a hull
        // would produce a fraction supported by nothing in the gap; see PoolEvidence.Split.
        var runs = new[] { new StaminaInterval(24, 25), new StaminaInterval(26, 27) };
        var hull = Pool(24, 27);
        var remaining = NpcRemainingStamina.Compute(
            hull, new DamageBracket(14, 14), new NpcRungAnchor(4, new DamageBracket(1.5, 1.5)));

        var asBand = NpcVitality.Estimate(4, hull, remaining)!.Value;
        var asSplit = NpcVitality.Estimate(4, Pool(24, 27, PoolEvidence.Split, runs), remaining)!.Value;

        // The same numbers, minus the split flag, genuinely narrow.
        Assert.True(asBand.High < BareRungFour.High);
        Assert.True(asBand.Basis.HasFlag(VitalityBasis.Narrowed));

        // With the flag, the seventh stands alone.
        Assert.Equal(BareRungFour.Low, asSplit.Low, 6);
        Assert.Equal(BareRungFour.High, asSplit.High, 6);
        Assert.Equal(VitalityBasis.Rung, asSplit.Basis);
    }

    [Fact]
    public void AContradictionKeepsTheDescriptor_BecauseItIsTheOneReadingOfThisCreature()
    {
        // A creature that walked in already hurt reads healthier than the species band minus our damage
        // allows. The species figure is about its kind; the descriptor is about this one.
        var pool = Pool(20, 25);
        var remaining = new NpcStaminaBand(
            new StaminaInterval(0, 2), RemainingBasis.Pool, PoolQuantity.StaminaPool, 3, true,
            PoolEvidence.Band);

        var band = NpcVitality.Estimate(7, pool, remaining)!.Value;

        Assert.Equal(6 * Seventh, band.Low, 6);
        Assert.Equal(1.0, band.High, 6);
    }

    [Fact]
    public void TheFractionNeverLeavesZeroToOne()
    {
        // A probe that read this individual higher than the species band allows - an ordinary outcome
        // for a creature the estimator has only ever met at half strength. The quotient is 1.2 and 4.0,
        // and either drawn raw would be more than a full ring.
        var pool = Pool(10, 25);
        var remaining = new NpcStaminaBand(
            new StaminaInterval(30, 40), RemainingBasis.Diagnose, PoolQuantity.StaminaPool, 1, true,
            PoolEvidence.Band);

        var band = NpcVitality.Estimate(rung: null, pool, remaining)!.Value;

        Assert.InRange(band.Low, 0.0, 1.0);
        Assert.InRange(band.High, 0.0, 1.0);
        Assert.True(band.High >= band.Low);
    }

    [Fact]
    public void ADerivationThatLearnsNothing_IsNoReadingAtAll()
    {
        // With no descriptor and a pool whose floor is 0, the quotient is the whole 0..1 range - not a
        // reading, and it must not draw as a full ring pretending to be one. Null sends it to the unmet
        // state instead.
        var pool = Pool(0, 25);
        var remaining = new NpcStaminaBand(
            new StaminaInterval(0, 40), RemainingBasis.Pool, PoolQuantity.StaminaPool, 1, false,
            PoolEvidence.Band);

        Assert.Null(NpcVitality.Estimate(rung: null, pool, remaining));
    }

    // -- rung boundary crossings -------------------------------------------------

    [Fact]
    public void ManySmallBlowsResolveTheLadderFarBetterThanFewLargeOnes_AtTheSameTotalDamage()
    {
        // The mechanism by which the reading earns accuracy: repeated small blows give a finer-grained
        // read on when a rung boundary is crossed than one large blow does.
        //
        // A 280-stamina giant driven into rung 4. Both fights have dealt the SAME 120 total, so nothing
        // here turns on cumulative damage - the only difference is the bracket of the blow that pushed
        // it over the line. A 1-4 pins the boundary to a 4-stamina window; a 20-29 pins it to a 29-wide
        // one, which is most of a rung and tells you almost nothing.
        var pool = Pool(240, 280);
        var dealt = new DamageBracket(120, 120);

        var manySmall = NpcVitality.Estimate(
            4, pool, null,
            new NpcRungCrossing(4, new DamageBracket(1, 4), DamageBracket.Zero), dealt)!.Value;

        var fewLarge = NpcVitality.Estimate(
            4, pool, null,
            new NpcRungCrossing(4, new DamageBracket(20, 29), DamageBracket.Zero), dealt)!.Value;

        Assert.True(manySmall.StepsWide < fewLarge.StepsWide);
        // Not a marginal difference: the small-blow reading is inside a fifth of a rung where the
        // large-blow one is most of one.
        Assert.True(manySmall.StepsWide < 0.2);
        Assert.True(fewLarge.StepsWide > 0.5);
        // Both are anchored at the boundary they just crossed, which is the top of rung 4.
        Assert.Equal(4 * Seventh, manySmall.High, 6);
        Assert.Equal(4 * Seventh, fewLarge.High, 6);
    }

    [Fact]
    public void ACrossingBeatsTheBareSeventh_AndSaysSo()
    {
        var pool = Pool(240, 280);
        var crossed = NpcVitality.Estimate(
            4, pool, null,
            new NpcRungCrossing(4, new DamageBracket(1, 4), DamageBracket.Zero),
            new DamageBracket(120, 120))!.Value;

        Assert.True(crossed.StepsWide < BareRungFour.StepsWide);
        Assert.True(crossed.Basis.HasFlag(VitalityBasis.Narrowed));
    }

    [Fact]
    public void ACrossingWorksOnAFirstEncounter_OffTheStillStandingFloorAlone()
    {
        // Nothing on file for the species. The creature is still up after 120, so its pool exceeds 120,
        // and that is enough for a 1-4 crossing blow to pin the boundary usefully.
        var band = NpcVitality.Estimate(
            4, pool: null, remaining: null,
            new NpcRungCrossing(4, new DamageBracket(1, 4), DamageBracket.Zero),
            new DamageBracket(120, 120))!.Value;

        Assert.True(band.StepsWide < BareRungFour.StepsWide);
        Assert.Equal(4 * Seventh, band.High, 6);
    }

    [Fact]
    public void ACrossingWithNoFloorToDivideBy_ClaimsNothing()
    {
        // First blow of a first encounter: no species pool and nothing dealt yet, so there is no
        // denominator and the seventh has to stand on its own.
        var band = NpcVitality.Estimate(
            4, pool: null, remaining: null,
            new NpcRungCrossing(4, new DamageBracket(1, 4), DamageBracket.Zero),
            DamageBracket.Zero)!.Value;

        Assert.Equal(BareRungFour.Low, band.Low, 6);
        Assert.Equal(BareRungFour.High, band.High, 6);
        Assert.Equal(VitalityBasis.Rung, band.Basis);
    }

    [Fact]
    public void ACrossingAgesForwardAsTheFightGoesOn()
    {
        // Blows landed after the crossing carry the band down from the boundary. With no pool ceiling
        // the TOP cannot move - the smallest possible fractional loss is zero when the creature's size
        // is unknown - so ageing widens rather than sliding, which is the honest consequence.
        var pool = Pool(240, 280);
        var fresh = NpcVitality.Estimate(
            4, pool, null,
            new NpcRungCrossing(4, new DamageBracket(1, 4), DamageBracket.Zero),
            new DamageBracket(120, 120))!.Value;
        var aged = NpcVitality.Estimate(
            4, pool, null,
            new NpcRungCrossing(4, new DamageBracket(1, 4), new DamageBracket(20, 20)),
            new DamageBracket(140, 140))!.Value;

        Assert.True(aged.Low < fresh.Low);
        Assert.True(aged.High <= fresh.High);
    }

    [Fact]
    public void ACrossingThatContradictsTheDescriptor_LosesToIt()
    {
        // Regeneration between the crossing and the latest reading. The descriptor is the later
        // statement about this creature, so it survives and the crossing is dropped rather than
        // producing an empty band.
        var pool = Pool(240, 280);
        var band = NpcVitality.Estimate(
            6, pool, null,
            new NpcRungCrossing(2, new DamageBracket(1, 4), DamageBracket.Zero),
            new DamageBracket(120, 120))!.Value;

        Assert.Equal(5 * Seventh, band.Low, 6);
        Assert.Equal(6 * Seventh, band.High, 6);
    }

    [Fact]
    public void OneBlowCrossingSeveralRungs_ProvesTheCreatureIsSmall_OnAFirstEncounter()
    {
        // A large blow that crosses NO boundary says the creature is big (the still-standing floor).
        // A small blow crossing ONE says where it is. A large blow crossing THREE says how big it is
        // - at most 7 x 29 / 2 = 101.5 -
        // and that is a ceiling, available on a species nobody has ever killed.
        var threeRungs = new NpcRungCrossing(4, new DamageBracket(20, 29), DamageBracket.Zero, RungsDropped: 3);

        Assert.Equal(101.5, NpcVitality.PoolCeiling(null, threeRungs)!.Value, 6);

        // One rung yields no ceiling at all - the (N-1) term vanishes.
        Assert.Null(NpcVitality.PoolCeiling(
            null, new NpcRungCrossing(4, new DamageBracket(20, 29), DamageBracket.Zero, RungsDropped: 1)));
    }

    [Fact]
    public void ALiveMultiRungDrop_TwoSidesTheRdptBands_WithNothingOnFile()
    {
        // The visible payoff. Same first encounter, same blow, same everything - except that the blow
        // is known to have carried the creature down three rungs. Without that the band runs from the
        // boundary outward with no lower edge; with it the band closes.
        var vitality = NpcVitality.Estimate(4, null, null)!.Value;
        var blow = new DamageBracket(20, 29);
        var dealt = new DamageBracket(60, 60);

        var oneRung = DamagePrediction.AfterBlows(
            1, vitality, null, blow, dealt,
            new NpcRungCrossing(4, blow, DamageBracket.Zero, RungsDropped: 1))!.Value;
        var threeRungs = DamagePrediction.AfterBlows(
            1, vitality, null, blow, dealt,
            new NpcRungCrossing(4, blow, DamageBracket.Zero, RungsDropped: 3))!.Value;

        // One-sided: the band starts at the boundary itself, because nothing bounds the pool above.
        Assert.Equal(1.0 - vitality.High, oneRung.Low, 6);
        // Two-sided: the ceiling gives the blow a minimum bite, so the band lifts off the boundary.
        Assert.True(threeRungs.Low > oneRung.Low);
        Assert.True(threeRungs.StepsWide < oneRung.StepsWide);
    }

    [Fact]
    public void ATighterSpeciesCeilingStillWins_OverAWeakCrossing()
    {
        // The two sources are both ceilings on one quantity, so the smaller binds - the mirror of the
        // floor taking the larger of the species estimate and the still-standing figure.
        var weak = new NpcRungCrossing(4, new DamageBracket(20, 29), DamageBracket.Zero, RungsDropped: 2);
        Assert.Equal(203.0, NpcVitality.PoolCeiling(null, weak)!.Value, 6);
        Assert.Equal(100.0, NpcVitality.PoolCeiling(Pool(96, 100), weak)!.Value, 6);

        // And a crossing tighter than the species figure binds instead.
        var strong = new NpcRungCrossing(4, new DamageBracket(20, 29), DamageBracket.Zero, RungsDropped: 6);
        Assert.Equal(40.6, NpcVitality.PoolCeiling(Pool(96, 100), strong)!.Value, 6);
    }

    [Fact]
    public void ASplitSpeciesContributesNoCeiling_ButItsCrossingStillDoes()
    {
        // The hull spans a gap nothing supports, so it is no denominator. A crossing is an observation
        // of the individual in front of the player, so it survives that refusal.
        var runs = new[] { new StaminaInterval(24, 25), new StaminaInterval(96, 100) };
        var split = Pool(24, 100, PoolEvidence.Split, runs);

        Assert.Null(NpcVitality.PoolCeiling(split, null));
        Assert.Equal(203.0,
            NpcVitality.PoolCeiling(
                split, new NpcRungCrossing(4, new DamageBracket(20, 29), DamageBracket.Zero, 2))!.Value, 6);
    }

    // -- the seal's geometry -----------------------------------------------------

    [Fact]
    public void NoReading_IsAFullRing_NeverAnEmptyOne()
    {
        // The most dangerous case on the panel must not be the emptiest-looking thing on it. Empty
        // reads as nearly dead; absent reads as safe.
        var plan = StaminaSeal.Plan(null, null, null);

        Assert.Equal(SealShape.Unmet, plan.Shape);
        Assert.False(plan.HasFill);
    }

    [Fact]
    public void ANarrowedReading_LooksDifferentFromABareRung()
    {
        var rung = StaminaSeal.Plan(new VitalityBand(3 * Seventh, 4 * Seventh, VitalityBasis.Rung), null, null);
        var narrowed = StaminaSeal.Plan(
            new VitalityBand(3 * Seventh, 3.3 * Seventh, VitalityBasis.Rung | VitalityBasis.Narrowed), null, null);

        Assert.Equal(SealShape.Rung, rung.Shape);
        Assert.Equal(SealShape.Narrowed, narrowed.Shape);
        // The uncertain run between the two boundaries is what shrinks.
        Assert.True(narrowed.LitSoft!.Value.SweepDegrees < rung.LitSoft!.Value.SweepDegrees);
        Assert.False(rung.Measured);
    }

    [Fact]
    public void TheSevenStepsAreTheRingsOwnNotches()
    {
        // "Down 2 bubbles" has to be countable off the ring, so the notches are the wound scale's own
        // seven steps and nothing else.
        Assert.Equal(360f / 7f, StaminaSeal.StepDegrees(1), 3);
        Assert.Equal(360f, StaminaSeal.StepDegrees(NpcHealthRungs.Rungs), 3);
    }

    [Fact]
    public void Sweep_ClampsRatherThanWrapping()
    {
        Assert.Equal(0f, StaminaSeal.Sweep(-0.5), 3);
        Assert.Equal(0f, StaminaSeal.Sweep(0.0), 3);
        Assert.Equal(180f, StaminaSeal.Sweep(0.5), 3);
        Assert.Equal(360f, StaminaSeal.Sweep(2.0), 3);
    }

    // -- rDPT: the prediction bands ----------------------------------------------

    /// <summary>A creature at rung 4, i.e. somewhere in its fourth seventh.</summary>
    private static readonly VitalityBand HalfGone = NpcVitality.Estimate(4, null, null)!.Value;

    [Fact]
    public void APredictionNeedsSomethingToDivideBy()
    {
        // A species never fought, no blow landed yet, so no floor under its pool and no bracket
        // history: there is genuinely nothing to divide by and the honest output is no band. Not a
        // wide band, not a default - nothing.
        Assert.Null(DamagePrediction.AfterBlows(
            1, HalfGone, pool: null, perBlow: new DamageBracket(15, 19), DamageBracket.Zero));

        // And no bracket means no band even against a well-known species.
        Assert.Null(DamagePrediction.AfterBlows(
            1, HalfGone, Pool(24, 25), perBlow: null, new DamageBracket(10, 10)));

        // And no reading means nothing to anchor to.
        Assert.Null(DamagePrediction.AfterBlows(
            1, vitality: null, Pool(24, 25), new DamageBracket(15, 19), new DamageBracket(10, 10)));
    }

    [Fact]
    public void OnAFirstEncounter_TheCreatureStillStanding_IsTheOnlyFloorThereIs()
    {
        // Nothing on file for the species, but this creature has taken 60 and is still up, so its pool
        // exceeds 60. That is the same constraint family the estimator's kill bound uses and it is what
        // makes a coarse band possible on a first meeting.
        var band = DamagePrediction.AfterBlows(
            1, HalfGone, pool: null, new DamageBracket(15, 19), new DamageBracket(60, 60));

        Assert.NotNull(band);
        // One-sided: no ceiling on the pool means no lower bound on the fraction a blow takes, so the
        // band starts at the boundary itself and runs out to the most the blow could take.
        Assert.Equal(1.0 - HalfGone.High, band!.Value.Low, 6);
        Assert.Equal(1.0 - HalfGone.High + (19.0 / 60.0), band.Value.High, 6);
    }

    [Fact]
    public void TheBandTightensAsTheBestiaryFillsIn()
    {
        // The payoff for the estimator existing, stated as an assertion. Same creature, same blow, three
        // states of knowledge: only this fight's floor, then a species floor from fights that never
        // ended in a kill, then a two-sided band from fights that did.
        var blow = new DamageBracket(15, 19);
        var dealt = new DamageBracket(30, 30);

        var firstMeeting = DamagePrediction.AfterBlows(1, HalfGone, null, blow, dealt)!.Value;
        var neverKilled = DamagePrediction.AfterBlows(
            1, HalfGone, Pool(90, null, PoolEvidence.LowerBoundOnly), blow, dealt)!.Value;
        var killedOften = DamagePrediction.AfterBlows(
            1, HalfGone, Pool(96, 100), blow, dealt)!.Value;

        Assert.True(neverKilled.StepsWide < firstMeeting.StepsWide);
        Assert.True(killedOften.StepsWide < neverKilled.StepsWide);
        // And the tightest of the three is inside a single rung step, which is the visible difference.
        Assert.True(killedOften.StepsWide < 1.0);
    }

    [Fact]
    public void TheBandTightensWithinTheFightToo()
    {
        // Nothing on file for the species: the only floor is what this creature has already absorbed, so
        // the prediction sharpens purely by fighting on. Nothing latches.
        var blow = new DamageBracket(15, 19);
        var early = DamagePrediction.AfterBlows(1, HalfGone, null, blow, new DamageBracket(20, 20))!.Value;
        var later = DamagePrediction.AfterBlows(1, HalfGone, null, blow, new DamageBracket(80, 80))!.Value;

        Assert.True(later.StepsWide < early.StepsWide);
    }

    [Fact]
    public void TheSecondBlowLandsFurtherAlongThanTheFirst()
    {
        var blow = new DamageBracket(15, 19);
        var pool = Pool(96, 100);
        var next = DamagePrediction.AfterBlows(1, HalfGone, pool, blow, DamageBracket.Zero)!.Value;
        var after = DamagePrediction.AfterBlows(2, HalfGone, pool, blow, DamageBracket.Zero)!.Value;

        Assert.True(after.Low > next.Low);
        Assert.True(after.High > next.High);
    }

    [Fact]
    public void TheBandIsAnchoredAtTheOptimisticEndOfTheReading()
    {
        // Errs toward the creature having more left, so the fight reads as longer to win - the same
        // direction StaminaPoolEstimate.PessimisticPool errs in, and the safe one in a permadeath game.
        var band = DamagePrediction.AfterBlows(
            1, HalfGone, Pool(96, 100), new DamageBracket(0, 0.0001), DamageBracket.Zero)!.Value;

        // With a blow that takes essentially nothing, the band sits at the boundary implied by the
        // reading's HIGH end (least taken off so far), not its low end.
        Assert.Equal(1.0 - HalfGone.High, band.Low, 4);
    }

    [Fact]
    public void ASplitPoolBoundsFromBelowOnly()
    {
        // Two disjoint runs and nothing to choose between them, so the hull is not a denominator - the
        // same refusal NpcVitality makes. Its floor still bounds the band from below, which is all a
        // one-sided prediction needs, and the result must be strictly looser than the same hull treated
        // as an honest band.
        var runs = new[] { new StaminaInterval(24, 25), new StaminaInterval(96, 100) };
        var blow = new DamageBracket(15, 19);

        var asSplit = DamagePrediction.AfterBlows(
            1, HalfGone, Pool(24, 100, PoolEvidence.Split, runs), blow, DamageBracket.Zero)!.Value;
        var asBand = DamagePrediction.AfterBlows(
            1, HalfGone, Pool(24, 100), blow, DamageBracket.Zero)!.Value;

        Assert.Equal(1.0 - HalfGone.High, asSplit.Low, 6);
        Assert.True(asBand.Low > asSplit.Low);
        Assert.True(asSplit.StepsWide > asBand.StepsWide);
    }

    [Fact]
    public void ThePredictionNeverRunsPastDead()
    {
        // A blow bigger than everything the creature has left. The boundary can reach the far end of the
        // ladder but not past it, and a band that wrapped would draw the prediction back at the healthy
        // end.
        var band = DamagePrediction.AfterBlows(
            2, HalfGone, Pool(9, 10), new DamageBracket(15, 19), DamageBracket.Zero)!.Value;

        Assert.InRange(band.Low, 0.0, 1.0);
        Assert.Equal(1.0, band.High, 6);

        var arc = StaminaSeal.BandArc(band)!.Value;
        Assert.True(arc.EndDegrees <= 360f + 0.001f);
        Assert.True(arc.SweepDegrees >= DamagePrediction.MinimumSweepDegrees);
    }

    [Fact]
    public void APerfectlyKnownBandIsStillVisible()
    {
        // Zero width would be invisible at exactly the moment the prediction is most certain. The floor
        // has to clear the smallest mark the rail can actually draw - about three units of arc in the
        // lanes it uses - rather than merely being non-zero.
        var arc = StaminaSeal.BandArc(new DamageBand(0.5, 0.5))!.Value;

        Assert.Equal(DamagePrediction.MinimumSweepDegrees, arc.SweepDegrees, 3);
        Assert.True(DamagePrediction.MinimumSweepDegrees >= 10f);
    }

    [Fact]
    public void ThisFightsOwnBracketBeatsHistory_SoAWeaponSwapShowsUpImmediately()
    {
        // History says this species is usually hit for 4-6; this fight, with something heavier in hand,
        // has averaged 15-19 over three blows. The prediction has to follow the hands, not the archive.
        var history = BracketProfile.Empty.Add(4, 6).Add(4, 6).Add(4, 6).Add(4, 6);

        var live = DamagePrediction.PerBlow(new DamageBracket(45, 57), 3, history);
        Assert.Equal(15.0, live!.Value.Low, 6);
        Assert.Equal(19.0, live.Value.High, 6);

        // Before anything has landed there are no hands to follow, so history stands in.
        var opening = DamagePrediction.PerBlow(DamageBracket.Zero, 0, history);
        Assert.Equal(4.0, opening!.Value.Low, 6);
        Assert.Equal(6.0, opening.Value.High, 6);

        // And with neither, nothing.
        Assert.Null(DamagePrediction.PerBlow(DamageBracket.Zero, 0, BracketProfile.Empty));
    }

    // -- rDPT: the "how soon" cue ------------------------------------------------

    [Fact]
    public void SwingingLikeAPro_IsSolid()
    {
        // At or above the measured corpus rate there is nothing further to show.
        Assert.Equal(DamagePrediction.TempoReading.Fluent,
            DamagePrediction.Tempo(new SwingTempo(Hits: 70, Misses: 30)).Reading);
        Assert.Equal(DamagePrediction.TempoReading.Fluent,
            DamagePrediction.Tempo(new SwingTempo(Hits: 100, Misses: 0)).Reading);
    }

    [Fact]
    public void NotHittingTheThing_DecaysToOneDotEveryFivePixels()
    {
        // Matches the spec at the low end, exactly.
        var tempo = DamagePrediction.Tempo(new SwingTempo(Hits: 0, Misses: 12));

        Assert.Equal(DamagePrediction.TempoReading.Landing, tempo.Reading);
        Assert.Equal(DamagePrediction.MinDot, tempo.Dot, 4);
        Assert.Equal(DamagePrediction.SparsePeriod, tempo.Dot + tempo.Gap, 4);
    }

    [Fact]
    public void NothingSwungYet_IsItsOwnState_NotAWhiffStreak()
    {
        // Zero attempts must not draw identically to twelve swings that all missed: the player swings
        // at ONE creature at a time, so in a pack every other live row would sit in this state for the
        // whole fight and draw as a fight being lost. MUD2 prints a descriptor after every non-killing
        // landed hit, so "never swung at" is the only way into this state, which makes it sharply
        // defined rather than rare.
        var nothingYet = DamagePrediction.Tempo(SwingTempo.None);
        var whiffing = DamagePrediction.Tempo(new SwingTempo(Hits: 0, Misses: 12));

        Assert.Equal(DamagePrediction.TempoReading.NoEvidence, nothingYet.Reading);
        Assert.Equal(DamagePrediction.TempoReading.Landing, whiffing.Reading);
        Assert.NotEqual(nothingYet.Reading, whiffing.Reading);

        // And it is a distinct reading rather than a dash that happens to be very sparse - a renderer
        // branching on the state cannot fall through to a density treatment.
        Assert.Equal(0f, nothingYet.Dot);
        Assert.Equal(0f, nothingYet.Gap);
    }

    [Fact]
    public void OneLandedBlow_IsAlreadyAMeasurement()
    {
        // The state ends the moment anything is swung, hit or miss - that is what makes it "no evidence
        // yet" rather than "doing badly".
        Assert.Equal(DamagePrediction.TempoReading.Landing,
            DamagePrediction.Tempo(new SwingTempo(Hits: 0, Misses: 1)).Reading);
        Assert.Equal(DamagePrediction.TempoReading.Fluent,
            DamagePrediction.Tempo(new SwingTempo(Hits: 1, Misses: 0)).Reading);
    }

    [Fact]
    public void TheDashClosesUpMonotonically_AsMoreBlowsLand()
    {
        // Between the two ends the gap only ever shrinks and the dot only ever grows, so the border
        // reads as one continuous "how fast is this going" and never doubles back.
        (float Dot, float Gap)? previous = null;
        for (var hits = 0; hits <= 6; hits++)
        {
            var tempo = DamagePrediction.Tempo(new SwingTempo(hits, 10 - hits));
            Assert.Equal(DamagePrediction.TempoReading.Landing, tempo.Reading);
            if (previous is { } prior)
            {
                Assert.True(tempo.Gap <= prior.Gap);
                Assert.True(tempo.Dot >= prior.Dot);
            }
            previous = (tempo.Dot, tempo.Gap);
        }
    }

    [Fact]
    public void TheIncomingTempoIsARatioOfSums_NotAnAverageOfRates()
    {
        // A rat that has swung twice and landed both, pooled with an ogre that has swung forty times and
        // landed four. Averaging the two rates gives 55%; the honest pooled rate is 6 of 42.
        var pooled = new SwingTempo(2, 0).Plus(new SwingTempo(4, 36));

        Assert.Equal(6, pooled.Hits);
        Assert.Equal(42, pooled.Attempts);
        Assert.Equal(6.0 / 42.0, pooled.HitRate, 6);
    }

    [Fact]
    public void TheTwoBandsAreRoutinelyCoincidentInAngle_SoHueCannotBeWhatSeparatesThem()
    {
        // Why the renderer gives each blow its own radial lane. Whenever the pool has no upper bound -
        // a first encounter, or a species fought but never killed - both bands start at the boundary
        // itself and the nearer one lies entirely inside the further one. Sharing a lane, they would be
        // one smudge distinguishable only by colour.
        var blow = new DamageBracket(15, 19);
        var dealt = new DamageBracket(60, 60);

        var next = DamagePrediction.AfterBlows(1, HalfGone, null, blow, dealt)!.Value;
        var after = DamagePrediction.AfterBlows(2, HalfGone, null, blow, dealt)!.Value;

        Assert.Equal(next.Low, after.Low, 6);
        Assert.True(after.High > next.High);

        var nextArc = StaminaSeal.BandArc(next)!.Value;
        var afterArc = StaminaSeal.BandArc(after)!.Value;
        Assert.Equal(nextArc.StartDegrees, afterArc.StartDegrees, 3);
    }

    // -- the player's own prediction bands ----------------------------------------

    [Fact]
    public void ThePlayersBandsAreTighterThanAnyOpponents_BecauseTheDenominatorIsKnown()
    {
        // The point of having a separate entry point at all. Same blow range against the same nominal
        // pool: the opponent's divides by an INFERRED interval and widens before the blow's own spread
        // is applied, the player's divides by a figure MUD2 prints.
        var blow = new DamageBracket(10, 14);

        var mine = DamagePrediction.PlayerAfterBlows(1, current: 60, max: 100, blow)!.Value;
        var theirs = DamagePrediction.AfterBlows(
            1, new VitalityBand(0.6, 0.6, VitalityBasis.Rung), Pool(96, 100), blow,
            new DamageBracket(40, 40))!.Value;

        Assert.True(mine.StepsWide < theirs.StepsWide);
        // Exactly the blow's own spread over the maximum, and nothing else.
        Assert.Equal((14 - 10) / 100.0, mine.High - mine.Low, 9);
    }

    [Fact]
    public void ThePlayersBandSitsAheadOfTheirOwnBoundary()
    {
        var blow = new DamageBracket(10, 14);
        var next = DamagePrediction.PlayerAfterBlows(1, 60, 100, blow)!.Value;
        var after = DamagePrediction.PlayerAfterBlows(2, 60, 100, blow)!.Value;

        // Boundary at 40% spent; one blow of 10-14 puts it at 50-54%, two at 60-68%.
        Assert.Equal(0.50, next.Low, 9);
        Assert.Equal(0.54, next.High, 9);
        Assert.Equal(0.60, after.Low, 9);
        Assert.Equal(0.68, after.High, 9);
        Assert.True(after.Low > next.Low);
    }

    [Fact]
    public void ThePlayersBandNeverRunsPastDead()
    {
        var band = DamagePrediction.PlayerAfterBlows(2, current: 9, max: 100, new DamageBracket(30, 40))!.Value;

        Assert.InRange(band.Low, 0.0, 1.0);
        Assert.Equal(1.0, band.High, 9);
    }

    [Fact]
    public void NoPlayerBandWithoutTheInputs()
    {
        var blow = new DamageBracket(10, 14);
        Assert.Null(DamagePrediction.PlayerAfterBlows(1, null, 100, blow));
        Assert.Null(DamagePrediction.PlayerAfterBlows(1, 60, null, blow));
        Assert.Null(DamagePrediction.PlayerAfterBlows(1, 60, 0, blow));
        Assert.Null(DamagePrediction.PlayerAfterBlows(1, 0, 100, blow));
        Assert.Null(DamagePrediction.PlayerAfterBlows(1, 60, 100, null));
        Assert.Null(DamagePrediction.PlayerAfterBlows(0, 60, 100, blow));
        // ...and with everything present it does produce one, so the nulls above are the guards firing.
        Assert.NotNull(DamagePrediction.PlayerAfterBlows(1, 60, 100, blow));
    }

    [Fact]
    public void IncomingPerBlowPrefersThisFight_AndIsTypicalToWorst()
    {
        // An empirical range, not a printed one: incoming damage arrives exact, so there is no bracket
        // to echo and this summarises the distribution instead.
        var history = new DamageProfile(Samples: 10, Max: 6, Sum: 40);      // avg 4, worst 6
        var thisFight = new DamageProfile(Samples: 3, Max: 19, Sum: 45);    // avg 15, worst 19

        var live = DamagePrediction.IncomingPerBlow(thisFight, history)!.Value;
        Assert.Equal(15, live.Low, 9);
        Assert.Equal(19, live.High, 9);

        var opening = DamagePrediction.IncomingPerBlow(DamageProfile.Empty, history)!.Value;
        Assert.Equal(4, opening.Low, 9);
        Assert.Equal(6, opening.High, 9);

        Assert.Null(DamagePrediction.IncomingPerBlow(DamageProfile.Empty, DamageProfile.Empty));
    }

    // -- the ring drains from Fit toward dead ------------------------------------

    [Fact]
    public void TheRingDrains_SoAFullCreatureHasItsBoundaryAtTheOrigin()
    {
        // The boundary is where what has been taken off meets what is left, on a 0-to-360 axis.
        // The screen mapping lives in CombatRailView.DrawVitalityBar, which no test can reach and
        // which turns a boundary back into a remaining-fraction as (360 - start) / 360; what is
        // pinnable here is that the axis runs 0 at full to 360 at dead, which is what every other
        // angle on the panel is measured against.
        Assert.Equal(0f, StaminaSeal.BoundaryFor(1.0), 3);
        Assert.Equal(360f, StaminaSeal.BoundaryFor(0.0), 3);
        Assert.Equal(180f, StaminaSeal.BoundaryFor(0.5), 3);
        Assert.Equal(90f, StaminaSeal.BoundaryFor(0.75), 3);
        Assert.Equal(270f, StaminaSeal.BoundaryFor(0.25), 3);
    }

    [Fact]
    public void TheLitRunEndsAtDead_AndTheFadeRunsBackTowardTheOrigin()
    {
        var plan = StaminaSeal.Plan(new VitalityBand(3 * Seventh, 4 * Seventh, VitalityBasis.Rung), null, null);

        // Certainly still has three sevenths, so the last three sevenths of the ring are solid.
        Assert.Equal(360f, plan.LitHard!.Value.EndDegrees, 3);
        Assert.Equal(StaminaSeal.BoundaryFor(3 * Seventh), plan.LitHard.Value.StartDegrees, 3);

        // The uncertain seventh sits immediately BEHIND it on the axis, between the two boundaries -
        // that is, on the spent side, which is where the doubt belongs.
        Assert.Equal(StaminaSeal.BoundaryFor(4 * Seventh), plan.LitSoft!.Value.StartDegrees, 3);
        Assert.Equal(plan.LitHard.Value.StartDegrees, plan.LitSoft.Value.EndDegrees, 3);
        Assert.Equal(plan.LitSoft.Value.StartDegrees, plan.Boundary, 3);
    }

    [Fact]
    public void ThePredictionsSitAheadOfTheBoundary_InTheDirectionItTravels()
    {
        var vitality = new VitalityBand(3 * Seventh, 4 * Seventh, VitalityBasis.Rung);
        var pool = Pool(96, 100);
        var blow = new DamageBracket(15, 19);

        var plan = StaminaSeal.Plan(
            vitality,
            DamagePrediction.AfterBlows(1, vitality, pool, blow, DamageBracket.Zero),
            DamagePrediction.AfterBlows(2, vitality, pool, blow, DamageBracket.Zero));

        Assert.NotNull(plan.NextBlow);
        Assert.NotNull(plan.BlowAfter);
        // Ahead of the boundary, and the second ahead of the first.
        Assert.True(plan.NextBlow!.Value.StartDegrees >= plan.Boundary);
        Assert.True(plan.BlowAfter!.Value.StartDegrees > plan.NextBlow.Value.StartDegrees);
    }

    [Fact]
    public void AnUnmetCreatureHasNoPredictions_HoweverGoodTheBrackets()
    {
        // Nothing to anchor them to. The ring says "?" and the lane beside it stays empty rather than
        // predicting against an imagined position.
        var plan = StaminaSeal.Plan(null, new DamageBand(0.2, 0.3), new DamageBand(0.4, 0.5));

        Assert.Equal(SealShape.Unmet, plan.Shape);
        Assert.Null(plan.NextBlow);
        Assert.Null(plan.BlowAfter);
    }

    // -- aggregating a pack ------------------------------------------------------

    private static RosterRow Row(string name, bool live, double reach, int blows = 20)
        => new(name, live, IsCurrentTarget: false, FightOutcome.Unresolved, Reach: new ReachMark(reach, blows));

    // There was a test here asserting that a RosterRow built with IsLive false reports IsLive
    // false. IsLive is a positional member of a readonly record struct, so it read back exactly
    // what the test supplied and no derivation was invoked. What actually derives it from
    // ParticipantFact.IsResolved is ParticipantRoster.Build, and
    // ParticipantRosterTests.Build_LiveParticipantsSortBeforeResolvedOnes is what catches Build
    // marking every row live.

    // -- group fights: which one is the greatest threat --------------------------
    //
    // GreatestThreat picks the ONE live opponent CombatFrameComposer.IncomingPerBlowOf projects the
    // player's own prediction bands from. GreatestThreat and the RosterRow.Reach data behind it are
    // load-bearing for those prediction bands, and CombatRailView's accent frame draws this same
    // selection on screen too, via GreatestThreatForAccent below.

    [Fact]
    public void GreatestThreat_IsTheRowWithTheLargestReachMark()
    {
        var plan = new RosterPlan(
            [Row("rat0", true, 7), Row("ogre1", true, 26), Row("rat2", true, 7)],
            LiveCount: 3, ResolvedCount: 0, HiddenCount: 0, HiddenLiveCount: 0);

        Assert.Equal(1, ReachAggregate.GreatestThreat(plan));
        Assert.Equal(26, plan.Rows[ReachAggregate.GreatestThreat(plan)].Reach.ReachesAtLeast, 3);
    }

    [Fact]
    public void GreatestThreat_BreaksASpeciesTieOnDamageActuallyTaken()
    {
        // Reach is per-species, so a pack of rats ties on it outright. What separates them is what each
        // one has actually done to the player this encounter - per-instance and measured.
        var rows = new[]
        {
            Row("rat0", true, 7) with { DamageTakenFrom = 4 },
            Row("rat1", true, 7) with { DamageTakenFrom = 19 },
            Row("rat2", true, 7) with { DamageTakenFrom = 11 },
        };
        var plan = new RosterPlan(rows, LiveCount: 3, ResolvedCount: 0, HiddenCount: 0, HiddenLiveCount: 0);

        Assert.Equal(1, ReachAggregate.GreatestThreat(plan));
    }

    [Fact]
    public void GreatestThreat_IgnoresTheDeadAndTheUnmeasured()
    {
        // The dead ogre carries the largest mark, so a green result means the live guard actually ran.
        var plan = new RosterPlan(
            [
                Row("ogre0", live: false, 26),
                new RosterRow("rat1", IsLive: true, IsCurrentTarget: true, FightOutcome.Unresolved),
                Row("rat2", true, 7),
            ],
            LiveCount: 2, ResolvedCount: 1, HiddenCount: 0, HiddenLiveCount: 0);

        Assert.Equal(2, ReachAggregate.GreatestThreat(plan));
    }

    [Fact]
    public void GreatestThreat_NeverSelectsAnUnknownBadge()
    {
        // The badge carries the WORD's record - several Creatures' blows pooled - and the largest
        // mark here, so a green result means the badge guard ran and the accent went to a Creature.
        var badge = Row(AnonymousOpponent.Thing, true, 30) with { SlotSpan = 2, UnseenLabel = "rat3, ???" };
        var plan = new RosterPlan(
            [Row("rat0", true, 7), badge, Row("ogre1", true, 26)],
            LiveCount: 4, ResolvedCount: 0, HiddenCount: 0, HiddenLiveCount: 0);

        Assert.Equal(2, ReachAggregate.GreatestThreat(plan));

        var badgeOnly = new RosterPlan([badge], LiveCount: 2, ResolvedCount: 0, HiddenCount: 0, HiddenLiveCount: 0);
        Assert.Equal(-1, ReachAggregate.GreatestThreat(badgeOnly));
    }

    [Fact]
    public void GreatestThreat_IsMinusOne_WhenNothingLiveHasAMark()
    {
        var plan = new RosterPlan(
            [new RosterRow("rat1", IsLive: true, IsCurrentTarget: true, FightOutcome.Unresolved)],
            LiveCount: 1, ResolvedCount: 0, HiddenCount: 0, HiddenLiveCount: 0);

        Assert.Equal(-1, ReachAggregate.GreatestThreat(plan));
        Assert.Equal(-1, ReachAggregate.GreatestThreat(RosterPlan.Empty));
    }

    [Fact]
    public void GreatestThreat_ExactTie_KeepsTheFirstRowFound()
    {
        // Reach AND damage taken both tied exactly - neither comparison in the loop is a strict
        // "greater than", so the incumbent (the earliest-indexed live row with evidence) is never
        // displaced by an equal challenger. Arbitrary, but STABLE across repeated calls on the same
        // roster, which is what a selection re-run every refresh actually needs - see this class's own
        // remarks on GreatestThreat.
        var rows = new[]
        {
            Row("rat0", true, 7) with { DamageTakenFrom = 4 },
            Row("rat1", true, 7) with { DamageTakenFrom = 4 },
        };
        var plan = new RosterPlan(rows, LiveCount: 2, ResolvedCount: 0, HiddenCount: 0, HiddenLiveCount: 0);

        Assert.Equal(0, ReachAggregate.GreatestThreat(plan));
        // And repeatable - not just correct once.
        Assert.Equal(0, ReachAggregate.GreatestThreat(plan));
    }

    // -- the accent's own threshold: at least two live opponents ------------------

    [Fact]
    public void GreatestThreatForAccent_IsMinusOne_BelowTwoLiveOpponents()
    {
        // One live opponent is trivially "the greatest threat" - there is nothing else it could be -
        // so the accent (CombatRailView.FrameAccent) has nothing to single out from a pack of one, even
        // though GreatestThreat itself would happily return a row.
        var plan = new RosterPlan(
            [Row("rat0", true, 26)],
            LiveCount: 1, ResolvedCount: 0, HiddenCount: 0, HiddenLiveCount: 0);

        Assert.True(ReachAggregate.GreatestThreat(plan) >= 0);
        Assert.Equal(-1, ReachAggregate.GreatestThreatForAccent(plan));
    }

    [Fact]
    public void GreatestThreatForAccent_MatchesGreatestThreat_AtTwoOrMoreLiveOpponents()
    {
        var plan = new RosterPlan(
            [Row("rat0", true, 7), Row("ogre1", true, 26), Row("rat2", true, 7)],
            LiveCount: 3, ResolvedCount: 0, HiddenCount: 0, HiddenLiveCount: 0);

        Assert.Equal(ReachAggregate.GreatestThreat(plan), ReachAggregate.GreatestThreatForAccent(plan));
        Assert.Equal(1, ReachAggregate.GreatestThreatForAccent(plan));
    }

    [Fact]
    public void GreatestThreatForAccent_IsMinusOne_WithTwoLiveButNeitherMeasured()
    {
        // Two live opponents, but LiveCount alone is not the whole gate - GreatestThreat's own "no
        // evidence" answer still has to come through unchanged.
        var plan = new RosterPlan(
            [
                new RosterRow("rat0", IsLive: true, IsCurrentTarget: true, FightOutcome.Unresolved),
                new RosterRow("rat1", IsLive: true, IsCurrentTarget: false, FightOutcome.Unresolved),
            ],
            LiveCount: 2, ResolvedCount: 0, HiddenCount: 0, HiddenLiveCount: 0);

        Assert.Equal(-1, ReachAggregate.GreatestThreatForAccent(plan));
    }
}
