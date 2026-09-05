using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace Mucka.Core;

/// <summary>
/// The wire log's SQLite backend: raw payload bytes, batched, compressed, one row per batch in
/// <see cref="WireLogDb"/>.
///
/// <para><b>Shape of the work.</b> <see cref="Record"/> is on the read and write loops and does nothing
/// but take a lock, memcpy the payload into the open batch buffer and compare two longs. When that
/// buffer crosses <see cref="MaxBatchBytes"/>, or the record just appended is <see cref="MaxBatchAge"/>
/// past the batch's first, it is swapped out and handed to a single background task, which does the
/// compression and the INSERT. Nothing on the caller's thread ever compresses or touches SQLite.</para>
///
/// <para>The age bound is enforced <i>on the record's own timestamp</i>, in <see cref="Record"/>, so it
/// fires exactly at the bound rather than at the next tick of a timer. The housekeeping timer below
/// exists only for the case <see cref="Record"/> cannot see: a batch that has some records in it and
/// then goes completely quiet. That batch is by definition small, so the timer's coarseness costs
/// nothing.</para>
///
/// <para><b>What a crash costs.</b> At most the open batch: everything already handed to the writer is
/// in a committed WAL transaction, and everything still in the builder is in memory only. That is
/// bounded by the two constants below — 64 KB of traffic or one minute of it, whichever comes first,
/// and at MUD2's measured 0.19 KB/s the minute is what fires. <see cref="Flush"/> closes the open batch
/// and is called on stop, on dispose, and on every path that ends a connection — the graceful
/// disconnect and the server drop alike — so a normal end of session loses nothing at all; only a hard
/// kill or a power cut can reach the window.</para>
///
/// <para>One minute rather than five is a paid-for trade, not a free one: see the measured table on
/// <see cref="WireLogFraming"/>. It costs about 0.04 GB a year to shrink the worst case from five
/// minutes of lost traffic to one.</para>
///
/// <para><b>Best-effort throughout.</b> Every failure path here swallows and reports; nothing in a
/// diagnostic recorder may take the client down or stall the socket. Two failure modes are handled
/// explicitly rather than swallowed, because both were silent and unbounded:</para>
/// <list type="bullet">
///   <item><description><b>A dead writer.</b> If the background task falls over — the disk fills, the
///   file is deleted underneath it — it sets <see cref="IsFaulted"/>, drops whatever is still queued,
///   and <see cref="Record"/> stops accepting. Retrying was rejected: if the database cannot be written
///   at all, a retry turns one failure into one a minute forever, and a diagnostic log is not worth
///   that. Continuing to accept was rejected harder — that is a queue with no reader, which is a memory
///   leak whose size is the rest of the session.</description></item>
///   <item><description><b>A wedged writer.</b> Not dead, just far behind (SQLite blocked on a lock, a
///   stalled disk). The queue is bounded at <see cref="MaxQueuedBatches"/> and drops the OLDEST batch
///   when full, so the backlog can never outgrow that. A drop is not silent: it increments
///   <see cref="DroppedBatches"/>, reports once, and leaves a hole in <c>batches.seq</c>, which is
///   exactly the evidence that column exists for.</description></item>
/// </list>
/// </summary>
public sealed class SqliteWireLogSink : IWireLogSink
{
    /// <summary>Close the batch once the framed buffer reaches this. At MUD2's measured 0.19 KB/s this
    /// takes about 5.6 minutes to reach, so in ordinary play <see cref="MaxBatchAge"/> is the bound that
    /// fires; this one catches a busy fight, where a minute's traffic is far above the average.</summary>
    public const int MaxBatchBytes = 64 * 1024;

    /// <summary>Close the batch once the record being appended is this far past the batch's first.
    /// One minute: see the measured ratio table on <see cref="WireLogFraming"/>, and the note above on
    /// what the choice costs.</summary>
    public static readonly TimeSpan MaxBatchAge = TimeSpan.FromMinutes(1);

    /// <summary>Hard cap on batches awaiting the writer. At the bounds above a batch is ~12 KB framed,
    /// so this is under a megabyte of backlog and about an hour of play — far more slack than a healthy
    /// writer ever needs, and a ceiling a wedged one cannot climb past.</summary>
    public const int MaxQueuedBatches = 256;

    private static readonly long MaxBatchAgeMs = (long)MaxBatchAge.TotalMilliseconds;
    private static readonly TimeSpan HousekeepingPeriod = TimeSpan.FromSeconds(30);

