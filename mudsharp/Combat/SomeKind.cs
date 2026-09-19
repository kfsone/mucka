namespace MudSharp.Combat;

/// <summary>
/// The two words MUD2 puts in place of a Creature's name when the player cannot see it, as two
/// enumerations of one concept. Which word a Creature gets is a property of the Creature (its
/// class - person-shaped or not), not of the cause, so it can be learned once per species and
/// carried between fights. See <see cref="AnonymousOpponent"/> for the words themselves.
/// </summary>
public enum SomeKind
{
    /// <summary>"someone" - a person-shaped Creature: a player, the man, the thief, a zombie.</summary>
    Someone,
    /// <summary>"something" - any other Creature: a rat, a snake, the fox, an eagle.</summary>
    Something,
}

/// <summary>
/// How many Unseen opponents of each word are open right now, and whether the player can see. The
/// combat tracker's answer, read by the roster every refresh: N per word is the count of anonymous
/// "is about to attack you" / "You attack someone" starts still open, each such start being one
/// Creature (every attacker announces itself under 08.00; a swing never opens one of these).
/// </summary>
public readonly record struct UnseenState(int Someone, int Something, bool CannotSee)
{
    public int Count(SomeKind kind) => kind == SomeKind.Someone ? Someone : Something;

    public bool Any => Someone > 0 || Something > 0;
}

public static class SomeKinds
{
    public static readonly SomeKind[] Both = [SomeKind.Someone, SomeKind.Something];

    /// <summary>What prose calls a Creature whose kind is not yet known. The operator's choice: it
    /// reads right ("something hit you") where the alternative asserts a person.</summary>
    public const SomeKind Default = SomeKind.Something;

    public static SomeKind FromWord(string word)
        => AnonymousOpponent.Canonical(word) == AnonymousOpponent.Person ? SomeKind.Someone : SomeKind.Something;

    public static string Word(SomeKind kind)
        => kind == SomeKind.Someone ? AnonymousOpponent.Person : AnonymousOpponent.Thing;

    public static SomeKind Other(SomeKind kind)
        => kind == SomeKind.Someone ? SomeKind.Something : SomeKind.Someone;
}

/// <summary>
/// What this install has learned about which word each species gets. Keyed by species
/// (<see cref="NpcGroups.Normalize"/>: "rat0" teaches "rats"), because the kind is the species',
/// not the instance's. In memory here; <c>Mucka.Combat.SomeKindStore</c> carries it between runs
/// through the <c>some_kinds</c> table.
///
/// <para>Threading: <see cref="Learn"/> and <see cref="Known"/> run on the Feed thread under the
/// tracker's lock; <see cref="Restore"/> runs on whatever thread reads the table at startup, which
/// may overlap the first fight. The dictionary is guarded here so the two cannot meet in it.</para>
/// </summary>
public sealed class SomeKindKnowledge
{
    private readonly object _lock = new();
    private readonly Dictionary<string, SomeKind> _bySpecies = new(StringComparer.Ordinal);

    /// <summary>Raised once per species the first time its kind is learned, and again only if a
    /// later deduction changes it. Not raised by <see cref="Restore"/>.</summary>
    public event Action<string, SomeKind>? Revealed;

    public SomeKind? Known(string npcName)
    {
        // Normalised outside the lock: string work under a lock the Feed thread also takes is time
        // the UI thread's per-row lookups would spend blocking it.
        var species = NpcGroups.Normalize(npcName);
        lock (_lock)
            return _bySpecies.TryGetValue(species, out var kind) ? kind : null;
    }

    /// <summary>The kind to assume when writing about this Creature: what is known, else
    /// <see cref="SomeKinds.Default"/>. Never used to attribute a line - that needs a known kind.</summary>
    public SomeKind Assumed(string npcName) => Known(npcName) ?? SomeKinds.Default;

    public void Learn(string npcName, SomeKind kind)
    {
        var species = NpcGroups.Normalize(npcName);
        if (species.Length == 0 || AnonymousOpponent.IsAnonymous(species))
            return;
        lock (_lock)
        {
            if (_bySpecies.TryGetValue(species, out var known) && known == kind)
                return;
            _bySpecies[species] = kind;
        }
        Revealed?.Invoke(species, kind);
    }

