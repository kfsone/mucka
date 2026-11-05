using Microsoft.Data.Sqlite;

namespace Mucka.Store;

/// <summary>
/// The one backup of <c>mucka.db</c> the client keeps: <see cref="FileName"/>, beside the database.
/// A schema upgrade of an existing file (<see cref="MuckaDb.ApplySchema(SqliteConnection, MaintenanceTracker, bool, string)"/>),
/// a heal of the wire log, and a <c>$trimdb</c> trim, unwrap or expire, take it first, an import
/// (<see cref="DatabaseImport"/>) leaves the
/// previous database under the same name, and each replaces whatever was there, so there is never
/// more than one extra copy of the file.
///
/// <para><b>One backup per launch.</b> The file is the state before the first thing this launch did to
/// it, and a second copy taken after that change would replace exactly that. <see cref="TakeOnce"/> takes
/// none when this process already holds a backup of the file, and an import that keeps its previous
/// database (<see cref="NoteKept"/>) counts as one. The record is per database path and lives as long
/// as the process.</para>
///
/// <para><b>How eager.</b> The player's <see cref="BackupPolicy"/> (<see cref="Policy"/>) says how old
/// the existing backup must be before another is taken, or that none is: <see cref="IsDue"/>. Its age is
/// the backup file's last-write time.</para>
///
/// <para><b>Under <see cref="DatabaseLock"/>.</b> <see cref="Take"/> and <see cref="TakeOnce"/> take it
/// themselves, so no caller can back up without it; it is re-entrant, so a caller that already holds it
/// for the work that follows the backup is not held up.</para>
///
/// <para>The copy is <c>VACUUM INTO</c> from a read-only connection: a consistent snapshot taken in a
/// read transaction, so the client's writer is not stopped for it, and one file with no WAL beside
/// it. It is written under a staging name and put in place whole, so a failure leaves the previous
/// backup where it was.</para>
/// </summary>
public static class DatabaseBackup
{
    /// <summary>The backup's file name.</summary>
    public const string FileName = "mucka.old.db";

    private static readonly HashSet<string> KeptThisSession = new(StringComparer.OrdinalIgnoreCase);

    private static string Key(string databasePath) => Path.GetFullPath(databasePath);

    private static volatile BackupPolicy s_policy = BackupPolicies.Default;

    /// <summary>The player's choice of how eager to be (<see cref="BackupPolicy"/>). Set when the
    /// client starts, before anything opens the database, and again when the player changes it.</summary>
    public static BackupPolicy Policy
    {
        get => s_policy;
        set => s_policy = value;
    }

    /// <summary>True when <see cref="TakeOnce"/> would take a backup of <paramref name="databasePath"/>
    /// now: this process holds none yet, the policy is not <see cref="BackupPolicy.Never"/>, and the
    /// existing backup, if there is one, is at least the policy's age (<see cref="BackupPolicies.MinimumAge"/>)
    /// old.</summary>
    public static bool IsDue(string databasePath)
    {
        if (HoldsBackup(databasePath))
            return false;
        if (BackupPolicies.MinimumAge(Policy) is not { } age)
            return false;
        var existing = PathFor(databasePath);
        return age == TimeSpan.Zero || !File.Exists(existing)
            || DateTime.UtcNow - File.GetLastWriteTimeUtc(existing) >= age;
    }

    /// <summary>True when this process has already taken, or been left, a backup of
    /// <paramref name="databasePath"/>.</summary>
    public static bool HoldsBackup(string databasePath)
    {
        lock (KeptThisSession)
            return KeptThisSession.Contains(Key(databasePath));
    }

    /// <summary>Records that this process holds a backup of <paramref name="databasePath"/>, so
    /// <see cref="TakeOnce"/> takes no other.</summary>
    internal static void NoteKept(string databasePath)
    {
        lock (KeptThisSession)
            KeptThisSession.Add(Key(databasePath));
    }

    /// <summary>Takes the backup when it is <see cref="IsDue"/>: the size of the backup taken, or null
    /// when nothing was done. A failure throws and records nothing, so the next call tries again.</summary>
    public static long? TakeOnce(string databasePath)
    {
        if (!IsDue(databasePath))
            return null;
        // One caller at a time, through the lock: the check and the copy are one step, or two callers
        // that both found nothing held would each copy the file over the other's.
        using var held = DatabaseLock.Acquire(databasePath, Maintenance.Current);
        if (!IsDue(databasePath))
            return null;
        var bytes = Take(databasePath);
        NoteKept(databasePath);
        return bytes;
    }

    /// <summary>Where the backup of <paramref name="databasePath"/> is kept.</summary>
    public static string PathFor(string databasePath)
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath))!, FileName);

    /// <summary>Copies the database to <see cref="PathFor"/>, replacing the previous backup, and returns
    /// the backup's size in bytes. Throws, and leaves the previous backup alone, when there is not room
    /// for a second copy or the copy fails. Off the UI thread: it reads the whole file.</summary>
    public static long Take(string databasePath)
    {
        // A backup is never taken without the lock: it replaces the previous one, which another Mucka
        // may be reading or replacing too.
        using var held = DatabaseLock.Acquire(databasePath, Maintenance.Current);
        var target = PathFor(databasePath);
        var staging = target + ".new";
        DeleteWithSidecars(staging);

        var needed = FileLength(databasePath) + FileLength(databasePath + "-wal");
        RequireFreeSpace(Path.GetFullPath(staging), needed, "backing up");

        try
        {
            using var source = MuckaDb.OpenRead(databasePath);
            using var command = source.CreateCommand();
            command.CommandText = "VACUUM INTO $target;";
            command.Parameters.AddWithValue("$target", staging);
            command.ExecuteNonQuery();
        }
        catch
        {
            DeleteWithSidecars(staging);
            throw;
        }

        // One call: the backup's name is never absent, and a failure leaves the previous one in place.
        if (File.Exists(target))
            File.Replace(staging, target, null);
        else
            File.Move(staging, target);
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            if (File.Exists(target + suffix))
                File.Delete(target + suffix);
        return new FileInfo(target).Length;
    }

    // Throws when the drive holding `path` has less than `needed` free. A path whose drive cannot be
    // read (a UNC path, a platform with no drive for it) is not checked: refusing on a guess would
    // keep the work from ever running there.
    internal static void RequireFreeSpace(string path, long needed, string what)
    {
        long free;
        string root;
        try
        {
            root = Path.GetPathRoot(path) ?? string.Empty;
            if (root.Length == 0) return;
            // Off Windows the root is "/", which is not the partition the data is on: ask about the
            // directory the file goes in.
            var measured = OperatingSystem.IsWindows() ? root : Path.GetDirectoryName(path) ?? root;
            free = new DriveInfo(measured).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return;
        }
        if (free < needed)
            throw new IOException(
                $"{what} needs about {needed / (1024 * 1024):N0} MB free on {root} and there is {free / (1024 * 1024):N0} MB");
    }

    private static long FileLength(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    private static void DeleteWithSidecars(string path)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            if (File.Exists(path + suffix))
                File.Delete(path + suffix);
    }
}
