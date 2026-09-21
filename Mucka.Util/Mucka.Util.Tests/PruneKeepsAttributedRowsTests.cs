using System.Globalization;
using Microsoft.Data.Sqlite;
using Mucka.Store;

namespace Mucka.Util.Tests;

/// <summary>
/// The prune in <c>0004_prune_unattributable.sql</c> removes only rows no login can ever claim.
///
/// <para><b>What this is here for.</b> Five of the eleven DELETEs address the encounter children,
/// which carry no <c>persona_session_id</c> of their own and reach one through <c>encounters</c>.
/// Written without that reach they remove every child older than the cut, attributed or not, which
/// is the destructive reading of the same sentence and looks identical in a diff. The six siblings
/// that DO carry the column write the guard inline; <c>swings</c> stands for them here as the
/// control, so a change that widened both halves at once could not pass.</para>
///
/// <para>The script is executed directly against a migrated file rather than by re-running the
/// ladder: the journal has already recorded it on a fresh database, so seeding pre-cut rows and
/// running the text is the only way to observe what it does to rows that exist.</para>
/// </summary>
public sealed class PruneKeepsAttributedRowsTests
{
    /// <summary>The script's own frozen cut, 2026-09-10 00:43:20 UTC. Restated here rather than
    /// parsed out of the SQL, because a test that reads the constant from the thing under test
    /// cannot notice the constant moving.</summary>
    private const long Cut = 1789001000023L;

    private const long BeforeCut = Cut - 60_000;

    /// <summary>The script under test, found by name rather than by position: inserting a migration
    /// ahead of it must not silently point these tests at a different script.</summary>
    private static string PruneScript
        => MigrationScripts.All.First(s => s.Name == "0004_prune_unattributable.sql").Contents;

    [Fact]
    public void RowsBeforeTheCutSurvive_WhenALoginClaimsThem_AndGoOtherwise()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mucka-prune", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "seeded.db");

