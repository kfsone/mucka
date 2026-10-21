namespace MudSharp.Combat;

/// <summary>How one participant's fight ended, as the book records it. <see cref="Ended"/> is a coded
/// fight end that gave no reason. <see cref="FateUnseen"/> is not a game ending: it is what a row
/// still parked when the encounter closes shows.</summary>
public enum UnseenOutcome { Killed, Died, Fled, FleeFailed, Withdrew, Ended, KilledYou, FateUnseen }

/// <summary>The subject of a combat line: a name, or the anonymous word the line used. Names are
/// taken as given; the caller normalises them.</summary>
public readonly record struct UnseenActor(string? Name, SomeKind Word)
{
    public static UnseenActor Named(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return new(name, SomeKinds.Default);
    }

    public static UnseenActor Anonymous(SomeKind word) => new(null, word);
}

/// <summary>One fight with one named creature, and whether it is shown inside the unseen group. A
/// creature fought twice has two rows. Its figures are its fight's in the aggregator, not the
/// book's.</summary>
public sealed class UnseenRow
{
    internal UnseenRow(string name, int index, SomeKind? cls) { Name = name; Index = index; Class = cls; }
    public string Name { get; }
    internal int Index { get; }
    internal SomeKind? Class { get; }
    /// <summary>Opened and not concluded, as far as the lines the book was fed can say.</summary>
    public bool Open { get; internal set; } = true;
    public bool Parked { get; internal set; }
    public bool HasSwung { get; internal set; }
    public UnseenOutcome? Outcome { get; internal set; }
    /// <summary>The outcome came from counting anonymous endings, not from a line carrying the name;
    /// such a row carries no score.</summary>
    public bool ByCounting { get; internal set; }
    /// <summary>The ending that concluded this row: its own named ending, or the anonymous ending
    /// whose arrival let counting place the outcome. -1 while none has.</summary>
    public int EndingId { get; internal set; } = -1;
    internal bool EverUnseen;
    internal int Order = int.MaxValue;
}

/// <summary>One anonymous own-fight ending, or a named one that concluded an anonymous slot.</summary>
public sealed class UnseenConclusion
{
    internal UnseenConclusion(int id, UnseenOutcome outcome, SomeKind word, string? name, int order)
    { Id = id; Outcome = outcome; Word = word; Name = name; Order = order; }
    public int Id { get; }
    public UnseenOutcome Outcome { get; }
    public SomeKind Word { get; }
    /// <summary>Set only when a named ending line concluded a slot.</summary>
    public string? Name { get; }
    /// <summary>Counting placed it on a named row, so its score is the group's.</summary>
    public bool HandedToRow { get; internal set; }
    internal int Order { get; }
}

public enum UnseenBadgeKind { Group, Conclusion, Row, Lump }

/// <summary>One history badge. Participants and Flights only on Group and Lump. Counted marks a row whose outcome came from counting. <see cref="ScoredBy"/> are
/// the endings (ids from <see cref="UnseenBook.End"/>) whose announced scores the badge sums, and
/// <see cref="Score"/> is that sum as the lookup handed to <see cref="UnseenBook.Badges"/> gave it.
/// <see cref="PlacedBy"/> is the ending that puts the badge in time, or -1 for the group, the lump
/// and a row nothing concluded.</summary>
public sealed record UnseenBadge(UnseenBadgeKind Kind, string? Name, UnseenOutcome? Outcome, int? Score,
    int Participants = 0, int Flights = 0, bool Counted = false,
    IReadOnlyList<int>? ScoredBy = null, int PlacedBy = -1);

/// <summary>
/// The unseen-group model of docs/unseen-presentation.md, fed combat lines only. A name receives a
/// blow, a score or an ending only from a line carrying that name; a row's outcome may also come
/// from counting, which never gives it a score. One instance per encounter: after
/// <see cref="Close"/> every input throws. Pure: no clock, no I/O, single-threaded. Scores are not
/// held here: each badge names the endings it sums, and the caller supplies what was announced.
/// </summary>
public sealed class UnseenBook
{
    /// <summary>Operator's figure: more unseen badges than this collapse into one.</summary>
    public const int MaxUnseenBadges = 5;