    private readonly object _lock = new();
    private readonly WireLogBatchBuilder _builder = new();
    private readonly Channel<PendingBatch> _writeQueue;
    private readonly Task _writerTask;
    private readonly Timer _housekeeping;
    private readonly Action<string, Exception>? _onError;
    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly long _sessionId;

    private int _seq;
    private int _dropped;
    private int _dropReported;
    private volatile bool _disposed;
    private volatile bool _faulted;

    private sealed record PendingBatch(int Seq, long BaseTsMs, long LastTsMs, int Records, byte[] Framed);

    /// <summary>
    /// Opens a wire log at <paramref name="dbPath"/> and starts a session against
    /// <paramref name="host"/>.
    ///
    /// <para><b>The database is opened here, on the caller's thread, and this throws if it cannot be.</b>
    /// It used to open lazily on the first batch, which meant the only report of a broken wire log was a
    /// line in the crash log some minutes later — and this is a feature whose whole design goal is that
    /// the owner turns it on once and never looks at it again. A failure that is not reported at start
    /// is a failure that is never reported. The cost of being eager is one empty <c>sessions</c> row and
    /// a created file for a connection that records nothing, which is a fair price and arguably the more
    /// honest record.</para>
    /// </summary>
    /// <exception cref="SqliteException">The database cannot be opened or written.</exception>
    public SqliteWireLogSink(string dbPath, string host, string? clientVersion = null,
        Action<string, Exception>? onError = null)
    {
        _dbPath = dbPath;
        _onError = onError;
        _writeQueue = Channel.CreateBounded<PendingBatch>(
            new BoundedChannelOptions(MaxQueuedBatches)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest,
            },
            itemDropped: OnBatchDropped);

        var connection = WireLogDb.Open(dbPath);
        try
        {
            _sessionId = InsertSession(connection, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                host, clientVersion);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
        _connection = connection;

        _writerTask = Task.Run(DrainAsync);
        _housekeeping = new Timer(_ => CloseIfStale(), null, HousekeepingPeriod, HousekeepingPeriod);
    }

    public string Location => _dbPath;

    /// <summary>True once the background writer has died. Nothing more is recorded after this; the
    /// failure has been handed to the error callback.</summary>
    public bool IsFaulted => _faulted;

    /// <summary>Batches thrown away because the writer could not keep up. Nonzero means holes in
    /// <c>batches.seq</c>.</summary>
    public int DroppedBatches => Volatile.Read(ref _dropped);

    public void Record(WireDirection direction, long timestampMs, ReadOnlySpan<byte> payload)
    {
        if (_disposed || _faulted) return;
        PendingBatch? ready = null;
        lock (_lock)
        {
            _builder.Append(direction, timestampMs, payload);
            if (_builder.Length >= MaxBatchBytes ||
                timestampMs - _builder.BaseTimestampMs >= MaxBatchAgeMs)
                ready = TakeBatchLocked();
        }
        // Outside the lock: TryWrite on this channel never blocks (it drops rather than waits), but the
        // caller here is the socket read loop and there is no reason to hold the buffer lock across it.
        if (ready is not null)
            _writeQueue.Writer.TryWrite(ready);
    }

    public void Flush()
    {
        if (_faulted) return;
        PendingBatch? ready;
        lock (_lock) ready = TakeBatchLocked();
        if (ready is not null)
            _writeQueue.Writer.TryWrite(ready);
    }

