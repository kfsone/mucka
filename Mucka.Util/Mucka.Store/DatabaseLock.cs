namespace Mucka.Store;

/// <summary>
/// One holder at a time of the work that upgrades, backs up and heals <c>mucka.db</c>, across
/// processes: the database's path plus <see cref="Suffix"/>, opened with no sharing for as long as the
/// work runs. The operating system owns it. A holder that is killed or crashes has its handle closed
/// by the system, so there is nothing to find stale and nothing to clean up; the file's existence means
/// nothing, only an open handle on it does.
///
/// <para>A second Mucka that wants the lock polls for it under a job on the caller's
/// <see cref="MaintenanceTracker"/> reading <see cref="WaitingDetail"/>, so the player sees why the
/// window is not ready, and the job can be cancelled (<see cref="MaintenanceTracker.Cancel"/>), which
/// ends the wait with <see cref="OperationCanceledException"/>. A caller that gets it at once opens
/// no job.</para>
///
/// <para>Held per path by one thread at a time and re-entrant on that thread, so a launch can take it
/// once around the upgrade and the heal and let the calls inside it nest. The handle is opened by the
/// outermost holder and closed when the last nested one is disposed.</para>
///
/// <para>SQLite cannot hold this: the backup, the upgrade and the heal are separate transactions on
/// separate connections, and a write lock held across them would block the upgrade's own.</para>
/// </summary>
public static class DatabaseLock
{
    /// <summary>What is added to the database's own path to name its lock file, so each database file
    /// has a lock of its own.</summary>
    public const string Suffix = ".lock";

    /// <summary>What the overlay says while another Mucka holds the lock.</summary>
    public const string WaitingDetail = "Waiting for another Mucka to finish with your game data";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    private static readonly Dictionary<string, State> States = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where the lock file for <paramref name="databasePath"/> is.</summary>
    public static string PathFor(string databasePath) => Path.GetFullPath(databasePath) + Suffix;

    /// <summary>Takes the lock for <paramref name="databasePath"/>, waiting for a holder in this process
    /// or another. Dispose the result on the thread that took it. Throws
    /// <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled or
    /// the player cancels the waiting job; there is no other limit, because the holder may be a heal of
    /// a large file. <paramref name="canGiveUp"/> is false for work a player starts inside a running
    /// game, where the overlay's exit would end that game: the wait then offers no way out.</summary>
    public static IDisposable Acquire(string databasePath, MaintenanceTracker maintenance,
        bool canGiveUp = true, CancellationToken cancellationToken = default)
    {
        var state = StateFor(PathFor(databasePath));
        MaintenanceTracker.Job? waiting = null;
        CancellationTokenSource? given = null;
        try
        {
            while (true)
            {
                var token = given?.Token ?? cancellationToken;
                token.ThrowIfCancellationRequested();
                if (TryEnter(state))
                    return new Hold(state);
                if (waiting is null)
                {
                    // The job offers the player a way out of the wait, which is the same exception as
                    // the caller's own token.
                    var own = given = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    waiting = maintenance.Begin(WaitingDetail, !canGiveUp ? null : () =>
                    {
                        try { own.Cancel(); }
                        catch (ObjectDisposedException) { }
                    });
                }
                given!.Token.WaitHandle.WaitOne(PollInterval);
            }
        }
        finally
        {
            waiting?.Dispose();
            given?.Dispose();
        }
    }

    private static State StateFor(string lockPath)
    {
        lock (States)
        {
            if (!States.TryGetValue(lockPath, out var state))
                States[lockPath] = state = new State(lockPath);
            return state;
        }
    }

    private static bool TryEnter(State state)
    {
        if (!Monitor.TryEnter(state.Sync))
            return false;
        if (state.Depth == 0)
        {
            FileStream? file;
            try
            {
                file = TryOpen(state.LockPath);
            }
            catch
            {
                Monitor.Exit(state.Sync);
                throw;
            }
            if (file is null)
            {
                Monitor.Exit(state.Sync);
                return false;
            }
            state.File = file;
        }
        state.Depth++;
        return true;
    }

    private const int ErrorSharingViolation = 32, ErrorLockViolation = 33;

    // Null when another process holds the file open. A missing directory, a full disk or an access
    // refusal is not that, and throws. Windows says which it is; on the other platforms every
    // IOException is taken to be a holder, because the code a refused flock carries is not recorded here.
    private static FileStream? TryOpen(string lockPath)
    {
        try
        {
            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when (ex is not (DirectoryNotFoundException or FileNotFoundException
                                                 or PathTooLongException or DriveNotFoundException)
                                     && (!OperatingSystem.IsWindows()
                                         || (ex.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation))
        {
            return null;
        }
    }

    private sealed class State(string lockPath)
    {
        public readonly string LockPath = lockPath;
        public readonly object Sync = new();
        public FileStream? File;
        public int Depth;
    }

    private sealed class Hold(State state) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released)
                return;
            _released = true;
            if (--state.Depth == 0)
            {
                state.File?.Dispose();
                state.File = null;
            }
            Monitor.Exit(state.Sync);
        }
    }
}
