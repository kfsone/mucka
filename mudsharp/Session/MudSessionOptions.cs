namespace MudSharp.Session;

public sealed class MudSessionOptions
{
    /// <summary>Interval between FES heartbeat subscriptions while in game mode. Default: 10 seconds.</summary>
    public TimeSpan FesHeartbeatInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Whether to buffer outgoing lines during parser reconnect/reset. Default: true.</summary>
    public bool BufferOnReset { get; init; } = true;
}
