namespace Mucka.Ui;

/// <summary>One object in a Carrying or Here list, kept for as long as the list reports it.
/// <see cref="ArrivedUtc"/> is null for an object that was already there when the list was first
/// read (or re-read after a <see cref="ArrivalLedger.Reset"/>): those are not arrivals.</summary>
public sealed class ArrivalSlot
{
    public string Name { get; }
    public DateTime? ArrivedUtc { get; }

    public ArrivalSlot(string name, DateTime? arrivedUtc)
    {
        Name = name;
        ArrivedUtc = arrivedUtc;
    }
}

/// <summary>
/// Tracks which objects in a list the game reports have just ARRIVED, and orders the list by arrival.
///
/// <para>The game lists objects in its own order and says nothing about what changed. Each
/// <see cref="Reconcile"/> compares the new list with the previous one by name, as a multiset: a name
/// that appears more often than before has that many new arrivals, one that appears less often has
/// lost that many. The first list after construction or <see cref="Reset"/> is a baseline - nothing
/// in it is an arrival.</para>
///
/// <para>Display order: arrivals younger than <see cref="PinFor"/> first, newest on top, then
/// everything else in the game's order. An arrival past <see cref="PinFor"/> falls back to the
/// game's order on the next reconcile; no timer is involved.</para>
///
/// <para>When one of several identically named objects leaves, the OLDEST is the one dropped, so the
/// recent arrival keeps its place and its glow.</para>
/// </summary>
public sealed class ArrivalLedger
{
    /// <summary>How long an arrival is drawn highlighted.</summary>
    public static readonly TimeSpan GlowFor = TimeSpan.FromSeconds(1.8);

    /// <summary>Opacity of the highlight <paramref name="elapsed"/> after arrival. Cubic, not linear:
    /// a linear fade reads as finished within about a third of its length, because gold over grey
    /// stops looking gold well before it is transparent. This holds bright and drops late.</summary>
    public static float GlowOpacity(TimeSpan elapsed)
    {
        var p = Math.Clamp(elapsed / GlowFor, 0.0, 1.0);
        return (float)(1.0 - p * p * p);
    }

    /// <summary>How long an arrival keeps its place at the top of the list.</summary>
    public static readonly TimeSpan PinFor = TimeSpan.FromSeconds(12);

    private List<ArrivalSlot> _slots = [];
    private bool _primed;

    /// <summary>Forgets everything. The next list is a baseline again.</summary>
    public void Reset()
    {
        _slots = [];
        _primed = false;
    }

    /// <summary>Folds the game's current list in and returns the slots in display order. A slot
    /// that persists from one call to the next is the same instance, so callers can diff by
    /// reference.</summary>
    public IReadOnlyList<ArrivalSlot> Reconcile(IReadOnlyList<string> gameOrder, DateTime nowUtc)
    {
        var announce = _primed;
        _primed = true;

        // Per name, previous slots newest-first, so whatever is left unclaimed is the oldest.
        var pool = new Dictionary<string, Queue<ArrivalSlot>>(StringComparer.Ordinal);
        foreach (var group in _slots
                     .GroupBy(s => s.Name, StringComparer.Ordinal))
        {
            pool[group.Key] = new Queue<ArrivalSlot>(
                group.OrderByDescending(s => s.ArrivedUtc ?? DateTime.MinValue));
        }

        var next = new List<ArrivalSlot>(gameOrder.Count);
        foreach (var name in gameOrder)
        {
            if (pool.TryGetValue(name, out var q) && q.Count > 0)
                next.Add(q.Dequeue());
            else
                next.Add(new ArrivalSlot(name, announce ? nowUtc : null));
        }
        _slots = next;

        var pinned = next
            .Select((slot, i) => (slot, i))
            .Where(p => p.slot.ArrivedUtc is { } at && nowUtc - at < PinFor)
            .OrderByDescending(p => p.slot.ArrivedUtc)
            .ThenBy(p => p.i)
            .Select(p => p.slot);
        var rest = next.Where(s => s.ArrivedUtc is not { } at || nowUtc - at >= PinFor);
        return pinned.Concat(rest).ToList();
    }
}
