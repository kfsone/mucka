using System.Globalization;
using Microsoft.Data.Sqlite;
using MudSharp.Session;
using Mucka.Commands;
using Mucka.Store;

namespace Mucka.Combat;

/// <summary>
/// Reconstructs <c>persona_sessions</c> for rows recorded before the table existed, by replaying the
/// wire log through the same parser that produced them, and then hands every unclaimed fact row to
/// the login it happened in.
///
/// <para>Those are two phases on purpose. Reconstruction is skipped for a run that already has
/// sessions; attribution is not, because the live path records sessions of its own and can still
/// leave rows unclaimed behind them.</para>
///
/// <para><b>Why a replay and not a query.</b> A login boundary is not in any column: it is the
/// server's own game-mode transition, which only the C1 decoder can see. The wire log kept the bytes,
/// so the boundary is still recoverable - it just has to be read the same way it was read live.
/// Nothing is inferred from timestamps or gaps.</para>
///
/// <para><b>Why it is not a migration.</b> <c>Mucka.Store</c> deliberately has no game model, so a
/// DbUp script cannot reach the parser. Instead this is idempotent and runs after migration: it fills
/// only what is empty, so a second run does nothing and an interrupted first run heals itself.</para>
///
/// <para><b>It cannot reach further than the wire log does, and "every fact row has a login" is not
/// true.</b> A login is only recoverable where the bytes that announced it survive, and two ordinary
/// things break that. A run may have had its wire pruned out from under it while it was still
/// playing - run 1 of the operator's store has 8,905 wire records ending an hour in and kept
/// recording swings for two more days. And a run that was killed rather than closed never wrote
/// <c>mucka_runs.ended_ms</c>, so it is permanently ineligible for replay, because a run that may
/// still be alive must never be replayed; five of the operator's 38 runs are in that state. Together
/// those leave 1,762 of 9,732 swings with no login, and no amount of re-running will change it. 0004
/// deleted what predates the wire log entirely, which is a smaller claim than it used to make
/// here.</para>
///
/// <para>Threading: opens its own short-lived connections and must run OFF the UI thread
/// (Invariant #1). Safe alongside the live writer - WAL, plus the busy timeout every connection
/// here sets.</para>
/// </summary>
public static class PersonaSessionBackfill
{
    /// <summary>One reconstructed login: when game mode was entered and left, who was playing, and
    /// how it finished.</summary>
    private readonly record struct Login(long StartedMs, long? EndedMs, string? Persona, string? EndNote);

    /// <summary>
    /// Fills in every login the wire log can still account for, then attributes whatever fact rows
    /// are still unclaimed. Returns how many <c>persona_sessions</c> rows were created; zero when
    /// there is nothing left to reconstruct, which is the steady state - and note that zero does not
    /// mean nothing happened, because attribution runs either way.
    /// </summary>
    /// <param name="liveRunId">The run this client is recording into right now, excluded from the
    /// replay. Without it there is a race: this runs fire-and-forget at start-up while the connection
    /// is already feeding bytes, so a login can complete in the wire log before the live path's own
    /// INSERT lands - at which point the current run looks exactly like an un-backfilled one and gets
    /// a duplicate session row. Nothing is corrupted (attribution only fills nulls), but the ghost row
    /// is permanent, because afterwards the run "has sessions" and is skipped for ever.</param>
    public static int Run(string databasePath, long? liveRunId = null,
        Action<string, Exception>? onError = null)
    {
        try
        {
            using var connection = MuckaDb.Open(databasePath);
            Execute(connection, "PRAGMA busy_timeout=5000;");

            var created = 0;
            foreach (var runId in RunsNeedingBackfill(connection, liveRunId))
                created += ReconstructSessions(connection, runId);

            // Separate phase, and deliberately not folded back into the one above. Reconstruction is
            // guarded by "this run has no sessions", which is right for creating them and wrong for
            // attribution: a run whose sessions the LIVE path recorded is skipped entirely, so rows it
            // left unclaimed would never be reachable. Attribution keyed on the sessions themselves
            // reaches those too, and having one rule instead of two is the point.
            AttributeUnclaimedRows(connection, liveRunId);

            return created;
        }
        catch (Exception ex)
        {
            // Best-effort: unattributed history is worse than attributed history and better than a
            // client that will not start.
            onError?.Invoke("PersonaSessionBackfill (older rows stay unattributed)", ex);
            return 0;
        }
    }

