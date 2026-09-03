using Mucka.Core;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// The tinted "just lost" slice on the player's stamina ring: which losses belong to the same tick,
/// what clears the slice, and what never does.
/// </summary>
public sealed class TickStaminaLossTests
{
    private static readonly DateTime T0 = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NothingObserved_IsNoSlice()
    {
        Assert.Equal(0, new TickStaminaLoss().LostThisTick);
    }

    [Fact]
    public void TheFirstReadingIsABaseline_NotALossOfEverything()
    {
        var loss = new TickStaminaLoss();
        loss.Observe(105, T0);

        Assert.Equal(0, loss.LostThisTick);
    }

    [Fact]
    public void OneBlowIsOneSlice()
    {
        var loss = new TickStaminaLoss();
        loss.Observe(105, T0);
        loss.Observe(98, T0.AddMilliseconds(2000));

        Assert.Equal(7, loss.LostThisTick);
    }

    [Fact]
    public void SeveralBlowsInOneTickMakeOneCombinedSlice_NotStackedOnes()
    {
        // A pack landing four hits on the same tick. MUD2 resolves them together and they reach the
        // client within a few tens of milliseconds, so they are one slice of 22 - not four slices.
        var loss = new TickStaminaLoss();
        loss.Observe(105, T0);
        loss.Observe(98, T0.AddMilliseconds(2000));
        loss.Observe(92, T0.AddMilliseconds(2012));
        loss.Observe(87, T0.AddMilliseconds(2026));
        loss.Observe(83, T0.AddMilliseconds(2041));

        Assert.Equal(22, loss.LostThisTick);
    }

    [Fact]
    public void TheNextTicksLossReplacesTheLastOne_RatherThanAddingToIt()
    {
        var loss = new TickStaminaLoss();
        loss.Observe(105, T0);
        loss.Observe(98, T0.AddMilliseconds(2000));
        Assert.Equal(7, loss.LostThisTick);

        loss.Observe(94, T0.AddMilliseconds(4000));
        Assert.Equal(4, loss.LostThisTick);
    }

    [Fact]
    public void TheWindowSeparatesConsecutiveTicksWithRoomToSpare()
    {
        // Two orders of magnitude of margin either side: blows within one tick arrive ~26ms apart
        // (median residual over 68 sessions), consecutive ticks are 2000ms apart, and the window sits
        // at a quarter tick between them.
        Assert.True(TickStaminaLoss.SameTickWindow > TimeSpan.FromMilliseconds(26 * 4));
        Assert.True(TickStaminaLoss.SameTickWindow < TimeSpan.FromMilliseconds(2000 / 2.0));
    }

    [Fact]
    public void AGainClearsTheSlice_BecauseItsPositionNoLongerMeansAnything()
    {
        // The slice is the segment sitting immediately behind the boundary. Regen, a wafer or a heal
        // moves the boundary away from it, so leaving it drawn would mark a stretch of ring that is no
        // longer where the loss happened.
        var loss = new TickStaminaLoss();
        loss.Observe(105, T0);
        loss.Observe(98, T0.AddMilliseconds(2000));
        Assert.Equal(7, loss.LostThisTick);

        loss.Observe(99, T0.AddMilliseconds(4000));
        Assert.Equal(0, loss.LostThisTick);
    }

    [Fact]
    public void AnUnchangedReadingLeavesTheSliceStanding()
    {
        // LostThisTick itself never fades or expires on its own - it is the record of the last thing
        // that hurt the player until something else does. The rendered TINT fades (see FadeFactor
        // below); the magnitude this class tracks does not, which is what lets the renderer keep
        // reporting the right slice LENGTH for as long as it draws one at all.
        var loss = new TickStaminaLoss();
        loss.Observe(105, T0);
        loss.Observe(98, T0.AddMilliseconds(2000));

        for (var tick = 2; tick < 10; tick++)
            loss.Observe(98, T0.AddMilliseconds(2000 * tick));

        Assert.Equal(7, loss.LostThisTick);
    }

    // ── FadeFactor: the just-lost tint's strength over time ──────────────────────────────────

    [Fact]
    public void FadeFactor_AtTheInstantOfLoss_IsPeakStrength()
    {
        Assert.Equal(TickStaminaLoss.PeakTintStrength, TickStaminaLoss.FadeFactor(T0, T0));
    }

    [Fact]
    public void FadeFactor_HalfwayThroughTheTick_IsHalfPeak()
    {
        var half = TickStaminaLoss.FadeFactor(T0, T0.AddMilliseconds(1000));
        Assert.Equal(TickStaminaLoss.PeakTintStrength / 2f, half, precision: 5);
    }

