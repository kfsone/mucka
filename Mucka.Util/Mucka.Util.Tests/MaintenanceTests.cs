using Microsoft.Data.Sqlite;
using Mucka.Combat;
using Mucka.Store;
using Mucka.WireLog;

namespace Mucka.Util.Tests;

/// <summary>
/// What the maintenance overlay is driven by: <see cref="MaintenanceTracker"/>, the schema upgrade that
/// opens a job on it, the backup a heal takes first, and the count that says whether a heal has work.
/// Each test uses a tracker of its own, never <see cref="Maintenance.Current"/>, which other tests
/// running at the same time open jobs on.
/// </summary>
public sealed class MaintenanceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mucka-maintenance-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        TestStore.ReleasePools(_dir);
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string DbPath => Path.Combine(_dir, MuckaDb.DefaultFileName);

    private static void Run(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Scalar(string path, string sql)
    {
        using var connection = MuckaDb.OpenRead(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    // -- The tracker -----------------------------------------------------------------------------------

    [Fact]
    public void AJob_IsActiveUntilItIsDisposed_AndEachChangeIsAnnounced()
    {
        var tracker = new MaintenanceTracker();
        var changes = 0;
        tracker.Changed += () => changes++;
        Assert.False(tracker.IsActive);

        var job = tracker.Begin("one");
        Assert.True(tracker.IsActive);
        Assert.Equal("one", tracker.Detail);
        job.Detail = "two";
        Assert.Equal("two", tracker.Detail);
        job.Detail = "two";   // unchanged: nothing to announce
        job.Dispose();

        Assert.False(tracker.IsActive);
        Assert.Equal(string.Empty, tracker.Detail);
        Assert.Equal(3, changes);
    }

    [Fact]
    public void WithTwoJobsOpen_TheNewestIsShown_AndTheOverlayStaysUntilBothAreDone()
    {
        var tracker = new MaintenanceTracker();
        var outer = tracker.Begin("outer");
        var inner = tracker.Begin("inner");
        Assert.Equal("inner", tracker.Detail);

        inner.Dispose();
        Assert.True(tracker.IsActive);
        Assert.Equal("outer", tracker.Detail);

        outer.Dispose();
        Assert.False(tracker.IsActive);
    }

    [Fact]
    public void DisposingAJobTwice_ClosesItOnce()
    {
        var tracker = new MaintenanceTracker();
        var first = tracker.Begin("first");
        using var second = tracker.Begin("second");

        first.Dispose();
        first.Dispose();

        Assert.True(tracker.IsActive);
        Assert.Equal("second", tracker.Detail);
    }

    // -- A schema upgrade --------------------------------------------------------------------------------

    [Fact]
    public void ANewFile_IsCreatedWithNoJob()
    {
        var tracker = new MaintenanceTracker();
        var changes = 0;
        tracker.Changed += () => changes++;
        Directory.CreateDirectory(_dir);

        using (var connection = new SqliteConnection(MuckaDb.ConnectionString(DbPath)))
        {
            connection.Open();
            MuckaDb.ApplySchema(connection, tracker);
        }

        Assert.Equal(0, changes);
    }

    [Fact]
    public void AFileWithTablesAndNoJournal_OpensAJob_AndClosesItAfter()
    {
        Directory.CreateDirectory(_dir);
        var tracker = new MaintenanceTracker();
        var seen = new List<string>();
        tracker.Changed += () => { if (tracker.IsActive) seen.Add(tracker.Detail); };

        using (var connection = new SqliteConnection(MuckaDb.ConnectionString(DbPath)))
        {
            connection.Open();
            // The baseline run by hand: the tables are there and nothing has been journaled.
            Run(connection, MigrationScripts.All[0].Contents);
            MuckaDb.ApplySchema(connection, tracker);
        }

        Assert.Contains(MuckaDb.UpgradeDetail, seen);
        Assert.False(tracker.IsActive);
    }

    [Fact]
    public void AFileWithAJournalAndAScriptStillToRun_OpensAJob()
    {
        using (var connection = MuckaDb.Open(DbPath))
            Run(connection, "DELETE FROM SchemaVersions WHERE ScriptName = (SELECT MAX(ScriptName) FROM SchemaVersions);");
        var tracker = new MaintenanceTracker();
        var seen = new List<string>();
        tracker.Changed += () => { if (tracker.IsActive) seen.Add(tracker.Detail); };

        using (var connection = new SqliteConnection(MuckaDb.ConnectionString(DbPath)))
        {
            connection.Open();
            MuckaDb.ApplySchema(connection, tracker);
        }

        Assert.Contains(MuckaDb.UpgradeDetail, seen);
        Assert.False(tracker.IsActive);
    }

    [Fact]
    public void AFileThatIsAlreadyCurrent_OpensNoJob()
    {
        using (MuckaDb.Open(DbPath)) { }
        var tracker = new MaintenanceTracker();
        var changes = 0;
        tracker.Changed += () => changes++;

        using (var connection = new SqliteConnection(MuckaDb.ConnectionString(DbPath)))
        {
            connection.Open();
            MuckaDb.ApplySchema(connection, tracker);
        }

        Assert.Equal(0, changes);
    }

    // -- The backup before an upgrade ------------------------------------------------------------------------

    private const string JournalTables = "SELECT COUNT(*) FROM sqlite_master WHERE name = 'SchemaVersions';";

    // A file as an older release left it: the baseline's tables, nothing journaled.
    private void WriteAnUnjournaledFile()
    {
        Directory.CreateDirectory(_dir);
        using var connection = new SqliteConnection(MuckaDb.ConnectionString(DbPath));
        connection.Open();
        Run(connection, MigrationScripts.All[0].Contents);
    }

    private void Upgrade(MaintenanceTracker tracker)
    {
        using var connection = new SqliteConnection(MuckaDb.ConnectionString(DbPath));
        connection.Open();
        // The path as the test spells it, as MuckaDb.Open does: on a Mac the connection's own DataSource
        // is the resolved form (/private/var/... for a path under /var/...), and the backup record is
        // keyed by the spelling.
        MuckaDb.ApplySchema(connection, tracker, databasePath: DbPath);
    }

    [Fact]
    public void AnUpgradeOfAnExistingFile_TakesTheBackupFirst_SoItHoldsTheFileAsItWas()
    {
        WriteAnUnjournaledFile();
        var tracker = new MaintenanceTracker();
        var seen = new List<string>();
        tracker.Changed += () => { if (tracker.IsActive) seen.Add(tracker.Detail); };

        Upgrade(tracker);

        Assert.Equal(1, Scalar(DbPath, JournalTables));
        Assert.Equal(0, Scalar(DatabaseBackup.PathFor(DbPath), JournalTables));
        Assert.Equal([MuckaDb.UpgradeDetail, MuckaDb.BackupDetail, MuckaDb.UpgradeDetail], seen);
    }

    [Fact]
    public void AScriptStillToRunOnAJournaledFile_IsBackedUpFirstToo()
    {
        // The last two: the newest only adds, and the one before it does not.
        using (var connection = MuckaDb.Open(DbPath))
            Run(connection, "DELETE FROM SchemaVersions WHERE ScriptName IN (SELECT ScriptName FROM SchemaVersions ORDER BY ScriptName DESC LIMIT 2);");
        var before = Scalar(DbPath, "SELECT COUNT(*) FROM SchemaVersions;");
        File.Delete(DatabaseBackup.PathFor(DbPath));
        // The record is per process and the path is this test's own, so nothing is held yet.
        Assert.False(DatabaseBackup.HoldsBackup(DbPath));

        try { Upgrade(new MaintenanceTracker()); }
        catch (InvalidOperationException) { }

        Assert.Equal(before, Scalar(DatabaseBackup.PathFor(DbPath), "SELECT COUNT(*) FROM SchemaVersions;"));
    }

    [Fact]
    public void AnUpgradeWhosePendingScriptsOnlyAdd_TakesNoBackup_ButStillRunsThem()
    {
        using (var connection = MuckaDb.Open(DbPath))
            Run(connection, "DELETE FROM SchemaVersions WHERE ScriptName = '0009_wire_run_index.sql';");
        File.Delete(DatabaseBackup.PathFor(DbPath));
        var tracker = new MaintenanceTracker();
        var seen = new List<string>();
        tracker.Changed += () => { if (tracker.IsActive) seen.Add(tracker.Detail); };

        Upgrade(tracker);

        Assert.False(File.Exists(DatabaseBackup.PathFor(DbPath)));
        Assert.Equal(1, Scalar(DbPath, "SELECT COUNT(*) FROM SchemaVersions WHERE ScriptName = '0009_wire_run_index.sql';"));
        Assert.Equal([MuckaDb.UpgradeDetail], seen);
    }

    [Fact]
    public void ANewFile_AndACurrentFile_TakeNoBackup()
    {
        using (MuckaDb.Open(DbPath)) { }
        Assert.False(File.Exists(DatabaseBackup.PathFor(DbPath)));

        using (MuckaDb.Open(DbPath)) { }

        Assert.False(File.Exists(DatabaseBackup.PathFor(DbPath)));
    }

    [Fact]
    public void AnUpgradeThatOptsOut_TakesNoBackup()
    {
        WriteAnUnjournaledFile();

        using (MuckaDb.Open(DbPath, backUpBeforeUpgrade: false)) { }

        Assert.Equal(1, Scalar(DbPath, JournalTables));
        Assert.False(File.Exists(DatabaseBackup.PathFor(DbPath)));
    }

    [Fact]
    public void AnUpgradeAfterTheLaunchHasABackup_LeavesThatBackupWhereItIs()
    {
        WriteAnUnjournaledFile();
        File.WriteAllText(DatabaseBackup.PathFor(DbPath), "the import's copy");
        DatabaseBackup.NoteKept(DbPath);

        Upgrade(new MaintenanceTracker());

        Assert.Equal(1, Scalar(DbPath, JournalTables));
        Assert.Equal("the import's copy", File.ReadAllText(DatabaseBackup.PathFor(DbPath)));
    }

    [Fact]
    public void AnUpgrade_WhoseBackupFails_MigratesNothing()
    {
        WriteAnUnjournaledFile();
        // A directory where the backup is staged: the copy cannot be made.
        Directory.CreateDirectory(DatabaseBackup.PathFor(DbPath) + ".new");

        Assert.ThrowsAny<Exception>(() => Upgrade(new MaintenanceTracker()));

        Assert.Equal(0, Scalar(DbPath, JournalTables));
        Assert.False(DatabaseBackup.HoldsBackup(DbPath));
    }

    // -- The backup -----------------------------------------------------------------------------------------

    private void WriteARun(string uid, bool ended)
    {
        using var connection = MuckaDb.Open(DbPath);
        Run(connection, $"INSERT INTO mucka_runs (uid, started_ms, host, ended_ms) VALUES ('{uid}', 1, 'test', {(ended ? "2" : "NULL")});");
    }

    [Fact]
    public void ABackup_IsTheOneFileBesideTheDatabase_AndHoldsWhatTheDatabaseHeld()
    {
        WriteARun("a", ended: true);

        var bytes = DatabaseBackup.Take(DbPath);

        var backup = DatabaseBackup.PathFor(DbPath);
        Assert.Equal(Path.Combine(_dir, "mucka.old.db"), backup);
        Assert.Equal(new FileInfo(backup).Length, bytes);
        Assert.Equal(1, Scalar(backup, "SELECT COUNT(*) FROM mucka_runs;"));
        Assert.False(File.Exists(backup + ".new"));
    }

    [Fact]
    public void ASecondBackup_ReplacesTheFirst_SoThereIsNeverMoreThanOneCopy()
    {
        WriteARun("a", ended: true);
        DatabaseBackup.Take(DbPath);
        WriteARun("b", ended: true);

        DatabaseBackup.Take(DbPath);

        Assert.Equal(2, Scalar(DatabaseBackup.PathFor(DbPath), "SELECT COUNT(*) FROM mucka_runs;"));
        var files = Directory.GetFiles(_dir)
            .Select(f => Path.GetFileName(f))
            .Where(n => !n.EndsWith("-wal", StringComparison.Ordinal) && !n.EndsWith("-shm", StringComparison.Ordinal))
            .Where(n => !n.EndsWith(DatabaseLock.Suffix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "mucka.db", "mucka.old.db" }, files);
    }

    [Fact]
    public void ABackupThatFails_LeavesThePreviousBackupAlone()
    {
        WriteARun("a", ended: true);
        DatabaseBackup.Take(DbPath);
        var before = File.ReadAllBytes(DatabaseBackup.PathFor(DbPath));

        // A database that is not one: the copy cannot be made.
        var broken = Path.Combine(_dir, "broken", MuckaDb.DefaultFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(broken)!);
        File.WriteAllText(broken, "not a database");
        File.WriteAllBytes(DatabaseBackup.PathFor(broken), before);

        Assert.ThrowsAny<Exception>(() => DatabaseBackup.Take(broken));

        Assert.Equal(before, File.ReadAllBytes(DatabaseBackup.PathFor(broken)));
        Assert.False(File.Exists(DatabaseBackup.PathFor(broken) + ".new"));
    }

    [Fact]
    public void ASecondBackupInTheSameSession_IsNotTaken()
    {
        WriteARun("a", ended: true);
        Assert.NotNull(DatabaseBackup.TakeOnce(DbPath));
        WriteARun("b", ended: true);

        Assert.Null(DatabaseBackup.TakeOnce(DbPath));

        Assert.Equal(1, Scalar(DatabaseBackup.PathFor(DbPath), "SELECT COUNT(*) FROM mucka_runs;"));
    }

    [Fact]
    public void ABackupThatFails_IsNotRecorded_SoTheNextCallTriesAgain()
    {
        var broken = Path.Combine(_dir, "broken", MuckaDb.DefaultFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(broken)!);
        File.WriteAllText(broken, "not a database");

        Assert.ThrowsAny<Exception>(() => DatabaseBackup.TakeOnce(broken));

        Assert.False(DatabaseBackup.HoldsBackup(broken));
    }

    [Fact]
    public void CallersThatTakeTheBackupAtOnce_TakeOneBackupBetweenThem()
    {
        WriteARun("a", ended: true);
        using var start = new ManualResetEventSlim();
        var taken = new long?[8];
        var threads = Enumerable.Range(0, taken.Length).Select(i => new Thread(() =>
        {
            start.Wait();
            taken[i] = DatabaseBackup.TakeOnce(DbPath);
        })).ToArray();
        foreach (var thread in threads) thread.Start();

        start.Set();
        foreach (var thread in threads) thread.Join();

        Assert.Equal(1, taken.Count(t => t is not null));
        Assert.True(DatabaseBackup.HoldsBackup(DbPath));
    }

    // -- A second process -------------------------------------------------------------------------------------

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
    public void AnUpgradeThatFindsTheLockHeld_WaitsForIt_AndFindsTheWorkDone_SoTakesNoBackupAndMigratesNothing()
    {
        WriteAnUnjournaledFile();
        var waiter = new MaintenanceTracker();
        var seen = new List<string>();
        waiter.Changed += () => { if (waiter.IsActive) seen.Add(waiter.Detail); };
        Exception? failure = null;

        // The holder is another thread of this process, which the lock treats as it would another process.
        var held = DatabaseLock.Acquire(DbPath, new MaintenanceTracker());
        var thread = new Thread(() =>
        {
            try { Upgrade(waiter); }
            catch (Exception ex) { failure = ex; }
        });
        thread.Start();
        Assert.True(WaitFor(() => waiter.Detail == DatabaseLock.WaitingDetail));
        Assert.Equal(0, Scalar(DbPath, JournalTables));

        // What the holder does while the other waits: the upgrade, with no backup of its own.
        using (MuckaDb.Open(DbPath, backUpBeforeUpgrade: false)) { }
        held.Dispose();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));

        Assert.Null(failure);
        Assert.Equal([DatabaseLock.WaitingDetail], seen);
        Assert.False(File.Exists(DatabaseBackup.PathFor(DbPath)));
        Assert.False(waiter.IsActive);
    }

    [Fact]
    public void AnUpgradeThatWaitsForTheLock_ThenStillHasWorkToDo_BacksUpAndMigrates()
    {
        WriteAnUnjournaledFile();
        var waiter = new MaintenanceTracker();
        Exception? failure = null;
        var held = DatabaseLock.Acquire(DbPath, new MaintenanceTracker());
        var thread = new Thread(() =>
        {
            try { Upgrade(waiter); }
            catch (Exception ex) { failure = ex; }
        });
        thread.Start();
        Assert.True(WaitFor(() => waiter.Detail == DatabaseLock.WaitingDetail));

        held.Dispose();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));

        Assert.Null(failure);
        Assert.Equal(1, Scalar(DbPath, JournalTables));
        Assert.Equal(0, Scalar(DatabaseBackup.PathFor(DbPath), JournalTables));
    }

    [Fact]
    public void ALaunchStartedWhileAnotherProcessHoldsTheLock_WaitsBeforeItBacksUpOrHeals()
    {
        WriteARun("ended", ended: true);
        var tracker = new MaintenanceTracker();
        var seen = new List<string>();
        tracker.Changed += () => { if (tracker.IsActive) seen.Add(tracker.Detail); };
        WireHeal.Result? result = null;
        Exception? failure = null;
        var other = new FileStream(DatabaseLock.PathFor(DbPath), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var thread = new Thread(() =>
        {
            try { result = WireHeal.RunWithBackup(DbPath, tracker); }
            catch (Exception ex) { failure = ex; }
        });
        thread.Start();
        Assert.True(WaitFor(() => tracker.Detail == DatabaseLock.WaitingDetail));

        Assert.Equal(1, WireHeal.PendingRuns(DbPath));
        Assert.False(File.Exists(DatabaseBackup.PathFor(DbPath)));

        other.Dispose();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));

        Assert.Null(failure);
        Assert.Equal(1, result?.RunsHealed);
        Assert.Equal([DatabaseLock.WaitingDetail, MuckaDb.BackupDetail, "Joining split lines in the wire log"], seen);
    }

    // -- What a heal has to do --------------------------------------------------------------------------------

    [Fact]
    public void ALaunchWithNoFile_HealsNothing_AndCreatesNothing()
    {
        Assert.Null(WireHeal.RunWithBackup(DbPath, new MaintenanceTracker()));

        Assert.False(File.Exists(DbPath));
    }

    [Fact]
    public void ALaunchWithNothingPending_TakesNoBackup_AndOpensNoJob()
    {
        WriteARun("open", ended: false);
        var tracker = new MaintenanceTracker();
        var changes = 0;
        tracker.Changed += () => changes++;

        Assert.Null(WireHeal.RunWithBackup(DbPath, tracker));

        Assert.False(File.Exists(DatabaseBackup.PathFor(DbPath)));
        Assert.Equal(0, changes);
    }

    [Fact]
    public void ALaunchWithAnEndedRun_BacksUpFirst_ThenHeals_UnderAJob()
    {
        WriteARun("ended", ended: true);
        var tracker = new MaintenanceTracker();
        var seen = new List<string>();
        tracker.Changed += () => { if (tracker.IsActive) seen.Add(tracker.Detail); };

        var result = WireHeal.RunWithBackup(DbPath, tracker);

        Assert.Equal(1, result?.RunsHealed);
        Assert.Equal(0, WireHeal.PendingRuns(DbPath));
        // The backup holds the file as it was: the run, not yet healed.
        Assert.Equal(1, Scalar(DatabaseBackup.PathFor(DbPath), "SELECT COUNT(*) FROM mucka_runs WHERE wire_healed_ms IS NULL;"));
        Assert.Equal([MuckaDb.BackupDetail, "Joining split lines in the wire log"], seen);
        Assert.False(tracker.IsActive);
    }

    [Fact]
    public void ALaunchThatAlreadyHoldsABackup_HealsWithoutTakingAnother()
    {
        // What an import that kept the previous database leaves behind: its copy, and the record of it.
        WriteARun("ended", ended: true);
        File.WriteAllText(DatabaseBackup.PathFor(DbPath), "the import's copy");
        DatabaseBackup.NoteKept(DbPath);
        var tracker = new MaintenanceTracker();
        var seen = new List<string>();
        tracker.Changed += () => { if (tracker.IsActive) seen.Add(tracker.Detail); };

        var result = WireHeal.RunWithBackup(DbPath, tracker);

        Assert.Equal(1, result?.RunsHealed);
        Assert.Equal("the import's copy", File.ReadAllText(DatabaseBackup.PathFor(DbPath)));
        Assert.Equal(["Joining split lines in the wire log"], seen);
    }

    [Fact]
    public void PendingRuns_CountsEndedRunsNotYetHealed()
    {
        WriteARun("ended", ended: true);
        WriteARun("open", ended: false);
        Assert.Equal(1, WireHeal.PendingRuns(DbPath));

        WireHeal.Run(DbPath);

        Assert.Equal(0, WireHeal.PendingRuns(DbPath));
    }
}
