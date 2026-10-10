using System.Globalization;
using Microsoft.Data.Sqlite;
using Mucka.Store;

namespace Mucka.Combat;

/// <summary>What one <see cref="MuckaIIImport.Run"/> did.</summary>
/// <param name="RunsImported">Source runs copied in this call.</param>
/// <param name="RunsAlreadyImported">Source runs skipped because an earlier import already holds them.</param>
/// <param name="RunsSkippedUnclosed">Source run ids with no <c>ended_ms</c> that were not named for
/// import: the process may still be alive, so they are neither copied nor replayed.</param>
/// <param name="WireRows">Wire records copied.</param>
/// <param name="Sessions">Logins reconstructed.</param>
/// <param name="ScoreEvents">Score lines recorded.</param>
/// <param name="ScoreEventsWithoutPersona">Score lines recorded in a login whose character was never
/// named. The score graph does not read a session without a persona, so these will not show.</param>
public sealed record MuckaIIImportResult(
    int RunsImported,
    int RunsAlreadyImported,
    IReadOnlyList<long> RunsSkippedUnclosed,
    long WireRows,
    int Sessions,
    int ScoreEvents,
    int ScoreEventsWithoutPersona);

/// <summary>
/// Imports finished runs from a MuckaII database (<c>~/.muckaii/muckaii.db</c>) so the score graph
/// and the session spans have no gap where that client was the one playing.
///
/// <para>A wire copy alone fills nothing: <c>score_events</c> is written by the live ledger, never
/// derived from <c>wire</c>. So each run is also replayed through the same parser that read it live,
/// by <see cref="PersonaSessionBackfill"/>'s login replay, and every score line is written with the
/// wire record's own instant and the login it happened in. Imported runs are excluded from
/// <see cref="PersonaSessionBackfill"/>'s time-window attribution, which ignores which run a row
/// came from.</para>
///
/// <para>Idempotent: an imported run carries <see cref="Tag"/> in <c>client_version</c>, and a source
/// run whose tag, start and host already exist here is skipped. The source's own run id is not the
/// key, because recreating that database restarts its ids. The source is opened read-only and never
/// modified. Swings, fights and encounters are not imported.</para>
///
/// <para>A run with no <c>ended_ms</c> is skipped unless its id is named in
/// <c>includeUnclosed</c>; the caller then asserts that process is dead. It is ended at its last wire
/// record and tagged <see cref="UnclosedMark"/>. Naming a live run freezes a partial copy, because the
/// run is never imported twice.</para>
///
/// <para>Threading: opens its own connections and must run OFF the UI thread (Invariant #1). The
/// replay runs before the destination transaction opens, so the write lock is held only for the
/// inserts.</para>
/// </summary>
public static class MuckaIIImport
{
    /// <summary>Prefix of <c>mucka_runs.client_version</c> on every imported run.</summary>
    public const string Tag = "MuckaII ";

    /// <summary>Suffix of <c>client_version</c> on a run whose end was taken from its last wire record.</summary>
    public const string UnclosedMark = " (unclosed)";

    /// <summary>MuckaII stores the host as typed in its profile; this client stores the .com server as
    /// <c>www.mud2.com</c>, and a character is keyed by host, so the two spellings would be two servers.</summary>
    internal static string MapHost(string host)
        => string.Equals(host, "mud2.com", StringComparison.OrdinalIgnoreCase) ? "www.mud2.com" : host;

    private readonly record struct SourceRun(long Id, long StartedMs, long? EndedMs, string Host, string Version);

    private readonly record struct WireRow(long Seq, long Ts, long Direction, byte[] Data);

    public static MuckaIIImportResult Run(string destinationPath, string sourcePath,
        IReadOnlySet<long>? includeUnclosed = null)
    {
        // Pooling off: a pooled read-only handle would keep the source file open after this returns
        // and block MuckaII's database being moved or recreated.
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        source.Open();
        using var destination = MuckaDb.Open(destinationPath);

        var skippedUnclosed = new List<long>();
        var alreadyImported = 0;
        var imported = 0;
        var wireRows = 0L;
        var sessions = 0;
        var scoreEvents = 0;
        var noPersona = 0;

        foreach (var run in ReadRuns(source))
        {
            var unclosed = run.EndedMs is null;
            if (unclosed && includeUnclosed?.Contains(run.Id) != true)
            {
                skippedUnclosed.Add(run.Id);
                continue;
            }

            var host = MapHost(run.Host);
            var wire = ReadWire(source, run.Id);
            var endedMs = run.EndedMs ?? (wire.Count > 0 ? wire.Max(w => w.Ts) : run.StartedMs);

            var scores = new List<PersonaSessionBackfill.ReplayedScore>();
            var logins = PersonaSessionBackfill.ReplayLogins(
                wire.Where(w => w.Direction == 0).Select(w => (w.Ts, w.Data)), scores);

            using var transaction = destination.BeginTransaction();
            // Inside the transaction, so two clients importing at once cannot both pass it.
            if (AlreadyImported(destination, transaction, run.StartedMs, host))
            {
                alreadyImported++;
                continue;
            }

            var version = Tag + run.Version + (unclosed ? UnclosedMark : string.Empty);
            Write(destination, transaction, run.StartedMs, endedMs, host, version, wire, logins, scores);
            transaction.Commit();

            imported++;
            wireRows += wire.Count;
            sessions += logins.Count;
            scoreEvents += scores.Count;
            noPersona += scores.Count(s => logins[s.LoginIndex].Persona is null);
        }

        return new MuckaIIImportResult(imported, alreadyImported, skippedUnclosed, wireRows, sessions,
            scoreEvents, noPersona);
    }

