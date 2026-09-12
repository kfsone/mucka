using MudSharp.Combat;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// The censored-interval stamina-pool estimator: the constraint arithmetic, the re-engagement filter,
/// and the max-depth intersection that keeps one contaminated fight from emptying the answer.
///
/// <para>These pin the ARITHMETIC. The evidence behind the model - equal-seventh rungs tested on 1,978
/// readings, a 21% high bias in the median-kill-damage estimator this replaced, re-engagement at
/// P=0.80 against a 0.05 baseline - is recorded on <see cref="StaminaPoolEstimator"/> itself. A failure
/// here means the code changed, not that the game did.</para>
/// </summary>
public sealed class StaminaPoolEstimatorTests
{
    private const long Minute = 60_000;

    private static PoolFightObservation Fight(
        string name = "rat0",
        long startedAtMs = 0,
        long endedAtMs = 10_000,
        bool kill = false,
        (double Low, double High)[]? blows = null,
        (int Rung, int After)[]? rungs = null,
        long? encounter = null,
        int? staStart = null,
        int? staEnd = null,
        int? staMax = null,
        string? weapon = null)
        => new(
            name, startedAtMs, endedAtMs, kill,
            (blows ?? []).Select(b => new DamageBracket(b.Low, b.High)).ToArray(),
            (rungs ?? []).Select(r => new RungReading(r.Rung, r.After)).ToArray(),
            encounter,
            new PlayerFightState(staStart, staEnd, staMax, weapon));

    /// <summary>A fight that leaves the player exactly as it found them - the state a chase presents.
    /// Depleted, same weapon, same maximum.</summary>
    private static PoolFightObservation Chased(
        long startedAtMs, long endedAtMs, bool kill = false,
        (double Low, double High)[]? blows = null, long? encounter = null, string name = "rat0")
        => Fight(name, startedAtMs, endedAtMs, kill, blows ?? [(4, 8)],
            encounter: encounter, staStart: 40, staEnd: 38, staMax: 100, weapon: "axe0");

    // -- Family 3: the multi-rung span ------------------------------------------

    /// <summary>Drove the creature from "fit" down to "covered in wounds" and did not kill it.</summary>
    private static PoolFightObservation DownThreeRungsNoKill() => Fight(
        blows: [(1, 1), (20, 29)],
        rungs: [(7, 1), (4, 2)]);

    /// <summary>The same total damage against a creature it barely scratched, and no kill.</summary>
    private static PoolFightObservation DownOneRungNoKill() => Fight(
        blows: [(1, 1), (20, 29)],
        rungs: [(7, 1), (6, 2)]);

    [Fact]
    public void AFightThatNeverEndedInAKill_CanNowCeilingThePool()
    {
        // The capability this family actually adds. Before it, only a KILL could close a pool band from
        // above - a fight the creature survived could floor the estimate and nothing more, which is what
        // PoolEvidence.LowerBoundOnly is for. Driving it down three rungs for 21-30 says the whole
        // creature is worth at most 7 x 29 / (3-1) = 101.5, with no kill anywhere in the record.
        var (deep, _) = StaminaPoolEstimator.BoundFor(DownThreeRungsNoKill());
        var (shallow, _) = StaminaPoolEstimator.BoundFor(DownOneRungNoKill());

        Assert.NotNull(deep);
        Assert.Equal(101.5, deep!.Value.AtMost!.Value, 6);

        // The same damage that only moved it one rung says nothing about its size - the (N-1) term
        // vanishes. What separates these two is how far down the ladder the damage drove it, and the
        // floors differ for the same reason: a creature still reading "superficially injured" after
        // 21 points must be bigger than one already at "covered in wounds".
        Assert.NotNull(shallow);
        Assert.Null(shallow!.Value.AtMost);
        Assert.True(shallow.Value.Above > deep.Value.Above);
    }

    [Fact]
    public void TheSpanCeilingSurvivesToTheSpeciesEstimate_AsARealEvidenceUpgrade()
    {
        // Not just a per-fight interval: it moves the species from "somewhere above 36.75" to a band,
        // off fights that never ended in a kill.
        var deep = StaminaPoolEstimator.Estimate("rat", [DownThreeRungsNoKill()]);
        var shallow = StaminaPoolEstimator.Estimate("rat", [DownOneRungNoKill()]);

        Assert.Equal(PoolEvidence.Band, deep.Evidence);
        Assert.Equal(36.75, deep.Interval.Above, 6);
        Assert.Equal(101.5, deep.Interval.AtMost!.Value, 6);

        Assert.Equal(PoolEvidence.LowerBoundOnly, shallow.Evidence);
        Assert.Null(shallow.Interval.AtMost);
    }

    [Fact]
    public void BlowGranularityDoesNotChangeThePoolCeiling_OnlyTheLadderPosition()
    {
        // One blow carrying the creature down three rungs and three blows carrying it down one each
        // reach the same place for the same damage, and the pair spanning the whole fall says exactly
        // the same thing in both - 7 x 29 / 2 = 101.5.
        //
        // Fine-grained blows DO buy something, but it is position rather than size: see
        // NpcVitality's crossing constraint, where a 1-4 span pins the creature to a fifth of a rung and
        // a 20-29 span pins it to most of one.
        var oneBigBlow = StaminaPoolEstimator.BoundFor(DownThreeRungsNoKill()).Bound;
        var threeSmallBlows = StaminaPoolEstimator.BoundFor(Fight(
            blows: [(1, 1), (7, 10), (7, 10), (6, 9)],
            rungs: [(7, 1), (6, 2), (5, 3), (4, 4)])).Bound;

        Assert.Equal(oneBigBlow!.Value.AtMost!.Value, threeSmallBlows!.Value.AtMost!.Value, 6);
        Assert.Equal(101.5, threeSmallBlows.Value.AtMost!.Value, 6);
    }

