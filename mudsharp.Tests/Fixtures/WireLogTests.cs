using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mucka.Core;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// The wire log's storage contract: bytes in must equal bytes out, through framing, SQLite and back;
/// the payloads must be findable verbatim in the stored blob; and the exported <c>.jsonl</c> must be
/// the format the whole existing corpus already reads.
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
    public void Sink_stores_real_mud2_traffic_as_bytes_you_can_grep_for()
    {
        // Stores raw, uncompressed bytes, asserted rather than described: every payload the sink was
        // handed is findable verbatim inside batches.data. That is what makes the log a corpus you can
        // ask questions of without writing a decoder first.
        var records = ReadJsonl(WyvernFixturePath);
        using (var sink = new SqliteWireLogSink(DbPath, "mud2.co.uk"))
            foreach (var r in records) sink.Record(r.Direction, r.TimestampMs, r.Payload);

        var blobs = new List<byte[]>();
        using (var connection = WireLogDb.OpenRead(DbPath))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT data FROM batches ORDER BY seq;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                blobs.Add((byte[])reader.GetValue(0));
        }

        Assert.NotEmpty(blobs);
        foreach (var record in records)
        {
            Assert.True(
                blobs.Exists(blob => Contains(blob, record.Payload)),
                $"payload of {record.Payload.Length} bytes is not present verbatim in any stored batch");
        }
    }

    /// <summary>Whether <paramref name="needle"/> appears as a contiguous byte run in
    /// <paramref name="haystack"/> - the programmatic form of grepping the blob.</summary>
    private static bool Contains(byte[] haystack, byte[] needle)
        => needle.Length == 0 || haystack.AsSpan().IndexOf(needle.AsSpan()) >= 0;

    [Fact]
    public void Open_discards_a_wire_log_written_by_a_build_with_a_different_batches_schema()
    {
        // WireLogDb.DiscardOnSchemaChange's standing policy, exercised on the shape that actually
        // provoked it: an older file whose `batches` still carries the `codec` and `raw_bytes` columns
        // the compression needed. CREATE TABLE IF NOT EXISTS would leave it alone and the first insert
        // would fail a NOT NULL constraint, faulting the sink out for the whole session.
        // WireLogDb.Open creates the directory; a bare connection does not.
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        using (var legacy = new SqliteConnection(WireLogDb.ConnectionString(DbPath)))
        {
            legacy.Open();
            using var command = legacy.CreateCommand();
            command.CommandText = """
                CREATE TABLE sessions (
                    id INTEGER PRIMARY KEY, started_ms INTEGER NOT NULL, ended_ms INTEGER,
                    host TEXT, client_version TEXT);
                CREATE TABLE batches (
                    id INTEGER PRIMARY KEY, session_id INTEGER NOT NULL, seq INTEGER NOT NULL,
                    base_ts_ms INTEGER NOT NULL, last_ts_ms INTEGER NOT NULL, records INTEGER NOT NULL,
                    raw_bytes INTEGER NOT NULL, codec INTEGER NOT NULL, data BLOB NOT NULL);
                INSERT INTO sessions (started_ms, host) VALUES (1, 'mud2.co.uk');
                """;
            command.ExecuteNonQuery();
        }

        var records = SampleRecords();
        using (var sink = new SqliteWireLogSink(DbPath, "mud2.co.uk"))
            foreach (var r in records) sink.Record(r.Direction, r.TimestampMs, r.Payload);

        // The legacy session went with the table; this build's own writes are intact.
        var session = Assert.Single(WireLogExport.ListSessions(DbPath));
        AssertSame(records, WireLogExport.ReadSession(DbPath, session.Id).ToList());
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

    // -- The batch bounds -------------------------------------------------------

    [Fact]
    public void Sink_closes_a_batch_at_the_age_bound_not_only_at_the_size_bound()
    {
        // Ten minutes of idle chatter: a few hundred bytes, nowhere near MaxBatchBytes. The age bound is
        // the only thing that can close these, and it is enforced on the RECORD's timestamp rather than
        // by the housekeeping timer, so this is deterministic rather than a sleep.
        var start = 1_787_000_000_000L;
        var records = new List<WireRecord>();
        for (var minute = 0; minute < 20; minute++)
            records.Add(new WireRecord(start + minute * 60_000L, WireDirection.Rx, Wire("tick%0D%0A")));

        using (var sink = new SqliteWireLogSink(DbPath, "mud2.co.uk"))
            foreach (var r in records) sink.Record(r.Direction, r.TimestampMs, r.Payload);

        // The bound is checked after the append, so a batch SPANS at most MaxBatchAge rather than
        // ending just before it: these pair up two ticks per batch, ten batches for twenty minutes.
        var session = Assert.Single(WireLogExport.ListSessions(DbPath));
        Assert.Equal(records.Count, session.Records);
        Assert.True(session.Batches >= 9,
            $"the {SqliteWireLogSink.MaxBatchAge.TotalSeconds}s age bound should have closed a batch a minute, got {session.Batches}");
        AssertSame(records, WireLogExport.ReadSession(DbPath, session.Id).ToList());
    }

    // -- Opening the database: eagerly, and loudly when it fails -----------------

    [Fact]
    public void Sink_creates_its_directory_and_database_before_anything_is_recorded()
    {
        // The wire log is switched on once and never checked again, so "did it open?" has to be
        // answerable at start. Nothing has been recorded here at all: the directory, the file and the
        // sessions row must exist anyway.
        var nested = Path.Combine(_dir, "does", "not", "exist", "yet");
        var path = Path.Combine(nested, "wire.db");
        Assert.False(Directory.Exists(nested));

        using (var sink = new SqliteWireLogSink(path, "mud2.co.uk"))
        {
            Assert.True(Directory.Exists(nested));
            Assert.True(File.Exists(path));
            var open = Assert.Single(WireLogExport.ListSessions(path));
            Assert.Equal("mud2.co.uk", open.Host);
            Assert.Equal(0L, open.Batches);
        }
    }

    [Fact]
    public void Sink_throws_at_construction_when_the_database_cannot_be_opened()
    {
        // A plain file where the directory has to go: Directory.CreateDirectory cannot make one, so the
        // open fails.
        Assert.ThrowsAny<Exception>(() => new SqliteWireLogSink(BlockedDbPath(), "mud2.co.uk"));
    }

    [Fact]
    public void Capture_reports_a_database_it_cannot_open_instead_of_claiming_success()
    {
        // This is the contract MuckaConnection.TryStartWireLog hands to ConnectViewModel, and the whole
        // reason that error branch is not dead code.
        var blocked = BlockedDbPath();
        using var capture = new SessionCapture();

        var started = capture.TryStartDatabase(() => new SqliteWireLogSink(blocked, "mud2.co.uk"), out var error);

        Assert.False(started);
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Null(capture.DatabasePath);
        Assert.False(capture.IsRecording);
    }

    [Fact]
    public void A_database_sink_that_fails_does_not_take_the_file_capture_down_with_it()
    {
        // One dead sink must not cost the others. The file capture is the one the player armed by hand
        // this run; the database log failing is not its problem.
        var capture = new SessionCapture();
        try
        {
            Assert.False(capture.TryStartDatabase(() => new SqliteWireLogSink(BlockedDbPath(), "h"), out _));
            Assert.True(capture.TryStartFile(_dir, "mud2.co.uk", out _));
            // ...and a sink that fails on every single call, attached alongside it.
            Assert.True(capture.TryStartDatabase(() => new ThrowingSink(), out _));

            capture.RecordRx(Wire("%A3%9Bhello%0D%0A"));
            var filePath = capture.FilePath!;
            capture.Stop();

            var lines = File.ReadAllLines(filePath).Where(l => l.Length > 0).ToArray();
            Assert.Contains(lines, l => l.Contains("hello", StringComparison.Ordinal));
        }
        finally { capture.Dispose(); }
    }

    [Fact]
    public void A_dead_writer_stops_accepting_records_and_says_so()
    {
        // Kill the writer for real - drop the table out from under it - rather than mocking the failure.
        // A writer that dies on a SQLite error must not leave Record()/Flush() feeding a channel with no
        // reader for the rest of the session: that is an unbounded leak whose only trace would be one
        // line in the crash log.
        var failures = new List<string>();
        using var sink = new SqliteWireLogSink(DbPath, "mud2.co.uk", null,
            (context, _) => { lock (failures) failures.Add(context); });

        sink.Record(WireDirection.Rx, 1_787_000_000_000L, Wire("first%0D%0A"));
        sink.Flush();
        WaitForRecords(DbPath, 1);   // the writer is definitely alive and has committed

        using (var saboteur = WireLogDb.Open(DbPath))
        {
            using var drop = saboteur.CreateCommand();
            drop.CommandText = "DROP TABLE batches;";
            drop.ExecuteNonQuery();
        }

        sink.Record(WireDirection.Rx, 1_787_000_000_001L, Wire("second%0D%0A"));
        sink.Flush();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!sink.IsFaulted && DateTime.UtcNow < deadline) Thread.Sleep(25);
        Assert.True(sink.IsFaulted, "the sink should have marked itself faulted when its writer died");
        lock (failures) Assert.NotEmpty(failures);

        // And it is now inert: nothing accepted, nothing queued, nothing thrown at the socket loop.
        for (var i = 0; i < 1000; i++)
            sink.Record(WireDirection.Rx, 1_787_000_000_002L + i, Wire("ignored%0D%0A"));
        sink.Flush();
        Assert.True(sink.IsFaulted);
        Assert.Equal(0, sink.DroppedBatches);
    }

    // -- Integrity: the two fields that were stored and ignored ------------------

    [Fact]
    public void Framing_rejects_a_batch_that_does_not_hold_the_record_count_the_row_claims()
    {
        var builder = new WireLogBatchBuilder();
        foreach (var r in SampleRecords()) builder.Append(r.Direction, r.TimestampMs, r.Payload);
        var framed = builder.ToArray();

        Assert.Equal(6, WireLogFraming.Decode(framed, 0, expectedRecords: 6).Count);
        Assert.Throws<InvalidDataException>(() => WireLogFraming.Decode(framed, 0, expectedRecords: 5));
        Assert.Throws<InvalidDataException>(() => WireLogFraming.Decode(framed, 0, expectedRecords: 7));
    }

    [Theory]
    [InlineData("records")]
    public void Export_refuses_a_batch_row_whose_integrity_field_no_longer_matches(string column)
    {
        // The end-to-end version: a row damaged in the database is rejected on read rather than
        // decoded into records nobody ever sent. `records` is the only such column left - `raw_bytes`
        // held the DECOMPRESSED length, which with nothing compressed is LENGTH(data), so it only
        // compared the blob against itself.
        var records = SampleRecords();
        using (var sink = new SqliteWireLogSink(DbPath, "mud2.co.uk"))
            foreach (var r in records) sink.Record(r.Direction, r.TimestampMs, r.Payload);

        var session = Assert.Single(WireLogExport.ListSessions(DbPath));
        AssertSame(records, WireLogExport.ReadSession(DbPath, session.Id).ToList());   // healthy first

        using (var connection = WireLogDb.Open(DbPath))
        {
            using var damage = connection.CreateCommand();
            damage.CommandText = $"UPDATE batches SET {column} = {column} + 1;";
            Assert.Equal(1, damage.ExecuteNonQuery());
        }

        Assert.Throws<InvalidDataException>(() => WireLogExport.ReadSession(DbPath, session.Id).ToList());
    }

    // -- Helpers ----------------------------------------------------------------

    /// <summary>A database path that cannot possibly be opened: its parent "directory" is a file.</summary>
    private string BlockedDbPath()
    {
        Directory.CreateDirectory(_dir);
        var blocker = Path.Combine(_dir, "blocker-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "not a directory");
        return Path.Combine(blocker, "wire.db");
    }

    /// <summary>A sink that fails at everything, to prove the others carry on regardless.</summary>
    private sealed class ThrowingSink : IWireLogSink
    {
        public string Location => "throwing";
        public void Record(WireDirection direction, long timestampMs, ReadOnlySpan<byte> payload)
            => throw new IOException("sink is broken");
        public void Flush() => throw new IOException("sink is broken");
        public void Dispose() => throw new IOException("sink is broken");
    }

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
