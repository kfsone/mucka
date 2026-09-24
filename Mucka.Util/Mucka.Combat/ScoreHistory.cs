using Microsoft.Data.Sqlite;
using Mucka.Store;

namespace Mucka.Combat;

/// <summary>One "Persona saved on ... = total" reading: when, and what the game said the score IS.</summary>
public readonly record struct ScorePoint(long Ms, long Total);

/// <summary>A span of time on the time axis - a persona session or a Mucka run.</summary>
public readonly record struct TimeSpanMs(long StartMs, long EndMs);

/// <summary>A persona is only one persona on one server: the same name on two MUD2s is two
/// characters. Hosts compare case-insensitively; persona names as the game prints them.</summary>
public readonly record struct PersonaKey(string Host, string Persona)
{
    public bool Equals(PersonaKey other)
        => string.Equals(Host, other.Host, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Persona, other.Persona, StringComparison.Ordinal);

    public override int GetHashCode()
        => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(Host), StringComparer.Ordinal.GetHashCode(Persona));
}

/// <summary>
/// Everything the score graph draws, read in one pass so every control the panel offers - server,
/// character, days, scale, squeeze, zero - is answered from memory without going back to the database.
///
/// <para>Points come from <c>score_events.total</c>, the game's own figure, and never from summing
/// <c>delta</c>: a step is the difference between consecutive totals. Rows without a persona session
/// cannot be placed on a character and are not read.</para>
/// </summary>
public sealed class ScoreHistory
{
    public static readonly ScoreHistory Empty = new(0, new Dictionary<PersonaKey, ScorePoint[]>(),
        new Dictionary<PersonaKey, TimeSpanMs[]>(), []);

    public ScoreHistory(long nowMs,
        IReadOnlyDictionary<PersonaKey, ScorePoint[]> points,
        IReadOnlyDictionary<PersonaKey, TimeSpanMs[]> personaSessions,
        IReadOnlyList<(string Host, TimeSpanMs Span)> runs,
        IReadOnlyDictionary<PersonaKey, long[]>? permadeaths = null)
    {
        NowMs = nowMs;
        Points = points;
        PersonaSessions = personaSessions;
        Runs = runs;
        Permadeaths = permadeaths ?? new Dictionary<PersonaKey, long[]>();
    }

    /// <summary>Per character, when its persona was wiped: the end of each persona session whose
    /// <c>ended_note</c> is <see cref="PersonaSessionEnd.Permadeath"/>. That note comes from the
    /// server's own C08+C13, and like every note it is only good in the positive direction - its
    /// absence is not evidence of no wipe.</summary>
    public IReadOnlyDictionary<PersonaKey, long[]> Permadeaths { get; }

    /// <summary>The instant the read was made; the right-hand end of every window.</summary>
    public long NowMs { get; }
    /// <summary>Per character, in time order.</summary>
    public IReadOnlyDictionary<PersonaKey, ScorePoint[]> Points { get; }
    /// <summary>Per character, in start order.</summary>
    public IReadOnlyDictionary<PersonaKey, TimeSpanMs[]> PersonaSessions { get; }
    /// <summary>Mucka runs, in start order, each with the server it connected to.</summary>
    public IReadOnlyList<(string Host, TimeSpanMs Span)> Runs { get; }

    /// <summary>Where one of <paramref name="selected"/>'s persona sessions began and the persona
    /// session before it, among every character on <paramref name="hosts"/>, was a different
    /// character. The first session read has nothing before it and is not a switch.</summary>
    public List<(PersonaKey Persona, TimeSpanMs Session, PersonaKey From)> Switches(IReadOnlySet<string> hosts, IReadOnlySet<PersonaKey> selected)
    {
        var ordered = PersonaSessions
            .Where(kv => hosts.Contains(kv.Key.Host))
            .SelectMany(kv => kv.Value.Select(span => (kv.Key, span)))
            .OrderBy(x => x.span.StartMs)
            .ToList();
        var result = new List<(PersonaKey, TimeSpanMs, PersonaKey)>();
        for (var i = 1; i < ordered.Count; i++)
            if (!ordered[i].Key.Equals(ordered[i - 1].Key) && selected.Contains(ordered[i].Key))
                result.Add((ordered[i].Key, ordered[i].span, ordered[i - 1].Key));
        return result;
    }

