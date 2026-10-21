using MudSharp.Combat;

namespace Mucka.Combat;

/// <summary>
/// The dead strip's unseen badges (docs/unseen-presentation.md, "What the history shows"), made from
/// the encounter's last <see cref="UnseenSnapshot"/> once the encounter has closed. The group, a
/// folded group and the lump carry the unseen group's own fight figures; a conclusion carries none; a
/// row handed its outcome by counting carries its own fight's figures and never a score.
///
/// <para>Pure and MAUI-free, so the history the view model folds can be pinned by a test.</para>
/// </summary>
public static class UnseenEndings
{
    /// <summary>Whether a fight's ending is drawn by the unseen badges rather than by a row of its
    /// own: the unseen group's fight, and a fight whose outcome counting placed.</summary>
    public static bool ShownByEntries(FightSnapshot fight)
        => AnonymousOpponent.IsGroup(fight.NpcName) || fight.ByCounting;

    /// <summary>The unseen badges of one closed encounter, in the snapshot's order.</summary>
    public static IEnumerable<CombatEnding> For(
        UnseenSnapshot? unseen, IReadOnlyList<FightSnapshot> fights,
        int encounterOrdinal, int resetOrdinal, long? personaSessionId)
    {
        if (unseen is null)
            yield break;
        var group = fights.LastOrDefault(f => AnonymousOpponent.IsGroup(f.NpcName));
        foreach (var entry in unseen.Entries)
        {
            var (name, detail, fight) = entry.Kind switch
            {
                UnseenBadgeKind.Lump => (AnonymousOpponent.Group,
                    $"x{entry.Participants} ()x{entry.Flights}", group),
                // The whole fight folded into one badge under a name a line gave it: "(basilisk)",
                // as MUD2 itself writes an invisible persona.
                UnseenBadgeKind.Group => entry.Name is { } folded ? ($"({folded})", (string?)null, group) : (AnonymousOpponent.Group, null, group),
                // A slot concluded by a named line is that Creature's badge: its plain name.
                UnseenBadgeKind.Conclusion => (entry.Name ?? AnonymousOpponent.Group, null, null),
                _ => (entry.Name ?? AnonymousOpponent.Group, (string?)null,
                    fights.LastOrDefault(f => f.ByCounting && string.Equals(f.NpcName, entry.Name, StringComparison.OrdinalIgnoreCase))),
            };
            // A group or lump that stands for several endings has no outcome of its own - unless the
            // player's flight ended every fight, or a death or force end cut it short, which it shows
            // in the words a named row would. KILLED YOU is never the group badge's from here: an
            // unseen killer's death is its own conclusion (or the fold), and a named killer's is
            // that Creature's row alone.
            var outcome = entry.Kind is UnseenBadgeKind.Group or UnseenBadgeKind.Lump
                    && entry.Outcome == FightOutcome.Unresolved
                    && group?.Outcome is FightOutcome.UFled or FightOutcome.UFledFail or FightOutcome.Interrupted
                ? group.Outcome
                : entry.Outcome;
            yield return new CombatEnding(
                name, outcome, entry.At, encounterOrdinal, resetOrdinal,
                fight is null ? default : ExchangeLines.DealtLine(fight),
                fight is null ? default : ExchangeLines.TakenLine(fight),
                ScoreAwarded: null,
                Duration: fight?.Duration ?? default,
                PersonaSessionId: personaSessionId,
                Detail: detail,
                Awards: entry.Awards);
        }
    }

    /// <summary>What an unseen badge was scored: the sum of the awards paired to its endings, or null
    /// when none was - never a zero for nothing announced.</summary>
    public static int? Score(IReadOnlyList<UnseenAward> awards, Func<UnseenAward, int?> awardOf)
    {
        int? total = null;
        foreach (var award in awards)
            if (awardOf(award) is int points)
                total = (total ?? 0) + points;
        return total;
    }
}
