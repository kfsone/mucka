using Microsoft.Data.Sqlite;

namespace Mucka.WireLog;

/// <summary>
/// The wire-log database: <c>~/.mucka/wire/wire.db</c>, holding whole sessions of raw socket traffic as
/// batches of length-prefixed records. The payload bytes are the server's own, verbatim and
/// uncompressed - see <see cref="WireLogFraming"/> for the byte layout and the rationale.
///
/// <para><b>Why this is its own file and not a table in <see cref="CombatDb"/>.</b> The combat database
/// argues for one file on the grounds that swings and fights JOIN - an analysis view walks from a fight
/// to the blows inside it. Nothing here joins to anything there. The only relation between a wire byte
/// and a swing row is the timestamp, which works as well across two files as within one (and SQLite can
/// <c>ATTACH</c> if it ever needs to be a real query). With no join to buy, the differences decide it,
/// and they all point the same way:</para>
/// <list type="bullet">
///   <item><description><b>Growth.</b> This grows without bound - every byte of every session, about
///   0.76 MB per play-hour stored plain (MB = 10^6; see <see cref="WireLogFraming"/> for the
///   arithmetic), so roughly 1.1 GB a year at four hours of daily play. The combat database is
///   thousands of small rows and is read interactively. Interleaving multi-kilobyte blob pages through
///   a file whose queries want to stay in page cache makes the small, frequent, latency-sensitive reads
///   pay for the large, rare, latency-indifferent writes.</description></item>
///   <item><description><b>Blast radius.</b> The combat corpus is months of irreplaceable observation and
///   is the only evidence behind every figure the client shows; CombatDb's own header says a schema
///   change may not cost it. The wire log is a diagnostic artifact that can be deleted at any time
///   without losing anything the client depends on. Those two want opposite handling, and "delete the
///   wire log" must never be an operation that can touch the combat data.</description></item>
///   <item><description><b>Writers.</b> CombatDb already carries two independent writers and needed a
///   five-second busy timeout to stop them dropping each other's batches. A third writer committing a
///   ~12 KB blob every minute is more lock time on a file whose readers are on the analysis
///   path.</description></item>
/// </list>
///
/// <para><b>Threading.</b> One connection, owned by <see cref="SqliteWireLogSink"/>'s single background
/// task. Readers (the exporter, and whatever the lab tool becomes) open their own short-lived
/// connection. WAL, for the same reason CombatDb uses it.</para>
/// </summary>
public static class WireLogDb
{
    /// <summary>Standard file name. The DIRECTORY comes from the caller (ClogPaths.GetWireLogDirectory
    /// owns the platform lookup) so this type stays free of MAUI references and can be tested from
    /// Mucka.Util.Tests against a temp path - the same split CombatDb already uses.</summary>
    public const string DefaultFileName = "wire.db";

    public static string ConnectionString(string path)
        => new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();

    /// <summary>Opens the database read/write, creating file, directory and schema as needed.
    /// Idempotent - every schema statement is IF NOT EXISTS.</summary>
    public static SqliteConnection Open(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var connection = new SqliteConnection(ConnectionString(path));
        connection.Open();
        Execute(connection, "PRAGMA journal_mode=WAL;");
        // NORMAL, matching CombatDb: one fsync per checkpoint rather than per commit. The exposure is
        // the same thing the open batch already exposes and is discussed on SqliteWireLogSink.
        Execute(connection, "PRAGMA synchronous=NORMAL;");
        Execute(connection, "PRAGMA foreign_keys=ON;");
        Execute(connection, "PRAGMA busy_timeout=5000;");
        DiscardOnSchemaChange(connection);
        ApplySchema(connection);
        return connection;
    }

