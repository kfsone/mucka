using System.Text;
using Microsoft.Data.Sqlite;
using Mucka.Store;

namespace Mucka.WireLog;

/// <summary>
/// The wire log: every byte of every session, raw, into <c>wire</c>. Always on - there is no setting
/// and nothing to arm.
///
/// <para><b>The tap, and nothing else.</b> <see cref="RecordRx"/> is called from the socket read loop
/// and <see cref="RecordTx"/> from the write loop, concurrently. Each stamps the time, tags the
/// direction, copies the payload and hands one row to <see cref="MuckaStore"/>. That hand-off is a
/// <c>TryWrite</c> onto an unbounded channel; nothing on the caller's thread touches SQLite, allocates
/// a batch, or takes a lock that another socket thread holds (Invariant #1).</para>
///
/// <para><b>One row per record, and the arithmetic that says it is affordable.</b> Measured over 13
/// sessions, 18.11 play-hours, 10-13 Sep 2026: 119,921 records, 10,141,176 bytes of payload, 84 bytes
/// average, about 1.8 records a second. SQLite's per-row overhead is ~48 bytes there, so the corpus
/// stores in ~17.1 MB - 0.94 MB per play-hour, about 1.4 GB a year at four hours of daily play. The
/// alternative was packing runs of records into one blob behind a varint framing, which cost 0.63 MB
/// per play-hour and made <c>SELECT data</c> print the magic number instead of the line. See the
/// <c>wire</c> table's comment in MuckaDb for why legibility wins that trade.</para>
///
/// <para>The writes themselves are not per row: the store's single background task drains whatever has
/// queued and commits it as one transaction, so a busy second is one commit, not a hundred.</para>
///
/// <para><b>What a crash costs.</b> Whatever has not yet drained, which is bounded by how fast the
/// store's task gets to it rather than by any buffer here - there is no buffer here.
/// <see cref="Flush"/> exists for the shape of the call sites that end a connection and has nothing to
/// do; draining is <see cref="MuckaStore.Dispose"/>'s job.</para>
/// </summary>
public sealed class WireLogWriter : IDisposable
{
    private readonly MuckaStore _store;
    private int _seq = -1;
    private volatile bool _disposed;

    public WireLogWriter(MuckaStore store) => _store = store;

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

    /// <summary>Nothing is held back, so there is nothing to flush. Kept because every path that ends
    /// a connection calls it, and because a recorder that grows a buffer later should have one place
    /// to empty it.</summary>
    public void Flush() { }

    public void Dispose() => _disposed = true;

    private void Emit(WireDirection direction, ReadOnlySpan<byte> payload)
    {
        if (_disposed) return;
        // Interlocked rather than a lock: the read and write loops are different threads and the only
        // shared state is this counter. `seq` is the order records were handed over, which is what a
        // replay needs - the store preserves it because there is one queue and one writer.
        var seq = Interlocked.Increment(ref _seq);
        _store.Enqueue(new WireRow(_store.SessionId, seq,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), direction, payload.ToArray()));
    }
}

/// <summary>One record on its way to <c>wire</c>. The payload goes in as-is: these are the server's
/// own bytes and nothing here transforms them.</summary>
internal sealed record WireRow(long SessionId, int Seq, long TimestampMs, WireDirection Direction,
    byte[] Payload) : IStoreRow
{
    private const string Sql = """
        INSERT INTO wire (session_id, seq, ts_ms, direction, data)
        VALUES ($session, $seq, $ts, $direction, $data);
        """;

    public void Write(StoreWrite write)
    {
        var command = write.Prepared(Sql);
        command.Parameters.AddWithValue("$session", SessionId);
        command.Parameters.AddWithValue("$seq", Seq);
        command.Parameters.AddWithValue("$ts", TimestampMs);
        command.Parameters.AddWithValue("$direction", (int)Direction);
        command.Parameters.Add("$data", SqliteType.Blob).Value = Payload;
        command.ExecuteNonQuery();
    }
}
