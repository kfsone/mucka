using System.Text;
using System.Text.Json;
using Mucka.Core;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// The wire log's storage contract: bytes in must equal bytes out, through framing, compression,
/// SQLite and back, and the exported <c>.jsonl</c> must be the format the whole existing corpus
/// already reads.
///
/// <para>The high-byte cases are the point of the whole exercise, not decoration. MUD2's C1 codes are
/// bytes 0x80-0xFF; any layer that quietly decides they are text - a UTF-8 round trip, a
/// <c>char</c>-based buffer, a JSON escape - corrupts them in a way that only shows up months later in
/// a capture nobody can replay. Everything here is asserted on raw bytes.</para>
///
/// <para>Every frame below is written through <see cref="Wire"/> rather than as a string literal, so
/// this file stays pure ASCII. A source file that carries C1 controls and embedded NULs literally is
/// one whose meaning survives only as long as nobody's editor re-encodes it - and MUD2 frames contain
/// both.</para>
/// </summary>
public sealed class WireLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mucka-wirelog-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string DbPath => Path.Combine(_dir, "wire.db");

    /// <summary>
    /// Builds a wire frame from a mini-notation: <c>%A3</c> is the single byte 0xA3, anything else is
    /// its own ASCII byte. Lets a real MUD2 frame - C1 codes, the 0xFF separators, the CR NUL CR LF
    /// line ending - be written out legibly without a single non-ASCII byte in the source.
    /// </summary>
    private static byte[] Wire(string spec)
    {
        var bytes = new List<byte>(spec.Length);
        for (var i = 0; i < spec.Length; i++)
        {
            if (spec[i] == '%' && i + 2 < spec.Length)
            {
                bytes.Add(Convert.ToByte(spec.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
            {
                bytes.Add((byte)spec[i]);
            }
        }
        return bytes.ToArray();
    }

    /// <summary>A run of real MUD2-shaped frames: C1 codes, embedded NULs, the whole byte range.</summary>
    private static List<WireRecord> SampleRecords(long baseTs = 1_787_778_044_300)
    {
        var everyByte = new byte[256];
        for (var i = 0; i < 256; i++) everyByte[i] = (byte)i;

        return
        [
            new WireRecord(baseTs, WireDirection.Rx,
                Wire("%A3%9B%FF%FFThe wyvern is staring at you ferociously.%FF%FF%0D%00%0D%0A")),
            new WireRecord(baseTs + 12, WireDirection.Tx, Wire("kill wyvern%0D%0A")),
            new WireRecord(baseTs + 1994, WireDirection.Rx,
                Wire("%A3%9F%FF%FFThe wyvern misses you.%FF%FF%0D%00%0D%0A%9C%FF%FF%9C%9D%FF%FF*%FF%FF")),
            // Annotations are UTF-8 of a .NET string, not Latin-1 of a wire frame - a non-ASCII
            // character in one must survive as itself rather than as a byte.
            new WireRecord(baseTs + 2000, WireDirection.Annotation,
                Encoding.UTF8.GetBytes("dreamword detected: sk" + (char)0x00F6 + "ll")),
            new WireRecord(baseTs + 2001, WireDirection.Rx, everyByte),
            new WireRecord(baseTs + 2002, WireDirection.Tx, []),   // an empty write must survive as a record
        ];
    }

    private static void AssertSame(IReadOnlyList<WireRecord> expected, IReadOnlyList<WireRecord> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].TimestampMs, actual[i].TimestampMs);
            Assert.Equal(expected[i].Direction, actual[i].Direction);
            Assert.Equal(expected[i].Payload, actual[i].Payload);   // byte-for-byte
        }
    }

    // -- Framing ---------------------------------------------------------------

    [Fact]
    public void Framing_round_trips_every_byte_value_and_all_three_directions()
    {
        var records = SampleRecords();
        var builder = new WireLogBatchBuilder();
        foreach (var r in records) builder.Append(r.Direction, r.TimestampMs, r.Payload);

        AssertSame(records, WireLogFraming.Decode(builder.ToArray(), builder.BaseTimestampMs));
    }

    [Fact]
    public void Framing_round_trips_a_backwards_clock_step()
    {
        // Zigzag deltas exist for exactly this: an NTP step mid-session must round-trip as the
        // timestamp that was actually recorded, not be clamped into a plausible lie.
        var records = new List<WireRecord>
        {
            new(2_000_000_000_000, WireDirection.Rx, Wire("before")),
            new(1_999_999_995_000, WireDirection.Rx, Wire("after the step back")),
            new(2_000_000_000_001, WireDirection.Rx, Wire("and forward again")),
        };
        var builder = new WireLogBatchBuilder();
        foreach (var r in records) builder.Append(r.Direction, r.TimestampMs, r.Payload);

        AssertSame(records, WireLogFraming.Decode(builder.ToArray(), builder.BaseTimestampMs));
    }

    [Fact]
    public void Framing_round_trips_through_compression()
    {
        var builder = new WireLogBatchBuilder();
        foreach (var r in SampleRecords()) builder.Append(r.Direction, r.TimestampMs, r.Payload);
        var framed = builder.ToArray();

        var (codec, blob) = WireLogFraming.Compress(framed);
        Assert.Equal(WireLogCodec.Brotli, codec);
        Assert.Equal(framed, WireLogFraming.Decompress(codec, blob, framed.Length));
    }

    [Fact]
    public void Framing_rejects_a_truncated_batch_rather_than_inventing_records()
    {
        var builder = new WireLogBatchBuilder();
        foreach (var r in SampleRecords()) builder.Append(r.Direction, r.TimestampMs, r.Payload);
        var framed = builder.ToArray();

        Assert.Throws<InvalidDataException>(() => WireLogFraming.Decode(framed.AsSpan(0, framed.Length - 5), 0));
        Assert.Throws<InvalidDataException>(() => WireLogFraming.Decode("nope"u8, 0));
    }

    // -- The SQLite sink, end to end -------------------------------------------

    [Fact]
    public void Sink_round_trips_a_partial_batch_closed_at_stop()
    {
        // Six small records is nowhere near the 64 KB bound, so the ONLY thing that gets them to disk
        // is Dispose() closing the open batch. That path is what a normal end of session uses.
        var records = SampleRecords();
        using (var sink = new SqliteWireLogSink(DbPath, "mud2.co.uk"))
        {
            foreach (var r in records) sink.Record(r.Direction, r.TimestampMs, r.Payload);
        }

        var session = Assert.Single(WireLogExport.ListSessions(DbPath));
        Assert.Equal(1, session.Batches);
        Assert.Equal(records.Count, session.Records);
        Assert.Equal("mud2.co.uk", session.Host);
        Assert.NotNull(session.EndedMs);
        AssertSame(records, WireLogExport.ReadSession(DbPath, session.Id).ToList());
    }

    [Fact]
    public void Sink_round_trips_across_several_batches_when_the_size_bound_fires()
    {
        // Enough traffic to cross MaxBatchBytes several times, so the sequence has to be
        // reassembled from multiple rows in seq order rather than read out of one blob.
        var rng = new Random(20260904);
        var records = new List<WireRecord>();
        var ts = 1_787_000_000_000L;
        var written = 0;
        while (written < SqliteWireLogSink.MaxBatchBytes * 3)
        {
            var payload = new byte[rng.Next(20, 400)];
            rng.NextBytes(payload);                       // random == worst case for the compressor
            records.Add(new WireRecord(ts, (WireDirection)(records.Count % 3), payload));
            written += payload.Length;
            ts += rng.Next(1, 50);
        }

        using (var sink = new SqliteWireLogSink(DbPath, "mud2.co.uk"))
        {
            foreach (var r in records) sink.Record(r.Direction, r.TimestampMs, r.Payload);
        }

        var session = Assert.Single(WireLogExport.ListSessions(DbPath));
        Assert.True(session.Batches >= 3, $"expected the size bound to fire, got {session.Batches} batch(es)");
        Assert.Equal(records.Count, session.Records);
        AssertSame(records, WireLogExport.ReadSession(DbPath, session.Id).ToList());
    }

    [Fact]
    public void Sink_flush_makes_records_readable_without_closing_the_session()
    {
        var records = SampleRecords();
        using var sink = new SqliteWireLogSink(DbPath, "mud2.co.uk");
        foreach (var r in records) sink.Record(r.Direction, r.TimestampMs, r.Payload);
        sink.Flush();

        // Flush hands the batch to the background writer; give it a moment to commit.
        var session = WaitForRecords(DbPath, records.Count);
        AssertSame(records, WireLogExport.ReadSession(DbPath, session.Id).ToList());
        Assert.Null(session.EndedMs);   // still open - only Dispose stamps the end
    }

    [Fact]
    public void Sink_compresses_real_mud2_traffic()
    {
        // Not a ratio assertion - the corpus here is six frames, and a ratio measured on six frames
        // would be a number pretending to be evidence. It asserts only the direction: the stored blob
        // is smaller than the bytes that went into it. The real figures, measured over the owner's 40
        // captures, are in the table on WireLogFraming.
        var records = ReadJsonl(WyvernFixturePath);
        using (var sink = new SqliteWireLogSink(DbPath, "mud2.co.uk"))
            foreach (var r in records) sink.Record(r.Direction, r.TimestampMs, r.Payload);

        var session = Assert.Single(WireLogExport.ListSessions(DbPath));
        Assert.True(session.StoredBytes < session.RawBytes,
            $"stored {session.StoredBytes} >= raw {session.RawBytes}");
    }

    // -- The .jsonl export, against the format that already exists --------------

    private static string WyvernFixturePath =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Data", "wyvern-poison-death.jsonl");

    [Fact]
    public void Export_reproduces_a_real_capture_from_the_existing_corpus()
    {
        // The strongest statement available that "the .jsonl shape keeps working": take a capture the
        // OLD writer produced (the committed wyvern fixture), push its records through framing,
        // compression and SQLite, export them again, and require the result to be the same capture.
        //
        // Compared as PARSED records rather than as text, for a reason worth writing down: the running
        // .NET now emits its \uXXXX escapes with UPPERCASE hex where the runtime that wrote the fixture
        // emitted lowercase. Both are the same JSON string to any parser, and nothing in this repo or
        // the offline corpus matches on the escape text - but a byte-for-byte assertion here would be
        // pinning a runtime detail and calling it the file format. Byte-exactness against the LIVE
        // writer is asserted separately, below, where it is actually the thing under test.
        var records = ReadJsonl(WyvernFixturePath);
        Assert.Equal(6, records.Count);

        using (var sink = new SqliteWireLogSink(DbPath, "mud2.co.uk"))
            foreach (var r in records) sink.Record(r.Direction, r.TimestampMs, r.Payload);

        var session = Assert.Single(WireLogExport.ListSessions(DbPath));
        var exportPath = Path.Combine(_dir, "export.jsonl");
        Assert.Equal(records.Count, WireLogExport.ExportSessionToJsonl(DbPath, session.Id, exportPath));

        Assert.Equal(
            File.ReadAllLines(WyvernFixturePath).Where(l => l.Length > 0).Count(),
            File.ReadAllLines(exportPath).Where(l => l.Length > 0).Count());
        AssertSame(records, ReadJsonl(exportPath));
    }

    [Fact]
    public void Export_matches_what_the_live_jsonl_sink_writes_for_the_same_records()
    {
        var records = SampleRecords();

        var livePath = Path.Combine(_dir, "live.jsonl");
        using (var file = new JsonlWireLogSink(livePath))
            foreach (var r in records) file.Record(r.Direction, r.TimestampMs, r.Payload);

        using (var sink = new SqliteWireLogSink(DbPath, "mud2.co.uk"))
            foreach (var r in records) sink.Record(r.Direction, r.TimestampMs, r.Payload);
        var session = Assert.Single(WireLogExport.ListSessions(DbPath));
        var exportPath = Path.Combine(_dir, "export.jsonl");
        WireLogExport.ExportSessionToJsonl(DbPath, session.Id, exportPath);

        Assert.Equal(File.ReadAllText(livePath), File.ReadAllText(exportPath));
    }

    // -- The setting's gate, both ways ------------------------------------------

    [Fact]
    public void Capture_with_no_database_sink_writes_no_database_at_all()
    {
        // Setting OFF. Nothing calls TryStartDatabase, so the recording path is inert and - the part
        // that actually matters for an opt-in feature - no file is created on disk.
        using var capture = new SessionCapture();
        Assert.False(capture.IsRecording);
        capture.RecordRx(Wire("%A3%9BYou are in a forest.%0D%0A"));
        capture.RecordTx(Wire("look%0D%0A"));
        capture.Annotate("a note nobody asked for");

        Assert.Null(capture.DatabasePath);
        Assert.False(File.Exists(DbPath));
    }

    [Fact]
    public void Capture_with_the_database_sink_records_everything_in_order()
    {
        // Setting ON. Same three calls, and all three come back with their direction and bytes intact.
        using (var capture = new SessionCapture())
        {
            Assert.True(capture.TryStartDatabase(() => new SqliteWireLogSink(DbPath, "mud2.co.uk"), out var error));
            Assert.Null(error);
            Assert.True(capture.IsRecording);
            Assert.Equal(DbPath, capture.DatabasePath);

            capture.RecordRx(Wire("%A3%9BYou are in a forest.%0D%0A"));
            capture.RecordTx(Wire("look%0D%0A"));
            capture.Annotate("a note somebody did ask for");
        }

        var session = Assert.Single(WireLogExport.ListSessions(DbPath));
        var records = WireLogExport.ReadSession(DbPath, session.Id).ToList();
        // Three above, plus the "capture stopped" annotation Stop() writes.
        Assert.Equal(4, records.Count);
        Assert.Equal(WireDirection.Rx, records[0].Direction);
        Assert.Equal(Wire("%A3%9BYou are in a forest.%0D%0A"), records[0].Payload);
        Assert.Equal(WireDirection.Tx, records[1].Direction);
        Assert.Equal(Wire("look%0D%0A"), records[1].Payload);
        Assert.Equal(WireDirection.Annotation, records[2].Direction);
        Assert.Equal("a note somebody did ask for", Encoding.UTF8.GetString(records[2].Payload));
    }

    [Fact]
    public void Stopping_the_manual_file_capture_leaves_the_always_on_database_log_running()
    {
        // The in-game capture button must never switch off a log the player enabled in settings
        // months ago - that is the whole reason the two sinks start and stop separately.
        using var capture = new SessionCapture();
        Assert.True(capture.TryStartDatabase(() => new SqliteWireLogSink(DbPath, "mud2.co.uk"), out _));
        Assert.True(capture.TryStartFile(_dir, "mud2.co.uk", out _));
        Assert.True(capture.IsFileRecording);

        capture.StopFile();

        Assert.False(capture.IsFileRecording);
        Assert.Null(capture.FilePath);
        Assert.True(capture.IsRecording);            // the database sink is still attached
        Assert.Equal(DbPath, capture.DatabasePath);
    }

    [Fact]
    public void The_manual_file_capture_still_writes_the_format_it_always_did()
    {
        // Additive only: the file sink is untouched by all of this, header annotation included.
        using (var capture = new SessionCapture())
        {
            Assert.True(capture.TryStartFile(_dir, "mud2.co.uk", out var error));
            Assert.Null(error);
            capture.RecordRx(Wire("%A3%9Bhello%0D%0A"));
            capture.StopFile();
        }

        var file = Directory.GetFiles(_dir, "session-rec.mud2.co.uk.*.jsonl").Single();
        var lines = File.ReadAllLines(file).Where(l => l.Length > 0).ToArray();
        Assert.Equal(3, lines.Length);                                   // started, the frame, stopped
        Assert.Contains("\"an\",\"capture started: mud2.co.uk\"", lines[0]);
        // The escaped form the corpus already uses - see the committed wyvern fixture. Case-insensitive
        // on the hex because the running .NET emits uppercase where the fixture's runtime emitted
        // lowercase; both are the same JSON string.
        Assert.Contains("\"rx\",\"\\u00a3\\u009bhello\\r\\n\"", lines[1], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"an\",\"capture stopped\"", lines[2]);
    }

    // -- Helpers ----------------------------------------------------------------

    /// <summary>Parses a capture file in the existing <c>[ts,"rx"|"tx"|"an",text]</c> format back into
    /// records, applying the encoding contract on <see cref="WireRecord"/> in reverse.</summary>
    private static List<WireRecord> ReadJsonl(string path)
    {
        var records = new List<WireRecord>();
        foreach (var line in File.ReadAllLines(path))
        {
            if (line.Length == 0) continue;
            using var document = JsonDocument.Parse(line);
            var array = document.RootElement;
            var ts = array[0].GetInt64();
            var mode = array[1].GetString();
            var text = array[2].GetString() ?? string.Empty;
            var direction = mode switch
            {
                "rx" => WireDirection.Rx,
                "tx" => WireDirection.Tx,
                _ => WireDirection.Annotation,
            };
            var payload = direction == WireDirection.Annotation
                ? Encoding.UTF8.GetBytes(text)
                : Encoding.Latin1.GetBytes(text);
            records.Add(new WireRecord(ts, direction, payload));
        }
        return records;
    }

    /// <summary>Polls until the background writer has committed at least <paramref name="expected"/>
    /// records. Bounded, and fails loudly rather than hanging.</summary>
    private static WireLogSessionInfo WaitForRecords(string dbPath, int expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(dbPath))
            {
                var session = WireLogExport.ListSessions(dbPath).FirstOrDefault();
                if (session is not null && session.Records >= expected)
                    return session;
            }
            Thread.Sleep(25);
        }
        throw new TimeoutException($"wire log never reached {expected} records");
    }
}