    /// <summary>Opens read-only, for the exporter. Throws if the file does not exist.</summary>
    public static SqliteConnection OpenRead(string path)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        return connection;
    }

    public static void ApplySchema(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = SchemaSql;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    /// <summary>The columns <c>batches</c> is expected to have, in order. The whole of the version
    /// check below - see <see cref="DiscardOnSchemaChange"/>.</summary>
    private static readonly string[] ExpectedBatchColumns =
        ["id", "session_id", "seq", "base_ts_ms", "last_ts_ms", "records", "data"];

    /// <summary>
    /// <b>The wire log is disposable, so a schema change discards it rather than migrating it.</b>
    /// If <c>batches</c> exists and does not have exactly the columns this build writes, both tables
    /// and the view are dropped and recreated.
    ///
    /// <para>This is a standing policy, not a one-off migration, and it is stated as one so the next
    /// schema change does not grow a second special case. It is admissible only because of what this
    /// file is: every byte in it is a diagnostic record of traffic that has already been parsed, and
    /// the class remarks above commit to it being deletable at any moment without the client losing
    /// anything. <see cref="CombatDb"/> gets the opposite treatment and must keep it - that corpus is
    /// months of irreplaceable observation and no code may ever drop a table in it.</para>
    ///
    /// <para><c>CREATE TABLE IF NOT EXISTS</c> silently leaves an existing table alone, so without this
    /// check an insert against an old file with a different column set would fail a NOT NULL constraint
    /// on a column this build no longer knows about, and the wire log would fault out for the rest of
    /// the session with nothing but a swallowed exception to show why.</para>
    /// </summary>
    private static void DiscardOnSchemaChange(SqliteConnection connection)
    {
        var columns = ColumnsOf(connection, "batches");
        if (columns.Count == 0 || columns.SequenceEqual(ExpectedBatchColumns))
            return;

        Execute(connection, """
            DROP VIEW  IF EXISTS v_session_sizes;
            DROP TABLE IF EXISTS batches;
            DROP TABLE IF EXISTS sessions;
            """);
    }

    /// <summary>Column names of <paramref name="table"/> in declared order, or empty when there is no
    /// such table. <c>PRAGMA table_info</c> returns the name in column 1.</summary>
    private static List<string> ColumnsOf(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(1));
        return names;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Two tables. <c>sessions</c> is one row per connection; <c>batches</c> is the traffic, as runs of
    /// framed records (see <see cref="WireLogFraming"/> for the byte layout inside <c>data</c> - the
    /// server's own bytes with a couple of header bytes between them, and nothing compressed).
    ///
    /// <para><c>records</c> earns its space twice: it makes "what is this costing me" arithmetic on two
    /// columns rather than a decode pass, and - since the framing itself carries no redundancy whatever
    /// - it is the ONLY thing that can tell a damaged batch from a plausible one. It is enforced on
    /// every read; see <see cref="WireLogExport.ReadSession"/>. <c>seq</c> is per-session and gapless
    /// when nothing was lost: a hole in it is the visible evidence that a batch failed to write or was
    /// dropped by a wedged writer, which a blob table otherwise hides completely.</para>
    ///
    /// <para>There is no <c>codec</c> column and no <c>raw_bytes</c>. The first described a compressor
    /// that no longer exists; the second was the decompressed length, which with nothing compressed is
    /// exactly <c>LENGTH(data)</c> and so checked the blob against itself.</para>
    /// </summary>
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS sessions (
            id             INTEGER PRIMARY KEY,
            started_ms     INTEGER NOT NULL,   -- unix ms, when capture began
            ended_ms       INTEGER,            -- unix ms; NULL means the client died before closing it
            host           TEXT,               -- the server this session was against
            client_version TEXT                -- Mucka's version, so a decode can be told which client wrote it
        );

        CREATE TABLE IF NOT EXISTS batches (
            id          INTEGER PRIMARY KEY,
            session_id  INTEGER NOT NULL REFERENCES sessions(id),
            seq         INTEGER NOT NULL,   -- 0-based within the session; a gap means a batch was lost
            base_ts_ms  INTEGER NOT NULL,   -- absolute timestamp of the batch's FIRST record
            last_ts_ms  INTEGER NOT NULL,   -- absolute timestamp of its last, for range queries
            records     INTEGER NOT NULL,
            data        BLOB    NOT NULL    -- framed records; payloads verbatim, uncompressed
        );

        CREATE INDEX IF NOT EXISTS ix_batches_session ON batches(session_id, seq);
        CREATE INDEX IF NOT EXISTS ix_batches_ts      ON batches(base_ts_ms);

        -- What the log is costing, per session, without decoding anything.
        CREATE VIEW IF NOT EXISTS v_session_sizes AS
        SELECT s.id, s.started_ms, s.ended_ms, s.host,
               COUNT(b.id)          AS batches,
               SUM(b.records)       AS records,
               SUM(LENGTH(b.data))  AS stored_bytes
        FROM sessions s LEFT JOIN batches b ON b.session_id = s.id
        GROUP BY s.id;
        """;
}
