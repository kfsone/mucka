using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace Mucka.Store;

/// <summary>
/// The one owner of the one database. Opens <c>~/.mucka/mucka.db</c>, writes its <c>mucka_runs</c> row,
/// and runs the single background task that owns the only write connection for as long as the store
/// lives. Every producer - the swing ledger, the fight history store, the clog writer, the wire log -
/// builds a row on its own thread and hands it over; none of them touches SQLite.
///
/// <para><b>Lifetime is one connection to MUD2</b>, not one app run: MuckaConnection is constructed per
/// connection attempt and constructs this with itself. One store is one <c>mucka_runs</c> row, which spans every login inside it.</para>
///
/// <para><b>Threading.</b> <see cref="Enqueue"/> is called from the socket read loop, the socket write
/// loop and the Feed thread, concurrently. It serialises nothing and does no I/O - it is a channel
/// write. Exactly one task reads, batching whatever is queued into one transaction. This is Invariant
/// #1 at the storage layer.</para>
///
/// <para><b>The queue is unbounded.</b> The bound it used to have defended against a writer wedged
/// behind SQLite's own lock, which was a real failure when four writers shared the file; with one
/// writer there is no such lock to lose. What is left is a stalled disk, and at the measured average
/// wire rate of 0.156 KB/s (13 sessions, 18.11 play-hours, 10-13 Sep 2026) an hour of total write
/// stall is about half a megabyte of backlog.</para>
///
/// <para><b>A dead writer stops accepting.</b> If the task falls over - the disk fills, the file is
/// deleted underneath it - <see cref="IsFaulted"/> is set, whatever is queued is discarded and
/// <see cref="Enqueue"/> becomes a no-op. Retrying is not done: if the database cannot be written at
/// all, a retry turns one failure into one per row forever. Continuing to accept would be worse - a
/// queue with no reader is a memory leak whose size is the rest of the session.</para>
/// </summary>
public sealed class MuckaStore : IDisposable
{
    /// <summary>How long <see cref="Dispose"/> waits for the writer to drain. Bounded so an app exit
    /// cannot hang; long enough that the handful of rows normally in flight always land.</summary>
    public static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    private readonly Channel<IStoreRow> _queue =
        Channel.CreateUnbounded<IStoreRow>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly string _path;
    private readonly SqliteConnection? _connection;
    private readonly StoreWrite? _write;
    private readonly Task? _writerTask;
    private readonly Action<string, Exception>? _onError;

    private volatile bool _disposed;
    private volatile bool _faulted;

    /// <summary>
    /// Opens the store and begins a session against <paramref name="host"/>.
    ///
    /// <para><b>The database is opened here, on the caller's thread, and a failure to open it is
    /// reported here.</b> A failure that is not reported at start is a failure that is never reported:
    /// this is a store that is switched on once and then trusted forever. The session row is written
    /// before the writer task starts, so <see cref="SessionId"/> is available immediately and nothing
    /// has to reach into the connection behind the writer's back.</para>
    ///
    /// <para>A store that could not be opened does not throw and does not block the connection - the
    /// point of the client is to play MUD2. It comes up <see cref="IsFaulted"/>, and every
    /// <see cref="Enqueue"/> is a no-op for the rest of the session.</para>
    /// </summary>
    public MuckaStore(string path, string host, string? clientVersion = null,
        Action<string, Exception>? onError = null)
    {
        _path = path;
        _onError = onError;

        SqliteConnection? connection = null;
        try
        {
            connection = MuckaDb.Open(path);
            SessionId = InsertSession(connection, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                host, clientVersion);
        }
        catch (Exception ex)
        {
            connection?.Dispose();
            _faulted = true;
            _onError?.Invoke("MuckaStore.Open (nothing will be recorded this session)", ex);
            return;
        }

        _connection = connection;
        _write = new StoreWrite(connection);
        _writerTask = Task.Run(DrainAsync);
    }

    /// <summary>Where this store is writing. Shown to the player.</summary>
    public string Path => _path;

