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
/// <para><b>Units, because these numbers are meaningless without them: KB = 1000 bytes and MB = 10^6
/// bytes throughout this file and in <see cref="SqliteWireLogSink"/>.</b> Not KiB/MiB. Everything below
/// was produced by replaying the owner's corpus through this code; nothing in it is an estimate.</para>
///
/// <para><b>Why batch at all.</b> The average wire record is 77.3 bytes (6.14 MB over 79,495 records in
/// the owner's existing corpus). Compressing one of those on its own gains nothing — a deflate or
/// brotli stream spends more on its own header than the record contains. Compression here is entirely
/// a function of how much text shares one dictionary, and MUD2 text is extremely repetitive, so the
/// gain is large but only if the window is large.</para>
///
/// <para><b>Measured, on the owner's 40 real captures</b> — 79,495 records, 6,144,190 bytes of payload,
/// 8.91 play-hours, an average wire rate of 0.191 KB/s, replayed through this framing and brotli-11.
/// The ratio is set by batch size and essentially nothing else:
/// <code>
///   batch bound   avg raw batch   ratio vs payload   MB/play-hour
///     5 s              1.3 KB          2.73x            0.252
///    15 s              3.3 KB          3.91x            0.176
///    60 s             11.9 KB          5.37x            0.128      &lt;- what this sink does
///    64 KB            50.9 KB          6.66x            0.103
///     5 min           48.6 KB          6.67x            0.103
///   whole session    160.3 KB          8.00x            0.086      &lt;- the ceiling
/// </code>
/// The 64 KB and 5-minute rows are the same row in practice: at 0.191 KB/s a 64 KB batch takes about
/// 5.6 minutes to fill, so whichever of the two is written down, the other one never fires.</para>
///
/// <para>End to end, through the real sink and SQLite, the shipped 60 s bound is <b>1.552 MB of wire.db
/// for 8.91 play-hours = 0.174 MB per play-hour = 0.254 GB/year at 4 h/day, and 11.0x smaller than the
/// same traffic as .jsonl</b> (17.15 MB, 1.924 MB/play-hour). The gap between the table's 0.128 and the
/// file's 0.174 is SQLite's own pages and two indexes, measured at ~0.74 KB per batch row.</para>
///
/// <para><b>What the one-minute bound costs, measured rather than predicted.</b> The same replay through
/// the previous bound — 64 KB, which is what actually fired before <see cref="SqliteWireLogSink"/>
/// enforced an age — produced 1.012 MB of wire.db, 0.114 MB per play-hour, 0.166 GB/year. So one minute
/// costs <b>0.088 GB a year, about 88 MB</b>, to cut the worst-case crash loss from roughly five and a
/// half minutes of traffic to one. Note that this is more than twice what the blob column alone
/// suggests (0.128 vs 0.103 is only ~37 MB/year): 4.3x as many rows is 4.3x as much per-row SQLite
/// overhead, and that overhead is the larger half of the bill. The trade is still obviously worth
/// taking at this scale; it is written down because "the blobs only grow 25%" would have been the
/// wrong number.</para>
///
/// <para>Going the other way is not free either: at 0.191 KB/s a five-second batch is a kilobyte, and a
/// kilobyte does not compress. Buying a five-second crash window would cost 0.252 against 0.128, near
/// enough double, forever, on a diagnostic log.</para>
///
/// <para>The remaining 5.37x-to-8.00x gap is only reachable by compressing a whole session as one blob,
/// which cannot be done live — it needs a compaction pass over a session after it closes. The schema
/// supports it (batches are keyed by session and seq); it is not implemented, and it is now worth
/// ~0.06 GB/year.</para>
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
/// of the corpus. Total framing overhead measured at 4.2% before compression, near zero after.</para>
///
/// <para><b>There is no redundancy in here at all</b>, and that is a deliberate limitation with a
/// measured consequence. Every byte is a varint or a payload byte, so damage that keeps the buffer
/// parseable produces a different but entirely well-formed run of records. What guards a batch is not
/// the format but the two numbers stored beside it in the row — <c>raw_bytes</c> and <c>records</c> —
/// which <see cref="Decompress"/> and <see cref="Decode"/> now enforce. See
/// <see cref="WireLogExport.ReadSession"/> for what that catches and what it does not.</para>
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

    /// <summary>
    /// Inverse of <see cref="Compress"/>, and the first of the two integrity checks.
    ///
    /// <para><paramref name="expectedLength"/> is <c>batches.raw_bytes</c>. It used to be nothing but a
    /// capacity hint for the output buffer; it is now enforced, because a stored length that disagrees
    /// with what came out of the decompressor is proof the row is damaged and costs one comparison to
    /// notice. Pass 0 only where no stored length exists.</para>
    /// </summary>
    /// <exception cref="InvalidDataException">Unknown codec, or the decompressed length is not
    /// <paramref name="expectedLength"/>.</exception>
    public static byte[] Decompress(WireLogCodec codec, byte[] data, int expectedLength)
    {
        var framed = DecompressRaw(codec, data, expectedLength);
        if (expectedLength > 0 && framed.Length != expectedLength)
            throw new InvalidDataException(
                $"Wire-log batch is {framed.Length} bytes decompressed, but the row says {expectedLength}.");
        return framed;
    }

    private static byte[] DecompressRaw(WireLogCodec codec, byte[] data, int expectedLength)
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
    ///
    /// <para><paramref name="expectedRecords"/> is <c>batches.records</c>, and is the second integrity
    /// check. The framing has no internal redundancy at all — every byte is either a varint or payload,
    /// so most single-byte damage re-parses into a different but perfectly well-formed run of records
    /// and is indistinguishable from real traffic. The record count is the one thing stored outside the
    /// blob that a corrupted blob has to agree with; requiring it is what turns "decoded fine" into
    /// "decoded as what was written." Pass -1 only where no stored count exists.</para>
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
