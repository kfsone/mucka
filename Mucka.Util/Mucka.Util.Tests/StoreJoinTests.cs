using Microsoft.Data.Sqlite;
using MudSharp.Combat;
using MudSharp.Models;
using Mucka.Combat;
using Mucka.Store;

namespace Mucka.Util.Tests;

/// <summary>
/// The join across the writers, which is the whole reason one database exists.
///
/// <para>Every writer's acceptance of the encounter key is covered in isolation elsewhere. Nothing
/// covered the thing those tests exist FOR: that a real SQL join on <c>encounter_started_at_ms</c>
/// returns rows. That is not a redundant assertion - the bug this stage fixed was exactly a writer
/// stamping its own <c>UtcNow</c>, which every isolated test still passed, because each one only ever
/// compared a writer against itself.</para>
///
/// <para>Wired the way <c>MuckaConnection</c> wires it: one store, one key stamped per encounter and
/// handed to every writer, one <c>CombatTracker</c> feeding the ledger and the encounter-log writer.
/// The key is fixed rather than read off a clock, since the tests assert on it.</para>
/// </summary>
public sealed class StoreJoinTests : IDisposable
{
    private const long Key = 1_787_000_000_000L;
    private static readonly DateTime T0 = new(2026, 5, 18, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mucka-storejoin-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string DbPath => Path.Combine(_directory, MuckaDb.DefaultFileName);

    private static StyledLine Line(string text) => new([new StyledSpan(text, TextStyle.Default)]);

    /// <summary>Plays one encounter through all three writers against one store, then closes the
    /// store so everything queued is committed.</summary>
    private void PlayOneEncounter()
    {
        using var store = new MuckaStore(DbPath, "test");
        var tracker = new CombatTracker();
        var ledger = new SwingLedger(store);
        var clog = new ClogWriter(store);
        var fights = new FightHistoryStore(store);

        tracker.EventOccurred += ledger.OnCombatEvent;
        tracker.EventOccurred += clog.OnCombatEvent;
        // The one stamp, to every writer - MuckaConnection.OnInCombatChanged's contract.
        tracker.InCombatChanged += inCombat =>
        {
            ledger.OnInCombatChanged(inCombat, inCombat ? Key : null);
            clog.OnInCombatChanged(inCombat, inCombat ? Key : null);
        };

        // A real login row: the fact tables carry a foreign key to it, so a fabricated id is
        // rejected outright rather than stored.
        var personaSession = store.BeginPersonaSession(Key, "test");
        ledger.OnPersonaSessionChanged(personaSession);

        var second = 0;
        foreach (var text in new[]
                 {
                     "You attack the rat0, using the axe0 as a weapon.",
                     "You hit the rat0 (15-19).",
                 })
        {
            clog.OnLineReady(Line(text));
            tracker.Observe(Line(text), T0.AddSeconds(second++));
        }

        fights.Append(new FightRecord
        {
            StartedAtMs = Key,
            EndedAtMs = Key + 5_000,
            DurationMs = 5_000,
            PersonaSessionId = personaSession,
            EncounterStartedAtMs = Key,
            NpcName = "rat0",
            NpcGroup = "rat",
            WeaponUsed = "axe0",
        });

        clog.Dispose();
    }


    private static IReadOnlyList<Dictionary<string, object?>> Query(string path, string sql)
    {
        var rows = new List<Dictionary<string, object?>>();
        using var connection = new SqliteConnection(MuckaDb.ConnectionString(path));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    [Fact]
    public void One_encounter_key_joins_the_encounter_its_fights_and_its_swings()
    {
        PlayOneEncounter();

        // The query the whole stage is for: start from the encounter row the log writer produced and
        // reach the rows the combat writers produced, on nothing but the shared key.
        var joined = Query(DbPath, """
            SELECT e.encounter_started_at_ms AS key,
                   (SELECT COUNT(*) FROM fights f WHERE f.encounter_started_at_ms = e.encounter_started_at_ms) AS fights,
                   (SELECT COUNT(*) FROM swings s WHERE s.encounter_started_at_ms = e.encounter_started_at_ms) AS swings
            FROM encounters e;
            """);

        var row = Assert.Single(joined);
        Assert.Equal(Key, Convert.ToInt64(row["key"]));
        Assert.True(Convert.ToInt64(row["fights"]) > 0,
            "the encounter joined to no fight: the writers disagree about the encounter key");
        Assert.True(Convert.ToInt64(row["swings"]) > 0,
            "the encounter joined to no swing: the writers disagree about the encounter key");
    }

    [Fact]
    public void Every_writers_rows_carry_the_same_key_and_none_stamps_its_own_clock()
    {
        // The failure this guards is specific: a writer that derives the key itself gets a value a
        // few microseconds off, which looks right in isolation and joins to nothing. Asserting the
        // literal key rather than "they match each other" is what catches all three drifting together.
        PlayOneEncounter();

        foreach (var (table, column) in new[]
                 {
                     ("encounters", "encounter_started_at_ms"),
                     ("fights", "encounter_started_at_ms"),
                     ("swings", "encounter_started_at_ms"),
                 })
        {
            var rows = Query(DbPath, $"SELECT DISTINCT {column} AS k FROM {table};");
            var only = Assert.Single(rows);
            Assert.True(Key == Convert.ToInt64(only["k"]),
                $"{table}.{column} is {only["k"]}, not the {Key} its caller supplied");
        }
    }
}
