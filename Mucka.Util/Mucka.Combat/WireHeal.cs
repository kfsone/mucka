using Microsoft.Data.Sqlite;
using MudSharp.Protocol;
using Mucka.Store;
using Mucka.WireLog;

namespace Mucka.Combat;

/// <summary>
/// Joins the rx rows of an ended run into the units <see cref="WireLogWriter.CloseUnit"/> would have
/// joined as they were written, for a run that was written one row per read.
///
/// <para>A run is replayed through <see cref="MudStreamParser"/> exactly as the connection feeds it:
/// each rx row is added to the open <see cref="WireUnit"/> and then fed, a prompt that closes in game
/// mode closes the unit, and game mode being entered abandons it. A unit's bytes go into the
/// row of its last read and the earlier reads are removed. Tx rows and annotations stay where they are,
/// so a command sent while a message was still arriving comes before the message, as the player saw it.
/// Nothing outside game mode is joined, which is what the live writer does with login and menu
/// traffic.</para>
///
/// <para>A gap in <c>seq</c> abandons the open unit. A gap is a row another pass removed
/// (a trim from the Logs page), and a revert must find the rows around it as they were.</para>
///
/// <para>The joined row carries the last read's <c>ts_ms</c>, so what the earlier reads arrived at is
/// lost. The join changes no byte.</para>
///
/// <para><b>Unwrap.</b> After the join, a run's rx rows are rewritten in place to take out the line
/// wrapping the server did at the width the client sent (<see cref="WireUnwrap"/>). That rewrite does
/// change bytes, and the launch's backup is the only way back, when the player's policy takes one. A run a trim has been over is passed
/// over: the trim took the sends and responses that mark where one turn ends and the next begins, and
/// joining lines across the gap would join lines of different turns.</para>
///
/// <para>Each run is healed once and unwrapped once: <c>mucka_runs.wire_healed_ms</c> and a
/// <c>wire_unwrapped</c> row record the passes, and a run this client records carries the second from
/// the start, and the first when it is written joined. A run that has not ended is skipped, because
/// another client may still be writing it. The join is committed in small transactions, and a run's
/// unwrap in one transaction with its mark, without a pause between them: it runs at launch, before
/// this process has a game whose writer it could keep from the lock.</para>
/// </summary>
public static class WireHeal
{
    /// <summary>The joins committed per transaction.</summary>
    public const int JoinsPerTransaction = 200;

    /// <summary>What a pass did.</summary>
    public sealed record Result(int RunsHealed, long RowsRemoved, long GroupsJoined, long RowsUnwrapped);

    private const string NotUnwrapped =
        "NOT EXISTS (SELECT 1 FROM wire_unwrapped u WHERE u.mucka_run_id = mucka_runs.id)";

    private const string PendingWhere =
        "ended_ms IS NOT NULL AND (wire_healed_ms IS NULL OR " + NotUnwrapped + ")";

