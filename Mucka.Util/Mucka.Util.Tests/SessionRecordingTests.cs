using System.Globalization;
using Microsoft.Data.Sqlite;
using Mucka.Combat;
using Mucka.Store;
using Mucka.WireLog;

namespace Mucka.Util.Tests;

/// <summary>A Session's recording by row ids (<see cref="SessionRecording"/>): which Sessions still have
/// one, and Delete, which takes one Session's and nothing else. Shrink the file, on the same page, last.</summary>
public sealed class SessionRecordingTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mucka-session-recording-tests", Guid.NewGuid().ToString("N"));

    public SessionRecordingTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        TestStore.ReleasePools(_directory);
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string Db => Path.Combine(_directory, MuckaDb.DefaultFileName);

    private static long Insert(SqliteConnection connection, string sql, params (string, object?)[] args)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql + " SELECT last_insert_rowid();";
        foreach (var (name, value) in args)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static long Run(SqliteConnection db, long? ended)
        => Insert(db, "INSERT INTO mucka_runs (uid, started_ms, ended_ms, host) VALUES ($u, 0, $e, 'test');",
            ("$u", Guid.NewGuid().ToString("N")), ("$e", ended));

    private static long Login(SqliteConnection db, long run, long started, long? ended)
        => Insert(db, "INSERT INTO persona_sessions (mucka_run_id, persona, started_ms, ended_ms) VALUES ($r, 'Ollie', $s, $e);",
            ("$r", run), ("$s", started), ("$e", ended));

    private static void Rows(SqliteConnection db, long run, params long[] stamps)
    {
        var seq = Scalar(db, $"SELECT COALESCE(MAX(seq), 0) FROM wire WHERE mucka_run_id = {run};");
        foreach (var ts in stamps)
            Insert(db, "INSERT INTO wire (mucka_run_id, seq, ts_ms, direction, data) VALUES ($r, $q, $t, 0, $d);",
                ("$r", run), ("$q", ++seq), ("$t", ts), ("$d", new[] { (byte)('A' + seq % 26) }));
    }

    /// <summary>The bytes of each record of a Session's slice, in order.</summary>
    private List<string> SliceOf(long session)
        => SessionSlice.Read(Db, session) is { IsEmpty: false } slice
            ? [.. slice.Run.Skip(slice.First).Take(slice.Last - slice.First + 1)
                .Select(r => $"{r.TimestampMs}:{System.Text.Encoding.Latin1.GetString(r.Payload)}")]
            : [];

    // Three logins in one ended run, a stamp stepping back (190 after 250), and a second run whose
    // first login's slice is empty: nothing stamped between the run's start and its end.
    private (long A, long B, long C, long Empty, long Next) Seed()
    {
        using var db = MuckaDb.Open(Db);
        var run = Run(db, 1000);
        var a = Login(db, run, 100, 200);
        var b = Login(db, run, 300, 400);
        var c = Login(db, run, 500, null);
        Rows(db, run, 50, 150, 250, 190, 350, 450, 650, 700);
        var second = Run(db, 3000);
        var empty = Login(db, second, 2000, 2100);
        var next = Login(db, second, 2200, 2300);
        Rows(db, second, 2150, 2250, 2400);
        return (a, b, c, empty, next);
    }

    [Fact]
    public void Recorded_IsTheSessionsWhoseSliceHoldsARow_AsSessionSliceCutsIt()
    {
        var (a, b, c, empty, next) = Seed();

        using var db = MuckaDb.OpenRead(Db);
        var recorded = SessionRecording.Recorded(db);

        foreach (var id in new[] { a, b, c, empty, next })
            Assert.Equal(SliceOf(id).Count > 0, recorded.Contains(id));
        Assert.DoesNotContain(empty, recorded);
        Assert.Contains(b, recorded);
    }

    [Fact]
    public void Recorded_AgreesWithTheSlice_ForAnOpenEnd_EqualBoundaries_AndARunWithNoRows()
    {
        long open, first, same, last, none;
        using (var db = MuckaDb.Open(Db))
        {
            var run = Run(db, 1000);
            open = Login(db, run, 100, null);   // no end and not last: ends where the next starts
            first = Login(db, run, 300, 400);
            same = Login(db, run, 350, 400);    // the same end as the one before: an empty slice
            last = Login(db, run, 500, 600);
            Rows(db, run, 150, 320, 380, 450, 550);
            var bare = Run(db, 2000);
            none = Login(db, bare, 1500, 1600);
        }

        using var read = MuckaDb.OpenRead(Db);
        var recorded = SessionRecording.Recorded(read);

        foreach (var id in new[] { open, first, same, last, none })
            Assert.Equal(SliceOf(id).Count > 0, recorded.Contains(id));
        Assert.Contains(open, recorded);
        Assert.DoesNotContain(same, recorded);
        Assert.DoesNotContain(none, recorded);
    }

    [Fact]
    public void Delete_TakesTheMiddleSessionsRecording_AndLeavesItsNeighboursAndItsFacts()
    {
        var (a, b, c, _, _) = Seed();
        using (var db = MuckaDb.Open(Db))
        {
            Insert(db, "INSERT INTO score_events (ts, encounter_open, total, delta, persona_session_id) VALUES (350, 0, 500, NULL, $p);", ("$p", b));
            // A row a trim took from each of A and B, by its stamp.
            foreach (var ts in new[] { 120L, 360L })
                Insert(db, "INSERT INTO trimmed (id, mucka_run_id, seq, ts_ms, direction, data, trimmed_ms, reason) VALUES ($i, 1, $i, $t, 1, X'00', 1, 'probe send');",
                    ("$i", 1000 + ts), ("$t", ts));
        }
        SessionLabels.SetTitle(Db, a, "kept");
        SessionLabels.SetTitle(Db, b, "gone");
        SessionLabels.SetTags(Db, b, ["gone"]);
        var (beforeA, beforeB, beforeC) = (SliceOf(a), SliceOf(b), SliceOf(c));

        var result = SessionRecording.Delete(Db, b);

        Assert.Equal(beforeB.Count, result.Rows);
        Assert.Empty(SliceOf(b));
        Assert.Equal(beforeA, SliceOf(a));
        Assert.Equal(beforeC, SliceOf(c));
        using var after = MuckaDb.OpenRead(Db);
        Assert.Equal(5, Scalar(after, "SELECT COUNT(*) FROM persona_sessions;"));
        Assert.Equal(1, Scalar(after, $"SELECT COUNT(*) FROM score_events WHERE persona_session_id = {b};"));
        Assert.Equal(1120, Scalar(after, "SELECT id FROM trimmed;"));
        Assert.Equal(0, Scalar(after, $"SELECT COUNT(*) FROM session_names WHERE persona_session_id = {b};"));
        Assert.Equal(0, Scalar(after, $"SELECT COUNT(*) FROM session_tags WHERE persona_session_id = {b};"));
        Assert.Equal(1, Scalar(after, $"SELECT COUNT(*) FROM session_names WHERE persona_session_id = {a};"));
        Assert.DoesNotContain(b, SessionHistoryReader.Read(Db, 10_000).Select(s => s.Id));
        Assert.Contains(a, SessionHistoryReader.Read(Db, 10_000).Select(s => s.Id));
    }

    [Fact]
    public void Delete_RefusesARunWithNoEnd_AndChangesNothing()
    {
        long session;
        using (var db = MuckaDb.Open(Db))
        {
            var run = Run(db, null);
            session = Login(db, run, 100, null);
            Rows(db, run, 150, 250);
        }

        Assert.Throws<InvalidOperationException>(() => SessionRecording.Delete(Db, session));
        Assert.Equal(2, SliceOf(session).Count);
    }

    [Fact]
    public void Shrink_TakesOutAFilesFreePages_AndKeepsWhatIsInIt()
    {
        using (var db = MuckaDb.Open(Db))
        {
            var run = Run(db, 1);
            using var insert = db.CreateCommand();
            insert.CommandText = "INSERT INTO wire (mucka_run_id, seq, ts_ms, direction, data) VALUES ($r, $q, 1, 0, $d);";
            insert.Parameters.AddWithValue("$r", run);
            var seq = insert.Parameters.Add("$q", SqliteType.Integer);
            insert.Parameters.AddWithValue("$d", new byte[2048]);
            using (var transaction = db.BeginTransaction())
            {
                insert.Transaction = transaction;
                for (var i = 0; i < 3000; i++) { seq.Value = i; insert.ExecuteNonQuery(); }
                transaction.Commit();
            }
            Insert(db, "DELETE FROM wire WHERE seq > 0;");
        }
        TestStore.ReleasePools(_directory);

        var result = DatabaseVacuum.Shrink(Db);

        Assert.True(result.BytesBefore > 4 * 1024 * 1024, "the setup was meant to leave several MB of free pages");
        Assert.True(result.BytesAfter < result.BytesBefore / 2,
            $"the file was {result.BytesBefore:N0} bytes and is {result.BytesAfter:N0}");
        using var after = MuckaDb.OpenRead(Db);
        Assert.Equal(1, Scalar(after, "SELECT COUNT(*) FROM wire;"));
    }
}
