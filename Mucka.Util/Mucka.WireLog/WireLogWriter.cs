using System.Text;
using Microsoft.Data.Sqlite;
using Mucka.Store;

namespace Mucka.WireLog;

/// <summary>
/// The wire log: every byte of every session, raw, into <c>batches</c>. Always on - there is no
/// setting and nothing to arm.
///
/// <para><b>The tap and the batcher in one.</b> <see cref="RecordRx"/> is called from the socket read
/// loop and <see cref="RecordTx"/> from the write loop, concurrently; each stamps the time, tags the
/// direction, takes a lock, memcpies the payload into the open batch buffer and compares two longs.
/// When that buffer crosses <see cref="MaxBatchBytes"/>, or the record just appended is
/// <see cref="MaxBatchAge"/> past the batch's first, the batch is swapped out and handed to
/// <see cref="MuckaStore"/>. Nothing on the caller's thread ever touches SQLite.</para>
///
/// <para>The age bound is enforced <i>on the record's own timestamp</i>, in <see cref="Emit"/>, so it
/// fires exactly at the bound rather than at the next tick of a timer. The housekeeping timer below
/// exists only for the case that cannot see: a batch that has some records in it and then goes
/// completely quiet. That batch is by definition small, so the timer's coarseness costs nothing.</para>
///
/// <para><b>What a crash costs.</b> At most the open batch: everything already handed to the store is
/// in a committed WAL transaction, and everything still in the builder is in memory only. That is
/// bounded by the two constants below - 64 KB of traffic or one minute of it, whichever comes first,
/// and at MUD2's measured 0.191 KB/s the minute is what fires. <see cref="Flush"/> closes the open
/// batch and is called on every path that ends a connection - the graceful disconnect and the server
/// drop alike - so a normal end of session loses nothing at all; only a hard kill or a power cut can
/// reach the window.</para>
///
/// <para>With nothing compressed, batch length only needs to amortise SQLite's ~0.74 KB of per-row
/// overhead, which a minute of MUD2 (about 12 KB) already reduces to noise.</para>
/// </summary>
public sealed class WireLogWriter : IDisposable
{
    /// <summary>Close the batch once the framed buffer reaches this. At MUD2's measured 0.191 KB/s this
    /// takes about 5.6 minutes to reach, so in ordinary play <see cref="MaxBatchAge"/> is the bound that
    /// fires; this one catches a busy fight, where a minute's traffic is far above the average.</summary>
    public const int MaxBatchBytes = 64 * 1024;

    /// <summary>Close the batch once the record being appended is this far past the batch's first.</summary>
    public static readonly TimeSpan MaxBatchAge = TimeSpan.FromMinutes(1);

    private static readonly long MaxBatchAgeMs = (long)MaxBatchAge.TotalMilliseconds;
    private static readonly TimeSpan HousekeepingPeriod = TimeSpan.FromSeconds(30);

    private readonly object _lock = new();
    private readonly WireLogBatchBuilder _builder = new();
    private readonly MuckaStore _store;
    private readonly Timer _housekeeping;

    private int _seq;
    private volatile bool _disposed;

    public WireLogWriter(MuckaStore store)
    {
        _store = store;
        _housekeeping = new Timer(_ => CloseIfStale(), null, HousekeepingPeriod, HousekeepingPeriod);
    }

    public void RecordRx(ReadOnlySpan<byte> data) => Emit(WireDirection.Rx, data);

    public void RecordTx(byte[] data) => Emit(WireDirection.Tx, data);

    /// <summary>Writes a free-text client-side note into the log - the dreamword, a reset, the
    /// confirmed terminal width. Cheap enough that callers sprinkle these unconditionally.</summary>
    public void Annotate(string message)
    {
        // UTF-8 rather than Latin-1: an annotation is a .NET string, and UTF-8 round-trips all of
        // them. See the encoding contract on WireRecord.
        Emit(WireDirection.Annotation, Encoding.UTF8.GetBytes(message));
    }

    /// <summary>Closes the open batch. Called on disconnect and on dispose; a no-op when the batch is
    /// empty. Never throws - a recorder must not break the session.</summary>
    public void Flush()
    {
        WireBatchRow? ready;
        lock (_lock) ready = TakeBatchLocked();
        if (ready is not null)
            _store.Enqueue(ready);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _housekeeping.Dispose();
        Flush();
    }

    private void Emit(WireDirection direction, ReadOnlySpan<byte> payload)
    {
        if (_disposed) return;
        var timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        WireBatchRow? ready = null;
        lock (_lock)
        {
            _builder.Append(direction, timestampMs, payload);
            if (_builder.Length >= MaxBatchBytes ||
                timestampMs - _builder.BaseTimestampMs >= MaxBatchAgeMs)
                ready = TakeBatchLocked();
        }
        // Outside the lock: the caller here is a socket loop and there is no reason to hold the buffer
        // lock across the handover.
        if (ready is not null)
            _store.Enqueue(ready);
    }

    /// <summary>Swaps the open batch out if it has anything in it. Caller holds <see cref="_lock"/>.</summary>
    private WireBatchRow? TakeBatchLocked()
    {
        if (_builder.IsEmpty) return null;
        var batch = new WireBatchRow(_store.SessionId, _seq++, _builder.BaseTimestampMs,
            _builder.LastTimestampMs, _builder.Count, _builder.ToArray());
        _builder.Reset();
        return batch;
    }

    /// <summary>The idle case the age check in <see cref="Emit"/> cannot reach: a batch with records in
    /// it and no traffic since. Its age is against the wall clock, because there is no next record to
    /// take a timestamp from.</summary>
    private void CloseIfStale()
    {
        if (_disposed) return;
        WireBatchRow? ready = null;
        lock (_lock)
        {
            if (!_builder.IsEmpty &&
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _builder.BaseTimestampMs >= MaxBatchAgeMs)
                ready = TakeBatchLocked();
        }
        if (ready is not null)
            _store.Enqueue(ready);
    }
}

/// <summary>One closed batch, on its way to <c>batches</c>. The framed buffer goes in as-is: the
/// payloads inside it are the server's own bytes and nothing here transforms them.</summary>
internal sealed record WireBatchRow(long SessionId, int Seq, long BaseTsMs, long LastTsMs,
    int Records, byte[] Framed) : IStoreRow
{
    private const string Sql = """
        INSERT INTO batches (session_id, seq, base_ts_ms, last_ts_ms, records, data)
        VALUES ($session, $seq, $base, $last, $records, $data);
        """;

    public void Write(StoreWrite write)
    {
        var command = write.Prepared(Sql);
        command.Parameters.AddWithValue("$session", SessionId);
        command.Parameters.AddWithValue("$seq", Seq);
        command.Parameters.AddWithValue("$base", BaseTsMs);
        command.Parameters.AddWithValue("$last", LastTsMs);
        command.Parameters.AddWithValue("$records", Records);
        command.Parameters.Add("$data", SqliteType.Blob).Value = Framed;
        command.ExecuteNonQuery();
    }
}
