namespace MudSharp.Session;

/// <summary>
/// One-shot deadline: <see cref="Change"/> replaces any deadline already pending, <see cref="Stop"/>
/// drops it, and the callback runs at most once per <see cref="Change"/>. Exactly the slice of
/// <see cref="System.Threading.Timer"/> the recovery probes use, expressed as an interface so a test
/// can substitute a virtual clock it steps by hand - a wall-clock timer can only be asserted against
/// with sleeps, and a sleep tuned to a margin fails on a loaded machine while the code is correct.
/// </summary>
internal interface IOneShotTimer : IDisposable
{
    /// <summary>Fire the callback once <paramref name="due"/> from now, replacing any pending deadline.</summary>
    void Change(TimeSpan due);

    /// <summary>Drop the pending deadline, if any. The timer stays usable.</summary>
    void Stop();
}

/// <summary>The production <see cref="IOneShotTimer"/>: a <see cref="System.Threading.Timer"/> that never repeats.</summary>
internal sealed class ThreadingOneShotTimer : IOneShotTimer
{
    private readonly System.Threading.Timer _timer;

    public ThreadingOneShotTimer(Action callback)
        => _timer = new System.Threading.Timer(_ => callback(), null, Timeout.Infinite, Timeout.Infinite);

    public void Change(TimeSpan due) => _timer.Change(due, Timeout.InfiniteTimeSpan);

    public void Stop() => _timer.Change(Timeout.Infinite, Timeout.Infinite);

    public void Dispose() => _timer.Dispose();
}