    [Fact]
    public void TheSweepTakesNonAdjacentPairs_WhichAreSometimesTheTighterOnes()
    {
        // A single-rung reading interrupting a longer fall. Adjacent pairs give only the 6-to-3 step:
        // 50 high over three rungs, 7 x 50 / 2 = 175. The pair straddling the interruption is four rungs
        // over 55, 7 x 55 / 3 = 128.3, and it is strictly tighter. Both are sound - the adjacent-only
        // sweep was leaving the tighter one unclaimed rather than reporting a wrong one.
        var (bound, _) = StaminaPoolEstimator.BoundFor(Fight(
            blows: [(5, 5), (50, 50)],
            rungs: [(7, 0), (6, 1), (3, 2)]));

        Assert.NotNull(bound);
        Assert.Equal(7 * 55 / 3.0, bound!.Value.AtMost!.Value, 6);
        Assert.True(bound.Value.AtMost!.Value < 175.0);
    }

    [Fact]
    public void ASingleRungDrop_BoundsNothingFromAbove()
    {
        // N=1 makes the (N-1) term vanish. A blow that crosses one boundary says where the creature is,
        // not how big it is - that is what NpcVitality's crossing constraint is for.
        var (bound, _) = StaminaPoolEstimator.BoundFor(
            Fight(blows: [(1, 1), (20, 29)], rungs: [(7, 1), (6, 2)]));

        Assert.NotNull(bound);
        Assert.Null(bound!.Value.AtMost);
    }

    [Fact]
    public void ARungThatRISES_BoundsNothing()
    {
        // Creatures regenerate; the corpus has a zombie oscillating four times in one fight. A reading
        // going back up is not a drop and must not be read as one.
        var (bound, _) = StaminaPoolEstimator.BoundFor(
            Fight(blows: [(1, 1), (20, 29)], rungs: [(4, 1), (7, 2)]));

        Assert.NotNull(bound);
        Assert.Null(bound!.Value.AtMost);
    }

    [Fact]
    public void TheSpanCeilingIsImmuneToPreDamage()
    {
        // The claim that makes this bound admissible where the static rung ceiling is not. Two fights
        // with identical blows and an identical three-rung drop - one against a creature met fresh
        // (7 down to 4), one against a creature that walked in already hurt (4 down to 1). A rung is
        // pool/7 wide either way, so the ceiling has to come out the same.
        var fresh = StaminaPoolEstimator.BoundFor(
            Fight(blows: [(1, 1), (20, 29)], rungs: [(7, 1), (4, 2)])).Bound;
        var preDamaged = StaminaPoolEstimator.BoundFor(
            Fight(blows: [(1, 1), (20, 29)], rungs: [(4, 1), (1, 2)])).Bound;

        Assert.Equal(fresh!.Value.AtMost!.Value, preDamaged!.Value.AtMost!.Value, 6);
    }

    [Fact]
    public void TheTightestSpanInAFightWins()
    {
        // Several multi-rung drops in one fight are several ceilings on one quantity, so the smallest
        // is the binding one - the mirror of family 2 taking the largest floor.
        var (bound, _) = StaminaPoolEstimator.BoundFor(Fight(
            blows: [(1, 1), (20, 29), (1, 1), (8, 10)],
            rungs: [(7, 1), (5, 2), (3, 4)]));

        // First span: 29 high over a 2-rung drop -> 7 x 29 / 1 = 203.
        // Second span: 11 high over a 2-rung drop -> 7 x 11 / 1 = 77.
        Assert.NotNull(bound);
        Assert.Equal(77.0, bound!.Value.AtMost!.Value, 6);
    }

    [Fact]
    public void ASpanCeilingBelowTheKillFloor_IsDroppedRatherThanEmptyingTheFight()
    {
        // A landed blow whose bracket never reached the ledger understates the damage behind a span and
        // can drive its ceiling under the kill bracket's own floor. The kill bracket is arithmetic over
        // what the game printed and has no model in it, so it wins and the span goes - rather than the
        // pair cancelling out and the fight constraining nothing at all.
        var (bound, _) = StaminaPoolEstimator.BoundFor(Fight(
            kill: true,
            blows: [(40, 45), (40, 45), (1, 2)],
            rungs: [(7, 1), (2, 2)]));

        // The span would say 7 x 45 / 4 = 78.75, below the kill floor of 80.
        Assert.NotNull(bound);
        Assert.Equal(80.0, bound!.Value.Above, 6);
        Assert.Equal(92.0, bound.Value.AtMost!.Value, 6);
    }

