namespace Mucka.Commands;

/// <summary>
/// Watches one login and decides how it ended. The single answer to that question: the guided-login
/// overlay, the swing ledger's session row and the wire-log backfill all read it, so they cannot
/// disagree about an event there is only one of.
///
/// <para>They did disagree. The overlay classified a drop while the store wrote its own note from a
/// separate copy of the same rules, and a third copy lived in a test - so a death showed as
/// "Unknown" on screen and "died" in the database, and the copies were free to drift. CLAUDE.md
/// names this module as the home for session-drop classification; this is that.</para>
///
/// <para><b>Code beats prose where a code exists.</b> A world reset and a permadeath are announced
/// by the server (C06 C06 and C08+C13 respectively) and are taken from those signals. A quit and an
/// ordinary death have no code of their own - the one code near a death narrative also precedes
/// unrelated scripted events - so they are read from the shell's own landmark lines, matched
/// whole-line because a player can say anything.</para>
///
/// <para><b>First writer wins.</b> A quit prints its farewell before the summary that a death would
/// also print, so the farewell settles it. A code-sourced answer arrives before either and is not
/// overwritten by prose.</para>
///
/// <para>Not thread-safe by itself; callers drive it from one thread (the Feed thread live, the
/// replay loop in the backfill).</para>
/// </summary>
public sealed class SessionEndWatcher
{
    /// <summary>How this login ended so far. <see cref="SessionDropReason.Unknown"/> until something
    /// says otherwise, which is the honest answer for a login still running, and the permanent one
    /// for a client that was killed.</summary>
    public SessionDropReason Reason { get; private set; } = SessionDropReason.Unknown;

    /// <summary>Whether anything has classified this login yet.</summary>
    public bool Decided => Reason != SessionDropReason.Unknown;

    /// <summary>Starts watching a fresh login. Call on game-mode entry.</summary>
    public void Begin() => Reason = SessionDropReason.Unknown;

    /// <summary>The world reset landed - the server's own C06 C06. It takes the server down and logs
    /// everyone out, so the game-mode exit that follows belongs to it.</summary>
    public void NoteWorldResetLanded() => Set(SessionDropReason.Reset);

    /// <summary>The persona was wiped - the decoder's C08+C13.</summary>
    public void NotePersonaWiped() => Set(SessionDropReason.Permadeath);

    /// <summary>
    /// One line of server output, as plain text. Normalised here so callers can hand over whatever
    /// they have - the live path has a styled line's PlainText, the backfill has a replayed one.
    /// </summary>
    public void NoteLine(string text)
    {
        if (Decided || string.IsNullOrEmpty(text))
            return;

        var normalized = ShellText.NormalizeWhitespace(text);
        if (ShellText.IsQuitFarewellLine(normalized))
            Set(SessionDropReason.Quit);
        else if (ShellText.IsGameSummaryLine(normalized))
            Set(SessionDropReason.Died);
    }

    private void Set(SessionDropReason reason)
    {
        if (!Decided)
            Reason = reason;
    }
}
