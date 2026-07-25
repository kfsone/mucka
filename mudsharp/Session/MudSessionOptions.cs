namespace MudSharp.Session;

public sealed class MudSessionOptions
{
    /// <summary>Interval between FES heartbeat subscriptions while in game mode. Default: 10 seconds.</summary>
    public TimeSpan FesHeartbeatInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Whether to buffer outgoing lines during parser reconnect/reset. Default: true.</summary>
    public bool BufferOnReset { get; init; } = true;

    /// <summary>
    /// Delay between a C1 stale-stats hint and the reactive probe it triggers, giving the
    /// server's own follow-up (e.g. the inline "(sta/max)" after a hit) a chance to arrive
    /// and cancel the probe. Default: 200ms.
    /// </summary>
    public TimeSpan StaleProbeDelay { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Minimum spacing between any two outgoing probes (routine or reactive). Probe
    /// commands cost the player a game turn, so reactive probes are rate-limited and
    /// skipped entirely when the routine heartbeat is about to fire anyway. Default: 500ms.
    /// </summary>
    public TimeSpan MinProbeSpacing { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Tunables for the reset-time projection / staged precision burst (see ResetClock).</summary>
    public ResetClockOptions ResetClock { get; init; } = new();
}
