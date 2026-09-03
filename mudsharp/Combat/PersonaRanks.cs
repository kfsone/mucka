namespace MudSharp.Combat;

/// <summary>
/// One row of MUD2's experience-level table: the score a persona needs to reach the level, what
/// killing one at that level pays, and the four naming schemes the game uses for it.
/// </summary>
/// <param name="Level">MUD2's own LEV column, 0 (novice) to 11 (wizard/witch).</param>
/// <param name="Points">Score required to reach this level. A clean doubling from 200.</param>
/// <param name="Mpksl">Minimum Points for Killing Someone of this Level. Null for level 11, where
/// the game's table prints none.</param>
/// <param name="MaleNormal">Male, unprotected, non-magical - "novice", "guardian".</param>
/// <param name="MaleMagical">Male, unprotected, magical - "necromancer". Null at level 0, which the
/// table prints as "-" because a novice has no magical style yet.</param>
/// <param name="FemaleNormal">Female, unprotected, non-magical - "guardienne".</param>
/// <param name="FemaleMagical">Female, unprotected, magical - "necromancess".</param>
/// <param name="MaleProtected">Male, protected, non-magical - "ranger".</param>
/// <param name="MaleProtectedMagical">Male, protected, magical - "prelate".</param>
/// <param name="FemaleProtected">Female, protected, non-magical.</param>
/// <param name="FemaleProtectedMagical">Female, protected, magical. Differs from the male column
/// only where the game gives a gendered word ("Brother"/"Sister", "priest"/"priestess").</param>
public readonly record struct PersonaRank(
    int Level,
    int Points,
    int? Mpksl,
    string? MaleNormal,
    string? MaleMagical,
    string? FemaleNormal,
    string? FemaleMagical,
    string? MaleProtected,
    string? MaleProtectedMagical,
    string? FemaleProtected,
    string? FemaleProtectedMagical);

/// <summary>
/// MUD2's experience-level table, exactly as the game prints it.
///
/// <para><b>Hard-coded on purpose, and the owner's reasoning is the whole justification</b>
/// (2026-09-02): "The levels table hasn't changed in nearly 50 years. Do you know something I don't
/// that suggests we shouldn't hard code it?" No. MUD2 is a frozen artifact - see CLAUDE.md's museum
/// section - so a constant of it is a constant, not a value that might drift. Asking the game for
/// this at login would cost four server ticks (`levels` then `nx` three times), a parser, and terminal
/// output the player did not ask for, all to re-derive something that cannot change. Do not "improve"
/// this into a runtime fetch.</para>
///
/// <para><b>Source.</b> Captured verbatim in
/// <c>session-rec.mud2.co.uk.20260902-160323.jsonl</c> (all four persona tables, dumped by `levels`
/// followed by `nx` three times); the male unprotected table appears again in
/// <c>session-rec.mud2.co.uk.20260827-170629</c>, <c>www.mud2.com.20260822-151859</c> and
/// <c>www.mud2.com.20260824-231526</c>. Independently corroborated by 22 level-crossing events across
/// the corpus - every one of the eight empirical brackets those produce contains the printed
/// figure, and none contradicts it.</para>
///
/// <para><b>The real risk here is transcription, not drift</b>, which is why
/// <see cref="MpkslFor"/>'s relationship is asserted in the tests rather than trusted: MPKSL is
/// exactly <c>Points / 5 + 75</c> for every row that has one, and <see cref="PersonaRank.Points"/>
/// doubles from 200. A mistyped digit breaks one of those and is caught.</para>
///
/// <para><b>This is the PLAYER table.</b> The owner reports that NPCs level "on a similar levelling
/// schema" with no upper cap on sta, str or dex where a player has one - similar, explicitly not
/// stated as identical. So using these thresholds to convert a creature's value into a level is an
/// ESTIMATE resting on an unverified premise, and any readout built on it must say so. See
/// <see cref="NpcPoolKey"/> for the separate matter of what a creature's value is.</para>
/// </summary>
public static class PersonaRanks
{
    /// <summary>The twelve rows, in level order. Index equals <see cref="PersonaRank.Level"/>.</summary>
    public static readonly IReadOnlyList<PersonaRank> Table =
    [
        new(0, 0, 75,
            "novice", null, "novice", null, "discoverer", null, "discoverer", null),
        new(1, 200, 115,
            "protector", "seer", "protector", "seeress", "pathfinder", "neophyte", "pathfinder", "neophyte"),
        new(2, 400, 155,
            "yeoman", "soothsayer", "yeowoman", "soothsayer", "voyager", "pilgrim", "voyager", "pilgrim"),
        new(3, 800, 235,
            "warrior", "cabalist", "warrior", "cabalist", "wayfarer", "acolyte", "wayfarer", "acolyte"),
        new(4, 1600, 395,
            "swordsman", "magician", "swordswoman", "magicienne", "scout", "friar", "scout", "friar"),
        new(5, 3200, 715,
            "hero", "enchanter", "heroine", "enchantress", "rover", "cleric", "rover", "cleric"),
        new(6, 6400, 1355,
            "superhero", "spellbinder", "superheroine", "spellbindress", "pioneer", "Brother", "pioneer", "Sister"),
        new(7, 12800, 2635,
            "champion", "sorcerer", "championne", "sorceress", "explorer", "priest", "explorer", "priestess"),
        new(8, 25600, 5195,
            "guardian", "necromancer", "guardienne", "necromancess", "ranger", "prelate", "ranger", "prelate"),
        new(9, 51200, 10315,
            "legend", "warlock", "legend", "warlock", "minstrel", "patriarch", "minstrel", "matriarch"),
        // Levels 10 and 11 print no protected columns at all - the table simply ends there, which is
        // recorded as null rather than filled in with a guess.
        new(10, 102400, 20555,
            "Sir", "mage", "Lady", "mage", null, null, null, null),
        new(11, 204800, null,
            null, "wizard", null, "witch", null, null, null, null),
    ];

    /// <summary>The highest level a score has reached. Never negative: a score below 200 is level 0,
    /// and a score above the top row stays at 11 rather than extrapolating a row the game does not
    /// print.</summary>
    public static int LevelForScore(long points)
    {
        var level = 0;
        for (var i = 1; i < Table.Count; i++)
        {
            if (points < Table[i].Points)
                break;
            level = i;
        }

        return level;
    }

    /// <summary>What killing a persona at this level pays, or null where the game's table prints
    /// none. Null for an out-of-range level too - callers must not read that as free.</summary>
    public static int? MpkslFor(int level)
        => level >= 0 && level < Table.Count ? Table[level].Mpksl : null;
}