    /// <summary>This store's row in <c>mucka_runs</c> - one run of the client, spanning every login,
    /// logout and world reset inside it. What <c>wire.mucka_run_id</c> points at, and what a
    /// <c>persona_sessions</c> row belongs to.</summary>
    public long SessionId { get; }

    /// <summary>True once the background writer has died. Nothing more is recorded after this; the
    /// failure has been handed to the error callback.</summary>
    public bool IsFaulted => _faulted;

    /// <summary>
    /// Opens a persona session - one login of one character - and returns its id, which every row
    /// recorded until the next login carries. Null if the store is faulted or the insert fails; a
    /// recording gap is preferable to taking the connection down.
    ///
    /// <para><b>Its own short-lived connection, not the writer's.</b> The id has to come back
    /// immediately so rows can be stamped with it, and the writer's connection is deliberately not
    /// reachable from outside the drain loop - see the constructor. This runs once per login, not on
    /// any hot path, and WAL plus the 5-second busy timeout cover the overlap with the writer.</para>
    /// </summary>
    public long? BeginPersonaSession(long startedMs, string? host)
    {
        if (_disposed || _faulted) return null;
        try
        {
            using var connection = new SqliteConnection(MuckaDb.ConnectionString(_path));
            connection.Open();
            Execute(connection, "PRAGMA busy_timeout=5000;");
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO persona_sessions (mucka_run_id, started_ms) VALUES ($run, $started); " +
                "SELECT last_insert_rowid();";
            command.Parameters.AddWithValue("$run", SessionId);
            command.Parameters.AddWithValue("$started", startedMs);
            return Convert.ToInt64(command.ExecuteScalar());
        }
        catch (Exception ex)
        {
            _onError?.Invoke("MuckaStore.BeginPersonaSession (this login will not be attributed)", ex);
            return null;
        }
    }

    /// <summary>Names the character on an open persona session, once the post-login score reply has
    /// identified it. Separate from <see cref="BeginPersonaSession"/> because game mode is entered
    /// before the game says who you are.</summary>
    public void NamePersonaSession(long personaSessionId, string persona)
        => UpdatePersonaSession(personaSessionId,
            "UPDATE persona_sessions SET persona = $value WHERE id = $id;", persona,
            "MuckaStore.NamePersonaSession");

    /// <summary>Closes a persona session. <paramref name="note"/> is advisory only - see the column
    /// comment in 0003_persona_sessions.sql; a crash leaves both it and ended_ms empty.</summary>
    public void EndPersonaSession(long personaSessionId, long endedMs, string? note)
    {
        UpdatePersonaSession(personaSessionId,
            "UPDATE persona_sessions SET ended_ms = $ended, ended_note = $value WHERE id = $id;",
            note, "MuckaStore.EndPersonaSession", endedMs);
    }

