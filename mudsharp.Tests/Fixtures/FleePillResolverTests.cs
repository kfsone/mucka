using MudSharp.Combat;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// Covers the flee pill's four states: the three stamina bands, the one-tick worst case that can raise
/// the pill in a fight where stamina still looks fine, and the hits-left count that speaks for a fight
/// where stamina is untouched.
/// </summary>
public sealed class FleePillResolverTests
{
    /// <summary>A live opponent that has hit the player for <paramref name="average"/> per blow this
    /// fight. Uses the this-fight profile rather than the historical one so each test states its
    /// evidence in the same place the game would have produced it.</summary>
    private static RosterRow Live(string name, double average, int samples = 4)
        => new(name, IsLive: true, IsCurrentTarget: false, FightOutcome.Unresolved,
            FightDamage: new DamageProfile(samples, average, average * samples));

    private static RosterPlan Plan(params RosterRow[] rows)
        => new(rows, LiveCount: rows.Count(r => r.IsLive), ResolvedCount: rows.Count(r => !r.IsLive),
            HiddenCount: 0, HiddenLiveCount: 0);

    // ── The gate ────────────────────────────────────────────────────────────────

    [Fact]
    public void OutOfCombat_IsHidden()
    {
        // flee is a combat command; out of combat the player simply walks. Stamina low enough to be
        // the loudest state in a fight must still draw nothing here.
        var status = FleePillResolver.Resolve(inCombat: false, staminaCurrent: 3,
            worstCaseTickDamage: 40, hitsLeft: 1);
        Assert.Equal(FleePillStatus.Hidden, status);
    }

    [Fact]
    public void HealthyWithNoDamageEvidence_IsHidden()
    {
        var status = FleePillResolver.Resolve(inCombat: true, staminaCurrent: 90,
            worstCaseTickDamage: 0, hitsLeft: null);
        Assert.Equal(FleePillStatus.Hidden, status);
    }

    // ── Stamina bands ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(6)]
    [InlineData(1)]
    public void AtOrBelowTheCriticalThreshold_IsEscapeNow(int stamina)
    {
        var status = FleePillResolver.Resolve(inCombat: true, stamina,
            worstCaseTickDamage: 0, hitsLeft: null);
        Assert.Equal(FleePillStatus.EscapeNow, status);
    }

    [Fact]
    public void JustAboveTheCriticalThreshold_IsOnlyCaution()
    {
        // 6.5 is the threshold, so 7 is outside it. The distinction matters: EscapeNow is a claim about
        // being inside the band where one ordinary blow kills, not a synonym for "very low".
        var status = FleePillResolver.Resolve(inCombat: true, staminaCurrent: 7,
            worstCaseTickDamage: 0, hitsLeft: null);
        Assert.Equal(FleePillStatus.Caution, status);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(12)]
    public void AtOrBelowTheSurvivalThreshold_IsCaution(int stamina)
    {
        var status = FleePillResolver.Resolve(inCombat: true, stamina,
            worstCaseTickDamage: 0, hitsLeft: null);
        Assert.Equal(FleePillStatus.Caution, status);
    }

    [Theory]
    [InlineData(26)]
    [InlineData(21)]
    public void InTheWarmUpBand_IsVisible(int stamina)
    {
        // Survival threshold + the critical band's width = 26.5, so 26 is inside and 27 is not. The
        // pill has to already be on screen before it starts alarming.
        var status = FleePillResolver.Resolve(inCombat: true, stamina,
            worstCaseTickDamage: 0, hitsLeft: null);
        Assert.Equal(FleePillStatus.Visible, status);
    }

    [Fact]
    public void JustAboveTheWarmUpBandWithNoOtherEvidence_IsHidden()
    {
        var status = FleePillResolver.Resolve(inCombat: true, staminaCurrent: 27,
            worstCaseTickDamage: 0, hitsLeft: null);
        Assert.Equal(FleePillStatus.Hidden, status);
    }

    // ── The one-tick worst case ─────────────────────────────────────────────────

    [Fact]
    public void OneTickCouldKill_IsCautionEvenWellAboveTheSurvivalThreshold()
    {
        // A pack averaging 45 between them against 40 stamina: everything lands on the same boundary in
        // MUD2, so this is one bad tick from death at a stamina the bands alone call safe.
        var status = FleePillResolver.Resolve(inCombat: true, staminaCurrent: 40,
            worstCaseTickDamage: 45, hitsLeft: null);
        Assert.Equal(FleePillStatus.Caution, status);
    }