    /// <summary>How many ended runs <see cref="Run"/> has yet to heal or unwrap.</summary>
    public static int PendingRuns(string dbPath)
    {
        using var connection = MuckaDb.OpenRead(dbPath);
        using var count = connection.CreateCommand();
        count.CommandText = $"SELECT COUNT(*) FROM mucka_runs WHERE {PendingWhere};";
        return Convert.ToInt32(count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>What a launch does before any page that can start a game is up: brings the schema of an
    /// existing file current (a heal needs <c>wire_healed_ms</c>), and when runs are pending, backs the
    /// database up (<see cref="DatabaseBackup"/>) and heals them under a job on
    /// <paramref name="maintenance"/> for the whole of it. The gate is held from the backup to the end, and
    /// <see cref="DatabaseLock"/> from before the schema upgrade, which another Mucka waits on.
    /// Null when there is no file or nothing to heal. A backup that cannot be taken throws before
    /// anything is healed, because a heal cannot be undone without it. A launch takes one backup
    /// (<see cref="DatabaseBackup.TakeOnce"/>): when an import already left the previous database as the
    /// backup, none is taken.</summary>
    public static Result? RunWithBackup(string dbPath, MaintenanceTracker maintenance,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(dbPath))
            return null;
        // From the schema upgrade to the end of the heal, so a second Mucka started meanwhile waits
        // (DatabaseLock) rather than backing up over the copy this launch is about to take.
        using var held = DatabaseLock.Acquire(dbPath, maintenance, cancellationToken: cancellationToken);
        using (MuckaDb.Open(dbPath)) { }
        if (PendingRuns(dbPath) == 0)
            return null;

        const string Joining = "Joining split lines in the wire log";
        using var job = maintenance.Begin(DatabaseBackup.IsDue(dbPath) ? MuckaDb.BackupDetail : Joining);
        lock (WireMaintenance.Gate)
        {
            if (DatabaseBackup.TakeOnce(dbPath) is not null)
                job.Detail = Joining;
            return RunLocked(dbPath, cancellationToken);
        }
    }

    /// <summary>Heals every ended run not yet healed. Off the UI thread: it walks the wire log.</summary>
    public static Result Run(string dbPath, CancellationToken cancellationToken = default)
    {
        lock (WireMaintenance.Gate)
            return RunLocked(dbPath, cancellationToken);
    }

    private static Result RunLocked(string dbPath, CancellationToken cancellationToken)
    {
        using var connection = new SqliteConnection(MuckaDb.ConnectionString(dbPath));
        connection.Open();
        Execute(connection, "PRAGMA busy_timeout=10000;");

        var runs = new List<(long Id, bool Join, bool Unwrap)>();
        using (var find = connection.CreateCommand())
        {
            find.CommandText =
                $"SELECT id, wire_healed_ms IS NULL, {NotUnwrapped} FROM mucka_runs WHERE {PendingWhere} ORDER BY id;";
            using var reader = find.ExecuteReader();
            while (reader.Read())
                runs.Add((reader.GetInt64(0), reader.GetBoolean(1), reader.GetBoolean(2)));
        }

        var healed = 0;
        long removed = 0, joined = 0, unwrapped = 0;
        foreach (var (run, join, unwrap) in runs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (join)
            {
                var (rowsRemoved, groups) = HealRun(connection, run, cancellationToken);
                removed += rowsRemoved;
                joined += groups;
            }
            if (unwrap)
                unwrapped += UnwrapRun(connection, run, cancellationToken);
            healed++;
        }
        return new Result(healed, removed, joined, unwrapped);
    }

    /// <summary>The joins one run's rows call for: <paramref name="KeepId"/>, with its
    /// <paramref name="KeepSeq"/>, is the row the joined <paramref name="Data"/> go into, and
    /// <paramref name="RemoveIds"/> the earlier rows of the unit that are removed. Pure, so the rule can
    /// be tested on its own.</summary>
    public readonly record struct Join(long KeepId, long KeepSeq, byte[] Data, IReadOnlyList<long> RemoveIds);

    /// <summary>The rows of one run, in id order, as <see cref="Plan"/> reads them.</summary>
    public readonly record struct Row(long Id, long Seq, int Direction, byte[] Data);

    /// <summary>The joins for <paramref name="rows"/>, which are one run's rows in id order.</summary>
    public static List<Join> Plan(IEnumerable<Row> rows)
    {
        var joins = new List<Join>();
        var parser = new MudStreamParser();
        var unit = new WireUnit();
        var idBySeq = new Dictionary<long, long>();

        void Abandon()
        {
            unit.Abandon();
            idBySeq.Clear();
        }

        void Close()
        {
            if (unit.Close(out var seqs, out var data))
            {
                var remove = new List<long>(seqs.Length - 1);
                for (var i = 0; i < seqs.Length - 1; i++)
                    remove.Add(idBySeq[seqs[i]]);
                joins.Add(new Join(idBySeq[seqs[^1]], seqs[^1], data, remove));
            }
            idBySeq.Clear();
        }

        parser.PromptClosed += Close;
        parser.GameModeEntered += Abandon;

        long? lastSeq = null;
        foreach (var row in rows)
        {
            if (lastSeq is { } last && row.Seq != last + 1)
                Abandon();
            lastSeq = row.Seq;

            if (row.Direction != (int)WireDirection.Rx)
                continue;
            idBySeq[row.Seq] = row.Id;
            unit.Add(row.Seq, row.Data);
            parser.Feed(row.Data);
        }
        return joins;
    }

    private static (long RowsRemoved, long Groups) HealRun(
        SqliteConnection connection, long run, CancellationToken cancellationToken)
    {
        var joins = Plan(ReadRows(connection, run));

        long removed = 0;
        for (var at = 0; at < joins.Count; at += JoinsPerTransaction)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var transaction = connection.BeginTransaction();
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                "UPDATE wire SET data = $data WHERE id = $id AND mucka_run_id = $run AND seq = $seq;";
            var data = update.Parameters.Add("$data", SqliteType.Blob);
            var keep = update.Parameters.Add("$id", SqliteType.Integer);
            var keepSeq = update.Parameters.Add("$seq", SqliteType.Integer);
            update.Parameters.AddWithValue("$run", run);
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM wire WHERE id = $id AND mucka_run_id = $run;";
            var gone = delete.Parameters.Add("$id", SqliteType.Integer);
            delete.Parameters.AddWithValue("$run", run);

            foreach (var join in joins.Skip(at).Take(JoinsPerTransaction))
            {
                data.Value = join.Data;
                keep.Value = join.KeepId;
                keepSeq.Value = join.KeepSeq;
                // The plan was made from a snapshot. If the row it keeps is gone - another pass moved
                // it - the rows before it are not removed, or their bytes would be nowhere.
                if (update.ExecuteNonQuery() != 1) continue;
                foreach (var id in join.RemoveIds)
                {
                    gone.Value = id;
                    removed += delete.ExecuteNonQuery();
                }
            }
            transaction.Commit();
        }

        using var mark = connection.CreateCommand();
        mark.CommandText = "UPDATE mucka_runs SET wire_healed_ms = $now WHERE id = $id;";
        mark.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        mark.Parameters.AddWithValue("$id", run);
        mark.ExecuteNonQuery();
        return (removed, joins.Count);
    }

