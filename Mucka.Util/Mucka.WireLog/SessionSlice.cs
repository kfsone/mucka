using Mucka.Store;

namespace Mucka.WireLog;

/// <summary>
/// One Session's recording: the stretch of its run's <c>wire</c> rows that belongs to it. The wire is
/// keyed by run, and a run holds 0..n Sessions (logins), so a Session's recording is a slice of the run.
///
/// <para>The rule (<see cref="Bounds"/>): a Session's slice is the run's rows after the previous
/// Session's end, up to and including its own end. The first Session's slice starts at the run's first
/// row, so the login and the character menu leading into it are its; the last one's runs to the run's
/// last row, so the logout tail is its. A Session with no <c>ended_ms</c> ends where the next one
/// starts. The slices partition the run: no row is in two, and none is in none.</para>
///
/// <para><see cref="Run"/> is the whole run, not the slice, because the login is masked over the whole
/// run before the slice is cut: a masked context can span a slice's edge
/// (<see cref="Muckrep.FromSession"/>).</para>
/// </summary>
/// <param name="RunId"><c>mucka_runs.id</c>.</param>
/// <param name="Run">Every record of the run, in order.</param>
/// <param name="First">The Session's first record in <see cref="Run"/>.</param>
/// <param name="Last">Its last, inclusive; less than <see cref="First"/> when the slice is empty.</param>
public sealed record SessionSlice(long RunId, IReadOnlyList<WireRecord> Run, int First, int Last)
{
    /// <summary>True when the Session has no rows left.</summary>
    public bool IsEmpty => Last < First;

    /// <summary>The Session as a replay (<see cref="Muckrep.FromSession"/>): the login masked over
    /// the whole run, then the slice cut.</summary>
    public List<WireRecord> ToReplay() => Muckrep.FromSession(Run, First, Last);

    /// <summary>The Session's slice of <paramref name="dbPath"/>, or null when there is no such
    /// Session. Reads the whole run: off the UI thread.</summary>
    public static SessionSlice? Read(string dbPath, long personaSessionId)
    {
        long runId;
        var inRun = new List<(long Id, long StartedMs, long? EndedMs)>();
        using (var connection = MuckaDb.OpenRead(dbPath))
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT mucka_run_id FROM persona_sessions WHERE id = $id;";
                command.Parameters.AddWithValue("$id", personaSessionId);
                if (command.ExecuteScalar() is not long id)
                    return null;
                runId = id;
            }
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT id, started_ms, ended_ms FROM persona_sessions
                    WHERE mucka_run_id = $run ORDER BY started_ms, id;
                    """;
                command.Parameters.AddWithValue("$run", runId);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    inRun.Add((reader.GetInt64(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetInt64(2)));
            }
        }

        var run = WireLogExport.ReadSession(dbPath, runId).ToList();
        var (after, through) = Bounds(inRun, personaSessionId);
        var (first, last) = Range(run, after, through);
        return new SessionSlice(runId, run, first, last);
    }

    /// <summary>
    /// The slice of Session <paramref name="id"/> among <paramref name="inRun"/> (its run's Sessions,
    /// ordered by start), as times: rows stamped after <c>AfterMs</c> and up to and including
    /// <c>ThroughMs</c>. Null means unbounded: the first Session has no <c>AfterMs</c>, the last no
    /// <c>ThroughMs</c>.
    ///
    /// <para>A boundary never moves backwards: one earlier than the boundary before it is taken as that
    /// boundary, so the slices still partition the run when clocks or records disagree.</para>
    /// </summary>
    public static (long? AfterMs, long? ThroughMs) Bounds(
        IReadOnlyList<(long Id, long StartedMs, long? EndedMs)> inRun, long id)
    {
        long? previous = null;
        for (var k = 0; k < inRun.Count; k++)
        {
            long? boundary = k == inRun.Count - 1
                ? null
                : inRun[k].EndedMs ?? inRun[k + 1].StartedMs;
            if (boundary is { } b && previous is { } p && b < p)
                boundary = p;
            if (inRun[k].Id == id)
                return (previous, boundary);
            previous = boundary;
        }
        throw new ArgumentException($"Session {id} is not in the run.", nameof(id));
    }

    /// <summary>The first and last indexes of <paramref name="run"/> inside the times
    /// <see cref="Bounds"/> gave; last is less than first when none are.
    ///
    /// <para>A boundary cuts the run at its first record stamped later than it (<see cref="Cut"/>), and
    /// a slice runs from one cut to the next. The cut moves forward with the boundary, so the slices
    /// partition the run even where a stamp steps backwards.</para></summary>
    public static (int First, int Last) Range(IReadOnlyList<WireRecord> run, long? afterMs, long? throughMs)
        => (afterMs is { } after ? Cut(run, after) : 0,
            (throughMs is { } through ? Cut(run, through) : run.Count) - 1);

    /// <summary>The index of the first record stamped later than <paramref name="boundaryMs"/>, or
    /// the run's length when none is.</summary>
    private static int Cut(IReadOnlyList<WireRecord> run, long boundaryMs)
    {
        var i = 0;
        while (i < run.Count && run[i].TimestampMs <= boundaryMs)
            i++;
        return i;
    }
}
