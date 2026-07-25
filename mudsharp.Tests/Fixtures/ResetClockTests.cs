using MudSharp.Session;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// Tests for <see cref="ResetClock"/>: floored-minute interval model with RTT-corrected intersection,
/// the channel-owning edge-search that locks once then stops, asymmetric re-anchoring (upward jump =
/// server reset event; a downward contradiction of a lock is annotated + re-verified, never ignored and
/// never a panic to coarse), the exact C06 C04 finish-up anchor, and the eligibility/coordination
/// gates. Driven with an injected monotonic clock and <see cref="ResetClockOptions.SelfSchedule"/> off.
/// </summary>
public class ResetClockTests
{
    private const long R = 1_000_000;

    private sealed class Harness
    {
        public long NowMs;
        public int Sends;
        public bool CanProbe = true;
        public bool DiscoveryHold;
        public readonly List<string> Notes = new();
        public readonly ResetClock Clock;

        public Harness(ResetClockOptions? o = null)
        {
            Clock = new ResetClock(o ?? Options(), Send, () => CanProbe, SetHold, () => NowMs);
            Clock.DiagnosticNote += Notes.Add;
        }

        private bool Send() { Sends++; return true; }
        private void SetHold(bool h) => DiscoveryHold = h;
        public ResetEstimate Snap => Clock.Snapshot();
    }

    private static ResetClockOptions Options() => new()
    {
        SelfSchedule     = false,
        ArmUncertaintySec = 4.0,
        SuccessTargetSec = 0.5,
        ApproachSec      = 6.0,
        SampleSpacing    = TimeSpan.FromMilliseconds(501),
        ClearChannelLead = TimeSpan.FromMilliseconds(1000),
        SampleTimeout    = TimeSpan.FromMilliseconds(1200),
        SampleCap        = 16,
        ProbeBudget      = 64,
        MaxReplyAgeToArm = TimeSpan.FromSeconds(15),
        FinishUpDuration = TimeSpan.FromSeconds(120),
    };

    private static int Val(long t) => (int)((R - t) / 60_000);

    private static void Routine(Harness h, long t)
    {
        h.NowMs = t;
        h.Clock.Observe(Val(t), fresh: true, t);
    }

    // Drive a discovery pass: honour the 1 s clear-channel lead, then send/reply one-in-flight at 501 ms
    // until the phase leaves Discovering.
    private static void DriveDiscovery(Harness h, long armMs, long rtt)
    {
        long firstSample = armMs + 1000;          // ClearChannelLead
        h.NowMs = firstSample;
        h.Clock.PumpForTest();                    // fires sample #1
        long sendT = firstSample;
        for (int i = 0; i < 60; i++)
        {
            long replyT = sendT + rtt;
            h.NowMs = replyT;
            h.Clock.Observe(Val(replyT), fresh: true, replyT);
            if (h.Snap.Phase != ResetPhase.Discovering) return;
            long nextSend = sendT + 501;
            h.NowMs = nextSend;
            int before = h.Sends;
            h.Clock.PumpForTest();
            if (h.Sends == before) return;
            sendT = nextSend;
        }
    }

    private static Harness CoarseStraddled()
    {
        var h = new Harness();
        h.Clock.OnGameModeEntered();
        Routine(h, 817_500);   // v=3
        Routine(h, 822_500);   // v=2, 5 s later — straddles the 3→2 boundary
        return h;
    }

    private static Harness LockedViaDiscovery()
    {
        var h = CoarseStraddled();
        Routine(h, 874_000);                      // ~6 s out → arms discovery (no sample until +1 s lead)
        DriveDiscovery(h, 874_000, rtt: 50);
        return h;
    }

    [Fact]
    public void FirstSighting_BootstrapsCoarseWindow()
    {
        var h = new Harness();
        h.Clock.OnGameModeEntered();
        Routine(h, 1_000);
        Assert.Equal(ResetPhase.Coarse, h.Snap.Phase);
        Assert.Equal(30.0, h.Snap.UncertaintySec, 3);
        Assert.Equal(0, h.Sends);
    }

    [Fact]
    public void NotInGame_IgnoresReadings()
    {
        var h = new Harness();
        h.Clock.Observe(5, fresh: true, 1_000);
        Assert.Null(h.Snap.TargetUtc);
    }

    [Fact]
    public void CarriedForwardValue_Ignored()
    {
        var h = new Harness();
        h.Clock.OnGameModeEntered();
        h.Clock.Observe(5, fresh: false, 1_000);
        Assert.Null(h.Snap.TargetUtc);
    }

    [Fact]
    public void CleanTransition_HalfGapUncertainty_NoProbeYet()
    {
        var h = CoarseStraddled();
        Assert.Equal(ResetPhase.Coarse, h.Snap.Phase);
        Assert.Equal(2.5, h.Snap.UncertaintySec, 2);
        Assert.Equal(0, h.Sends);
    }

    [Fact]
    public void Discovery_WaitsClearChannelLead_BeforeFirstSample()
    {
        var h = CoarseStraddled();
        Routine(h, 874_000);
        Assert.Equal(ResetPhase.Discovering, h.Snap.Phase);
        Assert.True(h.DiscoveryHold, "routine heartbeat should be suspended during discovery");
        Assert.Equal(0, h.Sends);                 // nothing sent until the 1 s clear-channel lead elapses

        h.NowMs = 874_500;                         // still inside the lead
        h.Clock.PumpForTest();
        Assert.Equal(0, h.Sends);

        h.NowMs = 875_000;                         // lead elapsed
        h.Clock.PumpForTest();
        Assert.Equal(1, h.Sends);
    }

