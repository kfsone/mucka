using System.Diagnostics;
using System.Text;

namespace Mucka.Core;

/// <summary>
/// Combat-tick phase diagnostics: what the two renderings of the tick were TOLD to do versus when
/// they actually did it. All methods are <see cref="ConditionalAttribute"/>("TICK_DIAG"), so every
/// call site is removed by the compiler unless built with <c>-p:TickDiag=true</c>. Zero cost
/// otherwise - and that matters more here than for most diagnostics, since one of the instrumented
/// paths is a thread-pool timer whose whole job is to be on time.
///
/// <para><b>What this exists to settle.</b> The click and the bar derive their phase from the same
/// anchor (<c>SidePanelViewModel.TickPhaseUtc</c>, the encounter's first swing) with arithmetic that
/// has been checked to agree exactly: the bar empties at <c>anchor + k*2000</c>, the high click fires
/// at <c>anchor + k*2000 - 275</c>. The anchor itself has been measured against real captures and
/// sits within 3% of a tick of the fitted lattice. So when the two are heard/seen to disagree, the
/// discrepancy is NOT in the derivation - it is in how long each takes to actually happen, which no
/// amount of reading the source can reveal. Hence measurement.</para>
///
/// <para><b>Reading the log</b> (<c>%TEMP%\mucka-tick.txt</c>). Every line carries a wall clock and a
/// monotonic +ms. The columns to correlate:</para>
/// <list type="bullet">
/// <item><description><c>anchor</c> - a new phase anchor arrived, and from which instrument.</description></item>
/// <item><description><c>click sched</c> - the delay the metronome computed, and the boundary it is aiming at.</description></item>
/// <item><description><c>click fire</c> - the timer actually ran. <c>late=</c> is timer slop; it should be small
/// and must NOT accumulate (the schedule is recomputed from the anchor every beat, so a growing value
/// here means the anchor is being replaced, not that the timer is drifting).</description></item>
/// <item><description><c>click audio</c> - how long the WinRT play call itself took to return. This is the
/// number the phase argument turns on: the lead is only 275 ms, so an audio path that costs a
/// comparable amount moves the "pre-tick" click onto the boundary and erases the bracket the player is
/// listening for.</description></item>
/// <item><description><c>bar restart</c> - the fraction of a tick the bar was told to resume from, and the
/// remaining-ms it was given. Compare its implied boundary with the click's.</description></item>
/// </list>
///
/// <para>Buffered and flushed in batches for the same reason <see cref="InputDiag"/> is: a
/// synchronous write per beat would itself perturb what is being timed.</para>
/// </summary>
public static class TickDiag
{
    private const int FlushEvery = 64;
    private static readonly object _gate = new();
    private static readonly Stopwatch _sw = Stopwatch.StartNew();
    private static readonly StringBuilder _buffer = new();
    private static int _pending;
    private static string? _path;

    [Conditional("TICK_DIAG")]
    public static void Log(string message)
    {
        try
        {
            lock (_gate)
            {
                _buffer.Append(DateTime.Now.ToString("HH:mm:ss.fff"))
                       .Append("  +").AppendFormat("{0,9:F1}", _sw.Elapsed.TotalMilliseconds)
                       .Append("ms  ").Append(message).Append('\n');
                if (++_pending >= FlushEvery)
                    FlushLocked();
            }
        }
        catch { /* diagnostics only - never throw into the beat */ }
    }

    /// <summary>Write any buffered lines to disk now (call on teardown so nothing is lost).</summary>
    [Conditional("TICK_DIAG")]
    public static void Flush()
    {
        try { lock (_gate) FlushLocked(); }
        catch { /* diagnostics only */ }
    }

    private static void FlushLocked()
    {
        if (_buffer.Length == 0)
            return;
        _path ??= System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mucka-tick.txt");
        System.IO.File.AppendAllText(_path, _buffer.ToString());
        _buffer.Clear();
        _pending = 0;
    }
}
