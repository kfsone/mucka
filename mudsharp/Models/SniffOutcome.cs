namespace MudSharp.Models;

/// <summary>
/// Result of a "sniff" probe — a <c>value &lt;name&gt;</c> command prepended to a routine FES
/// probe to work out whether a player who has dropped off the Online (FEW) list is really gone.
/// The three outcomes map to the three ways the server can answer <c>value</c>:
/// <list type="bullet">
///   <item><see cref="Present"/> — "The value of {name} … is {n} points." The player is online
///     and visible to us.</item>
///   <item><see cref="Offline"/> — "I don't know the word "{name}"." The name has left the
///     vocabulary; the player has logged out.</item>
///   <item><see cref="Invisible"/> — no reply at all. A gap in the game's behaviour: the player
///     is online but invisible and we cannot see through it.</item>
/// </list>
/// </summary>
public enum SniffOutcome
{
    /// <summary>Online and visible ("The value of … points.").</summary>
    Present,
    /// <summary>Logged out ("I don't know the word …").</summary>
    Offline,
    /// <summary>Online but invisible — the probe drew no reply.</summary>
    Invisible,
}
