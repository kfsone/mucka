#if WINDOWS
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mucka.Core.Mapping;

/// <summary>The "when" half of a travel-table row: an observable predicate over game state.
/// Kinds: "carrying" (an item in the FEI carried inventory), "door" (a door in the room's
/// long description, in some state), "weather", "count" (deferred), and "else" (the default
/// applied when no other guard for this direction matched). Human-authored only -- the
/// console never decides an edge is conditional; that is a game puzzle (see spirit rule).</summary>
public sealed record RuleGuard(
    string Kind,
    string? Item = null,     // carrying: the concrete observed token, e.g. "coracle"
    bool Negate = false,     // carrying: true for "!carrying <item>"
    string? Class = null,    // carrying: optional free human tag ("boat") -- never resolved, no synonym db
    string? Ref = null,      // door: discriminator ("kitchen", "white", "north"); null = the room's sole door
    string? State = null)    // door: open|closed|locked|absent ; weather: rain|...
{
    public string Describe() => Kind switch
    {
        "carrying" => (Negate ? "!carrying " : "carrying ") + (Item ?? "?")
                      + (string.IsNullOrEmpty(Class) ? string.Empty : $" ({Class})"),
        "door"     => $"door({Ref ?? "-"}) {State ?? "?"}",
        "weather"  => $"weather {State ?? "?"}",
        "else"     => "else",
        _          => Kind,
    };
}

/// <summary>The "then" half of a travel-table row. "arrive" traverses to Dest (which may
/// differ by guard -- a conditional/forked destination); "refuse" blocks with a fixed
/// message and no transit; "absent" means the exit is not offered at all under this guard
/// (it vanishes from the fex, as if there were no edge).</summary>
public sealed record RuleOutcome(
    string Kind,
    string? Dest = null,     // arrive: destination room name
    string? Text = null)     // refuse: the fixed message
{
    public string Describe() => Kind switch
    {
        "arrive" => $"-> arrive {Dest ?? "?"}",
        "refuse" => $"-> refuse \"{Text ?? string.Empty}\"",
        "absent" => "-> absent",
        _        => "-> " + Kind,
    };
}

/// <summary>One hand-authored decision-table row for an edge: (from-room + fex + dir) with a
/// guard -> outcome, plus the raw evidence observed when it was marked (never interpreted) and
/// an optional note. Persisted as a {"extra":"edge-rule"} walk-file record and re-read on load.
/// See MUD-Mapping-Design.md section 4.2.</summary>
public sealed record EdgeRule(
    string From,
    string Fex,
    string Dir,
    RuleGuard Guard,
    RuleOutcome Outcome,
    IReadOnlyList<string>? EvidenceFei,
    string? Note,
    long Ts)
{
    /// <summary>Edge identity, matching MappingSession.EdgeKey.</summary>
    public string EdgeKey => $"{From}|{Fex}|{Dir}";
}

/// <summary>(De)serialization for edge-rule walk-file records. One JSON object per line:
/// {"extra":"edge-rule","edge":{from,fex,dir},"guard":{...},"outcome":{...},
///  "evidence":{"fei":[...]},"note":...,"ts":...}</summary>
public static class EdgeRules
{
    private static readonly JsonSerializerOptions Opts =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static string Serialize(EdgeRule r)
    {
        var payload = new
        {
            extra = "edge-rule",
            edge = new { from = r.From, fex = r.Fex, dir = r.Dir },
            guard = new
            {
                kind   = r.Guard.Kind,
                item   = r.Guard.Item,
                negate = r.Guard.Negate ? true : (bool?)null,   // omit when false
                @class = r.Guard.Class,
                @ref   = r.Guard.Ref,
                state  = r.Guard.State,
            },
            outcome = new { kind = r.Outcome.Kind, dest = r.Outcome.Dest, text = r.Outcome.Text },
            evidence = r.EvidenceFei is null ? null : new { fei = r.EvidenceFei },
            note = r.Note,
            ts = r.Ts,
        };
        return JsonSerializer.Serialize(payload, Opts);
    }

    public static bool TryParse(string line, out EdgeRule? rule)
    {
        rule = null;
        if (!line.Contains("\"edge-rule\"", StringComparison.Ordinal)) return false;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("extra", out var ex) || ex.GetString() != "edge-rule") return false;
            if (!root.TryGetProperty("edge", out var edge)) return false;
            var from = Str(edge, "from");
            var fex  = Str(edge, "fex");
            var dir  = Str(edge, "dir");
            if (from is null || fex is null || dir is null) return false;

            var guard = root.TryGetProperty("guard", out var g)
                ? new RuleGuard(Str(g, "kind") ?? "else", Str(g, "item"), Bool(g, "negate"),
                                Str(g, "class"), Str(g, "ref"), Str(g, "state"))
                : new RuleGuard("else");
            var outcome = root.TryGetProperty("outcome", out var o)
                ? new RuleOutcome(Str(o, "kind") ?? "absent", Str(o, "dest"), Str(o, "text"))
                : new RuleOutcome("absent");

            IReadOnlyList<string>? fei = null;
            if (root.TryGetProperty("evidence", out var evd) && evd.ValueKind == JsonValueKind.Object
                && evd.TryGetProperty("fei", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var it in arr.EnumerateArray())
                    if (it.GetString() is { } s) list.Add(s);
                fei = list;
            }

            var note = Str(root, "note");
            long ts = root.TryGetProperty("ts", out var tsEl) && tsEl.TryGetInt64(out var t) ? t : 0;

            rule = new EdgeRule(from, fex, dir, guard, outcome, fei, note, ts);
            return true;
        }
        catch { return false; }
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
#endif