    [Fact]
    public void OneTickWouldReachTheSurvivalThreshold_IsVisible()
    {
        // 60 stamina, 45 a tick: one average boundary lands at 15, below the survival threshold.
        var status = FleePillResolver.Resolve(inCombat: true, staminaCurrent: 60,
            worstCaseTickDamage: 45, hitsLeft: null);
        Assert.Equal(FleePillStatus.Visible, status);
    }

    [Fact]
    public void OneTickLeavesPlentyOfRoom_IsHidden()
    {
        var status = FleePillResolver.Resolve(inCombat: true, staminaCurrent: 90,
            worstCaseTickDamage: 45, hitsLeft: null);
        Assert.Equal(FleePillStatus.Hidden, status);
    }

    [Fact]
    public void SingleHardHitterIsSubsumedByTheSum()
    {
        // The per-creature rule ("any one of them averages more than my whole stamina") is a strict
        // subset of the sum, so it needs no separate branch - this is the case it existed for.
        var plan = Plan(Live("dragon0", average: 60));
        var worst = FleePillResolver.WorstCaseTickDamage(plan);
        Assert.Equal(FleePillStatus.Caution,
            FleePillResolver.Resolve(inCombat: true, staminaCurrent: 55, worst, hitsLeft: null));
    }

    // ── hitsLeft: the count, not a forecast ─────────────────────────────────────

    [Fact]
    public void TwoHitsFromDeathAtFullHealth_IsCaution()
    {
        // How a dragon kills someone who never dropped below full. Without this the pill would stay
        // Hidden through exactly that fight.
        var status = FleePillResolver.Resolve(inCombat: true, staminaCurrent: 105,
            worstCaseTickDamage: 0, hitsLeft: 2);
        Assert.Equal(FleePillStatus.Caution, status);
    }

    [Fact]
    public void ThreeHitsFromDeathAtFullHealth_DoesNotRaiseThePill()
    {
        var status = FleePillResolver.Resolve(inCombat: true, staminaCurrent: 105,
            worstCaseTickDamage: 0, hitsLeft: 3);
        Assert.Equal(FleePillStatus.Hidden, status);
    }

    [Fact]
    public void UnknownStamina_NeverReachesEscapeNow()
    {
        // EscapeNow is a statement about an absolute stamina. With no reading at all the count can raise
        // the pill, but nothing can place the player in that band.
        var status = FleePillResolver.Resolve(inCombat: true, staminaCurrent: null,
            worstCaseTickDamage: 90, hitsLeft: 1);
        Assert.Equal(FleePillStatus.Caution, status);
    }

    [Fact]
    public void UnknownStaminaWithNoCount_IsHidden()
    {
        var status = FleePillResolver.Resolve(inCombat: true, staminaCurrent: null,
            worstCaseTickDamage: 90, hitsLeft: null);
        Assert.Equal(FleePillStatus.Hidden, status);
    }

    // ── WorstCaseTickDamage ─────────────────────────────────────────────────────

    [Fact]
    public void WorstCase_SumsOnlyLiveOpponents()
    {
        var dead = new RosterRow("rat1", IsLive: false, IsCurrentTarget: false, FightOutcome.Kill,
            FightDamage: new DamageProfile(4, 9, 36));
        var plan = Plan(Live("rat2", average: 5), Live("rat3", average: 7), dead);
        Assert.Equal(12.0, FleePillResolver.WorstCaseTickDamage(plan));
    }

    [Fact]
    public void WorstCase_TakesTheLouderOfThisFightAndAllHistory()
    {
        // Neither timescale is authoritative, so a survival alarm takes the higher reading rather than
        // averaging a measurement against a baseline.
        var row = new RosterRow("zombie9", IsLive: true, IsCurrentTarget: true, FightOutcome.Unresolved,
            FightDamage: new DamageProfile(2, 4, 8),        // averages 4 this fight
            EverDamage: new DamageProfile(30, 22, 330));    // averages 11 across history
        Assert.Equal(11.0, FleePillResolver.WorstCaseTickDamage(Plan(row)));
    }

    [Fact]
    public void WorstCase_UnmeasuredOpponentCountsAsTheAssumedUnknownHit()
    {
        // Owner's decision: an opponent nobody has been hit by is assumed to hit for the top of the
        // published ordinary-NPC range, not for nothing. Silence about an unmeasured creature reads as a
        // claim that it is harmless, which is the one direction this alarm must not fail in.
        var unknown = new RosterRow("stickleback0", IsLive: true, IsCurrentTarget: false, FightOutcome.Unresolved);
        Assert.Equal(FleePillResolver.AssumedUnknownHit, FleePillResolver.WorstCaseTickDamage(Plan(unknown)));
    }

