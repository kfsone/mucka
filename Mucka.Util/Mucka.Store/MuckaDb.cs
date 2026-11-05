using DbUp;
using DbUp.Engine;
using DbUp.Sqlite.Helpers;
using Microsoft.Data.Sqlite;

namespace Mucka.Store;

/// <summary>
/// The client's one database: <c>~/.mucka/mucka.db</c>. Combat corpus, raw wire log and
/// per-encounter combat logs, in one file. See <c>docs/persistence-design.md</c>, which governs this.
///
/// <para><b>Threading.</b> Writes go through a single connection owned by one background task - see
/// <see cref="MuckaStore"/>. Reads open their own short-lived connection and must never run on the UI
/// thread (Invariant #1); the live combat rail reads a warmed in-memory cache, never SQL. WAL is on so
/// a read can never block the writer or vice versa.</para>
///
/// <para><b>Migration policy: ordered, numbered, forward-only scripts.</b> Every schema change is a
/// new file in <c>Migrations/</c>, run once per database and recorded in DbUp's journal. There is no
/// second description of the current shape - see <see cref="MigrationScripts"/> for why, and for the
/// rule that a released script is never edited.</para>
///
/// <para><b>Why a journal rather than converging on the file's own shape.</b> Releases go out on
/// GitHub, so databases created by this client live on machines nobody here can inspect. Convergence
/// is a standing instruction re-evaluated at every open, forever: a rule that drops a dead column
/// would keep dropping it, on a stranger's file, unprompted and with no backup. Ordered replay runs a
/// change exactly once and then stops being a rule at all. A file that predates the journal is
/// brought to one known shape by <see cref="LegacySchemaAdopter"/> - once, and never again.</para>
/// </summary>
public static class MuckaDb
{
    /// <summary>Standard file name. The DIRECTORY is supplied by the caller (see Core/MuckaPaths.cs,
    /// which owns the platform lookup) so this type stays free of MAUI references and can be tested
    /// against a temp path.</summary>
    public const string DefaultFileName = "mucka.db";

    /// <summary>A connection string for <paramref name="path"/>. Pooling is left at its default (on),
    /// so the short-lived read connections cost an entry lookup rather than a file open.</summary>
    public static string ConnectionString(string path)
        => new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();

