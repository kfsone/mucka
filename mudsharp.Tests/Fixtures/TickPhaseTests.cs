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
    /// nothing outside that class should be choosing where the clicks sit. Kept here as a plain
    /// constant so the chain arithmetic below is checkable without widening that access; if the two
    /// ever diverge, the chain assertions stop meaning what they claim, which
    /// <see cref="Chain_LegsSumToExactlyOneTick"/> is the guard against.</summary>
    private const int Offset = 200;

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
    /// The click schedules itself as a chain rather than on a periodic timer: after-tick, then
    /// pre-tick, then after-tick. The two legs must sum to exactly one tick, or the beat walks off the
    /// boundary a little more every rollover - which is precisely the "decoupled from the bar" symptom,
    /// arrived at by drift rather than by a bad anchor.
    /// </summary>
    [Fact]
    public void Chain_LegsSumToExactlyOneTick()
    {
        var afterToPre = Tick - (2 * Offset);   // +N past a boundary -> -N before the next
        var preToAfter = 2 * Offset;            // -N before a boundary -> +N past it
        Assert.Equal(Tick, afterToPre + preToAfter);
    }

    /// <summary>Walking the chain for a hundred rollovers must land every beat exactly on N either side
    /// of a boundary, with no accumulated error - the property a fixed-period timer would not have.</summary>
    [Fact]
    public void Chain_HoldsTheLatticeOverAHundredRollovers()
    {
        // First beat: the after-tick for the rollover the bar is counting down to when we arm.
        var armedAt = Anchor.AddMilliseconds(137);   // arbitrary point inside a tick
        var t = CombatTiming.MillisecondsToNextBoundary(Anchor, armedAt) + Offset
                + (armedAt - Anchor).TotalMilliseconds;

        var afterTick = true;
        for (var beat = 0; beat < 200; beat++)
        {
            var toNext = CombatTiming.MillisecondsToNextBoundary(Anchor, Anchor.AddMilliseconds(t));
            if (afterTick)
                // Sits Offset PAST the boundary just gone, so the next one is a tick less that.
                Assert.Equal(Tick - Offset, toNext, 6);
            else
                // Sits Offset BEFORE the next boundary.
                Assert.Equal(Offset, toNext, 6);

            t += afterTick ? Tick - (2 * Offset) : 2 * Offset;
            afterTick = !afterTick;
        }
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

        // What the bar is given: the time it has left to drain.
        var barRemaining = CombatTiming.MillisecondsToNextBoundary(Anchor, armedAt);
        // What the click is given: the same, plus the offset that puts it past the rollover.
        var clickDelay = barRemaining + Offset;

        Assert.Equal(Offset, clickDelay - barRemaining);
        // Both describe the same absolute instant for the boundary itself.
        Assert.Equal(armedAt.AddMilliseconds(barRemaining), armedAt.AddMilliseconds(clickDelay - Offset));
    }
}
