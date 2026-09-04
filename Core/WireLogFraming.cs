using System.IO.Compression;

namespace Mucka.Core;

/// <summary>Which compressor produced a batch blob. Stored in <c>batches.codec</c>; never renumber.</summary>
public enum WireLogCodec
{
    /// <summary>No compression — the framed bytes as-is. Only used if compression itself fails.</summary>
    None = 0,
    /// <summary>Brotli, <see cref="CompressionLevel.SmallestSize"/> (quality 11).</summary>
    Brotli = 1,
    /// <summary>GZip. Reserved; nothing writes it today, the decoder accepts it.</summary>
    Gzip = 2,
}

/// <summary>
/// The batch framing: how a run of <see cref="WireRecord"/>s becomes one byte buffer that compresses
/// well and decodes back to exactly the records that went in.
///
/// <para><b>Why batch at all.</b> The average wire record is 77 bytes (6.1 MB over 79,455 records in
/// the owner's existing corpus). Compressing one of those on its own gains nothing — a deflate or
/// brotli stream spends more on its own header than the record contains. Compression here is entirely
/// a function of how much text shares one dictionary, and MUD2 text is extremely repetitive, so the
/// gain is large but only if the window is large.</para>
///
/// <para><b>Measured, on the owner's 40 real captures</b> — 79,495 records, 6.14 MB of payload, 8.91
/// play-hours, replayed through this framing and brotli-11. The ratio is set by batch size and
/// essentially nothing else:
/// <code>
///   batch bound   avg raw batch   ratio vs payload   MB/play-hour
///     5 s              1.1 KB          2.61x            0.303
///    15 s              3.1 KB          3.88x            0.192
///    60 s             11.7 KB          5.36x            0.133
///    64 KB            50.9 KB          6.66x            0.104      &lt;- what this sink does
///   whole session    160.3 KB          8.00x            0.086      &lt;- the ceiling
/// </code>
/// End to end, through the real sink and SQLite, the 64 KB row is <b>1.04 MB of wire.db for 8.91
/// play-hours = 0.117 MB per play-hour = ~0.17 GB/year at 4h/day, and 16.4x smaller than the same
/// traffic as .jsonl</b> (the gap between 0.104 and 0.117 is SQLite's own pages and indexes).</para>
///
/// <para>That table is the whole argument for <see cref="SqliteWireLogSink.MaxBatchAge"/> being five
/// minutes rather than five seconds: at MUD2's measured ~0.19 KB/s a five-second batch is one
/// kilobyte, and a kilobyte does not compress — it would cost 2.9x more disk forever to shrink the
/// crash window from five minutes to five seconds on a diagnostic log. The 64 KB size bound takes
/// ~5.6 minutes to reach at that rate, so the two bounds are deliberately the same order and the size
/// one is what normally fires.</para>
///
/// <para>The remaining 6.66x-to-8.00x gap is only reachable by compressing a whole session as one
/// blob, which cannot be done live — it needs a compaction pass over a session after it closes. The
/// schema supports it (batches are keyed by session and seq); it is not implemented, and it is worth
/// ~0.04 GB/year.</para>
///
/// <para><b>Format.</b> A batch buffer is
/// <code>
///   [4]  magic "MWL1"
///   records*, until the buffer ends:
///        varint   zigzag(ts_delta_ms)   -- from the previous record; the first is from base_ts_ms
///        varint   (payload_length &lt;&lt; 2) | direction
///        [len]    payload bytes, verbatim
/// </code>
/// Timestamps are deltas because a batch spans minutes and absolute unix-ms values are 6 bytes of
/// varint each that compress badly; zigzag rather than plain because a wall-clock step backwards
/// (NTP) must round-trip exactly rather than being clamped into a lie. Direction rides in the low two
/// bits of the length varint because it only has three values and a whole byte per record is 1.3%
/// of the corpus. Total framing overhead measured at 4.7% before compression, near zero after.</para>
/// </summary>
public static class WireLogFraming
{
    /// <summary>"MWL1" — Mucka Wire Log, framing version 1. A version bump means a new magic.</summary>
    public static ReadOnlySpan<byte> Magic => "MWL1"u8;

    /// <summary>Compresses a framed batch buffer. Returns <see cref="WireLogCodec.None"/> and the
    /// input unchanged if compression somehow fails — a bigger row beats a lost one.</summary>
    public static (WireLogCodec Codec, byte[] Data) Compress(byte[] framed)
    {
        try
        {
            using var output = new MemoryStream(framed.Length / 4 + 64);
            using (var brotli = new BrotliStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
                brotli.Write(framed, 0, framed.Length);
            return (WireLogCodec.Brotli, output.ToArray());
        }
        catch
        {
            return (WireLogCodec.None, framed);
        }
    }

    /// <summary>Inverse of <see cref="Compress"/>.</summary>
    public static byte[] Decompress(WireLogCodec codec, byte[] data, int expectedLength)
    {
        if (codec == WireLogCodec.None) return data;

        using var input = new MemoryStream(data, writable: false);
        Stream decoder = codec switch
        {
            WireLogCodec.Brotli => new BrotliStream(input, CompressionMode.Decompress),
            WireLogCodec.Gzip   => new GZipStream(input, CompressionMode.Decompress),
            _ => throw new InvalidDataException($"Unknown wire-log codec {(int)codec}."),
        };
        using (decoder)
        {
            using var output = new MemoryStream(expectedLength > 0 ? expectedLength : 4096);
            decoder.CopyTo(output);
            return output.ToArray();
        }
    }

    /// <summary>
    /// Decodes a framed batch buffer back into the exact records that were appended to it.
    /// <paramref name="baseTimestampMs"/> is the batch's stored base (the first record's absolute
    /// timestamp); everything after is reconstructed from the deltas.
    /// </summary>
    /// <exception cref="InvalidDataException">The buffer is not a batch, or is truncated.</exception>
    public static List<WireRecord> Decode(ReadOnlySpan<byte> framed, long baseTimestampMs)
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
            var length = (int)(header >> 2);
            var direction = (WireDirection)(byte)(header & 0b11);
            if (length < 0 || offset + length > framed.Length)
                throw new InvalidDataException("Truncated wire-log batch.");

            var timestamp = previous + delta;
            previous = timestamp;
            records.Add(new WireRecord(timestamp, direction, framed.Slice(offset, length).ToArray()));
            offset += length;
        }
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
/// Accumulates records into one framed batch buffer. Not thread-safe by itself — the owning sink
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
    /// <summary>Absolute timestamp of the first record — the decoder's starting point.</summary>
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
