using System.Text;
using System.Text.Json;

namespace Mucka.Core;

/// <summary>
/// Debug session capture. Records raw RX/TX bytes and text annotations to a JSONL file.
/// Each line: [timestamp_ms,"rx"|"tx"|"an",data_string]
/// File: session-rec.{hostname}.{start_datetime}.jsonl in the app's data directory.
/// </summary>
public sealed class SessionCapture : IDisposable
{
    private StreamWriter? _writer;
    private readonly object _lock = new();
    private volatile bool _isRecording;

    public bool IsRecording => _isRecording;
    public string? FilePath { get; private set; }

    public void Start(string hostname)
    {
        lock (_lock)
        {
            if (_isRecording) return;
            var safeHost = string.Concat(hostname.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var filename = $"session-rec.{safeHost}.{timestamp}.jsonl";
            FilePath = Path.Combine(FileSystem.Current.AppDataDirectory, filename);
            _writer = new StreamWriter(FilePath, append: false, Encoding.UTF8) { AutoFlush = true };
            _isRecording = true;
            WriteEntryLocked("an", $"capture started: {hostname}");
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!_isRecording) return;
            WriteEntryLocked("an", "capture stopped");
            _isRecording = false;
            _writer!.Dispose();
            _writer = null;
        }
    }

    public void RecordRx(ReadOnlySpan<byte> data)
    {
        if (!_isRecording) return;
        var text = Encoding.Latin1.GetString(data);
        lock (_lock)
        {
            WriteEntryLocked("rx", text);
        }
    }

    public void RecordTx(byte[] data)
    {
        if (!_isRecording) return;
        var text = Encoding.Latin1.GetString(data);
        lock (_lock)
        {
            WriteEntryLocked("tx", text);
        }
    }

    public void Annotate(string message)
    {
        if (!_isRecording) return;
        lock (_lock)
        {
            WriteEntryLocked("an", message);
        }
    }

    private void WriteEntryLocked(string mode, string data)
    {
        if (_writer == null) return;
        var ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _writer.WriteLine($"[{ms},{JsonSerializer.Serialize(mode)},{JsonSerializer.Serialize(data)}]");
    }

    public void Dispose() => Stop();
}
