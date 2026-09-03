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

    /// <summary>
    /// How long an FES-carrying probe may remain unanswered before incoming server data reads
    /// as a wake-up (the character was asleep — probes no-op during sleep) and fires an
    /// immediate recovery beat. Default: 5 seconds.
    /// </summary>
    public TimeSpan WakeReplySlack { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Quiet period after the LAST observed inventory-change line before the in-combat inventory
    /// probe is sent (see MudSession.NoteInventoryChangeLine). Trailing, not leading: a bulk
    /// command ("dr t") delivers every "X dropped." line in one server frame - the owner's own clog
    /// has three items landing inside the same millisecond - and the probe must cost one tick for
    /// the burst rather than one per item. It is also the window in which a player command can
    /// carry the probe out with it (MudSession.SendLine). Default: 100ms, the owner's figure.
    /// </summary>
    public TimeSpan InventoryProbeDebounce { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// How close to the next combat-tick boundary the inventory probe refuses to fire. Commands are
    /// drained from a server-side queue one per tick, so a probe landing immediately before a
    /// boundary competes for the slot the player's own action wanted. Inside this window the probe
    /// is moved to the far side of the boundary instead. Default: 200ms, the owner's figure.
    /// </summary>
    public TimeSpan InventoryProbeTickGuard { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// How far PAST the boundary a guarded inventory probe is placed - the position with the longest
    /// clear run before the following one. Also the slack that absorbs the phase estimate's own
    /// error, which fits a whole session to a ~26ms median residual. Default: 50ms, the owner's
    /// figure.
    /// </summary>
    public TimeSpan InventoryProbeTickClearance { get; init; } = TimeSpan.FromMilliseconds(50);

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

    /// <summary>
    /// Backstop for the in-combat creature-value probe (see MudSession's "In-combat creature value
    /// probe" remarks): if the outstanding batch has not been fully accounted for (every requested
    /// name has drawn a reply or a bad-target rejection) by this long after it was sent, the window
    /// is force-closed and whatever names are left simply stay unknown. A name can legitimately
    /// never draw a reply - the match was ambiguous and consumed by another slot, or the creature
    /// left before answering - and without this bound such a name would wedge the window open
    /// forever, since the window's normal close condition (every name accounted for, at the next
    /// frame boundary) would then never fire. Default: 5 seconds - generous next to the other
    /// timings in this file, since this is purely a give-up bound and not itself timing-sensitive.
    /// </summary>
    public TimeSpan CreatureValueProbeTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