    private void UpdatePersonaSession(long id, string sql, string? value, string what, long? endedMs = null)
    {
        if (_disposed || _faulted) return;
        try
        {
            using var connection = new SqliteConnection(MuckaDb.ConnectionString(_path));
            connection.Open();
            Execute(connection, "PRAGMA busy_timeout=5000;");
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$value", (object?)value ?? DBNull.Value);
            if (endedMs is long ended)
                command.Parameters.AddWithValue("$ended", ended);
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _onError?.Invoke($"{what} (the session row stays as it is)", ex);
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Hands one row to the writer. Cheap, thread-safe, never blocks, never throws.</summary>
    public void Enqueue(IStoreRow row)
    {
        if (_disposed || _faulted) return;
        _queue.Writer.TryWrite(row);
    }

    /// <summary>Stops accepting, drains what is queued and stamps the session's end time. Blocks up
    /// to <see cref="DrainTimeout"/>, so an app exit cannot lose what is buffered and cannot hang on
    /// it either.
    ///
    /// <para>What that does NOT cover: a producer that passed the <c>_disposed</c> check just before
    /// this ran can call <c>TryWrite</c> after the channel is completed, and that row is dropped
    /// silently. It is bounded to whatever one producer emits in the instant of shutdown, and
    /// <c>MuckaConnection</c> disposes every writer before the store precisely so the producers have
    /// stopped first. Closing the hole properly means a lock on the enqueue path, which is a lock on
    /// the socket threads' path, and that is a worse trade than losing a record at disconnect.</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.Writer.TryComplete();
        try
        {
            _writerTask?.Wait(DrainTimeout);
        }
        catch
        {
            // Best-effort: Dispose must never throw during shutdown.
        }
    }

    /// <summary>The single background writer. Owns the only write connection for the store's lifetime.
    /// <c>WaitToReadAsync</c> completes only once the queue is both closed and drained, which is the
    /// "nothing queued is lost on shutdown" property <see cref="Dispose"/> relies on.</summary>
    private async Task DrainAsync()
    {
        var reader = _queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
                WriteBatch(reader);
            CloseSession();
        }
        catch (Exception ex)
        {
            _faulted = true;
            var discarded = DiscardQueued(reader);
            // The batch that threw rolled back whole - one transaction, so the rows before the bad
            // one in it are gone too, not just the ones behind it in the queue.
            _onError?.Invoke(
                $"MuckaStore.Drain (recording stopped for this session; {discarded} queued rows dropped)",
                ex);
        }
        finally
        {
            _write!.DisposeCommands();
            _connection!.Dispose();
        }
    }

    /// <summary>
    /// Writes everything already queued in one transaction.
    ///
    /// <para><b>A row that throws takes the store down with it</b>, through <see cref="DrainAsync"/>'s
    /// handler: one failure policy, not two. The writers this replaced caught per row and carried on,
    /// which reads as resilience and is not - with one arbiter the only two things that make a row
    /// throw are a dead database and a schema bug, and swallowing either produces an error per row
    /// forever with nothing that ever says the recording has stopped being trustworthy. Faulting
    /// reports once, to the terminal, at the moment it happens.</para>
    /// </summary>
    private void WriteBatch(ChannelReader<IStoreRow> reader)
    {
        using var transaction = _connection!.BeginTransaction();
        _write!.Begin(transaction);

        var written = 0;
        while (reader.TryRead(out var row))
        {
            try
            {
                row.Write(_write);
            }
            catch (Exception ex)
            {
                // Name the row type. The two things that make a row throw are a dead database and a
                // schema bug, and for a schema bug the type IS the answer - "FOREIGN KEY constraint
                // failed" with nothing else said costs an instrumented rebuild to turn into "the
                // swings insert". The batch still dies; it just says what killed it.
                throw new InvalidOperationException(
                    $"{row.GetType().Name} could not be written: {ex.Message}", ex);
            }
            written++;
        }

        if (written > 0)
            transaction.Commit();
    }

    /// <summary>Empties the queue after a fault so the rows already in it can be collected. Without
    /// this, completing the channel still holds every queued blob alive. Returns how many rows went
    /// with it, so the one error this store ever reports can say what the recording actually lost -
    /// the rows behind the bad one are unrelated to it and are dropped anyway, and a silent count of
    /// zero reads identically to a silent count of four hundred.</summary>
    private int DiscardQueued(ChannelReader<IStoreRow> reader)
    {
        _queue.Writer.TryComplete();
        var discarded = 0;
        while (reader.TryRead(out _))
            discarded++;
        return discarded;
    }

    private static long InsertSession(SqliteConnection connection, long startedMs, string host,
        string? clientVersion)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO mucka_runs (started_ms, host, client_version) VALUES ($started, $host, $version); " +
            "SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$started", startedMs);
        command.Parameters.AddWithValue("$host", host);
        command.Parameters.AddWithValue("$version", (object?)clientVersion ?? DBNull.Value);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private void CloseSession()
    {
        using var command = _connection!.CreateCommand();
        command.CommandText = "UPDATE mucka_runs SET ended_ms = $ended WHERE id = $id;";
        command.Parameters.AddWithValue("$ended", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$id", SessionId);
        command.ExecuteNonQuery();
    }
}
