using System.Globalization;
using Microsoft.Data.Sqlite;
using Mucka.Store;
using Mucka.WireLog;

namespace Mucka.Combat;

/// <summary>
/// A Session's recording in the store: which <c>wire</c> rows its slice holds (<see cref="SessionSlice"/>'s
/// rule, worked out from row ids without reading the bytes), whether it holds any, and deleting it.
///
/// <para><b>The same slice as <see cref="SessionSlice"/>.</b> A boundary cuts the run at its first row,
/// in id order, stamped later than it (<see cref="SessionSlice.Range"/>), which is the run's lowest id
/// with a later stamp. So a slice is the run's ids from the cut at its start up to the cut at its end,
/// and is empty when there is no row at or after the first cut, or the two cuts are the same row.</para>
///
/// <para><b>Delete removes the recording only</b> (operator): the slice's <c>wire</c> rows, the
/// <c>trimmed</c> rows taken out of it, and its title and tags. <c>persona_sessions</c> and every fact
/// table stay, so the score graph and Combat Insights keep its points and fights. A Session whose slice
/// is empty is not listed (<see cref="Recorded"/>): that is derived, not a flag.</para>
/// </summary>
public static class SessionRecording
{
    /// <summary>A slice as ids: the run's rows with ids from <paramref name="First"/> up to but not
    /// including <paramref name="End"/>.</summary>
    public readonly record struct IdRange(long First, long End)
    {
        /// <summary>True when the slice holds no row.</summary>
        public bool IsEmpty => End <= First;

        /// <summary>No rows.</summary>
        public static readonly IdRange None = new(0, 0);

        /// <summary>True when <paramref name="id"/> is one of the slice's rows' ids.</summary>
        public bool Contains(long id) => id >= First && id < End;
    }