    /// <summary>Every server a character or a run has been seen on, sorted.</summary>
    public IReadOnlyList<string> Hosts()
        => Points.Keys.Select(k => k.Host)
            .Concat(PersonaSessions.Keys.Select(k => k.Host))
            .Concat(Runs.Select(r => r.Host))
            .Where(h => h.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
}

/// <summary>Reads a <see cref="ScoreHistory"/> from <c>~/.mucka/mucka.db</c>. Synchronous; callers run
/// it off the UI thread.</summary>
public static class ScoreHistoryReader
{
    // persona_sessions.host can be NULL on existing databases. A persona session belongs to one run,
    // and the run's host names the same server, so it stands in.
    private const string PointsSql = """
        SELECT COALESCE(ps.host, r.host, ''), ps.persona, e.ts, e.total
        FROM score_events e
        JOIN persona_sessions ps ON ps.id = e.persona_session_id
        JOIN mucka_runs r        ON r.id  = ps.mucka_run_id
        WHERE e.ts >= $since AND ps.persona IS NOT NULL
        ORDER BY e.ts, e.id;
        """;

    // An open end is either the session still in progress (its run is this one) or a client that
    // died without closing it. The run's end, then the session's last score reading, bound the
    // second; a session with neither is drawn as an instant.
    private const string SessionsSql = """
        SELECT COALESCE(ps.host, r.host, ''), ps.persona, ps.started_ms,
               COALESCE(ps.ended_ms,
                        CASE WHEN r.id = $current THEN $now END,
                        r.ended_ms,
                        (SELECT MAX(e.ts) FROM score_events e WHERE e.persona_session_id = ps.id),
                        ps.started_ms) AS end_ms,
               ps.ended_note
        FROM persona_sessions ps
        JOIN mucka_runs r ON r.id = ps.mucka_run_id
        WHERE ps.persona IS NOT NULL AND end_ms >= $since
        ORDER BY ps.started_ms;
        """;

    private const string RunsSql = """
        SELECT COALESCE(host, ''), started_ms,
               COALESCE(ended_ms, CASE WHEN id = $current THEN $now END, started_ms) AS end_ms
        FROM mucka_runs
        WHERE end_ms >= $since
        ORDER BY started_ms;
        """;

    /// <param name="path">The database file.</param>
    /// <param name="sinceMs">Oldest instant to read, unix ms.</param>
    /// <param name="nowMs">The instant to treat as now, unix ms.</param>
    /// <param name="currentRunId">This process's <c>mucka_runs.id</c>, whose open spans end at
    /// <paramref name="nowMs"/>; any other open span belonged to a client that exited without closing
    /// it.</param>
    public static ScoreHistory Read(string path, long sinceMs, long nowMs, long currentRunId)
    {
        using var connection = MuckaDb.OpenRead(path);

        var points = new Dictionary<PersonaKey, List<ScorePoint>>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = PointsSql;
            command.Parameters.AddWithValue("$since", sinceMs);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                Add(points, new PersonaKey(reader.GetString(0), reader.GetString(1)),
                    new ScorePoint(reader.GetInt64(2), reader.GetInt64(3)));
        }

        var sessions = new Dictionary<PersonaKey, List<TimeSpanMs>>();
        var wipes = new Dictionary<PersonaKey, List<long>>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = SessionsSql;
            Bind(command, sinceMs, nowMs, currentRunId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var key = new PersonaKey(reader.GetString(0), reader.GetString(1));
                var span = new TimeSpanMs(reader.GetInt64(2), reader.GetInt64(3));
                Add(sessions, key, span);
                if (!reader.IsDBNull(4) && reader.GetString(4) == PersonaSessionEnd.Permadeath)
                    Add(wipes, key, span.EndMs);
            }
        }

        var runs = new List<(string, TimeSpanMs)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = RunsSql;
            Bind(command, sinceMs, nowMs, currentRunId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                runs.Add((reader.GetString(0), new TimeSpanMs(reader.GetInt64(1), reader.GetInt64(2))));
        }

        return new ScoreHistory(nowMs,
            points.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()),
            sessions.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()),
            runs,
            wipes.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()));
    }

    private static void Bind(SqliteCommand command, long sinceMs, long nowMs, long currentRunId)
    {
        command.Parameters.AddWithValue("$since", sinceMs);
        command.Parameters.AddWithValue("$now", nowMs);
        command.Parameters.AddWithValue("$current", currentRunId);
    }

    private static void Add<T>(Dictionary<PersonaKey, List<T>> into, PersonaKey key, T value)
    {
        if (!into.TryGetValue(key, out var list))
            into[key] = list = [];
        list.Add(value);
    }
}
