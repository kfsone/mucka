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

    private long NewRun(SqliteConnection connection, string host,
        long startedMs = 1, long endedMs = 99999)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            // ended_ms set, because only a FINISHED run is backfilled - a null one is a process that
            // may still be alive, and replaying it would invent sessions the live path is recording.
            "INSERT INTO mucka_runs (started_ms, ended_ms, host) VALUES ($started, $ended, $host); "
            + "SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$started", startedMs);
        command.Parameters.AddWithValue("$ended", endedMs);
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

    /// <summary>
    /// A world reset ends the login as `reset`, not `died`.
    ///
    /// <para>The replay used to skip <c>WorldResetLanded</c>, so the end-of-game summary that follows
    /// a reset - identical to the one a death prints - was all the classifier saw, and the backfill
    /// recorded `died` where the live path recorded `reset`. Two answers for one event, and being
    /// idempotent, the wrong one was permanent.</para>
    /// </summary>
    [Fact]
    public void AResetEndedLogin_IsNotRecordedAsADeath()
    {
        using (var connection = MuckaDb.Open(DbPath))
        {
            var run = NewRun(connection, "mud2.co.uk");
            Wire(connection, run, 1, 1_000, EntersGameMode);
            // C06 C06 - "Something magical is happening." - then the summary the shell prints on the
            // way out, which on its own looks exactly like an ordinary death.
            Wire(connection, run, 2, 5_000,
                [0xA1, 0xA1, 0xFF, 0xFF, .. Encoding.Latin1.GetBytes(
                    "Something magical is happening.\r\nOverall, you scored 1,263 points this game.\r\n")]);
            Wire(connection, run, 3, 9_000, LeavesGameMode);
        }

        Assert.Equal(1, PersonaSessionBackfill.Run(DbPath));
        Assert.Equal("reset", ScalarString(DbPath, "SELECT ended_note FROM persona_sessions"));
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

    /// <summary>
    /// A login whose close the log never saw does not reach past the process that recorded it.
    ///
    /// <para>It used to. An unclosed last login was attributed with no upper bound at all, and runs
    /// are replayed oldest first, so the earliest such login claimed every fact row in the database
    /// from its start to the end of time - including rows written by later runs, days afterwards, by
    /// other personas. In the operator's own store that was 8,598 of 8,652 swings on one session, and
    /// it was permanent: afterwards every later run "has sessions" and is skipped for ever. The
    /// single-run tests around this one could not see it by construction.</para>
    ///
    /// <para>"The client was killed mid-game" is not the rare case it sounds like - it is what closing
    /// Mucka while still logged in looks like. Twelve of the operator's finished runs end that
    /// way.</para>
    /// </summary>
    [Fact]
    public void AnUnclosedLogin_DoesNotClaimALaterRunsRows()
    {
        long runA, runB;
        using (var connection = MuckaDb.Open(DbPath))
        {
            // Enters game mode and never leaves: the process went away with the persona still in.
            runA = NewRun(connection, "mud2.co.uk", startedMs: 1_000, endedMs: 10_000);
            Wire(connection, runA, 1, 2_000, EntersGameMode);

            // A separate, later run that logs in and out cleanly.
            runB = NewRun(connection, "mud2.co.uk", startedMs: 20_000, endedMs: 40_000);
            Wire(connection, runB, 1, 21_000, EntersGameMode);
            Wire(connection, runB, 2, 39_000, LeavesGameMode);

            Swing(connection, 5_000);    // run A, inside its unclosed login
            Swing(connection, 30_000);   // run B, and A must not reach it
        }

        Assert.Equal(2, PersonaSessionBackfill.Run(DbPath));

        var aSession = Count(DbPath, $"SELECT id FROM persona_sessions WHERE mucka_run_id = {runA}");
        var bSession = Count(DbPath, $"SELECT id FROM persona_sessions WHERE mucka_run_id = {runB}");

        Assert.Equal(aSession, Count(DbPath, "SELECT persona_session_id FROM swings WHERE ts = 5000"));
        Assert.Equal(bSession, Count(DbPath, "SELECT persona_session_id FROM swings WHERE ts = 30000"));
    }

    /// <summary>A login the log never saw close - the client was killed mid-game. The session is still
    /// recorded, with no end, and still claims the rows after it up to the point the RUN ended: an
    /// open span is the honest reading of the login, and the alternative is throwing away every row of
    /// the last session before a crash. What it cannot do is outlive its own process - see
    /// <see cref="AnUnclosedLogin_DoesNotClaimALaterRunsRows"/>.</summary>
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

    /// <summary>
    /// A run whose logins the LIVE path already recorded still gets its unclaimed rows attributed.
    ///
    /// <para>Reconstruction skips such a run - it has sessions, so there is nothing to rebuild - and
    /// while attribution was a side effect of reconstruction, that skip took the rows with it. Rows
    /// the live path left null were then unreachable for ever. Here there is no wire log at all, which
    /// is the sharpest form of the case: nothing to replay, and the row still finds its login.</para>
    /// </summary>
    [Fact]
    public void ARunWhoseSessionsAlreadyExist_StillGetsItsUnclaimedRowsAttributed()
    {
        long sessionId;
        using (var connection = MuckaDb.Open(DbPath))
        {
            var run = NewRun(connection, "mud2.co.uk", startedMs: 1_000, endedMs: 40_000);
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO persona_sessions (mucka_run_id, persona, host, started_ms, ended_ms) "
                + "VALUES ($run, 'Ollie', 'mud2.co.uk', 2000, 30000); SELECT last_insert_rowid();";
            command.Parameters.AddWithValue("$run", run);
            sessionId = Convert.ToInt64(command.ExecuteScalar());

            Swing(connection, 5_000);    // inside the login
            Swing(connection, 35_000);   // after it, so still nobody's
        }

        Assert.Equal(0, PersonaSessionBackfill.Run(DbPath));   // nothing reconstructed

        Assert.Equal(sessionId, Count(DbPath, "SELECT persona_session_id FROM swings WHERE ts = 5000"));
        Assert.Equal(1,
            Count(DbPath, "SELECT COUNT(*) FROM swings WHERE persona_session_id IS NULL"));
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