    private readonly Func<string, SomeKind?> _classOf;
    private readonly Dictionary<string, SomeKind?> _classCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<UnseenRow> _rows = [];
    private readonly Dictionary<string, UnseenRow> _latest = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<UnseenRow> _openRows = [];
    private readonly int[] _openByClass = new int[3]; // Someone, Something, unknown
    private int _parkedOpen;
    private readonly int[] _openSlots = new int[2];
    private int _slotsEver, _slotsToRows;
    private readonly List<UnseenConclusion> _conclusions = [];
    private readonly List<UnseenConclusion> _pending = [];
    private readonly int[] _pendingByWord = new int[2];
    private bool _pendingMixed;
    private int _nextId, _nextOrder;
    private bool _countingOff;

    public UnseenBook(Func<string, SomeKind?>? classOf = null) => _classOf = classOf ?? (_ => null);

    /// <summary>Anonymous blows fed, either direction. The group's figures are its fight's in the
    /// aggregator; the book needs only whether there were any.</summary>
    public int GroupBlows { get; private set; }
    public IReadOnlyList<UnseenRow> Rows => _rows;
    public IReadOnlyList<UnseenConclusion> Conclusions => _conclusions;
    public bool Closed { get; private set; }

    /// <summary>Rises on every change to what <see cref="Badges"/>, the counts or the parked rows
    /// can show - a start, an ending, a park or promotion, the first blow of a row or of the group,
    /// a change of sight, a wake-up, the close - and on nothing else, so an unchanged version is an
    /// unchanged snapshot.</summary>
    public int Version { get; private set; }
    /// <summary>The player cannot see: dark or blind. Nothing in the book decides on it; the snapshot
    /// carries it to the rail (<see cref="UnseenSnapshot.From"/>).</summary>
    public bool CannotSee { get; private set; }

    /// <summary>The anonymous slots still open, and not matched by a pending ending, when the
    /// encounter closed: participants that never concluded.</summary>
    public int SlotsLeftAtClose { get; private set; }

    /// <summary>The rail's "x N": open slots plus open parked rows, less the own-fight anonymous
    /// endings not yet placed (each concluded one of them).</summary>
    public int UnseenCount => Math.Max(0, OpenSlots + _parkedOpen - _pending.Count);

    /// <summary>The participants still open: open rows, parked or not, plus open slots, less the
    /// own-fight anonymous endings counting has not placed - each of those concluded one open
    /// participant without saying which. The encounter is over when this reaches zero; zero once
    /// closed.</summary>
    public int OpenParticipants => _openRows.Count + OpenSlots - _pending.Count;

    /// <summary>Anonymous slots opened and not concluded by a line or by counting.</summary>
    public int OpenSlots => _openSlots[0] + _openSlots[1];

    /// <summary>The one definition of a slot that can still be concluded or spent: open, and not
    /// already matched by a pending anonymous ending of its word. A pending ending concluded one
    /// participant; a slot of its word it may be about is not available to a name.</summary>
    public int AvailableSlotsOf(SomeKind word) => Math.Max(0, _openSlots[(int)word] - _pendingByWord[(int)word]);

    /// <summary><see cref="AvailableSlotsOf"/> over both words.</summary>
    public int AvailableSlots => AvailableSlotsOf(SomeKind.Someone) + AvailableSlotsOf(SomeKind.Something);

    /// <summary>Own-fight anonymous endings counting has not placed yet. Each concluded one open
    /// participant, slot or row, without saying which.</summary>
    public int PendingEndings => _pending.Count;

    /// <summary>Rows opened and not concluded, parked or not.</summary>
    public int OpenRowCount => _openRows.Count;

    /// <summary>Whether an open slot of <paramref name="word"/> not already matched by a pending
    /// ending of that word, or an open row whose class could produce it (that class, or unknown),
    /// exists - something still in the book that an anonymous line of that word can be about.</summary>
    public bool HasOpenProducer(SomeKind word)
        => AvailableSlotsOf(word) > 0 || _openByClass[(int)word] + _openByClass[ClassIndex(null)] > 0;

    /// <summary>Open rows drawn inside the unseen group.</summary>
    public IEnumerable<UnseenRow> ParkedRows => _openRows.Where(r => r.Parked);

    /// <summary>The most recent row for this name.</summary>
    public UnseenRow? Row(string name) => _latest.GetValueOrDefault(name);

    public void Start(UnseenActor who)
    {
        var name = Check(who);
        if (name is null)
        {
            ParkCompatible(who.Word);
            _openSlots[(int)who.Word]++;
            _slotsEver++;
            Version++;
            return;
        }
        if (Row(name) is not { Open: true })
            OpenRow(name);
        else if (_pending.Count > 0)
            // A start for an open row while anonymous endings are pending: one of those endings may
            // have been this Creature's, so counting is off for the rest of the encounter
            // (UnseenBookTests.StartForAnOpenRowWhileEndingsPend_TurnsCountingOff).
            _countingOff = true;
    }

