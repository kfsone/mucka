namespace MudSharp.Combat;

/// <summary>
/// The two words MUD2 puts in place of a Creature's name when the player cannot see it, as two
/// enumerations of one concept. Which word a Creature gets is a property of the Creature (its
/// class - person-shaped or not), not of the cause. See <see cref="AnonymousOpponent"/> for the
/// words themselves.
/// </summary>
public enum SomeKind
{
    /// <summary>"someone" - a person-shaped Creature: a player, the man, the thief, a zombie.</summary>
    Someone,
    /// <summary>"something" - any other Creature: a rat, a snake, the fox, an eagle.</summary>
    Something,
}

public static class SomeKinds
{
    /// <summary>What prose calls a Creature whose kind is not yet known. The operator's choice: it
    /// reads right ("something hit you") where the alternative asserts a person.</summary>
    public const SomeKind Default = SomeKind.Something;

    public static SomeKind FromWord(string word)
        => AnonymousOpponent.Canonical(word) == AnonymousOpponent.Person ? SomeKind.Someone : SomeKind.Something;
}

/// <summary>
/// The species whose anonymous word is known before any fight, read-only. <see cref="UnseenBook"/>
/// asks it whether a named Creature could have produced an anonymous line, so that a Creature of
/// known class is not parked by a word it cannot produce. Nothing is learned or persisted.
/// </summary>
public static class SomeKindKnowledge
{
    /// <summary>
    /// Species known to be "someone". Goblin, dwarf and giant are the operator's rule; the man ("The
    /// man fades from view." then "Someone hits you") and the thief (blind: "Someone hits you
    /// (115/120).") are observed in the operator's wire corpus. Every player is "someone" too, by
    /// <see cref="PlayerOpponent"/>. Ghoul, zombie and skeleton are not listed: untested.
    /// </summary>
    private static readonly HashSet<string> SeededSomeone =
        new(new[] { "man", "thief", "goblin", "dwarf", "giant" }.Select(NpcGroups.Normalize), StringComparer.Ordinal);

    /// <summary>
    /// Species known to be "something": rat, water-snake, fox and eagle, each observed in the
    /// operator's wire corpus as "Something hits you" while the player was blind or in the dark; and
    /// the basilisk, observed in the operator's wire corpus as "Something is about to attack you."
    /// opening the one unseen slot that "You have killed the basilisk." concluded. Grouped by
    /// <see cref="NpcGroups.Normalize"/>, so the water-snake seed covers every "snake".
    /// </summary>
    private static readonly HashSet<string> SeededSomething =
        new(new[] { "rat", "water-snake", "fox", "eagle", "basilisk" }.Select(NpcGroups.Normalize), StringComparer.Ordinal);

    /// <summary>The word this Creature gets when unseen, or null when it is not known.</summary>
    public static SomeKind? Known(string npcName)
    {
        if (PlayerOpponent.IsPersona(npcName))
            return SomeKind.Someone;
        var species = NpcGroups.Normalize(npcName);
        return SeededSomeone.Contains(species) ? SomeKind.Someone
             : SeededSomething.Contains(species) ? SomeKind.Something
             : null;
    }
}
