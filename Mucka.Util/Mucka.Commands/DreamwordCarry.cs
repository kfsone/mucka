namespace Mucka.Commands;

/// <summary>
/// The dreamword, held across connections for as long as it can still be the world's.
///
/// <para>A dreamword belongs to one world between two resets. Nothing in the protocol repeats it at
/// login, so a relog - another persona, or the same one back after a drop - would otherwise lose a
/// word that is still live. It goes when the player connects to a different server or when the reset
/// it belonged to has come: a reset seen live clears it (<see cref="Clear"/>), and one that happened
/// while the player was away is caught by the reset projection last seen before leaving
/// (<see cref="NoteResetDue"/>).</para>
///
/// <para>A word held with no projection at all is kept. Speaking a dead word costs one command and
/// clears it (<c>MudSession</c> watches for the player saying it); losing a live one costs the
/// stamina refresh.</para>
///
/// <para>Locked because a connection's Feed thread writes it and the next connection reads it from
/// whichever thread calls <c>ConnectAsync</c>.</para>
/// </summary>
public sealed class DreamwordCarry
{
    private readonly object _lock = new();
    private string? _host;
    private string? _word;
    private DateTime? _resetDueUtc;

    /// <summary>A connection to <paramref name="host"/> is starting. Returns the word to restore, or
    /// null. A different server drops everything held.</summary>
    public string? Connect(string host, DateTime nowUtc)
    {
        lock (_lock)
        {
            if (!string.Equals(_host, host, StringComparison.OrdinalIgnoreCase))
            {
                _host = host;
                _word = null;
                _resetDueUtc = null;
                return null;
            }
            if (IsPastResetLocked(nowUtc))
            {
                _word = null;
                _resetDueUtc = null;
            }
            return _word;
        }
    }

    /// <summary>The dreamword changed on the current connection; null when it was cleared.</summary>
    public void Note(string? word)
    {
        lock (_lock)
            _word = word;
    }

    /// <summary>The reset projection. A null (no projection, or one dropped on leaving the game) never
    /// replaces a known time: the last one seen is what a later connection has to judge by.</summary>
    public void NoteResetDue(DateTime? dueUtc)
    {
        if (dueUtc is not DateTime due)
            return;
        lock (_lock)
            _resetDueUtc = due;
    }

    /// <summary>Whether the reset last projected has come.</summary>
    public bool IsPastReset(DateTime nowUtc)
    {
        lock (_lock)
            return IsPastResetLocked(nowUtc);
    }

    /// <summary>A reset landed: the word and the projection both belonged to the world that ended.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _word = null;
            _resetDueUtc = null;
        }
    }

    private bool IsPastResetLocked(DateTime nowUtc) => _resetDueUtc is DateTime due && nowUtc >= due;
}
