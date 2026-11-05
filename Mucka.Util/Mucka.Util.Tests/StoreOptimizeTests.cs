using Microsoft.Data.Sqlite;
using Mucka.Store;
using Mucka.WireLog;

namespace Mucka.Util.Tests;

/// <summary>
/// <see cref="MuckaStore.Optimize"/>: the file shrinks, and the writer is paused rather than broken -
/// what the client records while it runs is written once it finishes.
/// </summary>
public sealed class StoreOptimizeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mucka-optimize-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        TestStore.ReleasePools(_dir);
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string DbPath => Path.Combine(_dir, MuckaDb.DefaultFileName);

    private static long Count(string path)
    {
        using var connection = MuckaDb.OpenRead(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM wire;";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void TheFile_ShrinksOnceItsFreePagesAreGone()
    {
        // A run's worth of rows written and then removed leaves free pages the file keeps.
        using (var connection = MuckaDb.Open(DbPath))
        {
            using var run = connection.CreateCommand();
            run.CommandText = "INSERT INTO mucka_runs (uid, started_ms, host) VALUES ('a', 1, 'test');";
            run.ExecuteNonQuery();
            using var insert = connection.CreateCommand();
            insert.CommandText =
                "INSERT INTO wire (mucka_run_id, seq, ts_ms, direction, data) VALUES (1, $q, 1, 0, $d);";
            var seq = insert.Parameters.Add("$q", SqliteType.Integer);
            insert.Parameters.AddWithValue("$d", new byte[2048]);
            using var transaction = connection.BeginTransaction();
            insert.Transaction = transaction;
            for (var i = 0; i < 3000; i++) { seq.Value = i; insert.ExecuteNonQuery(); }
            transaction.Commit();
            using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM wire;";
            delete.ExecuteNonQuery();
        }
        TestStore.ReleasePools(_dir);

        using var store = TestStore.Open(DbPath);
        var result = store.Optimize();

        Assert.True(result.BytesBefore > 4 * 1024 * 1024, "the setup was meant to leave several MB of free pages");
        Assert.True(result.BytesAfter < result.BytesBefore / 2,
            $"the file was {result.BytesBefore:N0} bytes and is {result.BytesAfter:N0}");
    }

    [Fact]
    public async Task WhatIsRecordedWhileItRuns_IsWrittenOnceItFinishes()
    {
        const int rows = 20000;
        var done = false;
        using (var store = TestStore.Open(DbPath))
        {
            using var writer = new WireLogWriter(store);
            var optimizing = Task.Run(() =>
            {
                // Back to back for as long as the rows are coming, so a batch is open on some of them.
                while (!Volatile.Read(ref done)) store.Optimize();
            });
            var payload = new byte[64];
            for (var i = 0; i < rows; i++)
            {
                writer.RecordRx(payload);
                if (i % 200 == 0) Thread.Yield();
            }
            Volatile.Write(ref done, true);
            await optimizing;
        }

        Assert.Equal(rows, Count(DbPath));
    }

    /// <summary>A row whose write holds the batch open until released.</summary>
    private sealed class HoldsTheBatchOpen(ManualResetEventSlim inside, ManualResetEventSlim release) : IStoreRow
    {
        public void Write(StoreWrite write)
        {
            inside.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        }
    }

    [Fact]
    public async Task Optimize_WaitsForABatchThatIsOpen_ThenRuns()
    {
        using var store = TestStore.Open(DbPath);
        using var inside = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        store.Enqueue(new HoldsTheBatchOpen(inside, release));
        Assert.True(inside.Wait(TimeSpan.FromSeconds(10)), "the writer never reached the row");

        var optimizing = Task.Run(() => store.Optimize());
        var first = await Task.WhenAny(optimizing, Task.Delay(500));
        Assert.NotSame(optimizing, first);   // still waiting: it has not compacted under an open batch

        release.Set();
        await optimizing;
    }

    [Fact]
    public void ADisposedStore_CannotBeOptimized()
    {
        var store = TestStore.Open(DbPath);
        store.Dispose();

        Assert.Throws<InvalidOperationException>(() => store.Optimize());
    }
}