    [Fact]
    public void FadeFactor_AtAFullTick_IsZero()
    {
        Assert.Equal(0f, TickStaminaLoss.FadeFactor(T0, T0.AddMilliseconds(2000)));
    }

    [Fact]
    public void FadeFactor_PastAFullTick_StaysZero_RatherThanGoingNegative()
    {
        Assert.Equal(0f, TickStaminaLoss.FadeFactor(T0, T0.AddMilliseconds(5000)));
    }

    [Fact]
    public void FadeFactor_NowBeforeTheLoss_ClampsToPeak_RatherThanExceedingIt()
    {
        // Clock-skew guard: a "now" that precedes the loss (a few ms of dispatch-hop inversion) must
        // not read as elapsed time going backwards, which unclamped arithmetic would turn into a
        // strength ABOVE the peak.
        var strength = TickStaminaLoss.FadeFactor(T0, T0.AddMilliseconds(-5));
        Assert.Equal(TickStaminaLoss.PeakTintStrength, strength);
    }

    [Fact]
    public void FadeFactor_DecaysStrictlyFromThePeakToNothingWithinOneTick()
    {
        // This replaced a test asserting the strength stayed under the OLD static 0.35 at every point.
        // That premise is dead: the peak went 0.35 -> 0.18 -> 0.35 again once it turned out the owner's
        // "too much red" was about the slice PERSISTING rather than its saturation, and cutting the
        // peak as well made it invisible ("I couldn't make it out with the last build"). See
        // TickStaminaLoss.PeakTintStrength.
        //
        // What is load-bearing now is the DECAY, not any particular ceiling - a slice that reaches zero
        // inside one tick cannot read as a live part of the gauge however bright it starts. So that is
        // what this pins: starts at the peak, never rises, and is gone by the end of the tick.
        var previous = TickStaminaLoss.FadeFactor(T0, T0);
        Assert.Equal(TickStaminaLoss.PeakTintStrength, previous);

        for (var elapsedMs = 100; elapsedMs < CombatTiming.TickMilliseconds; elapsedMs += 100)
        {
            var strength = TickStaminaLoss.FadeFactor(T0, T0.AddMilliseconds(elapsedMs));
            Assert.True(
                strength < previous,
                $"strength must fall every step; at {elapsedMs}ms it was {strength}, previously {previous}");
            Assert.True(strength > 0f, $"still inside the tick at {elapsedMs}ms, so must not be spent yet");
            previous = strength;
        }

        Assert.Equal(0f, TickStaminaLoss.FadeFactor(T0, T0.AddMilliseconds(CombatTiming.TickMilliseconds)));
    }

    [Fact]
    public void LastLossUtc_ReportsWhenTheCurrentSliceArrived()
    {
        var loss = new TickStaminaLoss();
        loss.Observe(105, T0);
        loss.Observe(98, T0.AddMilliseconds(2000));

        Assert.Equal(T0.AddMilliseconds(2000), loss.LastLossUtc);
    }

    [Fact]
    public void LastLossUtc_RestartsOnTheLatestBlowWithinAAccumulatingBurst()
    {
        // Several blows landing in the same tick accumulate into one slice (see
        // SeveralBlowsInOneTickMakeOneCombinedSlice above); the fade clock has to restart on each one,
        // because the slice just grew and a fade already partway through would be fading the wrong
        // (smaller) slice.
        var loss = new TickStaminaLoss();
        loss.Observe(105, T0);
        loss.Observe(98, T0.AddMilliseconds(2000));
        loss.Observe(92, T0.AddMilliseconds(2012));

        Assert.Equal(T0.AddMilliseconds(2012), loss.LastLossUtc);
    }

    [Fact]
    public void AMissingReadingIsIgnored_NotTreatedAsZero()
    {
        var loss = new TickStaminaLoss();
        loss.Observe(105, T0);
        loss.Observe(98, T0.AddMilliseconds(2000));
        loss.Observe(null, T0.AddMilliseconds(2500));

        Assert.Equal(7, loss.LostThisTick);
    }

    [Fact]
    public void ResetForgetsEverything()
    {
        var loss = new TickStaminaLoss();
        loss.Observe(105, T0);
        loss.Observe(98, T0.AddMilliseconds(2000));

        loss.Reset();

        Assert.Equal(0, loss.LostThisTick);
        // And the next reading is a baseline again rather than a loss against the old one.
        loss.Observe(40, T0.AddMilliseconds(9000));
        Assert.Equal(0, loss.LostThisTick);
    }
}
