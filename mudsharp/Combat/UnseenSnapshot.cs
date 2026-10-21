namespace MudSharp.Combat;

/// <summary>The key one announced score is paired under: the participant name the ending's event
/// carried and the event's timestamp - what <c>Mucka.Combat.KillAwardLedger</c> queues an ending
/// by.</summary>
public readonly record struct UnseenAward(string Name, DateTime At);

/// <summary>
/// One unseen badge of the history (docs/unseen-presentation.md, "What the history shows"): the
/// group, a conclusion, a row whose outcome came from counting, or the lump. Named rows with their
/// own endings are not entries - their fights carry them.
/// </summary>
/// <param name="Name">A conclusion's, or a folded group's, name when a named ending line concluded
/// the slot; a counted row's name. Null otherwise.</param>
/// <param name="Outcome">How it ended; <see cref="FightOutcome.Unresolved"/> for a group that did not
/// fold and for the lump, which stand for several endings.</param>
/// <param name="At">Where the badge sits in time: its ending's event timestamp, and for the group and
/// the lump a tick before the encounter's first unseen ending.</param>
/// <param name="Awards">The endings whose announced scores the badge sums.</param>
public sealed record UnseenEntry(
    UnseenBadgeKind Kind, string? Name, FightOutcome Outcome, DateTime At,
    IReadOnlyList<UnseenAward> Awards, int Participants = 0, int Flights = 0);

/// <summary>
/// The encounter's unseen group as <see cref="CombatTracker"/> last published it, carried on
/// <see cref="CombatEventKind.UnseenChanged"/>. Immutable. <see cref="Count"/> is the rail's "x N"
/// (<see cref="UnseenBook.UnseenCount"/>), <see cref="Parked"/> the named rows drawn inside the group,
/// <see cref="Entries"/> the history's unseen badges in order.
/// </summary>
/// <param name="Concluded">Unseen participants concluded by an anonymous ending, or by a named ending
/// of a slot, that counting did not hand to a named row - the ones no fight of a Creature
/// stands for.</param>
/// <param name="Killed">Of those, the kills.</param>
/// <param name="Fled">Of those, the flights that got away.</param>
/// <param name="KilledYou">Of those, the player's death.</param>
/// <param name="LeftOpen">Slots still open when the encounter closed (<see cref="UnseenBook.SlotsLeftAtClose"/>):
/// participants that never concluded.</param>
public sealed record UnseenSnapshot(
    int Count, IReadOnlyList<string> Parked, bool CannotSee, IReadOnlyList<UnseenEntry> Entries,
    int Concluded = 0, int Killed = 0, int Fled = 0, int KilledYou = 0, int LeftOpen = 0)
{
    public static readonly UnseenSnapshot Empty = new(0, [], false, []);

    /// <summary>The snapshot of <paramref name="book"/>. <paramref name="stampOf"/> gives the award key
    /// of an ending id (see <see cref="UnseenBook.End"/>); <paramref name="nowUtc"/> places a group or
    /// lump with no ending to sit before, and is a fixed instant of the encounter so that two
    /// snapshots of an unchanged book are equal.</summary>
    public static UnseenSnapshot From(UnseenBook book, Func<int, UnseenAward> stampOf, DateTime nowUtc)
    {
        var parked = book.ParkedRows.Select(r => r.Name).ToArray();
        var entries = new List<UnseenEntry>();
        DateTime? first = null;
        foreach (var badge in book.Badges())
        {
            if (badge.Kind == UnseenBadgeKind.Row && !badge.Counted)
                continue;
            var awards = badge.ScoredBy!.Select(stampOf).ToArray();
            DateTime? at = badge.PlacedBy >= 0 ? stampOf(badge.PlacedBy).At : null;
            if (at is { } placed && (first is null || placed < first))
                first = placed;
            entries.Add(new UnseenEntry(badge.Kind, badge.Name,
                badge.Outcome is { } o ? OutcomeOf(o) : FightOutcome.Unresolved,
                at ?? DateTime.MinValue, awards, badge.Participants, badge.Flights));
        }
        // The group and the lump stand first among the encounter's unseen badges.
        var lead = (first ?? nowUtc).AddTicks(-1);
        for (var i = 0; i < entries.Count; i++)
            if (entries[i].At == DateTime.MinValue)
                entries[i] = entries[i] with { At = lead };
        var own = book.Conclusions.Where(c => !c.HandedToRow).ToList();
        return new UnseenSnapshot(book.UnseenCount, parked, book.CannotSee, entries,
            own.Count,
            own.Count(c => c.Outcome == UnseenOutcome.Killed),
            own.Count(c => c.Outcome == UnseenOutcome.Fled),
            own.Count(c => c.Outcome == UnseenOutcome.KilledYou),
            book.Closed ? book.SlotsLeftAtClose : 0);
    }

    public static FightOutcome OutcomeOf(UnseenOutcome outcome) => outcome switch
    {
        UnseenOutcome.Killed => FightOutcome.Kill,
        UnseenOutcome.Died => FightOutcome.NoMore,
        UnseenOutcome.Fled => FightOutcome.CFled,
        UnseenOutcome.FleeFailed => FightOutcome.CFledFail,
        UnseenOutcome.Withdrew => FightOutcome.Withdraw,
        UnseenOutcome.Ended => FightOutcome.EndOther,
        UnseenOutcome.KilledYou => FightOutcome.Died,
        _ => FightOutcome.FateUnseen,
    };

    /// <summary>Equal in everything the rail and the history draw.</summary>
    public bool SameAs(UnseenSnapshot other)
        => Count == other.Count && CannotSee == other.CannotSee
            && Concluded == other.Concluded && Killed == other.Killed && Fled == other.Fled
            && KilledYou == other.KilledYou && LeftOpen == other.LeftOpen
            && Parked.SequenceEqual(other.Parked)
            && Entries.Count == other.Entries.Count
            && Entries.Zip(other.Entries).All(p => p.First.Kind == p.Second.Kind && p.First.Name == p.Second.Name
                && p.First.Outcome == p.Second.Outcome && p.First.At == p.Second.At
                && p.First.Participants == p.Second.Participants && p.First.Flights == p.Second.Flights
                && p.First.Awards.SequenceEqual(p.Second.Awards));
}
