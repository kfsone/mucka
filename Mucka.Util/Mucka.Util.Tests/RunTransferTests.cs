using System.Globalization;
using Microsoft.Data.Sqlite;
using Mucka.Combat;
using Mucka.Store;

namespace Mucka.Util.Tests;

/// <summary>
/// <c>$EXPORT</c> and <c>$IMPORT</c>. Every database here is built by <see cref="MuckaDb.Open"/> and
/// filled with plain INSERTs, so the shape under test is the real one.
/// </summary>
public sealed class RunTransferTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mucka-transfer-tests", Guid.NewGuid().ToString("N"));

    public RunTransferTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        TestStore.ReleasePools(_directory);
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string PathOf(string name) => Path.Combine(_directory, name);

    private static void Exec(SqliteConnection connection, string sql, params (string, object?)[] args)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in args)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private long Count(string database, string sql)
    {
        using var connection = MuckaDb.OpenRead(PathOf(database));
        return Scalar(connection, sql);
    }

    /// <summary>One run holding one login that has every kind of row the transfer carries, with ids
    /// pushed off 1 by <paramref name="idOffset"/> junk rows so a copy that kept source ids would
    /// point at the wrong parent.</summary>
    private void AddRun(string database, string host, long startedMs, long? endedMs, int idOffset = 0,
        string version = "0.9.9", int logins = 1, string? runUid = null)
    {
        using var db = MuckaDb.Open(PathOf(database));
        for (var i = 0; i < idOffset; i++)
        {
            Exec(db, "INSERT INTO mucka_runs (uid, started_ms, ended_ms, host) VALUES ($u, 1, 2, 'junk');",
                ("$u", $"junk-{database}-{i}"));
            Exec(db, "INSERT INTO persona_sessions (mucka_run_id, started_ms) VALUES (last_insert_rowid(), 1);");
            Exec(db, "INSERT INTO encounter_contents (encounter_uid, ts, carried_count) VALUES ($e, 1, 0);",
                ("$e", $"junk-enc-{database}-{i}"));
        }

        Exec(db, "INSERT INTO mucka_runs (uid, started_ms, ended_ms, host, client_version) VALUES ($u,$s,$e,$h,$v);",
            ("$u", runUid ?? $"run-{startedMs}"), ("$s", startedMs), ("$e", endedMs), ("$h", host), ("$v", version));
        var run = Scalar(db, "SELECT last_insert_rowid();");

        for (var seq = 0; seq < 3; seq++)
            Exec(db, "INSERT INTO wire (mucka_run_id, seq, ts_ms, direction, data) VALUES ($r,$q,$t,0,$d);",
                ("$r", run), ("$q", seq), ("$t", startedMs + seq), ("$d", new byte[] { 0xFF, (byte)seq, 0 }));

        // Every login of a run has an encounter that began at the SAME instant, as two clients (or one
        // client's two logins) can. Their uids are what tells them apart.
        for (var login = 0; login < logins; login++)
        {
            Exec(db, "INSERT INTO persona_sessions (mucka_run_id, persona, host, started_ms, ended_ms, ended_note) "
                + "VALUES ($r,'Ollie',$h,$s,$e,'quit');", ("$r", run), ("$h", host), ("$s", startedMs), ("$e", endedMs));
            var session = Scalar(db, "SELECT last_insert_rowid();");

            var key = startedMs + 5;
            var uid = $"enc-{runUid ?? $"run-{startedMs}"}-{login}";
            Exec(db, "INSERT INTO swings (ts, dir, encounter_uid, hit, persona_session_id) VALUES ($t,'out',$k,1,$p);",
                ("$t", key), ("$k", uid), ("$p", session));
            Exec(db, "INSERT INTO score_events (ts, encounter_open, total, delta, persona_session_id) VALUES ($t,0,19214,38,$p);",
                ("$t", key), ("$p", session));
            Exec(db, "INSERT INTO npc_stamina_reads (ts, npc, npc_group, pool_key, printed_low, printed_high, persona_session_id) "
                + "VALUES ($t,'rat0','rat','rat',90,99,$p);", ("$t", key), ("$p", session));
            Exec(db, "INSERT INTO encounters (uid, started_at_ms, persona_session_id, room) VALUES ($k,$t,$p,'cellar');",
                ("$k", uid), ("$t", key), ("$p", session));
            Exec(db, "INSERT INTO encounter_events (encounter_uid, ts, kind) VALUES ($k,$t,'Hit');",
                ("$k", uid), ("$t", key));
            Exec(db, "INSERT INTO encounter_lines (encounter_uid, phase, ord, text) VALUES ($k,'tail',0,'x');",
                ("$k", uid));
            Exec(db, "INSERT INTO encounter_contents (encounter_uid, ts, carried_count) VALUES ($k,$t,1);",
                ("$k", uid), ("$t", key));
            Exec(db, "INSERT INTO encounter_contents_items (contents_id, ord, name, is_creature, is_carried) "
                + "VALUES (last_insert_rowid(),0,'sword',0,1);");
        }
    }

    private static string Row(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "(null)";
    }

    [Fact]
    public void AFinishedRun_ArrivesWithItsWholeTree_UnderTheDestinationsOwnIds()
    {
        AddRun("a.db", "www.mud2.com", 1_000_000, 1_100_000);
        AddRun("b.db", "www.mud2.com", 9_000_000, 9_100_000, idOffset: 4);
        RunTransfer.Export(PathOf("a.db"), PathOf("a.export"));

        var result = RunTransfer.Import(PathOf("b.db"), PathOf("a.export"));

        Assert.Equal(1, result.RunsImported);
        Assert.Equal(3, result.WireRows);
        Assert.Equal(1, result.Sessions);
        Assert.Equal(4, result.FactRows);   // swing, score, stamina read, encounter
        Assert.Equal(2, Count("b.db", "SELECT COUNT(*) FROM persona_sessions WHERE persona = 'Ollie'"));

        // The imported login points at the imported run, and every fact at that login - not at
        // whatever held the source's id here.
        using var b = MuckaDb.OpenRead(PathOf("b.db"));
        var run = Scalar(b, "SELECT id FROM mucka_runs WHERE started_ms = 1000000");
        var session = Scalar(b, $"SELECT id FROM persona_sessions WHERE mucka_run_id = {run}");
        Assert.Equal(3, Scalar(b, $"SELECT COUNT(*) FROM wire WHERE mucka_run_id = {run}"));
        foreach (var table in new[] { "swings", "score_events", "npc_stamina_reads", "encounters" })
            Assert.Equal(1, Scalar(b, $"SELECT COUNT(*) FROM {table} WHERE persona_session_id = {session}"));

        // The contents list's item follows the contents row's NEW id.
        Assert.Equal("sword", Row(b,
            "SELECT i.name FROM encounter_contents_items i JOIN encounter_contents c ON c.id = i.contents_id "
            + "WHERE c.encounter_uid = 'enc-run-1000000-0'"));
        Assert.Equal(1, Scalar(b, "SELECT COUNT(*) FROM encounter_events WHERE encounter_uid = 'enc-run-1000000-0'"));
        Assert.Equal(1, Scalar(b, "SELECT COUNT(*) FROM encounter_lines WHERE encounter_uid = 'enc-run-1000000-0'"));

        // Wire bytes, order and the session's note survive verbatim.
        Assert.Equal("FF0100", Row(b, $"SELECT hex(data) FROM wire WHERE mucka_run_id = {run} AND seq = 1"));
        Assert.Equal("quit", Row(b, $"SELECT ended_note FROM persona_sessions WHERE id = {session}"));
        Assert.Equal(0, Scalar(b, "SELECT COUNT(*) FROM wire w LEFT JOIN mucka_runs r ON r.id = w.mucka_run_id WHERE r.id IS NULL"));
    }

    [Fact]
    public void TwoLoginsWhoseEncountersBeganInTheSameMillisecond_EachKeepTheirOwnChildren()
    {
        // The case the old millisecond key could not express: both logins' encounters carry the same
        // start instant, and every child used to be copied once per login, doubling it.
        AddRun("a.db", "www.mud2.com", 1_000_000, 1_100_000, logins: 2);
        RunTransfer.Export(PathOf("a.db"), PathOf("a.export"));

        RunTransfer.Import(PathOf("b.db"), PathOf("a.export"));

        Assert.Equal(2, Count("b.db", "SELECT COUNT(*) FROM encounters"));
        Assert.Equal(2, Count("b.db", "SELECT COUNT(*) FROM encounters WHERE started_at_ms = 1000005"));
        Assert.Equal(2, Count("b.db", "SELECT COUNT(*) FROM encounter_lines"));
        Assert.Equal(2, Count("b.db", "SELECT COUNT(*) FROM encounter_events"));
        Assert.Equal(2, Count("b.db", "SELECT COUNT(*) FROM encounter_contents"));
        Assert.Equal(2, Count("b.db", "SELECT COUNT(*) FROM encounter_contents_items"));
        Assert.Equal(1, Count("b.db", "SELECT COUNT(*) FROM encounter_lines WHERE encounter_uid = 'enc-run-1000000-0'"));
        Assert.Equal(1, Count("b.db", "SELECT COUNT(*) FROM encounter_lines WHERE encounter_uid = 'enc-run-1000000-1'"));
    }

    [Fact]
    public void ARunWithAnotherUidButTheSameStartHostAndRecordCount_IsLeftOutAndReported()
    {
        // The same history given its uid twice: what a file older than the uid migration looks like,
        // since it is migrated afresh - and so numbered afresh - every time it is read.
        AddRun("a.db", "www.mud2.com", 1_000_000, 1_100_000, runUid: "first-uid");
        AddRun("b.db", "www.mud2.com", 1_000_000, 1_100_000, runUid: "second-uid");
        RunTransfer.Export(PathOf("a.db"), PathOf("a.export"));

        var result = RunTransfer.Import(PathOf("b.db"), PathOf("a.export"));

        Assert.Equal(0, result.RunsImported);
        Assert.Equal(1, result.RunsProbablyHere);
        Assert.Equal(1, Count("b.db", "SELECT COUNT(*) FROM mucka_runs"));
    }

    [Fact]
    public void ARunThatOnlySharesAStartAndHost_IsStillImported()
    {
        // Two clients can start in the same millisecond. Without the same record count that is two
        // different runs, and neither is dropped.
        AddRun("a.db", "www.mud2.com", 1_000_000, 1_100_000, runUid: "first-uid");
        AddRun("b.db", "www.mud2.com", 1_000_000, 1_100_000, runUid: "second-uid");
        using (var b = MuckaDb.Open(PathOf("b.db")))
            Exec(b, "INSERT INTO wire (mucka_run_id, seq, ts_ms, direction, data) "
                + "VALUES ((SELECT id FROM mucka_runs), 3, 1000003, 0, x'00');");
        RunTransfer.Export(PathOf("a.db"), PathOf("a.export"));

        var result = RunTransfer.Import(PathOf("b.db"), PathOf("a.export"));

        Assert.Equal(1, result.RunsImported);
        Assert.Equal(2, Count("b.db", "SELECT COUNT(*) FROM mucka_runs"));
    }

    [Fact]
    public void ImportingTheSameFileAgain_AddsNothing()
    {
        AddRun("a.db", "www.mud2.com", 1_000_000, 1_100_000);
        RunTransfer.Export(PathOf("a.db"), PathOf("a.export"));
        RunTransfer.Import(PathOf("b.db"), PathOf("a.export"));
        var before = Count("b.db", "SELECT (SELECT COUNT(*) FROM wire) + (SELECT COUNT(*) FROM swings) + (SELECT COUNT(*) FROM mucka_runs)");

        var again = RunTransfer.Import(PathOf("b.db"), PathOf("a.export"));

        Assert.Equal(0, again.RunsImported);
        Assert.Equal(1, again.RunsAlreadyHere);
        Assert.Equal(before, Count("b.db", "SELECT (SELECT COUNT(*) FROM wire) + (SELECT COUNT(*) FROM swings) + (SELECT COUNT(*) FROM mucka_runs)"));
    }

    [Fact]
    public void ARoundTrip_AtoB_ThenBackToA_AddsNothingToA()
    {
        AddRun("a.db", "www.mud2.com", 1_000_000, 1_100_000);
        AddRun("b.db", "www.mud2.com", 9_000_000, 9_100_000);
        RunTransfer.Export(PathOf("a.db"), PathOf("a.export"));
        RunTransfer.Import(PathOf("b.db"), PathOf("a.export"));
        RunTransfer.Export(PathOf("b.db"), PathOf("b.export"));
        var runsInA = Count("a.db", "SELECT COUNT(*) FROM mucka_runs");

        var back = RunTransfer.Import(PathOf("a.db"), PathOf("b.export"));

        // B's own run comes to A; A's own, which B carries marked as imported, is recognised.
        Assert.Equal(1, back.RunsImported);
        Assert.Equal(1, back.RunsAlreadyHere);
        Assert.Equal(runsInA + 1, Count("a.db", "SELECT COUNT(*) FROM mucka_runs"));
        Assert.Equal(1, Count("a.db", "SELECT COUNT(*) FROM mucka_runs WHERE started_ms = 1000000"));
        // A's own copy is not given the mark by the round trip.
        Assert.Equal(0, Count("a.db", $"SELECT COUNT(*) FROM mucka_runs WHERE started_ms = 1000000 AND client_version LIKE '%{RunTransfer.ImportedMark}'"));
    }

    [Fact]
    public void ARunWithNoEnd_IsLeftOutAndReported_BecauseACopyWouldFreezeHalfOfIt()
    {
        AddRun("a.db", "www.mud2.com", 1_000_000, endedMs: null);
        AddRun("a.db", "www.mud2.com", 2_000_000, 2_100_000);
        RunTransfer.Export(PathOf("a.db"), PathOf("a.export"));

        var result = RunTransfer.Import(PathOf("b.db"), PathOf("a.export"));

        Assert.Equal(1, result.RunsImported);
        Assert.Single(result.RunsSkippedUnclosed);
        Assert.Equal(0, Count("b.db", "SELECT COUNT(*) FROM mucka_runs WHERE started_ms = 1000000"));
        Assert.Equal(0, Count("b.db", "SELECT COUNT(*) FROM wire WHERE ts_ms < 2000000"));
    }

    [Fact]
    public void AnImportedRun_IsMarked_AndItsLoginsDoNotBecomeWindowsOverTheDestinationsOwnRows()
    {
        AddRun("a.db", "www.mud2.com", 1_000_000, 1_100_000, version: "1.0");
        RunTransfer.Export(PathOf("a.db"), PathOf("a.export"));
        AddRun("b.db", "www.mud2.com", 5_000_000, 5_100_000);   // opens the destination's schema
        using (var b = MuckaDb.Open(PathOf("b.db")))
            // A row of the destination's own, inside the window the imported login spans, with no login.
            Exec(b, "INSERT INTO swings (ts, dir, hit) VALUES (1050000, 'in', 1);");

        RunTransfer.Import(PathOf("b.db"), PathOf("a.export"));
        PersonaSessionBackfill.Run(PathOf("b.db"));

        Assert.Equal(1, Count("b.db", $"SELECT COUNT(*) FROM mucka_runs WHERE client_version = '1.0{RunTransfer.ImportedMark}'"));
        Assert.Equal(1, Count("b.db", "SELECT COUNT(*) FROM swings WHERE ts = 1050000 AND persona_session_id IS NULL"));
    }

    [Fact]
    public void RowsWithNoLogin_AreLeftBehindAndCounted()
    {
        AddRun("a.db", "www.mud2.com", 1_000_000, 1_100_000);
        using (var a = MuckaDb.Open(PathOf("a.db")))
            Exec(a, "INSERT INTO swings (ts, dir, hit) VALUES (7, 'in', 1);");
        RunTransfer.Export(PathOf("a.db"), PathOf("a.export"));

        var result = RunTransfer.Import(PathOf("b.db"), PathOf("a.export"));

        Assert.Equal(1, result.UnattributedRowsLeftBehind);
        Assert.Equal(0, Count("b.db", "SELECT COUNT(*) FROM swings WHERE ts = 7"));
    }

    [Fact]
    public void SomeKinds_TheLaterReadingWins_InEitherDirection()
    {
        AddRun("a.db", "www.mud2.com", 1_000_000, 1_100_000);
        using (var a = MuckaDb.Open(PathOf("a.db")))
        {
            Exec(a, "INSERT INTO some_kinds VALUES ('rat','something',200);");
            Exec(a, "INSERT INTO some_kinds VALUES ('thief','someone',100);");
        }
        using (var b = MuckaDb.Open(PathOf("b.db")))
        {
            Exec(b, "INSERT INTO some_kinds VALUES ('rat','someone',100);");
            Exec(b, "INSERT INTO some_kinds VALUES ('thief','something',300);");
        }
        RunTransfer.Export(PathOf("a.db"), PathOf("a.export"));

        RunTransfer.Import(PathOf("b.db"), PathOf("a.export"));

        using var read = MuckaDb.OpenRead(PathOf("b.db"));
        Assert.Equal("something", Row(read, "SELECT kind FROM some_kinds WHERE species = 'rat'"));
        Assert.Equal("something", Row(read, "SELECT kind FROM some_kinds WHERE species = 'thief'"));
    }

    [Fact]
    public void TheExportFile_IsNotModified_AndAnOlderOneIsMigratedOnACopy()
    {
        AddRun("a.db", "www.mud2.com", 1_000_000, 1_100_000);
        RunTransfer.Export(PathOf("a.db"), PathOf("a.export"));
        // Make it look like a release that predates the last script.
        using (var export = new SqliteConnection(new SqliteConnectionStringBuilder
                   { DataSource = PathOf("a.export"), Pooling = false }.ToString()))
        {
            export.Open();
            Exec(export, "DELETE FROM SchemaVersions WHERE ScriptName = '0005_some_kinds.sql';");
            Exec(export, "DROP TABLE some_kinds;");
        }
        var before = File.ReadAllBytes(PathOf("a.export"));

        var result = RunTransfer.Import(PathOf("b.db"), PathOf("a.export"));

        Assert.Equal(1, result.RunsImported);
        Assert.Equal(before, File.ReadAllBytes(PathOf("a.export")));
        Assert.Empty(Directory.GetFiles(_directory, "a.export-*"));
    }

    [Fact]
    public void AFileFromANewerRelease_IsRefusedAndNothingIsImported()
    {
        AddRun("a.db", "www.mud2.com", 1_000_000, 1_100_000);
        RunTransfer.Export(PathOf("a.db"), PathOf("a.export"));
        using (var export = new SqliteConnection(new SqliteConnectionStringBuilder
                   { DataSource = PathOf("a.export"), Pooling = false }.ToString()))
        {
            export.Open();
            Exec(export, "INSERT INTO SchemaVersions (ScriptName, Applied) VALUES ('0099_from_the_future.sql', 'now');");
        }

        var ex = Assert.Throws<InvalidOperationException>(() => RunTransfer.Import(PathOf("b.db"), PathOf("a.export")));

        Assert.Contains("0099_from_the_future.sql", ex.Message);
        Assert.False(File.Exists(PathOf("b.db")));
    }

    [Fact]
    public void AnythingThatIsNotAMuckaDatabase_IsRefusedUntouched()
    {
        using (var other = new SqliteConnection(new SqliteConnectionStringBuilder
                   { DataSource = PathOf("other.db"), Pooling = false }.ToString()))
        {
            other.Open();
            Exec(other, "SELECT 1;");
        }
        File.WriteAllText(PathOf("text.db"), "not sqlite");

        Assert.Throws<InvalidOperationException>(() => RunTransfer.Import(PathOf("b.db"), PathOf("other.db")));
        Assert.ThrowsAny<SqliteException>(() => RunTransfer.Import(PathOf("b.db"), PathOf("text.db")));
        Assert.False(File.Exists(PathOf("b.db")));
    }

    [Fact]
    public void Export_RefusesToOverwrite_AndLeavesASelfContainedFile()
    {
        AddRun("a.db", "www.mud2.com", 1_000_000, 1_100_000);
        RunTransfer.Export(PathOf("a.db"), PathOf("a.export"));

        Assert.Throws<IOException>(() => RunTransfer.Export(PathOf("a.db"), PathOf("a.export")));

        // Moving the one file is the whole transfer: no WAL beside it holds rows the file lacks.
        Assert.Empty(Directory.GetFiles(_directory, "a.export-*"));
        File.Copy(PathOf("a.export"), PathOf("moved.db"));
        Assert.Equal(3, Count("moved.db", "SELECT COUNT(*) FROM wire"));
    }

    [Fact]
    public void Export_TakesASnapshotWhileTheStoreIsWriting()
    {
        AddRun("a.db", "www.mud2.com", 1_000_000, 1_100_000);
        var live = TestStore.Open(PathOf("a.db"));
        try
        {
            RunTransfer.Export(PathOf("a.db"), PathOf("a.export"));
        }
        finally { TestStore.Close(live); }

        // The store's own run is open in the snapshot, and the finished one is whole.
        Assert.Equal(3, Count("a.export", "SELECT COUNT(*) FROM wire WHERE ts_ms < 2000000"));
        Assert.Equal(1, Count("a.export", "SELECT COUNT(*) FROM mucka_runs WHERE ended_ms IS NULL"));
    }
}