    /// <summary>A blow, either direction. An anonymous one is the group's and parks the rows that
    /// could have thrown it; a named one is its row's, and brings the row out of the group.</summary>
    public void Swing(UnseenActor who)
    {
        var name = Check(who);
        if (name is null)
        {
            if (GroupBlows++ == 0)
                Version++;
            ParkCompatible(who.Word);
            return;
        }
        var row = Row(name);
        if (row is null)
        {
            var spent = SpendSlot(name);
            row = OpenRow(name);
            row.EverUnseen = spent;
            if (spent)
                _slotsToRows++;
        }
        else if (!row.Open)
            row = OpenRow(name);
        SetParked(row, false);
        if (!row.HasSwung)
        {
            row.HasSwung = true;
            Version++;
        }
    }

    /// <summary>An ending line. <paramref name="ownFight"/>: the line is coded as a fight end, is a
    /// "You have killed ..." line, or names a Creature that has an open row - a named ending of an
    /// engaged Creature concludes its row whoever caused it, because the line carries the name. A
    /// plain line naming a Creature with no row, and a plain anonymous line, are not own-fight: they
    /// conclude nothing and spend no slot. A named ending for a name the book has never had a row
    /// for concludes an available slot (<see cref="AvailableSlotsOf"/>) or nothing: with no slot
    /// available it opens no row. Returns the ending's id, which the badge that shows it lists in
    /// <see cref="UnseenBadge.ScoredBy"/>, or -1 when the line concludes nothing.</summary>
    public int End(UnseenActor who, UnseenOutcome outcome, bool ownFight = true)
    {
        var name = Check(who);
        if (name is null)
            ParkCompatible(who.Word);
        if (!ownFight)
            return -1;
        var row = name is null ? null : Row(name);
        SomeKind? slotWord = null;
        if (name is not null && row is null && (slotWord = SlotWordFor(name)) is null)
            return -1;
        var id = _nextId++;
        Version++;
        if (name is null)
        {
            var c = new UnseenConclusion(id, outcome, who.Word, null, _nextOrder++);
            _conclusions.Add(c);
            if (_pending.Count > 0 && _pending[0].Outcome != outcome)
                _pendingMixed = true;
            _pending.Add(c);
            _pendingByWord[(int)who.Word]++;
            Settle(id);
            return id;
        }
        // Any own-fight ending - kill, flight, withdrawal, death, "no longer" - for a name with no row
        // concludes an available slot, the name kept in the conclusion.
        if (slotWord is { } word)
        {
            _openSlots[(int)word]--;
            _conclusions.Add(new UnseenConclusion(id, outcome, word, name, _nextOrder++));
            Settle(id);
            return id;
        }
        if (row is not { Open: true })
            row = OpenRow(name);
        SetParked(row, false);
        CloseRow(row);
        row.Outcome = outcome;
        row.Order = _nextOrder++;
        row.EndingId = id;
        Settle(id);
        return id;
    }

    /// <summary>The conclusion a slot-concluding or anonymous ending produced, or null when the ending
    /// <paramref name="endingId"/> was a named row's own.</summary>
    public UnseenConclusion? ConclusionOf(int endingId)
    {
        for (var i = _conclusions.Count - 1; i >= 0; i--)
            if (_conclusions[i].Id == endingId)
                return _conclusions[i];
        return null;
    }

    /// <summary>A witnessed fade of an engaged creature.</summary>
    public void Faded(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ThrowIfClosed();
        if (Row(name) is { Open: true } row)
            SetParked(row, true);
    }

    public void SightLost()
    {
        ThrowIfClosed();
        if (!CannotSee)
            Version++;
        CannotSee = true;
        foreach (var row in _openRows)
            SetParked(row, true);
    }

    /// <summary>Promotes nothing: only a swing line carrying a name does.</summary>
    public void SightRegained()
    {
        ThrowIfClosed();
        if (CannotSee)
            Version++;
        CannotSee = false;
    }

    /// <summary>"You have just been woken up!" or "has woken up!": starts may be missing from here
    /// on, so counting is off for the rest of the encounter. Slot spending stays on: it changes only
    /// the count and never moves a blow, and with it off a slot whose Creature is then named is left
    /// open for good (UnseenBookTests.WokenUp_TurnsCountingOff_AndLeavesSlotSpendingOn).</summary>
    public void WokenUp()
    {
        ThrowIfClosed();
        _countingOff = true;
        Version++;
    }

