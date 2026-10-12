using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Mucka.Store;

/// <summary>
/// Fills <c>mucka_runs.account_id</c> for runs recorded before the column existed, or by a login
/// typed by hand, from what the server said back during the login.
///
/// <para><b>Why the received text and not what was sent.</b> What the client sent is not a reliable
/// source: in the operator's database the records sent after the <c>Account ID:</c> prompt are in clear
/// for runs of client 0.22.x, and a MuckaII-imported run has asterisks there. The server's side is in
/// every login, twice - echoed after <c>Account ID:</c>, and printed in the
/// <c>&lt;id&gt; logged in on pts/N.</c> banner. The banner is read first; the echo is the fallback,
/// for a run whose banner never came.</para>
///
/// <para>Only the first <see cref="LoginRecords"/> records of a run are read: one run is one
/// connection, so its login is at the start. The id is compared upper-case everywhere, because the
/// server echoes <c>P90003673</c> and the menu later prints <c>p90003673</c>.</para>
///
/// <para><b>Idempotent without a second pass over everything.</b> A run that is finished and names no
/// account is marked with the empty string - "the wire was read and names none" - and is not read
/// again. A run that has not finished is never marked: it may be mid-login right now, in this process
/// or another, and the next start-up looks again. The live run is excluded outright.</para>
///
/// <para>Threading: opens its own connection; run it off the UI and Feed threads. Never throws.</para>
/// </summary>
public static class AccountBackfill
{
    /// <summary>How far into a run its login is looked for. The banner of every run in the operator's
    /// database arrives within the first few dozen records.</summary>
    public const int LoginRecords = 500;

    private static readonly Regex Banner = new(
        @"([A-Za-z]\d+)\s+logged in on", RegexOptions.CultureInvariant);

    private static readonly Regex Echo = new(
        @"Account ID:\s*([A-Za-z0-9]+)\s*[\r\n]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The canonical form of an account id, used by every writer and reader of the column.</summary>
    public static string? Normalize(string? accountId)
        => string.IsNullOrWhiteSpace(accountId) ? null : accountId.Trim().ToUpperInvariant();

    /// <summary>Returns how many runs were given an account id.</summary>
    public static int Run(string databasePath, long? liveRunId = null, Action<string, Exception>? onError = null)
    {
        try
        {
            using var connection = MuckaDb.Open(databasePath);
            Execute(connection, "PRAGMA busy_timeout=5000;");

            var pending = Pending(connection, liveRunId);
            if (pending.Count == 0)
                return 0;

            var found = Scan(connection, pending);

            using var transaction = connection.BeginTransaction();
            var filled = 0;
            foreach (var (id, finished) in pending)
            {
                string? account = found.GetValueOrDefault(id);
                if (account is null && !finished)
                    continue;
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE mucka_runs SET account_id = $account WHERE id = $id AND account_id IS NULL;";
                command.Parameters.AddWithValue("$account", account ?? string.Empty);
                command.Parameters.AddWithValue("$id", id);
                command.ExecuteNonQuery();
                if (account is not null)
                    filled++;
            }
            transaction.Commit();
            return filled;
        }
        catch (Exception ex)
        {
            onError?.Invoke("AccountBackfill (older runs keep no account)", ex);
            return 0;
        }
    }

    private static Dictionary<long, bool> Pending(SqliteConnection connection, long? liveRunId)
    {
        var pending = new Dictionary<long, bool>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, ended_ms IS NOT NULL FROM mucka_runs WHERE account_id IS NULL AND id IS NOT $live;";
        command.Parameters.AddWithValue("$live", (object?)liveRunId ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            pending[reader.GetInt64(0)] = reader.GetInt64(1) != 0;
        return pending;
    }

    /// <summary>One pass over the early received records of every pending run, in arrival order. Each
    /// run keeps the tail of what came before, because the echo arrives in pieces ("P", then
    /// "90003673\r\nPassword: ") and a banner can be split too.</summary>
    private static Dictionary<long, string> Scan(SqliteConnection connection, Dictionary<long, bool> pending)
    {
        var banner = new Dictionary<long, string>();
        var echo = new Dictionary<long, string>();
        var text = new Dictionary<long, StringBuilder>();

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT w.mucka_run_id, w.data FROM wire w
            JOIN mucka_runs r ON r.id = w.mucka_run_id
            WHERE r.account_id IS NULL AND w.direction = 0 AND w.seq < {LoginRecords}
            ORDER BY w.id;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var run = reader.GetInt64(0);
            if (!pending.ContainsKey(run) || banner.ContainsKey(run))
                continue;

            if (!text.TryGetValue(run, out var window))
                text[run] = window = new StringBuilder();
            window.Append(Encoding.Latin1.GetString((byte[])reader[1]));
            // Long enough to hold the prompt, the echo and the banner; bounded so a run of chatter
            // cannot grow it.
            if (window.Length > 4096)
                window.Remove(0, window.Length - 4096);

            var window2 = window.ToString();
            if (Banner.Match(window2) is { Success: true } b)
                banner[run] = Normalize(b.Groups[1].Value)!;
            else if (!echo.ContainsKey(run) && Echo.Match(window2) is { Success: true } e)
                echo[run] = Normalize(e.Groups[1].Value)!;
        }

        var found = new Dictionary<long, string>(echo);
        foreach (var (run, account) in banner)
            found[run] = account;
        return found;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
