namespace Mucka.Core;

/// <summary>
/// The batch framing: how a run of <see cref="WireRecord"/>s becomes one byte buffer that decodes back
/// to exactly the records that went in.
///
/// <para><b>Nothing here compresses anything, deliberately.</b> The wire log's only job is to be the
/// corpus that questions get asked of, and a blob you cannot look at without writing a decoder first
/// is a corpus in name only. The provocation was real: answering "what does a flee actually cost"
/// needed a hand-written brotli-and-varint reader before a single frame could be read. See
/// <c>docs/Lab-spec.md</c> for the tool that is meant to make that a query instead.</para>
///
/// <para><b>What it costs, as arithmetic off the figures the compressed version measured</b> (MB = 10^6
/// bytes here and in <see cref="SqliteWireLogSink"/>, not MiB). Measured over 40 captures: 79,495
/// records, 6,144,190 bytes of payload over 8.91 play-hours, an average wire rate of 0.191 KB/s, and
/// ~0.74 KB of SQLite page and index overhead per batch row. Plain, at the one-minute batch bound, that
/// same corpus is 6.14 MB of payload plus 4.2% framing plus ~535 rows of overhead: about 6.8 MB, or
/// <b>0.76 MB per play-hour, ~1.1 GB/year at four hours of daily play</b>. Brotli made it 0.174
/// MB/play-hour and ~0.25 GB/year. So legibility costs roughly 0.85 GB a year, which is a few pence of
/// disk against a corpus that can now be grepped. It is still 2.5x smaller than the same traffic as
/// <c>.jsonl</c> (1.924 MB/play-hour), because the framing does not repeat a timestamp and a direction
/// as text on every line.</para>
///
/// <para><b>Why batch at all, now that it is not for the dictionary.</b> Purely SQLite row overhead:
/// one row per record would pay ~0.74 KB of pages and index entries for a 77-byte average payload,
/// which is an order of magnitude worse than the traffic itself. A minute of MUD2 is about 12 KB, so a
/// batch amortises that overhead to nothing while bounding what a hard kill can lose to one minute of
/// traffic.</para>
///
/// <para><b>Format.</b> A batch buffer is
/// <code>
///   [4]  magic "MWL1"
///   records*, until the buffer ends:
///        varint   zigzag(ts_delta_ms)   -- from the previous record; the first is from base_ts_ms
///        varint   (payload_length &lt;&lt; 2) | direction
///        [len]    payload bytes, verbatim
/// </code>
/// <b>The payload bytes are the server's own bytes, untouched</b>, which is what makes the blob
/// legible: a hexdump or a <c>strings</c> pass over <c>batches.data</c> reads as MUD2 text with a
/// couple of header bytes between records. What the varints buy is the two things text cannot carry
/// for free - which direction a record went and when it arrived - at 4.2% of the corpus rather than
/// the ~180% <c>.jsonl</c> spends on the same two facts.</para>
///
/// <para>Timestamps are deltas because a batch spans minutes and absolute unix-ms values are 6 bytes
/// of varint each; zigzag rather than plain because a wall-clock step backwards (NTP) must round-trip
/// exactly rather than being clamped into a lie. Direction rides in the low two bits of the length
/// varint because it only has three values.</para>
///
/// <para><b>There is no redundancy in here at all</b>, and that is a deliberate limitation with a
/// measured consequence. Every byte is a varint or a payload byte, so damage that keeps the buffer
/// parseable produces a different but entirely well-formed run of records - over 200,000 mutated
/// batches, 39,789 decoded into silent garbage. What guards a batch is not the format but
/// <c>batches.records</c> stored beside it, which <see cref="Decode"/> enforces.</para>
/// </summary>
public static class WireLogFraming
{
    /// <summary>"MWL1" - Mucka Wire Log, framing version 1. A version bump means a new magic. The magic
    /// reflects only the byte format INSIDE a batch; a storage-level change to the row that wraps one
    /// does not by itself require a new magic.</summary>
    public static ReadOnlySpan<byte> Magic => "MWL1"u8;