    /// <summary>Opens a connection, creating the file and directory if needed, and brings the schema
    /// up to date - see <see cref="ApplySchema(SqliteConnection)"/>. An existing file that has
    /// migrations to run is backed up first (<see cref="DatabaseBackup.TakeOnce"/>) unless
    /// <paramref name="backUpBeforeUpgrade"/> is false, which is for a scratch copy the caller
    /// backs up, or replaces, itself.
    ///
    /// <para><b>A second process opening the same file waits.</b> Migrations are not all IF NOT EXISTS -
    /// they remove columns and rename tables - so two clients that both read the journal before either
    /// writes it would both try, and the loser would throw. A file that is new or has migrations to run is
    /// brought to the current schema under <see cref="DatabaseLock"/>: the second client waits, under a
    /// maintenance job, then finds the work done and does none of it.</para>
    /// </summary>
    public static SqliteConnection Open(string path, bool backUpBeforeUpgrade = true)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var connection = new SqliteConnection(ConnectionString(path));
        try
        {
            connection.Open();
            Prepare(connection);
            ApplySchema(connection, Maintenance.Current, backUpBeforeUpgrade, path);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void Prepare(SqliteConnection connection)
    {
        // WAL: readers and the single writer never block each other, and a crash mid-write rolls back
        // to the last commit rather than leaving a torn row - strictly better than the append-only
        // text files this replaced, which could and did leave truncated final lines.
        Execute(connection, "PRAGMA journal_mode=WAL;");
        // NORMAL rather than FULL: one fsync per checkpoint instead of one per commit. A power cut can
        // lose the last transaction or two, which is a couple of swings and the open wire batch.
        Execute(connection, "PRAGMA synchronous=NORMAL;");
        Execute(connection, "PRAGMA foreign_keys=ON;");
        // There is one client writer, so this no longer arbitrates between writers. It covers a
        // READER overlapping the writer - a warm-up query, or an offline tool - and a second instance
        // of the client. Five seconds is far longer than any commit here takes.
        Execute(connection, "PRAGMA busy_timeout=5000;");
    }

    /// <summary>The read-only connection string <see cref="OpenRead"/> uses.</summary>
    public static string ReadConnectionString(string path)
        => new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString();

    /// <summary>Opens read-only. Throws if the file does not exist.</summary>
    public static SqliteConnection OpenRead(string path)
    {
        var connection = new SqliteConnection(ReadConnectionString(path));
        connection.Open();
        return connection;
    }

    /// <summary>Deletes a database file, its WAL companions and its lock file, closing the pooled connections that
    /// would otherwise hold it open. For a throwaway store - a replay's - never for the real one.
    /// Best effort: a file that cannot go yet is left for the system's temp cleanup.</summary>
    public static void Delete(string path)
    {
        using (var writable = new SqliteConnection(ConnectionString(path)))
            SqliteConnection.ClearPool(writable);
        using (var readOnly = new SqliteConnection(ReadConnectionString(path)))
            SqliteConnection.ClearPool(readOnly);
        foreach (var file in new[] { path, path + "-wal", path + "-shm", DatabaseLock.PathFor(path) })
        {
            try { File.Delete(file); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Brings <paramref name="connection"/> to the current schema: the legacy adopter first if this
    /// file has never seen the journal, then every forward script it has not already run.
    ///
    /// <para>Safe to call repeatedly: a file already current does no work beyond reading its journal.
    /// Skipping forward several scripts at once is the ordinary path, not a special case.</para>
    ///
    /// <para>What it does NOT cover is a file older than the adopter's two shapes - fresh, or v0.20.0.
    /// Anything earlier gets the baseline's CREATE IF NOT EXISTS as no-ops and then fails at its first
    /// INSERT against a column it never gained. Nothing older than v0.20.0 was supported before the
    /// journal either, so this narrows nothing; it is stated because the alternative is a reader
    /// assuming otherwise.</para>
    /// </summary>
    public static void ApplySchema(SqliteConnection connection)
        => ApplySchema(connection, Maintenance.Current);

    /// <summary>
    /// <see cref="ApplySchema(SqliteConnection)"/> that opens a job on <paramref name="maintenance"/>
    /// for as long as the work takes, and none when the file is already current or has nothing in it yet.
    ///
    /// <para>A file that exists and has tables is backed up before its first migration
    /// (<see cref="DatabaseBackup.TakeOnce"/>), because released scripts remove columns and tables and
    /// a stranger's file has no other copy - unless every pending script only adds an index or table
    /// (<see cref="MigrationScripts.IsAdditive"/>), which can lose nothing. A backup that cannot be taken throws, and nothing is
    /// migrated. It is skipped when <paramref name="backUp"/> is false, and for a connection that is
    /// not a file.</para>
    /// </summary>
    public static void ApplySchema(SqliteConnection connection, MaintenanceTracker maintenance, bool backUp = true,
        string? databasePath = null)
    {
        MaintenanceTracker.Job? job = null;
        IDisposable? held = null;
        try
        {
            var existing = HasTables(connection);

            var journaled = LegacySchemaAdopter.JournalExists(connection);
            var engine = BuildUpgradeEngine(connection);
            if (!journaled || engine.IsUpgradeRequired())
            {
                // The caller's own spelling of the path is what the rest of a launch keys its backup by,
                // so it is used when there is one; the connection's is its resolved form.
                var path = databasePath ?? connection.DataSource;
                if (backUp && IsFile(path))
                {
                    // One process creates or upgrades a file at a time (DatabaseLock), a new one included:
                    // two first launches would otherwise both run the scripts. Whatever was decided before
                    // the lock was had is decided again after it: the holder may have done this very work.
                    // A scratch copy (backUp false) is the caller's own, and takes no lock.
                    held = DatabaseLock.Acquire(path, maintenance);
                    existing = HasTables(connection);
                    journaled = LegacySchemaAdopter.JournalExists(connection);
                }
                // A file with nothing in it is being created, not upgraded: no job, so a throwaway store
                // (a replay's, an export's scratch) does not put the overlay over the game.
                if (existing && (!journaled || engine.IsUpgradeRequired()))
                {
                    job = maintenance.Begin(UpgradeDetail);
                    // Only scripts that add nothing but an index or table need no copy of the file.
                    if (!journaled || engine.GetScriptsToExecute().Any(s => !MigrationScripts.IsAdditive(s.Name)))
                        BackUp(path, job, backUp);
                }
            }

            // Before the journal exists, and only then: the two v0.20.0 column adds that no idempotent
            // script can express. Everything else, including the baseline, is an ordinary migration.
            // An empty file has no journal either, and the adopter declines every probe on it.
            if (!journaled)
                LegacySchemaAdopter.Run(connection);

            var result = engine.PerformUpgrade();
            if (!result.Successful)
                throw new InvalidOperationException(
                    $"Schema migration failed on '{result.ErrorScript?.Name ?? "(unknown script)"}'.",
                    result.Error);
        }
        finally
        {
            job?.Dispose();
            held?.Dispose();
        }
    }

    private static bool IsFile(string path) => !string.IsNullOrEmpty(path) && File.Exists(path);

    private static void BackUp(string path, MaintenanceTracker.Job job, bool backUp)
    {
        if (!backUp || !IsFile(path))
            return;
        if (!DatabaseBackup.IsDue(path))
            return;
        job.Detail = BackupDetail;
        DatabaseBackup.TakeOnce(path);
        job.Detail = UpgradeDetail;
    }

    private static bool HasTables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table';";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    /// <summary>What the maintenance overlay says while a schema upgrade runs.</summary>
    public const string UpgradeDetail = "Upgrading your game data";

    /// <summary>What the maintenance overlay says while a backup is taken, for players rather than for
    /// whoever reads the code: a line, and after the line feed a sub-heading the overlay shows
    /// smaller (<see cref="MaintenanceTracker.Job.Detail"/>).</summary>
    public const string BackupDetail = "Backing up data for safety\n(probably so I can introduce a shiny new feature)";

    /// <summary>
    /// The configured runner. Three settings are load-bearing and none of them is DbUp's default.
    ///
    /// <para><b>A transaction per script.</b> DbUp runs without one unless told. SQLite DDL is
    /// transactional, so with this set a script and its journal row commit or roll back together -
    /// which is what makes journal and schema unable to disagree on this engine, and what stops a
    /// half-applied migration on a file we cannot reach. Per script rather than one for all of them so
    /// a failure at 0007 keeps the work 0002..0006 did.</para>
    ///
    /// <para><b>Variables disabled.</b> DbUp otherwise treats <c>$name$</c> in a script as a
    /// substitution and throws on an undefined one. Nothing trips it today; a future migration
    /// containing a dollar sign should not fail at a stranger's first launch over string
    /// interpolation nobody asked for.</para>
    ///
    /// <para><b>Scripts handed over explicitly</b>, so nothing is discovered by reflection - see
    /// <see cref="MigrationScripts"/>.</para>
    /// </summary>
    private static UpgradeEngine BuildUpgradeEngine(SqliteConnection connection)
        => DeployChanges.To
            .SqliteDatabase(new SharedConnection(connection))
            .WithScripts(MigrationScripts.All)
            .WithTransactionPerScript()
            .WithVariablesDisabled()
            .LogToNowhere()
            .Build();
}
