using Microsoft.Data.Sqlite;

namespace Mucka.Store;

/// <summary>
/// The one thing the baseline script cannot express: the two columns v0.20.0 added to tables that
/// already had rows. Runs once, on a file that predates the journal, and then never again.
///
/// <para><b>Why this exists at all.</b> The baseline is idempotent - every statement is
/// CREATE ... IF NOT EXISTS - so it can simply be the first migration and run against any file,
/// fresh or legacy. SQLite has no ADD COLUMN IF NOT EXISTS, so these two cannot join it. They have
/// to be decided by looking at the file, and looking at the file is exactly what a journal cannot
/// do. Hence: probe once, at the seam, then hand authority to the journal forever.</para>
///
/// <para><b>Every word of this class is frozen.</b> A schema change belongs in a new numbered script,
/// never here - see <see cref="MigrationScripts"/>. Every database in the world enters the journaled
/// era at ONE known shape, the shape that existed the day the journal was adopted, and that property
/// survives only while this file does not change. A guard test pins the list's length.</para>
///
/// <para>It runs when <c>SchemaVersions</c> is absent, which is true of a brand-new empty file as
/// well as a legacy one, and no discrimination is needed: on an empty file the tables do not exist
/// yet and every probe declines.</para>
/// </summary>
internal static class LegacySchemaAdopter
{
    /// <summary>The two columns v0.20.0 added to tables that already had rows. Frozen at that
    /// release; a column added after adoption is a numbered script, not an entry here.</summary>
    private static readonly (string Table, string Column, string Declaration)[] AddedColumns =
    [
        ("fights", "prev_same_name_ended_ms", "INTEGER"),
        ("score_events", "after_task_line", "INTEGER"),
    ];

    /// <summary>
    /// What <see cref="AddedColumns"/> held at adoption, and must still hold. A guard test compares
    /// this against <see cref="AddedColumnsNow"/>, so a model that "solves" tomorrow's change by
    /// editing the frozen list fails the build instead.
    ///
    /// <para>The CONTENT, not the count. A length check would pass a change that retargets an entry
    /// to a different table, column or declaration - silently altering the one shape every legacy
    /// database in the world converges to, which is the single thing this class exists to hold
    /// still.</para>
    /// </summary>
    internal const string AddedColumnsAtAdoption =
        "fights.prev_same_name_ended_ms INTEGER;score_events.after_task_line INTEGER";

    /// <summary>The same list as it stands now, in the same form. Exposed only so the guard test has
    /// both sides - comparing the constant against itself would be a test that cannot fail.</summary>
    internal static string AddedColumnsNow =>
        string.Join(";", AddedColumns.Select(c => $"{c.Table}.{c.Column} {c.Declaration}"));

    /// <summary>
    /// Converges <paramref name="connection"/> to the v0.20.0 shape in one transaction. Does NOT write
    /// the journal row - the caller does that, so the two stay in one place.
    /// </summary>
    internal static void Run(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();

        foreach (var (table, column, declaration) in AddedColumns)
        {
            // Two questions, and the order matters. On a brand-new empty file the table does not
            // exist yet - the baseline script creates it, with this column already in it - so an
            // ALTER here would throw. pragma_table_info returns no rows for a table that is absent,
            // which answers both questions at once: add the column only when the table is there and
            // the column is not.
            using var probe = connection.CreateCommand();
            probe.Transaction = transaction;
            probe.CommandText =
                "SELECT (SELECT COUNT(*) FROM pragma_table_info($table)) AS table_cols, " +
                "       (SELECT COUNT(*) FROM pragma_table_info($table) WHERE name = $column) AS has_column;";
            probe.Parameters.AddWithValue("$table", table);
            probe.Parameters.AddWithValue("$column", column);
            using (var reader = probe.ExecuteReader())
            {
                if (!reader.Read() || reader.GetInt64(0) == 0 || reader.GetInt64(1) != 0)
                    continue;
            }

            using var alter = connection.CreateCommand();
            alter.Transaction = transaction;
            // Identifiers cannot be parameterised; all three strings are compile-time literals from
            // the frozen table above, never anything a caller or the game supplies.
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {declaration};";
            alter.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>Whether this file has already entered the journaled era.</summary>
    internal static bool JournalExists(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $journal;";
        command.Parameters.AddWithValue("$journal", MigrationScripts.JournalTable);
        return Convert.ToInt64(command.ExecuteScalar()) != 0;
    }
}
