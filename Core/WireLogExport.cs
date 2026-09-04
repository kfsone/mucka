using Microsoft.Data.Sqlite;

namespace Mucka.Core;

/// <summary>One row of <c>sessions</c>, plus the rollup the <c>v_session_sizes</c> view computes.</summary>
public sealed record WireLogSessionInfo(
    long Id, long StartedMs, long? EndedMs, string? Host, string? ClientVersion,
    long Batches, long Records, long RawBytes, long StoredBytes)
{
    /// <summary>Compression actually achieved on this session. 0 when nothing was stored.</summary>
    public double Ratio => StoredBytes > 0 ? (double)RawBytes / StoredBytes : 0;
}

/// <summary>
/// Reads the wire log back: batches to records, and records to the <c>.jsonl</c> shape everything
/// downstream already understands.
///
/// <para>The decoder is the other half of the storage contract and the reason the framing carries
/// per-record boundaries rather than just a byte stream: <see cref="ReadSession"/> reconstructs the
/// exact original sequence — same bytes, same directions, same timestamps, same order — so nothing is
/// lost by storing compressed blobs instead of lines of text. <see cref="ExportSessionToJsonl"/> then
/// writes precisely what <see cref="JsonlWireLogSink"/> would have written for the same records.</para>
///
/// <para>Reads open their own short-lived read-only connection and must not run on the UI thread
/// (Invariant #1): decompressing a session is tens of megabytes of work.</para>
/// </summary>
public static class WireLogExport
{
    /// <summary>Every session in the log, newest first.</summary>
    public static List<WireLogSessionInfo> ListSessions(string dbPath)
    {
        using var connection = WireLogDb.OpenRead(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, started_ms, ended_ms, host,
                   (SELECT client_version FROM sessions s2 WHERE s2.id = v.id),
                   COALESCE(batches, 0), COALESCE(records, 0),
                   COALESCE(raw_bytes, 0), COALESCE(stored_bytes, 0)
            FROM v_session_sizes v
            ORDER BY started_ms DESC;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<WireLogSessionInfo>();
        while (reader.Read())
        {
            result.Add(new WireLogSessionInfo(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8)));
        }
        return result;
    }

    /// <summary>
    /// The complete record sequence of one session, in the order it was captured. Batches are read in
    /// <c>seq</c> order; a gap in <c>seq</c> (a batch that failed to write) shows up here as missing
    /// records and nothing else — the surrounding ones still decode, which is the point of framing per
    /// batch rather than per session.
    /// </summary>
    public static IEnumerable<WireRecord> ReadSession(string dbPath, long sessionId)
    {
        using var connection = WireLogDb.OpenRead(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT base_ts_ms, raw_bytes, codec, data
            FROM batches WHERE session_id = $session ORDER BY seq;
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var baseTs = reader.GetInt64(0);
            var rawBytes = reader.GetInt32(1);
            var codec = (WireLogCodec)reader.GetInt32(2);
            var blob = (byte[])reader.GetValue(3);
            var framed = WireLogFraming.Decompress(codec, blob, rawBytes);
            foreach (var record in WireLogFraming.Decode(framed, baseTs))
                yield return record;
        }
    }

    /// <summary>
    /// Writes one session out as <c>[ts,"rx"|"tx"|"an",text]</c> lines — byte-for-byte the format
    /// <see cref="JsonlWireLogSink"/> writes live, so every existing reader (the offline corpus, the
    /// decode probe, the fixtures in mudsharp.Tests) works on an exported session unchanged.
    /// </summary>
    /// <returns>The number of records written.</returns>
    public static int ExportSessionToJsonl(string dbPath, long sessionId, string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var writer = new StreamWriter(outputPath, append: false, System.Text.Encoding.UTF8);
        var count = 0;
        foreach (var record in ReadSession(dbPath, sessionId))
        {
            writer.WriteLine(JsonlWireLogSink.FormatLine(record.Direction, record.TimestampMs, record.Payload));
            count++;
        }
        return count;
    }

    /// <summary>The conventional export file name for a session — the same shape live capture uses.</summary>
    public static string SuggestFileName(WireLogSessionInfo session)
        => JsonlWireLogSink.BuildFileName(
            string.IsNullOrWhiteSpace(session.Host) ? "unknown" : session.Host,
            DateTimeOffset.FromUnixTimeMilliseconds(session.StartedMs).LocalDateTime);
}