    /// <summary>Closes the open batch, drains the writer, and stamps the session's end time.
    /// Blocks briefly (bounded) so an app exit cannot lose what is already buffered — the same
    /// shutdown contract SwingLedger and FightHistoryStore use.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _housekeeping.Dispose();
        Flush();
        _writeQueue.Writer.TryComplete();
        try
        {
            _writerTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Best-effort: Dispose must never throw during shutdown.
        }
    }

    /// <summary>Swaps the open batch out if it has anything in it. Caller holds <see cref="_lock"/>.</summary>
    private PendingBatch? TakeBatchLocked()
    {
        if (_builder.IsEmpty) return null;
        var batch = new PendingBatch(_seq++, _builder.BaseTimestampMs, _builder.LastTimestampMs,
            _builder.Count, _builder.ToArray());
        _builder.Reset();
        return batch;
    }

    /// <summary>The idle case the age check in <see cref="Record"/> cannot reach: a batch with records
    /// in it and no traffic since. Its age is against the wall clock, because there is no next record to
    /// take a timestamp from.</summary>
    private void CloseIfStale()
    {
        if (_disposed || _faulted) return;
        PendingBatch? ready = null;
        lock (_lock)
        {
            if (!_builder.IsEmpty &&
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _builder.BaseTimestampMs >= MaxBatchAgeMs)
                ready = TakeBatchLocked();
        }
        if (ready is not null)
            _writeQueue.Writer.TryWrite(ready);
    }

    /// <summary>Called by the channel itself when the bound above forces a batch out. Counts every one
    /// and reports the first — a report per drop would be its own flood.</summary>
    private void OnBatchDropped(PendingBatch batch)
    {
        var total = Interlocked.Increment(ref _dropped);
        if (Interlocked.Exchange(ref _dropReported, 1) == 0)
            _onError?.Invoke("SqliteWireLogSink.Drop", new InvalidOperationException(
                $"Wire-log writer fell {MaxQueuedBatches} batches behind; dropping oldest. " +
                $"First dropped: seq {batch.Seq}, {batch.Records} records. Total dropped so far: {total}."));
    }

    /// <summary>The single background writer. Owns the only write connection for this session's
    /// lifetime — opened in the constructor, so a database that cannot be written is reported to the
    /// caller at start rather than discovered here. <c>WaitToReadAsync</c> completes only once the queue
    /// is both closed and drained, which is the "nothing queued is lost on shutdown" property
    /// <see cref="Dispose"/> relies on.</summary>
    private async Task DrainAsync()
    {
        var reader = _writeQueue.Reader;
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
                WriteBatches(_connection, _sessionId, reader);
            CloseSession(_connection, _sessionId);
        }
        catch (Exception ex)
        {
            // Abandoned rather than retried, matching SwingLedger: if the database cannot be written at
            // all, retrying per batch turns one failure into one every minute, forever. But abandoning
            // has to mean abandoning BOTH ends — before this flag existed, Record and Flush kept feeding
            // a channel with no reader, and the wire log's failure mode was to eat memory for the rest
            // of the session.
            _faulted = true;
            DiscardQueued(reader);
            _onError?.Invoke("SqliteWireLogSink.Drain (wire log stopped for this session)", ex);
        }
        finally
        {
            _connection.Dispose();
        }
    }

    /// <summary>Empties the queue after a fault so the batches already in it can be collected. Without
    /// this, completing the channel still holds every queued blob alive.</summary>
    private void DiscardQueued(ChannelReader<PendingBatch> reader)
    {
        _writeQueue.Writer.TryComplete();
        while (reader.TryRead(out _)) { }
    }

    private static long InsertSession(SqliteConnection connection, long startedMs, string host,
        string? clientVersion)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO sessions (started_ms, host, client_version) VALUES ($started, $host, $version); " +
            "SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$started", startedMs);
        command.Parameters.AddWithValue("$host", host);
        command.Parameters.AddWithValue("$version", (object?)clientVersion ?? DBNull.Value);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void CloseSession(SqliteConnection connection, long sessionId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE sessions SET ended_ms = $ended WHERE id = $id;";
        command.Parameters.AddWithValue("$ended", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$id", sessionId);
        command.ExecuteNonQuery();
    }

    private const string InsertSql = """
        INSERT INTO batches (session_id, seq, base_ts_ms, last_ts_ms, records, raw_bytes, codec, data)
        VALUES ($session, $seq, $base, $last, $records, $raw, $codec, $data);
        """;

    /// <summary>Writes everything already queued in one transaction. Compression happens here, on this
    /// background thread, never on a caller's.</summary>
    private void WriteBatches(SqliteConnection connection, long sessionId, ChannelReader<PendingBatch> reader)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = InsertSql;
        var session = command.Parameters.Add("$session", SqliteType.Integer);
        var seq     = command.Parameters.Add("$seq",     SqliteType.Integer);
        var baseTs  = command.Parameters.Add("$base",    SqliteType.Integer);
        var lastTs  = command.Parameters.Add("$last",    SqliteType.Integer);
        var records = command.Parameters.Add("$records", SqliteType.Integer);
        var raw     = command.Parameters.Add("$raw",     SqliteType.Integer);
        var codec   = command.Parameters.Add("$codec",   SqliteType.Integer);
        var data    = command.Parameters.Add("$data",    SqliteType.Blob);
        session.Value = sessionId;

        while (reader.TryRead(out var batch))
        {
            var (usedCodec, blob) = WireLogFraming.Compress(batch.Framed);
            seq.Value     = batch.Seq;
            baseTs.Value  = batch.BaseTsMs;
            lastTs.Value  = batch.LastTsMs;
            records.Value = batch.Records;
            raw.Value     = batch.Framed.Length;
            codec.Value   = (int)usedCodec;
            data.Value    = blob;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }
}
