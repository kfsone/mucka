namespace Mucka.Combat;

/// <summary>
/// Which reset cycle the dead strip is in, as the ordinal <see cref="CombatEnding.ResetOrdinal"/>
/// carries.
///
/// <para>Two signals, both the server's. The corroborated landing (<c>MudSession.WorldResetLanded</c>,
/// C06 C06) advances it at the moment the world turns over. The login banner's reset number ("This
/// reset is number 127231.") advances it when it names a number other than the last one seen - the
/// reset the landing was not corroborated for, or one that came while the player sat at the menu.
/// A landing already counted is not counted again when the next banner confirms it, and one the
/// next banner contradicts - the same number as before it - is withdrawn: the banner is the server's
/// own count, and the landing's corroboration is an inference. Nothing is recorded between the two,
/// since a landing returns the player to the menu and the banner is the next login.</para>
///
/// <para>Leaving the game is not a signal. A quit, a death and a relog all return to the menu inside
/// one reset, and the banner that follows names the same number.</para>
/// </summary>
public sealed class ResetCycle
{
    private long? _lastNumber;
    private bool _landingCounted;

    /// <summary>Starts at 0 and only goes up; two endings share it iff they were recorded in the same
    /// cycle.</summary>
    public int Ordinal { get; private set; }

    /// <summary>The corroborated reset landing.</summary>
    public void NoteLanding()
    {
        Ordinal++;
        _landingCounted = true;
    }

    /// <summary>A login banner named <paramref name="number"/>.</summary>
    public void NoteResetNumber(long number)
    {
        if (_lastNumber is long last)
        {
            if (last != number && !_landingCounted)
                Ordinal++;
            else if (last == number && _landingCounted)
                Ordinal--;
        }
        _landingCounted = false;
        _lastNumber = number;
    }
}
