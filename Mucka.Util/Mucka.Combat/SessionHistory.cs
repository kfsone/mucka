using Mucka.Store;

namespace Mucka.Combat;

/// <summary>One row of History's Sessions list: one login of one persona (<c>persona_sessions</c>).
/// </summary>
/// <param name="Id"><c>persona_sessions.id</c>.</param>
/// <param name="StartedMs">Game mode entered, unix ms.</param>
/// <param name="EndedMs">Game mode left; null when the client died without saying.</param>
/// <param name="Host">The Session's server, else its run's; empty when neither names one.</param>
/// <param name="Persona">Null until the post-login score reply named the character.</param>
/// <param name="EndedNote"><c>persona_sessions.ended_note</c> as stored: advisory, and null says
/// nothing about how the Session ended.</param>
/// <param name="Points">What the score graph's steps inside this Session add up to
/// (<see cref="SessionPoints.Of"/>); null when the Session has no score reading.</param>
/// <param name="Title">The title the player gave it, or null.</param>
/// <param name="Tags">The player's tags, in <see cref="SessionLabels.Ordered"/> order.</param>
public sealed record SessionSummary(
    long Id,
    long StartedMs,
    long? EndedMs,
    string Host,
    string? Persona,
    string? EndedNote,
    long? Points,
    string? Title,
    IReadOnlyList<string> Tags)
{
    /// <summary>Null when the end was never recorded.</summary>
    public long? DurationMs => EndedMs - StartedMs;

    /// <summary>The title a Session has until the player gives it one, and the name a file made
    /// from it is offered: the character, then the local start time
    /// (<c>Derpster-20261112-2057</c>).</summary>
    public string DefaultTitle
        => $"{Persona ?? "session"}-"
           + DateTimeOffset.FromUnixTimeMilliseconds(StartedMs).ToLocalTime()
               .ToString("yyyyMMdd-HHmm", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The title, or null while it is the default: the list shows the server then.</summary>
    public string? ShownTitle => Title is { } title && title != DefaultTitle ? title : null;

    /// <summary>What to store for a typed title: null, which removes it, for a blank one or the
    /// default; otherwise the typed title, trimmed.</summary>
    public string? TitleToStore(string? typed)
        => typed?.Trim() is { Length: > 0 } title && title != DefaultTitle ? title : null;
}

/// <summary>
/// A Session's points, taken from the same readings the score graph draws (<see cref="ScoreHistory"/>,
/// a permadeath's step to zero included) so the two can never disagree: the last reading inside the
/// Session less the reading before it. A Session with no earlier reading of its character starts from
/// its own first reading. A gap between two Sessions - points made where nothing recorded them - is
/// counted in the Session whose first reading follows it, as the graph's step is.
/// </summary>
public static class SessionPoints
{
    /// <param name="series">One character's readings, in time order.</param>
    /// <param name="startMs">The Session's start.</param>
    /// <param name="endMs">The Session's end, inclusive: a permadeath's zero is stamped at it.</param>
    public static long? Of(IReadOnlyList<ScorePoint> series, long startMs, long endMs)
    {
        ScorePoint? before = null, first = null, last = null;
        foreach (var point in series)
        {
            if (point.Ms < startMs)
                before = point;
            else if (point.Ms <= endMs)
            {
                first ??= point;
                last = point;
            }
            else
                break;
        }
        if (last is not { } end)
            return null;
        return end.Total - (before ?? first ?? end).Total;
    }
}

/// <summary>Reads History's Sessions list, newest first. Synchronous; callers run it off the UI
/// thread. Reads no <c>wire</c> rows.</summary>
public static class SessionHistoryReader
{
    // The end the score graph gives a Session (ScoreHistoryReader's SessionsSql, with no current run):
    // its own, else its run's, else its last score reading, else its start.
    private const string SessionsSql = """
        SELECT ps.id, ps.started_ms, ps.ended_ms, COALESCE(ps.host, r.host, ''), ps.persona, ps.ended_note,
               COALESCE(ps.ended_ms, r.ended_ms,
                        (SELECT MAX(e.ts) FROM score_events e WHERE e.persona_session_id = ps.id),
                        ps.started_ms),
               n.name
        FROM persona_sessions ps
        JOIN mucka_runs r ON r.id = ps.mucka_run_id
        LEFT JOIN session_names n ON n.persona_session_id = ps.id
        ORDER BY ps.started_ms DESC, ps.id DESC;
        """;

    private static readonly string UnlabelledSessionsSql = SessionsSql
        .Replace("n.name", "NULL", StringComparison.Ordinal)
        .Replace("LEFT JOIN session_names n ON n.persona_session_id = ps.id", "", StringComparison.Ordinal);

    private const string TagsSql = "SELECT persona_session_id, tag FROM session_tags;";

    /// <param name="path">The database file.</param>
    /// <param name="nowMs">The instant to treat as now, unix ms.</param>
    public static IReadOnlyList<SessionSummary> Read(string path, long nowMs)
    {
        // No database yet is a player who has never connected.
        if (!File.Exists(path))
            return [];

        // No run of this process is live while History is open, hence -1: no mucka_runs.id.
        var score = ScoreHistoryReader.Read(path, 0, nowMs, -1);

        using var connection = MuckaDb.OpenRead(path);

        // A read-only open migrates nothing, and the launch can leave a file un-upgraded (its upgrade
        // is skipped while "nettimes" is on, and a failed one waits for the next game). Such a file has
        // no titles or tags yet.
        bool labelled;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('session_names', 'session_tags');";
            labelled = Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 2;
        }

        var tags = new Dictionary<long, List<string>>();
        if (labelled)
        {
            using var command = connection.CreateCommand();
            command.CommandText = TagsSql;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                if (!tags.TryGetValue(id, out var list))
                    tags[id] = list = [];
                list.Add(reader.GetString(1));
            }
        }

        var result = new List<SessionSummary>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = labelled ? SessionsSql : UnlabelledSessionsSql;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                var started = reader.GetInt64(1);
                var host = reader.GetString(3);
                var persona = reader.IsDBNull(4) ? null : reader.GetString(4);
                long? points = persona is not null
                               && score.Points.TryGetValue(new PersonaKey(host, persona), out var series)
                    ? SessionPoints.Of(series, started, reader.GetInt64(6))
                    : null;
                result.Add(new SessionSummary(
                    id,
                    started,
                    reader.IsDBNull(2) ? null : reader.GetInt64(2),
                    host,
                    persona,
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    points,
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    tags.TryGetValue(id, out var own) ? SessionLabels.Ordered(own) : []));
            }
        }
        return result;
    }
}
