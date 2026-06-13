#if WINDOWS
namespace Mucka.Core.Mapping;

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
        // Resolved neighbors: dir → destination room name.
        public Dictionary<string, string> Neighbors { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly Dictionary<string, RoomNode> _nodes;

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
            if (!edge.ResolvesEdge) return;   // far end unidentified -- stays wanted
            fromNode.ResolvedDirs.Add(edge.Direction);
            fromNode.Neighbors[edge.Direction] = edge.Outcome;
        }
        else
        {
            if (!edge.ResolvesEdge)
                return;
            fromNode.ResolvedDirs.Add(edge.Direction);
        }
    }

    // ── Queries / live updates ─────────────────────────────────────────────────

    /// <summary>Name-level destination evidence: where dir from room has been seen to
    /// lead (any walk, any fingerprint/door state), or null when never traversed.
    /// Same-name collisions apply -- this is "seems to lead to", not proof.</summary>
    public string? KnownDestination(string room, string dir)
        => _nodes.TryGetValue(room, out var n) && n.Neighbors.TryGetValue(dir, out var dest)
            ? dest : null;

    /// <summary>Records a live traversal so decisions made this session see edges
    /// captured this session (the disk rescan only happens on reload).</summary>
    public void RecordTraversal(string from, string dir, string to)
    {
        if (!_nodes.TryGetValue(from, out var node))
            _nodes[from] = node = new RoomNode();
        node.ResolvedDirs.Add(dir);
        node.Neighbors[dir] = to;
    }

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

        // Stage 2: BFS to nearest room that has uncaptured exits.
        return BfsFirstHop(room, resolvedDirs);
    }

    // Guidance travel cap: BFS already prefers the NEAREST room with outstanding edges,
    // but past this many hops the suggestion costs more travel than it saves -- offer
    // nothing and let the user pick. TODO: smarter tour planning -- minimize total
    // travel time to close ALL outstanding edges/disambiguations (a routing problem,
    // not nearest-target), and weight pending-edge closure over frontier expansion.
    private const int MaxGuidanceHops = 10;

    private string? BfsFirstHop(string startRoom, IReadOnlySet<string> startResolved)
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

            if (!_nodes.TryGetValue(room, out var node) || node.KnownExits.Count == 0)
                return firstHop;   // never captured from here -- go explore it

            if (node.KnownExits.Any(e => !node.ResolvedDirs.Contains(e)))
                return firstHop;   // has at least one uncaptured exit

            if (depth >= MaxGuidanceHops) continue;
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
