#if WINDOWS
namespace Mucka.Core.Mapping;

/// <summary>Aggregate counts for the stats panel. Name-keyed, so room/edge counts are
/// name-level (same-name instances collapse) and "closed" is provisional (conditional
/// exits can reopen a room). Take two snapshots to diff a session's effect on the model.</summary>
public readonly record struct MapStats(
    int Rooms,         // distinct room names we have stood in (have a known exit set)
    int OpenRooms,     // explored rooms with at least one unresolved known exit
    int ClosedRooms,   // explored rooms with every known exit resolved (provisional)
    int Edges,         // resolved directed edges (traversals + structural refusals)
    int DarkExits);    // edges observed to lead into an unseen room -- revisit with light

/// <summary>
/// In-memory directed multigraph of captured edges, loaded from walk files.
/// Used for two things: static reciprocal-direction lookup (no load needed) and
/// BFS guidance (find the next uncaptured exit, or the first hop toward the nearest
/// room that has one).
/// </summary>
public sealed class MapGraph
{
    private static readonly Dictionary<string, string> Reciprocals =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["n"]    = "s",    ["s"]    = "n",
            ["ne"]   = "sw",   ["sw"]   = "ne",
            ["e"]    = "w",    ["w"]    = "e",
            ["se"]   = "nw",   ["nw"]   = "se",
            ["up"]   = "down", ["down"] = "up",
            ["in"]   = "out",  ["out"]  = "in",
            // swamp has no reciprocal -- it is a bearing, not a direction pair
        };

    /// <summary>Returns the reciprocal direction, or null for swamp.</summary>
    public static string? Reciprocal(string dir)
        => Reciprocals.TryGetValue(dir, out var r) ? r : null;

    // Per-room state accumulated from edge annotations.
    private sealed class RoomNode
    {
        // Exits the room is known to have (union of fex fingerprints seen when
        // starting a move from here -- the fex at move time is the ground truth).
        public HashSet<string> KnownExits { get; } = new(StringComparer.OrdinalIgnoreCase);
        // Directions that have been resolved (traversed or structural-refused) from this room.
        public HashSet<string> ResolvedDirs { get; } = new(StringComparer.OrdinalIgnoreCase);
        // Resolved neighbors: dir → destination room name. Name-level (collapses
        // same-name rooms) -- used only for guidance heuristics, never for return proof.
        public Dictionary<string, string> Neighbors { get; } = new(StringComparer.OrdinalIgnoreCase);
        // Fex-aware neighbors: "{fex}|{dir}" → destination. Disambiguates same-name rooms
        // with different exit sets (two "Flower garden"s, one with sw and one without), so
        // return-routing can trust "this exact room's dir leads to origin".
        public Dictionary<string, string> NeighborsByKey { get; } = new(StringComparer.OrdinalIgnoreCase);
        // Directions observed to lead into an unseen (dark) room: traversed, far end
        // unidentified for lack of light. NOT in ResolvedDirs -- they stay wanted, but
        // we want to surface them as "needs a light source" rather than plain unexplored.
        public HashSet<string> DarkExits { get; } = new(StringComparer.OrdinalIgnoreCase);

        // A room we have stood in (and so know the exits of) iff KnownExits is non-empty.
        public bool Explored => KnownExits.Count > 0;
        // Every known exit resolved -- provisional: a door/condition can mint a new exit later.
        public bool Closed   => Explored && KnownExits.All(ResolvedDirs.Contains);
    }

    private readonly Dictionary<string, RoomNode> _nodes;
    // Hand-authored travel-table rows, keyed "{from}|{fex}|{dir}" (matches MappingSession.EdgeKey).
    private readonly Dictionary<string, List<EdgeRule>> _rules =
        new(StringComparer.OrdinalIgnoreCase);
    // Reported (dangling, name-level) exit destinations from the exits verb, keyed
    // "{room}|{fex}|{dir}" -> destination reference name. Not persisted -- live this session.
    private readonly Dictionary<string, string> _reported =
        new(StringComparer.OrdinalIgnoreCase);

    private MapGraph(Dictionary<string, RoomNode> nodes) => _nodes = nodes;

    // ── Loading ────────────────────────────────────────────────────────────────

    internal static MapGraph CreateEmpty()
        => new(new Dictionary<string, RoomNode>(StringComparer.OrdinalIgnoreCase));

    internal void RecordAnnotation(MappingStore.EdgeAnnotation edge)
    {
        if (!_nodes.TryGetValue(edge.From, out var fromNode))
            _nodes[edge.From] = fromNode = new RoomNode();

        foreach (var exit in edge.ExitFingerprint.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            fromNode.KnownExits.Add(exit);

        if (edge.IsTraversal)
        {
            if (!edge.ResolvesEdge)
            {
                // Walked it but could not see the far end (no light). Record it as a dark
                // exit so the stats/room panels can flag "needs a light source"; it stays
                // out of ResolvedDirs so the compass keeps offering it.
                if (edge.Outcome == "(dark)")
                    fromNode.DarkExits.Add(edge.Direction);
                return;
            }
            fromNode.ResolvedDirs.Add(edge.Direction);
            fromNode.Neighbors[edge.Direction] = edge.Outcome;
            fromNode.NeighborsByKey[$"{edge.ExitFingerprint}|{edge.Direction}"] = edge.Outcome;
        }
        else
        {
            if (!edge.ResolvesEdge)
                return;
            fromNode.ResolvedDirs.Add(edge.Direction);
        }
    }

    // ── Queries / live updates ─────────────────────────────────────────────────

    /// <summary>Fex-aware destination evidence: where dir leads from the room *with this
    /// exact exit set*, or null when that room-state's dir has never been traversed.
    /// Distinguishes same-name rooms (two "Flower garden"s); this is the lookup
    /// return-routing must use so a sibling room's edge can't masquerade as this one's.</summary>
    public string? KnownDestination(string room, string fex, string dir)
        => _nodes.TryGetValue(room, out var n) && n.NeighborsByKey.TryGetValue($"{fex}|{dir}", out var dest)
            ? dest : null;

    /// <summary>Records a live traversal so decisions made this session see edges
    /// captured this session (the disk rescan only happens on reload). <paramref name="fex"/>
    /// is the from-room's exit fingerprint at move time -- keys the fex-aware lookup.</summary>
    public void RecordTraversal(string from, string fex, string dir, string to)
    {
        if (!_nodes.TryGetValue(from, out var node))
            _nodes[from] = node = new RoomNode();
        node.ResolvedDirs.Add(dir);
        node.Neighbors[dir] = to;
        node.NeighborsByKey[$"{fex}|{dir}"] = to;
    }

    /// <summary>Live mirror of a dark arrival (walked, far end unseen). Keeps the
    /// session's stats snapshot accurate without a disk rescan.</summary>
    public void RecordDarkExit(string from, string dir)
    {
        if (!_nodes.TryGetValue(from, out var node))
            _nodes[from] = node = new RoomNode();
        node.DarkExits.Add(dir);
    }

    // -- Edge rules (hand-authored travel-table rows) --

    /// <summary>Records a hand-authored edge rule (guard -> outcome). Rules accumulate;
    /// contradictions add rows, they never replace one (see MUD-Mapping-Design.md sect 4.2).</summary>
    internal void RecordRule(EdgeRule rule)
    {
        if (!_rules.TryGetValue(rule.EdgeKey, out var list))
            _rules[rule.EdgeKey] = list = new List<EdgeRule>();
        list.Add(rule);
    }

    /// <summary>Hand-authored rows for this exact edge (room + fex + dir), or empty. Returns a
    /// snapshot (callers iterate outside the session lock -- matches the other accessors).</summary>
    public IReadOnlyList<EdgeRule> RulesFor(string room, string fex, string dir)
        => _rules.TryGetValue($"{room}|{fex}|{dir}", out var l) ? l.ToArray() : Array.Empty<EdgeRule>();

    /// <summary>Records the reported (name-level) destination of an exit, from the exits verb.
    /// Dangling -- never binds an instance, so routing must still verify on arrival.</summary>
    internal void RecordReported(string room, string fex, string dir, string dest)
        => _reported[$"{room}|{fex}|{dir}"] = dest;

    /// <summary>Reported destination name for this exit (from the exits verb), or null.</summary>
    public string? ReportedDestination(string room, string fex, string dir)
        => _reported.TryGetValue($"{room}|{fex}|{dir}", out var d) ? d : null;

    // ── Stats / panel queries ───────────────────────────────────────────────────

    /// <summary>Current aggregate counts. Cheap O(rooms·exits) scan -- call on demand.</summary>
    public MapStats Snapshot()
    {
        int open = 0, closed = 0, edges = 0, dark = 0;
        foreach (var node in _nodes.Values)
        {
            edges += node.Neighbors.Count;
            dark  += node.DarkExits.Count;
            if (!node.Explored) continue;       // unexplored: counts toward neither open nor closed
            if (node.Closed) closed++; else open++;   // Closed implies Explored, so scan KnownExits once
        }
        return new MapStats(_nodes.Count, open, closed, edges, dark);
    }

    /// <summary>Directions from <paramref name="room"/> observed to lead into a dark
    /// (unseen) room -- the "needs a light source" exits for the room-data panel.</summary>
    public IReadOnlyCollection<string> DarkExitsFrom(string room)
        => _nodes.TryGetValue(room, out var n) ? n.DarkExits : Array.Empty<string>();

    // ── Guidance ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the direction the user should explore next from the given room, or null
    /// if everything reachable is already captured. Two-stage:
    ///   1. First uncaptured enabled exit in clockwise compass order (fast path).
    ///   2. BFS along resolved edges to the nearest room with uncaptured exits; return
    ///      the first-hop direction. Rooms never captured from are highest priority.
    /// enabledExits: the live FEX set for the current room (from MappingSession).
    /// resolvedDirs: the live resolved set for the current room (overrides stale graph).
    /// </summary>
    public string? SuggestedNextExit(
        string room,
        IReadOnlySet<string> enabledExits,
        IReadOnlySet<string> resolvedDirs)
    {
        // Stage 1: any uncaptured enabled exit here?
        foreach (var dir in MappingSession.Directions)
        {
            if (enabledExits.Contains(dir) && !resolvedDirs.Contains(dir))
                return dir;
        }

        // Stage 2: BFS to the nearest room that is open OR unexplored.
        return BfsFirstHop(room, resolvedDirs, n => IsOpenRoom(n) || IsFrontier(n));
    }

    /// <summary>First hop toward the nearest room that still "needs closing": an explored
    /// room with at least one uncaptured exit, preferred over never-visited frontier.
    /// Null when neither is reachable. The caller re-plans from where it actually lands
    /// after each hop -- a name-keyed path can thread the wrong same-name instance, so a
    /// precomputed route is never followed blindly (see MUD-Cartography close-room note).</summary>
    public string? FirstHopToClose(string room, IReadOnlySet<string> resolvedDirs)
        => BfsFirstHop(room, resolvedDirs, IsOpenRoom)
        ?? BfsFirstHop(room, resolvedDirs, IsFrontier);

    /// <summary>True when this exit (from the room with this exact fex) has been traversed
    /// to a room that is itself still open -- i.e. going that way leads to more work. False
    /// for unwalked exits (destination unknown) and exits into closed rooms.</summary>
    public bool ExitLeadsToOpenRoom(string room, string fex, string dir)
        => _nodes.TryGetValue(room, out var n)
        && n.NeighborsByKey.TryGetValue($"{fex}|{dir}", out var dest)
        && _nodes.TryGetValue(dest, out var dn)
        && IsOpenRoom(dn);

    // Open = explored (we know its exits) with at least one not yet captured.
    private static bool IsOpenRoom(RoomNode? n)
        => n is { } node && node.KnownExits.Count > 0 && node.KnownExits.Any(e => !node.ResolvedDirs.Contains(e));

    // Frontier = a room we have heard of by name but never stood in.
    private static bool IsFrontier(RoomNode? n)
        => n is null || n.KnownExits.Count == 0;

    // Guidance travel cap: BFS already prefers the NEAREST room with outstanding edges,
    // but past this many hops the suggestion costs more travel than it saves -- offer
    // nothing and let the user pick. TODO: smarter tour planning -- minimize total
    // travel time to close ALL outstanding edges/disambiguations (a routing problem,
    // not nearest-target), and weight pending-edge closure over frontier expansion.
    private const int MaxPlanningDepth = 10;

    /// <summary>First-hop direction toward the nearest room (within the guidance cap) for
    /// which <paramref name="isTarget"/> holds, BFS over resolved edges; null if none.
    /// The target predicate sees the destination's node (null when never captured from).</summary>
    private string? BfsFirstHop(string startRoom, IReadOnlySet<string> startResolved, Func<RoomNode?, bool> isTarget)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { startRoom };
        var queue   = new Queue<(string room, string firstHop, int depth)>();

        // Seed from the start room's resolved neighbors (live resolved set takes precedence).
        if (_nodes.TryGetValue(startRoom, out var startNode))
        {
            foreach (var (dir, neighbor) in startNode.Neighbors)
            {
                if (startResolved.Contains(dir) && !visited.Contains(neighbor))
                {
                    visited.Add(neighbor);
                    queue.Enqueue((neighbor, dir, 1));
                }
            }
        }

        while (queue.Count > 0)
        {
            var (room, firstHop, depth) = queue.Dequeue();
            var node = _nodes.TryGetValue(room, out var n) ? n : null;

            if (isTarget(node))
                return firstHop;

            if (depth >= MaxPlanningDepth || node is null) continue;
            foreach (var (_, neighbor) in node.Neighbors)
            {
                if (!visited.Contains(neighbor))
                {
                    visited.Add(neighbor);
                    queue.Enqueue((neighbor, firstHop, depth + 1));
                }
            }
        }

        return null;
    }
}
#endif
