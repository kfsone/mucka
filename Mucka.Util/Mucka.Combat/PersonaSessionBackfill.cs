using Microsoft.Data.Sqlite;
using MudSharp.Session;
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
    public static int Run(string databasePath, Action<string, Exception>? onError = null)
    {
        try
        {
            using var connection = MuckaDb.Open(databasePath);
            Execute(connection, "PRAGMA busy_timeout=5000;");

            var created = 0;
            foreach (var runId in RunsNeedingBackfill(connection))
                created += BackfillRun(connection, runId);
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
    /// Runs that still have wire bytes and no logins recorded against them.
    ///
    /// <para>"No logins" is the whole idempotence guard, and it is sound because the two ways a run
    /// gets them are exclusive: a run from before this table existed has none until this fills them,
    /// and a run recorded since opens its own at game-mode entry. A run can never be both.</para>
    /// </summary>
    private static List<long> RunsNeedingBackfill(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT w.mucka_run_id
            FROM wire w
            WHERE NOT EXISTS (SELECT 1 FROM persona_sessions p WHERE p.mucka_run_id = w.mucka_run_id)
            ORDER BY w.mucka_run_id;
            """;
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
        string? endNote = null;
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
        session.GameModeEntered += () => { openedAt = ts; persona = null; endNote = null; };
        session.CharacterIdentified += name => persona = name;

        // The same classification the live path applies, against the same signals - see
        // MuckaConnection.NotePersonaSessionEnd and PersonaSessionEnd. A world reset is deliberately
        // NOT reconstructed here: the live path takes it from the C06 C06 landing, and a replay would
        // have to guess whether an exit that merely happened near one was caused by it.
        session.PersonaWiped += () => endNote = PersonaSessionEnd.Permadeath;
        session.LineReady += line =>
        {
            if (openedAt is null || endNote is not null)
                return;
            var text = line.PlainText;
            if (text.Contains("Cheerio!", StringComparison.Ordinal))
                endNote = PersonaSessionEnd.Quit;
            else if (text.Contains("Overall, you ", StringComparison.Ordinal)
                  && text.Contains(" points this game.", StringComparison.Ordinal))
                endNote = PersonaSessionEnd.Died;
        };

        session.GameModeExited += () =>
        {
            if (openedAt is long started)
                logins.Add(new Login(started, ts, persona, endNote));
            openedAt = null;
            persona = null;
            endNote = null;
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
            logins.Add(new Login(stillOpen, null, persona, endNote));

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
