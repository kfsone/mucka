using Mucka.Core;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// The session-scoped tick-phase estimate that replaced a single per-encounter sample.
///
/// <para>Measured motivation, from <c>tools/combat/sessionlattice.py</c> over the live clog corpus: the
/// old first-swing anchor was off by more than 150 ms in 18.9% of encounters and by up to 963 ms - half
/// a tick - against a session-wide lattice that itself fits to a median 26.5 ms. These tests pin the
/// properties that make the estimator better than that, not the arithmetic it uses to get there.</para>
/// </summary>
public class TickPhaseEstimatorTests
{
    private const double Tick = CombatTiming.TickMilliseconds;
    private static readonly DateTime T0 = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Folded signed distance to the nearest lattice point of <paramref name="anchor"/>.</summary>
    private static double Error(DateTime anchor, DateTime truth)
    {
        var r = (anchor - truth).TotalMilliseconds % Tick;
        if (r < 0) r += Tick;
        return Math.Abs(r > Tick / 2 ? r - Tick : r);
    }

    private static TickPhase Feed(DateTime truth, IEnumerable<double> jittersMs, int tickStep = 1)
    {
        var phase = new TickPhase();
        var k = 0;
        foreach (var j in jittersMs)
        {
            phase.Observe(truth.AddMilliseconds((k * tickStep * Tick) + j));
            k++;
        }
        return phase;
    }

    [Fact]
    public void NoAnchorUntilEnoughSwingsHaveBeenSeen()
    {
        // Deliberately does not hard-code the threshold: what matters is that ONE swing is never enough
        // (that was the old behaviour, tail and all) and that the estimate does eventually arrive.
        var phase = new TickPhase();
        Assert.Null(phase.Anchor);
        phase.Observe(T0);
        Assert.Null(phase.Anchor);

        for (var k = 1; k <= 8 && phase.Anchor is null; k++)
            phase.Observe(T0.AddMilliseconds(k * Tick));

        Assert.NotNull(phase.Anchor);
        Assert.True(phase.Samples > 1, "one sample must never be published as a lattice");
    }

    /// <summary>
    /// The first sample becomes the reference, so it sits at angle zero and always votes for itself. Two
    /// correlated bad openers - plausible in a pack fight, where several participants' swings land in one
    /// frame - must not be enough to get a wrong lattice published.
    /// </summary>
    [Fact]
    public void TwoCorrelatedBadOpenersDoNotGetPublished()
    {
        var phase = new TickPhase();
        phase.Observe(T0.AddMilliseconds(700));      // keystroke-phased opener, becomes the reference
        phase.Observe(T0.AddMilliseconds(690));      // a second swing in the same frame, agreeing with it
        // The BAR may take this - it corrects visibly and that is honest. The CLICK must not: a sound
        // bracketing a boundary that is not there has nothing on screen to explain itself.
        Assert.False(phase.IsSettled);

        // The honest swings that follow must win.
        for (var k = 1; k <= 12; k++)
            phase.Observe(T0.AddMilliseconds(k * Tick));
        Assert.NotNull(phase.Anchor);
        Assert.True(Error(phase.Anchor!.Value, T0) < 60.0,
            $"the correlated pair still dominates: {Error(phase.Anchor!.Value, T0):F1} ms");
    }

    [Fact]
    public void ConvergesOnTheTruthDespiteOrdinaryJitter()
    {
        // The corpus says text lands within 25 ms of the lattice ~88% of the time, tailing to ~196 ms
        // on about one swing-carrying tick in eleven. This is that shape.
        var rng = new Random(1234);
        var jitters = new List<double>();
        for (var i = 0; i < 120; i++)
            jitters.Add(rng.Next(11) == 0 ? rng.Next(0, 200) : rng.Next(-25, 26));

        var phase = Feed(T0, jitters);
        Assert.NotNull(phase.Anchor);
        Assert.True(Error(phase.Anchor!.Value, T0) < 25.0,
            $"error {Error(phase.Anchor.Value, T0):F1} ms after 120 swings");
    }

    /// <summary>
    /// THE case this class exists for. A keystroke-phased opening swing - the player's own `kill` reply
    /// arriving in the same frame as the first swing, which the corpus shows is over 100 ms out 52.1% of
    /// the time against 18.4% for later openers (tools/combat/opener_phase.py) - must be out-voted by the
    /// swings that follow rather than defining the lattice for the whole fight.
    /// </summary>
    [Theory]
    [InlineData(900.0)]     // near the worst measured (963 ms)
    [InlineData(-800.0)]
    [InlineData(400.0)]
    public void OutvotesAKeystrokePhasedOpeningSwing(double openerErrorMs)
    {
        var phase = new TickPhase();
        phase.Observe(T0.AddMilliseconds(openerErrorMs));                 // the bad one, first
        for (var k = 1; k <= 30; k++)
            phase.Observe(T0.AddMilliseconds(k * Tick + (k % 3) - 1));    // honest swings, +/-1 ms

        Assert.NotNull(phase.Anchor);
        var error = Error(phase.Anchor!.Value, T0);
        Assert.True(error < 25.0, $"the bad opener still dominates: error {error:F1} ms");
    }

