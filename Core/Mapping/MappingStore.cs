using System.Text.Json;
using Microsoft.Maui.Storage;

namespace Mucka.Core.Mapping;

/// <summary>
/// Locates and inventories the mapping data directory. The directory on disk is the
/// source of truth: the client appends probe captures, external tooling (python via uv,
/// sub-agents) may add or rewrite derived files between reloads.
/// </summary>
public static class MappingStore
{
    internal sealed record EdgeAnnotation(
        string From,
        string Direction,
        string Outcome,
        string ExitFingerprint,
        bool IsTraversal,
        bool ResolvesEdge);

    internal sealed record EdgeState(HashSet<string> Resolved, MapGraph Graph);

    /// <summary>
    /// The mapping directory: "mappingdir" from mucka.ini ([settings:Profile] preferred,
    /// then [settings]), defaulting to ~/.mucka/mapping. The key is hand-edited only --
    /// the settings dialog never writes it, so IniFile preserves it like the [watch] rules.
    /// </summary>
    public static string ResolveDirectory(string profileName)
    {
        var ini = IniFile.Load(SettingsStore.ResolvePath());
        var configured = ini.Get($"settings:{profileName}", "mappingdir")
                      ?? ini.Get("settings", "mappingdir");
        if (!string.IsNullOrWhiteSpace(configured))
            return ExpandHome(configured.Trim());

        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mucka", "mapping");
        return Path.Combine(FileSystem.AppDataDirectory, "mapping");
    }

    private static string ExpandHome(string path)
        => path.StartsWith('~')
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                           path.TrimStart('~', '/', '\\'))
            : path;

    public sealed record Summary(int FileCount, long EntryCount, string? NewestFile);

    /// <summary>
    /// Reads a capture line by line while it may still be open for writing (the live
    /// walk file keeps its StreamWriter for the whole session -- FileShare.ReadWrite
    /// is required or the read fails with a sharing violation).
    /// </summary>
    public static IEnumerable<string> ReadLinesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
            yield return line;
    }

    /// <summary>Rescans the directory so data written by external tools shows up.</summary>
    public static Summary Reload(string directory)
    {
        if (!Directory.Exists(directory))
            return new Summary(0, 0, null);

        var files = Directory.GetFiles(directory, "*.jsonl");
        long entries = 0;
        string? newest = null;
        var newestTime = DateTime.MinValue;
        foreach (var file in files)
        {
            try { entries += ReadLinesShared(file).Count(); }
            catch { /* unreadable file still counts toward FileCount */ }
            var written = File.GetLastWriteTime(file);
            if (written > newestTime)
            {
                newestTime = written;
                newest = Path.GetFileName(file);
            }
        }
        return new Summary(files.Length, entries, newest);
    }

    internal static EdgeState LoadEdgeState(string directory)
    {
        var resolved = new HashSet<string>();
        var graph = MapGraph.CreateEmpty();

        foreach (var edge in ReadEdgeAnnotations(directory))
        {
            graph.RecordAnnotation(edge);
            if (edge.ResolvesEdge)
                resolved.Add($"{edge.From}|{edge.ExitFingerprint}|{edge.Direction}");
        }

        return new EdgeState(resolved, graph);
    }

    internal static IEnumerable<EdgeAnnotation> ReadEdgeAnnotations(string directory)
    {
        if (!Directory.Exists(directory))
            yield break;

        foreach (var file in Directory.GetFiles(directory, "*.jsonl"))
        {
            List<string> lines;
            try
            {
                lines = ReadLinesShared(file).ToList();
            }
            catch
            {
                continue;
            }

            foreach (var line in lines)
            {
                EdgeAnnotation? edge = null;
                try
                {
                    if (!line.Contains("\"edge: ", StringComparison.Ordinal)) continue;
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
                    if (doc.RootElement[1].GetString() != "an") continue;
                    var data = doc.RootElement[2].GetString();
                    if (data is null || !data.StartsWith("edge: ", StringComparison.Ordinal)) continue;
                    if (TryParseEdgeAnnotation(data, out edge) && edge is not null) { }
                }
                catch { /* partial or malformed line -- skip it, keep scanning */ }

                if (edge is not null)
                    yield return edge;
            }
        }
    }

    /// <summary>Parses "edge: {from} |{dir}>|! ... [{fingerprint}]" into a structured
    /// annotation. Legacy lines without the trailing fingerprint get an empty fingerprint
    /// component.</summary>
    internal static bool TryParseEdgeAnnotation(string data, out EdgeAnnotation? edge)
    {
        var bar = data.IndexOf(" |", StringComparison.Ordinal);
        if (bar < 6) { edge = null; return false; }
        var end = data.IndexOfAny(['>', '!'], bar + 2);
        if (end < 0) { edge = null; return false; }
        var from = data[6..bar];
        var dir = data[(bar + 2)..end];

        var fex = string.Empty;
        var tail = data;
        if (data.EndsWith(']') && data.LastIndexOf(" [", StringComparison.Ordinal) is var open and >= 0)
        {
            fex = data[(open + 2)..^1];
            tail = data[..open];
        }

        var outcome = tail[(end + 1)..].Trim();
        var isTraversal = data[end] == '>';
        var resolvesEdge = true;
        if (data[end] == '!')
        {
            // Dark-text refusals are legacy records from before the parser knew the
            // period variant of the too-dark line; like (dark) arrivals they mean the
            // far end is unidentified.
            if (outcome is "(timeout)" or "(no output)" || IsTransientRefusal(outcome)
                || outcome.StartsWith("It's too dark to see", StringComparison.Ordinal))
                resolvesEdge = false;
        }
        else if (outcome == "(dark)")
        {
            // Traversed but unseen -- stays wanted until re-walked with a light source.
            resolvesEdge = false;
        }

        edge = new EdgeAnnotation(from, dir, outcome, fex, isTraversal, resolvesEdge);
        return true;
    }

    /// <summary>Refusals caused by something movable (an ox, a player) -- recorded as
    /// data, but the edge stays uncaptured so the console offers it again.</summary>
    public static bool IsTransientRefusal(string reason)
        => reason.Contains("blocked by", StringComparison.OrdinalIgnoreCase);
}
