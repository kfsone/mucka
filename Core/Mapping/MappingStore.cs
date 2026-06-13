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

    /// <summary>
    /// Rebuilds the set of already-captured edges from the edge annotations in every
    /// capture: "edge: {from} |{dir}> {to} [{exit fingerprint}]" (or |{dir}! refusal --
    /// both outcomes count as resolved; a recorded refusal is data we do not need to
    /// re-collect). Keys are "{room}|{fingerprint}|{dir}": same-named rooms are only
    /// treated as the same place when their enabled-exit sets also match, since short
    /// descriptions are not unique (five "Badly-paved road"s). Same name AND same
    /// fingerprint still collide -- true instance identity is the analysis pipeline's
    /// job; the console only errs toward re-capturing.
    /// </summary>
    public static HashSet<string> ScanResolvedEdges(string directory)
    {
        var resolved = new HashSet<string>();
        if (!Directory.Exists(directory))
            return resolved;

        foreach (var file in Directory.GetFiles(directory, "*.jsonl"))
        {
            try
            {
                foreach (var line in ReadLinesShared(file))
                {
                    try
                    {
                        // Cheap pre-filter; full parse only for candidate annotation lines.
                        if (!line.Contains("\"edge: ", StringComparison.Ordinal)) continue;
                        using var doc = JsonDocument.Parse(line);
                        if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
                        if (doc.RootElement[1].GetString() != "an") continue;
                        var data = doc.RootElement[2].GetString();
                        if (data is null || !data.StartsWith("edge: ", StringComparison.Ordinal)) continue;
                        if (ParseEdgeAnnotation(data) is { } key)
                            resolved.Add(key);
                    }
                    catch { /* partial or malformed line -- skip it, keep scanning */ }
                }
            }
            catch { /* unreadable or foreign file -- contributes nothing */ }
        }
        return resolved;
    }

    /// <summary>Parses "edge: {from} |{dir}>|! ... [{fingerprint}]" into a resolved-edge
    /// key, or null when the line does not parse OR does not resolve the edge: transient
    /// refusals (something movable in the way) and op artifacts (timeout, no output)
    /// are recorded data but the edge is still wanted. Legacy lines without the trailing
    /// fingerprint get an empty fingerprint component.</summary>
    internal static string? ParseEdgeAnnotation(string data)
    {
        var bar = data.IndexOf(" |", StringComparison.Ordinal);
        if (bar < 6) return null;
        var end = data.IndexOfAny(['>', '!'], bar + 2);
        if (end < 0) return null;
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
        if (data[end] == '!')
        {
            // Dark-text refusals are legacy records from before the parser knew the
            // period variant of the too-dark line; like (dark) arrivals they mean the
            // far end is unidentified.
            if (outcome is "(timeout)" or "(no output)" || IsTransientRefusal(outcome)
                || outcome.StartsWith("It's too dark to see", StringComparison.Ordinal))
                return null;
        }
        else if (outcome == "(dark)")
        {
            // Traversed but unseen -- stays wanted until re-walked with a light source.
            return null;
        }

        return $"{from}|{fex}|{dir}";
    }

    /// <summary>Refusals caused by something movable (an ox, a player) -- recorded as
    /// data, but the edge stays uncaptured so the console offers it again.</summary>
    public static bool IsTransientRefusal(string reason)
        => reason.Contains("blocked by", StringComparison.OrdinalIgnoreCase);
}
