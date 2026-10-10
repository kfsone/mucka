namespace MudSharp.Combat;

/// <summary>
/// A player as an opponent. MUD2 names a creature "the zombie4" / "The zombie4" and a player by
/// persona, bare and titled by level: "Kickflip the warrior misses you." / "You hit Kickflip the
/// warrior (1-4)." - verbatim, a player's attack on the operator. Every creature name on the wire
/// is lower-case ("zombie4", "rat8"), so a name whose first letter is upper-case is a persona; the
/// tracker reports the persona alone, never the title, which changes with level.
///
/// <para>A player is person-class: the word MUD2 puts in place of the name is "someone".</para>
/// </summary>
public static class PlayerOpponent
{
    /// <summary>
    /// The pattern fragment for a persona as a sentence subject or object, capturing the persona
    /// alone in the group <c>npc</c> - the same group the creature alternatives fill, so a consumer
    /// reads one group whichever matched. The words "The", "You", "Someone" and "Something" are
    /// excluded because the patterns that carry this fragment also carry sentences that start with
    /// them. An optional "Sir " / "Lady " prefix is accepted, as <c>PlayerNameParts</c> does.
    /// </summary>
    public const string Persona =
        @"(?!(?:The|You|Your|Someone|Something)\b)(?:(?:Sir|Lady) )?(?<npc>[A-Z][A-Za-z]+)(?: the [a-z]+(?: [a-z]+)*?)?";

    /// <summary>Whether <paramref name="name"/> is a player's persona rather than a creature.</summary>
    public static bool IsPersona(string? name)
        => !string.IsNullOrEmpty(name) && char.IsAsciiLetterUpper(name[0]) && !AnonymousOpponent.IsAnonymous(name);
}
