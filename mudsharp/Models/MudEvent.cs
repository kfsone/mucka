namespace MudSharp.Models;

/// <summary>
/// Base class for all events emitted by MudStreamParser.
/// All events fire on the caller's thread (the thread that called Feed()).
/// Consumers are responsible for marshaling to their UI thread.
/// </summary>
public abstract class MudEvent { }

/// <summary>A complete or partial line of styled text is ready to display.</summary>
public sealed class LineReadyEvent(StyledLine Line) : MudEvent
{
    public StyledLine Line { get; } = Line;
}

/// <summary>FES stats snapshot updated.</summary>
public sealed class StatsUpdatedEvent(GameStatsSnapshot Stats) : MudEvent
{
    public GameStatsSnapshot Stats { get; } = Stats;
}

/// <summary>Server has signalled game-mode entry (0x9D 0x9C 0xFF 0xFF).</summary>
public sealed class GameModeEnteredEvent : MudEvent { }

/// <summary>Parser has exited game mode (connection closed / reset).</summary>
public sealed class GameModeExitedEvent : MudEvent { }

/// <summary>Parser wants to send bytes to the server (e.g. telnet negotiation replies, FES subscription).</summary>
public sealed class OutgoingBytesEvent(byte[] Bytes) : MudEvent
{
    public byte[] Bytes { get; } = Bytes;
}

/// <summary>The dreamword has changed. Null = cleared.</summary>
public sealed class DreamwordChangedEvent(string? Dreamword) : MudEvent
{
    public string? Dreamword { get; } = Dreamword;
}

/// <summary>Client-mode (C95) data block received.</summary>
public sealed class ClientModeEvent(string Data) : MudEvent
{
    public string Data { get; } = Data;
}
