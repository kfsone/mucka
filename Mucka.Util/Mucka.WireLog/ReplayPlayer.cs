namespace Mucka.WireLog;

/// <summary>
/// Plays a replay's server records out on their own schedule, at a speed that can change while it
/// plays. The speed is a <see cref="Step"/> on a fixed ladder - the replay bar's slider positions -
/// from <see cref="PauseStep"/> at the far left to <see cref="MaxStep"/> at the far right.
///
/// <para>Between two records it waits the gap the original session had, divided by the speed. At
/// <see cref="MaxStep"/> it ignores the original gaps and sends a record every
/// <see cref="MaxIntervalMs"/>. A wait is taken in slices of at most <see cref="SliceMs"/>, and the
/// speed is read again before each one, so moving the slider takes effect within a slice rather
/// than at the next record.</para>
///
/// <para>Annotation records are not played; only <see cref="WireDirection.Rx"/> reaches
/// <c>emit</c>.</para>
/// </summary>
public sealed class ReplayPlayer
{
    /// <summary>The multiplier at each step between pause and max.</summary>
    private static readonly double[] Multipliers = [0.25, 0.5, 1.0, 2.0, 4.0, 8.0, 16.0];

    /// <summary>The far left: nothing plays.</summary>
    public const int PauseStep = 0;

    /// <summary>The far right: one record every <see cref="MaxIntervalMs"/>.</summary>
    public static readonly int MaxStep = Multipliers.Length + 1;

    /// <summary>1.0x - the original session's own timing. Where the slider starts.</summary>
    public static readonly int OriginalStep = Array.IndexOf(Multipliers, 1.0) + 1;

    /// <summary>The send interval at <see cref="MaxStep"/>.</summary>
    public const int MaxIntervalMs = 20;

    /// <summary>The longest single wait before the speed is read again.</summary>
    public const int SliceMs = 50;

    private readonly IReadOnlyList<WireRecord> _records;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private volatile int _step;

    /// <param name="delay">The clock: <see cref="Task.Delay(TimeSpan, CancellationToken)"/> in the app,
    /// a recording fake in a test.</param>
    public ReplayPlayer(IReadOnlyList<WireRecord> records, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _records = records;
        _delay = delay ?? Task.Delay;
        _step = OriginalStep;
    }

    /// <summary>The current speed step; any thread. Clamped to the ladder.</summary>
    public int Step
    {
        get => _step;
        set => _step = Math.Clamp(value, PauseStep, MaxStep);
    }

    /// <summary>How the slider labels a step: <c>paused</c>, <c>0.25x</c> .. <c>16x</c>, <c>max</c>.</summary>
    public static string Label(int step)
    {
        step = Math.Clamp(step, PauseStep, MaxStep);
        if (step == PauseStep) return "paused";
        if (step == MaxStep) return "max";
        var m = Multipliers[step - 1];
        return m < 1
            ? m.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "x"
            : m.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "x";
    }

    /// <summary>Plays every Rx record through <paramref name="emit"/>, in order, then returns.</summary>
    public async Task PlayAsync(Func<byte[], CancellationToken, ValueTask> emit, CancellationToken ct)
    {
        long? previous = null;
        foreach (var record in _records)
        {
            if (record.Direction != WireDirection.Rx)
                continue;
            var gap = previous is null ? 0 : Math.Max(0, record.TimestampMs - previous.Value);
            previous = record.TimestampMs;
            await WaitAsync(gap, ct).ConfigureAwait(false);
            await emit(record.Payload, ct).ConfigureAwait(false);
        }
    }

    // Spends `originalMs` of the original session's time, at whatever speed each slice finds.
    private async Task WaitAsync(double originalMs, CancellationToken ct)
    {
        var waitedAtMax = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var step = _step;
            if (step == PauseStep)
            {
                await _delay(TimeSpan.FromMilliseconds(SliceMs), ct).ConfigureAwait(false);
                continue;
            }
            if (step == MaxStep)
            {
                if (waitedAtMax >= MaxIntervalMs)
                    return;
                var slice = MaxIntervalMs - waitedAtMax;
                await _delay(TimeSpan.FromMilliseconds(slice), ct).ConfigureAwait(false);
                waitedAtMax += slice;
                // A slice taken at max counts as all of the original gap: leaving max mid-gap
                // does not then sit out the rest of a long original pause.
                originalMs = 0;
                continue;
            }
            if (originalMs <= 0)
                return;
            var multiplier = Multipliers[step - 1];
            var realMs = Math.Min(SliceMs, originalMs / multiplier);
            await _delay(TimeSpan.FromMilliseconds(realMs), ct).ConfigureAwait(false);
            originalMs -= realMs * multiplier;
        }
    }
}