    [Fact]
    public void TheSpanCeilingComposesWithTheMaxDepthRun_RatherThanShortCircuitingIt()
    {
        // Three fights, two of which agree on a tight span ceiling and one contaminated outlier that
        // does not. The sweep still picks the interval the most fights cover, and the ceiling it
        // reports is the agreeing one - the span is a constraint like any other, not a veto.
        var agreeing = Fight(
            name: "rat0", startedAtMs: 0, endedAtMs: 10_000,
            blows: [(1, 1), (20, 29)], rungs: [(7, 1), (4, 2)]);
        var alsoAgreeing = Fight(
            name: "rat1", startedAtMs: 10 * Minute, endedAtMs: 10 * Minute + 10_000,
            blows: [(1, 1), (20, 28)], rungs: [(7, 1), (4, 2)]);
        var outlier = Fight(
            name: "rat2", startedAtMs: 20 * Minute, endedAtMs: 20 * Minute + 10_000,
            blows: [(1, 1), (60, 70)], rungs: [(7, 1), (4, 2)]);

        var estimate = StaminaPoolEstimator.Estimate("rat", [agreeing, alsoAgreeing, outlier]);

        Assert.Equal(3, estimate.ContributingFights);
        Assert.Equal(2, estimate.SupportingFights);
        Assert.Equal(PoolEvidence.Band, estimate.Evidence);
        // The two agreeing fights cap at 7 x 29 / 2 = 101.5 and 7 x 28 / 2 = 98; their shared run ends
        // at the tighter of the two.
        Assert.Equal(98.0, estimate.Interval.AtMost!.Value, 6);
    }

    // -- Family 2: the rung constraint --------------------------------------------

    [Fact]
    public void RungSevenAfterTwoBlowsFloorsThePoolAtSevenTimesTheBracketLow()
    {
        // The worked example. Two 20-29 blows have landed, the creature still reads "fit": the
        // cumulative LOW is 40, rung 7's multiplier is 7/(8-7) = 7, so the pool exceeds 280.
        var (bound, contradicted) = StaminaPoolEstimator.BoundFor(
            Fight(blows: [(20, 29), (20, 29)], rungs: [(7, 2)]));

        Assert.False(contradicted);
        Assert.Equal(280, bound!.Value.Above, 6);
        Assert.Null(bound.Value.AtMost);
        Assert.False(bound.Value.Contains(280));    // strictly greater, not "at least"
        Assert.True(bound.Value.Contains(281));
    }

    [Theory]
    // pool > D x 7/(8-k). D = 70 throughout, so the multiplier is the whole test.
    [InlineData(7, 490)]    // 7/1
    [InlineData(6, 245)]    // 7/2
    [InlineData(5, 163.333333)]
    [InlineData(4, 122.5)]
    [InlineData(3, 98)]
    [InlineData(2, 81.666667)]
    [InlineData(1, 70)]     // 7/7 - the same as simply having survived the damage
    public void EveryRungUsesTheEqualSeventhsMultiplier(int rung, double expectedFloor)
    {
        var (bound, _) = StaminaPoolEstimator.BoundFor(
            Fight(blows: [(70, 90)], rungs: [(rung, 1)]));
        Assert.Equal(expectedFloor, bound!.Value.Above, 4);
    }

    [Fact]
    public void TheStrongestRungReadingWinsAndTheReadingUsesTheBracketLow()
    {
        // Three readings; the last is the one with the most damage behind it at the same rung, so it
        // is the binding one. Lows only - 10+10+10 = 30, times 7 = 210. Using the highs (45) would
        // give 315 and claim a pool nobody has evidence for.
        var (bound, _) = StaminaPoolEstimator.BoundFor(
            Fight(blows: [(10, 15), (10, 15), (10, 15)], rungs: [(7, 1), (7, 2), (7, 3)]));

        Assert.Equal(210, bound!.Value.Above, 6);
    }

    [Fact]
    public void RungReadingsOutsideTheScaleOrBeforeAnyBlowAreIgnored()
    {
        var (bound, _) = StaminaPoolEstimator.BoundFor(
            Fight(blows: [(10, 15)], rungs: [(0, 1), (8, 1), (7, 0)]));

        // Only the survivor floor is left: it took at least 10 and lived.
        Assert.Equal(10, bound!.Value.Above, 6);
    }

    // -- Family 1: the kill constraint --------------------------------------------

    [Fact]
    public void AKillBracketsThePoolBetweenTheSurvivedLowsAndEveryHigh()
    {
        // Alive after two blows, dead on the third: pool > (10+10) and pool <= (14+14+14).
        var (bound, contradicted) = StaminaPoolEstimator.BoundFor(
            Fight(kill: true, blows: [(10, 14), (10, 14), (10, 14)]));

        Assert.False(contradicted);
        Assert.Equal(20, bound!.Value.Above, 6);
        Assert.Equal(42, bound.Value.AtMost!.Value, 6);
        Assert.False(bound.Value.Contains(20));
        Assert.True(bound.Value.Contains(21));
        Assert.True(bound.Value.Contains(42));      // inclusive at the top
        Assert.False(bound.Value.Contains(43));
    }

    [Fact]
    public void AOneBlowKillOnlyCapsThePool()
    {
        var (bound, _) = StaminaPoolEstimator.BoundFor(Fight(kill: true, blows: [(5, 9)]));
        Assert.Equal(0, bound!.Value.Above, 6);
        Assert.Equal(9, bound.Value.AtMost!.Value, 6);
    }

