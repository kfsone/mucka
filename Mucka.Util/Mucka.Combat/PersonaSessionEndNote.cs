using Mucka.Commands;
using Mucka.Store;

namespace Mucka.Combat;

/// <summary>
/// Turns the one classification (<see cref="SessionDropReason"/>) into the one stored word
/// (<see cref="PersonaSessionEnd"/>).
///
/// <para>It lives here rather than on either side because neither should reach the other:
/// <c>Mucka.Store</c> is deliberately standalone so a command-line tool can open the database with
/// it alone, and <c>Mucka.Commands</c> has no business knowing a column's vocabulary. This assembly
/// already depends on both.</para>
/// </summary>
public static class PersonaSessionEndNote
{
    /// <summary>The note to store, or null when nothing classified the login - a client killed
    /// mid-game, or one still running. Null is the honest value and the column allows it.</summary>
    public static string? For(SessionDropReason reason) => reason switch
    {
        SessionDropReason.Reset => PersonaSessionEnd.Reset,
        SessionDropReason.Quit => PersonaSessionEnd.Quit,
        SessionDropReason.Died => PersonaSessionEnd.Died,
        SessionDropReason.Permadeath => PersonaSessionEnd.Permadeath,
        _ => null,
    };
}
