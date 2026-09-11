using System.Text;
using System.Text.Json;

namespace Mucka.Core;

/// <summary>
/// The original wire-log backend, unchanged in what it produces: one JSON array per line,
/// <c>[timestamp_ms,"rx"|"tx"|"an",text]</c>, in <c>session-rec.{host}.{stamp}.jsonl</c>.
///
/// <para><b>Kept deliberately.</b> Every session recording the owner has, the whole offline analysis
/// corpus under tools/, the decode probe and several tests read exactly this shape — see
/// mudsharp.Tests' wyvern-poison-death fixture. It is also the format a plain-text capture for other
/// players would want. It is additive-only alongside <see cref="SqliteWireLogSink"/>; retiring it is a
/// separate decision.</para>
///
/// <para><b>What it costs, so the choice is informed.</b> Measured on the owner's 40 captures (MB =
/// 10^6 bytes, as in <see cref="WireLogFraming"/>): 6.14 MB of payload becomes 17.15 MB on disk,
/// because MUD2's C1 codes are high bytes and <see cref="JsonSerializer"/> writes each one as a
/// six-character <c>ÿ</c> escape. That is 1.924 MB per play-hour against the SQLite sink's 0.76 —
/// 2.5x the disk for the same traffic, and it was eleven times while the SQLite sink still
/// compressed.</para>
///
/// <para><b>Encoding.</b> The inverse of the contract on <see cref="WireRecord"/>: rx/tx payloads are raw
/// wire bytes and come back through Latin-1 (byte-for-char, lossless); annotations are UTF-8 of a .NET
/// string. That reproduces the previous writer's output byte-for-byte.</para>
///
/// <para><b>Threading.</b> One lock around the writer, which is <c>AutoFlush</c> — the file is meant to
/// survive a crash mid-session, which is most of why it exists.</para>
/// </summary>
public sealed class JsonlWireLogSink : IWireLogSink
{
    private readonly object _lock = new();
    private StreamWriter? _writer;

    /// <summary>Creates the file. Throws if it cannot be created — the caller surfaces the message.</summary>
    public JsonlWireLogSink(string filePath)
    {
        Location = filePath;
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        _writer = new StreamWriter(filePath, append: false, Encoding.UTF8) { AutoFlush = true };
    }

    public string Location { get; }

    /// <summary>Builds the conventional file name: <c>session-rec.{host}.{yyyyMMdd-HHmmss}.jsonl</c>,
    /// with anything illegal in the host replaced. Shared with the exporter so a round-tripped session
    /// lands on a name of the same shape.</summary>
    public static string BuildFileName(string host, DateTime localTimestamp)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
        var safeHost = new string(host.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return $"session-rec.{safeHost}.{localTimestamp:yyyyMMdd-HHmmss}.jsonl";
    }

    /// <summary>The three-letter mode token this format uses for a direction.</summary>
    public static string ModeToken(WireDirection direction) => direction switch
    {
        WireDirection.Rx => "rx",
        WireDirection.Tx => "tx",
        _ => "an",
    };

    /// <summary>Decodes a payload for this format — see the encoding note on the class.</summary>
    public static string DecodePayload(WireDirection direction, ReadOnlySpan<byte> payload)
        => direction == WireDirection.Annotation
            ? Encoding.UTF8.GetString(payload)
            : Encoding.Latin1.GetString(payload);

    /// <summary>Renders one record as the exact line this format uses, newline excluded.</summary>
    public static string FormatLine(WireDirection direction, long timestampMs, ReadOnlySpan<byte> payload)
        => $"[{timestampMs},{JsonSerializer.Serialize(ModeToken(direction))}," +
           $"{JsonSerializer.Serialize(DecodePayload(direction, payload))}]";

    public void Record(WireDirection direction, long timestampMs, ReadOnlySpan<byte> payload)
    {
        var line = FormatLine(direction, timestampMs, payload);
        lock (_lock)
        {
            _writer?.WriteLine(line);
        }
    }

    public void Flush()
    {
        lock (_lock)
        {
            _writer?.Flush();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