    // The rows of one run the unwrap changes, rewritten in place, and the run marked unwrapped - also
    // when the gate refuses the plan or a trim has been over the run, so neither is replayed at every
    // launch. One transaction with the mark: a line already joined is longer than the width allows a
    // wrapped row to be, so a run left half unwrapped would never have the rest of a paragraph joined.
    private static long UnwrapRun(SqliteConnection connection, long run, CancellationToken cancellationToken)
    {
        var changes = (WireTrim.HasTrimmedProbes(connection, run)
            ? null
            : WireUnwrap.Plan(WireTrim.ReadRows(connection, run))) ?? new List<WireUnwrap.Changed>();
        cancellationToken.ThrowIfCancellationRequested();

        long changed = 0;
        using var transaction = connection.BeginTransaction();
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                "UPDATE wire SET data = $data WHERE id = $id AND mucka_run_id = $run AND seq = $seq;";
            var data = update.Parameters.Add("$data", SqliteType.Blob);
            var id = update.Parameters.Add("$id", SqliteType.Integer);
            var seq = update.Parameters.Add("$seq", SqliteType.Integer);
            update.Parameters.AddWithValue("$run", run);
            foreach (var change in changes)
            {
                data.Value = change.Data;
                id.Value = change.Id;
                seq.Value = change.Seq;
                changed += update.ExecuteNonQuery();
            }
        }

        using (var mark = connection.CreateCommand())
        {
            mark.Transaction = transaction;
            mark.CommandText = "INSERT OR IGNORE INTO wire_unwrapped (mucka_run_id, unwrapped_ms) VALUES ($id, $now);";
            mark.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            mark.Parameters.AddWithValue("$id", run);
            mark.ExecuteNonQuery();
        }
        transaction.Commit();
        return changed;
    }

    internal const string ReadRowsSql =
        "SELECT id, seq, direction, data FROM wire WHERE mucka_run_id = $run ORDER BY id;";

    private static IEnumerable<Row> ReadRows(SqliteConnection connection, long run)
    {
        using var command = connection.CreateCommand();
        command.CommandText = ReadRowsSql;
        command.Parameters.AddWithValue("$run", run);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            yield return new Row(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2),
                reader.GetFieldValue<byte[]>(3));
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
