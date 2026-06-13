#if WINDOWS
using System.Text.Json;

namespace Mucka.Core.Mapping;

/// <summary>
/// In-memory directed multigraph of captured edges, loaded from walk files.
/// Used for two things: static reciprocal-direction lookup (no load needed) and
/// BFS guidance (find the next uncaptured exit, or the first hop toward the nearest
/// room that has one). Loaded from disk in MappingPage.Reload() -- guidance may be
/// one session behind live ops, which is acceptable.
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

    public static MapGraph Load(string directory)
    {
        var nodes = new Dictionary<string, RoomNode>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(directory))
            return new MapGraph(nodes);

        RoomNode GetOrAdd(string room) =>
            nodes.TryGetValue(room, out var n) ? n : (nodes[room] = new RoomNode());

        foreach (var file in Directory.GetFiles(directory, "*.jsonl"))
        {
            try
            {
                foreach (var line in MappingStore.ReadLinesShared(file))
                {
                    try
                    {
                        if (!line.Contains("\"edge: ", StringComparison.Ordinal)) continue;
                        using var doc = JsonDocument.Parse(line);
                        if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
                        if (doc.RootElement[1].GetString() != "an") continue;
                        var data = doc.RootElement[2].GetString();
                        if (data is null || !data.StartsWith("edge: ", StringComparison.Ordinal)) continue;
                        ParseEdgeLine(data, GetOrAdd);
                    }
                    catch { }
                }
            }
            catch { }
        }

        return new MapGraph(nodes);
    }

    private static void ParseEdgeLine(string data, Func<string, RoomNode> getOrAdd)
    {
        // Format: "edge: {from} |{dir}> {to} [{fex}]"  or  "edge: {from} |{dir}! {reason} [{fex}]"
        var bar = data.IndexOf(" |", StringComparison.Ordinal);
        if (bar < 6) return;
        var end = data.IndexOfAny(['>', '!'], bar + 2);
        if (end < 0) return;

        var from = data[6..bar];
        var dir  = data[(bar + 2)..end];

        var tail = data;
        var fex  = string.Empty;
        if (data.EndsWith(']') && data.LastIndexOf(" [", StringComparison.Ordinal) is var open and >= 0)
        {
            fex  = data[(open + 2)..^1];
            tail = data[..open];
        }

        var outcome = tail[(end + 1)..].Trim();
        var fromNode = getOrAdd(from);

        foreach (var exit in fex.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            fromNode.KnownExits.Add(exit);

        if (data[end] == '>')
        {
            if (outcome == "(dark)") return;   // far end unidentified -- stays wanted
            fromNode.ResolvedDirs.Add(dir);
            fromNode.Neighbors[dir] = outcome;
        }
        else
        {
            // Refusal: structural ones resolve; transient / artifacts do not.
            if (outcome is "(timeout)" or "(no output)"
                || MappingStore.IsTransientRefusal(outcome)
                || outcome.StartsWith("It's too dark to see", StringComparison.Ordinal))
                return;
            fromNode.ResolvedDirs.Add(dir);
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