    /// <summary>
    /// Decodes a framed batch buffer back into the exact records that were appended to it.
    /// <paramref name="baseTimestampMs"/> is the batch's stored base (the first record's absolute
    /// timestamp); everything after is reconstructed from the deltas.
    ///
    /// <para><paramref name="expectedRecords"/> is <c>batches.records</c>, and is the only integrity
    /// check there is. The framing has no internal redundancy, so a damaged blob usually re-parses into
    /// a well-formed run of records that is simply not what was written. The record count is the one
    /// thing stored outside the blob that a corrupted blob has to agree with; requiring it is what
    /// turns "decoded fine" into "decoded as what was written." Pass -1 only where no stored count
    /// exists.</para>
    /// </summary>
    /// <exception cref="InvalidDataException">The buffer is not a batch, is truncated, or does not hold
    /// <paramref name="expectedRecords"/> records.</exception>
    public static List<WireRecord> Decode(ReadOnlySpan<byte> framed, long baseTimestampMs,
        int expectedRecords = -1)
    {
        if (framed.Length < Magic.Length || !framed[..Magic.Length].SequenceEqual(Magic))
            throw new InvalidDataException("Not a wire-log batch (bad magic).");

        var records = new List<WireRecord>();
        var offset = Magic.Length;
        var previous = baseTimestampMs;
        while (offset < framed.Length)
        {
            var delta = Zigzag.Decode(ReadVarint(framed, ref offset));
            var header = ReadVarint(framed, ref offset);
            // The cast to int is why this is checked against the ulong and not after: a length varint of
            // 2^32 truncates to 0, which is a valid empty payload, so the bounds test below would pass
            // and the batch would decode into records nobody wrote.
            var length64 = header >> 2;
            var direction = (WireDirection)(byte)(header & 0b11);
            if (length64 > int.MaxValue || offset + (int)length64 > framed.Length)
                throw new InvalidDataException("Truncated wire-log batch.");
            var length = (int)length64;

            var timestamp = previous + delta;
            previous = timestamp;
            records.Add(new WireRecord(timestamp, direction, framed.Slice(offset, length).ToArray()));
            offset += length;
        }

        if (expectedRecords >= 0 && records.Count != expectedRecords)
            throw new InvalidDataException(
                $"Wire-log batch decoded {records.Count} records, but the row says {expectedRecords}.");
        return records;
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> buffer, ref int offset)
    {
        ulong value = 0;
        var shift = 0;
        while (true)
        {
            if (offset >= buffer.Length) throw new InvalidDataException("Truncated varint in wire-log batch.");
            if (shift > 63) throw new InvalidDataException("Overlong varint in wire-log batch.");
            var b = buffer[offset++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return value;
            shift += 7;
        }
    }

    internal static void WriteVarint(ulong value, byte[] buffer, ref int offset)
    {
        while (value >= 0x80)
        {
            buffer[offset++] = (byte)(value | 0x80);
            value >>= 7;
        }
        buffer[offset++] = (byte)value;
    }

    /// <summary>Bytes a varint of this value occupies. Used to size the buffer before writing.</summary>
    internal static int VarintSize(ulong value)
    {
        var n = 1;
        while (value >= 0x80) { value >>= 7; n++; }
        return n;
    }

    internal static class Zigzag
    {
        public static ulong Encode(long value) => (ulong)((value << 1) ^ (value >> 63));
        public static long Decode(ulong value) => (long)(value >> 1) ^ -(long)(value & 1);
    }
}

/// <summary>
/// Accumulates records into one framed batch buffer. Not thread-safe by itself - the owning sink
/// holds a lock around it (see <see cref="SqliteWireLogSink"/>).
/// </summary>
public sealed class WireLogBatchBuilder
{
    private byte[] _buffer;
    private int _length;
    private long _baseTimestampMs;
    private long _lastTimestampMs;

    public WireLogBatchBuilder(int initialCapacity = 8 * 1024)
    {
        _buffer = new byte[Math.Max(initialCapacity, WireLogFraming.Magic.Length + 64)];
        Reset();
    }

    /// <summary>Records appended since the last <see cref="Reset"/>.</summary>
    public int Count { get; private set; }
    /// <summary>Framed bytes so far, magic included.</summary>
    public int Length => _length;
    /// <summary>Absolute timestamp of the first record - the decoder's starting point.</summary>
    public long BaseTimestampMs => _baseTimestampMs;
    /// <summary>Absolute timestamp of the most recent record.</summary>
    public long LastTimestampMs => _lastTimestampMs;
    public bool IsEmpty => Count == 0;

    public void Append(WireDirection direction, long timestampMs, ReadOnlySpan<byte> payload)
    {
        if (Count == 0)
        {
            _baseTimestampMs = timestampMs;
            _lastTimestampMs = timestampMs;
        }

        var delta = WireLogFraming.Zigzag.Encode(timestampMs - _lastTimestampMs);
        var header = ((ulong)(uint)payload.Length << 2) | (byte)direction;
        var needed = WireLogFraming.VarintSize(delta) + WireLogFraming.VarintSize(header) + payload.Length;
        EnsureCapacity(_length + needed);

        WireLogFraming.WriteVarint(delta, _buffer, ref _length);
        WireLogFraming.WriteVarint(header, _buffer, ref _length);
        payload.CopyTo(_buffer.AsSpan(_length));
        _length += payload.Length;

        _lastTimestampMs = timestampMs;
        Count++;
    }

    /// <summary>The framed bytes, copied out. Call <see cref="Reset"/> to start the next batch.</summary>
    public byte[] ToArray() => _buffer.AsSpan(0, _length).ToArray();

    public void Reset()
    {
        WireLogFraming.Magic.CopyTo(_buffer);
        _length = WireLogFraming.Magic.Length;
        Count = 0;
        _baseTimestampMs = 0;
        _lastTimestampMs = 0;
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _buffer.Length) return;
        var size = _buffer.Length;
        while (size < required) size *= 2;
        Array.Resize(ref _buffer, size);
    }
}
