using Microsoft.Data.Sqlite;

namespace Mucka.Store;

/// <summary>
/// The name and tags a player gives a Session (one login, a <c>persona_sessions</c> row) in History.
/// Each edit is one short transaction on its own connection; another Mucka recording into the same
/// file waits behind it for at most <c>busy_timeout</c>.
///
/// <para>Threading: synchronous SQLite. Callers run it off the UI thread (Invariant #1).</para>
/// </summary>
public static class SessionLabels
{
    /// <summary>Sets the Session's name; a blank one removes it.</summary>
    public static void SetName(string databasePath, long sessionId, string? name)
    {
        name = name?.Trim() ?? string.Empty;
        using var connection = MuckaDb.Open(databasePath);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = name.Length == 0
            ? "DELETE FROM session_names WHERE persona_session_id = $id;"
            : """
              INSERT INTO session_names (persona_session_id, name) VALUES ($id, $name)
              ON CONFLICT (persona_session_id) DO UPDATE SET name = excluded.name;
              """;
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$name", name);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    /// <summary>Replaces the Session's tags with <paramref name="tags"/>, as <see cref="Parse"/> left
    /// them.</summary>
    public static void SetTags(string databasePath, long sessionId, IEnumerable<string> tags)
    {
        var wanted = Parse(string.Join(Separator, tags));
        using var connection = MuckaDb.Open(databasePath);
        using var transaction = connection.BeginTransaction();

        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM session_tags WHERE persona_session_id = $id;";
            clear.Parameters.AddWithValue("$id", sessionId);
            clear.ExecuteNonQuery();
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO session_tags (persona_session_id, tag) VALUES ($id, $tag);";
            insert.Parameters.AddWithValue("$id", sessionId);
            var tag = insert.Parameters.Add("$tag", SqliteType.Text);
            foreach (var t in wanted)
            {
                tag.Value = t;
                insert.ExecuteNonQuery();
            }
        }
        transaction.Commit();
    }

    /// <summary>What separates tags where they are typed and shown as one line.</summary>
    public const char Separator = ',';

    /// <summary>Tags from one typed line: split on <see cref="Separator"/>, trimmed, blanks dropped,
    /// and a tag that differs from an earlier one only in case dropped, the first spelling kept.</summary>
    public static IReadOnlyList<string> Parse(string? text)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in (text ?? string.Empty).Split(Separator))
        {
            var tag = part.Trim();
            if (tag.Length > 0 && seen.Add(tag))
                result.Add(tag);
        }
        return result;
    }

    /// <summary>The order tags are shown in: alphabetical ignoring case, then by exact spelling. The
    /// one ordering, used by the reader and by a row redrawn after an edit, so the two cannot
    /// disagree.</summary>
    public static IReadOnlyList<string> Ordered(IEnumerable<string> tags)
        => [.. tags.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ThenBy(t => t, StringComparer.Ordinal)];
}
