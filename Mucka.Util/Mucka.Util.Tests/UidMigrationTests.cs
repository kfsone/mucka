using System.Globalization;
using Microsoft.Data.Sqlite;
using Mucka.Store;

namespace Mucka.Util.Tests;

/// <summary>
/// <c>0006_uids.sql</c>, run on a database in the shape the earlier scripts leave. Built by executing
/// the scripts' text up to 0006 and then 0006 itself, because <see cref="MuckaDb.Open"/> would run
/// them all before anything could be seeded.
///
/// <para>EVIDENCE: the same script run over a snapshot of the operator's database (1,946 encounters,
/// 34,935 swings, 3,358 fights, 1,179,988 wire rows) left every table's row count unchanged, kept every
/// keyed swing, fight and score line keyed, kept the 2,350 distinct swing keys as 2,350 distinct uids,
/// and passed <c>integrity_check</c> and <c>foreign_key_check</c>. 406 of those keys had no
/// <c>encounters</c> row at all, which is what <see cref="RowsWhoseEncounterWasNeverWritten_KeepTheirGrouping"/>
/// is for.</para>
/// </summary>
public sealed class UidMigrationTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mucka-uid-migration-tests", Guid.NewGuid().ToString("N"));

    public UidMigrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "(null)";
    }

    private SqliteConnection OpenBefore0006(string name)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(_directory, name), Pooling = false }.ToString());
        connection.Open();
        foreach (var script in MigrationScripts.All.TakeWhile(s => s.Name != "0006_uids.sql"))
            Execute(connection, script.Contents);
        return connection;
    }

    private static void Migrate(SqliteConnection connection)
        => Execute(connection, MigrationScripts.All.First(s => s.Name == "0006_uids.sql").Contents);

    [Fact]
    public void EveryRunGetsADistinctUid()
    {
        using var db = OpenBefore0006("runs.db");
        Execute(db, "INSERT INTO mucka_runs (started_ms, host) VALUES (1,'h'), (1,'h'), (2,'h');");

        Migrate(db);

        Assert.Equal("3", Scalar(db, "SELECT COUNT(DISTINCT uid) FROM mucka_runs WHERE uid IS NOT NULL"));
    }

    [Fact]
    public void EachEncounterGetsAUid_AndItsChildrenAndFactsFollowIt()
    {
        using var db = OpenBefore0006("encounters.db");
        Execute(db, """
            INSERT INTO mucka_runs (id, started_ms) VALUES (1, 1);
            INSERT INTO persona_sessions (id, mucka_run_id, started_ms) VALUES (1, 1, 1);
            INSERT INTO encounters (id, encounter_started_at_ms, persona_session_id) VALUES (10, 500, 1), (11, 600, 1);
            INSERT INTO swings (ts, dir, hit, persona_session_id, encounter_started_at_ms) VALUES (500, 'in', 1, 1, 500), (600, 'in', 1, 1, 600);
            INSERT INTO encounter_lines (encounter_started_at_ms, phase, ord, text) VALUES (500, 'tail', 0, 'a'), (600, 'tail', 0, 'b');
            """);

        Migrate(db);

        Assert.Equal("2", Scalar(db, "SELECT COUNT(DISTINCT uid) FROM encounters"));
        Assert.Equal("500", Scalar(db, "SELECT started_at_ms FROM encounters WHERE id = 10"));
        Assert.Equal("a", Scalar(db,
            "SELECT l.text FROM encounter_lines l JOIN encounters e ON e.uid = l.encounter_uid WHERE e.id = 10"));
        Assert.Equal("b", Scalar(db,
            "SELECT l.text FROM encounter_lines l JOIN encounters e ON e.uid = l.encounter_uid WHERE e.id = 11"));
        Assert.Equal("1", Scalar(db,
            "SELECT COUNT(*) FROM swings s JOIN encounters e ON e.uid = s.encounter_uid WHERE e.id = 11 AND s.ts = 600"));
    }

    [Fact]
    public void RowsWhoseEncounterWasNeverWritten_KeepTheirGrouping()
    {
        using var db = OpenBefore0006("orphans.db");
        Execute(db, """
            INSERT INTO mucka_runs (id, started_ms) VALUES (1, 1);
            INSERT INTO persona_sessions (id, mucka_run_id, started_ms) VALUES (1, 1, 1);
            INSERT INTO swings (ts, dir, hit, persona_session_id, encounter_started_at_ms)
                 VALUES (1, 'in', 1, 1, 700), (2, 'in', 1, 1, 700), (3, 'in', 1, 1, 800), (4, 'in', 1, 1, NULL);
            INSERT INTO fights (persona_session_id, encounter_started_at_ms, started_at_ms, ended_at_ms, duration_ms,
                                npc_name, npc_group, outcome, you_hits, you_misses, they_hits, they_misses,
                                approx_damage_done, approx_damage_taken, narrative_mode, is_blind, is_deaf,
                                is_crippled, is_dumb, effects)
                 VALUES (1, 700, 1, 2, 1, 'rat0', 'rat', 'Won', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, '');
            """);

        Migrate(db);

        // Both swings of the encounter written under 700 share one uid, 800 has another, and a swing
        // with no encounter still has none. The fight of 700 joins the same group as its swings.
        Assert.Equal("2", Scalar(db, "SELECT COUNT(DISTINCT encounter_uid) FROM swings"));
        Assert.Equal("1", Scalar(db, "SELECT COUNT(DISTINCT encounter_uid) FROM swings WHERE ts IN (1, 2)"));
        Assert.Equal("1", Scalar(db, "SELECT COUNT(*) FROM swings WHERE encounter_uid IS NULL"));
        Assert.Equal("1", Scalar(db,
            "SELECT COUNT(*) FROM fights f JOIN swings s ON s.encounter_uid = f.encounter_uid WHERE s.ts = 1"));
    }

    [Fact]
    public void TwoEncountersOfOneLoginSharingAKey_BothSurvive_AndTheirChildrenGoToOne()
    {
        using var db = OpenBefore0006("shared.db");
        Execute(db, """
            INSERT INTO mucka_runs (id, started_ms) VALUES (1, 1);
            INSERT INTO persona_sessions (id, mucka_run_id, started_ms) VALUES (1, 1, 1), (2, 1, 1);
            INSERT INTO encounters (id, encounter_started_at_ms, persona_session_id) VALUES (10, 500, 1), (11, 500, 2);
            INSERT INTO encounter_lines (encounter_started_at_ms, phase, ord, text) VALUES (500, 'tail', 0, 'x');
            """);

        Migrate(db);

        Assert.Equal("2", Scalar(db, "SELECT COUNT(DISTINCT uid) FROM encounters"));
        Assert.Equal("1", Scalar(db, "SELECT COUNT(*) FROM encounter_lines"));
        Assert.Equal("1", Scalar(db, "SELECT COUNT(*) FROM encounter_lines l JOIN encounters e ON e.uid = l.encounter_uid"));
    }

    [Fact]
    public void AFactWithNoLoginSharingAnEncountersKey_DoesNotPullTheChildrenAwayFromIt()
    {
        using var db = OpenBefore0006("nulllogin.db");
        Execute(db, """
            INSERT INTO mucka_runs (id, started_ms) VALUES (1, 1);
            INSERT INTO persona_sessions (id, mucka_run_id, started_ms) VALUES (2, 1, 1);
            INSERT INTO encounters (id, encounter_started_at_ms, persona_session_id) VALUES (10, 500, 2);
            INSERT INTO swings (ts, dir, hit, persona_session_id, encounter_started_at_ms) VALUES (500, 'in', 1, NULL, 500);
            INSERT INTO encounter_lines (encounter_started_at_ms, phase, ord, text) VALUES (500, 'tail', 0, 'x');
            """);

        Migrate(db);

        Assert.Equal("1", Scalar(db,
            "SELECT COUNT(*) FROM encounter_lines l JOIN encounters e ON e.uid = l.encounter_uid"));
    }

    [Fact]
    public void NothingIsLeftNamingTheOldColumn_AndTheRowsAreWhole()
    {
        using var db = OpenBefore0006("whole.db");
        Execute(db, """
            INSERT INTO mucka_runs (id, started_ms) VALUES (1, 1);
            INSERT INTO persona_sessions (id, mucka_run_id, started_ms) VALUES (1, 1, 1);
            INSERT INTO encounters (id, encounter_started_at_ms, persona_session_id) VALUES (10, 500, 1);
            INSERT INTO encounter_contents (id, encounter_started_at_ms, ts, carried_count) VALUES (20, 500, 500, 1);
            INSERT INTO encounter_contents_items (contents_id, ord, name, is_creature, is_carried) VALUES (20, 0, 'sword', 0, 1);
            """);

        Migrate(db);

        Assert.Equal("0", Scalar(db,
            "SELECT COUNT(*) FROM sqlite_master WHERE sql LIKE '%encounter_started_at_ms%'"));
        Assert.Equal("0", Scalar(db, "SELECT COUNT(*) FROM pragma_foreign_key_check"));
        Assert.Equal("sword", Scalar(db, "SELECT name FROM encounter_contents_items"));
    }
}
