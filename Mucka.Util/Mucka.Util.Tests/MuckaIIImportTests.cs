using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Mucka.Combat;
using Mucka.Store;

namespace Mucka.Util.Tests;

/// <summary>
/// Importing finished runs from a MuckaII database. The source is built here with the same tables
/// and view MuckaII creates (<c>run</c>, <c>payload</c>, <c>wire</c>, <c>wire_log</c>), and driven
/// with real C1 bytes so the login is found by the parser rather than by a stub.
/// </summary>
public sealed class MuckaIIImportTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mucka-import-tests", Guid.NewGuid().ToString("N"));

    public MuckaIIImportTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        TestStore.ReleasePools(_directory);
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string DestinationPath => Path.Combine(_directory, MuckaDb.DefaultFileName);
    private string SourcePath => Path.Combine(_directory, "muckaii.db");

    private static readonly byte[] EntersGameMode = [0xAF, 0x9C, 0xFF, 0xFF, 0x9D, 0x9C, 0xFF, 0xFF];
    private static byte[] LeavesGameMode => Encoding.Latin1.GetBytes("\r\nOption (H for help): ");
    private static byte[] Text(string s) => Encoding.Latin1.GetBytes(s);

    private SqliteConnection CreateSource()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = SourcePath, Pooling = false }.ToString());
        connection.Open();
        Exec(connection, """
            CREATE TABLE run (id INTEGER PRIMARY KEY, started_ms INTEGER NOT NULL, ended_ms INTEGER,
                host TEXT NOT NULL, client_version TEXT NOT NULL);
            CREATE TABLE payload (hash BLOB PRIMARY KEY, data BLOB NOT NULL) WITHOUT ROWID;
            CREATE TABLE wire (run INTEGER NOT NULL, seq INTEGER NOT NULL, ts_ms INTEGER NOT NULL,
                direction INTEGER NOT NULL, payload BLOB NOT NULL, PRIMARY KEY (run, seq)) WITHOUT ROWID;
            CREATE VIEW wire_log AS SELECT w.run AS run, w.seq AS seq, w.ts_ms AS ts_ms,
                w.direction AS direction, p.data AS data FROM wire AS w JOIN payload AS p ON p.hash = w.payload;
            """);
        return connection;
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void AddRun(SqliteConnection source, long id, string host, long startedMs, long? endedMs)
    {
        using var command = source.CreateCommand();
        command.CommandText =
            "INSERT INTO run (id, started_ms, ended_ms, host, client_version) VALUES ($i,$s,$e,$h,'0.0.0');";
        command.Parameters.AddWithValue("$i", id);
        command.Parameters.AddWithValue("$s", startedMs);
        command.Parameters.AddWithValue("$e", (object?)endedMs ?? DBNull.Value);
        command.Parameters.AddWithValue("$h", host);
        command.ExecuteNonQuery();
    }

    private static void AddWire(SqliteConnection source, long run, int seq, long ts, int direction, byte[] data)
    {
        var hash = SHA256.HashData(data);
        using var command = source.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO payload (hash, data) VALUES ($h, $d);
            INSERT INTO wire (run, seq, ts_ms, direction, payload) VALUES ($r, $q, $t, $dir, $h);
            """;
        command.Parameters.AddWithValue("$h", hash);
        command.Parameters.AddWithValue("$d", data);
        command.Parameters.AddWithValue("$r", run);
        command.Parameters.AddWithValue("$q", seq);
        command.Parameters.AddWithValue("$t", ts);
        command.Parameters.AddWithValue("$dir", direction);
        command.ExecuteNonQuery();
    }

    /// <summary>A run with one login holding two score lines, closed.</summary>
    private static void AddPlayedRun(SqliteConnection source, long id, string host, long startedMs, long? endedMs)
    {
        AddRun(source, id, host, startedMs, endedMs);
        AddWire(source, id, 0, startedMs + 10, 0, EntersGameMode);
        AddWire(source, id, 1, startedMs + 20, 1, Text("kill rat\r\n"));
        AddWire(source, id, 2, startedMs + 1_000, 0, Text("(Persona saved on +38 = 19,214).\r\n"));
        AddWire(source, id, 3, startedMs + 2_000, 0, Text("(Persona saved on -102 = 19,112).\r\n"));
        AddWire(source, id, 4, startedMs + 3_000, 0, LeavesGameMode);
    }

    private long Count(string sql)
    {
        using var connection = MuckaDb.OpenRead(DestinationPath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    [Fact]
    public void AFinishedRun_ComesAcrossWithItsWireSessionAndScoreLines()
    {
        using (var source = CreateSource())
            AddPlayedRun(source, 7, "mud2.co.uk", 1_000_000, 1_010_000);

        var result = MuckaIIImport.Run(DestinationPath, SourcePath);

        Assert.Equal(1, result.RunsImported);
        Assert.Equal(5, result.WireRows);
        Assert.Equal(1, result.Sessions);
        Assert.Equal(2, result.ScoreEvents);
        Assert.Equal(5, Count("SELECT COUNT(*) FROM wire"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM mucka_runs WHERE client_version = 'MuckaII 0.0.0'"));

        // The instant is the wire record's, not the moment of the import.
        Assert.Equal(1_001_000, Count("SELECT MIN(ts) FROM score_events"));
        Assert.Equal(1_002_000, Count("SELECT MAX(ts) FROM score_events"));
        Assert.Equal(-102, Count("SELECT delta FROM score_events WHERE total = 19112"));
        Assert.Equal(0, Count("SELECT COUNT(*) FROM score_events WHERE persona_session_id IS NULL"));
    }

    [Fact]
    public void ASecondImport_FindsEveryRunAlreadyHere()
    {
        using (var source = CreateSource())
        {
            AddPlayedRun(source, 1, "mud2.co.uk", 1_000_000, 1_010_000);
            AddPlayedRun(source, 2, "mud2.co.uk", 2_000_000, 2_010_000);
        }

        MuckaIIImport.Run(DestinationPath, SourcePath);
        var again = MuckaIIImport.Run(DestinationPath, SourcePath);

        Assert.Equal(0, again.RunsImported);
        Assert.Equal(2, again.RunsAlreadyImported);
        Assert.Equal(2, Count("SELECT COUNT(*) FROM mucka_runs"));
        Assert.Equal(4, Count("SELECT COUNT(*) FROM score_events"));
    }

    [Fact]
    public void ARunWithNoEnd_IsReportedAndLeftAlone()
    {
        using (var source = CreateSource())
        {
            AddPlayedRun(source, 4, "mud2.co.uk", 1_000_000, null);
            AddPlayedRun(source, 5, "mud2.co.uk", 2_000_000, 2_010_000);
        }

        var result = MuckaIIImport.Run(DestinationPath, SourcePath);

        Assert.Equal(1, result.RunsImported);
        Assert.Equal([4L], result.RunsSkippedUnclosed);
        Assert.Equal(1, Count("SELECT COUNT(*) FROM mucka_runs"));
        Assert.Equal(2_000_000, Count("SELECT started_ms FROM mucka_runs"));
    }

    /// <summary>A character is keyed by host, so MuckaII's spelling of the .com server must become
    /// the one this client already uses or the graph shows two servers.</summary>
    [Fact]
    public void TheDotComHost_IsStoredUnderTheSpellingThisClientUses()
    {
        using (var source = CreateSource())
            AddPlayedRun(source, 1, "mud2.com", 1_000_000, 1_010_000);

        MuckaIIImport.Run(DestinationPath, SourcePath);

        Assert.Equal(1, Count("SELECT COUNT(*) FROM mucka_runs WHERE host = 'www.mud2.com'"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM persona_sessions WHERE host = 'www.mud2.com'"));
    }

    private string Scalar(string sql)
    {
        using var connection = MuckaDb.OpenRead(DestinationPath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    /// <summary>Every column of every copied record, not just how many: a swapped seq/ts, a direction
    /// collapsed to 0 or a dropped annotation each change this string.</summary>
    [Fact]
    public void TheWireIsCopiedRecordForRecord()
    {
        using (var source = CreateSource())
        {
            AddRun(source, 1, "mud2.co.uk", 1_000, 9_000);
            AddWire(source, 1, 0, 1_100, 0, EntersGameMode);
            AddWire(source, 1, 1, 1_200, 1, Text("look\r\n"));
            AddWire(source, 1, 2, 1_300, 2, Text("masked: password"));
            AddWire(source, 1, 3, 1_400, 0, LeavesGameMode);
        }

        MuckaIIImport.Run(DestinationPath, SourcePath);

        Assert.Equal(
            "0|1100|0|AF9CFFFF9D9CFFFF;1|1200|1|6C6F6F6B0D0A;2|1300|2|6D61736B65643A2070617373776F7264;3|1400|0|"
            + Convert.ToHexString(LeavesGameMode),
            Scalar("SELECT group_concat(seq || '|' || ts_ms || '|' || direction || '|' || hex(data), ';') "
                + "FROM (SELECT * FROM wire ORDER BY seq)"));
        Assert.Equal(9_000, Count("SELECT ended_ms FROM mucka_runs"));
    }

    /// <summary>Each score line goes to the login it happened in, by that login's own id.</summary>
    [Fact]
    public void ScoreLinesGoToTheirOwnLogin_AndNoneIsRecordedAtTheShell()
    {
        using (var source = CreateSource())
        {
            AddRun(source, 1, "mud2.co.uk", 1_000, 90_000);
            AddWire(source, 1, 0, 1_100, 0, Text("(Persona saved on +1 = 100).\r\n"));      // before any login
            AddWire(source, 1, 1, 2_000, 0, EntersGameMode);
            AddWire(source, 1, 2, 3_000, 0, Text("(Persona saved on +2 = 102).\r\n"));
            AddWire(source, 1, 3, 4_000, 0, LeavesGameMode);
            AddWire(source, 1, 4, 5_000, 0, Text("(Persona saved on +3 = 105).\r\n"));      // at the shell
            AddWire(source, 1, 5, 6_000, 0, EntersGameMode);
            AddWire(source, 1, 6, 7_000, 0, Text("(Persona saved on +4 = 109).\r\n"));      // login still open at the end
        }

        var result = MuckaIIImport.Run(DestinationPath, SourcePath);

        Assert.Equal(2, result.Sessions);
        Assert.Equal(2, result.ScoreEvents);
        Assert.Equal(2, result.ScoreEventsWithoutPersona);
        Assert.Equal(2_000, Count("SELECT ps.started_ms FROM score_events e JOIN persona_sessions ps "
            + "ON ps.id = e.persona_session_id WHERE e.total = 102"));
        Assert.Equal(4_000, Count("SELECT ps.ended_ms FROM score_events e JOIN persona_sessions ps "
            + "ON ps.id = e.persona_session_id WHERE e.total = 102"));
        Assert.Equal(6_000, Count("SELECT ps.started_ms FROM score_events e JOIN persona_sessions ps "
            + "ON ps.id = e.persona_session_id WHERE e.total = 109"));
        Assert.Equal(0, Count("SELECT COUNT(*) FROM score_events WHERE total IN (100, 105)"));
    }

    [Fact]
    public void AnUnclosedRun_IsImportedOnlyWhenNamed_AndEndsAtItsLastRecord()
    {
        using (var source = CreateSource())
            AddPlayedRun(source, 4, "mud2.co.uk", 1_000_000, null);

        var skipped = MuckaIIImport.Run(DestinationPath, SourcePath);
        Assert.Equal([4L], skipped.RunsSkippedUnclosed);
        Assert.Equal(0, Count("SELECT COUNT(*) FROM mucka_runs"));

        var named = MuckaIIImport.Run(DestinationPath, SourcePath, new HashSet<long> { 4 });

        Assert.Equal(1, named.RunsImported);
        Assert.Empty(named.RunsSkippedUnclosed);
        Assert.Equal(1_003_000, Count("SELECT ended_ms FROM mucka_runs"));
        Assert.Equal("MuckaII 0.0.0 (unclosed)", Scalar("SELECT client_version FROM mucka_runs"));
        Assert.Equal(2, Count("SELECT COUNT(*) FROM score_events"));
    }

    /// <summary>A login's window can hold another client's rows (the operator runs several), and the
    /// attribution pass claims by time alone, so imported logins must not take part in it.</summary>
    [Fact]
    public void ImportedLogins_AreNotAttributionWindows()
    {
        using (var source = CreateSource())
            AddPlayedRun(source, 1, "mud2.co.uk", 1_000_000, 1_010_000);
        MuckaIIImport.Run(DestinationPath, SourcePath);
        using (var connection = MuckaDb.Open(DestinationPath))
            Exec(connection, "INSERT INTO swings (ts, dir, npc_group, hit) VALUES (1001500, 'out', 'rats', 1);");

        PersonaSessionBackfill.Run(DestinationPath);

        Assert.Equal(1, Count("SELECT COUNT(*) FROM swings WHERE persona_session_id IS NULL"));
    }

    [Fact]
    public void RowsAlreadyHere_AreNotTouched()
    {
        using (var connection = MuckaDb.Open(DestinationPath))
        {
            Exec(connection, "INSERT INTO mucka_runs (id, started_ms, ended_ms, host) VALUES (1, 1, 2, 'mud2.co.uk');");
            Exec(connection, "INSERT INTO persona_sessions (id, mucka_run_id, persona, host, started_ms, ended_ms) "
                + "VALUES (1, 1, 'Ollie', 'mud2.co.uk', 1, 2);");
            Exec(connection, "INSERT INTO score_events (ts, encounter_open, persona_session_id, total) "
                + "VALUES (1_000_500, 0, 1, 5);".Replace("_", string.Empty));
        }
        using (var source = CreateSource())
            AddPlayedRun(source, 1, "mud2.co.uk", 1_000_000, 1_010_000);

        MuckaIIImport.Run(DestinationPath, SourcePath);

        Assert.Equal(1, Count("SELECT persona_session_id FROM score_events WHERE total = 5"));
        Assert.Equal(3, Count("SELECT COUNT(*) FROM score_events"));
    }
}
