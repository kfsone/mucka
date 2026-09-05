using Microsoft.Data.Sqlite;

namespace Mucka.Core;

/// <summary>
/// The wire-log database: <c>~/.mucka/wire/wire.db</c>, holding whole sessions of raw socket traffic as
/// compressed batches of length-prefixed records.
///
/// <para><b>Why this is its own file and not a table in <see cref="CombatDb"/>.</b> The combat database
/// argues for one file on the grounds that swings and fights JOIN — an analysis view walks from a fight
/// to the blows inside it. Nothing here joins to anything there. The only relation between a wire byte
/// and a swing row is the timestamp, which works as well across two files as within one (and SQLite can
/// <c>ATTACH</c> if it ever needs to be a real query). With no join to buy, the differences decide it,
/// and they all point the same way:</para>
/// <list type="bullet">
///   <item><description><b>Growth.</b> This grows without bound — every byte of every session, a
///   measured 0.174 MB per play-hour compressed (MB = 10^6; see <see cref="WireLogFraming"/>), so about
///   0.25 GB a year at the owner's four-hours-a-day rate. The combat
///   database is thousands of small rows and is read interactively. Interleaving multi-kilobyte blob
///   pages through a file whose queries want to stay in page cache makes the small, frequent, latency-
///   sensitive reads pay for the large, rare, latency-indifferent writes.</description></item>
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
/// task. Readers (the exporter) open their own short-lived connection. WAL, for the same reason
/// CombatDb uses it.</para>
/// </summary>
public static class WireLogDb
{
    /// <summary>Standard file name. The DIRECTORY comes from the caller (ClogPaths.GetWireLogDirectory
    /// owns the platform lookup) so this type stays free of MAUI references and can be linked into
    /// mudsharp.Tests against a temp path — the same split CombatDb already uses.</summary>
    public const string DefaultFileName = "wire.db";

    public static string ConnectionString(string path)
        => new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();

    /// <summary>Opens the database read/write, creating file, directory and schema as needed.
    /// Idempotent — every schema statement is IF NOT EXISTS.</summary>
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

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Two tables. <c>sessions</c> is one row per connection; <c>batches</c> is the traffic, as
    /// compressed runs of framed records (see <see cref="WireLogFraming"/> for the byte layout inside
    /// <c>data</c>).
    ///
    /// <para><c>raw_bytes</c> and <c>records</c> earn their space three times over: they size the
    /// decoder's output buffer in one allocation, they make "what is this costing me" arithmetic on two
    /// columns rather than a decompression pass, and — since the framing itself carries no redundancy
    /// whatever — they are the ONLY thing that can tell a damaged batch from a plausible one. Both are
    /// enforced on every read; see <see cref="WireLogExport.ReadSession"/>. <c>seq</c> is per-session and
    /// gapless when nothing was lost: a hole in it is the visible evidence that a batch failed to write
    /// or was dropped by a wedged writer, which a blob table otherwise hides completely.</para>
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
            raw_bytes   INTEGER NOT NULL,   -- length of `data` decompressed
            codec       INTEGER NOT NULL,   -- WireLogCodec
            data        BLOB    NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_batches_session ON batches(session_id, seq);
        CREATE INDEX IF NOT EXISTS ix_batches_ts      ON batches(base_ts_ms);

        -- What the log is costing, per session, without decompressing anything.
        CREATE VIEW IF NOT EXISTS v_session_sizes AS
        SELECT s.id, s.started_ms, s.ended_ms, s.host,
               COUNT(b.id)          AS batches,
               SUM(b.records)       AS records,
               SUM(b.raw_bytes)     AS raw_bytes,
               SUM(LENGTH(b.data))  AS stored_bytes,
               CAST(SUM(b.raw_bytes) AS REAL) / NULLIF(SUM(LENGTH(b.data)), 0) AS ratio
        FROM sessions s LEFT JOIN batches b ON b.session_id = s.id
        GROUP BY s.id;
        """;
}
