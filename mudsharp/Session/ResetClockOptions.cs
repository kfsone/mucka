namespace MudSharp.Session;

/// <summary>
/// Tunables for <see cref="ResetClock"/>. Defaults target the author's setup (5 s FES heartbeat) and
/// the "lock once, near the start, then stop" model. Times are wall durations; the engine works in a
/// monotonic millisecond domain internally.
/// </summary>
public sealed class ResetClockOptions
{
    /// <summary>Coarse readings must have plateaued to at least this ± (seconds) before discovery arms.
    /// At a 5 s heartbeat the intersection collapses to ~2.5 s the first time two beats straddle a
    /// minute boundary.</summary>
    public double ArmUncertaintySec { get; init; } = 4.0;

    /// <summary>Success: once the window half-width is at or below this (seconds) the projection is
    /// locked and all probing stops for the session.</summary>
    public double SuccessTargetSec { get; init; } = 0.5;

    /// <summary>Begin discovery this many seconds before the predicted decrement — wider than the
    /// coarse uncertainty so we are already sampling when the real decrement lands.</summary>
    public double ApproachSec { get; init; } = 6.0;

    /// <summary>Spacing between discovery samples. Kept just above the server's ~500 ms probe rate
    /// limit so each FES probe is answered with a stats block (not a bare prompt). One in flight at a
    /// time, so the effective cadence is max(this, RTT); a 60 s edge brackets to ≈ this/2.</summary>
    public TimeSpan SampleSpacing { get; init; } = TimeSpan.FromMilliseconds(501);

    /// <summary>Clear-channel lead: suspend the routine heartbeat and wait at least this long (no
    /// regular FES enroute) before the first discovery sample, so our probe never races a compound
    /// heartbeat reply.</summary>
    public TimeSpan ClearChannelLead { get; init; } = TimeSpan.FromMilliseconds(1000);

    /// <summary>How long to wait for a sample's reply before treating it as unanswered (a bare prompt /
    /// collision / asleep). Covers a slow RTT. Logged so the rate-limit theory can be confirmed.</summary>
    public TimeSpan SampleTimeout { get; init; } = TimeSpan.FromMilliseconds(1200);

    /// <summary>Max samples one discovery pass may spend before giving up on this decrement and
    /// retrying at the next minute boundary (prediction off / player busy).</summary>
    public int SampleCap { get; init; } = 16;

    /// <summary>Total discovery samples across the whole session (across retried boundaries).
    /// Exhausting it stops probing and holds the best window achieved.</summary>
    public int ProbeBudget { get; init; } = 64;

    /// <summary>A discovery pass won't arm unless a fresh reading arrived within this window — asleep /
    /// stalled sessions produce no FES replies.</summary>
    public TimeSpan MaxReplyAgeToArm { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Post-reset (CoarseOnly) the heartbeat keeps FES at beat cadence until the new cycle's
    /// window has re-converged to at most this ± (seconds); after that — as when Locked — FES relaxes
    /// to the slow sweep cadence (<see cref="ResetClock.FesCadenceRelaxed"/>).</summary>
    public double RelaxedUncertaintySec { get; init; } = 3.0;

    /// <summary>The server-announced finish-up period after the main counter expires (C06 C04
    /// "Auto reset initiated, you have 120 seconds…"). Used to anchor the final countdown exactly.</summary>
    public TimeSpan FinishUpDuration { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>When true (production) the engine owns an internal timer to pace samples and detect
    /// timeouts. Unit tests set false and drive <see cref="ResetClock.PumpForTest"/> manually.</summary>
    public bool SelfSchedule { get; init; } = true;
}
