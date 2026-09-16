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

    // -- The row is the record --------------------------------------------------

    [Fact]
    public void A_row_holds_the_payload_and_nothing_else()
    {
        // The whole storage rule in one assertion: `data` is the bytes that went past, byte for byte,
        // with no magic, no length prefix and no encoded timestamp in front of them. `SELECT data`
        // prints the line. Everything else about a record - when, which way - is a column.
        var records = SampleRecords();
        using (var store = NewStore())
        using (var writer = new WireLogWriter(store))
            Feed(writer, records);

        var stored = new List<(long Ts, int Direction, byte[] Data)>();
        using (var connection = MuckaDb.OpenRead(DbPath))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT ts_ms, direction, data FROM wire ORDER BY seq;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                stored.Add((reader.GetInt64(0), reader.GetInt32(1), (byte[])reader.GetValue(2)));
        }

        Assert.Equal(records.Count, stored.Count);
        for (var i = 0; i < records.Count; i++)
        {
            Assert.Equal(records[i].Payload, stored[i].Data);
            Assert.Equal((int)records[i].Direction, stored[i].Direction);
            Assert.True(stored[i].Ts > 0, "every row carries its own timestamp");
        }
    }

    [Fact]
    public void Seq_is_gapless_and_zero_based_within_the_session()
    {
        // seq is the order the socket loops handed records over, and a replay walks it. A gap would
        // mean a record was lost between the tap and the commit.
        var records = SampleRecords();
        using (var store = NewStore())
        using (var writer = new WireLogWriter(store))
            Feed(writer, records);

        using var connection = MuckaDb.OpenRead(DbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MIN(seq), MAX(seq), COUNT(*), COUNT(DISTINCT seq) FROM wire;";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(0, reader.GetInt32(0));
        Assert.Equal(records.Count - 1, reader.GetInt32(1));
        Assert.Equal(records.Count, reader.GetInt32(2));
        Assert.Equal(records.Count, reader.GetInt32(3));
    }

    [Fact]
    public void Seq_id_and_timestamp_all_agree_even_under_concurrent_taps()
    {
        // The read loop and the write loop call the writer at the same time. A reader walks `id`
        // (the rowid, so no index and no sort), `seq` is what proves nothing was lost, and `ts_ms`
        // is what a cross-stream question compares - so all three have to be the same order. They
        // only are if the clock reading, the seq and the hand-over happen at one serialization
        // point; split any of them out and a thread preempted in between lets a later record win.
        // Two threads hammering both taps is what shakes that out.
        const int PerThread = 4000;
        using (var store = NewStore())
        using (var writer = new WireLogWriter(store))
        {
            var rx = new Thread(() => { for (var i = 0; i < PerThread; i++) writer.RecordRx(Wire("rx%0D%0A")); });
            var tx = new Thread(() => { for (var i = 0; i < PerThread; i++) writer.RecordTx(Wire("tx%0D%0A")); });
            rx.Start(); tx.Start();
            rx.Join(); tx.Join();
        }

        using var connection = MuckaDb.OpenRead(DbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT seq, ts_ms FROM wire ORDER BY id;";
        using var reader = command.ExecuteReader();
        var walked = new List<(int Seq, long Ts)>();
        while (reader.Read()) walked.Add((reader.GetInt32(0), reader.GetInt64(1)));

        Assert.Equal(PerThread * 2, walked.Count);
        for (var i = 0; i < walked.Count; i++)
        {
            Assert.True(walked[i].Seq == i,
                $"row {i} in id order carries seq {walked[i].Seq}: id order and seq order have diverged");
            if (i > 0)
                Assert.True(walked[i].Ts >= walked[i - 1].Ts,
                    $"row {i} is stamped {walked[i].Ts}, before row {i - 1}'s {walked[i - 1].Ts}: "
                    + "id order and timestamp order have diverged");
        }
    }

    // -- The writer and the store, end to end ----------------------------------

    [Fact]
    public void The_log_round_trips_what_was_recorded_when_the_store_closes()
    {
        var records = SampleRecords();
        using (var store = NewStore())
        using (var writer = new WireLogWriter(store))
            Feed(writer, records);

        var session = Assert.Single(WireLogExport.ListSessions(DbPath));
        Assert.Equal(records.Count, session.Records);
        Assert.Equal("mud2.co.uk", session.Host);
        Assert.NotNull(session.EndedMs);
        AssertRecordShapes(records, WireLogExport.ReadSession(DbPath, session.Id).ToList());
    }

    [Fact]
    public void The_log_round_trips_a_volume_of_traffic_that_spans_many_drains()
    {
        // Far more than one drain pass can carry, so the sequence has to come back out of hundreds of
        // rows in seq order rather than out of whatever one transaction happened to commit.
        var rng = new Random(20260904);
        var records = new List<WireRecord>();
        var written = 0;
        while (written < 192 * 1024)
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
        Assert.Equal(records.Count, session.Records);
        AssertRecordShapes(records, WireLogExport.ReadSession(DbPath, session.Id).ToList());
    }

    [Fact]
    public void Records_become_readable_without_closing_the_session()
    {
        // Nothing is held back by the writer, so records reach the file as fast as the store's task
        // drains them - no flush needed, and the session stays open while they do.
        var records = SampleRecords();
        using var store = NewStore();
        using var writer = new WireLogWriter(store);
        Feed(writer, records);

        var session = WaitForRecords(DbPath, records.Count);
        AssertRecordShapes(records, WireLogExport.ReadSession(DbPath, session.Id).ToList());
        Assert.Null(session.EndedMs);   // still open - only the store's Dispose stamps the end
    }

    [Fact]
    public void The_log_stores_real_mud2_traffic_as_bytes_you_can_grep_for()
    {
        // Stores raw, uncompressed bytes, asserted rather than described: every payload the writer was
        // handed is a row of wire.data, verbatim and whole. That is what makes the log a corpus you can
        // ask questions of without writing a decoder first.
        var records = ReadCapture(WyvernFixturePath);
        using (var store = NewStore())
        using (var writer = new WireLogWriter(store))
            Feed(writer, records);

        var blobs = new List<byte[]>();
        using (var connection = MuckaDb.OpenRead(DbPath))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT data FROM wire ORDER BY seq;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                blobs.Add((byte[])reader.GetValue(0));
        }

        Assert.NotEmpty(blobs);
        foreach (var record in records)
        {
            Assert.True(
                blobs.Exists(blob => Contains(blob, record.Payload)),
                $"payload of {record.Payload.Length} bytes is not present verbatim in any stored row");
        }
    }

    [Fact]
    public void The_log_reproduces_a_real_capture_from_the_existing_corpus()
    {
        // The strongest statement available that the storage layer is lossless: take a capture from the
        // committed wyvern fixture, push its records through the writer, the framing and SQLite, read
        // them back, and require the same records in the same order.
        var records = ReadCapture(WyvernFixturePath);
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
            Assert.Equal(0L, open.Records);
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
            drop.CommandText = "DROP TABLE wire;";
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

    // -- Helpers ----------------------------------------------------------------

    private static string WyvernFixturePath =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Data", "wyvern-poison-death.c1");

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

    /// <summary>
    /// Reads a <c>.c1</c> capture: one record per line, <c>ts_ms direction payload</c>, the payload
    /// being the server's own bytes. Only three are escaped - a backslash, and the CR and LF that
    /// would otherwise split a record - so the C1 codes sit in the file raw and the frame is legible
    /// beside its text. Latin-1 throughout, which is the identity map for a byte-oriented protocol.
    /// </summary>
    private static List<WireRecord> ReadCapture(string path)
    {
        var records = new List<WireRecord>();
        foreach (var line in File.ReadAllLines(path, Encoding.Latin1))
        {
            if (line.Length == 0) continue;
            var parts = line.Split(' ', 3);
            var direction = parts[1] switch
            {
                "rx" => WireDirection.Rx,
                "tx" => WireDirection.Tx,
                _ => WireDirection.Annotation,
            };
            records.Add(new WireRecord(long.Parse(parts[0]), direction, CaptureBytes(parts[2])));
        }
        return records;
    }

    /// <summary>Undoes the three escapes a <c>.c1</c> payload carries.</summary>
    private static byte[] CaptureBytes(string payload)
    {
        var bytes = new List<byte>(payload.Length);
        for (var i = 0; i < payload.Length; i++)
        {
            if (payload[i] == '\\' && i + 1 < payload.Length)
            {
                bytes.Add(payload[++i] switch { 'r' => (byte)0x0D, 'n' => (byte)0x0A, _ => (byte)0x5C });
            }
            else
            {
                bytes.Add((byte)payload[i]);   // Latin-1: char and byte are the same value
            }
        }
        return bytes.ToArray();
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
