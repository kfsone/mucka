namespace Mucka.Commands;

/// <summary>What <c>$CHATTEST [slow|stop]</c> asks for.</summary>
public enum ChatTestMode
{
    /// <summary>No argument: the whole script at once.</summary>
    All,
    /// <summary>One scripted line at a time, a random pause before each.</summary>
    Slow,
    /// <summary>Stop a slow run.</summary>
    Stop,
    /// <summary>Anything else; the caller prints the usage.</summary>
    Unknown,
}

public static class ChatTestCommand
{
    public const string Usage = "$CHATTEST [slow|stop]";

    /// <summary>The argument after <c>$CHATTEST</c>, in any case, surrounding blanks ignored.</summary>
    public static ChatTestMode Parse(string argument)
    {
        var word = (argument ?? string.Empty).Trim();
        if (word.Length == 0) return ChatTestMode.All;
        if (word.Equals("slow", StringComparison.OrdinalIgnoreCase)) return ChatTestMode.Slow;
        if (word.Equals("stop", StringComparison.OrdinalIgnoreCase)) return ChatTestMode.Stop;
        return ChatTestMode.Unknown;
    }
}

/// <summary>
/// The pacing of <c>$CHATTEST slow</c>: a pause drawn uniformly from
/// [<see cref="MinDelay"/>, <see cref="MaxDelay"/>] before every unit, the first included. At most
/// one run at a time. Delivery happens where the run was started resumes - the UI thread for the
/// view model, whose awaits keep its context. Nothing is delivered once <see cref="Stop"/> has
/// returned, even when the pause had already elapsed and its continuation was still queued.
/// </summary>
public sealed class ChatTestSlowRun
{
    public static readonly TimeSpan MinDelay = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(9);

    private readonly Func<double> _random;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private CancellationTokenSource? _current;

    /// <param name="random">Uniform in [0, 1); <see cref="Random.Shared"/> when null.</param>
    /// <param name="delay">The pause; <see cref="Task.Delay(TimeSpan, CancellationToken)"/> when null.</param>
    public ChatTestSlowRun(Func<double>? random = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _random = random ?? Random.Shared.NextDouble;
        _delay = delay ?? Task.Delay;
    }

    public bool IsRunning => _current is not null;

    /// <summary>The pause for a draw <paramref name="unit"/> in [0, 1], clamped to it.</summary>
    public static TimeSpan DelayFor(double unit)
        => MinDelay + (MaxDelay - MinDelay) * Math.Clamp(unit, 0.0, 1.0);

    /// <summary>
    /// Starts delivering <paramref name="units"/>, stopping any run already going first.
    /// <paramref name="finished"/> is called only when every unit has been delivered.
    /// </summary>
    /// <param name="stoppedPrevious">True when a run was going and has been stopped.</param>
    /// <returns>The run; it completes on finishing or on being stopped, and never faults on a stop.</returns>
    public Task Start<T>(IReadOnlyList<T> units, Action<T> deliver, Action finished, out bool stoppedPrevious)
    {
        ArgumentNullException.ThrowIfNull(units);
        ArgumentNullException.ThrowIfNull(deliver);
        ArgumentNullException.ThrowIfNull(finished);
        stoppedPrevious = Stop();
        var run = new CancellationTokenSource();
        _current = run;
        return RunAsync(run, units, deliver, finished);
    }

    /// <summary>Stops the run, if one is going. True when one was.</summary>
    public bool Stop()
    {
        var run = _current;
        if (run is null) return false;
        _current = null;
        run.Cancel();
        return true;
    }

    private async Task RunAsync<T>(CancellationTokenSource run, IReadOnlyList<T> units, Action<T> deliver, Action finished)
    {
        var token = run.Token;
        try
        {
            foreach (var unit in units)
            {
                try
                {
                    await _delay(DelayFor(_random()), token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                // A pause that elapsed just before Stop still resumes here afterwards.
                if (token.IsCancellationRequested) return;
                deliver(unit);
            }
        }
        finally
        {
            if (ReferenceEquals(_current, run)) _current = null;
            run.Dispose();
        }
        finished();
    }
}
