using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace Mucka.Core;

/// <summary>
/// The wire log's SQLite backend: raw payload bytes, batched, compressed, one row per batch in
/// <see cref="WireLogDb"/>.
///
/// <para><b>Shape of the work.</b> <see cref="Record"/> is on the read and write loops and does nothing
/// but take a lock and memcpy the payload into the open batch buffer. When that buffer crosses
/// <see cref="MaxBatchBytes"/> — or a 30-second housekeeping tick finds it older than
/// <see cref="MaxBatchAge"/> — it is swapped out and handed to a single background task, which does the
/// compression and the INSERT. Nothing on the caller's thread ever compresses or touches
/// SQLite.</para>
///
/// <para><b>What a crash costs.</b> At most the open batch: everything already handed to the writer is
/// in a committed WAL transaction, and everything still in the builder is in memory only. That is
/// bounded by the two constants below — 64 KB of traffic or five minutes of it, whichever comes first.
/// <see cref="Flush"/> closes the open batch and is called on stop, on disconnect and on dispose, so a
/// normal end of session loses nothing at all; only a hard kill or a power cut can reach the window.
/// This is a deliberate trade against compression: see the measured table on
/// <see cref="WireLogFraming"/>, where a five-SECOND batch compresses 2.6x and a five-MINUTE batch
/// 6.7x. A wire log is a diagnostic corpus, not the combat data — losing its last few minutes to a
/// crash costs far less than tripling its size forever.</para>
///
/// <para><b>Best-effort throughout.</b> Every failure path here swallows and reports; nothing in a
/// diagnostic recorder may take the client down or stall the socket.</para>
/// </summary>
public sealed class SqliteWireLogSink : IWireLogSink
{
    /// <summary>Close the batch once the framed buffer reaches this. At MUD2's measured ~0.19 KB/s
    /// this is reached in roughly 5.6 minutes of play, so it and <see cref="MaxBatchAge"/> are
    /// deliberately the same order — the size bound catches a busy fight, the age bound catches
    /// an idle stretch.</summary>
    public const int MaxBatchBytes = 64 * 1024;

    /// <summary>Close the batch once its first record is this old. Five minutes, not five seconds:
    /// see the measured ratio table on <see cref="WireLogFraming"/>.</summary>
    public static readonly TimeSpan MaxBatchAge = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan HousekeepingPeriod = TimeSpan.FromSeconds(30);

    private readonly object _lock = new();
    private readonly WireLogBatchBuilder _builder = new();
    private readonly Channel<PendingBatch> _writeQueue =
        Channel.CreateUnbounded<PendingBatch>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Task _writerTask;
    private readonly Timer _housekeeping;
    private readonly Action<string, Exception>? _onError;
    private readonly string _dbPath;
    private readonly string _host;
    private readonly string? _clientVersion;
    private readonly long _startedMs;

    private int _seq;
    private volatile bool _disposed;

    private sealed record PendingBatch(int Seq, long BaseTsMs, long LastTsMs, int Records, byte[] Framed);

    /// <summary>
    /// Opens (lazily — the file is not created until the first batch actually arrives) a wire log at
    /// <paramref name="dbPath"/> for a session against <paramref name="host"/>.
    /// </summary>
    public SqliteWireLogSink(string dbPath, string host, string? clientVersion = null,
        Action<string, Exception>? onError = null)
    {
        _dbPath = dbPath;
        _host = host;
        _clientVersion = clientVersion;
        _onError = onError;
        _startedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _writerTask = Task.Run(DrainAsync);
        _housekeeping = new Timer(_ => CloseIfStale(), null, HousekeepingPeriod, HousekeepingPeriod);
    }

    public string Location => _dbPath;

    public void Record(WireDirection direction, long timestampMs, ReadOnlySpan<byte> payload)
    {
        if (_disposed) return;
        PendingBatch? ready = null;
        lock (_lock)
        {
            _builder.Append(direction, timestampMs, payload);
            if (_builder.Length >= MaxBatchBytes)
                ready = TakeBatchLocked();
        }
        // Outside the lock: TryWrite on an unbounded channel never blocks, but the caller here is the
        // socket read loop and there is no reason to hold the buffer lock across it.
        if (ready is not null)
            _writeQueue.Writer.TryWrite(ready);
    }

    public void Flush()
    {
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

    private void CloseIfStale()
    {
        if (_disposed) return;
        PendingBatch? ready = null;
        lock (_lock)
        {
            if (!_builder.IsEmpty &&
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _builder.BaseTimestampMs >= (long)MaxBatchAge.TotalMilliseconds)
                ready = TakeBatchLocked();
        }
        if (ready is not null)
            _writeQueue.Writer.TryWrite(ready);
    }

    /// <summary>The single background writer. Owns the only write connection for this session's
    /// lifetime; opens it lazily on the first batch so a session that recorded nothing leaves no file
    /// behind. <c>WaitToReadAsync</c> completes only once the queue is both closed and drained, which
    /// is the "nothing queued is lost on shutdown" property <see cref="Dispose"/> relies on.</summary>
    private async Task DrainAsync()
    {
        SqliteConnection? connection = null;
        long sessionId = 0;
        var reader = _writeQueue.Reader;
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                if (connection is null)
                {
                    connection = WireLogDb.Open(_dbPath);
                    sessionId = InsertSession(connection);
                }
                WriteBatches(connection, sessionId, reader);
            }
            if (connection is not null)
                CloseSession(connection, sessionId);
        }
        catch (Exception ex)
        {
            // Abandoned rather than retried, matching SwingLedger: if the database cannot be written
            // at all, retrying per batch turns one failure into one every few minutes, forever.
            _onError?.Invoke("SqliteWireLogSink.Drain", ex);
        }
        finally
        {
            connection?.Dispose();
        }
    }

    private long InsertSession(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO sessions (started_ms, host, client_version) VALUES ($started, $host, $version); " +
            "SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$started", _startedMs);
        command.Parameters.AddWithValue("$host", _host);
        command.Parameters.AddWithValue("$version", (object?)_clientVersion ?? DBNull.Value);
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