    /// <summary>The ids of the Sessions whose slice holds at least one row. Reads ids and stamps
    /// through the run index, never a row's bytes.</summary>
    public static HashSet<long> Recorded(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var byRun = new Dictionary<long, List<(long Id, long StartedMs, long? EndedMs)>>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT mucka_run_id, id, started_ms, ended_ms FROM persona_sessions ORDER BY mucka_run_id, started_ms, id;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var run = reader.GetInt64(0);
                if (!byRun.TryGetValue(run, out var list))
                    byRun[run] = list = [];
                list.Add((reader.GetInt64(1), reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetInt64(3)));
            }
        }

        var recorded = new HashSet<long>();
        using var cut = CutCommand(connection);
        using var first = FirstCommand(connection);
        foreach (var (run, inRun) in byRun)
        {
            // A Session starts at the cut the one before it ended at (SessionSlice.Bounds), so a run of
            // n Sessions needs one cut per boundary, carried from each Session to the next. A null cut
            // is no row stamped later: the slices from there on are empty.
            var from = FirstId(first, run);
            for (var k = 0; k < inRun.Count && from is not null; k++)
            {
                var (_, through) = SessionSlice.Bounds(inRun, inRun[k].Id);
                var end = through is { } t ? Cut(cut, run, t) : null;
                if (from < (end ?? long.MaxValue))
                    recorded.Add(inRun[k].Id);
                if (through is null)
                    break;
                from = end;
            }
        }
        return recorded;
    }

    /// <summary>Session <paramref name="personaSessionId"/>'s slice as ids, and its run; null when
    /// there is no such Session.</summary>
    public static (long RunId, IdRange Range, long? AfterMs, long? ThroughMs)? RangeOf(SqliteConnection connection,
        long personaSessionId)
    {
        ArgumentNullException.ThrowIfNull(connection);
        long run;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT mucka_run_id FROM persona_sessions WHERE id = $id;";
            command.Parameters.AddWithValue("$id", personaSessionId);
            if (command.ExecuteScalar() is not long id)
                return null;
            run = id;
        }
        var inRun = new List<(long Id, long StartedMs, long? EndedMs)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, started_ms, ended_ms FROM persona_sessions WHERE mucka_run_id = $run ORDER BY started_ms, id;";
            command.Parameters.AddWithValue("$run", run);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                inRun.Add((reader.GetInt64(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetInt64(2)));
        }
        var (after, through) = SessionSlice.Bounds(inRun, personaSessionId);
        using var cut = CutCommand(connection);
        using var first = FirstCommand(connection);
        var from = after is { } a ? Cut(cut, run, a) : FirstId(first, run);
        var end = through is { } t ? Cut(cut, run, t) ?? long.MaxValue : long.MaxValue;
        return (run, from is { } f ? new IdRange(f, end) : IdRange.None, after, through);
    }

    /// <summary>What <see cref="Delete"/> removed: the recording's rows and their payload.</summary>
    public sealed record DeleteResult(long Rows, long Bytes);

    /// <summary>
    /// Deletes Session <paramref name="personaSessionId"/>'s recording: its slice's <c>wire</c> rows,
    /// the <c>trimmed</c> rows stamped inside its bounds, and its title and tags, in one transaction,
    /// after <see cref="DatabaseBackup.TakeOnce"/>: the rows cannot be put back. Its
    /// <c>persona_sessions</c> row and the fact tables stay.
    ///
    /// <para>Refuses, with <see cref="InvalidOperationException"/>, a Session whose run has no end: a
    /// second Mucka may still be recording it. Under <see cref="DatabaseLock"/> and
    /// <see cref="WireMaintenance.Gate"/>, as every pass over the wire log is. Off the UI thread.</para>
    /// </summary>
    public static DeleteResult Delete(string dbPath, long personaSessionId)
    {
        using var held = DatabaseLock.Acquire(dbPath, Maintenance.Current, canGiveUp: false);
        lock (WireMaintenance.Gate)
        {
            using var connection = WireTrim.Open(dbPath);
            if (RangeOf(connection, personaSessionId) is not { } found)
                return new DeleteResult(0, 0);
            var (run, range, after, through) = found;
            RequireEnded(connection, run);
            DatabaseBackup.TakeOnce(dbPath);

            using var transaction = connection.BeginTransaction();
            long rows = 0, bytes = 0;
            if (!range.IsEmpty)
            {
                using (var size = connection.CreateCommand())
                {
                    size.Transaction = transaction;
                    size.CommandText = "SELECT COUNT(*), COALESCE(SUM(LENGTH(data)), 0) FROM wire WHERE mucka_run_id = $run AND id >= $first AND id < $end;";
                    AddRange(size, run, range);
                    using var reader = size.ExecuteReader();
                    reader.Read();
                    (rows, bytes) = (reader.GetInt64(0), reader.GetInt64(1));
                }
                using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM wire WHERE mucka_run_id = $run AND id >= $first AND id < $end;";
                AddRange(delete, run, range);
                delete.ExecuteNonQuery();
            }
            // A trimmed row left wire, so it has no place among the slice's ids: it goes by its stamp,
            // inside the Session's bounds.
            using (var trimmed = connection.CreateCommand())
            {
                trimmed.Transaction = transaction;
                trimmed.CommandText = "DELETE FROM trimmed WHERE mucka_run_id = $run"
                    + (after is null ? "" : " AND ts_ms > $after")
                    + (through is null ? "" : " AND ts_ms <= $through") + ";";
                trimmed.Parameters.AddWithValue("$run", run);
                if (after is { } a)
                    trimmed.Parameters.AddWithValue("$after", a);
                if (through is { } t)
                    trimmed.Parameters.AddWithValue("$through", t);
                trimmed.ExecuteNonQuery();
            }
            // A file the launch has not upgraded yet has no labels to delete.
            if (HasTable(connection, transaction, "session_names"))
                foreach (var table in new[] { "session_names", "session_tags" })
                {
                    using var labels = connection.CreateCommand();
                    labels.Transaction = transaction;
                    labels.CommandText = $"DELETE FROM {table} WHERE persona_session_id = $id;";
                    labels.Parameters.AddWithValue("$id", personaSessionId);
                    labels.ExecuteNonQuery();
                }
            transaction.Commit();
            return new DeleteResult(rows, bytes);
        }
    }

    /// <summary>Throws <see cref="InvalidOperationException"/> when run <paramref name="run"/> has no
    /// end: a second Mucka may still be recording it.</summary>
    internal static void RequireEnded(SqliteConnection connection, long run)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ended_ms IS NOT NULL FROM mucka_runs WHERE id = $run;";
        command.Parameters.AddWithValue("$run", run);
        if (Convert.ToInt64(command.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture) == 0)
            throw new InvalidOperationException(
                "this Session's recording has no recorded end, so another Mucka may still be writing it. "
                + "It may get one when the same account next logs in on the same server.");
    }

    private static void AddRange(SqliteCommand command, long run, IdRange range)
    {
        command.Parameters.AddWithValue("$run", run);
        command.Parameters.AddWithValue("$first", range.First);
        command.Parameters.AddWithValue("$end", range.End);
    }

    private static bool HasTable(SqliteConnection connection, SqliteTransaction transaction, string name)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name);";
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    // The run's lowest id stamped later than a boundary: where the boundary cuts it. Walks the run index
    // in id order and stops at the first match.
    private static SqliteCommand CutCommand(SqliteConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM wire WHERE mucka_run_id = $run AND ts_ms > $boundary ORDER BY id LIMIT 1;";
        command.Parameters.Add("$run", SqliteType.Integer);
        command.Parameters.Add("$boundary", SqliteType.Integer);
        return command;
    }

    private static SqliteCommand FirstCommand(SqliteConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM wire WHERE mucka_run_id = $run ORDER BY id LIMIT 1;";
        command.Parameters.Add("$run", SqliteType.Integer);
        return command;
    }

    private static long? Cut(SqliteCommand cut, long run, long boundaryMs)
    {
        cut.Parameters["$run"].Value = run;
        cut.Parameters["$boundary"].Value = boundaryMs;
        return cut.ExecuteScalar() is long id ? id : null;
    }

    private static long? FirstId(SqliteCommand first, long run)
    {
        first.Parameters["$run"].Value = run;
        return first.ExecuteScalar() is long id ? id : null;
    }
}
