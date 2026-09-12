using System.Text;

namespace Mucka.Core;

/// <summary>
/// Whole-session recording: raw RX/TX bytes and text annotations, fanned out to whichever
/// <see cref="IWireLogSink"/>s are attached.
///
/// <para>The tap and nothing else - it stamps the time, tags the direction, and hands the bytes to
/// every attached sink. Two sinks exist and they are independent:</para>
/// <list type="bullet">
///   <item><description><b>The file</b> (<see cref="JsonlWireLogSink"/>) - armed by hand, per run, from
///   the connect page or the in-game capture button.</description></item>
///   <item><description><b>The database</b> (<see cref="SqliteWireLogSink"/>) - driven by the global
///   <c>logwiresession</c> setting in mucka.ini, started with the session, never touched again. Its
///   whole design goal is to be turned on once and forgotten.</description></item>
/// </list>
///
/// <para>They start and stop separately on purpose: the in-game "capture stopped" button must not
/// silently switch off a log the player turned on months ago in settings.</para>
///
/// <para><b>Threading.</b> <see cref="RecordRx"/> is called from the socket read loop and
/// <see cref="RecordTx"/> from the write loop, concurrently. The sink list is copied on write, so the
/// hot path reads it without a lock; the sinks themselves are individually thread-safe.</para>
/// </summary>
public sealed class SessionCapture : IDisposable
{
    private readonly object _lock = new();
    // Copy-on-write: RecordRx/RecordTx read this reference once and enumerate it without a lock, so a
    // sink being attached or detached can never contend with the socket threads.
    private volatile IWireLogSink[] _sinks = [];
    private JsonlWireLogSink? _file;
    private IWireLogSink? _database;

    /// <summary>True when anything at all is being recorded.</summary>
    public bool IsRecording => _sinks.Length > 0;

    /// <summary>True when the manual JSONL file capture is running - what the capture button reflects.</summary>
    public bool IsFileRecording => _file is not null;

    /// <summary>Path of the active JSONL capture file, or null.</summary>
    public string? FilePath => _file?.Location;

    /// <summary>Path of the active wire-log database, or null.</summary>
    public string? DatabasePath => _database?.Location;

    /// <summary>
    /// Starts the manual JSONL file capture in <paramref name="directory"/>, named for
    /// <paramref name="hostname"/> and the current local time. Idempotent - a second call while one is
    /// running succeeds and changes nothing.
    /// </summary>
    public bool TryStartFile(string directory, string hostname, out string? error)
    {
        lock (_lock)
        {
            if (_file is not null)
            {
                error = null;
                return true;
            }

            try
            {
                var path = Path.Combine(directory, JsonlWireLogSink.BuildFileName(hostname, DateTime.Now));
                var sink = new JsonlWireLogSink(path);
                _file = sink;
                AttachLocked(sink);
                error = null;
            }
            catch (Exception ex)
            {
                _file = null;
                error = ex.Message;
                return false;
            }
        }
        Annotate($"capture started: {hostname}");
        return true;
    }

    /// <summary>Stops the manual JSONL file capture. Leaves the database log alone.</summary>
    public void StopFile()
    {
        JsonlWireLogSink? sink;
        lock (_lock)
        {
            if (_file is null) return;
            sink = _file;
        }
        Annotate("capture stopped");
        lock (_lock)
        {
            DetachLocked(sink);
            _file = null;
        }
        sink.Dispose();
    }

    /// <summary>
    /// Starts the always-on database log. The sink is supplied by the caller so this class stays free
    /// of any decision about where the database lives; <see cref="MuckaConnection"/> owns that.
    /// Idempotent.
    /// </summary>
    public bool TryStartDatabase(Func<IWireLogSink> factory, out string? error)
    {
        lock (_lock)
        {
            if (_database is not null)
            {
                error = null;
                return true;
            }

            try
            {
                var sink = factory();
                _database = sink;
                AttachLocked(sink);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                _database = null;
                error = ex.Message;
                return false;
            }
        }
    }

    /// <summary>Flushes every sink as far as it can go without closing. Called on disconnect.</summary>
    public void Flush()
    {
        foreach (var sink in _sinks)
        {
            try { sink.Flush(); } catch { /* best-effort: a recorder must not break the session */ }
        }
    }

    /// <summary>Stops everything and releases every sink.</summary>
    public void Stop()
    {
        if (_sinks.Length == 0) return;
        Annotate("capture stopped");
        IWireLogSink[] sinks;
        lock (_lock)
        {
            sinks = _sinks;
            _sinks = [];
            _file = null;
            _database = null;
        }
        foreach (var sink in sinks)
        {
            try { sink.Dispose(); } catch { /* best-effort */ }
        }
    }

    public void RecordRx(ReadOnlySpan<byte> data) => Emit(WireDirection.Rx, data);

    public void RecordTx(byte[] data) => Emit(WireDirection.Tx, data);

    /// <summary>Writes a free-text client-side note into the log. No-ops when nothing is recording,
    /// which is what lets the callers sprinkle these unconditionally.</summary>
    public void Annotate(string message)
    {
        var sinks = _sinks;
        if (sinks.Length == 0) return;
        // UTF-8 rather than Latin-1: an annotation is a .NET string, and UTF-8 round-trips all of
        // them. See the encoding contract on WireRecord.
        Emit(WireDirection.Annotation, Encoding.UTF8.GetBytes(message));
    }

    private void Emit(WireDirection direction, ReadOnlySpan<byte> payload)
    {
        var sinks = _sinks;
        if (sinks.Length == 0) return;
        var timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var sink in sinks)
        {
            try { sink.Record(direction, timestampMs, payload); }
            catch { /* best-effort: a failing recorder must never break the socket loop */ }
        }
    }

    private void AttachLocked(IWireLogSink sink) => _sinks = [.. _sinks, sink];

    private void DetachLocked(IWireLogSink sink) => _sinks = _sinks.Where(s => !ReferenceEquals(s, sink)).ToArray();

    public void Dispose() => Stop();
}
