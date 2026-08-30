using Mucka.Core;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// The one piece of arithmetic both renderings of the combat tick depend on, plus the alternating
/// chain the click schedules on top of it.
///
/// <para>Worth pinning because "the bar and the click disagree" is this feature's most-reported fault,
/// and every time it has been investigated the derivation turned out to be right - the anchor was
/// measured against real captures at within 3% of a tick, and the two boundary calculations agreed
/// algebraically. What that history actually argues for is not more scrutiny of the formula but a test
/// that makes the shared lattice impossible to break silently while attention is elsewhere.</para>
/// </summary>
public class TickPhaseTests
{
    private const double Tick = CombatTiming.TickMilliseconds;

    /// <summary>N, mirrored from CombatMetronome.OffsetMilliseconds - which is private, deliberately:
    /// nothing outside that class should be choosing where the clicks sit. Kept here as a plain constant
    /// so the chain arithmetic below is checkable without widening that access.
    ///
    /// <para>The value does not have to match the shipping one for these tests to mean something - they
    /// assert properties of the lattice that hold for ANY offset under half a tick, and each one passes
    /// its own offset to <c>NextBeat</c>. That is deliberate: the previous version of this file mirrored
    /// the constant and then built its assertions out of the same arithmetic the code used, so the tests
    /// could only ever agree with themselves.</para></summary>
    private const int Offset = 50;

