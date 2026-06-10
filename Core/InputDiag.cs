using System.Diagnostics;
using System.Text;

namespace Mucka.Core;

/// <summary>
/// Input-latency diagnostics. All methods are <see cref="ConditionalAttribute"/>("INPUT_DIAG"),
/// so every call site is removed by the compiler unless the project is built with the
/// INPUT_DIAG symbol defined (build with <c>-p:InputDiag=true</c>). Zero cost in normal builds.
///
/// Mirrors the existing FOCUS_DIAG pattern in GamePage.xaml.cs but lives in a shared type so
/// both the page (key handlers / UI-thread probe) and the view-model (InputText setter) can log
/// to one timeline. Output goes to <c>%TEMP%\mucka-input.txt</c>.
///
/// Log lines are BUFFERED in memory and flushed in batches (every <see cref="FlushEvery"/> lines
/// or on <see cref="Flush"/>) so the act of logging does NOT do a synchronous disk write on the
/// UI thread per event — that I/O would itself perturb the very latency we are measuring.
///
/// Reading the log: every line is "&lt;wall-clock&gt;  +&lt;ms-since-start&gt;  &lt;message&gt;".
/// The high-resolution +ms column is what you correlate — a keystroke's TextChanged stamp that
/// lands well after its KeyDown stamp, or a "UI STALL" line between them, pinpoints the culprit.
/// </summary>
public static class InputDiag
{
    private const int FlushEvery = 256;
    private static readonly object _gate = new();
    private static readonly Stopwatch _sw = Stopwatch.StartNew();
    private static readonly StringBuilder _buffer = new();
    private static int _pending;
    private static string? _path;

    [Conditional("INPUT_DIAG")]
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
        catch { /* diagnostics only — never throw into the input path */ }
    }

    /// <summary>Write any buffered lines to disk now (call on teardown so nothing is lost).</summary>
    [Conditional("INPUT_DIAG")]
    public static void Flush()
    {
        try { lock (_gate) FlushLocked(); }
        catch { /* diagnostics only */ }
    }

    private static void FlushLocked()
    {
        if (_buffer.Length == 0) return;
        _path ??= System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mucka-input.txt");
        System.IO.File.AppendAllText(_path, _buffer.ToString());
        _buffer.Clear();
        _pending = 0;
    }
}