    /// <summary>A row read back from the store: a species already normalised, learned in an earlier
    /// run. Silent - nothing to persist - and it never displaces a kind this run has already learned
    /// live, which is the newer observation and is on its way to the same row anyway.</summary>
    public void Restore(string species, SomeKind kind)
    {
        if (species.Length == 0 || AnonymousOpponent.IsAnonymous(species))
            return;
        lock (_lock)
            _bySpecies.TryAdd(species, kind);
    }

    /// <summary>Every species learned so far, as a copy.</summary>
    public IReadOnlyDictionary<string, SomeKind> All
    {
        get { lock (_lock) return new Dictionary<string, SomeKind>(_bySpecies, StringComparer.Ordinal); }
    }
}

/// <summary>
/// One stretch of a fight during which the player could not see, and what it teaches about the
/// Creatures in it. Everything here is a deduction over small sets, so the rules are written out as
/// rules rather than as a search; the operator supplied them as worked cases and they are pinned by
/// the tests that carry his cases under made-up names.
///
/// <para>The pieces: the Creatures ENGAGED and named when sight was lost (each of known or unknown
/// kind); NEWCOMERS that announced themselves during the episode ("Something is about to attack
/// you." - a Creature of known word and unknown identity); the WORDS seen on swings; the anonymous
/// ENDS (a kill or flee of a Creature of that word); and, once sight returns, the SURVIVORS named.</para>
///
/// <para>Rules, in the order they are applied on every note:</para>
/// <list type="number">
/// <item>An anonymous end removes one Creature of its word. Once survivors are named, the engaged
/// set minus the survivors is who was removed; if that is exactly one Creature and every end was of
/// one word, that Creature has that kind.</item>
/// <item>Exact elimination: an unknown Creature's kind is revealed when every assignment of kinds
/// to the unknowns that satisfies "each word seen was produced by at least one engaged Creature of
/// that kind" (newcomers counted by their announced word) gives it the same kind.</item>
/// <item>The single-kind heuristic, applied only when the episode CLOSES (sight returns, or the
/// fight ends still unseen): every engaged Creature unknown, no newcomers, no ends, exactly one word
/// seen over the whole episode - all of them get it. Not applied per note, because the first
/// "Someone hits you" of a two-word episode looks identical to the only one of a one-word episode;
/// only the close knows which it was. And it does not fire when a known Creature already explains
/// the word: with the thief known and the man unknown, "Someone hits you" says nothing about the
/// man.</item>
/// </list>
///
/// <para>Not done, deliberately: a newcomer is never matched to a name by set difference. With the
/// thief and the rat engaged, "Something is about to attack you." in the dark, and the thief, the
/// rat and a goat named afterwards, the operator's answer is "no reveals"; a stricter engine could
/// name the goat, and that is his to loosen.</para>
/// </summary>
public sealed class UnseenEpisode
{
    private readonly SomeKindKnowledge _knowledge;
    private readonly List<string> _engaged;
    private readonly List<SomeKind> _newcomers = new();
    private readonly HashSet<SomeKind> _wordsSeen = new();
    private readonly List<SomeKind> _ends = new();
    private readonly HashSet<string> _survivors = new(StringComparer.OrdinalIgnoreCase);
    private bool _sightReturned;