    public void Close()
    {
        ThrowIfClosed();
        foreach (var row in _openRows)
        {
            row.Open = false;
            if (row.Parked)
                row.Outcome = UnseenOutcome.FateUnseen;
        }
        _openRows.Clear();
        _openByClass[0] = _openByClass[1] = _openByClass[2] = 0;
        SlotsLeftAtClose = AvailableSlots;
        _pending.Clear();
        _openSlots[0] = _openSlots[1] = _parkedOpen = 0;
        Closed = true;
        Version++;
    }

    private string? Check(UnseenActor who)
    {
        ThrowIfClosed();
        if (who.Name is not null)
            ArgumentException.ThrowIfNullOrEmpty(who.Name, nameof(who));
        return who.Name;
    }

    private void ThrowIfClosed()
    {
        if (Closed)
            throw new InvalidOperationException("The encounter is closed; use a new UnseenBook.");
    }

    private SomeKind? ClassOf(string name)
    {
        if (!_classCache.TryGetValue(name, out var k))
            _classCache[name] = k = _classOf(name);
        return k;
    }

    private static int ClassIndex(SomeKind? k) => k is { } v ? (int)v : 2;

    private UnseenRow OpenRow(string name)
    {
        var row = new UnseenRow(name, _rows.Count, ClassOf(name));
        _rows.Add(row);
        _latest[name] = row;
        _openRows.Add(row);
        _openByClass[ClassIndex(row.Class)]++;
        Version++;
        return row;
    }

    private void CloseRow(UnseenRow row)
    {
        row.Open = false;
        _openRows.Remove(row);
        _openByClass[ClassIndex(row.Class)]--;
    }

    private void SetParked(UnseenRow row, bool parked)
    {
        if (row.Parked == parked)
            return;
        row.Parked = parked;
        Version++;
        if (parked)
            row.EverUnseen = true;
        if (row.Open)
            _parkedOpen += parked ? 1 : -1;
    }

    private void ParkCompatible(SomeKind word)
    {
        foreach (var row in _openRows)
            if (row.Class is not { } k || k == word)
                SetParked(row, true);
    }

    /// <summary>The word of an available slot (<see cref="AvailableSlotsOf"/>) a name with no row
    /// could have started under, or null.</summary>
    private SomeKind? SlotWordFor(string name)
    {
        int one = AvailableSlotsOf(SomeKind.Someone), thing = AvailableSlotsOf(SomeKind.Something);
        if (ClassOf(name) is { } k)
            return (k == SomeKind.Someone ? one : thing) > 0 ? k : null;
        if (one == 0 && thing == 0)
            return null;
        return thing >= one ? SomeKind.Something : SomeKind.Someone;
    }

    private bool SpendSlot(string name)
    {
        if (SlotWordFor(name) is not { } word)
            return false;
        _openSlots[(int)word]--;
        Version++;
        return true;
    }

    /// <summary>Counting: every participant concludes once, so as many pending own-fight anonymous
    /// endings as open participants, all of one outcome, means every participant had that outcome.
    /// An ending of word W goes only to a slot of word W; the leftovers of each word must fit the
    /// open rows that could have produced that word.</summary>
    private void Settle(int endingId)
    {
        if (_countingOff || _pending.Count == 0 || _pendingMixed)
            return;
        int lOne = _pendingByWord[0] - _openSlots[0], lThing = _pendingByWord[1] - _openSlots[1];
        if (lOne < 0 || lThing < 0 || lOne + lThing != _openRows.Count
            || lOne < _openByClass[0] || lThing < _openByClass[1])
            return;
        var outcome = _pending[0].Outcome;
        var toSlots = new[] { _openSlots[0], _openSlots[1] };
        foreach (var c in _pending)
        {
            if (toSlots[(int)c.Word] > 0)
                toSlots[(int)c.Word]--;
            else
                c.HandedToRow = true;
        }
        foreach (var row in _openRows)
        {
            row.Open = false;
            row.Outcome = outcome;
            row.ByCounting = true;
            row.EndingId = endingId;
            row.Order = _nextOrder++;
        }
        _openRows.Clear();
        _openByClass[0] = _openByClass[1] = _openByClass[2] = 0;
        _openSlots[0] = _openSlots[1] = _parkedOpen = 0;
        _pending.Clear();
        _pendingByWord[0] = _pendingByWord[1] = 0;
    }

