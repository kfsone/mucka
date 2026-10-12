using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Mucka.Store;

/// <summary>What <see cref="DatabaseImport.Run"/> did, in a form the caller can show as it stands.</summary>
public sealed record DatabaseImportOutcome(bool Ok, string Message);

/// <summary>
/// <c>mucka -import &lt;file&gt;</c>: merges an export file (see <see cref="RunTransfer.Export"/>) into
/// the client's database by building a NEW database file and swapping it in. Runs at start-up, before
/// anything has opened the database, so there is no live writer to lock out and nothing to roll back
/// but a file that was never put in place.
///
/// <para>The steps, and what each is for:</para>
/// <list type="number">
/// <item>No other client may have the database open. An exclusive open of the file is the test: SQLite
/// on Windows holds it shared, so any connection from another Mucka makes this fail.</item>
/// <item>The live database is checkpointed out of WAL mode, so the file alone is the whole database
/// and no <c>-wal</c> is left beside it for the replacement to inherit - a stale one would be
/// replayed into the new file.</item>
/// <item>The new file starts as a <c>VACUUM INTO</c> copy of it, and <see cref="RunTransfer.Import"/>
/// merges the export into that copy.</item>
/// <item>The result is checked (<c>integrity_check</c>, <c>foreign_key_check</c>) before it can
/// replace anything.</item>
/// <item>The live file's size and write time are read again. If they moved, something wrote to it
/// after the copy was taken and the swap would lose that, so nothing is swapped.</item>
/// <item>Only then: the previous <c>mucka.old.db</c> is deleted, the live file becomes
/// <c>mucka.old.db</c>, and the new file becomes the live file. If the last rename fails the first is
/// undone.</item>
/// </list>
///
/// <para>Any failure leaves the live database exactly where it was and deletes the new file. The file
/// handed over is never modified. Every connection here has pooling off: a pooled handle would keep
/// the file open and refuse the rename.</para>
///
/// <para>Threading: blocks for as long as the copy and merge take; it runs before there is a window
/// to be unresponsive.</para>
/// </summary>
public static class DatabaseImport
{
    /// <summary>The previous database, kept after a successful import. The one before it is deleted by
    /// the next import.</summary>
    public const string OldFileName = "mucka.old.db";

    public static DatabaseImportOutcome Run(string databasePath, string exportPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath))!;
        var oldPath = Path.Combine(directory, OldFileName);
        var newPath = databasePath + ".new";

        try
        {
            if (!File.Exists(exportPath))
                return new(false, $"No such file: {exportPath}");
            Directory.CreateDirectory(directory);
            DeleteWithSidecars(newPath);

            var liveExists = File.Exists(databasePath);
            if (liveExists)
            {
                RequireNoOtherClient(databasePath);
                Quiesce(databasePath);
                Snapshot(databasePath, newPath);
            }
            var fingerprint = liveExists ? Fingerprint(databasePath) : default;

            var result = RunTransfer.Import(newPath, exportPath);
            ReleasePool(newPath);
            Verify(newPath);

            if (liveExists && Fingerprint(databasePath) != fingerprint)
                throw new InvalidOperationException(
                    "the database changed while the import was running (another Mucka was started); "
                    + "nothing was replaced");

            Swap(databasePath, newPath, oldPath, liveExists);
            return new(true, Describe(result, liveExists));
        }
        catch (Exception ex)
        {
            ReleasePool(newPath);
            try { DeleteWithSidecars(newPath); } catch (IOException) { }
            return new(false, $"Import failed, and your database was not changed: {ex.Message}");
        }
    }

    private static string Describe(RunTransferResult r, bool hadDatabase)
    {
        var text = $"{r.RunsImported} runs imported ({r.RunsAlreadyHere} already here), "
            + $"{r.Sessions} logins, {r.FactRows:N0} rows, {r.WireRows:N0} wire records.";
        if (r.RunsProbablyHere > 0)
            text += $"\n{r.RunsProbablyHere} run(s) were left out because the same start, host and "
                + "record count are already here under another identity.";
        if (r.RunsSkippedUnclosed.Count > 0)
            text += $"\n{r.RunsSkippedUnclosed.Count} run(s) with no recorded end were left out "
                + "(the run that was open when the file was exported is one).";
        if (r.UnattributedRowsLeftBehind > 0)
            text += $"\n{r.UnattributedRowsLeftBehind:N0} rows that belong to no login were left behind.";
        if (hadDatabase)
            text += $"\nYour previous database is kept as {OldFileName}.";
        return text;
    }

    private static void RequireNoOtherClient(string databasePath)
    {
        try
        {
            using var exclusive = new FileStream(databasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw new IOException("another Mucka has the database open; close every Mucka and try again");
        }
    }

    private static SqliteConnection OpenPlain(string path)
        => OpenPlain(path, SqliteOpenMode.ReadWrite);

    private static SqliteConnection OpenPlain(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = mode, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    /// <summary>Folds the WAL into the file and leaves the database in rollback-journal mode, so that
    /// afterwards the one file is all of it. <c>MuckaDb.Open</c> puts it back into WAL on the next
    /// launch.</summary>
    private static void Quiesce(string databasePath)
    {
        using var connection = OpenPlain(databasePath);
        Execute(connection, "PRAGMA busy_timeout=5000;");
        Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        if (!string.Equals(Scalar(connection, "PRAGMA journal_mode=DELETE;"), "delete", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("could not take the database out of WAL mode");
    }

    private static void Snapshot(string databasePath, string newPath)
    {
        using var connection = OpenPlain(databasePath, SqliteOpenMode.ReadOnly);
        using var command = connection.CreateCommand();
        command.CommandText = "VACUUM INTO $target;";
        command.Parameters.AddWithValue("$target", newPath);
        command.ExecuteNonQuery();
    }

    private static (long Length, DateTime WrittenUtc) Fingerprint(string path)
    {
        var info = new FileInfo(path);
        return (info.Length, info.LastWriteTimeUtc);
    }

    private static void ReleasePool(string path)
    {
        using var pooled = new SqliteConnection(MuckaDb.ConnectionString(path));
        SqliteConnection.ClearPool(pooled);
    }

    /// <summary>The merged file must be sound before anything is replaced by it. Leaves it out of WAL
    /// mode, so there are no sidecar files to move with it.</summary>
    private static void Verify(string newPath)
    {
        using var connection = OpenPlain(newPath);
        var integrity = Scalar(connection, "PRAGMA integrity_check;");
        if (integrity != "ok")
            throw new InvalidOperationException($"the merged database failed its integrity check ({integrity})");
        if (Scalar(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check;") != "0")
            throw new InvalidOperationException("the merged database has rows that refer to rows that are not there");
        if (!string.Equals(Scalar(connection, "PRAGMA journal_mode=DELETE;"), "delete", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("could not finish the merged database");
    }

    private static void Swap(string databasePath, string newPath, string oldPath, bool liveExists)
    {
        if (!liveExists)
        {
            File.Move(newPath, databasePath);
            return;
        }

        if (File.Exists(databasePath + "-wal") || File.Exists(databasePath + "-shm"))
            throw new IOException("the database has files beside it that a replacement would inherit");

        // One call: the live name is never absent, and a failure leaves the previous backup in place.
        File.Replace(newPath, databasePath, oldPath);
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            if (File.Exists(oldPath + suffix))
                File.Delete(oldPath + suffix);
    }

    private static void DeleteWithSidecars(string path)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            if (File.Exists(path + suffix))
                File.Delete(path + suffix);
    }
}
