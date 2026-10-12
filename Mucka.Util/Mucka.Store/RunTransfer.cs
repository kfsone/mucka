using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Mucka.Store;

/// <summary>What one <see cref="RunTransfer.Import"/> did.</summary>
/// <param name="RunsImported">Runs copied in this call.</param>
/// <param name="RunsAlreadyHere">Runs skipped because this database already holds a run with the same
/// uid.</param>
/// <param name="RunsProbablyHere">Runs skipped because this database holds a run with a different uid
/// but the same start, host and number of wire records. That is a copy of the same run that was
/// given its uid separately - a file older than the uid migration, migrated afresh each time it is
/// read - and importing it would double that history. It is a detection, not a key: a run that only
/// resembles one here is skipped too, and is reported so the operator can look.</param>
/// <param name="RunsSkippedUnclosed">Ids, in the export file, of runs with no <c>ended_ms</c>: the
/// process that wrote them may still have been alive when the file was made, so a copy would freeze
/// part of the run.</param>
/// <param name="Sessions">Logins copied.</param>
/// <param name="WireRows">Wire records copied.</param>
/// <param name="FactRows">Rows copied into <c>swings</c>, <c>fights</c>, <c>score_events</c>,
/// <c>npc_stamina_reads</c> and <c>encounters</c>.</param>
/// <param name="UnattributedRowsLeftBehind">Rows in those five tables with no login in the export
/// file. They belong to no run, so there is nothing to key or de-duplicate them by.</param>
public sealed record RunTransferResult(
    int RunsImported,
    int RunsAlreadyHere,
    int RunsProbablyHere,
    IReadOnlyList<long> RunsSkippedUnclosed,
    int Sessions,
    long WireRows,
    long FactRows,
    long UnattributedRowsLeftBehind);

/// <summary>
/// Moves the recorded corpus from one machine's <c>mucka.db</c> to another's: <c>$EXPORT</c> writes a
/// snapshot file, and <see cref="Import"/> merges one into a database that no client has open - see
/// <see cref="DatabaseImport"/>, which builds the merged file at start-up.
///
/// <para><b>Export is a whole-database snapshot</b> (<c>VACUUM INTO</c>, a read transaction), not a
/// selection. Everything that decides what is worth keeping happens at import, which has to decide it
/// anyway because it cannot know where a file came from.</para>
///
/// <para><b>Import merges finished runs.</b> A run is copied with its whole tree in one transaction:
/// its wire records, its logins, every fact row on those logins, and those logins' encounters with
/// their child rows. Row ids are local to their database, so each is renumbered and each foreign key
/// to one rewritten (<c>mucka_run_id</c>, <c>persona_session_id</c>, <c>contents_id</c>). An encounter
/// is referred to by its uid, which means the same in every database and is copied as it stands.</para>
///
/// <para>Idempotent on <c>mucka_runs.uid</c>: importing the same file twice, or importing into A the
/// export of a B that holds A's runs, adds nothing. <c>encounters.uid</c> is unique, so a copy of an
/// encounter that slipped past the run check fails the import instead of doubling its rows.</para>
///
/// <para>The export file is copied to a scratch path and migrated there with <see cref="MuckaDb.Open"/>,
/// so a file written by an older release arrives in this release's shape and the file the operator
/// handed over is never modified. A file whose journal names a script this build does not have came
/// from a newer release and is refused.</para>
///
/// <para>A run copied in is marked with <see cref="ImportedMark"/>. Its logins are exact, so they must
/// not become windows that claim this database's own unattributed rows, and
/// <c>PersonaSessionBackfill</c> leaves out every run carrying <see cref="ImportedMark"/>.</para>
///
/// <para>Threading: opens its own connections and must run OFF the UI thread (Invariant #1).</para>
/// </summary>
public static class RunTransfer
{
    /// <summary>Suffix of <c>mucka_runs.client_version</c> on every run an import copied in. Added once:
    /// a run exported again from the machine it was imported to keeps one.</summary>
    public const string ImportedMark = " (imported)";

    private static readonly string[] SessionTables =
        ["swings", "fights", "score_events", "npc_stamina_reads", "encounters"];

    /// <summary>Encounter children, which refer to their encounter by uid. <c>encounter_contents</c> is
    /// handled apart, because its own children key on its id.</summary>
    private static readonly string[] EncounterChildTables =
        ["encounter_lines", "encounter_stats", "creature_values", "encounter_events"];

