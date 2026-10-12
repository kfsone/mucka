using System.Globalization;
using Microsoft.Data.Sqlite;
using Mucka.Store;

namespace Mucka.Util.Tests;

/// <summary>
/// <c>mucka -import</c>: the merged file is built beside the live one and swapped in only when every
/// check has passed. Each test names a way the swap could cost the operator their database.
/// </summary>
public sealed class DatabaseImportTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mucka-db-import-tests", Guid.NewGuid().ToString("N"));

    public DatabaseImportTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        TestStore.ReleasePools(_directory);
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string Live => Path.Combine(_directory, MuckaDb.DefaultFileName);
    private string Old => Path.Combine(_directory, DatabaseImport.OldFileName);
    private string PathOf(string name) => Path.Combine(_directory, name);

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>A finished run with one wire record, in the database at <paramref name="path"/>.</summary>
    private void AddRun(string path, string uid, long startedMs)
    {
        InsertRun(path, uid, startedMs);
        // A pooled connection keeps the file open, which is what the import refuses to start under.
        TestStore.ReleasePools(_directory);
    }

    private static void InsertRun(string path, string uid, long startedMs)
    {
        using var db = MuckaDb.Open(path);
        Exec(db, $"INSERT INTO mucka_runs (uid, started_ms, ended_ms, host) VALUES ('{uid}', {startedMs}, {startedMs + 10}, 'h');");
        Exec(db, $"INSERT INTO wire (mucka_run_id, seq, ts_ms, direction, data) VALUES (last_insert_rowid(), 0, {startedMs}, 0, x'41');");
    }

    private static long Count(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private void MakeExport(string uid, long startedMs)
    {
        AddRun(PathOf("other.db"), uid, startedMs);
        RunTransfer.Export(PathOf("other.db"), PathOf("export.db"));
    }

    private string[] Leftovers() => Directory.GetFiles(_directory)
        .Select(Path.GetFileName).Cast<string>()
        .Where(n => n.StartsWith("mucka.db", StringComparison.Ordinal) && n != MuckaDb.DefaultFileName)
        .ToArray();

    [Fact]
    public void TheMergedFileBecomesTheDatabase_AndThePreviousOneIsKeptAsOld()
    {
        AddRun(Live, "mine", 1_000);
        MakeExport("theirs", 2_000);

        var outcome = DatabaseImport.Run(Live, PathOf("export.db"));

        Assert.True(outcome.Ok, outcome.Message);
        Assert.Equal(2, Count(Live, "SELECT COUNT(*) FROM mucka_runs"));
        Assert.Equal(1, Count(Old, "SELECT COUNT(*) FROM mucka_runs"));
        Assert.Contains("mucka.old.db", outcome.Message);
        Assert.Empty(Leftovers());
    }

    [Fact]
    public void ThePreviousOldFileIsReplaced_NotKeptAlongsideTheNewOne()
    {
        AddRun(Live, "mine", 1_000);
        File.WriteAllText(Old, "an older backup");
        MakeExport("theirs", 2_000);

        Assert.True(DatabaseImport.Run(Live, PathOf("export.db")).Ok);

        Assert.Equal(1, Count(Old, "SELECT COUNT(*) FROM mucka_runs"));
    }

    [Fact]
    public void NoDatabaseYet_IsCreatedFromTheExport()
    {
        MakeExport("theirs", 2_000);

        var outcome = DatabaseImport.Run(Live, PathOf("export.db"));

        Assert.True(outcome.Ok, outcome.Message);
        Assert.Equal(1, Count(Live, "SELECT COUNT(*) FROM mucka_runs"));
        Assert.False(File.Exists(Old));
    }

    [Fact]
    public void AnImportThatFails_LeavesTheLiveDatabaseAndItsBackupUntouched()
    {
        AddRun(Live, "mine", 1_000);
        File.WriteAllText(Old, "the backup");
        // Newer than this build knows: refused during the merge, after the copy of the live file.
        MakeExport("theirs", 2_000);
        using (var export = new SqliteConnection(new SqliteConnectionStringBuilder
                   { DataSource = PathOf("export.db"), Pooling = false }.ToString()))
        {
            export.Open();
            Exec(export, "INSERT INTO SchemaVersions (ScriptName, Applied) VALUES ('0099_future.sql', 'now');");
        }

        var outcome = DatabaseImport.Run(Live, PathOf("export.db"));

        Assert.False(outcome.Ok);
        Assert.Contains("0099_future.sql", outcome.Message);
        Assert.Contains("was not changed", outcome.Message);
        Assert.Equal(1, Count(Live, "SELECT COUNT(*) FROM mucka_runs"));
        Assert.Equal("the backup", File.ReadAllText(Old));
        Assert.Empty(Leftovers());
    }

    [Fact]
    public void AnotherClientHoldingTheDatabase_StopsTheImportBeforeAnythingIsCopied()
    {
        AddRun(Live, "mine", 1_000);
        MakeExport("theirs", 2_000);
        TestStore.ReleasePools(_directory);
        using var otherClient = new FileStream(Live, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var outcome = DatabaseImport.Run(Live, PathOf("export.db"));

        Assert.False(outcome.Ok);
        Assert.Contains("another Mucka", outcome.Message);
        Assert.False(File.Exists(Old));
        Assert.Empty(Leftovers());
    }

    [Fact]
    public void AMissingExportFile_IsReportedAndNothingIsTouched()
    {
        AddRun(Live, "mine", 1_000);

        var outcome = DatabaseImport.Run(Live, PathOf("nothing-here.db"));

        Assert.False(outcome.Ok);
        Assert.Contains("No such file", outcome.Message);
        Assert.False(File.Exists(Old));
        Assert.Equal(1, Count(Live, "SELECT COUNT(*) FROM mucka_runs"));
    }

    [Fact]
    public void ARunningWalFileIsNotLeftBesideTheReplacement()
    {
        // A database left as a crashed client leaves it: its newest rows only in the -wal beside it.
        // The files are copied while the connection is still open, because closing the last
        // connection would fold the WAL in and leave nothing to test.
        using (var db = MuckaDb.Open(PathOf("crashing.db")))
        {
            Exec(db, "PRAGMA wal_autocheckpoint=0;");
            Exec(db, "INSERT INTO mucka_runs (uid, started_ms, ended_ms, host) VALUES ('mine', 1, 2, 'h');");
            File.Copy(PathOf("crashing.db"), Live);
            File.Copy(PathOf("crashing.db-wal"), Live + "-wal");
        }
        TestStore.ReleasePools(_directory);
        Assert.True(File.Exists(Live + "-wal"));
        MakeExport("theirs", 2_000);

        var outcome = DatabaseImport.Run(Live, PathOf("export.db"));

        Assert.True(outcome.Ok, outcome.Message);
        Assert.False(File.Exists(Live + "-wal"));
        Assert.False(File.Exists(Live + "-shm"));
        // The row that lived only in the WAL is in the merged file.
        Assert.Equal(1, Count(Live, "SELECT COUNT(*) FROM mucka_runs WHERE uid = 'mine'"));
        Assert.Equal(2, Count(Live, "SELECT COUNT(*) FROM mucka_runs"));
    }
}
