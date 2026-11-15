using Microsoft.Data.Sqlite;

namespace Mucka.Store;

/// <summary>
/// Shrink mucka.db, on the Logs page's Maintenance tab: checkpoints the WAL, refreshes the planner's
/// statistics, rebuilds the file without its free pages, and drops the pooled connections so the next
/// one opens the new file. Blocks for as long as that takes, which grows with the file - never call it
/// on the UI thread.
///
/// <para>On a connection of its own, under <see cref="DatabaseLock"/>, which every pass over the wire log
/// also takes, so none runs beside it. The Logs page is never open while this process plays a game, so
/// no writer of this process is recording. <b>Another process is not protected:</b> a second Mucka on
/// the same file has its own writer, whose commit gives up after the busy timeout and faults its store,
/// so it stops recording for the rest of its session if this runs past that.</para>
///
/// <para>Needs free space of twice the file's size, on its drive and on the temp drive; without it
/// nothing is attempted and the exception says how much is needed. The check is skipped on a platform
/// whose drive information cannot be read for the path.</para>
/// </summary>
public static class DatabaseVacuum
{
    /// <summary>What <see cref="Shrink"/> did to the file.</summary>
    public readonly record struct Result(long BytesBefore, long BytesAfter);

    /// <summary>Shrinks <paramref name="databasePath"/>.</summary>
    public static Result Shrink(string databasePath)
    {
        using var held = DatabaseLock.Acquire(databasePath, Maintenance.Current, canGiveUp: false);
        var before = FileLength(databasePath);
        var needed = before * 2;
        DatabaseBackup.RequireFreeSpace(Path.GetFullPath(databasePath), needed, "shrinking the file");
        DatabaseBackup.RequireFreeSpace(Path.GetTempPath(), needed, "shrinking the file");

        using (var connection = new SqliteConnection(MuckaDb.ConnectionString(databasePath)))
        {
            connection.Open();
            foreach (var statement in new[]
                     {
                         "PRAGMA busy_timeout=10000;", "PRAGMA wal_checkpoint(TRUNCATE);", "ANALYZE;", "VACUUM;",
                         "PRAGMA wal_checkpoint(TRUNCATE);",
                     })
            {
                using var command = connection.CreateCommand();
                command.CommandText = statement;
                command.ExecuteNonQuery();
            }
        }

        using (var pooled = new SqliteConnection(MuckaDb.ConnectionString(databasePath)))
            SqliteConnection.ClearPool(pooled);
        using (var pooledRead = new SqliteConnection(MuckaDb.ReadConnectionString(databasePath)))
            SqliteConnection.ClearPool(pooledRead);
        return new Result(before, FileLength(databasePath));
    }

    private static long FileLength(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;
}
