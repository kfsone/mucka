using System.Text;
using System.Text.Json;
using Mucka.Store;
using Mucka.WireLog;

namespace Mucka.Util.Tests;

/// <summary>
/// The wire log's storage contract: bytes in must equal bytes out, through framing, SQLite and back,
/// and the payloads must be findable verbatim in the stored blob.
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

    private string DbPath => Path.Combine(_dir, MuckaDb.DefaultFileName);

    private MuckaStore NewStore(string? path = null, Action<string, Exception>? onError = null)
        => new(path ?? DbPath, "mud2.co.uk", null, onError);

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

    /// <summary>Pushes a run of records at a writer in the order they were captured.</summary>
    private static void Feed(WireLogWriter writer, IEnumerable<WireRecord> records)
    {
        foreach (var r in records)
        {
            switch (r.Direction)
            {
                case WireDirection.Rx: writer.RecordRx(r.Payload); break;
                case WireDirection.Tx: writer.RecordTx(r.Payload); break;
                default: writer.Annotate(Encoding.UTF8.GetString(r.Payload)); break;
            }
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

    // -- The writer and the store, end to end ----------------------------------

    [Fact]
    public void The_log_round_trips_a_partial_batch_closed_at_dispose()
    {
        // Six small records is nowhere near the 64 KB bound, so the ONLY thing that gets them to disk
        // is the writer's Dispose closing the open batch and the store's draining it. That pair is what
        // a normal end of session uses.
        var records = SampleRecords();
        using (var store = NewStore())
        using (var writer = new WireLogWriter(store))
            Feed(writer, records);

        var session = Assert.Single(WireLogExport.ListSessions(DbPath));
        Assert.Equal(1, session.Batches);
        Assert.Equal(records.Count, session.Records);
        Assert.Equal("mud2.co.uk", session.Host);
        Assert.NotNull(session.EndedMs);
        AssertRecordShapes(records, WireLogExport.ReadSession(DbPath, session.Id).ToList());
    }

    [Fact]
    public void The_log_round_trips_across_several_batches_when_the_size_bound_fires()
    {
        // Enough traffic to cross MaxBatchBytes several times, so the sequence has to be
        // reassembled from multiple rows in seq order rather than read out of one blob.
        var rng = new Random(20260904);
        var records = new List<WireRecord>();
        var written = 0;
        while (written < WireLogWriter.MaxBatchBytes * 3)
        {
            var payload = new byte[rng.Next(20, 400)];
            rng.NextBytes(payload);
            records.Add(new WireRecord(0, records.Count % 2 == 0 ? WireDirection.Rx : WireDirection.Tx, payload));
            written += payload.Length;
        }

        using (var store = NewStore())
        using (var writer = new WireLogWriter(store))
            Feed(writer, records);

        var session = Assert.Single(WireLogExport.ListSessions(DbPath));
        Assert.True(session.Batches >= 3, $"expected the size bound to fire, got {session.Batches} batch(es)");
        Assert.Equal(records.Count, session.Records);
        AssertRecordShapes(records, WireLogExport.ReadSession(DbPath, session.Id).ToList());
    }

    [Fact]
    public void Flush_makes_records_readable_without_closing_the_session()
    {
        var records = SampleRecords();
        using var store = NewStore();
        using var writer = new WireLogWriter(store);
        Feed(writer, records);
        writer.Flush();

        // Flush hands the batch to the store's background writer; give it a moment to commit.
        var session = WaitForRecords(DbPath, records.Count);
        AssertRecordShapes(records, WireLogExport.ReadSession(DbPath, session.Id).ToList());
        Assert.Null(session.EndedMs);   // still open - only the store's Dispose stamps the end
    }

    [Fact]
    public void The_log_stores_real_mud2_traffic_as_bytes_you_can_grep_for()
    {
        // Stores raw, uncompressed bytes, asserted rather than described: every payload the writer was
        // handed is findable verbatim inside batches.data. That is what makes the log a corpus you can
        // ask questions of without writing a decoder first.
        var records = ReadJsonl(WyvernFixturePath);
        using (var store = NewStore())
        using (var writer = new WireLogWriter(store))
            Feed(writer, records);

        var blobs = new List<byte[]>();
        using (var connection = MuckaDb.OpenRead(DbPath))
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

    [Fact]
    public void The_log_reproduces_a_real_capture_from_the_existing_corpus()
    {
        // The strongest statement available that the storage layer is lossless: take a capture from the
        // committed wyvern fixture, push its records through the writer, the framing and SQLite, read
        // them back, and require the same records in the same order.
        var records = ReadJsonl(WyvernFixturePath);
        Assert.Equal(6, records.Count);

        using (var store = NewStore())
        using (var writer = new WireLogWriter(store))
            Feed(writer, records);

        var session = Assert.Single(WireLogExport.ListSessions(DbPath));
        Assert.Equal(records.Count, session.Records);
        AssertRecordShapes(records, WireLogExport.ReadSession(DbPath, session.Id).ToList());
    }

    /// <summary>Whether <paramref name="needle"/> appears as a contiguous byte run in
    /// <paramref name="haystack"/> - the programmatic form of grepping the blob.</summary>
    private static bool Contains(byte[] haystack, byte[] needle)
        => needle.Length == 0 || haystack.AsSpan().IndexOf(needle.AsSpan()) >= 0;

    // -- The batch bounds -------------------------------------------------------

    [Fact]
    public void A_batch_closes_at_the_age_bound_and_not_only_at_the_size_bound()
    {
        // Twenty minutes of idle chatter, a few hundred bytes in total - nowhere near MaxBatchBytes. The
        // age bound is the only thing that can close these. It is enforced on the RECORD's own
        // timestamp, so this drives the builder directly rather than sleeping for twenty minutes.
        var start = 1_787_000_000_000L;
        var builder = new WireLogBatchBuilder();
        var closed = 0;
        for (var minute = 0; minute < 20; minute++)
        {
            var ts = start + minute * 60_000L;
            builder.Append(WireDirection.Rx, ts, Wire("tick%0D%0A"));
            if (builder.Length >= WireLogWriter.MaxBatchBytes ||
                ts - builder.BaseTimestampMs >= (long)WireLogWriter.MaxBatchAge.TotalMilliseconds)
            {
                closed++;
                builder.Reset();
            }
        }

        // The bound is checked after the append, so a batch SPANS at most MaxBatchAge rather than
        // ending just before it: these pair up two ticks per batch, ten batches for twenty minutes.
        Assert.True(closed >= 9,
            $"the {WireLogWriter.MaxBatchAge.TotalSeconds}s age bound should close a batch a minute, got {closed}");
    }

    // -- Opening the database: eagerly, and loudly when it fails -----------------

    [Fact]
    public void The_store_creates_its_directory_and_database_before_anything_is_recorded()
    {
        // The store is switched on once and never checked again, so "did it open?" has to be answerable
        // at start. Nothing has been recorded here at all: the directory, the file and the sessions row
        // must exist anyway.
        var nested = Path.Combine(_dir, "does", "not", "exist", "yet");
        var path = Path.Combine(nested, MuckaDb.DefaultFileName);
        Assert.False(Directory.Exists(nested));

        using (var store = new MuckaStore(path, "mud2.co.uk"))
        {
            Assert.False(store.IsFaulted);
            Assert.True(Directory.Exists(nested));
            Assert.True(File.Exists(path));
            var open = Assert.Single(WireLogExport.ListSessions(path));
            Assert.Equal("mud2.co.uk", open.Host);
            Assert.Equal(0L, open.Batches);
        }
    }

    [Fact]
    public void A_store_that_cannot_be_opened_reports_itself_and_then_accepts_nothing()
    {
        // The point of the client is to play MUD2, so a database that cannot be opened must not throw
        // out of the connect path. It reports once, comes up faulted, and swallows every row after.
        var failures = new List<string>();
        using var store = NewStore(BlockedDbPath(), (context, _) => failures.Add(context));

        Assert.True(store.IsFaulted);
        Assert.Single(failures);

        using var writer = new WireLogWriter(store);
        writer.RecordRx(Wire("%A3%9Bhello%0D%0A"));
        writer.Flush();
        Assert.True(store.IsFaulted);
    }

    [Fact]
    public void A_dead_writer_stops_accepting_rows_and_says_so()
    {
        // Kill the writer for real - drop the table out from under it - rather than mocking the failure.
        // A writer that dies on a SQLite error must not leave Enqueue feeding a channel with no reader
        // for the rest of the session: that is an unbounded leak whose only trace would be one line in
        // the crash log.
        var failures = new List<string>();
        using var store = NewStore(onError: (context, _) => { lock (failures) failures.Add(context); });
        using var writer = new WireLogWriter(store);

        writer.RecordRx(Wire("first%0D%0A"));
        writer.Flush();
        WaitForRecords(DbPath, 1);   // the writer is definitely alive and has committed

        using (var saboteur = MuckaDb.Open(DbPath))
        {
            using var drop = saboteur.CreateCommand();
            drop.CommandText = "DROP TABLE batches;";
            drop.ExecuteNonQuery();
        }

        writer.RecordRx(Wire("second%0D%0A"));
        writer.Flush();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!store.IsFaulted && DateTime.UtcNow < deadline) Thread.Sleep(25);
        Assert.True(store.IsFaulted, "the store should have marked itself faulted when its writer died");
        lock (failures) Assert.NotEmpty(failures);

        // And it is now inert: nothing accepted, nothing queued, nothing thrown at the socket loop.
        for (var i = 0; i < 1000; i++)
            writer.RecordRx(Wire("ignored%0D%0A"));
        writer.Flush();
        Assert.True(store.IsFaulted);
    }

    // -- Integrity: the field that is stored and checked -------------------------

    [Fact]
    public void Export_refuses_a_batch_row_whose_record_count_no_longer_matches()
    {
        // The end-to-end version: a row damaged in the database is rejected on read rather than
        // decoded into records nobody ever sent.
        var records = SampleRecords();
        using (var store = NewStore())
        using (var writer = new WireLogWriter(store))
            Feed(writer, records);

        var session = Assert.Single(WireLogExport.ListSessions(DbPath));
        Assert.Equal(records.Count, WireLogExport.ReadSession(DbPath, session.Id).Count());   // healthy first

        using (var connection = MuckaDb.Open(DbPath))
        {
            using var damage = connection.CreateCommand();
            damage.CommandText = "UPDATE batches SET records = records + 1;";
            Assert.Equal(1, damage.ExecuteNonQuery());
        }

        Assert.Throws<InvalidDataException>(() => WireLogExport.ReadSession(DbPath, session.Id).ToList());
    }

    // -- Helpers ----------------------------------------------------------------

    private static string WyvernFixturePath =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Data", "wyvern-poison-death.jsonl");

    /// <summary>Same as <see cref="AssertSame"/> minus the timestamps, for records that went through the
    /// live writer: it stamps each one with its own <c>UtcNow</c> reading, so only the payload, the
    /// direction and the order are the writer's to preserve.</summary>
    private static void AssertRecordShapes(IReadOnlyList<WireRecord> expected, IReadOnlyList<WireRecord> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Direction, actual[i].Direction);
            Assert.Equal(expected[i].Payload, actual[i].Payload);   // byte-for-byte
        }
    }

    /// <summary>A database path that cannot possibly be opened: its parent "directory" is a file.</summary>
    private string BlockedDbPath()
    {
        Directory.CreateDirectory(_dir);
        var blocker = Path.Combine(_dir, "blocker-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "not a directory");
        return Path.Combine(blocker, MuckaDb.DefaultFileName);
    }

    /// <summary>Parses a capture file in the old <c>[ts,"rx"|"tx"|"an",text]</c> format back into
    /// records, applying the encoding contract on <see cref="WireRecord"/> in reverse. The fixture is
    /// committed test data; nothing writes this format any more.</summary>
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
