using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Mucka.Core;

/// <summary>
/// Combat-tick phase diagnostics: what the two renderings of the tick - the bar and the click - were
/// TOLD to do versus when they actually did it. Off until the player types <c>$debug tick</c>, in any
/// build; not persisted, so every launch starts off. While off, <see cref="Log"/> is one volatile read.
///
/// <para>Output goes to <c>~/.mucka/mucka-tick.txt</c>. Every line carries a wall clock and a monotonic
/// +ms. The lines to correlate:</para>
/// <list type="bullet">
/// <item><description><c>gate</c> - the metronome driver's decision and every input to it.</description></item>
/// <item><description><c>anchor</c> / <c>beat</c> - the click's arming, and each beat's lateness and next
/// delay.</description></item>
/// <item><description><c>audio</c> - the platform play call and what came back.</description></item>
/// <item><description><c>bar restart</c> / <c>bar probe</c> - the fraction the bar was told to resume
/// from, and how long the compositor took to actually begin.</description></item>
/// </list>
///
/// <para>Buffered, and written from the thread pool: a write on the thread being timed would itself
/// perturb what is being measured, and a log call can land on the UI thread.</para>
/// </summary>
public static class TickDiag
{
    private const int FlushEvery = 64;
    private const long FlushEveryMs = 2000;
    private static readonly object _gate = new();
    private static readonly Stopwatch _sw = Stopwatch.StartNew();
    private static readonly StringBuilder _buffer = new();
    private static int _pending;
    private static long _lastFlushMs;
    private static bool _exitHooked;
    private static string? _path;
    private static volatile bool _enabled;

    /// <summary>Whether logging is on. Call sites that would do real work only to produce a log line
    /// check this first.</summary>
    public static bool Enabled => _enabled;

    /// <summary>The file lines are appended to.</summary>
    public static string LogPath => _path ??= Path.Combine(MuckaPaths.GetDataDirectory(), "mucka-tick.txt");

    /// <summary>Turns logging on or off. Turning it off writes out what is buffered.</summary>
    public static void SetEnabled(bool on)
    {
        if (_enabled == on)
            return;
        if (on)
        {
            _enabled = true;
            Log("start    tick diagnostics on");
        }
        else
        {
            Log("stop     tick diagnostics off");
            _enabled = false;
            Flush();
        }
    }

    public static void Log(string message)
    {
        if (!_enabled)
            return;
        try
        {
            lock (_gate)
            {
                _buffer.Append(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture))
                       .Append("  +").AppendFormat(CultureInfo.InvariantCulture, "{0,9:F1}", _sw.Elapsed.TotalMilliseconds)
                       .Append("ms  ").Append(message).Append('\n');
                if (!_exitHooked)
                {
                    _exitHooked = true;
                    AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
                }
                // On count and on age, and at process exit: a short session logs fewer than FlushEvery
                // lines, and a count-only flush then loses all of it.
                if (++_pending >= FlushEvery || _sw.ElapsedMilliseconds - _lastFlushMs >= FlushEveryMs)
                {
                    _pending = 0;
                    _lastFlushMs = _sw.ElapsedMilliseconds;
                    ThreadPool.QueueUserWorkItem(_ => Flush());
                }
            }
        }
        catch (Exception ex) { CrashLog.Write("TickDiag.Log", ex); }
    }

    /// <summary>Write any buffered lines to disk now.</summary>
    public static void Flush()
    {
        try
        {
            lock (_gate)
            {
                if (_buffer.Length == 0)
                    return;
                File.AppendAllText(LogPath, _buffer.ToString());
                _buffer.Clear();
            }
        }
        catch (Exception ex) { CrashLog.Write("TickDiag.Flush", ex); }
    }
}
