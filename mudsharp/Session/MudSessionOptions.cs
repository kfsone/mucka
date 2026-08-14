namespace MudSharp.Session;

public sealed class MudSessionOptions
{
    /// <summary>Interval between FES heartbeat subscriptions while in game mode. Default: 10 seconds.</summary>
    public TimeSpan FesHeartbeatInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Interval between injected `score` refreshes while in game mode. The score sheet is the ONLY
    /// source for carried weight, objects carried, persona value and sex — the FES heartbeat carries
    /// none of them — so without this they would hold their game-entry values for the whole session.
    /// Kept far slower than the FES beat because a `score` costs a game turn. Zero or negative
    /// disables the refresh (the game-entry setup batch still runs). Default: 5 minutes.
    /// </summary>
    public TimeSpan ScoreRefreshInterval { get; init; } = TimeSpan.FromMinutes(5);

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

    /// <summary>
    /// How long an FES-carrying probe may remain unanswered before incoming server data reads
    /// as a wake-up (the character was asleep — probes no-op during sleep) and fires an
    /// immediate recovery beat. Default: 5 seconds.
    /// </summary>
    public TimeSpan WakeReplySlack { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Tunables for the reset-time projection / staged precision burst (see ResetClock).</summary>
    public ResetClockOptions ResetClock { get; init; } = new();

    /// <summary>
    /// How long to wait after a room description arrives (<c>RoomEntered</c>) for an accompanying
    /// FEX list before assuming none is coming and sending an explicit probe. Ordinary movement's
    /// auto-fex list normally arrives in the same transmission, well within this window; a
    /// spell-driven relocation (resite, supersite, and any future same-shaped mechanic) fires no
    /// auto commands at all, so nothing arrives and the probe fires instead. Default: 1750ms.
    /// </summary>
    public TimeSpan RoomEntryFexProbeDelay { get; init; } = TimeSpan.FromMilliseconds(1750);
}
