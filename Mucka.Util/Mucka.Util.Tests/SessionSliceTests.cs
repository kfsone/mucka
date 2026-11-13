using System.Text;
using Microsoft.Data.Sqlite;
using Mucka.Store;
using Mucka.WireLog;

namespace Mucka.Util.Tests;

/// <summary>A Session's recording as a slice of its run, and the replay History makes from it.</summary>
public sealed class SessionSliceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mucka-session-slice-tests", Guid.NewGuid().ToString("N"));

    public SessionSliceTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        TestStore.ReleasePools(_directory);
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string Db => Path.Combine(_directory, "mucka.db");

    private static WireRecord Rx(long ms) => new(ms, WireDirection.Rx, [0x41]);

    // ------------------------------------------------------------------- the rule ----

    private static readonly (long, long, long?)[] ThreeLogins =
        [(1, 100, 200), (2, 300, 400), (3, 500, 600)];

    [Fact]
    public void TheFirstSession_StartsAtTheRunsStart_AndEndsAtItsOwnEnd()
        => Assert.Equal(((long?)null, (long?)200), SessionSlice.Bounds(ThreeLogins, 1));

    [Fact]
    public void AMiddleSession_RunsFromThePreviousEnd_ToItsOwn()
        => Assert.Equal(((long?)200, (long?)400), SessionSlice.Bounds(ThreeLogins, 2));

    [Fact]
    public void TheLastSession_TakesTheLogoutTail_ToTheRunsEnd()
        => Assert.Equal(((long?)400, (long?)null), SessionSlice.Bounds(ThreeLogins, 3));

    [Fact]
    public void ASessionWithNoEnd_EndsWhereTheNextStarts()
        => Assert.Equal(((long?)null, (long?)300), SessionSlice.Bounds([(1, 100, null), (2, 300, 400)], 1));

    [Fact]
    public void ABoundaryEarlierThanTheOneBefore_IsHeldAtIt()
        => Assert.Equal(((long?)400, (long?)400), SessionSlice.Bounds([(1, 100, 400), (2, 300, 350), (3, 500, 600)], 2));

    [Fact]
    public void TheSlices_PartitionTheRun_EvenWhereAStampStepsBack()
    {
        WireRecord[] run = [Rx(50), Rx(150), Rx(250), Rx(190), Rx(350), Rx(450), Rx(650)];
        var covered = new List<int>();
        foreach (var (id, _, _) in ThreeLogins)
        {
            var (after, through) = SessionSlice.Bounds(ThreeLogins, id);
            var (first, last) = SessionSlice.Range(run, after, through);
            for (var i = first; i <= last; i++)
                covered.Add(i);
        }
        Assert.Equal(Enumerable.Range(0, run.Length), covered);
    }

    // ------------------------------------------------------------------- the replay ----

    private static long Insert(SqliteConnection connection, string sql, params (string, object?)[] args)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql + " SELECT last_insert_rowid();";
        foreach (var (name, value) in args)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private const string AccountId = "P90003673";
    private const string Password = "hunter22";

    /// <summary>One run, two logins: the login exchange is in the first Session's slice, and the
    /// account-logout notice (FE code 95 03) and the logout line in the second's.</summary>
    private (long First, long Second) SeedTwoLogins()
    {
        using var db = MuckaDb.Open(Db);
        var run = Insert(db, "INSERT INTO mucka_runs (uid, started_ms, ended_ms, host) VALUES ('r', 0, 900, 'mud2.com');");
        var seq = 0;
        void Wire(long ms, WireDirection direction, string text)
            => Insert(db, "INSERT INTO wire (mucka_run_id, seq, ts_ms, direction, data) VALUES ($r, $q, $t, $d, $b);",
                ("$r", run), ("$q", seq++), ("$t", ms), ("$d", (int)direction), ("$b", Encoding.Latin1.GetBytes(text)));

        Wire(10, WireDirection.Rx, "Type QUIT to abort login.\r\nAccount ID: ");
        Wire(11, WireDirection.Tx, AccountId + "\r\n");
        Wire(12, WireDirection.Rx, AccountId + "\r\nPassword: ");
        Wire(13, WireDirection.Tx, Password + "\r\n");
        Wire(150, WireDirection.Rx, "\r\nOK\r\nfirst session play\r\n");
        Wire(450, WireDirection.Rx, "second session play\r\n");
        Wire(800, WireDirection.Rx, $"Option (H for help): q\r\n{AccountId} logged out.\r\n\u00fa\u009e\u00ff\u00ff{AccountId}\r\n\u00ff\u00ffgoodbye\r\n");

        var first = Insert(db, "INSERT INTO persona_sessions (mucka_run_id, persona, host, started_ms, ended_ms) VALUES ($r, 'Ollie', 'mud2.com', 100, 300);",
            ("$r", run));
        var second = Insert(db, "INSERT INTO persona_sessions (mucka_run_id, persona, host, started_ms, ended_ms) VALUES ($r, 'Ollie', 'mud2.com', 400, 500);",
            ("$r", run));
        return (first, second);
    }

    private static string Text(IEnumerable<WireRecord> records)
        => string.Concat(records.Select(r => Encoding.Latin1.GetString(r.Payload)));

    [Fact]
    public void ALaterSessionsReplay_MasksTheLogoutItHolds()
    {
        var (_, second) = SeedTwoLogins();
        var text = Text(SessionSlice.Read(Db, second)!.ToReplay());

        Assert.Contains("second session", text);
        Assert.Contains("goodbye", text);
        Assert.DoesNotContain("first session", text);
        Assert.DoesNotContain(AccountId, text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Muckrep.PlaceholderAccountId + " logged out.", text);
    }

    [Fact]
    public void TheFirstSessionsReplay_HoldsTheLoginMasked_AndNothingTyped()
    {
        var (first, _) = SeedTwoLogins();
        var replay = SessionSlice.Read(Db, first)!.ToReplay();
        Assert.All(replay, r => Assert.NotEqual(WireDirection.Tx, r.Direction));
        var text = Text(replay);
        Assert.Contains("Account ID: " + Muckrep.PlaceholderAccountId, text);
        Assert.Contains("first session", text);
        Assert.DoesNotContain("second session", text);
        Assert.DoesNotContain(AccountId, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Password, text);
    }

    [Fact]
    public void ASingleSession_IsTheWholeRun()
        => Assert.Equal(((long?)null, (long?)null), SessionSlice.Bounds([(7, 100, 200)], 7));

    [Fact]
    public void ASessionThatDoesNotExist_HasNoSlice()
    {
        SeedTwoLogins();
        Assert.Null(SessionSlice.Read(Db, 999));
    }

    [Fact]
    public void WriteNewFile_NeverOverwrites_AndLeavesTheOldFileAlone()
    {
        var path = Path.Combine(_directory, "x.muckrep");
        File.WriteAllText(path, "keep");
        Assert.Throws<IOException>(() => Muckrep.WriteNewFile(path, [Rx(1)]));
        Assert.Equal("keep", File.ReadAllText(path));
    }
}
