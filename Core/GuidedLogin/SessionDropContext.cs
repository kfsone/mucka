using MudSharp.Models;

namespace Mucka.Core.GuidedLogin;

/// <summary>Why the shell dropped us out of game mode and back to the Option menu.</summary>
public enum SessionDropReason
{
    /// <summary>No classifying signal — a deliberate QUIT, an idle boot, a server-side kick, or
    /// anything else we cannot name. The player gets the last few lines and decides for themselves.</summary>
    Unknown,
    /// <summary>A game reset: the server announced C06 C04 ("auto reset initiated") and dropped us
    /// on the projected reset instant.</summary>
    Reset,
    /// <summary>Permadeath: the decoder saw C08+C13 ("Not updating persona.") — the persona we were
    /// playing is gone.</summary>
    Permadeath,
}

/// <summary>
/// What the guided-login overlay tells the player about the drop that put them there. Captured at
/// game-mode exit — the moment the terminal goes behind the overlay — and displayed unchanged for
/// the whole life of the overlay, however it ends.
///
/// <para><see cref="TailLines"/> is the last handful of server output before the drop, kept as
/// styled lines so the overlay can render it through the same <c>TerminalView</c> the game screen
/// uses (real ANSI colours) rather than flattening it to text.</para>
/// </summary>
public sealed record SessionDropContext(
    SessionDropReason Reason,
    string? PersonaName,
    IReadOnlyList<StyledLine> TailLines)
{
    /// <summary>Headline shown above the tail lines. The persona name is only interesting when we
    /// are announcing its death.</summary>
    public string Headline => Reason switch
    {
        SessionDropReason.Reset => "Reset In Progress",
        SessionDropReason.Permadeath => string.IsNullOrWhiteSpace(PersonaName)
            ? "Rest In Peace"
            : $"Rest In Peace {PersonaName}",
        _ => "Oops!",
    };

    /// <summary>A reset says all it needs to in the headline; the other two are the player asking
    /// "what just happened to me?", so they get the server's own last words.</summary>
    public bool ShowsTailLines => Reason != SessionDropReason.Reset && TailLines.Count > 0;
}