    [Fact]
    public void SurvivesAnEncounterBoundary()
    {
        // No Reset between fights - that was the bug. A second encounter starts with the phase the first
        // one established rather than re-deriving it from one sample.
        var phase = new TickPhase();
        for (var k = 0; k < 20; k++)
            phase.Observe(T0.AddMilliseconds(k * Tick));
        var afterFirstFight = phase.Anchor;

        Assert.NotNull(afterFirstFight);
        // ... a gap of several minutes with no combat, then a new fight opens with a bad first swing.
        phase.Observe(T0.AddMilliseconds((200 * Tick) + 900));
        Assert.True(Error(phase.Anchor!.Value, T0) < 60.0,
            "one bad swing in a new encounter must not move a converged estimate far");
    }

    [Fact]
    public void FollowsRealDriftRatherThanAveragingItAway()
    {
        // The spec's own figure is ~4 ppm, which over a long session is real. An estimator that weighted
        // every historical swing equally would lag it; this one forgets.
        var phase = new TickPhase();
        for (var k = 0; k < 400; k++)
            phase.Observe(T0.AddMilliseconds(k * Tick));            // settle on T0's lattice

        // Now the server's lattice is 120 ms later. Feed a few hundred swings on the NEW phase.
        var moved = T0.AddMilliseconds(120);
        for (var k = 400; k < 1000; k++)
            phase.Observe(moved.AddMilliseconds(k * Tick));

        Assert.True(Error(phase.Anchor!.Value, moved) < 25.0,
            $"did not follow the drift: {Error(phase.Anchor!.Value, moved):F1} ms from the new phase");
    }

    [Fact]
    public void ReportsWhenTheAnchorHasActuallyMoved()
    {
        // Every republish restarts the bar's Composition animation, so Observe must not claim a move on
        // every swing once the estimate has settled.
        var phase = new TickPhase();
        for (var k = 0; k < 60; k++)
            phase.Observe(T0.AddMilliseconds(k * Tick));

        var moves = 0;
        for (var k = 60; k < 160; k++)
            if (phase.Observe(T0.AddMilliseconds((k * Tick) + ((k % 5) - 2))))   // +/-2 ms noise
                moves++;

        Assert.True(moves <= 3, $"republished {moves} times over 100 settled swings");
    }

    [Fact]
    public void ConcentrationIsHighWhenSwingsAgreeAndLowWhenTheyDoNot()
    {
        var agreeing = Feed(T0, Enumerable.Repeat(0.0, 40));
        Assert.True(agreeing.Concentration > 0.95, $"agreeing: {agreeing.Concentration:F3}");

        // Uniformly scattered across the whole tick - no lattice at all.
        var rng = new Random(7);
        var scattered = Feed(T0, Enumerable.Range(0, 200).Select(_ => rng.NextDouble() * Tick));
        Assert.True(scattered.Concentration < 0.35, $"scattered: {scattered.Concentration:F3}");
    }

    [Fact]
    public void HandlesResidualsSittingRightOnTheWrap()
    {
        // Half a tick either way is the same phase. A plain mean or median of folded residuals averages
        // these two identical readings into a phase half a tick away; circular statistics do not.
        var phase = new TickPhase();
        phase.Observe(T0);
        for (var k = 1; k <= 40; k++)
            phase.Observe(T0.AddMilliseconds((k * Tick) + (k % 2 == 0 ? 995 : -995)));

        // The true phase here is T0 + ~995 (equivalently T0 - 1005); either reading is correct, and what
        // must NOT happen is landing on T0 itself, which is what a non-circular average would give.
        Assert.NotNull(phase.Anchor);
        Assert.True(Error(phase.Anchor!.Value, T0) > 900.0,
            $"collapsed to the midpoint of a wrapped pair: {Error(phase.Anchor!.Value, T0):F1} ms from T0");
    }

    /// <summary>The bar gets the estimate before the click does, and that split is deliberate - see
    /// TickPhase.SettledSamples. Raising the bar to the click's threshold once left the ticker dark for
    /// the opening of a fight, which the owner read as nothing happening.</summary>
    [Fact]
    public void TheBarGetsThePhaseBeforeTheClickDoes()
    {
        var phase = new TickPhase();
        phase.Observe(T0);
        phase.Observe(T0.AddMilliseconds(Tick));

        Assert.NotNull(phase.Anchor);        // the bar may draw
        Assert.False(phase.IsSettled);       // the click stays silent

        for (var k = 2; k < 6; k++)
            phase.Observe(T0.AddMilliseconds(k * Tick));
        Assert.True(phase.IsSettled);
    }

    [Fact]
    public void ResetDiscardsEverything()
    {
        var phase = Feed(T0, Enumerable.Repeat(0.0, 10));
        Assert.NotNull(phase.Anchor);
        phase.Reset();
        Assert.Null(phase.Anchor);
        Assert.Equal(0, phase.Samples);
    }
}