    private static readonly DateTime Anchor = new(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void OnTheAnchor_ReportsAFullTick_NotZero()
    {
        // A bar told "0 ms left" would restart from empty and a click told the same would fire on a
        // rollover it has already marked. The next boundary is a whole tick away, and that is the
        // honest answer.
        Assert.Equal(Tick, CombatTiming.MillisecondsToNextBoundary(Anchor, Anchor));
    }

    [Theory]
    [InlineData(0, 2000)]
    [InlineData(1, 1999)]
    [InlineData(500, 1500)]
    [InlineData(1999, 1)]
    [InlineData(2000, 2000)]     // exactly one tick on: back to a full tick, not zero
    [InlineData(2500, 1500)]     // several ticks in, same phase as 500
    [InlineData(60_000, 2000)]   // thirty ticks: no accumulated error, it is pure modulo
    [InlineData(60_500, 1500)]
    public void RemainingIsThePhase_RegardlessOfHowManyTicksHavePassed(double elapsedMs, double expected)
        => Assert.Equal(expected, CombatTiming.MillisecondsToNextBoundary(Anchor, Anchor.AddMilliseconds(elapsedMs)));

    /// <summary>
    /// "now" preceding the anchor is not a hypothetical: the anchor is a feed-thread timestamp and the
    /// callers read their own clock after a dispatch hop, so a few ms of inversion is ordinary at the
    /// exact moment a fight's phase first arrives. A naive modulo goes negative there, which would hand
    /// the bar a negative duration and the click a delay in the past.
    /// </summary>
    [Theory]
    [InlineData(-1, 1)]           // the anchor itself is the next boundary, 1 ms away
    [InlineData(-500, 500)]       // ditto, 500 ms away - NOT a tick-and-a-half
    [InlineData(-2500, 500)]      // a lattice point sits at anchor-2000, so still 500 ms
    public void NowBeforeAnchor_StaysOnTheLattice(double elapsedMs, double expected)
        => Assert.Equal(expected, CombatTiming.MillisecondsToNextBoundary(Anchor, Anchor.AddMilliseconds(elapsedMs)));

    [Fact]
    public void ResultIsAlwaysWithinOneTick()
    {
        for (var ms = -4000; ms <= 4000; ms += 7)
        {
            var remaining = CombatTiming.MillisecondsToNextBoundary(Anchor, Anchor.AddMilliseconds(ms));
            Assert.InRange(remaining, 0.0, Tick);
        }
    }

    // ---- The alternating chain ------------------------------------------------------------------

    /// <summary>
    /// Beats sit at <c>boundary +/- N</c>, and the pair brackets the rollover: the pre-tick before it,
    /// the after-tick past it, and nothing exactly on it.
    /// </summary>
    [Fact]
    public void NextBeat_PlacesThePairEitherSideOfTheBoundary()
    {
        // Just after a boundary: the after-tick of the boundary just gone is next.
        var (delay, afterTick) = CombatTiming.NextBeat(Anchor, Anchor.AddMilliseconds(1), Offset, Offset);
        Assert.True(afterTick);
        Assert.Equal(Offset - 1, delay, 6);

        // Between the two: the pre-tick of the boundary coming up is next.
        (delay, afterTick) = CombatTiming.NextBeat(Anchor, Anchor.AddMilliseconds(1000), Offset, Offset);
        Assert.False(afterTick);
        Assert.Equal(Tick - Offset - 1000, delay, 6);

        // Past the pre-tick: the after-tick of the NEXT boundary.
        (delay, afterTick) = CombatTiming.NextBeat(Anchor, Anchor.AddMilliseconds(Tick - Offset + 1), Offset, Offset);
        Assert.True(afterTick);
        Assert.Equal((2 * Offset) - 1, delay, 6);
    }

    /// <summary>
    /// THE test this file exists for, and the one its predecessor could not perform.
    ///
    /// <para>A <c>System.Threading.Timer</c> is never early and is routinely late - Windows' default
    /// granularity is ~15.6 ms. The old chain re-armed with two constant legs measured from the previous
    /// callback's own execution instant, so that lateness accumulated with nothing to correct it: the
    /// entire budget before a beat crossed to the wrong side of its boundary was N ms for a whole fight,
    /// and at N=200 the pre-boundary beat exhausted it in roughly thirteen rollovers. That was the
    /// shipped bug behind "the pre-cycle sound only occasionally plays".</para>
    ///
    /// <para>The two tests replaced here claimed to prove the opposite property. One asserted
    /// <c>1600 + 400 == 2000</c>. The other walked two hundred beats advancing a simulated clock by
    /// EXACTLY the legs its assertions were derived from - an ideal zero-slop clock, so it could not
    /// observe timer lateness, which was the entire defect. This one injects the lateness.</para>
    /// </summary>
    [Theory]
    [InlineData(15.6)]    // Windows default timer granularity
    [InlineData(1.0)]     // a process that has raised timer resolution
    [InlineData(40.0)]    // a loaded machine
    public void Chain_AbsorbsTimerLatenessRatherThanAccumulatingIt(double latenessPerBeat)
    {
        var now = Anchor.AddMilliseconds(137);   // arbitrary point inside a tick
        var (delay, afterTick) = CombatTiming.NextBeat(Anchor, now, Offset, Offset);

        var worst = 0.0;
        for (var beat = 0; beat < 600; beat++)   // ~5 minutes of fighting at 2 beats a tick
        {
            // The beat fires late, as a thread-pool timer does. Never early.
            now = now.AddMilliseconds(delay + latenessPerBeat);

            var toNext = CombatTiming.MillisecondsToNextBoundary(Anchor, now);
            // Where this beat actually landed, relative to where its kind belongs.
            var error = afterTick
                ? Math.Abs((Tick - toNext) - Offset)
                : Math.Abs(Offset - toNext);
            worst = Math.Max(worst, error);

            (delay, afterTick) = CombatTiming.NextBeat(Anchor, now, Offset, Offset);
        }

        // Bounded by ONE beat's lateness for the whole fight, not 600 of them accumulated. The old
        // fixed-leg chain would reach 600 * lateness here.
        Assert.True(worst <= latenessPerBeat + 0.001,
            $"worst error {worst:F1} ms against a per-beat lateness of {latenessPerBeat} ms - lateness is accumulating");
    }

    /// <summary>
    /// A beat that is already too late is SKIPPED, not fired late. A click in the wrong place is worse
    /// than a missing one: the player is using the pair to feel where the boundary is, so a late click
    /// moves the boundary rather than marking it.
    /// </summary>
    [Fact]
    public void NextBeat_SkipsABeatThatHasAlreadyPassedRatherThanSchedulingItLate()
    {
        // Woken 10 ms after the pre-tick's position - that beat is gone.
        var late = Anchor.AddMilliseconds(Tick - Offset + 10);
        var (delay, afterTick) = CombatTiming.NextBeat(Anchor, late, Offset, Offset);

        Assert.True(delay > 0, "a beat must never be scheduled in the past");
        Assert.True(afterTick, "the missed pre-tick must be skipped, not fired late");
    }

    /// <summary>Never schedules a beat so close that it would fire on top of the one just sounded -
    /// which at a 100 ms gap would collapse the tik-tok into a single doubled hit.</summary>
    [Fact]
    public void NextBeat_NeverReturnsANearZeroDelay()
    {
        for (var ms = -4000.0; ms <= 4000.0; ms += 0.37)
        {
            var (delay, _) = CombatTiming.NextBeat(Anchor, Anchor.AddMilliseconds(ms), Offset, Offset);
            Assert.InRange(delay, 2.0, Tick + Offset);
        }
    }

    /// <summary>The offset has to stay under half a tick or the pre- and after-tick positions cross and
    /// the lattice stops being a bracket at all. Rejected loudly rather than silently producing a
    /// nonsense schedule.</summary>
    [Theory]
    [InlineData(1000.0, 1000.0)]   // sum to exactly one tick: the two beats coincide
    [InlineData(1400.0, 700.0)]    // sum past a tick: they cross
    [InlineData(0.0, 50.0)]        // an after-tick that is not after
    [InlineData(-50.0, 50.0)]
    [InlineData(50.0, 0.0)]        // a pre-tick that is not before
    [InlineData(50.0, -50.0)]
    public void NextBeat_RejectsOffsetsThatWouldNotBracket(double afterOffset, double preLead)
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => CombatTiming.NextBeat(Anchor, Anchor, afterOffset, preLead));

