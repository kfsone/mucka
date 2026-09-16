using Microsoft.Data.Sqlite;
using MudSharp.Session;
using Mucka.Commands;
using Mucka.Store;

namespace Mucka.Combat;

/// <summary>
/// Reconstructs <c>persona_sessions</c> for rows recorded before the table existed, by replaying the
/// wire log through the same parser that produced them.
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
/// <para>It cannot reach further back than the wire log does. Everything older was already dropped by
/// 0003, which is what makes "every fact row has a login" true rather than aspirational.</para>
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
    /// Fills in every login the wire log can still account for. Returns how many
    /// <c>persona_sessions</c> rows were created; zero when there is nothing left to do, which is the
    /// steady state.
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
                created += BackfillRun(connection, runId);

            PruneUnattributable(connection);
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
    /// game-mode entry, and neither changes afterwards. It does NOT hold for the run in progress,
    /// which passes through "has wire, has no sessions yet" on its way to the first login - hence
    /// <paramref name="liveRunId"/>. An earlier version of this comment claimed the guard was sound
    /// because a run "can never be both", which was simply wrong about the current one.</para>
    /// </summary>
    private static List<long> RunsNeedingBackfill(SqliteConnection connection, long? liveRunId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT w.mucka_run_id
            FROM wire w
            WHERE w.mucka_run_id IS NOT $live
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

    private static int BackfillRun(SqliteConnection connection, long runId)
    {
        var logins = ReplayLogins(connection, runId);
        if (logins.Count == 0)
            return 0;

        var host = HostOf(connection, runId);
        using var transaction = connection.BeginTransaction();

        for (var i = 0; i < logins.Count; i++)
        {
            var login = logins[i];
            // A login with no recorded exit runs until the next one starts, or to the end of time for
            // the last. The client was killed rather than logging out, so there is no honest end - but
            // the rows in between still belong to it.
            var endsAt = login.EndedMs
                ?? (i + 1 < logins.Count ? logins[i + 1].StartedMs - 1 : (long?)null);

            var id = InsertSession(connection, transaction, runId, host, login);
            AttributeRows(connection, transaction, id, login.StartedMs, endsAt);
        }

        transaction.Commit();
        return logins.Count;
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

        // The one classifier, the same instance type the live path drives - see SessionEndWatcher.
        // A world reset is deliberately not reconstructed: live it comes from the C06 C06 landing,
        // and a replay would have to guess whether an exit that merely happened near one was caused
        // by it.
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

    /// <summary>
    /// Deletes the fact rows no login can ever account for, so "every row has a session" is true
    /// rather than aspirational and no query has to special-case a null key forever.
    ///
    /// <para><b>After attribution, never before.</b> A row is unattributable only once the wire log
    /// has been replayed and declined to claim it. The migration used to do this cut, before anything
    /// had tried - which is the wrong order and, being frozen, permanent.</para>
    ///
    /// <para><b>Only older than the oldest wire record.</b> Anything inside the log's reach that is
    /// still unattributed is a gap in reconstruction, not a row from before sessions existed, and
    /// deleting it would hide the bug. The cut is read once per run here rather than embedded in a
    /// frozen script, so pruning the wire log shrinks what can be RECONSTRUCTED without silently
    /// enlarging what gets DESTROYED. With no wire at all there is no cut and nothing is deleted.</para>
    /// </summary>
    private static void PruneUnattributable(SqliteConnection connection)
    {
        long cut;
        using (var probe = connection.CreateCommand())
        {
            probe.CommandText = "SELECT MIN(ts_ms) FROM wire;";
            if (probe.ExecuteScalar() is not long min)
                return;
            cut = min;
        }

        using var transaction = connection.BeginTransaction();
        foreach (var (table, column) in FactTables)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            // Identifiers are compile-time literals from FactTables. Both halves matter: unattributed
            // AND older than any wire record.
            command.CommandText =
                $"DELETE FROM {table} WHERE persona_session_id IS NULL AND {column} < $cut;";
            command.Parameters.AddWithValue("$cut", cut);
            command.ExecuteNonQuery();
        }

        // The encounter children have no session key of their own - they reach it through encounters,
        // so they follow whatever their parent did.
        foreach (var sql in new[]
        {
            "DELETE FROM encounter_contents_items WHERE contents_id IN (SELECT id FROM encounter_contents "
            + "WHERE encounter_started_at_ms NOT IN (SELECT encounter_started_at_ms FROM encounters));",
            "DELETE FROM encounter_contents WHERE encounter_started_at_ms NOT IN (SELECT encounter_started_at_ms FROM encounters);",
            "DELETE FROM encounter_lines    WHERE encounter_started_at_ms NOT IN (SELECT encounter_started_at_ms FROM encounters);",
            "DELETE FROM encounter_events   WHERE encounter_started_at_ms NOT IN (SELECT encounter_started_at_ms FROM encounters);",
            "DELETE FROM encounter_stats    WHERE encounter_started_at_ms NOT IN (SELECT encounter_started_at_ms FROM encounters);",
            "DELETE FROM creature_values    WHERE encounter_started_at_ms NOT IN (SELECT encounter_started_at_ms FROM encounters);",
        })
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
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
        return Convert.ToInt64(command.ExecuteScalar());
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
        long personaSessionId, long startedMs, long? endedMs)
    {
        foreach (var (table, column) in FactTables)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            // IS NULL is what makes this idempotent AND what stops it stealing rows a live login
            // already claimed. Identifiers are compile-time literals from FactTables above.
            command.CommandText =
                $"UPDATE {table} SET persona_session_id = $id " +
                $"WHERE persona_session_id IS NULL AND {column} >= $from " +
                $"  AND ($to IS NULL OR {column} <= $to);";
            command.Parameters.AddWithValue("$id", personaSessionId);
            command.Parameters.AddWithValue("$from", startedMs);
            command.Parameters.AddWithValue("$to", (object?)endedMs ?? DBNull.Value);
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