    [Fact]
    public void ASurvivorOnlyFloorsThePoolAndNeverCapsIt()
    {
        // The censoring that made the old median-of-kill-damage estimator exclude non-kills entirely.
        // Here the observation is kept, as the half-line it actually is.
        var (bound, _) = StaminaPoolEstimator.BoundFor(Fight(blows: [(10, 14), (10, 14)]));

        Assert.Equal(20, bound!.Value.Above, 6);
        Assert.Null(bound.Value.AtMost);
    }

    [Fact]
    public void AFightWithNoParsedBlowsConstrainsNothing()
    {
        // Narrative mode: MUD2 prints no figure at all, so there is no bracket to sum.
        var (bound, _) = StaminaPoolEstimator.BoundFor(Fight(kill: true));
        Assert.Null(bound);
    }

    [Fact]
    public void AKillWhoseRungReadingDemandsMoreThanTheKillAllowsKeepsTheKillBracket()
    {
        // The pre-damaged case: it read "fit" after 40 low damage (floor 280) yet died to a total of
        // at most 60. The kill bracket is arithmetic over what the game printed; the rung bound also
        // assumes the creature started this fight undamaged, which is exactly what a re-engagement
        // breaks. So the kill survives and the rung goes.
        var (bound, contradicted) = StaminaPoolEstimator.BoundFor(
            Fight(kill: true, blows: [(20, 29), (20, 29), (0, 2)], rungs: [(7, 2)]));

        Assert.True(contradicted);
        Assert.Equal(40, bound!.Value.Above, 6);        // survived the first two blows' lows
        Assert.Equal(60, bound.Value.AtMost!.Value, 6);
    }

    // -- chase linking ------------------------------------------------------------

    [Fact]
    public void AChaseIsLinkedIntoOneObservation_NotDropped()
    {
        // Linking must preserve both fights: the finishing link is the only two-sided constraint
        // a fight can produce, so it must not be the one dropped.
        var (chains, linked, dropped) = ChaseLinker.Link(new[]
        {
            Chased(0, 10_000, blows: [(4, 8), (4, 8)]),
            Chased(14_000, 20_000, kill: true, blows: [(5, 9)]),
        });

        Assert.Equal(1, linked);
        Assert.Equal(0, dropped);
        var chain = Assert.Single(chains);

        // One observation against one pool: every blow, the last link's ending.
        Assert.Equal(3, chain.Blows.Count);
        Assert.True(chain.EndedInKill);
        Assert.Equal(0, chain.StartedAtMs);
        Assert.Equal(20_000, chain.EndedAtMs);
    }

    [Fact]
    public void ALinkedChainYieldsACeilingWhereTheOldFilterYieldedNone()
    {
        // The measured gain, as an assertion. Alone, the opening fight is a survivor and floors the pool
        // and nothing more; linked to the kill that ended it, the chain brackets it from both sides.
        var opening = Chased(0, 10_000, blows: [(4, 8), (4, 8)]);
        var finish = Chased(14_000, 20_000, kill: true, blows: [(5, 9)]);

        var alone = StaminaPoolEstimator.Estimate("rat", [opening]);
        Assert.Equal(PoolEvidence.LowerBoundOnly, alone.Evidence);
        Assert.Null(alone.Interval.AtMost);

        var chained = StaminaPoolEstimator.Estimate("rat", [opening, finish]);
        Assert.Equal(PoolEvidence.Band, chained.Evidence);
        // Alive after the first two blows' lows (8), dead by the sum of every blow's high (25).
        Assert.Equal(8, chained.Interval.Above, 6);
        Assert.Equal(25, chained.Interval.AtMost!.Value, 6);
        Assert.Equal(1, chained.LinkedEngagements);
    }

    [Fact]
    public void RungReadingsAreReIndexedOntoTheCombinedBlowSequence()
    {
        // A reading credited with "two blows before it" in the second link happened after everything the
        // first link landed as well. Left un-offset it would understate the damage behind the reading
        // and so understate the floor.
        var (chains, _, _) = ChaseLinker.Link(new[]
        {
            Chased(0, 10_000, blows: [(4, 8), (4, 8)]),
            Fight("rat0", 14_000, 20_000, blows: [(5, 9), (5, 9)], rungs: [(4, 2)],
                staStart: 38, staEnd: 36, staMax: 100, weapon: "axe0"),
        });

        var chain = Assert.Single(chains);
        var reading = Assert.Single(chain.Rungs);
        Assert.Equal(4, reading.LandedBlowsBefore);
    }

    [Fact]
    public void AKillEndsTheChainAbsolutely()
    {
        // Whatever answers to the name afterwards is a different creature - a respawn, or a packmate
        // sharing an unnumbered name. Linking across it would add one creature's damage to another.
        var (chains, linked, _) = ChaseLinker.Link(new[]
        {
            Chased(0, 10_000, kill: true),
            Chased(14_000, 20_000, kill: true),
        });

        Assert.Equal(0, linked);
        Assert.Equal(2, chains.Count);
    }

    [Fact]
    public void RecoveringSubstantiallyBetweenFightsIsAFreshAttempt_NotAChase()
    {
        // Too low on stamina, leave, sleep, come back: the gap can be short and it is still not a
        // chase, because the player's state reset.
        var (chains, linked, _) = ChaseLinker.Link(new[]
        {
            Fight("banshee", 0, 10_000, staStart: 90, staEnd: 20, staMax: 100, weapon: "axe0"),
            Fight("banshee", 15_000, 25_000, staStart: 100, staEnd: 80, staMax: 100, weapon: "axe0"),
        });

        Assert.Equal(0, linked);
        Assert.Equal(2, chains.Count);
    }

