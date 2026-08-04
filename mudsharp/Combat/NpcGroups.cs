namespace MudSharp.Combat;

/// <summary>
/// Normalizes a MUD2 NPC instance name to its group name: "rat0" -> "rats", "dwarf1" -> "dwarves".
/// MUD2 spawns numbered instances of the same creature, so the instance name answers "is THIS
/// one tough" while the group name is what gives usable sample sizes for cross-fight comparison.
///
/// <para>IMPORTANT: this is a deliberate port of <c>normalize_npc_group</c> in
/// tools/combat/reduce_combat.py, and the two must stay in agreement — the offline sqlite
/// pipeline and the live client would otherwise bucket the same fight under different groups and
/// silently disagree about history. NpcGroupsTests pins the behaviour against a fixture of
/// name/group pairs; if the Python side gains an irregular, add it here and to that fixture.</para>
/// </summary>
public static class NpcGroups
{
    /// <summary>Creatures whose plural the suffix rules below would get wrong. Mirrors
    /// reduce_combat.py's IRREGULAR_GROUPS exactly.</summary>
    private static readonly Dictionary<string, string> Irregular = new(StringComparer.Ordinal)
    {
        ["dwarf"] = "dwarves",
        ["mouse"] = "mice",
        ["thief"] = "thieves",
        ["wolf"] = "wolves",
    };

    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        // Strip the instance number ("rat0" -> "rat"), then take the last whitespace/hyphen
        // separated token, so "giant cave bat" groups as "bats" rather than "giant cave bats".
        var trimmed = name.Trim().ToLowerInvariant();
        var baseName = TrimTrailingDigits(trimmed);
        var leaf = LastToken(baseName);
        // Deliberate divergence from the Python, which pluralizes whatever survives stripping and
        // so answers "s" for an all-digits or blank name. An empty group is rejected downstream by
        // FightHistory's guard, whereas a literal "s" group would quietly accumulate a junk bucket.
        // Unreachable for real names — callers reject blank NPC names first. See NpcGroupsTests.
        if (leaf.Length == 0)
            return string.Empty;

        if (Irregular.TryGetValue(leaf, out var irregular))
            return irregular;

        if (leaf.EndsWith("s", StringComparison.Ordinal)
            || leaf.EndsWith("x", StringComparison.Ordinal)
            || leaf.EndsWith("ch", StringComparison.Ordinal)
            || leaf.EndsWith("sh", StringComparison.Ordinal))
            return leaf + "es";

        // Consonant + y pluralizes as -ies ("harpy" -> "harpies"), vowel + y does not
        // ("donkey" -> "donkeys").
        if (leaf.Length > 1 && leaf.EndsWith("y", StringComparison.Ordinal) && !IsVowel(leaf[^2]))
            return leaf[..^1] + "ies";

        return leaf + "s";
    }

    private static string TrimTrailingDigits(string value)
    {
        var end = value.Length;
        while (end > 0 && char.IsAsciiDigit(value[end - 1]))
            end--;
        return value[..end];
    }

    private static readonly char[] TokenSeparators = [' ', '\t', '\n', '\r', '\f', '\v', '-'];

    private static string LastToken(string value)
    {
        // Direct translation of the Python's re.split(r"[\s-]+", base) with empties discarded,
        // then tokens[-1]; falls back to the base itself when there are no tokens at all.
        var tokens = value.Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length > 0 ? tokens[^1] : value;
    }

    private static bool IsVowel(char c) => c is 'a' or 'e' or 'i' or 'o' or 'u';
}
