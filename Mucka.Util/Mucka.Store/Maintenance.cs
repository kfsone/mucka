namespace Mucka.Store;

/// <summary>
/// What the client shows over its window while it works on the database and the player cannot be
/// served by it: a schema upgrade, a heal of the wire log, a <c>$trimdb</c> pass. The work calls
/// <see cref="MaintenanceTracker.Begin"/> and disposes the job; the pages show their overlay for as
/// long as any job is open. No MAUI here, so the rule is testable on its own.
/// </summary>
public static class Maintenance
{
    /// <summary>The tracker the pages watch.</summary>
    public static readonly MaintenanceTracker Current = new();

    /// <summary>Opens a job on <see cref="Current"/>.</summary>
    public static MaintenanceTracker.Job Begin(string detail) => Current.Begin(detail);
}

/// <summary>The open maintenance jobs, and a change event for whoever shows them. The newest job's
/// detail is the one shown.</summary>
public sealed class MaintenanceTracker
{
    private readonly object _sync = new();
    private readonly List<Job> _open = [];

    /// <summary>Raised, on the thread that changed it, when a job opens or closes or its detail
    /// changes. A handler that touches a view marshals to the UI thread itself.</summary>
    public event Action? Changed;

    /// <summary>True while any job is open.</summary>
    public bool IsActive
    {
        get { lock (_sync) return _open.Count > 0; }
    }

    /// <summary>The newest open job's detail, or empty.</summary>
    public string Detail
    {
        get { lock (_sync) return _open.Count > 0 ? _open[^1].Detail : string.Empty; }
    }

    /// <summary>True when the newest open job can be given up on - it was opened with a
    /// <c>cancel</c> action - so the overlay offers the player a way out.</summary>
    public bool CanCancel
    {
        get { lock (_sync) return _open.Count > 0 && _open[^1].HasCancel; }
    }

    /// <summary>Runs the newest open job's cancel action, if it has one.</summary>
    public void Cancel()
    {
        Action? cancel;
        lock (_sync)
            cancel = _open.Count > 0 ? _open[^1].CancelAction : null;
        cancel?.Invoke();
    }

    /// <summary>Opens a job; dispose it to close it. <paramref name="cancel"/> is what the player's
    /// "give up" does to the work, for a job that is only waiting: the work stops, and the owner of it
    /// decides what the client does next.</summary>
    public Job Begin(string detail, Action? cancel = null)
    {
        var job = new Job(this, detail, cancel);
        lock (_sync)
            _open.Add(job);
        Changed?.Invoke();
        return job;
    }

    /// <summary>One piece of maintenance work.</summary>
    public sealed class Job : IDisposable
    {
        private readonly MaintenanceTracker _owner;
        private string _detail;
        private bool _closed;

        internal Job(MaintenanceTracker owner, string detail, Action? cancel)
        {
            _owner = owner;
            _detail = detail;
            CancelAction = cancel;
        }

        internal Action? CancelAction { get; }

        internal bool HasCancel => CancelAction is not null;

        /// <summary>What the job is doing now, as a short phrase for the overlay. Text after a first
        /// line feed is a sub-heading, which the overlay shows below it in a smaller font.</summary>
        public string Detail
        {
            get { lock (_owner._sync) return _detail; }
            set
            {
                lock (_owner._sync)
                {
                    if (_closed || _detail == value) return;
                    _detail = value;
                }
                _owner.Changed?.Invoke();
            }
        }

        public void Dispose()
        {
            lock (_owner._sync)
            {
                if (_closed) return;
                _closed = true;
                _owner._open.Remove(this);
            }
            _owner.Changed?.Invoke();
        }
    }
}
