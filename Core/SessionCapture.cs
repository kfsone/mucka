using System.Text;
using System.Text.Json;

namespace Mucka.Core;

/// <summary>
/// Whole-session recording (an advanced/opt-in feature). Records raw RX/TX bytes and text
/// annotations to a JSONL file. Available in all builds/platforms.
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

    private static string GetCaptureDirectory()
    {
        // Desktop capture files are transient debug artifacts; keep them in temp, not roaming app data.
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
            return Path.Combine(Path.GetTempPath(), "mucka");

        return Path.Combine(FileSystem.Current.CacheDirectory, "mucka");
    }

    public bool TryStart(string hostname, out string? error)
    {
        lock (_lock)
        {
            if (_isRecording)
            {
                error = null;
                return true;
            }

            try
            {
                var invalidChars = new HashSet<char>(Path.GetInvalidFileNameChars());
                var safeHost = new string(hostname.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray());
                var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var filename = $"session-rec.{safeHost}.{timestamp}.jsonl";
                var captureDirectory = GetCaptureDirectory();
                Directory.CreateDirectory(captureDirectory);
                FilePath = Path.Combine(captureDirectory, filename);
                _writer = new StreamWriter(FilePath, append: false, Encoding.UTF8) { AutoFlush = true };
                _isRecording = true;
                WriteEntryLocked("an", $"capture started: {hostname}");
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                _writer?.Dispose();
                _writer = null;
                _isRecording = false;
                FilePath = null;
                error = ex.Message;
                return false;
            }
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