    private static List<SourceRun> ReadRuns(SqliteConnection source)
    {
        using var command = source.CreateCommand();
        command.CommandText =
            "SELECT id, started_ms, ended_ms, host, client_version FROM run ORDER BY started_ms, id;";
        var runs = new List<SourceRun>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            runs.Add(new SourceRun(reader.GetInt64(0), reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2), reader.GetString(3), reader.GetString(4)));
        return runs;
    }

    private static List<WireRow> ReadWire(SqliteConnection source, long runId)
    {
        using var command = source.CreateCommand();
        command.CommandText =
            "SELECT seq, ts_ms, direction, data FROM wire_log WHERE run = $run ORDER BY seq;";
        command.Parameters.AddWithValue("$run", runId);
        var rows = new List<WireRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            rows.Add(new WireRow(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), (byte[])reader[3]));
        return rows;
    }

    private static bool AlreadyImported(SqliteConnection destination, SqliteTransaction transaction,
        long startedMs, string host)
    {
        using var command = destination.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM mucka_runs
                          WHERE substr(client_version, 1, length($tag)) = $tag
                            AND started_ms = $started AND host = $host);
            """;
        command.Parameters.AddWithValue("$tag", Tag);
        command.Parameters.AddWithValue("$started", startedMs);
        command.Parameters.AddWithValue("$host", host);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    private static void Write(SqliteConnection destination, SqliteTransaction transaction, long startedMs,
        long endedMs, string host, string version, List<WireRow> wire,
        List<PersonaSessionBackfill.Login> logins, List<PersonaSessionBackfill.ReplayedScore> scores)
    {
        long runId;
        using (var command = destination.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO mucka_runs (started_ms, ended_ms, host, client_version)
                VALUES ($started, $ended, $host, $version);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$started", startedMs);
            command.Parameters.AddWithValue("$ended", endedMs);
            command.Parameters.AddWithValue("$host", host);
            command.Parameters.AddWithValue("$version", version);
            runId = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        using (var insert = destination.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO wire (mucka_run_id, seq, ts_ms, direction, data)
                VALUES ($run, $seq, $ts, $direction, $data);
                """;
            insert.Parameters.AddWithValue("$run", runId);
            var seq = insert.Parameters.Add("$seq", SqliteType.Integer);
            var ts = insert.Parameters.Add("$ts", SqliteType.Integer);
            var direction = insert.Parameters.Add("$direction", SqliteType.Integer);
            var data = insert.Parameters.Add("$data", SqliteType.Blob);
            foreach (var row in wire)
            {
                seq.Value = row.Seq;
                ts.Value = row.Ts;
                direction.Value = row.Direction;
                data.Value = row.Data;
                insert.ExecuteNonQuery();
            }
        }

        var sessionIds = new List<long>(logins.Count);
        foreach (var login in logins)
            sessionIds.Add(PersonaSessionBackfill.InsertSession(destination, transaction, runId, host, login));

        using (var command = destination.CreateCommand())
        {
            command.Transaction = transaction;
            // Stamped with the wire instant and the login it happened in. No encounter: those come from
            // the combat recorders, which are not replayed.
            command.CommandText = """
                INSERT INTO score_events (
                    ts, encounter_started_at_ms, encounter_open, persona_session_id, delta, total,
                    after_task_line, raw_text
                ) VALUES ($ts, NULL, 0, $psid, $delta, $total, $after_task_line, $raw_text);
                """;
            var ts = command.Parameters.Add("$ts", SqliteType.Integer);
            var psid = command.Parameters.Add("$psid", SqliteType.Integer);
            var delta = command.Parameters.Add("$delta", SqliteType.Integer);
            var total = command.Parameters.Add("$total", SqliteType.Integer);
            var afterTask = command.Parameters.Add("$after_task_line", SqliteType.Integer);
            var raw = command.Parameters.Add("$raw_text", SqliteType.Text);
            foreach (var score in scores)
            {
                ts.Value = score.Ms;
                psid.Value = sessionIds[score.LoginIndex];
                delta.Value = (object?)score.Save.Delta ?? DBNull.Value;
                total.Value = score.Save.Total;
                afterTask.Value = score.AfterTaskLine ? 1 : 0;
                raw.Value = score.Save.RawText;
                command.ExecuteNonQuery();
            }
        }
    }
}
