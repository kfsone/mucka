using System.Globalization;
using Microsoft.Data.Sqlite;
using Mucka.Store;

namespace Mucka.Util.Tests;

/// <summary>
/// The arbiter itself, driven directly rather than through a producer.
///
/// <para>Everything here was previously covered only sideways, through <c>WireLogTests</c> and
/// <c>FightHistoryStoreTests</c> - which means the properties those tests DEPEND on were never
/// stated. The batch is all-or-nothing, a row that throws faults the store rather than being
/// swallowed, the fault is reported once, and <c>Dispose</c> is bounded. Each of those is a decision
/// recorded in <c>docs/persistence-design.md</c>, and a decision nothing asserts is a decision that
/// can be reversed by accident.</para>
///
/// <para>Rows here are test doubles because the point is the arbiter's contract, not any particular
/// table's SQL. They write into <c>wire</c> because it is the one table whose only foreign key is the
/// store's own session row.</para>
/// </summary>
public sealed class MuckaStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mucka-store-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string DbPath => Path.Combine(_directory, MuckaDb.DefaultFileName);

    /// <summary>One row that lands in <c>wire</c>, identifiable by its seq.</summary>
    private sealed record GoodRow(long SessionId, int Seq) : IStoreRow
    {
        public void Write(StoreWrite write)
        {
            var command = write.Prepared(
                "INSERT INTO wire (mucka_run_id, seq, ts_ms, direction, data) VALUES ($s,$q,$t,0,$d);");
            command.Parameters.AddWithValue("$s", SessionId);
            command.Parameters.AddWithValue("$q", Seq);
            command.Parameters.AddWithValue("$t", 1_787_000_000_000L + Seq);
            command.Parameters.Add("$d", SqliteType.Blob).Value = new byte[] { (byte)Seq };
            command.ExecuteNonQuery();
        }
    }

    /// <summary>A row that fails the way a schema bug fails: on the write, every time.</summary>
    private sealed record PoisonRow : IStoreRow
    {
        public void Write(StoreWrite write) => throw new InvalidOperationException("poisoned row");
    }

    /// <summary>A row that holds the writer inside its transaction until released. Lets a test decide
    /// exactly which rows share one batch, which is otherwise at the scheduler's mercy.
    /// <para><see cref="Entered"/> is what a test waits on: it is set from INSIDE the write, so it is
    /// proof the writer is in the transaction rather than a guess that it probably is by now.</para>
    /// </summary>
    private sealed record GateRow(ManualResetEventSlim Entered, ManualResetEventSlim Release) : IStoreRow
    {
        public void Write(StoreWrite write)
        {
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(30));
        }
    }

    private long CountWire()
    {
        using var connection = MuckaDb.OpenRead(DbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM wire;";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Smoke wait, and the one wall-clock wait in this file that cannot be closed by construction:
    /// what it waits on is a real background writer thread reaching a real sqlite commit, so there
    /// is no clock to inject - substituting one would leave the thread untested, which is the whole
    /// subject here. The 10 s bound is set against a commit that normally takes single-digit
    /// milliseconds, so it is a hang detector rather than a timing assertion.
    /// </summary>
    private static void WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) Thread.Sleep(10);
        Assert.True(condition(), what);
    }

    [Fact]
    public void Everything_queued_before_Dispose_is_committed()
    {
        // The drain guarantee the whole design rests on: a producer hands rows over and never waits,
        // so the only thing that can promise they reached disk is Dispose.
        using (var store = new MuckaStore(DbPath, "test"))
            for (var i = 0; i < 500; i++)
                store.Enqueue(new GoodRow(store.SessionId, i));

        Assert.Equal(500, CountWire());
    }

    [Fact]
    public void A_batch_is_all_or_nothing_when_a_row_throws_partway_through()
    {
        // Atomicity, stated rather than assumed. The gate row holds the writer inside its
        // transaction, so the rows enqueued behind it are guaranteed to be in the SAME batch as the
        // poison row - otherwise which rows share a transaction is up to the scheduler and the test
        // would pass for the wrong reason.
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var store = new MuckaStore(DbPath, "test");
        try
        {
            store.Enqueue(new GateRow(entered, release));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)),
                "the writer never entered the gate row's transaction");

            store.Enqueue(new GoodRow(store.SessionId, 1));
            store.Enqueue(new GoodRow(store.SessionId, 2));
            store.Enqueue(new PoisonRow());
            release.Set();

            WaitUntil(() => store.IsFaulted, "the poison row should have faulted the store");
        }
        finally
        {
            release.Set();
            store.Dispose();
        }

        // The two good rows shared the poison row's transaction, so they must have rolled back with
        // it. A partial batch would be worse than none: the log would look whole and not be.
        Assert.Equal(0, CountWire());
    }

    /// <summary>The one error this store ever reports has to be diagnosable on its own, because it is
    /// the only thing anyone will see. A row throws for two reasons - a dead database, or a schema bug
    /// - and for a schema bug the row TYPE is the answer. It must also say how much recording went
    /// with it: the rows queued behind the bad one are unrelated to it and are dropped regardless, and
    /// a silent drop of four hundred rows reads exactly like a silent drop of none.</summary>
    [Fact]
    public void The_fault_report_names_the_row_that_failed_and_how_much_was_dropped()
    {
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var reports = new List<string>();

        var store = new MuckaStore(DbPath, "test", null, (context, ex) => reports.Add(context + " :: " + ex.Message));
        try
        {
            // Hold the writer inside a transaction so the rows below are all queued behind it, then
            // release: the poison row faults, and everything after it is discarded unwritten.
            store.Enqueue(new GateRow(entered, release));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)),
                "the writer never entered the gate row's transaction");

            store.Enqueue(new PoisonRow());
            store.Enqueue(new GoodRow(store.SessionId, 1));
            store.Enqueue(new GoodRow(store.SessionId, 2));
            store.Enqueue(new GoodRow(store.SessionId, 3));
            release.Set();

            WaitUntil(() => store.IsFaulted, "the poison row should have faulted the store");
        }
        finally
        {
            release.Set();
            store.Dispose();
        }

        var report = Assert.Single(reports);
        Assert.Contains(nameof(PoisonRow), report);
        Assert.Contains("poisoned row", report);   // the original cause survives the wrapping
        Assert.Contains("3 queued rows dropped", report);
    }

    [Fact]
    public void A_row_that_throws_faults_the_store_reports_once_and_stops_accepting()
    {
        // One failure policy, not two. The writers this replaced caught per row and carried on, which
        // produces an error per row forever and never says the recording stopped being trustworthy.
        var failures = new List<string>();
        var store = new MuckaStore(DbPath, "test",
            onError: (context, _) => { lock (failures) failures.Add(context); });
        try
        {
            store.Enqueue(new GoodRow(store.SessionId, 1));
            store.Enqueue(new PoisonRow());
            WaitUntil(() => store.IsFaulted, "the poison row should have faulted the store");

            for (var i = 0; i < 1000; i++)
                store.Enqueue(new GoodRow(store.SessionId, 100 + i));   // must all be no-ops
            Assert.True(store.IsFaulted);
        }
        finally
        {
            store.Dispose();
        }

        lock (failures)
        {
            var only = Assert.Single(failures);
            Assert.Contains("recording stopped", only, StringComparison.Ordinal);
        }
        Assert.Equal(0, CountWire());
    }

    [Fact]
    public void A_store_that_cannot_open_comes_up_faulted_without_throwing()
    {
        // The point of the client is to play MUD2: a database that cannot be opened reports and gets
        // out of the way. It must not throw out of the connect path.
        var blocked = Path.Combine(_directory, "not-a-directory");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(blocked, "this is a file, so it cannot also be a directory");

        var failures = new List<string>();
        using var store = new MuckaStore(Path.Combine(blocked, MuckaDb.DefaultFileName), "test",
            onError: (context, _) => failures.Add(context));

        Assert.True(store.IsFaulted);
        var only = Assert.Single(failures);
        Assert.Contains("nothing will be recorded", only, StringComparison.Ordinal);
        store.Enqueue(new PoisonRow());     // does not throw, does not reach a writer
    }

    [Fact]
    public void Dispose_is_bounded_by_DrainTimeout_even_when_the_writer_is_wedged()
    {
        // An app exit must not hang on the recorder. This is the one slow test in the suite: it holds
        // the writer for the whole of DrainTimeout on purpose, because the bound IS the property.
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var store = new MuckaStore(DbPath, "test");
        store.Enqueue(new GateRow(entered, release));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)),
            "the writer never entered the gate row, so nothing would have been wedged to time out");

        var started = DateTime.UtcNow;
        store.Dispose();
        var waited = DateTime.UtcNow - started;
        release.Set();

        Assert.True(waited >= MuckaStore.DrainTimeout - TimeSpan.FromMilliseconds(250),
            $"Dispose returned after {waited.TotalSeconds:F1}s without waiting for the wedged writer: "
            + "the test proved nothing about the bound");

        Assert.True(waited < MuckaStore.DrainTimeout + TimeSpan.FromSeconds(5),
            $"Dispose waited {waited.TotalSeconds:F1}s, past DrainTimeout "
            + $"({MuckaStore.DrainTimeout.TotalSeconds:F0}s) plus slack");
    }

    [Fact]
    public void Dispose_is_safe_twice_and_Enqueue_after_it_is_a_no_op()
    {
        var store = new MuckaStore(DbPath, "test");
        store.Enqueue(new GoodRow(store.SessionId, 1));
        store.Dispose();
        store.Dispose();
        store.Enqueue(new GoodRow(store.SessionId, 2));   // accepted silently, never written
        store.Enqueue(new PoisonRow());                   // and cannot fault a dead store

        Assert.Equal(1, CountWire());
    }

    [Fact]
    public void One_store_is_one_session_row_opened_at_construction_and_closed_at_Dispose()
    {
        long id;
        using (var store = new MuckaStore(DbPath, "mud2.co.uk", "9.9.9"))
        {
            id = store.SessionId;

            // Open and readable immediately: the session row is written before the writer task
            // starts, so nothing has to reach into the connection behind the writer's back.
            using var live = MuckaDb.OpenRead(DbPath);
            using var probe = live.CreateCommand();
            probe.CommandText = "SELECT host, client_version, started_ms, ended_ms FROM mucka_runs WHERE id = $id;";
            probe.Parameters.AddWithValue("$id", id);
            using var reader = probe.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("mud2.co.uk", reader.GetString(0));
            Assert.Equal("9.9.9", reader.GetString(1));
            Assert.True(reader.GetInt64(2) > 0);
            Assert.True(reader.IsDBNull(3), "ended_ms is stamped by Dispose, not before");
        }

        using var connection = MuckaDb.OpenRead(DbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), SUM(ended_ms IS NOT NULL) FROM mucka_runs;";
        using var after = command.ExecuteReader();
        Assert.True(after.Read());
        Assert.Equal(1, after.GetInt32(0));
        Assert.Equal(1, after.GetInt32(1));
    }

    /// <summary>
    /// The two persona-session writes, read back off disk. These bypass the background writer and
    /// open their own connection, so nothing else in this fixture speaks for them, and they are what
    /// attributes a login to a character and records how it ended.
    ///
    /// <para>Two rows are opened and only the first is written to, which is what makes the three
    /// mutations below distinguishable rather than merely detectable:</para>
    /// <list type="bullet">
    /// <item><b>An empty <c>UpdatePersonaSession</c> body</b> - the first row's persona, ended_ms and
    /// ended_note all stay NULL.</item>
    /// <item><b>The <c>$ended</c> and <c>$value</c> bindings swapped</b> - observed: the name write,
    /// which passes no <c>endedMs</c>, is then left with <c>$value</c> unbound, sqlite refuses the
    /// command, and <c>UpdatePersonaSession</c> swallows it. The error-callback assertion below is
    /// what catches that; the read-backs alone would only see a NULL persona.</item>
    /// <item><b><c>WHERE id</c> dropped, or turned into <c>WHERE id = $ended</c></b> - dropped, every
    /// row is updated and the untouched second row stops being NULL; pointed at <c>$ended</c>, no row
    /// matches on the end write and the name write throws on an unbound parameter, which
    /// <c>UpdatePersonaSession</c> swallows - hence the error-callback assertion, which is the only
    /// thing that separates "swallowed a broken statement" from "wrote nothing".</item>
    /// </list>
    /// </summary>
    [Fact]
    public void A_persona_session_is_named_and_closed_on_its_own_row_and_no_other()
    {
        const long EndedMs = 1_787_000_123_456;
        var failures = new List<string>();

        long named, untouched;
        using (var store = new MuckaStore(DbPath, "test", onError: (context, ex) => failures.Add(context + " :: " + ex.Message)))
        {
            named = store.BeginPersonaSession(1_787_000_000_000, "mud2.co.uk")
                ?? throw new InvalidOperationException("BeginPersonaSession returned no id");
            untouched = store.BeginPersonaSession(1_787_000_000_001, "mud2.co.uk")
                ?? throw new InvalidOperationException("BeginPersonaSession returned no id");
            Assert.NotEqual(named, untouched);

            store.NamePersonaSession(named, "Ollie");
            store.EndPersonaSession(named, EndedMs, PersonaSessionEnd.Quit);
        }

        // Nothing was allowed to fail quietly: both writes swallow their exceptions, so a statement
        // that never executed would otherwise be indistinguishable from one that wrote nothing.
        Assert.Empty(failures);

        using var connection = MuckaDb.OpenRead(DbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT persona, ended_ms, ended_note FROM persona_sessions WHERE id = $id;";
        command.Parameters.AddWithValue("$id", named);
        using (var reader = command.ExecuteReader())
        {
            Assert.True(reader.Read(), "the persona session row went missing");
            Assert.False(reader.IsDBNull(0), "persona was never written");
            Assert.False(reader.IsDBNull(1), "ended_ms was never written");
            Assert.False(reader.IsDBNull(2), "ended_note was never written");
            Assert.Equal("Ollie", reader.GetString(0));
            Assert.Equal(EndedMs, reader.GetInt64(1));
            Assert.Equal(PersonaSessionEnd.Quit, reader.GetString(2));
        }

        command.Parameters["$id"].Value = untouched;
        using var other = command.ExecuteReader();
        Assert.True(other.Read(), "the second persona session row went missing");
        Assert.True(other.IsDBNull(0), "the name write reached a row it was not given");
        Assert.True(other.IsDBNull(1), "the end write reached a row it was not given");
        Assert.True(other.IsDBNull(2), "the end write reached a row it was not given");
    }

    [Fact]
    public void Rows_are_committed_in_the_order_they_were_enqueued_across_producers()
    {
        // One channel, not one per producer: the order between a swing, a diagnose reading and a
        // score announcement made in the same breath is the whole basis on which an award is later
        // attributed to a kill. Two threads enqueueing interleaved proves the arbiter preserves it.
        const int Each = 2000;
        var store = new MuckaStore(DbPath, "test");
        var handed = new System.Collections.Concurrent.ConcurrentQueue<int>();
        var next = 0;
        var gate = new object();

        void Produce()
        {
            for (var i = 0; i < Each; i++)
                lock (gate)
                {
                    var seq = next++;
                    store.Enqueue(new GoodRow(store.SessionId, seq));
                    handed.Enqueue(seq);
                }
        }

        var a = new Thread(Produce);
        var b = new Thread(Produce);
        a.Start(); b.Start(); a.Join(); b.Join();
        store.Dispose();

        using var connection = MuckaDb.OpenRead(DbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT seq FROM wire ORDER BY id;";
        using var reader = command.ExecuteReader();
        var committed = new List<int>();
        while (reader.Read()) committed.Add(reader.GetInt32(0));

        Assert.Equal(handed.ToList(), committed);
    }
}
