namespace Mucka.Combat;

/// <summary>
/// Which NPCs the player is in an OPEN fight with right now, by instance name.
///
/// <para>Feeds the side panel's "Here" list: it is what turns a creature's swords icon red, and it
/// is also the second, independent answer to "is this name alive at all" - anything being swung at is
/// a creature whether or not the room ever described it (see
/// <see cref="MudSharp.Models.RoomCreatures"/> for the first answer and what it cannot cover).</para>
///
/// <para>The property below is load-bearing and not obvious, and cannot be checked at all from
/// inside a MAUI view model. This type deliberately references nothing but
/// <see cref="FightSnapshot"/>, so Mucka.Util.Tests can exercise it.</para>
///
/// <para><b>The property that matters: an unchanged refresh must report false.</b>
/// <see cref="Update"/> runs on every combat event, every FES heartbeat and every 1 Hz tick, and the
/// caller re-walks the whole Here list whenever it returns true. Returning true on a refresh that
/// says the same thing as the last one would put that walk - and the bindable-property notifications
/// it can raise - on the typing path several times a second (Invariant #1).</para>
///
/// <para>Not thread-safe, and does not need to be: its single owner touches it only from the UI
/// thread.</para>
/// </summary>
public sealed class EngagedNpcSet
{
    // Two sets, swapped rather than copied: Update rebuilds into the spare and keeps it if it
    // differs, so a steady state costs one clear, N adds and a SetEquals with no allocation at all
    // after the first couple of fights.
    private HashSet<string> _current = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _scratch = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a fight against this instance is open. Ordinal-ignore-case, since FEI and the
    /// combat tracker both print the game's own spelling but nothing guarantees the case matches. The
    /// set holds each open fight under BOTH the name the tracker saw and that name's bare instance id -
    /// see <see cref="Update"/> for why the two are not always the same string.</summary>
    public bool Contains(string? name)
        => !string.IsNullOrEmpty(name) && _current.Contains(name);

    /// <summary>How many lookup keys are held, which is NOT the number of open fights - a creature
    /// whose combat name carries a descriptor contributes two (see <see cref="Update"/>). Only ever
    /// used to assert the set has emptied; nothing on screen reports it.</summary>
    public int Count => _current.Count;

    /// <summary>
    /// Rebuilds from one snapshot's unresolved fights. Returns true only when the membership actually
    /// changed.
    ///
    /// <para><b>Built as a whole set and compared, rather than added-to and count-checked.</b> The
    /// cheaper trick - add every open name, then rebuild only if the resulting count disagrees with
    /// the number of open fights - misses the case where one fight resolves and another opens in the
    /// SAME refresh. rat17 dies as rat19 joins: the count is one before and one after, the
    /// membership is completely different, and the count check reports no change. The Here list then
    /// keeps a grey swords icon on rat19 while rat19 is hitting the player, and keeps a red one on
    /// the dead rat17. A set comparison cannot be wrong about that, and costs one pass either way.</para>
    ///
    /// <para><b>Each fight is indexed under its bare instance id as well as its full name, because the
    /// two sides of this comparison do not always spell the creature the same way.</b> Fighting the
    /// large rat0 in a room of four rats, the terminal and the rail both said "large rat0" while the
    /// Here list said "rat0", so the exact-name lookup missed and rat0 alone kept a grey swords icon
    /// while it was hitting him. The other three, whose names carry no descriptor, matched and went
    /// red. Combat lines carry a creature's descriptor and FEI prints the bare id - and the descriptor
    /// is not even reliably present on the combat side, since "stocky dwarf" appears bare 653 times in
    /// the corpus.</para>
    ///
    /// <para><b>Only a token ENDING IN DIGITS is treated as an id.</b> That is what makes the extra
    /// entry safe: an instance number is unique within a room, so "rat0" can only mean the one rat0,
    /// whatever adjectives the game put in front of it. The unnumbered mobs - coot, fox, banshee,
    /// thief, "giant cave bat" - have no such token, and matching their last word would let a Here row
    /// reading "bat" light up because a "giant cave bat" was engaged. Those keep exact-name matching
    /// only.</para>
    ///
    /// <para><b>This is not <see cref="MudSharp.Combat.NpcPoolKey"/> and must not be confused with
    /// it.</b> That key deliberately keeps every adjective, because "large rat0" and "rat0" are
    /// measured to have very different stamina pools and pooling them produces a band that is wrong at
    /// both ends. The question here is a different one - "is the row in front of me the thing I am
    /// swinging at" - and it is answered within a single room, where the instance number settles
    /// identity on its own. Nothing here changes any pool bucketing.</para>
    /// </summary>
    public bool Update(IReadOnlyList<FightSnapshot> fights)
    {
        _scratch.Clear();
        foreach (var fight in fights)
        {
            if (fight.IsResolved || string.IsNullOrWhiteSpace(fight.NpcName))
                continue;

            _scratch.Add(fight.NpcName);

            var id = InstanceId(fight.NpcName);
            if (id is not null)
                _scratch.Add(id);
        }

        if (_scratch.Count == _current.Count && _current.SetEquals(_scratch))
            return false;

        (_current, _scratch) = (_scratch, _current);
        return true;
    }

    /// <summary>
    /// The bare instance id inside a combat name - the last whitespace-delimited token, but only when
    /// it ends in a digit and the name has something in front of it. Null when there is no separate id
    /// to add: an already-bare name ("rat0"), or an unnumbered mob whose last word is an ordinary word
    /// ("giant cave bat"). See <see cref="Update"/> for why the digit test is the safety rule and not
    /// a detail.
    /// </summary>
    private static string? InstanceId(string npcName)
    {
        var name = npcName.AsSpan().Trim();
        var lastSpace = name.LastIndexOfAny(' ', '\t');
        if (lastSpace < 0)
            // Already a single token - it is its own id, and Update has added it.
            return null;

        var id = name[(lastSpace + 1)..];
        return id.Length > 0 && char.IsAsciiDigit(id[^1]) ? id.ToString() : null;
    }
}