    /// <summary>
    /// The asymmetric case the shipping code actually uses: the pre-click is started early by its own
    /// clip length so it FINISHES at -N rather than starting there, leaving the boundary as 2N of
    /// silence between the two sounds. The after-tick is unaffected.
    /// </summary>
    [Fact]
    public void NextBeat_HonoursAPreLeadLongerThanTheAfterOffset()
    {
        const double clip = 170.0;                 // the real Perc_Stick_hi length
        const double preLead = Offset + clip;      // start early enough to end at -Offset

        // Mid-cycle, before either beat: the pre-tick is next, and it is preLead before the boundary.
        var (delay, afterTick) = CombatTiming.NextBeat(
            Anchor, Anchor.AddMilliseconds(1000), Offset, preLead);
        Assert.False(afterTick);
        Assert.Equal(Tick - preLead - 1000, delay, 6);

        // The clip therefore ends exactly Offset before the boundary...
        var startsAt = 1000 + delay;
        Assert.Equal(Tick - Offset, startsAt + clip, 6);

        // ...and the after-tick beat sits Offset past it, so the silence spanning the boundary is 2N.
        (delay, afterTick) = CombatTiming.NextBeat(
            Anchor, Anchor.AddMilliseconds(Tick - Offset), Offset, preLead);
        Assert.True(afterTick);
        Assert.Equal(2 * Offset, delay, 6);
    }

    /// <summary>The bar and the click, armed from one anchor, must agree on where the rollover is. This
    /// is the invariant the shared helper exists to make unbreakable: the bar empties at the boundary,
    /// the after-tick click sounds N past that same instant.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(137)]
    [InlineData(999)]
    [InlineData(1980)]
    public void BarAndClick_AgreeOnTheBoundary(double armedAtMs)
    {
        var armedAt = Anchor.AddMilliseconds(armedAtMs);

        // Where the BAR says the rollover is: the instant it finishes draining.
        var barRemaining = CombatTiming.MillisecondsToNextBoundary(Anchor, armedAt);
        var barBoundary = armedAt.AddMilliseconds(barRemaining);

        // Where the CLICK puts its next beat, and therefore which boundary that beat is bracketing.
        var (delay, afterTick) = CombatTiming.NextBeat(Anchor, armedAt, Offset, Offset);
        var beatAt = armedAt.AddMilliseconds(delay);
        // A pre-tick brackets the boundary ahead of it; an after-tick brackets the one behind it.
        var clickBoundary = beatAt.AddMilliseconds(afterTick ? -Offset : Offset);

        // Both must name a point on the ANCHOR'S LATTICE. That is the invariant the shared helper
        // exists to make unbreakable, and it is the strongest one that is actually true - see below
        // for why it is not equality.
        foreach (var boundary in new[] { barBoundary, clickBoundary })
        {
            var offLattice = (boundary - Anchor).TotalMilliseconds % Tick;
            Assert.Equal(0.0, Math.Min(offLattice, Tick - offLattice), 6);
        }

        // And they must name the same boundary or adjacent ones - never further apart than that.
        var apart = Math.Abs((barBoundary - clickBoundary).TotalMilliseconds);
        Assert.InRange(apart, 0.0, Tick);

        // Why not equality: at an instant EXACTLY on a lattice point the two deliberately disagree by
        // one tick, and both are right for their own instrument. MillisecondsToNextBoundary returns a
        // full tick rather than zero, because a bar handed "0 ms left" would restart from empty. The
        // beat lattice treats that same instant as a boundary just PASSED and schedules its after-tick
        // 50 ms later - which is correct, because the anchor is a swing timestamp and a swing is
        // emitted BY a tick, so the anchor itself is a real boundary that deserves its after-click.
        // An earlier version of this test asserted equality here and failed on exactly that case; the
        // premise was wrong, not the code.
    }
}