    [Fact]
    public void SingleDiscovery_LocksWithinTarget_AndReleasesChannel()
    {
        var h = LockedViaDiscovery();
        Assert.Equal(ResetPhase.Locked, h.Snap.Phase);
        Assert.True(h.Snap.UncertaintySec <= 0.5, $"expected ≤±0.5 s lock, got ±{h.Snap.UncertaintySec:F2}s");
        Assert.False(h.DiscoveryHold, "routine heartbeat must be resumed after locking");
        double secsToTarget = (h.Snap.TargetUtc!.Value - DateTime.UtcNow).TotalSeconds;
        double estTargetMs = h.NowMs + secsToTarget * 1000;
        Assert.True(Math.Abs(estTargetMs - R) <= 500, $"locked target off by {estTargetMs - R:F0} ms");
    }

    [Fact]
    public void Locked_TrustsConsistent_ReAnchorsOnUpwardJump()
    {
        var h = LockedViaDiscovery();
        int sendsAtLock = h.Sends;

        h.NowMs = 900_000;
        h.Clock.Observe(Val(900_000), fresh: true, 900_000);   // consistent
        Assert.Equal(ResetPhase.Locked, h.Snap.Phase);
        Assert.Equal(sendsAtLock, h.Sends);

        h.NowMs = 905_000;
        h.Clock.Observe(90, fresh: true, 905_000);             // reset fired (time up)
        Assert.Equal(ResetPhase.CoarseOnly, h.Snap.Phase);
    }

    [Fact]
    public void EarlyDecrement_ContradictsLock_Annotates_AndReopens()
    {
        var h = LockedViaDiscovery();
        Assert.Equal(ResetPhase.Locked, h.Snap.Phase);

        // A reading whose remaining is BELOW the locked window (the reset came sooner — an early
        // decrement like the observed v9→v8): must annotate and reopen, not silently hold a stale lock.
        h.NowMs = 900_000;
        h.Clock.Observe(0, fresh: true, 900_000);
        Assert.Contains(h.Notes, n => n.Contains("contradicted"));
        Assert.NotEqual(ResetPhase.Locked, h.Snap.Phase);      // reopened (eligible to re-verify)
    }

    [Fact]
    public void AutoResetInitiated_AnchorsFinishUpExactly()
    {
        var h = new Harness();
        h.Clock.OnGameModeEntered();
        Routine(h, 500_000);                        // some coarse window
        h.NowMs = 600_000;
        h.Clock.NoteAutoResetInitiated(600_000);

        var s = h.Snap;
        Assert.Equal(ResetPhase.Locked, s.Phase);
        Assert.True(s.UncertaintySec <= 0.5);
        Assert.False(h.DiscoveryHold);
        double secs = (s.TargetUtc!.Value - DateTime.UtcNow).TotalSeconds;
        Assert.InRange(secs, 119.0, 121.0);         // reset in ~120 s (RTT/2-corrected)
        Assert.Contains(h.Notes, n => n.Contains("auto-reset"));
    }

    [Fact]
    public void OncePerSession_NoResweepAfterLock()
    {
        var h = LockedViaDiscovery();
        int sendsAtLock = h.Sends;
        h.NowMs = 999_000;
        h.Clock.PumpForTest();
        Assert.Equal(sendsAtLock, h.Sends);
        Assert.Equal(ResetPhase.Locked, h.Snap.Phase);
    }

    [Fact]
    public void ResetBeforeLock_StaysEligibleOnNewCycle()
    {
        var h = CoarseStraddled();
        h.NowMs = 830_000;
        h.Clock.Observe(90, fresh: true, 830_000);   // reset fired before we ever locked
        Assert.Equal(ResetPhase.Coarse, h.Snap.Phase);
    }

    [Fact]
    public void Relog_ReArmsRefinement()
    {
        var h = LockedViaDiscovery();
        Assert.Equal(ResetPhase.Locked, h.Snap.Phase);

        h.Clock.OnGameModeExited();
        Assert.Null(h.Snap.TargetUtc);

        h.Clock.OnGameModeEntered();
        Routine(h, 817_500);
        Routine(h, 822_500);
        Routine(h, 874_000);
        Assert.Equal(ResetPhase.Discovering, h.Snap.Phase);
        Assert.True(h.DiscoveryHold);
    }

    [Fact]
    public void StaleSession_DoesNotArm()
    {
        var h = CoarseStraddled();
        h.NowMs = 874_000;            // ~51 s since the last routine reading — stale
        h.Clock.PumpForTest();
        Assert.Equal(0, h.Sends);
        Assert.Equal(ResetPhase.Coarse, h.Snap.Phase);
        Assert.False(h.DiscoveryHold);
    }

    [Fact]
    public void ProbesHeld_DoesNotArm_ResumesAfterRelease()
    {
        var h = CoarseStraddled();
        h.CanProbe = false;
        Routine(h, 874_000);
        Assert.Equal(0, h.Sends);
        Assert.Equal(ResetPhase.Coarse, h.Snap.Phase);

        h.CanProbe = true;
        Routine(h, 875_000);
        Assert.Equal(ResetPhase.Discovering, h.Snap.Phase);
        Assert.True(h.DiscoveryHold);
    }
}
