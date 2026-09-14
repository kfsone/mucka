using Mucka.Store;

namespace Mucka.WireLog;

/// <summary>One row of <c>sessions</c>, plus the rollup the <c>v_session_sizes</c> view computes.
/// <para><c>PayloadBytes</c> is <c>SUM(LENGTH(data))</c> - the traffic itself. The file costs more
/// than that: about 48 bytes of SQLite page and index overhead per row on top.</para></summary>
public sealed record WireLogSessionInfo(
    long Id, long StartedMs, long? EndedMs, string? Host, string? ClientVersion,
    long Records, long PayloadBytes);

/// <summary>
/// Reads the wire log back: sessions, and the exact records that were captured.
///
/// <para><see cref="ReadSession"/> reconstructs the original sequence - same bytes, same directions,
/// same timestamps, same order - by selecting it. The rows ARE the records.</para>
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
                   COALESCE(records, 0), COALESCE(payload_bytes, 0)
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
                reader.GetInt64(5), reader.GetInt64(6)));
        }
        return result;
    }

    /// <summary>
    /// The complete record sequence of one session, in the order it was captured.
    ///
    /// <para>Ordered by <c>id</c> rather than <c>seq</c>, and they are the same order: the writer
    /// takes a record's <c>seq</c> and hands it to the store under one lock, so nothing can reach the
    /// queue out of turn. <c>id</c> is the rowid, so walking it is the table's own order - no index
    /// and no sort. <c>seq</c> is still the one that PROVES the order is whole, because it is
    /// per-session and gapless where <c>id</c> is global and interleaves when two clients run.</para>
    ///
    /// <para>There is no decode step and no integrity check, because the rows are the records: the
    /// timestamp and the direction are columns and <c>data</c> is the bytes. Damage to a row is
    /// damage to that row, visible as bytes that do not read as MUD2 and confined to it.</para>
    /// </summary>
    public static IEnumerable<WireRecord> ReadSession(string dbPath, long sessionId)
    {
        using var connection = MuckaDb.OpenRead(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ts_ms, direction, data
            FROM wire WHERE session_id = $session ORDER BY id;
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            yield return new WireRecord(reader.GetInt64(0), (WireDirection)reader.GetInt32(1),
                (byte[])reader.GetValue(2));
    }
}
