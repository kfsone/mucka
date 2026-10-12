using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Mucka.Store;

namespace Mucka.Util.Tests;

/// <summary>
/// Reading the account a run logged in as out of what the server said back. The records here are the
/// shapes a real run's login has: the prompt, the echo arriving in pieces, and the banner.
/// </summary>
public sealed class AccountBackfillTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mucka-account-backfill-tests", Guid.NewGuid().ToString("N"));

    public AccountBackfillTests() => Directory.CreateDirectory(_directory);

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

    /// <summary>A run with received records in order (and a masked sent one between, as the real log
    /// has). <paramref name="ended"/> false leaves it open.</summary>
    private long Run(bool ended, string? account, params string[] received)
    {
        using var db = MuckaDb.Open(Db);
        Exec(db, "INSERT INTO mucka_runs (uid, started_ms, ended_ms, host, account_id) VALUES ($u, 1, $e, 'h', $a);",
            ("$u", Guid.NewGuid().ToString("N")), ("$e", ended ? 2 : null), ("$a", account));
        using var id = db.CreateCommand();
        id.CommandText = "SELECT last_insert_rowid();";
        var run = Convert.ToInt64(id.ExecuteScalar(), CultureInfo.InvariantCulture);
        for (var i = 0; i < received.Length; i++)
        {
            Exec(db, "INSERT INTO wire (mucka_run_id, seq, ts_ms, direction, data) VALUES ($r,$q,$t,0,$d);",
                ("$r", run), ("$q", i * 2), ("$t", i), ("$d", Encoding.Latin1.GetBytes(received[i])));
            Exec(db, "INSERT INTO wire (mucka_run_id, seq, ts_ms, direction, data) VALUES ($r,$q,$t,1,$d);",
                ("$r", run), ("$q", i * 2 + 1), ("$t", i), ("$d", Encoding.Latin1.GetBytes("*********\r\n")));
        }
        TestStore.ReleasePools(_directory);
        return run;
    }

    private string? Account(long run)
    {
        using var db = MuckaDb.OpenRead(Db);
        using var command = db.CreateCommand();
        command.CommandText = "SELECT account_id FROM mucka_runs WHERE id = $id;";
        command.Parameters.AddWithValue("$id", run);
        return command.ExecuteScalar() is string s ? s : null;
    }

    private const string Prompt = "Type QUIT to abort login.\r\nAccount ID: ";
    private const string Banner = "\r\n\u001b[0;37;40mOK\r\n\u001b[f\u001b[2JP90003673 logged in on pts/13.\r\n****11-MAR-26****\r\n";

    [Fact]
    public void TheBanner_NamesTheAccount_EvenWhenAnEscapeSequenceRunsRightUpToIt()
    {
        var run = Run(true, null, Prompt, "P", "90003673\r\nPassword: ", Banner);

        Assert.Equal(1, AccountBackfill.Run(Db));

        Assert.Equal("P90003673", Account(run));
    }

    [Fact]
    public void TheEcho_IsTheFallback_WhenNoBannerEverCame()
    {
        var run = Run(true, null, Prompt, "p", "90003673\r\nPassword: ", "\r\nWrong password.\r\n");

        Assert.Equal(1, AccountBackfill.Run(Db));

        Assert.Equal("P90003673", Account(run));
    }

    [Fact]
    public void TheBannerSplitAcrossRecords_IsStillRead()
    {
        var run = Run(true, null, Prompt, "x\u001b[2JP9000", "3673 logg", "ed in on pts/13.\r\n");

        Assert.Equal(1, AccountBackfill.Run(Db));

        Assert.Equal("P90003673", Account(run));
    }

    [Fact]
    public void AFinishedRunThatNamesNoAccount_IsMarkedAndNotReadAgain()
    {
        var run = Run(true, null, "nothing of the kind here\r\n");

        Assert.Equal(0, AccountBackfill.Run(Db));

        Assert.Equal(string.Empty, Account(run));
    }

    [Fact]
    public void AnUnfinishedRunThatNamesNoAccount_IsLeftToBeReadAgain_BecauseItMayBeMidLogin()
    {
        var run = Run(false, null, Prompt);

        Assert.Equal(0, AccountBackfill.Run(Db));
        Assert.Null(Account(run));

        // Its login completes later; the next start-up finds it.
        using (var db = MuckaDb.Open(Db))
            Exec(db, "INSERT INTO wire (mucka_run_id, seq, ts_ms, direction, data) VALUES ($r, 10, 9, 0, $d);",
                ("$r", run), ("$d", Encoding.Latin1.GetBytes("P90003673 logged in on pts/1.\r\n")));
        TestStore.ReleasePools(_directory);

        Assert.Equal(1, AccountBackfill.Run(Db));
        Assert.Equal("P90003673", Account(run));
    }

    [Fact]
    public void AnUnfinishedRunWhoseLoginIsInTheLog_IsFilled_SoTheNextLoginCanEndIt()
    {
        var run = Run(false, null, Prompt, "P", "90003673\r\nPassword: ", Banner);

        Assert.Equal(1, AccountBackfill.Run(Db));

        Assert.Equal("P90003673", Account(run));
    }

    [Fact]
    public void TheLiveRun_AndRunsThatAlreadyHaveAnAccount_AreNotTouched()
    {
        var live = Run(false, null, Prompt, "P", "90003673\r\nPassword: ", Banner);
        var known = Run(true, "P11111111", Prompt, "P", "90003673\r\nPassword: ", Banner);

        Assert.Equal(0, AccountBackfill.Run(Db, liveRunId: live));

        Assert.Null(Account(live));
        Assert.Equal("P11111111", Account(known));
    }

    [Fact]
    public void ARunsLaterTextThatMerelyLooksLikeABanner_IsNotRead()
    {
        // Past the login records the game is talking; nothing it says there is the server's banner.
        var chatter = Enumerable.Repeat("The rat squeaks.\r\n", AccountBackfill.LoginRecords + 5).ToList();
        chatter.Add("X99 logged in on pts/9.\r\n");
        var run = Run(true, null, [.. chatter]);

        Assert.Equal(0, AccountBackfill.Run(Db));

        Assert.Equal(string.Empty, Account(run));
    }

    [Fact]
    public void Idempotent_ASecondPassFindsNothingToDo()
    {
        Run(true, null, Prompt, "P", "90003673\r\nPassword: ", Banner);

        Assert.Equal(1, AccountBackfill.Run(Db));
        Assert.Equal(0, AccountBackfill.Run(Db));
    }

    [Fact]
    public void ARecoveredAccount_IsWhatTheNextLoginOfThatAccountEnds()
    {
        // The two halves together: an old run, recovered from its wire, is ended by a later login.
        var old = Run(false, null, Prompt, "P", "90003673\r\nPassword: ", Banner);
        AccountBackfill.Run(Db);

        var store = TestStore.Open(Db, "h", accountId: "p90003673");
        int closed;
        try { closed = store.CloseAbandonedRuns(); }
        finally { TestStore.Close(store); TestStore.ReleasePools(_directory); }

        Assert.Equal(1, closed);
        using var db = MuckaDb.OpenRead(Db);
        using var command = db.CreateCommand();
        command.CommandText = "SELECT ended_ms IS NOT NULL FROM mucka_runs WHERE id = $id;";
        command.Parameters.AddWithValue("$id", old);
        Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture));
    }
}