    /// <summary>
    /// Runs that still have wire bytes and no logins recorded against them, excluding the live one.
    ///
    /// <para>"No logins" is the idempotence guard. It holds for every FINISHED run: one from before
    /// the table existed has none until this fills them, one recorded since opened its own at
    /// game-mode entry, and neither changes afterwards. It does NOT hold for a run still going, which
    /// passes through "has wire, has no sessions yet" on its way to its first login. Replaying one
    /// then invents a session for a login the live path is about to record, and the ghost is
    /// permanent, because afterwards the run "has sessions" and is skipped for ever.</para>
    ///
    /// <para><b>Every unfinished run is excluded, not just this client's.</b> A <c>mucka_runs</c> row
    /// gets its <c>ended_ms</c> when that store is disposed, so a null one is a run whose process may
    /// still be alive - and the operator runs several Muckas at once, on different MUD2s or different
    /// personas, which 0003 records as a design fact. An earlier version excluded only
    /// <paramref name="liveRunId"/>, which is a one-instance fix to a multi-instance problem: a second
    /// client starting eighteen seconds after the first would happily replay the first's open run.
    /// The cost of the wider rule is that a run left unfinished by a crash is never backfilled, which
    /// is the safe direction - it keeps its rows unattributed rather than gaining invented ones.</para>
    /// </summary>
    private static List<long> RunsNeedingBackfill(SqliteConnection connection, long? liveRunId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT w.mucka_run_id
            FROM wire w
            JOIN mucka_runs r ON r.id = w.mucka_run_id
            WHERE r.ended_ms IS NOT NULL
              AND w.mucka_run_id IS NOT $live
              AND NOT EXISTS (SELECT 1 FROM persona_sessions p WHERE p.mucka_run_id = w.mucka_run_id)
            ORDER BY w.mucka_run_id;
            """;
        command.Parameters.AddWithValue("$live", (object?)liveRunId ?? DBNull.Value);
        var runs = new List<long>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            runs.Add(reader.GetInt64(0));
        return runs;
    }

    private static int ReconstructSessions(SqliteConnection connection, long runId)
    {
        var logins = ReplayLogins(connection, runId);
        if (logins.Count == 0)
            return 0;

        var host = HostOf(connection, runId);
        using var transaction = connection.BeginTransaction();

        foreach (var login in logins)
            InsertSession(connection, transaction, runId, host, login);

        transaction.Commit();
        return logins.Count;
    }

    /// <summary>One login's span: the rows it may claim.</summary>
    private readonly record struct Window(long Id, long From, long To);

    /// <summary>
    /// Hands every still-unclaimed fact row to the login it happened in.
    ///
    /// <para>Idempotent: it fills only nulls, so a second run over the same rows does nothing.</para>
    ///
    /// <para>The <see cref="AnythingUnattributed"/> guard bounds the cost, it does not reach zero.
    /// Rows that no login can ever claim stay null for ever - see the note on this class - so on a
    /// store that holds any, the guard passes on every start-up and the full pass runs every time.
    /// That is five short UPDATEs per login against an indexed column, which is cheap; the guard is
    /// there for the store that has none, not as a promise that work eventually stops.</para>
    /// </summary>
    private static void AttributeUnclaimedRows(SqliteConnection connection, long? liveRunId)
    {
        if (!AnythingUnattributed(connection))
            return;

        var windows = BoundedSessions(connection, liveRunId);
        if (windows.Count == 0)
            return;

        using var transaction = connection.BeginTransaction();
        foreach (var window in windows)
            AttributeRows(connection, transaction, window);
        transaction.Commit();
    }

    private static bool AnythingUnattributed(SqliteConnection connection)
    {
        foreach (var (table, _) in FactTables)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT EXISTS(SELECT 1 FROM {table} WHERE persona_session_id IS NULL);";
            if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Every login that has a definite end, oldest first.
    ///
    /// <para><b>A login cannot outlive the process that recorded it.</b> One with no recorded exit -
    /// the operator closed Mucka while still logged in, which is ordinary, not a crash - ends where
    /// the next login in the same run begins, or failing that where the RUN ended. It used to end
    /// nowhere, and since runs are replayed oldest first, the earliest such login claimed every fact
    /// row written afterwards, for ever, across other runs and other personas.</para>
    ///
    /// <para>Unfinished runs are excluded, so <c>r.ended_ms</c> is never null and a window always has
    /// an upper bound. That is why <see cref="AttributeRows"/> takes a <c>long</c> rather than a
    /// nullable one: an unbounded claim is now unsayable rather than merely unsaid.</para>
    ///
    /// <para><b>Concurrent runs are not resolvable here.</b> The operator runs several Muckas at once,
    /// and the fact tables carry no run of their own - only the login - so where two runs overlap, a
    /// row inside both windows goes to whichever login started first. In the operator's store that is
    /// 81 swings of 9,461. Fixing it properly means stamping the run on every fact row; ordering by
    /// <c>started_ms</c> at least makes the wrong answer the same wrong answer every time.</para>
    /// </summary>
    private static List<Window> BoundedSessions(SqliteConnection connection, long? liveRunId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.id, p.started_ms,
                   COALESCE(
                       p.ended_ms,
                       (SELECT MIN(q.started_ms) - 1 FROM persona_sessions q
                         WHERE q.mucka_run_id = p.mucka_run_id AND q.started_ms > p.started_ms),
                       r.ended_ms) AS ends
            FROM persona_sessions p
            JOIN mucka_runs r ON r.id = p.mucka_run_id
            WHERE r.ended_ms IS NOT NULL
              AND p.mucka_run_id IS NOT $live
            ORDER BY p.started_ms, p.id;
            """;
        command.Parameters.AddWithValue("$live", (object?)liveRunId ?? DBNull.Value);
        var windows = new List<Window>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            windows.Add(new Window(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2)));
        return windows;
    }

    /// <summary>
    /// Feeds one run's received bytes back through <see cref="MudSession"/> and records where game
    /// mode opened and closed. Each boundary is stamped with the timestamp of the wire record that
    /// carried it, so the reconstructed instants are the ones the rows were written against rather
    /// than today's.
    /// </summary>
    private static List<Login> ReplayLogins(SqliteConnection connection, long runId)
    {
        var logins = new List<Login>();
        long? openedAt = null;
        string? persona = null;
        var watcher = new SessionEndWatcher();
        var ts = 0L;   // the wire record being fed; every instant recorded comes from this

        using var session = new MudSession(new MudSessionOptions
        {
            // No probes: there is no socket to send them on, and a replay must not invent traffic
            // that was never in the log.
            FesHeartbeatInterval = TimeSpan.FromDays(1),
            StaleProbeDelay = TimeSpan.FromDays(1),
        });
        // The combat clock is left alone - it is internal to mudsharp, and nothing here reads a combat
        // event. Game mode and character identity come off the C1 stream and the score sheet, neither
        // of which consults a clock. Every instant recorded below is the WIRE record's own ts.
        session.GameModeEntered += () => { openedAt = ts; persona = null; watcher.Begin(); };
        session.CharacterIdentified += name => persona = name;

        // The one classifier, driven from what a replay can actually observe.
        //
        // WorldResetLanded is deliberately NOT subscribed, and subscribing it would be dead code:
        // MudSession corroborates that event against DateTime.UtcNow before raising it, so on bytes
        // recorded days ago the check never passes. The watcher picks the reset up from its own line
        // instead - see ShellText.IsWorldResetLandingLine. Without that it recorded a reset-ended
        // login as `died`, because the summary a reset prints on the way out is the same one a death
        // prints, and the live path recorded `reset` for the same event.
        session.PersonaWiped += watcher.NotePersonaWiped;
        session.LineReady += line =>
        {
            if (openedAt is not null)
                watcher.NoteLine(line.PlainText);
        };

        session.GameModeExited += () =>
        {
            if (openedAt is long started)
                logins.Add(new Login(started, ts, persona, PersonaSessionEndNote.For(watcher.Reason)));
            openedAt = null;
            persona = null;
            watcher.Begin();
        };

        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT ts_ms, data FROM wire WHERE mucka_run_id = $run AND direction = 0 ORDER BY seq;";
            command.Parameters.AddWithValue("$run", runId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                ts = reader.GetInt64(0);
                session.Feed((byte[])reader[1]);
            }
        }

        // A run whose log ends mid-game - the client was killed, or the capture was truncated.
        if (openedAt is long stillOpen)
            logins.Add(new Login(stillOpen, null, persona, PersonaSessionEndNote.For(watcher.Reason)));

        return logins;
    }

    private static string? HostOf(SqliteConnection connection, long runId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT host FROM mucka_runs WHERE id = $run;";
        command.Parameters.AddWithValue("$run", runId);
        return command.ExecuteScalar() as string;
    }

    private static long InsertSession(SqliteConnection connection, SqliteTransaction transaction,
        long runId, string? host, Login login)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO persona_sessions (mucka_run_id, persona, host, started_ms, ended_ms, ended_note)
            VALUES ($run, $persona, $host, $started, $ended, $note);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$persona", (object?)login.Persona ?? DBNull.Value);
        command.Parameters.AddWithValue("$host", (object?)host ?? DBNull.Value);
        command.Parameters.AddWithValue("$started", login.StartedMs);
        command.Parameters.AddWithValue("$ended", (object?)login.EndedMs ?? DBNull.Value);
        command.Parameters.AddWithValue("$note", (object?)login.EndNote ?? DBNull.Value);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>The fact tables and the column each stamps its own time in.</summary>
    private static readonly (string Table, string Column)[] FactTables =
    [
        ("swings", "ts"),
        ("fights", "started_at_ms"),
        ("score_events", "ts"),
        ("npc_stamina_reads", "ts"),
        ("encounters", "encounter_started_at_ms"),
    ];

    private static void AttributeRows(SqliteConnection connection, SqliteTransaction transaction,
        Window window)
    {
        foreach (var (table, column) in FactTables)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            // IS NULL is what makes this idempotent AND what stops it stealing rows a live login
            // already claimed. Identifiers are compile-time literals from FactTables above.
            command.CommandText =
                $"UPDATE {table} SET persona_session_id = $id " +
                $"WHERE persona_session_id IS NULL AND {column} BETWEEN $from AND $to;";
            command.Parameters.AddWithValue("$id", window.Id);
            command.Parameters.AddWithValue("$from", window.From);
            command.Parameters.AddWithValue("$to", window.To);
            command.ExecuteNonQuery();
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