    [Fact]
    public void ChangingWeaponBetweenFightsIsAFreshAttempt()
    {
        var (chains, linked, _) = ChaseLinker.Link(new[]
        {
            Fight("banshee", 0, 10_000, staStart: 40, staEnd: 38, staMax: 100, weapon: "axe0"),
            Fight("banshee", 12_000, 20_000, staStart: 38, staEnd: 30, staMax: 100, weapon: "pick1"),
        });

        Assert.Equal(0, linked);
        Assert.Equal(2, chains.Count);
    }

    [Fact]
    public void ADreamwordBetweenFightsIsAFreshAttempt()
    {
        // sta_max moved, so the player went and got stronger. That is a re-attempt by definition.
        var (chains, linked, _) = ChaseLinker.Link(new[]
        {
            Fight("banshee", 0, 10_000, staStart: 40, staEnd: 38, staMax: 100, weapon: "axe0"),
            Fight("banshee", 12_000, 20_000, staStart: 38, staEnd: 30, staMax: 115, weapon: "axe0"),
        });

        Assert.Equal(0, linked);
        Assert.Equal(2, chains.Count);
    }

    [Fact]
    public void TimeIsOnlyTheBackstop()
    {
        // Continuous player state links across a long gap up to the backstop, and stops beyond it. The
        // clock is not the test - it is what stops an unbounded chain forming when no state signal
        // fires at all.
        var withinBackstop = ChaseLinker.Link(new[]
        {
            Chased(0, 10_000),
            Chased(10_000 + ChaseLinkPolicy.BackstopMs, 110_000 + ChaseLinkPolicy.BackstopMs),
        });
        Assert.Equal(1, withinBackstop.Linked);

        var past = ChaseLinker.Link(new[]
        {
            Chased(0, 10_000),
            Chased(10_000 + ChaseLinkPolicy.BackstopMs + 1, 200_000),
        });
        Assert.Equal(0, past.Linked);
    }

    [Fact]
    public void DifferentInstancesAreNeverLinked()
    {
        // rat0 and rat3 in one pack are two rats, and both met the player at full.
        var (chains, linked, _) = ChaseLinker.Link(new[]
        {
            Chased(0, 10_000, name: "rat0"),
            Chased(11_000, 20_000, name: "rat3"),
        });

        Assert.Equal(0, linked);
        Assert.Equal(2, chains.Count);
    }

    [Fact]
    public void UnsortedInputIsSortedBeforeLinking()
    {
        var (chains, linked, _) = ChaseLinker.Link(new[]
        {
            Chased(14_000, 20_000, kill: true),
            Chased(0, 10_000),
        });

        Assert.Equal(1, linked);
        Assert.Equal(0, Assert.Single(chains).StartedAtMs);
    }

    // -- folds --------------------------------------------------------------------

    [Fact]
    public void AFoldIsDroppedEntirely_NotResolvedToItsWorstRow()
    {
        // Several creatures printed under one name inside one encounter. The first row absorbed every
        // blow landed on every one of them before the first died, which makes it the worst row in the
        // group. No blow can be attributed, so none of them counts.
        //
        // The discriminator is that an EARLIER fight in the same encounter ended in a kill: the creature
        // carrying the name is dead, so whatever answers to it next is a different one.
        var (chains, _, dropped) = ChaseLinker.Link(new[]
        {
            Fight("rat", 0, 10_000, kill: true, blows: [(4, 8), (4, 8), (4, 8)], encounter: 1),
            Fight("rat", 11_000, 20_000, kill: true, blows: [(4, 8)], encounter: 1),
        });

        Assert.Equal(2, dropped);
        Assert.Empty(chains);
    }

    [Fact]
    public void AnInEncounterChaseIsLinked_NotMistakenForAFold()
    {
        // The case that looks identical from outside: two same-name fights in ONE encounter. Here the
        // first ended CFledFail rather than in a kill, so the creature is alive and it is the same one.
        // No fold appears anywhere in the corpus; all five same-encounter same-name groups are this.
        var (chains, linked, dropped) = ChaseLinker.Link(new[]
        {
            Chased(0, 10_000, encounter: 1),
            Chased(12_000, 20_000, kill: true, encounter: 1),
        });

        Assert.Equal(0, dropped);
        Assert.Equal(1, linked);
        Assert.True(Assert.Single(chains).EndedInKill);
    }

    [Fact]
    public void AFoldDoesNotPoisonTheSameNameInOtherEncounters()
    {
        var (chains, _, dropped) = ChaseLinker.Link(new[]
        {
            Fight("rat", 0, 10_000, kill: true, blows: [(4, 8)], encounter: 1),
            Fight("rat", 11_000, 20_000, kill: true, blows: [(4, 8)], encounter: 1),
            Fight("rat", 500_000, 510_000, kill: true, blows: [(4, 8)], encounter: 2),
        });

        Assert.Equal(2, dropped);
        Assert.Equal(500_000, Assert.Single(chains).StartedAtMs);
    }

    // -- Max-depth intersection ---------------------------------------------------