    /// <summary>The history badges, per the spec's "What the history shows". <paramref name="scoreOf"/>
    /// is the score announced for an ending id, or null for none; without it no badge has a
    /// score.</summary>
    public IReadOnlyList<UnseenBadge> Badges(Func<int, int?>? scoreOf = null)
    {
        int? Sum(IReadOnlyList<int> ids)
        {
            int? total = null;
            if (scoreOf is not null)
                foreach (var id in ids)
                    if (scoreOf(id) is { } s)
                        total = (total ?? 0) + s;
            return total;
        }
        UnseenBadge Scored(UnseenBadge b) => b with { Score = Sum(b.ScoredBy!) };

        var everUnseen = _slotsEver - _slotsToRows + _rows.Count(r => r.EverUnseen);
        int[] handed = [.. _conclusions.Where(c => c.HandedToRow).Select(c => c.Id)];
        var entries = _conclusions.Where(c => !c.HandedToRow)
            .Select(c => (c.Order, Index: -1, Badge: new UnseenBadge(UnseenBadgeKind.Conclusion, c.Name, c.Outcome, null,
                ScoredBy: [c.Id], PlacedBy: c.Id)))
            .Concat(_rows.Where(r => r.ByCounting)
                .Select(r => (r.Order, r.Index, Badge: new UnseenBadge(UnseenBadgeKind.Row, r.Name, r.Outcome, null,
                    Counted: true, ScoredBy: [], PlacedBy: r.EndingId))))
            .ToList();
        var named = _rows.Where(r => !r.ByCounting && (r.HasSwung || r.Outcome is not null))
            .Select(r => (r.Order, r.Index, Badge: new UnseenBadge(UnseenBadgeKind.Row, r.Name, r.Outcome, null,
                ScoredBy: r.EndingId < 0 ? [] : [r.EndingId], PlacedBy: r.EndingId)));
        var result = new List<UnseenBadge>();
        // No group badge when the group holds nothing: no anonymous blow and no conclusion. A row
        // parked for a moment (a short blindness, a fade) with nothing anonymous after it was on the
        // rail inside the group while it lasted, and leaves no empty badge in the history.
        if (GroupBlows == 0 && entries.Count == 0)
            return Ordered(named).Select(Scored).ToList();
        // Likewise when the only entries are rows counting concluded with an ending that is never
        // scored ("You can fight someone no longer." handed to a parked player): the group would
        // carry nothing - no blow, no conclusion of its own, no score.
        if (GroupBlows == 0 && entries.All(e => e.Badge.Kind == UnseenBadgeKind.Row)
            && !_conclusions.Any(c => c.HandedToRow
                && c.Outcome is UnseenOutcome.Killed or UnseenOutcome.Fled or UnseenOutcome.FleeFailed))
            return Ordered(entries.Concat(named)).Select(Scored).ToList();
        if (1 + entries.Count > MaxUnseenBadges)
        {
            var flights = entries.Count(e => e.Badge.Outcome is UnseenOutcome.Fled or UnseenOutcome.FleeFailed);
            int[] all = [.. handed, .. entries.SelectMany(e => e.Badge.ScoredBy!)];
            result.Add(new UnseenBadge(UnseenBadgeKind.Lump, null, null, null, everUnseen, flights, ScoredBy: all));
        }
        else if (everUnseen == 1 && entries.Count == 1 && entries[0].Badge.Kind == UnseenBadgeKind.Conclusion)
        {
            // Only an anonymous slot folds: a counted row keeps its own badge.
            // The name shows only when a named ending line concluded the slot.
            var only = entries[0].Badge;
            result.Add(new UnseenBadge(UnseenBadgeKind.Group, only.Name,
                only.Outcome, null, 1, ScoredBy: [.. only.ScoredBy!, .. handed], PlacedBy: only.PlacedBy));
        }
        else
        {
            result.Add(new UnseenBadge(UnseenBadgeKind.Group, null, null, null, everUnseen, ScoredBy: handed));
            result.AddRange(Ordered(entries.Concat(named)));
            return result.Select(Scored).ToList();
        }
        result.AddRange(Ordered(named));
        return result.Select(Scored).ToList();
    }

    private static IEnumerable<UnseenBadge> Ordered(IEnumerable<(int Order, int Index, UnseenBadge Badge)> entries)
        => entries.OrderBy(e => e.Order).ThenBy(e => e.Index).Select(e => e.Badge);
}
