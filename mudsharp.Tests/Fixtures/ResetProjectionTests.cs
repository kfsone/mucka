using MudSharp.Session;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// Tests for <see cref="ResetProjection"/>: the floored-minute interval model, its midpoint anchor
/// and gap/2 uncertainty, sub-second convergence from near-boundary readings, precision-probe
/// scheduling (one per boundary, only while converging), floor back-off, and re-bootstrap on a
/// reset. All timings are supplied explicitly so the tests are deterministic.
/// </summary>
public class ResetProjectionTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static DateTime At(double sec) => T0 + TimeSpan.FromSeconds(sec);

    [Fact]
    public void FirstSighting_AnchorsMinuteMidpoint()
    {
        var p = new ResetProjection();
        p.Observe(5, fresh: true, T0);
        // A value of 5 means remaining ∈ [5, 6) min; anchor the midpoint (5·60 + 30 s), ±30 s.
        Assert.Equal(At(5 * 60 + 30), p.TargetUtc);
        Assert.Equal(30.0, p.UncertaintySec, 3);
    }

    [Fact]
    public void CarriedForwardValue_Ignored()
    {
        var p = new ResetProjection();
        p.Observe(5, fresh: false, T0);   // combat/text line echoing the last value — not a reading
        Assert.Null(p.TargetUtc);
    }

    [Fact]
    public void CarriedForward_DoesNotTightenWindow()
    {
        var p = new ResetProjection();
        p.Observe(5, fresh: true, T0);
        var before = p.UncertaintySec;
        p.Observe(4, fresh: false, At(5));   // would look like a transition, but it's not fresh
        Assert.Equal(before, p.UncertaintySec, 5);
        Assert.Equal(At(5 * 60 + 30), p.TargetUtc);
    }

    [Fact]
    public void CleanTransition_MidpointAnchor_HalfGapUncertainty()
    {
        var p = new ResetProjection();
        p.Observe(5, fresh: true, T0);
        p.Observe(4, fresh: true, At(5));   // 5→4 with a 5 s gap
        // Intersection → window [T0+300, T0+305]: target at the midpoint, ±half the gap.
        Assert.Equal(At(302.5), p.TargetUtc);
        Assert.Equal(2.5, p.UncertaintySec, 3);
    }

    [Fact]
    public void StraddlingReadings_ConvergeSubSecond_AndStopProbing()
    {
        var p = new ResetProjection();
        var r = At(302.5);   // pretend true reset instant
        // One reading just after a boundary and one just before the next pin R from both edges.
        p.Observe(4, fresh: true, r - TimeSpan.FromSeconds(299.9));
        p.Observe(4, fresh: true, r - TimeSpan.FromSeconds(240.1));
        Assert.True(p.UncertaintySec < 1.0, $"expected sub-second, got ±{p.UncertaintySec:F2}s");
        // Sub-second: never worth a game turn.
        Assert.False(p.TryGetPrecisionProbeDue(r - TimeSpan.FromSeconds(120.4)));
    }

    [Fact]
    public void ProbeDue_OnlyWhenConvergingAndNearBoundary()
    {
        var p = new ResetProjection();
        p.Observe(5, fresh: true, T0);
        p.Observe(4, fresh: true, At(5));   // ±2.5 s, target T0+302.5
        var target = p.TargetUtc!.Value;

        // Just before a boundary (seconds-to-reset ≈ a multiple of 60): due.
        Assert.True(p.TryGetPrecisionProbeDue(target - TimeSpan.FromSeconds(120.4)));
        // Mid-minute: not near a boundary, nothing to gain.
        Assert.False(p.TryGetPrecisionProbeDue(target - TimeSpan.FromSeconds(130)));
    }

    [Fact]
    public void ProbeNotDue_WhenTooCoarse()
    {
        var p = new ResetProjection();
        p.Observe(5, fresh: true, T0);   // lone sighting: ±30 s, above ProbeStartUncertaintySec
        var target = p.TargetUtc!.Value;
        Assert.False(p.TryGetPrecisionProbeDue(target - TimeSpan.FromSeconds(120.4)));
    }

    [Fact]
    public void OneProbePerBoundary()
    {
        var p = new ResetProjection();
        p.Observe(5, fresh: true, T0);
        p.Observe(4, fresh: true, At(5));
        var target = p.TargetUtc!.Value;
        var now = target - TimeSpan.FromSeconds(120.4);

        Assert.True(p.TryGetPrecisionProbeDue(now));
        p.NotePrecisionProbeSent(now);
        // Same boundary a moment later — already spent our turn on it.
        Assert.False(p.TryGetPrecisionProbeDue(now + TimeSpan.FromSeconds(0.3)));
    }

    [Fact]
    public void FloorBackoff_PausesProbing_AfterNonImprovingReplies()
    {
        var p = new ResetProjection();
        p.Observe(5, fresh: true, T0);
        p.Observe(4, fresh: true, At(5));   // ±2.5 s
        var target = p.TargetUtc!.Value;
        var now = target - TimeSpan.FromSeconds(120.4);

        for (int i = 0; i < 3; i++)
        {
            Assert.True(p.TryGetPrecisionProbeDue(now));
            p.NotePrecisionProbeSent(now);
            // A reply whose 60 s interval already contains the window carries no new information.
            p.Observe(0, fresh: true, target - TimeSpan.FromSeconds(30));
            now += TimeSpan.FromSeconds(60);
        }

        Assert.True(p.UncertaintySec > 1.0);   // never got dialled in...
        // ...and we've stopped spending turns on it.
        Assert.False(p.TryGetPrecisionProbeDue(target - TimeSpan.FromSeconds(120.4)));
    }

    [Fact]
    public void DisjointReading_ReBootstraps()
    {
        var p = new ResetProjection();
        p.Observe(4, fresh: true, T0);          // window [T0+240, T0+300)
        p.Observe(90, fresh: true, At(1));      // a reset fired: value jumps ~90 min out
        Assert.Equal(At(1) + TimeSpan.FromSeconds(90 * 60 + 30), p.TargetUtc);
        Assert.Equal(30.0, p.UncertaintySec, 3);
    }

    [Fact]
    public void Tick_ClearsAfterWindowLapses()
    {
        var p = new ResetProjection();
        p.Observe(1, fresh: true, T0);          // window [T0+60, T0+120)
        p.Tick(At(200));                        // entire window is in the past
        Assert.Null(p.TargetUtc);
    }
}