        try
        {
            using (var connection = MuckaDb.Open(path))
            {
                Seed(connection);
                Execute(connection, PruneScript);

                // The attributed side, whole: the parent and one row of every child table.
                Assert.Equal(1, Count(connection, "encounters",               "persona_session_id IS NOT NULL"));
                Assert.Equal(1, Count(connection, "encounter_lines",           "encounter_started_at_ms = " + BeforeCut));
                Assert.Equal(1, Count(connection, "encounter_events",          "encounter_started_at_ms = " + BeforeCut));
                Assert.Equal(1, Count(connection, "encounter_stats",           "encounter_started_at_ms = " + BeforeCut));
                Assert.Equal(1, Count(connection, "encounter_contents",        "encounter_started_at_ms = " + BeforeCut));
                Assert.Equal(1, Count(connection, "creature_values",           "encounter_started_at_ms = " + BeforeCut));
                Assert.Equal(1, Count(connection, "encounter_contents_items",  "name = 'kept'"));

                // The unattributable side, whole: the parent with no login, its children, and the
                // orphan child that never had a parent at all.
                Assert.Equal(0, Count(connection, "encounters",               "persona_session_id IS NULL"));
                Assert.Equal(0, Count(connection, "encounter_lines",           "encounter_started_at_ms <> " + BeforeCut));
                Assert.Equal(0, Count(connection, "encounter_events",          "encounter_started_at_ms <> " + BeforeCut));
                Assert.Equal(0, Count(connection, "encounter_stats",           "encounter_started_at_ms <> " + BeforeCut));
                Assert.Equal(0, Count(connection, "encounter_contents",        "encounter_started_at_ms <> " + BeforeCut));
                Assert.Equal(0, Count(connection, "creature_values",           "encounter_started_at_ms <> " + BeforeCut));
                Assert.Equal(0, Count(connection, "encounter_contents_items",  "name <> 'kept'"));

                // The control, from the half that carries the column inline.
                Assert.Equal(1, Count(connection, "swings", "persona_session_id IS NOT NULL"));
                Assert.Equal(0, Count(connection, "swings", "persona_session_id IS NULL"));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>A row at or after the cut is never touched, whether a login claims it or not - the
    /// instant is the whole bound, and an unattributed row newer than it is a backfill gap to
    /// investigate rather than evidence to destroy.</summary>
    [Fact]
    public void NothingAtOrAfterTheCutIsTouched()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mucka-prune", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "after.db");

        try
        {
            using (var connection = MuckaDb.Open(path))
            {
                Execute(connection, $"""
                    INSERT INTO encounters (id, encounter_started_at_ms, persona_session_id)
                         VALUES (30, {Cut}, NULL);
                    INSERT INTO encounter_events (encounter_started_at_ms, ts, kind)
                         VALUES ({Cut}, {Cut}, 'Hit');
                    INSERT INTO encounter_lines (encounter_started_at_ms, phase, ord, text)
                         VALUES ({Cut}, 'tail', 0, 'after');
                    INSERT INTO creature_values (encounter_started_at_ms, ts, npc, value, ambiguous)
                         VALUES ({Cut}, {Cut}, 'rat', 10, 0);
                    """);
                Execute(connection, PruneScript);

                Assert.Equal(1, Count(connection, "encounters",       "1=1"));
                Assert.Equal(1, Count(connection, "encounter_events", "1=1"));
                Assert.Equal(1, Count(connection, "encounter_lines",  "1=1"));
                Assert.Equal(1, Count(connection, "creature_values",  "1=1"));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Two encounters sharing one start instant, one of them attributed - which the schema permits,
    /// because <c>ix_encounters_key</c> is deliberately not unique. The children keyed on that
    /// instant belong to whichever of the two wrote them and nothing here can say which, so they
    /// stay: a row kept can be deleted later and a row deleted cannot be got back.
    /// </summary>
    [Fact]
    public void AChildOfAnAmbiguousPairIsKept_WhenEitherParentIsAttributed()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mucka-prune", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "ambiguous.db");

        try
        {
            using (var connection = MuckaDb.Open(path))
            {
                Execute(connection, $"""
                    INSERT INTO mucka_runs (id, started_ms) VALUES (1, {BeforeCut});
                    INSERT INTO persona_sessions (id, mucka_run_id, started_ms) VALUES (1, 1, {BeforeCut});
                    INSERT INTO encounters (id, encounter_started_at_ms, persona_session_id)
                         VALUES (40, {BeforeCut}, NULL), (41, {BeforeCut}, 1);
                    INSERT INTO encounter_events (encounter_started_at_ms, ts, kind)
                         VALUES ({BeforeCut}, {BeforeCut}, 'Hit');
                    """);
                Execute(connection, PruneScript);

                Assert.Equal(1, Count(connection, "encounters",       "1=1"));
                Assert.Equal(1, Count(connection, "encounter_events", "1=1"));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// One login, two encounters before the cut - one claimed by it and one claimed by nothing -
    /// with a row of every child table under each, one child orphaned outright, and an attributed
    /// and an unattributed swing for the control.
    /// </summary>
    private static void Seed(SqliteConnection connection)
    {
        const long orphan = BeforeCut - 1000;
        const long unattributed = BeforeCut - 2000;

        Execute(connection, $"""
            INSERT INTO mucka_runs (id, started_ms) VALUES (1, {BeforeCut});
            INSERT INTO persona_sessions (id, mucka_run_id, persona, started_ms)
                 VALUES (1, 1, 'Awlie', {BeforeCut});

            INSERT INTO encounters (id, encounter_started_at_ms, persona_session_id)
                 VALUES (10, {BeforeCut}, 1), (11, {unattributed}, NULL);

            INSERT INTO encounter_lines (encounter_started_at_ms, phase, ord, text)
                 VALUES ({BeforeCut}, 'tail', 0, 'kept'),
                        ({unattributed}, 'tail', 0, 'gone'),
                        ({orphan}, 'tail', 0, 'orphan');
            INSERT INTO encounter_events (encounter_started_at_ms, ts, kind)
                 VALUES ({BeforeCut}, {BeforeCut}, 'Hit'),
                        ({unattributed}, {unattributed}, 'Hit'),
                        ({orphan}, {orphan}, 'Hit');
            INSERT INTO encounter_stats (encounter_started_at_ms, ts, reason)
                 VALUES ({BeforeCut}, {BeforeCut}, 'start'),
                        ({unattributed}, {unattributed}, 'start'),
                        ({orphan}, {orphan}, 'start');
            INSERT INTO creature_values (encounter_started_at_ms, ts, npc, value, ambiguous)
                 VALUES ({BeforeCut}, {BeforeCut}, 'rat', 10, 0),
                        ({unattributed}, {unattributed}, 'rat', 10, 0),
                        ({orphan}, {orphan}, 'rat', 10, 0);

            INSERT INTO encounter_contents (id, encounter_started_at_ms, ts, carried_count)
                 VALUES (20, {BeforeCut}, {BeforeCut}, 1),
                        (21, {unattributed}, {unattributed}, 1),
                        (22, {orphan}, {orphan}, 1);
            INSERT INTO encounter_contents_items (contents_id, ord, name, is_creature, is_carried)
                 VALUES (20, 0, 'kept', 0, 1),
                        (21, 0, 'gone', 0, 1),
                        (22, 0, 'orphan', 0, 1);

            INSERT INTO swings (ts, dir, hit, persona_session_id)
                 VALUES ({BeforeCut}, 'in', 1, 1), ({BeforeCut}, 'in', 1, NULL);
            """);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static int Count(SqliteConnection connection, string table, string where)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {where};";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
