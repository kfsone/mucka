using Mucka.Store;

namespace Mucka.WireLog;

/// <summary>One row of <c>sessions</c>, plus the rollup the <c>v_session_sizes</c> view computes.
/// <para><c>StoredBytes</c> is the whole of the size story now that nothing is compressed: it is
/// <c>SUM(LENGTH(data))</c>, which is the payloads plus the framing's ~4.2%.</para></summary>
public sealed record WireLogSessionInfo(
    long Id, long StartedMs, long? EndedMs, string? Host, string? ClientVersion,
    long Batches, long Records, long StoredBytes);

/// <summary>
/// Reads the wire log back: sessions, and batches to the exact records that were captured.
///
/// <para>The decoder is the other half of the storage contract and the reason the framing carries
/// per-record boundaries rather than just a byte stream: <see cref="ReadSession"/> reconstructs the
/// original sequence - same bytes, same directions, same timestamps, same order.</para>
///
/// <para>Nothing in the running client calls either of these. They are the read half of the store, for
/// the command-line tool that surfaces logs from it (see <c>TODO</c>) and for
/// <c>docs/Lab-spec.md</c>.</para>
///
/// <para>Reads open their own short-lived read-only connection and must not run on the UI thread
/// (Invariant #1): a session is tens of megabytes of blob to walk.</para>
/// </summary>
public static class WireLogExport
{
    /// <summary>Every session in the log, newest first.</summary>
    public static List<WireLogSessionInfo> ListSessions(string dbPath)
    {
        using var connection = MuckaDb.OpenRead(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, started_ms, ended_ms, host,
                   (SELECT client_version FROM sessions s2 WHERE s2.id = v.id),
                   COALESCE(batches, 0), COALESCE(records, 0), COALESCE(stored_bytes, 0)
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
                reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7)));
        }
        return result;
    }

    /// <summary>
    /// The complete record sequence of one session, in the order it was captured. Batches are read in
    /// <c>seq</c> order; a gap in <c>seq</c> shows up here as missing records and nothing else - the
    /// surrounding ones still decode, which is the point of framing per batch rather than per session.
    ///
    /// <para><b><c>records</c> is checked here, and it is the only check there is.</b> The framing has
    /// no internal redundancy, so a damaged blob usually re-parses into a well-formed run of records
    /// that is simply not what was written - measured over 200,000 mutated batches, 39,789 of them
    /// decoded into silent garbage. Requiring the blob to agree with the count stored beside it costs
    /// one comparison and removes almost all of that; a damaged batch throws
    /// <see cref="InvalidDataException"/> instead of yielding invented traffic, which is the right
    /// outcome for a corpus whose only job is to be believed.</para>
    /// </summary>
    /// <exception cref="InvalidDataException">A batch does not match its stored record count.</exception>
    public static IEnumerable<WireRecord> ReadSession(string dbPath, long sessionId)
    {
        using var connection = MuckaDb.OpenRead(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT base_ts_ms, data, records
            FROM batches WHERE session_id = $session ORDER BY seq;
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var baseTs = reader.GetInt64(0);
            var framed = (byte[])reader.GetValue(1);
            var records = reader.GetInt32(2);
            foreach (var record in WireLogFraming.Decode(framed, baseTs, records))
                yield return record;
        }
    }
}