    [Fact]
    public void AgreeingKillsIntersectToTheTightestBandTheyAllContain()
    {
        var estimate = StaminaPoolEstimator.Estimate("rat", new[]
        {
            Fight(name: "rat0", kill: true, blows: [(10, 14), (10, 14)]),   // (10, 28]
            Fight(name: "rat1", kill: true, blows: [(12, 15), (10, 13)]),   // (12, 28]
            Fight(name: "rat2", kill: true, blows: [(8, 12), (9, 13)]),     // (8, 25]
        });

        Assert.Equal(PoolEvidence.Band, estimate.Evidence);
        Assert.Equal(12, estimate.Interval.Above, 6);
        Assert.Equal(25, estimate.Interval.AtMost!.Value, 6);
        Assert.Equal(3, estimate.SupportingFights);
        Assert.Equal(3, estimate.ContributingFights);
        Assert.Single(estimate.TiedRuns);
        Assert.False(estimate.IsSplit);
        Assert.Equal(estimate.Interval, estimate.TiedRuns[0]);
    }

    [Fact]
    public void OneContaminatedFightDoesNotEmptyTheInterval()
    {
        // The failure the max-depth rule exists for: a hard AND of these is empty (the pre-damaged
        // kill caps the pool at 8, below every other fight's floor), and a hard intersection was
        // measured empty for 15 of 23 species groups. The band the other three agree on survives, and
        // the depth says how many agreed.
        var estimate = StaminaPoolEstimator.Estimate("rat", new[]
        {
            Fight(name: "rat0", kill: true, blows: [(10, 14), (10, 14)]),   // (10, 28]
            Fight(name: "rat1", kill: true, blows: [(12, 15), (10, 13)]),   // (12, 28]
            Fight(name: "rat2", kill: true, blows: [(11, 14), (10, 13)]),   // (11, 27]
            Fight(name: "rat9", kill: true, blows: [(6, 8)]),               // (0, 8] - arrived hurt
        });

        Assert.Equal(PoolEvidence.Band, estimate.Evidence);
        Assert.Equal(12, estimate.Interval.Above, 6);
        Assert.Equal(27, estimate.Interval.AtMost!.Value, 6);
        Assert.Equal(3, estimate.SupportingFights);
        Assert.Equal(4, estimate.ContributingFights);
    }

    [Fact]
    public void ARegenInflatedFloorAlsoFailsToEmptyTheInterval()
    {
        // Contamination is one-sided in BOTH directions at once. Here the odd fight's floor is above
        // everyone else's ceiling rather than below their floor, and the same rule holds the answer.
        var estimate = StaminaPoolEstimator.Estimate("rat", new[]
        {
            Fight(name: "rat0", kill: true, blows: [(10, 14), (10, 14)]),   // (10, 28]
            Fight(name: "rat1", kill: true, blows: [(12, 15), (10, 13)]),   // (12, 28]
            Fight(name: "rat2", kill: true, blows: [(11, 14), (10, 13)]),   // (11, 27]
            Fight(name: "rat9", blows: [(40, 50), (40, 50)]),               // survived 80 - regen
        });

        Assert.Equal(12, estimate.Interval.Above, 6);
        Assert.Equal(27, estimate.Interval.AtMost!.Value, 6);
        Assert.Equal(3, estimate.SupportingFights);
    }

    [Fact]
    public void SurvivorFloorsAboveEveryKillCeilingLoseToTheBoundedRun()
    {
        // Two kills cap the pool at 20; two survivors insist it exceeds 50. Both cannot be right, and
        // only bounded runs compete - a kill total is arithmetic over what the game printed, where a
        // survivor's floor is inflated by any regeneration the client cannot see. The two that lost
        // still show up as the gap between SupportingFights and ContributingFights.
        var estimate = StaminaPoolEstimator.Estimate("rat", new[]
        {
            Fight(name: "rat0", kill: true, blows: [(10, 20)]),          // (0, 20]
            Fight(name: "rat1", kill: true, blows: [(10, 20)]),          // (0, 20]
            Fight(name: "rat3", blows: [(50, 60)]),                      // (50, inf)
            Fight(name: "rat4", blows: [(50, 60)]),                      // (50, inf)
        });

        Assert.Equal(2, estimate.SupportingFights);
        Assert.Equal(4, estimate.ContributingFights);
        Assert.Equal(0, estimate.Interval.Above, 6);
        Assert.Equal(20, estimate.Interval.AtMost!.Value, 6);
    }

    [Fact]
    public void TwoEquallySupportedBandsReportSplitEvidenceAndBothRuns()
    {
        // Two kills each, disjoint. Picking one would be a claim about which contaminant to believe, so
        // neither is picked. The evidence STATE is what carries that: a consumer wording this estimate
        // has to name the Split case rather than being handed a hull that looks like an ordinary band.
        var estimate = StaminaPoolEstimator.Estimate("rat", new[]
        {
            Fight(name: "rat0", kill: true, blows: [(0, 10)]),                    // (0, 10]
            Fight(name: "rat1", kill: true, blows: [(0, 10)]),                    // (0, 10]
            Fight(name: "rat2", kill: true, blows: [(100, 100), (0, 10)]),        // (100, 110]
            Fight(name: "rat3", kill: true, blows: [(100, 100), (0, 10)]),        // (100, 110]
        });

        Assert.Equal(PoolEvidence.Split, estimate.Evidence);
        Assert.True(estimate.IsSplit);
        Assert.Equal(2, estimate.SupportingFights);

        // The real bands, which is what a consumer should draw...
        Assert.Equal(2, estimate.TiedRuns.Count);
        Assert.Equal(new StaminaInterval(0, 10), estimate.TiedRuns[0]);
        Assert.Equal(new StaminaInterval(100, 110), estimate.TiedRuns[1]);

        // ...and the hull, which it must not, because nothing supports the gap between them.
        Assert.Equal(0, estimate.Interval.Above, 6);
        Assert.Equal(110, estimate.Interval.AtMost!.Value, 6);
        Assert.DoesNotContain(estimate.TiedRuns, run => run.Contains(50));
        Assert.True(estimate.Interval.Contains(50));
    }

