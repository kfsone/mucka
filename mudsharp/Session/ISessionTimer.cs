namespace MudSharp.Session;

/// <summary>
/// Exactly the slice of <see cref="System.Threading.Timer"/> the session's probe timers use:
/// <see cref="Change"/> replaces any deadline already pending, <see cref="Stop"/> drops it, and a
/// period of <see cref="Timeout.InfiniteTimeSpan"/> makes the deadline one-shot. Expressed as an
/// interface so a test can substitute a virtual clock it steps by hand - a wall-clock timer can only
/// be asserted against with sleeps, and a sleep tuned to a margin fails on a loaded machine while the
/// code is correct.
/// </summary>
internal interface ISessionTimer : IDisposable
{
    /// <summary>
    /// Fire the callback once <paramref name="due"/> from now and then every <paramref name="period"/>,
    /// replacing any pending deadline. An infinite period makes it one-shot.
    /// </summary>
    void Change(TimeSpan due, TimeSpan period);

    /// <summary>Drop the pending deadline and any period. The timer stays usable.</summary>
    void Stop();
}

/// <summary>The production <see cref="ISessionTimer"/>: a <see cref="System.Threading.Timer"/>.</summary>
internal sealed class ThreadingSessionTimer : ISessionTimer
{
    private readonly System.Threading.Timer _timer;

    public ThreadingSessionTimer(Action callback)
        => _timer = new System.Threading.Timer(_ => callback(), null, Timeout.Infinite, Timeout.Infinite);

    public void Change(TimeSpan due, TimeSpan period) => _timer.Change(due, period);

    public void Stop() => _timer.Change(Timeout.Infinite, Timeout.Infinite);

    public void Dispose() => _timer.Dispose();
}
