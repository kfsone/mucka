using System.Text;
using Microsoft.Data.Sqlite;
using Mucka.Combat;
using Mucka.Store;

namespace Mucka.Util.Tests;

/// <summary>
/// Rebuilding <c>persona_sessions</c> for rows recorded before the table existed.
///
/// <para>Driven through real C1 bytes rather than a stubbed boundary, because the thing under test is
/// precisely that the parser finds the login where it found it live. The entering bytes are lifted
/// from a real login in the wire log rather than assembled from the decoder; the option-menu prompt
/// closes it.</para>
/// </summary>
public sealed class PersonaSessionBackfillTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mucka-backfill-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string DbPath => Path.Combine(_directory, MuckaDb.DefaultFileName);

    /// <summary>The game-mode-entering prefix, lifted verbatim from the head of a real login in the
    /// operator's own wire log rather than assembled from the decoder's source. Two C1 sequences,
    /// each terminated by C255: it is what the server actually sends, which is the only thing that
    /// makes this test evidence of anything.</summary>
    private static readonly byte[] EntersGameMode =
        [0xAF, 0x9C, 0xFF, 0xFF, 0x9D, 0x9C, 0xFF, 0xFF];

    /// <summary>The MUD Shell's own prompt, which is what leaving the game lands on.</summary>
    private static byte[] LeavesGameMode => Encoding.Latin1.GetBytes("\r\nOption (H for help): ");

    private long NewRun(SqliteConnection connection, string host)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO mucka_runs (started_ms, host) VALUES (1, $host); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$host", host);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private void Wire(SqliteConnection connection, long runId, int seq, long ts, byte[] data)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO wire (mucka_run_id, seq, ts_ms, direction, data) VALUES ($r,$q,$t,0,$d);";
        command.Parameters.AddWithValue("$r", runId);
        command.Parameters.AddWithValue("$q", seq);
        command.Parameters.AddWithValue("$t", ts);
        command.Parameters.AddWithValue("$d", data);
        command.ExecuteNonQuery();
    }

    private void Swing(SqliteConnection connection, long ts)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO swings (ts, dir, npc_group, hit) VALUES ($t, 'out', 'rats', 1);";
        command.Parameters.AddWithValue("$t", ts);
        command.ExecuteNonQuery();
    }

    private static long Count(string path, string sql)
    {
        using var connection = MuckaDb.OpenRead(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    /// <summary>The whole point: a swing recorded before persona_sessions existed gets the login it
    /// actually happened in, read back out of the bytes that were on the wire at the time.</summary>
    [Fact]
    public void ASwingInsideARecordedLogin_GetsThatLogin()
    {
        using (var connection = MuckaDb.Open(DbPath))
        {
            var run = NewRun(connection, "mud2.co.uk");
            Wire(connection, run, 1, 1_000, EntersGameMode);
            Wire(connection, run, 2, 9_000, LeavesGameMode);
            Swing(connection, 5_000);           // inside the login
            Swing(connection, 50_000);          // after it closed - at the shell, so unattributed
        }

        Assert.Equal(1, PersonaSessionBackfill.Run(DbPath));

        Assert.Equal(1, Count(DbPath, "SELECT COUNT(*) FROM persona_sessions"));
        Assert.Equal(1_000, Count(DbPath, "SELECT started_ms FROM persona_sessions"));
        Assert.Equal(9_000, Count(DbPath, "SELECT ended_ms FROM persona_sessions"));
        Assert.Equal("mud2.co.uk",
            Convert.ToString(ScalarString(DbPath, "SELECT host FROM persona_sessions")));

        // The swing inside the window is attributed; the one at the shell is left alone rather than
        // being credited to the character who had just logged out.
        Assert.Equal(1, Count(DbPath, "SELECT COUNT(*) FROM swings WHERE persona_session_id IS NOT NULL"));
        Assert.Equal(5_000, Count(DbPath, "SELECT ts FROM swings WHERE persona_session_id IS NOT NULL"));
    }

    /// <summary>
    /// How the login ended is reconstructed too, from the same lines the live path reads - see
    /// <see cref="PersonaSessionEnd"/>. The death text is verbatim from the operator's wire log.
    ///
    /// <para>The quit case is the one that matters: it carries an "Overall, you scored" summary
    /// exactly as the death does, so anything keying on that summary alone would call it a death.
    /// The <c>Cheerio!</c> is the discriminator.</para>
    /// </summary>
    [Theory]
    [InlineData("AN UNSEEN STRONG CURRENT SUDDENLY DRAGS HOLD OF YOUR LITTLE CRAFT AND SUCKS IT "
              + "UNDERWATER. YOU SPLASH ABOUT, BUT EVENTUALLY DROWN. SIGH.\r\n"
              + "(Persona saved on -55 = 2,321).\r\nOverall, you scored 2,126 points this game.\r\n", "died")]
    [InlineData("Cheerio!\r\nOverall, you scored 7,342 points this game.\r\n", "quit")]
    [InlineData("You hit the rat0 (15-19).\r\n", null)]
    public void HowTheLoginEnded_IsReconstructedFromTheSameLines(string exitText, string? expected)
    {
        using (var connection = MuckaDb.Open(DbPath))
        {
            var run = NewRun(connection, "mud2.co.uk");
            Wire(connection, run, 1, 1_000, EntersGameMode);
            Wire(connection, run, 2, 5_000, Encoding.Latin1.GetBytes(exitText));
            Wire(connection, run, 3, 9_000, LeavesGameMode);
        }

        Assert.Equal(1, PersonaSessionBackfill.Run(DbPath));
        Assert.Equal(expected, ScalarString(DbPath, "SELECT ended_note FROM persona_sessions"));
    }

    /// <summary>It runs on every start-up, so doing nothing the second time is not a nicety. A run
    /// that already has logins is skipped entirely - which is also what stops it touching the rows a
    /// LIVE login already claimed.</summary>
    [Fact]
    public void RunningItAgain_ChangesNothing()
    {
        using (var connection = MuckaDb.Open(DbPath))
        {
            var run = NewRun(connection, "mud2.co.uk");
            Wire(connection, run, 1, 1_000, EntersGameMode);
            Wire(connection, run, 2, 9_000, LeavesGameMode);
            Swing(connection, 5_000);
        }

        Assert.Equal(1, PersonaSessionBackfill.Run(DbPath));
        Assert.Equal(0, PersonaSessionBackfill.Run(DbPath));
        Assert.Equal(0, PersonaSessionBackfill.Run(DbPath));
        Assert.Equal(1, Count(DbPath, "SELECT COUNT(*) FROM persona_sessions"));
    }

    /// <summary>A login the log never saw close - the client was killed mid-game. The session is still
    /// recorded, with no end, and still claims the rows after it: an open span is the honest reading,
    /// and the alternative is throwing away every row of the last session before a crash.</summary>
    [Fact]
    public void ALoginTheLogNeverSawClose_IsStillRecorded_AndStillClaimsItsRows()
    {
        using (var connection = MuckaDb.Open(DbPath))
        {
            var run = NewRun(connection, "mud2.co.uk");
            Wire(connection, run, 1, 1_000, EntersGameMode);
            Swing(connection, 7_000);
        }

        Assert.Equal(1, PersonaSessionBackfill.Run(DbPath));
        Assert.Equal(1, Count(DbPath, "SELECT COUNT(*) FROM persona_sessions WHERE ended_ms IS NULL"));
        Assert.Equal(1, Count(DbPath, "SELECT COUNT(*) FROM swings WHERE persona_session_id IS NOT NULL"));
    }

    /// <summary>Nothing on the wire, nothing to reconstruct. The rows stay unattributed rather than
    /// being handed to an invented session - which is the case for everything older than the wire log
    /// itself, and the reason 0003 dropped those rows instead of keeping them.</summary>
    [Fact]
    public void ARunWithNoWireBytes_InventsNothing()
    {
        using (var connection = MuckaDb.Open(DbPath))
        {
            NewRun(connection, "mud2.co.uk");
            Swing(connection, 5_000);
        }

        Assert.Equal(0, PersonaSessionBackfill.Run(DbPath));
        Assert.Equal(0, Count(DbPath, "SELECT COUNT(*) FROM persona_sessions"));
        Assert.Equal(0, Count(DbPath, "SELECT COUNT(*) FROM swings WHERE persona_session_id IS NOT NULL"));
    }

    private static string? ScalarString(string path, string sql)
    {
        using var connection = MuckaDb.OpenRead(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() as string;
    }
}