    [Fact]
    public void ASplitEstimateStillYieldsASoundPessimisticPool()
    {
        // The truth is inside one of the runs and every run is inside the hull, so the hull's top is
        // still a genuine upper bound - just a loose one, which is the correct consequence of the
        // evidence disagreeing. This is why the survivability projection can keep consuming a split
        // estimate where a renderer cannot.
        var estimate = StaminaPoolEstimator.Estimate("rat", new[]
        {
            Fight(name: "rat0", kill: true, blows: [(0, 10)]),
            Fight(name: "rat1", kill: true, blows: [(0, 10)]),
            Fight(name: "rat2", kill: true, blows: [(100, 100), (0, 10)]),
            Fight(name: "rat3", kill: true, blows: [(100, 100), (0, 10)]),
        });

        Assert.Equal(110, estimate.PessimisticPool);
        Assert.All(estimate.TiedRuns, run => Assert.True(run.AtMost <= estimate.PessimisticPool));
    }

    [Fact]
    public void DisjointBoundedRunsOfEqualDepthProduceAHull()
    {
        var result = StaminaPoolEstimator.MaxDepthRun(new[]
        {
            new StaminaInterval(0, 10),
            new StaminaInterval(0, 10),
            new StaminaInterval(100, 110),
            new StaminaInterval(100, 110),
        });

        Assert.Equal(2, result.Depth);
        Assert.Equal(2, result.Runs.Count);
        Assert.Equal(new StaminaInterval(0, 10), result.Runs[0]);
        Assert.Equal(new StaminaInterval(100, 110), result.Runs[1]);
        Assert.Equal(0, result.Interval.Above, 6);
        Assert.Equal(110, result.Interval.AtMost!.Value, 6);
    }

    [Fact]
    public void AdjacentGapsAtTheSameDepthAreOneRunNotTwo()
    {
        // (0,10] and (0,20] overlap on (0,10] at depth 3, then (10,20] drops - so there is exactly one
        // max-depth run. A sweep that emitted a run per gap would report a spurious split on perfectly
        // agreeing evidence, which is the failure mode of making Split loud.
        var result = StaminaPoolEstimator.MaxDepthRun(new[]
        {
            new StaminaInterval(0, 10),
            new StaminaInterval(0, 20),
            new StaminaInterval(0, 20),
        });

        Assert.Single(result.Runs);
        Assert.Equal(new StaminaInterval(0, 10), result.Runs[0]);
    }

    [Fact]
    public void AMultiGapRunIsReportedAsOneContiguousBand()
    {
        // Three constraints whose max-depth region spans two adjacent gaps. They must merge, or the
        // band would be reported narrower than the evidence supports AND flagged as split.
        var result = StaminaPoolEstimator.MaxDepthRun(new[]
        {
            new StaminaInterval(0, 30),
            new StaminaInterval(0, 30),
            new StaminaInterval(10, 20),
        });

        // Depth is 3 on (10,20]; the neighbouring gaps sit at depth 2, so the run is (10,20].
        Assert.Equal(3, result.Depth);
        Assert.Single(result.Runs);
        Assert.Equal(new StaminaInterval(10, 20), result.Runs[0]);
    }

    [Fact]
    public void WithNoKillsTheAnswerIsTheHighestFloorAndEveryFightSupportsIt()
    {
        // Half-lines all overlap out at infinity, so a naive max-depth sweep would report "somewhere
        // above everything", which says nothing. The hard intersection of half-lines is the highest
        // floor and cannot be empty.
        var estimate = StaminaPoolEstimator.Estimate("rat", new[]
        {
            Fight(name: "rat0", blows: [(10, 14)]),
            Fight(name: "rat1", blows: [(30, 40)]),
            Fight(name: "rat2", blows: [(20, 25)]),
        });

        Assert.Equal(PoolEvidence.LowerBoundOnly, estimate.Evidence);
        Assert.Equal(30, estimate.Interval.Above, 6);
        Assert.Null(estimate.Interval.AtMost);
        Assert.Equal(3, estimate.SupportingFights);
        Assert.Null(estimate.PessimisticPool);
        Assert.False(estimate.IsSplit);
        Assert.Single(estimate.TiedRuns);
    }

    [Fact]
    public void NothingOnFileIsNoEvidenceRatherThanZero()
    {
        var estimate = StaminaPoolEstimator.Estimate("rat", []);

        Assert.Equal(PoolEvidence.None, estimate.Evidence);
        Assert.False(estimate.HasEvidence);
        Assert.Equal(0, estimate.SupportingFights);
        Assert.Null(estimate.PessimisticPool);
        Assert.Empty(estimate.TiedRuns);
        Assert.Equal("rat", estimate.PoolKey);
    }

