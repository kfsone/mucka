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

    /// <summary>Opens a connection, creating the file and directory if needed, and guarantees the
    /// schema is present and current. Safe to call concurrently from several threads: schema creation
    /// is idempotent (every statement is IF NOT EXISTS) and runs inside a transaction.</summary>
    public static SqliteConnection Open(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var connection = new SqliteConnection(ConnectionString(path));
        connection.Open();

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

        ApplySchema(connection);
        return connection;
    }

    /// <summary>Opens read-only. Throws if the file does not exist.</summary>
    public static SqliteConnection OpenRead(string path)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        return connection;
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
    /// <para>Safe to call on a file at any released shape, including one several versions behind -
    /// skipping versions is the ordinary path, not a special case. Safe to call repeatedly; a file
    /// already current does no work beyond reading its journal.</para>
    /// </summary>
    public static void ApplySchema(SqliteConnection connection)
    {
        // Before the journal exists, and only then: the two v0.20.0 column adds that no idempotent
        // script can express. Everything else, including the baseline, is an ordinary migration.
        if (!LegacySchemaAdopter.JournalExists(connection))
            LegacySchemaAdopter.Run(connection);

        var result = BuildUpgradeEngine(connection).PerformUpgrade();
        if (!result.Successful)
            throw new InvalidOperationException(
                $"Schema migration failed on '{result.ErrorScript?.Name ?? "(unknown script)"}'.",
                result.Error);
    }

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
