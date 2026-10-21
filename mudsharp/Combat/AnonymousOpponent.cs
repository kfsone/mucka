namespace MudSharp.Combat;

/// <summary>
/// The two words MUD2 puts in place of a Creature's name when the player cannot see it, and the
/// one place they are spelled. Every anonymous line in the corpus uses exactly one of them; the
/// choice is the CREATURE's class - a person-shaped one (a player, the man, the thief, a zombie, a
/// dwarf) is <see cref="Person"/>, anything else (a rat, a snake, the fox, an eagle) is
/// <see cref="Thing"/> - and not the cause, which may be the player blind, the room dark, or the
/// Creature invisible. "somebody" occurs nowhere.
///
/// <para>Neither word is a participant name. Every anonymous line goes to the encounter's one unseen
/// group (docs/unseen-presentation.md), which the event stream, the fight records and the rail name
/// <see cref="Group"/>. Fight records written before the group existed carry the two words as
/// names, so <see cref="IsAnonymous"/> answers for them too.</para>
/// </summary>
public static class AnonymousOpponent
{
    /// <summary>The participant name of the encounter's unseen group: every anonymous blow, and every
    /// ending that concluded an unseen participant. Angle brackets, which a persona
    /// (<see cref="PlayerOpponent.Persona"/>, letters only) cannot contain, so a player called
    /// "Unseen" is never the group.</summary>
    public const string Group = "<unseen>";

    /// <summary>The group's name as a title.</summary>
    public const string GroupAsSubject = "Unseen";

    /// <summary>The group's name as the history draws it.</summary>
    public const string GroupLabel = "unseen";

    /// <summary>Exactly <see cref="Group"/>, ordinal: a persona called "Unseen" is a player.</summary>
    public static bool IsGroup(string? name) => string.Equals(name, Group, StringComparison.Ordinal);

    /// <summary>A person-shaped Creature the player cannot see.</summary>
    public const string Person = "someone";

    /// <summary>A non-person Creature the player cannot see.</summary>
    public const string Thing = "something";

    /// <summary>The same two words as a sentence SUBJECT ("Someone hits you."). Spelled out as
    /// constants rather than derived, so the tracker's patterns - which are compile-time constants
    /// composed from these - stay constants. Four spellings, still one place.</summary>
    public const string PersonAsSubject = "Someone";
    public const string ThingAsSubject = "Something";

    /// <summary>Whether <paramref name="name"/> is the unseen group or one of the two anonymous words
    /// rather than a Creature's name. Case-insensitive, because the words appear capitalised as a
    /// sentence subject and lower-cased as its object.</summary>
    public static bool IsAnonymous(string? name)
        => IsGroup(name)
        || string.Equals(name, Person, StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, Thing, StringComparison.OrdinalIgnoreCase);

    /// <summary>The title spelling of the group or an anonymous word however it was capitalised - what
    /// the unseen badge is titled with. Null for anything that is neither.</summary>
    public static string? Subject(string word)
        => IsGroup(word) ? GroupAsSubject
         : string.Equals(word, Person, StringComparison.OrdinalIgnoreCase) ? PersonAsSubject
         : string.Equals(word, Thing,  StringComparison.OrdinalIgnoreCase) ? ThingAsSubject
         : null;

    /// <summary>The canonical (lower-case) constant for an anonymous word however it was
    /// capitalised on the line, so a consumer can compare by reference to <see cref="Person"/> or
    /// <see cref="Thing"/>. Null for anything that is not an anonymous word.</summary>
    public static string? Canonical(string word)
        => string.Equals(word, Person, StringComparison.OrdinalIgnoreCase) ? Person
         : string.Equals(word, Thing,  StringComparison.OrdinalIgnoreCase) ? Thing
         : null;
}
