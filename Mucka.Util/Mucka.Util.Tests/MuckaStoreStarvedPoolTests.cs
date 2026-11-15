using Mucka.Store;
using Mucka.WireLog;

namespace Mucka.Util.Tests;

/// <summary>This class fills the thread pool, so it runs with nothing else running: any other test
/// that needed a pool thread meanwhile would stall behind it.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class StarvedPoolRunsAlone
{
    public const string Name = "StarvedPool";
}

/// <summary>
/// The store's writer needs no thread from the thread pool: with every pool thread blocked,
/// <see cref="MuckaStore.Dispose"/> still drains inside <see cref="MuckaStore.DrainTimeout"/> and the
/// session's end time is stamped. A pool-task writer failed this with an empty table after 6 s.
/// </summary>
[Collection(StarvedPoolRunsAlone.Name)]
public sealed class MuckaStoreStarvedPoolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mucka-starved-pool-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        TestStore.ReleasePools(_dir);
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>Holds a pool thread until the gate opens. A sleep, not a wait on the gate: with blockers
    /// that waited, a pool-task writer still drained and this test passed against it.</summary>
    private static void Block(ManualResetEventSlim gate)
    {
        while (!gate.IsSet)
            Thread.Sleep(10);
    }

    [Fact]
    public void Dispose_drains_and_stamps_the_end_while_every_pool_thread_is_blocked()
    {
        var path = Path.Combine(_dir, MuckaDb.DefaultFileName);

        // Never disposed: the blockers read it as they leave, and a disposed event would throw on a
        // pool thread and take the test host down.
        var gate = new ManualResetEventSlim();
        try
        {
            // Far more blockers than the pool can start threads for while this test runs, so work queued
            // after them is not reached until the gate opens.
            for (var i = 0; i < Environment.ProcessorCount * 64; i++)
                ThreadPool.QueueUserWorkItem(static g => Block((ManualResetEventSlim)g!), gate, preferLocal: false);
            Assert.True(ThreadPool.PendingWorkItemCount > 0, "the pool is not starved, so this proves nothing");

            var store = TestStore.Open(path);
            using (var writer = new WireLogWriter(store))
            {
                writer.RecordRx([1, 2, 3]);
                writer.RecordTx([4, 5]);
            }
            TestStore.Close(store);

            // Read while the pool is still blocked: nothing that follows may rescue the write.
            Assert.True(ThreadPool.PendingWorkItemCount > 0, "the pool was released before the store closed");
            var session = Assert.Single(WireLogExport.ListSessions(path));
            Assert.Equal(2, session.Records);
            Assert.NotNull(session.EndedMs);
        }
        finally
        {
            gate.Set();
        }
    }
}