    [Fact]
    public void WorstCase_MeasuredZeroIsAMeasurementAndNotAnUnknown()
    {
        // MUD2 lands blows that take nothing off, and DamageProfile counts them on purpose - so
        // Samples > 0 with Sum == 0 describes a creature that has demonstrably failed to hurt anyone over
        // twelve landed blows. It must contribute 0, not the unknown assumption. This regressed once: the
        // branch tested the resulting number rather than the sample count, which made a proven-harmless
        // creature exactly as alarming as one never seen before.
        var harmless = new RosterRow("firefly0", IsLive: true, IsCurrentTarget: true, FightOutcome.Unresolved,
            FightDamage: new DamageProfile(12, 0, 0));
        Assert.Equal(0.0, FleePillResolver.WorstCaseTickDamage(Plan(harmless)));
    }

    [Fact]
    public void WorstCase_MeasuredZeroDoesNotDragDownAMeasuredSibling()
    {
        // The other half of the same rule: a zero reading is averaged in as zero, not skipped, and the
        // creature beside it still speaks for itself.
        var harmless = new RosterRow("firefly0", IsLive: true, IsCurrentTarget: true, FightOutcome.Unresolved,
            FightDamage: new DamageProfile(12, 0, 0));
        Assert.Equal(7.0, FleePillResolver.WorstCaseTickDamage(Plan(harmless, Live("rat3", average: 7))));
    }

    [Fact]
    public void WorstCase_MeasuredOpponentIsNotRaisedToTheAssumption()
    {
        // A substitution, not a floor. A rat measured at 4 a blow contributes 4 - otherwise every fight
        // in the game would be described by the same number and the figure would say nothing.
        Assert.Equal(4.0, FleePillResolver.WorstCaseTickDamage(Plan(Live("rat2", average: 4))));
    }

    [Fact]
    public void WorstCase_UnknownOpponentsMultiply()
    {
        // Recorded because it is the assumption's one surprising consequence: a pack of a species nobody
        // has met sums to a large figure fast, and will raise the pill early. Transient - one landed blow
        // each replaces the assumption with a measurement - but real while it lasts.
        var plan = Plan(
            new RosterRow("newt0", IsLive: true, IsCurrentTarget: true, FightOutcome.Unresolved),
            new RosterRow("newt1", IsLive: true, IsCurrentTarget: false, FightOutcome.Unresolved),
            new RosterRow("newt2", IsLive: true, IsCurrentTarget: false, FightOutcome.Unresolved),
            new RosterRow("newt3", IsLive: true, IsCurrentTarget: false, FightOutcome.Unresolved));
        var worst = FleePillResolver.WorstCaseTickDamage(plan);
        Assert.Equal(FleePillResolver.AssumedUnknownHit * 4, worst);
        Assert.Equal(FleePillStatus.Caution,
            FleePillResolver.Resolve(inCombat: true, staminaCurrent: 75, worst, hitsLeft: null));
    }

    [Fact]
    public void WorstCase_AssumptionDoesNotApplyToResolvedOpponents()
    {
        // The dead do not swing. Without the IsLive test the assumption would keep a killed creature's
        // worst case on the books for the rest of the encounter.
        var dead = new RosterRow("rat1", IsLive: false, IsCurrentTarget: false, FightOutcome.Kill);
        Assert.Equal(0.0, FleePillResolver.WorstCaseTickDamage(Plan(dead)));
    }

    [Fact]
    public void WorstCase_ExtrapolatesHiddenLiveOpponentsAtTheMeanOfTheOnesWithRows()
    {
        // Past the roster's row cap. Two rows averaging 5 and 7, plus two live opponents with no row:
        // 12 + 2 * 6. The mean is preferred over the blanket assumption here because reaching this case
        // means eight rows of real participants are already in hand.
        var rows = new[] { Live("rat2", average: 5), Live("rat3", average: 7) };
        var plan = new RosterPlan(rows, LiveCount: 4, ResolvedCount: 0,
            HiddenCount: 2, HiddenLiveCount: 2);
        Assert.Equal(24.0, FleePillResolver.WorstCaseTickDamage(plan));
    }

    [Fact]
    public void WorstCase_NoLiveOpponentsAtAllIsZeroEvenWithHiddenLiveClaimed()
    {
        // The one path where the extrapolation's guard earns its keep: nothing to extrapolate from. Zero
        // here means "no evidence", and Resolve's own guards are what stop it being read as harmless.
        var dead = new RosterRow("rat1", IsLive: false, IsCurrentTarget: false, FightOutcome.Kill);
        var plan = new RosterPlan([dead], LiveCount: 0, ResolvedCount: 1,
            HiddenCount: 2, HiddenLiveCount: 2);
        Assert.Equal(0.0, FleePillResolver.WorstCaseTickDamage(plan));
    }
}
