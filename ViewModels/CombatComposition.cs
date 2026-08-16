using MudSharp.Combat;

namespace Mucka.ViewModels;

/// <summary>
/// Pure, MAUI-independent helpers shared by the combat composition path: which fight the panel is
/// currently about, the survivability projection behind it, the live damage-per-hit figure, and
/// name shortening for a fixed-width surface.
///
/// <para>Extracted from the deleted text formatter, which mixed these value computations in with
/// line composition. Everything here computes a number or a name; nothing here decides layout,
/// colour, or wording.</para>
/// </summary>
internal static class CombatComposition
{
    /// <summary>Name length past which a trailing instance number is dropped for display. MUD2 names
    /// creatures and items with a numeric suffix ("zombie9", "dagger0"); at panel width the suffix
    /// costs more room than it earns once the base name is already long.</summary>
    private const int DisplayNameThreshold = 10;

    /// <summary>
    /// "Am I going to die before it does" - <see cref="CombatOutlook.Project"/> against the
    /// encounter's primary fight. Extracted so the Combat Rail's tier resolver (DESIGN_FINAL.md 4.3)
    /// and flee-cost risk pairing (5.4) can reuse the EXACT SAME projection
    /// <see cref="AppendSurvivability"/> renders, rather than each computing their own and risking
    /// the outlook line and the tier/ladder disagreeing about "how close is this fight".
    /// </summary>
    internal static CombatOutlook ComputeOutlook(
        CombatEncounterSnapshot snapshot, CombatStatDeficits deficits, CombatHistoryContext history,
        FightSnapshot? primary = null)
    {
        if (!snapshot.InCombat)
            return CombatOutlook.Unknown;

        primary ??= PrimaryFight(snapshot);
        return primary is null
            ? CombatOutlook.Unknown
            : CombatOutlook.Project(
                primary.Duration.TotalSeconds,
                primary.ApproxDamageDone,
                primary.ApproxDamageTaken,
                primary.YouHits,
                primary.TheyHits,
                deficits.StaminaCurrent,
                history.Primary.EstimatedStaminaPool);
    }


    /// <summary>Damage per landed blow so far in the current fight, which is what the historical
    /// per-hit figures are comparable against. Taken from the primary fight rather than the whole
    /// encounter so a second target of a different species cannot pollute it.</summary>
    internal static double? LivePerHit(CombatEncounterSnapshot snapshot, CombatHistoryContext history)
    {
        var primary = PrimaryFight(snapshot);
        if (primary is null || primary.YouHits == 0 || primary.ApproxDamageDone <= 0)
            return null;
        if (history.GroupName.Length > 0
            && !string.Equals(primary.NpcGroup, history.GroupName, StringComparison.OrdinalIgnoreCase))
            return null;
        return primary.ApproxDamageDone / primary.YouHits;
    }