    private const string EncountersOfSession =
        "encounter_uid IN (SELECT uid FROM encounters WHERE persona_session_id = $p)";

    /// <summary>Writes a snapshot of <paramref name="databasePath"/> to <paramref name="targetPath"/>.
    /// Refuses to overwrite: a file already there is either an earlier export or something that is not
    /// ours, and nothing here can tell which.</summary>
    public static void Export(string databasePath, string targetPath)
    {
        targetPath = Path.GetFullPath(targetPath);
        if (File.Exists(targetPath))
            throw new IOException($"{targetPath} already exists");
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        try
        {
            using var connection = MuckaDb.OpenRead(databasePath);
            using var command = connection.CreateCommand();
            command.CommandText = "VACUUM INTO $target;";
            command.Parameters.AddWithValue("$target", targetPath);
            command.ExecuteNonQuery();
        }
        catch
        {
            // A half-written snapshot would be refused as "already exists" next time.
            try { File.Delete(targetPath); } catch (IOException) { }
            throw;
        }
    }

    public static RunTransferResult Import(string destinationPath, string sourcePath)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "mucka-transfer-" + Guid.NewGuid().ToString("N") + ".db");
        File.Copy(sourcePath, scratch);
        File.SetAttributes(scratch, FileAttributes.Normal);
        try
        {
            RequireJournalWeKnow(scratch);
            using var source = MuckaDb.Open(scratch);
            using var destination = MuckaDb.Open(destinationPath);
            return Merge(source, destination);
        }
        finally
        {
            using (var pooled = new SqliteConnection(MuckaDb.ConnectionString(scratch)))
                SqliteConnection.ClearPool(pooled);
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                try { File.Delete(scratch + suffix); } catch (IOException) { }
        }
    }

    /// <summary>Refuses a file that is not a Mucka database, or that a newer release wrote. Reads the
    /// journal before <see cref="MuckaDb.Open"/>, which would otherwise build the baseline schema into
    /// whatever SQLite file it was handed.</summary>
    private static void RequireJournalWeKnow(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open();

        using var exists = connection.CreateCommand();
        exists.CommandText =
            $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{MigrationScripts.JournalTable}' COLLATE NOCASE;";
        if (Convert.ToInt64(exists.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
            throw new InvalidOperationException("not a Mucka database (no schema journal)");

        using var names = connection.CreateCommand();
        names.CommandText = $"SELECT ScriptName FROM {MigrationScripts.JournalTable};";
        using var reader = names.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(0);
            if (!MigrationScripts.AllNames.Contains(name))
                throw new InvalidOperationException(
                    $"written by a newer Mucka (schema script {name}); update this one first");
        }
    }

    private static RunTransferResult Merge(SqliteConnection source, SqliteConnection destination)
    {
        var copier = new Copier(source, destination);
        var skipped = new List<long>();
        int imported = 0, already = 0, probably = 0, sessions = 0;
        long wireRows = 0, factRows = 0;
        var counts = new Counts(new WireCounts(source), new WireCounts(destination));

        foreach (var run in ReadRuns(source))
        {
            if (run.EndedMs is null)
            {
                skipped.Add(run.Id);
                continue;
            }

            using var transaction = destination.BeginTransaction();
            switch (HeldAlready(counts, destination, transaction, run))
            {
                case Held.Uid:
                    already++;
                    continue;
                case Held.Lookalike:
                    probably++;
                    continue;
            }

            var runId = InsertRun(destination, transaction, run);
            var copiedWire = copier.Copy(transaction, "wire", "mucka_run_id = $p", run.Id, "mucka_run_id", runId);
            wireRows += copiedWire;
            counts.Destination.Add(runId, copiedWire, transaction);

            var sessionMap = new Dictionary<long, long>();
            sessions += (int)copier.Copy(transaction, "persona_sessions", "mucka_run_id = $p", run.Id,
                "mucka_run_id", runId, sessionMap);

            foreach (var (oldSession, newSession) in sessionMap)
            {
                foreach (var table in SessionTables)
                    factRows += copier.Copy(transaction, table, "persona_session_id = $p", oldSession,
                        "persona_session_id", newSession);

                foreach (var table in EncounterChildTables)
                    copier.Copy(transaction, table, EncountersOfSession, oldSession);

                var contentsMap = new Dictionary<long, long>();
                copier.Copy(transaction, "encounter_contents", EncountersOfSession, oldSession, idMap: contentsMap);
                foreach (var (oldContents, newContents) in contentsMap)
                    copier.Copy(transaction, "encounter_contents_items", "contents_id = $p", oldContents,
                        "contents_id", newContents);
            }

            transaction.Commit();
            imported++;
        }

        MergeSomeKinds(source, destination);
        return new RunTransferResult(imported, already, probably, skipped, sessions, wireRows, factRows,
            CountUnattributed(source));
    }

    private readonly record struct SourceRun(
        long Id, string Uid, long StartedMs, long? EndedMs, string? Host, string? Version, string? AccountId);

    private static List<SourceRun> ReadRuns(SqliteConnection source)
    {
        using var command = source.CreateCommand();
        command.CommandText =
            "SELECT id, uid, started_ms, ended_ms, host, client_version, account_id FROM mucka_runs ORDER BY started_ms, id;";
        var runs = new List<SourceRun>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            runs.Add(new SourceRun(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        return runs;
    }

    private enum Held { No, Uid, Lookalike }

    /// <summary>Whether this database already holds the run: by uid, which is the key, or by a run that
    /// starts at the same instant on the same host with the same number of wire records, which is what
    /// the same run looks like when it was given its uid twice (see
    /// <see cref="RunTransferResult.RunsProbablyHere"/>). Inside the transaction, so two importers
    /// cannot both pass it.</summary>
    private static Held HeldAlready(Counts counts, SqliteConnection destination,
        SqliteTransaction transaction, SourceRun run)
    {
        using var byUid = destination.CreateCommand();
        byUid.Transaction = transaction;
        byUid.CommandText = "SELECT EXISTS(SELECT 1 FROM mucka_runs WHERE uid = $uid);";
        byUid.Parameters.AddWithValue("$uid", run.Uid);
        if (Convert.ToInt64(byUid.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
            return Held.Uid;

        using var candidates = destination.CreateCommand();
        candidates.Transaction = transaction;
        candidates.CommandText = "SELECT id FROM mucka_runs WHERE started_ms = $started AND host IS $host;";
        candidates.Parameters.AddWithValue("$started", run.StartedMs);
        candidates.Parameters.AddWithValue("$host", (object?)run.Host ?? DBNull.Value);
        var ids = new List<long>();
        using (var reader = candidates.ExecuteReader())
            while (reader.Read())
                ids.Add(reader.GetInt64(0));
        if (ids.Count == 0)
            return Held.No;

        var wire = counts.Source.Of(run.Id);
        return ids.Any(id => counts.Destination.Of(id, transaction) == wire) ? Held.Lookalike : Held.No;
    }

    /// <summary>Wire records per run, read in one pass the first time they are asked for. <c>wire</c>
    /// has no index on its run (see the design doc), so a count per run is a scan per run: on a
    /// database of 1.18 million wire rows, 185 such counts took over a minute.</summary>
    private sealed class WireCounts(SqliteConnection connection)
    {
        private Dictionary<long, long>? _byRun;

        public long Of(long runId, SqliteTransaction? transaction = null)
        {
            if (_byRun is null)
            {
                _byRun = [];
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "SELECT mucka_run_id, COUNT(*) FROM wire GROUP BY mucka_run_id;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    _byRun[reader.GetInt64(0)] = reader.GetInt64(1);
            }
            return _byRun.GetValueOrDefault(runId);
        }

        /// <summary>Records a run this import has just copied in, so a later source run is compared
        /// against it too.</summary>
        public void Add(long runId, long count, SqliteTransaction transaction)
        {
            Of(runId, transaction);
            _byRun![runId] = count;
        }
    }

    private sealed record Counts(WireCounts Source, WireCounts Destination);

    private static long InsertRun(SqliteConnection destination, SqliteTransaction transaction, SourceRun run)
    {
        var version = run.Version ?? string.Empty;
        if (!version.EndsWith(ImportedMark, StringComparison.Ordinal))
            version += ImportedMark;

        using var command = destination.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO mucka_runs (uid, started_ms, ended_ms, host, client_version, account_id)
            VALUES ($uid, $started, $ended, $host, $version, $account);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$uid", run.Uid);
        command.Parameters.AddWithValue("$started", run.StartedMs);
        command.Parameters.AddWithValue("$ended", run.EndedMs);
        command.Parameters.AddWithValue("$host", (object?)run.Host ?? DBNull.Value);
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$account", (object?)run.AccountId ?? DBNull.Value);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>One row per species and a later reading replaces an earlier one, so the row with the
    /// larger <c>learned_ms</c> wins.</summary>
    private static void MergeSomeKinds(SqliteConnection source, SqliteConnection destination)
    {
        using var select = source.CreateCommand();
        select.CommandText = "SELECT species, kind, learned_ms FROM some_kinds;";
        using var reader = select.ExecuteReader();

        using var transaction = destination.BeginTransaction();
        using var upsert = destination.CreateCommand();
        upsert.Transaction = transaction;
        upsert.CommandText = """
            INSERT INTO some_kinds (species, kind, learned_ms) VALUES ($species, $kind, $learned)
            ON CONFLICT (species) DO UPDATE SET kind = excluded.kind, learned_ms = excluded.learned_ms
            WHERE excluded.learned_ms > some_kinds.learned_ms;
            """;
        var species = upsert.Parameters.Add("$species", SqliteType.Text);
        var kind = upsert.Parameters.Add("$kind", SqliteType.Text);
        var learned = upsert.Parameters.Add("$learned", SqliteType.Integer);
        while (reader.Read())
        {
            species.Value = reader.GetString(0);
            kind.Value = reader.GetString(1);
            learned.Value = reader.GetInt64(2);
            upsert.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static long CountUnattributed(SqliteConnection source)
    {
        long total = 0;
        foreach (var table in SessionTables)
        {
            using var command = source.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM \"{table}\" WHERE persona_session_id IS NULL;";
            total += Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        return total;
    }

    /// <summary>Copies rows between two databases of the same shape, column for column, so the column
    /// list is read from the destination's schema and a new column travels without this file being
    /// edited.</summary>
    private sealed class Copier(SqliteConnection source, SqliteConnection destination)
    {
        private readonly Dictionary<string, List<string>> _columns = new(StringComparer.Ordinal);

        private List<string> ColumnsOf(string table)
        {
            if (_columns.TryGetValue(table, out var cached))
                return cached;

            using var command = destination.CreateCommand();
            command.CommandText = $"PRAGMA table_info(\"{table}\");";
            var columns = new List<string>();
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    if (reader.GetString(1) != "id")
                        columns.Add(reader.GetString(1));
            return _columns[table] = columns;
        }

        /// <summary>Copies the rows of <paramref name="table"/> matching <paramref name="where"/> (which
        /// binds <c>$p</c> to <paramref name="oldParent"/>), in id order. <paramref name="parentColumn"/>,
        /// when given, is written as <paramref name="newParent"/> instead of the source's value. Each
        /// new id is recorded in <paramref name="idMap"/> against the old.</summary>
        public long Copy(SqliteTransaction transaction, string table, string where, long oldParent,
            string? parentColumn = null, long newParent = 0, Dictionary<long, long>? idMap = null)
        {
            var copied = ColumnsOf(table).Where(c => c != parentColumn).ToList();
            var written = parentColumn is null ? copied : [.. copied, parentColumn];

            using var select = source.CreateCommand();
            select.CommandText =
                $"SELECT id{string.Concat(copied.Select(c => $", \"{c}\""))} FROM \"{table}\" WHERE {where} ORDER BY id;";
            select.Parameters.AddWithValue("$p", oldParent);

            using var insert = destination.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = $"INSERT INTO \"{table}\" ({string.Join(", ", written.Select(c => $"\"{c}\""))}) "
                + $"VALUES ({string.Join(", ", written.Select((_, i) => $"$c{i}"))});";
            var parameters = written.Select((_, i) =>
            {
                var parameter = insert.CreateParameter();
                parameter.ParameterName = $"$c{i}";
                insert.Parameters.Add(parameter);
                return parameter;
            }).ToList();

            using var lastId = destination.CreateCommand();
            lastId.Transaction = transaction;
            lastId.CommandText = "SELECT last_insert_rowid();";

            long count = 0;
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                for (var i = 0; i < copied.Count; i++)
                    parameters[i].Value = reader.GetValue(i + 1);
                if (parentColumn is not null)
                    parameters[copied.Count].Value = newParent;
                insert.ExecuteNonQuery();
                if (idMap is not null)
                    idMap[reader.GetInt64(0)] = Convert.ToInt64(lastId.ExecuteScalar(), CultureInfo.InvariantCulture);
                count++;
            }
            return count;
        }
    }
}