    public UnseenEpisode(IEnumerable<string> engagedNames, SomeKindKnowledge knowledge)
    {
        _knowledge = knowledge;
        _engaged = engagedNames.Where(n => !AnonymousOpponent.IsAnonymous(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>"Someone/Something is about to attack you." or "You attack someone/something." for a
    /// Creature not engaged before sight was lost.</summary>
    public void NoteAnonymousStart(SomeKind kind) { _newcomers.Add(kind); Deduce(); }

    /// <summary>Any swing line carrying an anonymous word, either direction.</summary>
    public void NoteAnonymousSwing(SomeKind kind) { _wordsSeen.Add(kind); Deduce(); }

    /// <summary>"You have killed someone/something." or an anonymous flee.</summary>
    public void NoteAnonymousEnd(SomeKind kind) { _ends.Add(kind); Deduce(); }

    /// <summary>Sight has returned and this Creature is named as present. Call once per named
    /// Creature; deduction re-runs after each.</summary>
    public void NoteNamed(string name) { _sightReturned = true; _survivors.Add(name); Deduce(); }

    /// <summary>A Creature named and engaged after the episode opened, while the player still cannot
    /// see - a pack member whose first line reached the client by name. It is part of the engaged
    /// set from here on, exactly as if it had been there when sight was lost.</summary>
    public void NoteEngaged(string name)
    {
        if (AnonymousOpponent.IsAnonymous(name) || _engaged.Contains(name, StringComparer.OrdinalIgnoreCase))
            return;
        _engaged.Add(name);
        Deduce();
    }

    /// <summary>The episode is over without sight returning - the fight ended, or the player left -
    /// so every word that was going to be seen has been. The single-kind heuristic runs here.</summary>
    public void Close() { _closed = true; Deduce(); }

    private bool _closed;

    private void Deduce()
    {
        RevealTheRemoved();
        RevealByElimination();
        RevealSingleKind();
    }

    // Rule 1.
    private void RevealTheRemoved()
    {
        if (!_sightReturned || _ends.Count == 0)
            return;
        var removed = _engaged.Where(n => !_survivors.Contains(n)).ToList();
        if (removed.Count != 1)
            return;
        var endKinds = _ends.Distinct().ToList();
        if (endKinds.Count != 1)
            return;
        if (_knowledge.Known(removed[0]) is null)
            _knowledge.Learn(removed[0], endKinds[0]);
    }

    /// <summary>Elimination enumerates every assignment of kinds to the unknown Creatures - two to
    /// the power of their number - on the Feed thread under the tracker's lock, once per anonymous
    /// line. Bounded here: the largest pack observed had 7 Creatures engaged at once (984
    /// encounters), so 8 covers every fight seen and caps the enumeration at 256.</summary>
    private const int MaxEliminationUnknowns = 8;

    // Rule 2.
    private void RevealByElimination()
    {
        var unknown = _engaged.Where(n => _knowledge.Known(n) is null).ToList();
        if (unknown.Count == 0 || unknown.Count > MaxEliminationUnknowns || _wordsSeen.Count == 0)
            return;

        // Every assignment of kinds to the unknowns; keep the consistent ones.
        var consistent = new List<SomeKind[]>();
        var total = 1 << unknown.Count;
        for (var mask = 0; mask < total; mask++)
        {
            var assignment = new SomeKind[unknown.Count];
            for (var i = 0; i < unknown.Count; i++)
                assignment[i] = (mask & (1 << i)) != 0 ? SomeKind.Someone : SomeKind.Something;
            if (IsConsistent(unknown, assignment))
                consistent.Add(assignment);
        }
        if (consistent.Count == 0)
            return;   // knowledge already held contradicts the episode; learn nothing from it

        for (var i = 0; i < unknown.Count; i++)
        {
            var first = consistent[0][i];
            if (consistent.All(a => a[i] == first))
                _knowledge.Learn(unknown[i], first);
        }
    }

    private bool IsConsistent(List<string> unknown, SomeKind[] assignment)
    {
        foreach (var word in _wordsSeen)
        {
            var producers = _newcomers.Count(k => k == word);
            producers += _engaged.Count(n => _knowledge.Known(n) == word);
            for (var i = 0; i < unknown.Count; i++)
                if (assignment[i] == word) producers++;
            if (producers == 0)
                return false;
        }
        return true;
    }

    // Rule 3. Only once the episode's word set is complete - see the class remarks.
    private void RevealSingleKind()
    {
        if (!(_sightReturned || _closed))
            return;
        if (_newcomers.Count > 0 || _ends.Count > 0 || _wordsSeen.Count != 1)
            return;
        if (_engaged.Count == 0 || _engaged.Any(n => _knowledge.Known(n) is not null))
            return;
        var kind = _wordsSeen.Single();
        foreach (var name in _engaged)
            _knowledge.Learn(name, kind);
    }
}