    private static bool IsCurrentWeapon(string weapon, string? current)
        => !string.IsNullOrWhiteSpace(current) && string.Equals(weapon, current, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The alternate weapon the rail offers on Ctrl+W: the carried item most worth switching to,
    /// or null when nothing in the pack qualifies.
    ///
    /// <para>Candidates come from the live FEI inventory, narrowed by
    /// <paramref name="isKnownWeapon"/> to items already on file as having been fought with (see
    /// <c>HistoryIndex.IsKnownWeapon</c> - the client has no other way to tell a weapon from a
    /// postcard, and guessing costs a dropped guard). The one in hand is excluded, since offering to
    /// swap to what you are already holding is noise.</para>
    ///
    /// <para>Ranking is by this NPC group's own record: highest median damage per landed blow first,
    /// which is the axis MUD2's hidden per-creature weapon modifiers show up on (dagger0 kills
    /// zombies in 2.3 hits where axe0 needs 5.0). Weapons with no record against THIS group rank
    /// after every weapon that has one, in inventory order - they are still real offers, just
    /// unevidenced ones. Deliberately NOT gated on beating the weapon in hand: the player asks for
    /// this key when their weapon has broken or been refused, and at that moment "worse than what
    /// you had" is still the only thing to fight with.</para>
    ///
    /// <para>Out of combat <paramref name="byWeapon"/> is empty (there is no group to score
    /// against), so the offer falls back to inventory order over known weapons.</para>
    /// </summary>
    internal static string? ChooseAltWeapon(
        IReadOnlyList<string> carried,
        string? currentWeapon,
        IReadOnlyList<WeaponHistorySummary> byWeapon,
        Func<string, bool> isKnownWeapon)
    {
        string? best = null;
        double? bestPerHit = null;

        foreach (var item in carried)
        {
            if (string.IsNullOrWhiteSpace(item) || IsCurrentWeapon(item, currentWeapon))
                continue;
            if (!isKnownWeapon(item))
                continue;

            double? perHit = null;
            foreach (var entry in byWeapon)
            {
                if (!string.Equals(entry.Weapon, item, StringComparison.OrdinalIgnoreCase))
                    continue;
                perHit = entry.Summary.MedianDamagePerHit;
                break;
            }

            // First qualifying item wins by default; after that only a strictly better evidenced
            // per-hit figure displaces it, so unevidenced candidates never outrank evidenced ones
            // and ties resolve to inventory order.
            if (best is null || (perHit is double rate && (bestPerHit is not double top || rate > top)))
            {
                best = item;
                bestPerHit = perHit;
            }
        }

        return best;
    }

    /// <summary>
    /// The typeable noun for an item name - what goes after <c>wield</c>.
    ///
    /// <para>MUD2 item names may arrive with descriptive words in front ("a rusty pick2"), and only
    /// the final token identifies the object to the parser. Distinct from
    /// <see cref="DisplayName"/>, which shortens for a fixed-width column and deliberately leaves
    /// short names alone - a display rule must never decide what gets typed at the game.</para>
    /// </summary>
    internal static string CommandNoun(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var trimmed = name.Trim();
        var lastSpace = trimmed.LastIndexOf(' ');
        return lastSpace < 0 || lastSpace == trimmed.Length - 1 ? trimmed : trimmed[(lastSpace + 1)..];
    }

    /// <summary>The fight the comparison describes: the first still-unresolved one, falling back to
    /// the first of the encounter so the block survives the post-kill grace window.</summary>
    internal static FightSnapshot? PrimaryFight(CombatEncounterSnapshot snapshot)
    {
        FightSnapshot? first = null;
        foreach (var fight in snapshot.Fights)
        {
            first ??= fight;
            if (!fight.IsResolved)
                return fight;
        }

        return first;
    }

    private static double? HitRate(int hits, int misses)
    {
        var attempts = hits + misses;
        return attempts == 0 ? null : hits / (double)attempts;
    }

    private static string Truncate(string value, int width)
        => value.Length <= width ? value : value[..width];

    /// <summary>
    /// Shortens a long item name for DISPLAY only, never for recording.
    ///
    /// <para>MUD2 item names carry descriptive prefixes ("a rusty pick2", "the ornate falchion3") but
    /// the trailing token with its instance number is the part that identifies the thing and the part
    /// the player types. So for anything over <see cref="DisplayNameThreshold"/> characters whose last
    /// word ends in a digit, that last word becomes the label - "a rusty pick2" shows as "pick2",
    /// which also stops one long name from widening the whole weapon column.</para>
    ///
    /// <para>Names whose last word does NOT end in a digit are left alone: "croquet mallet" has no
    /// instance number, so "mallet" would be a lossy guess rather than the canonical short form.
    /// FightRecord always stores the full name, so history and the offline pipeline are unaffected.</para>
    /// </summary>
    internal static string DisplayName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var trimmed = name.Trim();
        if (trimmed.Length <= DisplayNameThreshold)
            return trimmed;

        var lastSpace = trimmed.LastIndexOf(' ');
        if (lastSpace < 0 || lastSpace == trimmed.Length - 1)
            return trimmed;

        var lastWord = trimmed[(lastSpace + 1)..];
        return char.IsAsciiDigit(lastWord[^1]) ? lastWord : trimmed;
    }
}
