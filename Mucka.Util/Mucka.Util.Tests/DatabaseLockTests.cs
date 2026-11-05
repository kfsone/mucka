using Mucka.Combat;
using Mucka.Store;

namespace Mucka.Util.Tests;

/// <summary>
/// <see cref="DatabaseLock"/>: one holder at a time of the upgrade, backup and heal of a database,
/// across processes. Another process is played by a handle opened on the lock file with no sharing,
/// which is exactly what the system sees of one.
/// </summary>
public sealed class DatabaseLockTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mucka-lock-tests", Guid.NewGuid().ToString("N"));

    public DatabaseLockTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string DbPath => Path.Combine(_dir, MuckaDb.DefaultFileName);

    private FileStream AnotherProcessHolds()
        => new(DatabaseLock.PathFor(DbPath), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private bool AnotherProcessCouldTakeIt()
    {
        try
        {
            using (AnotherProcessHolds()) { }
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return condition();
    }

    [Fact]
    public void ALockNobodyHolds_IsTakenAtOnce_WithNoJob()
    {
        var tracker = new MaintenanceTracker();
        var changes = 0;
        tracker.Changed += () => changes++;

        using (DatabaseLock.Acquire(DbPath, tracker))
            Assert.False(AnotherProcessCouldTakeIt());

        Assert.Equal(0, changes);
        Assert.True(AnotherProcessCouldTakeIt());
    }

    [Fact]
    public void ALockAnotherProcessHolds_IsWaitedFor_UnderAJob_ThenTaken()
    {
        var tracker = new MaintenanceTracker();
        using var acquired = new ManualResetEventSlim();
        using var letGo = new ManualResetEventSlim();
        Exception? failure = null;
        var other = AnotherProcessHolds();
        var thread = new Thread(() =>
        {
            try
            {
                using (DatabaseLock.Acquire(DbPath, tracker))
                {
                    acquired.Set();
                    letGo.Wait();
                }
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.Start();

        Assert.True(WaitFor(() => tracker.IsActive));
        Assert.Equal(DatabaseLock.WaitingDetail, tracker.Detail);
        Assert.False(acquired.IsSet);

        other.Dispose();
        Assert.True(acquired.Wait(TimeSpan.FromSeconds(10)));

        Assert.False(tracker.IsActive);
        Assert.False(AnotherProcessCouldTakeIt());
        letGo.Set();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(failure);
        Assert.True(AnotherProcessCouldTakeIt());
    }

    [Fact]
    public void ALockLeftBehindByAProcessThatIsGone_IsJustAFile_AndIsTakenAtOnce()
    {
        using (AnotherProcessHolds()) { }
        Assert.True(File.Exists(DatabaseLock.PathFor(DbPath)));
        var tracker = new MaintenanceTracker();
        var changes = 0;
        tracker.Changed += () => changes++;

        using (DatabaseLock.Acquire(DbPath, tracker)) { }

        Assert.Equal(0, changes);
    }

    [Fact]
    public void TheLock_IsReentrantOnItsThread_AndLetGoWhenTheLastHolderIsDisposed()
    {
        var tracker = new MaintenanceTracker();
        var outer = DatabaseLock.Acquire(DbPath, tracker);
        var inner = DatabaseLock.Acquire(DbPath, tracker);

        inner.Dispose();
        Assert.False(AnotherProcessCouldTakeIt());
        outer.Dispose();
        Assert.True(AnotherProcessCouldTakeIt());
        Assert.False(tracker.IsActive);
    }

    [Fact]
    public void ADisposeThatHappensTwice_ReleasesOnce()
    {
        var tracker = new MaintenanceTracker();
        var outer = DatabaseLock.Acquire(DbPath, tracker);
        var inner = DatabaseLock.Acquire(DbPath, tracker);

        inner.Dispose();
        inner.Dispose();

        Assert.False(AnotherProcessCouldTakeIt());
        outer.Dispose();
    }

    [Fact]
    public void AnotherThreadOfTheProcess_WaitsForTheHolder_UnderAJob()
    {
        var tracker = new MaintenanceTracker();
        var held = DatabaseLock.Acquire(DbPath, tracker);
        using var acquired = new ManualResetEventSlim();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using (DatabaseLock.Acquire(DbPath, tracker))
                    acquired.Set();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.Start();

        Assert.True(WaitFor(() => tracker.IsActive));
        Assert.Equal(DatabaseLock.WaitingDetail, tracker.Detail);
        Assert.False(acquired.IsSet);

        held.Dispose();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));

        Assert.Null(failure);
        Assert.True(acquired.IsSet);
        Assert.False(tracker.IsActive);
        Assert.True(AnotherProcessCouldTakeIt());
    }

    [Fact]
    public void AWaitThatIsCancelled_Throws_AndLeavesNoJobOpen()
    {
        var tracker = new MaintenanceTracker();
        using var other = AnotherProcessHolds();
        using var cancel = new CancellationTokenSource();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { DatabaseLock.Acquire(DbPath, tracker, cancellationToken: cancel.Token); }
            catch (Exception ex) { failure = ex; }
        });
        thread.Start();
        Assert.True(WaitFor(() => tracker.IsActive));

        cancel.Cancel();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));

        Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.False(tracker.IsActive);
    }

    [Fact]
    public void TheWaitingJob_CanBeGivenUpOn_AndThatEndsTheWaitWithACancellation()
    {
        var tracker = new MaintenanceTracker();
        using var other = AnotherProcessHolds();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { DatabaseLock.Acquire(DbPath, tracker); }
            catch (Exception ex) { failure = ex; }
        });
        thread.Start();
        Assert.True(WaitFor(() => tracker.IsActive));
        Assert.True(tracker.CanCancel);

        tracker.Cancel();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));

        Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.False(tracker.IsActive);
        Assert.False(tracker.CanCancel);
    }

    [Fact]
    public void AWaitThatCannotBeGivenUpOn_OffersNoWayOut()
    {
        var tracker = new MaintenanceTracker();
        var other = AnotherProcessHolds();
        var thread = new Thread(() => DatabaseLock.Acquire(DbPath, tracker, canGiveUp: false).Dispose());
        thread.Start();

        Assert.True(WaitFor(() => tracker.IsActive));
        Assert.Equal(DatabaseLock.WaitingDetail, tracker.Detail);
        Assert.False(tracker.CanCancel);

        other.Dispose();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void ANewFile_IsCreatedUnderTheLock_SoTwoFirstLaunchesDoNotBothRunTheScripts()
    {
        var other = AnotherProcessHolds();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { using (MuckaDb.Open(DbPath)) { } }
            catch (Exception ex) { failure = ex; }
        });
        thread.Start();

        Assert.False(thread.Join(TimeSpan.FromMilliseconds(600)));

        other.Dispose();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        Assert.Null(failure);
    }

    [Fact]
    public void AJobThatIsNotWaiting_OffersNoWayOut_AndCancelDoesNothing()
    {
        var tracker = new MaintenanceTracker();
        using var job = tracker.Begin("Upgrading the database");

        Assert.False(tracker.CanCancel);
        tracker.Cancel();

        Assert.True(tracker.IsActive);
    }

    [Fact]
    public void ABackup_WaitsForTheLock_AndIsTakenOnceItIsFree()
    {
        using (var seed = MuckaDb.Open(DbPath)) { }
        var other = AnotherProcessHolds();
        long? taken = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { taken = DatabaseBackup.TakeOnce(DbPath); }
            catch (Exception ex) { failure = ex; }
        });
        thread.Start();

        Assert.False(thread.Join(TimeSpan.FromMilliseconds(600)));
        Assert.False(File.Exists(DatabaseBackup.PathFor(DbPath)));

        other.Dispose();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));

        Assert.Null(failure);
        Assert.NotNull(taken);
        Assert.True(File.Exists(DatabaseBackup.PathFor(DbPath)));
    }

    [Fact]
    public void ATrimVerb_WaitsForTheLock_BeforeItTouchesTheFile()
    {
        using (var seed = MuckaDb.Open(DbPath)) { }
        var other = AnotherProcessHolds();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { WireTrim.Expire(DbPath, WireTrim.DefaultDays, DateTimeOffset.UtcNow); }
            catch (Exception ex) { failure = ex; }
        });
        thread.Start();

        Assert.False(thread.Join(TimeSpan.FromMilliseconds(600)));

        other.Dispose();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(failure);
    }

    [Fact]
    public void ADirectoryThatDoesNotExist_Throws_RatherThanWaitingForeverForAHolderThatIsNotThere()
    {
        var missing = Path.Combine(_dir, "no-such-directory", MuckaDb.DefaultFileName);

        Assert.ThrowsAny<IOException>(() => DatabaseLock.Acquire(missing, new MaintenanceTracker()));
    }
}
