using Microsoft.Data.Sqlite;

namespace Mucka.Store;

/// <summary>
/// When each persona was last logged in to a given MUD2, read from <c>persona_sessions</c>, and the
/// ordering of a persona list by it. The picker offers the shell's slot order; this puts the
/// character the operator most recently played first.
/// </summary>
public static class PersonaRecency
{
    // Persona is null until the post-login score reply names the character, so an unnamed row is not
    // evidence of who logged in. Host is matched the way ScoreHistory keys it: the session's own, then
    // its run's.
    private const string LastStartedSql = """
        SELECT ps.persona, MAX(ps.started_ms)
        FROM persona_sessions ps
        JOIN mucka_runs r ON r.id = ps.mucka_run_id
        WHERE ps.persona IS NOT NULL AND COALESCE(ps.host, r.host, '') = $host
        GROUP BY ps.persona;
        """;

    /// <summary>Last login start per persona name (case-insensitive) on <paramref name="host"/>, unix
    /// ms. Empty when the file does not exist or cannot be read: ordering is a convenience and must
    /// never stand between the operator and the picker.</summary>
    public static IReadOnlyDictionary<string, long> LastStarted(string path, string host)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(path)) return result;
            using var connection = MuckaDb.OpenRead(path);
            using var command = connection.CreateCommand();
            command.CommandText = LastStartedSql;
            command.Parameters.AddWithValue("$host", host);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                result[reader.GetString(0)] = reader.GetInt64(1);
        }
        catch (SqliteException) { }
        catch (IOException) { }
        return result;
    }

    /// <summary>Most recently logged-in first; names with no recorded login keep their given order
    /// after every name that has one.</summary>
    public static List<T> OrderMostRecentFirst<T>(IEnumerable<T> items, Func<T, string?> name,
        IReadOnlyDictionary<string, long> lastStarted)
        => items
            .Select((item, index) =>
                (item, index, ms: name(item) is string n && lastStarted.TryGetValue(n, out var v) ? v : long.MinValue))
            .OrderByDescending(x => x.ms)
            .ThenBy(x => x.index)
            .Select(x => x.item)
            .ToList();
}
