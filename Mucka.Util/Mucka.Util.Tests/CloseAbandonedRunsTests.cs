using System.Globalization;
using Microsoft.Data.Sqlite;
using Mucka.Store;

namespace Mucka.Util.Tests;

/// <summary>
/// A login reaching game mode ends the earlier runs of its own account that never ended, at their own
/// last wire record, and touches nothing else. The scenarios are the ones that decide it: which run
/// died, which only looks old, and which belongs to someone else.
///
/// <para>Each earlier run is written straight into the database, as a killed client leaves it:
/// started, never ended, with whatever it had recorded. The run that reaches game mode is a real
/// <see cref="MuckaStore"/> and is asked to close the others.</para>
/// </summary>
public sealed class CloseAbandonedRunsTests : IDisposable
{
    private const string Host = "www.mud2.com";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mucka-close-abandoned-tests", Guid.NewGuid().ToString("N"));

    public CloseAbandonedRunsTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        TestStore.ReleasePools(_directory);
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string Db => Path.Combine(_directory, MuckaDb.DefaultFileName);

    private static void Exec(SqliteConnection connection, string sql, params (string, object?)[] args)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in args)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    /// <summary>An earlier run that was killed: open, with a login, and wire records at the given
    /// times. Returns its id.</summary>
    private long DeadRun(string uid, string? account, string host, long startedMs, params long[] wireAt)
    {
        using var db = MuckaDb.Open(Db);
        Exec(db, "INSERT INTO mucka_runs (uid, started_ms, host, account_id) VALUES ($u,$s,$h,$a);",
            ("$u", uid), ("$s", startedMs), ("$h", host), ("$a", account));
        var run = Scalar(db, "SELECT last_insert_rowid();");
        for (var i = 0; i < wireAt.Length; i++)
            Exec(db, "INSERT INTO wire (mucka_run_id, seq, ts_ms, direction, data) VALUES ($r,$q,$t,0,x'41');",
                ("$r", run), ("$q", i), ("$t", wireAt[i]));
        Exec(db, "INSERT INTO persona_sessions (mucka_run_id, persona, host, started_ms) VALUES ($r,'p',$h,$s);",
            ("$r", run), ("$h", host), ("$s", startedMs));
        TestStore.ReleasePools(_directory);
        return run;
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private (long? RunEnd, long? SessionEnd) Ends(long run)
    {
        using var db = MuckaDb.OpenRead(Db);
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT r.ended_ms, (SELECT s.ended_ms FROM persona_sessions s WHERE s.mucka_run_id = r.id)
            FROM mucka_runs r WHERE r.id = $id;
            """;
        command.Parameters.AddWithValue("$id", run);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return (reader.IsDBNull(0) ? null : reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetInt64(1));
    }

    private int NewLogin(string? account, string host = Host)
    {
        var store = TestStore.Open(Db, host, accountId: account);
        try { return store.CloseAbandonedRuns(); }
        finally { TestStore.Close(store); TestStore.ReleasePools(_directory); }
    }

    [Fact]
    public void ScenarioA_ARunKilledAfterItsLastRecord_IsPinnedToThatRecord()
    {
        // M1 logs in at 0, plays to 20 and is killed at 40; M2 then logs in as the same account.
        var m1 = DeadRun("m1", "A", Host, 0, 0, 10, 20);

        Assert.Equal(1, NewLogin("A"));

        Assert.Equal((20L, 20L), Ends(m1));
    }

    [Fact]
    public void ScenarioB_AnotherAccountsOpenRun_IsLeftAlone()
    {
        // M1 (A) was killed; M2 (B) is still playing; M3 (A) logs in. Only M1 is ended.
        var m1 = DeadRun("m1", "A", Host, 0, 0, 10, 20);
        var m2 = DeadRun("m2", "B", Host, 30, 30, 35);

        Assert.Equal(1, NewLogin("A"));

        Assert.Equal((20L, 20L), Ends(m1));
        Assert.Equal((null, null), Ends(m2));
    }

    [Fact]
    public void ScenarioC_EachRunIsPinnedToItsOwnLastRecord_NotToTheLastRecordOfAnyRun()
    {
        // M1 (B) kept going to 30 while M2 (A) was killed after 25. M3 (A) logs in: M2 ends at 25,
        // and M1 - still B's, still writing - is untouched.
        var m1 = DeadRun("m1", "B", Host, 0, 0, 10, 20, 30);
        var m2 = DeadRun("m2", "A", Host, 15, 15, 25);

        Assert.Equal(1, NewLogin("A"));

        Assert.Equal((25L, 25L), Ends(m2));
        Assert.Equal((null, null), Ends(m1));
    }

    [Fact]
    public void ScenarioD_TwoAccountsKilledTogether_EachIsEndedByItsOwnNextLogin()
    {
        var m1 = DeadRun("m1", "A", Host, 0, 0, 10);
        var m2 = DeadRun("m2", "B", Host, 0, 0, 10, 15);

        Assert.Equal(1, NewLogin("A"));
        Assert.Equal((10L, 10L), Ends(m1));
        Assert.Equal((null, null), Ends(m2));

        Assert.Equal(1, NewLogin("B"));
        Assert.Equal((15L, 15L), Ends(m2));
    }

    [Fact]
    public void ScenarioE_RunsStartingAMicrosecondApart_AreToldApartByAccountNotByTime()
    {
        // Two accounts whose runs begin and write within the same millisecond: nothing about the
        // timestamps can separate them, and nothing needs to.
        var a = DeadRun("a", "A", Host, 1_000, 1_000, 1_010);
        var b = DeadRun("b", "B", Host, 1_000, 1_000, 1_010);

        Assert.Equal(1, NewLogin("B"));

        Assert.Equal((null, null), Ends(a));
        Assert.Equal((1_010L, 1_010L), Ends(b));
    }

    [Fact]
    public void TheSameAccountOnAnotherHost_IsAnotherAccount()
    {
        var other = DeadRun("m1", "A", "mud2.co.uk", 0, 0, 10);

        Assert.Equal(0, NewLogin("A"));

        Assert.Equal((null, null), Ends(other));
    }

    [Fact]
    public void ALoginTypedByHand_HasNoAccountAndEndsNothing()
    {
        var dead = DeadRun("m1", "A", Host, 0, 0, 10);

        Assert.Equal(0, NewLogin(null));

        Assert.Equal((null, null), Ends(dead));
    }

    [Fact]
    public void ARunWithNoAccountRecorded_IsNeverEnded()
    {
        var older = DeadRun("m1", null, Host, 0, 0, 10);

        Assert.Equal(0, NewLogin("A"));

        Assert.Equal((null, null), Ends(older));
    }

    [Fact]
    public void ARunThatEndedNormally_KeepsItsEnd_AndARunWithNoWireEndsAtItsStart()
    {
        var closed = DeadRun("m1", "A", Host, 0, 0, 10);
        using (var db = MuckaDb.Open(Db))
            Exec(db, "UPDATE mucka_runs SET ended_ms = 99 WHERE id = $id;", ("$id", closed));
        var silent = DeadRun("m2", "A", Host, 500);
        TestStore.ReleasePools(_directory);

        Assert.Equal(1, NewLogin("A"));

        Assert.Equal(99L, Ends(closed).RunEnd);
        Assert.Equal((500L, 500L), Ends(silent));
    }
}
