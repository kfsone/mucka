using System.Globalization;
using System.Text;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// Reads a committed <c>.c1</c> capture from <c>Fixtures/Data</c>: one record per line,
/// <c>&lt;ts_ms&gt; &lt;rx|tx&gt; &lt;payload&gt;</c>, the payload raw bytes except for three escapes -
/// <c>\\</c>, <c>\r</c> and <c>\n</c> - that keep a record on one line.
/// </summary>
internal static class C1Capture
{
    internal readonly record struct Record(long TimestampMs, string Direction, byte[] Payload);

    internal static string PathOf(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Data", name);

    /// <summary>Every record in the capture, in order.</summary>
    internal static IEnumerable<Record> Read(string name)
    {
        foreach (var raw in File.ReadLines(PathOf(name), Encoding.Latin1))
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            var parts = raw.Split(' ', 3);
            if (parts.Length < 3)
                continue;
            yield return new Record(long.Parse(parts[0], CultureInfo.InvariantCulture), parts[1], Unescape(parts[2]));
        }
    }

    /// <summary>The server's side of the capture, in order.</summary>
    internal static IEnumerable<byte[]> ReadRx(string name)
        => Read(name).Where(r => r.Direction == "rx").Select(r => r.Payload);

    private static byte[] Unescape(string payload)
    {
        var bytes = new List<byte>(payload.Length);
        for (var i = 0; i < payload.Length; i++)
        {
            if (payload[i] == '\\' && i + 1 < payload.Length)
                bytes.Add(payload[++i] switch { 'r' => (byte)0x0D, 'n' => (byte)0x0A, _ => (byte)0x5C });
            else
                bytes.Add((byte)payload[i]);
        }
        return bytes.ToArray();
    }
}