    [Fact]
    public void TheEstimateReportsHowMuchEvidenceItLinkedAndWhatItDropped()
    {
        // rat0 breaks off and is chased down: two fights, one chain, one observation. rat1 is its own
        // fight and contradicts itself (its rung reading demands more pool than its kill bracket
        // allows), so it is counted as contradictory but still contributes its kill bracket.
        var estimate = StaminaPoolEstimator.Estimate("rat", new[]
        {
            Fight(name: "rat0", startedAtMs: 0, endedAtMs: 1_000, blows: [(10, 14), (10, 14)],
                  staStart: 40, staEnd: 38, staMax: 100, weapon: "axe0"),
            Fight(name: "rat0", startedAtMs: 2_000, endedAtMs: 3_000, kill: true, blows: [(6, 8)],
                  staStart: 38, staEnd: 36, staMax: 100, weapon: "axe0"),
            Fight(name: "rat1", startedAtMs: 0, endedAtMs: 1_000, kill: true,
                  blows: [(20, 29), (20, 29), (0, 2)], rungs: [(7, 2)]),
        });

        Assert.Equal(1, estimate.LinkedEngagements);
        Assert.Equal(0, estimate.DroppedInFolds);
        Assert.Equal(1, estimate.ContradictoryFights);
        Assert.Equal(2, estimate.ContributingFights);
    }

    /// <summary>
    /// A kill followed by the same name is two creatures, and both are evidence.
    ///
    /// <para>A potion in the game summons creatures and can draw from the full pool including dead
    /// ones, which is how the same name can be killed twice in one reset. A wizard can resummon too,
    /// so this is repeatable - and capped, since no more than two of the same id'd creature can exist
    /// at once.</para>
    ///
    /// <para>The second life is CLEAN data rather than contamination, which is why both observations
    /// stand instead of the later one being dropped. Measured: successors of a same-reset kill reproduce
    /// the isolated kill distribution (rats n=10, median damage-to-kill 37.5 against an isolated median
    /// of 37 over n=445, IQR 33-42).</para>
    /// </summary>
    [Fact]
    public void AKillFollowedByTheSameNameIsTwoObservations_NotAChain()
    {
        // The clock alone does not merge these two kills; a kill ends the chain absolutely, so
        // each is its own observation.
        var estimate = StaminaPoolEstimator.Estimate("rat", new[]
        {
            Fight(name: "rat0", startedAtMs: 0, endedAtMs: 1_000, kill: true, blows: [(10, 14), (10, 14)]),
            Fight(name: "rat0", startedAtMs: 2_000, endedAtMs: 3_000, kill: true, blows: [(10, 14), (10, 14)]),
        });

        Assert.Equal(0, estimate.LinkedEngagements);
        Assert.Equal(2, estimate.ContributingFights);
    }

    // -- Width, which is how a caller tells a tight band from a useless one -------

    [Fact]
    public void WidthAndRelativeWidthDescribeHowWellTheBandIsKnown()
    {
        var tight = StaminaPoolEstimator.Estimate("rat", new[]
        {
            Fight(name: "rat0", kill: true, blows: [(24, 25), (0, 1)]),   // (24, 26]
        });
        Assert.Equal(2, tight.Interval.Width!.Value, 6);
        Assert.InRange(tight.Interval.RelativeWidth!.Value, 0.07, 0.09);

        var wide = StaminaPoolEstimator.Estimate("coot", new[]
        {
            Fight(name: "coot", kill: true, blows: [(1, 40), (1, 40)]),   // (1, 80]
        });
        Assert.True(wide.Interval.RelativeWidth > tight.Interval.RelativeWidth);
    }

    // -- Regeneration labelling ---------------------------------------------------

    [Theory]
    [InlineData("zombie", PoolQuantity.DamageToKill)]
    [InlineData("large zombie", PoolQuantity.DamageToKill)]     // regen is a property of the animal
    [InlineData("rat", PoolQuantity.StaminaPool)]
    [InlineData("large rat", PoolQuantity.StaminaPool)]
    public void RegeneratingSpeciesAreLabelledAsDamageToKill(string poolKey, PoolQuantity expected)
        => Assert.Equal(expected, StaminaPoolEstimator.QuantityFor(poolKey));

    [Fact]
    public void AZombieEstimateCarriesTheDamageToKillLabelEvenWithNoEvidence()
    {
        // The label is a property of the species, not of how much has been measured, so a caller can
        // word the readout correctly before the first kill.
        var estimate = StaminaPoolEstimator.Estimate("zombie", []);
        Assert.Equal(PoolQuantity.DamageToKill, estimate.Quantity);
    }

    // -- The pessimistic end, which is the only single number this offers --------

    [Fact]
    public void PessimisticPoolIsTheTopOfTheBandNotItsMiddle()
    {
        var estimate = StaminaPoolEstimator.Estimate("rat", new[]
        {
            Fight(name: "rat0", kill: true, blows: [(10, 14), (10, 14)]),
        });

        Assert.Equal(estimate.Interval.AtMost, estimate.PessimisticPool);
        Assert.NotEqual(estimate.Interval.Midpoint, estimate.PessimisticPool);
    }
}
