namespace MudSharp.Combat;

/// <summary>
/// The two words MUD2 puts in place of a Creature's name when the player cannot see it, and the
/// one place they are spelled. Every anonymous line in the corpus uses exactly one of them; the
/// choice is the CREATURE's class - a person-shaped one (a player, the man, the thief, a zombie, a
/// dwarf) is <see cref="Person"/>, anything else (a rat, a snake, the fox, an eagle) is
/// <see cref="Thing"/> - and not the cause, which may be the player blind, the room dark, or the
/// Creature invisible. "somebody" occurs nowhere.
///
/// <para>These are also the participant names the tracker reports when an anonymous line cannot be
/// attributed to a known Creature, so the same constant is what a roster row, a fight record or a
/// species index compares against to know it is looking at an unseen opponent rather than a
/// Creature called "someone".</para>
/// </summary>
public static class AnonymousOpponent
{
    /// <summary>A person-shaped Creature the player cannot see.</summary>
    public const string Person = "someone";

    /// <summary>A non-person Creature the player cannot see.</summary>
    public const string Thing = "something";

    /// <summary>The same two words as a sentence SUBJECT ("Someone hits you."). Spelled out as
    /// constants rather than derived, so the tracker's patterns - which are compile-time constants
    /// composed from these - stay constants. Four spellings, still one place.</summary>
    public const string PersonAsSubject = "Someone";
    public const string ThingAsSubject = "Something";

    /// <summary>Whether <paramref name="name"/> is one of the two anonymous words rather than a
    /// Creature's name. Case-insensitive, because the words appear capitalised as a sentence
    /// subject and lower-cased as its object.</summary>
    public static bool IsAnonymous(string? name)
        => string.Equals(name, Person, StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, Thing, StringComparison.OrdinalIgnoreCase);

    /// <summary>The subject spelling of an anonymous word however it was capitalised - what the
    /// unknown badge is titled with. Null for anything that is not an anonymous word.</summary>
    public static string? Subject(string word)
        => string.Equals(word, Person, StringComparison.OrdinalIgnoreCase) ? PersonAsSubject
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
