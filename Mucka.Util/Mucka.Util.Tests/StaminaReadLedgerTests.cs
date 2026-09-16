using Microsoft.Data.Sqlite;
using MudSharp.Combat;
using MudSharp.Models;
using Mucka.Combat;
using Mucka.Store;

namespace Mucka.Util.Tests;

/// <summary>
/// Persisting the <c>diagnose</c> probe, and warming the pool and reach indexes back off the
/// database.
///
/// <para>The probe line - "The water-snake5 has a stamina lying between 90 and 99." - is the only
/// direct measurement of NPC stamina MUD2 gives; four observations exist in the whole corpus. These
/// tests drive it through the real tracker and read the DATABASE back, so the column names are under
/// test too.</para>
/// </summary>
public sealed class StaminaReadLedgerTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 8, 12, 9, 0, 0, DateTimeKind.Utc);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mucka-staminaread-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string DbPath => Path.Combine(_directory, MuckaDb.DefaultFileName);

    private static StyledLine Line(string text) => new([new StyledSpan(text, TextStyle.Default)]);

    private sealed class Session : IDisposable
    {
        private readonly CombatTracker _tracker = new();
        private readonly MuckaStore _db;
        private readonly string _path;
        private int _second;

        public Session(string path)
        {
            _path = path;
            // The store is what drains, so it is what this closes before reading rows back.
            _db = new MuckaStore(path, "test");
            Ledger = new SwingLedger(_db);
            _tracker.EventOccurred += Ledger.OnCombatEvent;
            _tracker.InCombatChanged += inCombat => Ledger.OnInCombatChanged(inCombat);
        }

        public SwingLedger Ledger { get; }

        /// <summary>Opens a login the way MuckaConnection does - a real persona_sessions row, because
        /// the fact tables carry a foreign key to it and an invented id is rejected.</summary>
        public Session PersonaSession()
        {
            PersonaSessionId = _db.BeginPersonaSession(1, "test");
            Ledger.OnPersonaSessionChanged(PersonaSessionId);
            return this;
        }

        public long? PersonaSessionId { get; private set; }

        public Session Stats(GameStatsSnapshot stats)
        {
            Ledger.OnStatsUpdated(stats);
            return this;
        }

        public Session Say(params string[] lines)
        {
            foreach (var text in lines)
                _tracker.Observe(Line(text), T0.AddSeconds(_second++));
            return this;
        }

        /// <summary>Drains the writer and reads every stamina-read row back as column-name maps.</summary>
        public IReadOnlyList<Dictionary<string, object?>> Reads()
        {
            _db.Dispose();
            if (!File.Exists(_path))
                return [];

            var rows = new List<Dictionary<string, object?>>();
            using var connection = new SqliteConnection(MuckaDb.ConnectionString(_path));
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM npc_stamina_reads ORDER BY id;";
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

        public void Dispose() => _db.Dispose();
    }

    [Fact]
    public void ADiagnoseReadingIsPersistedWithBothPrintedNumbersAndTheRawLine()
    {
        using var session = new Session(DbPath);
        session.PersonaSession()
               .Say("You attack the water-snake5, using the falchion as a weapon.",
                    "The water-snake5 has a stamina lying between 90 and 99.");

        var reads = session.Reads();

        Assert.Single(reads);
        var read = reads[0];
        Assert.Equal("water-snake5", read["npc"]);
        // NpcGroups pluralizes the LAST token, hyphens included, so a water-snake groups as "snakes"
        // alongside every other snake - right for weapon susceptibility, useless for a pool.
        Assert.Equal("snakes", read["npc_group"]);
        // The pool key keeps everything but the instance number, which is exactly what npc_group drops
        // and exactly what separates a large rat from a rat.
        Assert.Equal("water-snake", read["pool_key"]);
        Assert.Equal(90L, read["printed_low"]);
        Assert.Equal(99L, read["printed_high"]);
        Assert.Equal(session.PersonaSessionId, read["persona_session_id"]);
        Assert.Equal("The water-snake5 has a stamina lying between 90 and 99.", read["raw_text"]);
    }

    [Fact]
    public void ThePrintedNumbersAreStoredExactlyAsGivenAndNotSnappedToAGrid()
    {
        // Three observed readings, none of which agrees with the others about alignment: 117-126 spans
        // a decade boundary, 90-99 sits inside one, 18-27 crosses one too. Whether the bracket aligns
        // to tens or to a tenth of the pool is unresolved at four observations in the whole corpus, so
        // rounding here would turn those four into a rule.
        using var session = new Session(DbPath);
        session.Say("The giant snake has a stamina lying between 117 and 126.",
                    "The water-snake5 has a stamina lying between 90 and 99.",
                    "The viper has a stamina lying between 18 and 27.");

        var reads = session.Reads();

        Assert.Equal(3, reads.Count);
        Assert.Equal([117L, 90L, 18L], reads.Select(r => r["printed_low"]));
        Assert.Equal([126L, 99L, 27L], reads.Select(r => r["printed_high"]));
    }

    [Fact]
    public void AReadingTakenBeforeAnyFightIsStillRecorded()
    {
        // Diagnosing something BEFORE picking a fight with it is the point of carrying a stethoscope,
        // so the row exists with a null encounter rather than not existing.
        using var session = new Session(DbPath);
        session.Say("The ogre has a stamina lying between 200 and 209.");

        var reads = session.Reads();

        Assert.Single(reads);
        Assert.Null(reads[0]["encounter_started_at_ms"]);
        Assert.Equal("ogre", reads[0]["npc"]);
    }

    [Fact]
    public void TheReadingIsInterleavedWithSwingsRatherThanQueuedSeparately()
    {
        // Both row kinds go down one channel so the ordering between a swing and a probe taken in the
        // same breath survives. This asserts the swing rows are written too - a second queue that
        // silently swallowed one kind would pass every other test here.
        using var session = new Session(DbPath);
        session.Stats(new GameStatsSnapshot(Stamina: 100, MaxStamina: 100))
               .Say("You attack the water-snake5, using the falchion as a weapon.",
                    "You hit the water-snake5 (5-9).",
                    "The water-snake5 has a stamina lying between 90 and 99.",
                    "You hit the water-snake5 (5-9).");

        Assert.Single(session.Reads());

        using var connection = new SqliteConnection(MuckaDb.ConnectionString(DbPath));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM swings WHERE dir = 'out' AND hit = 1;";
        Assert.Equal(2L, command.ExecuteScalar());
    }

    // -- Warming the indexes back off the database --------------------------------

    [Fact]
    public async Task WarmRebuildsThePoolBandFromTheStoredSwingsAndFightRows()
    {
        // End to end: the swing ledger stores the brackets and the rungs, the fight store records how
        // the fight ENDED, and the estimator needs both. A join that misses either produces a pool
        // estimate from nothing, which is the failure this covers.
        var startedAt = new DateTimeOffset(T0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        using (var session = new Session(DbPath))
        {
            session.Stats(new GameStatsSnapshot(Stamina: 100, MaxStamina: 100))
                   .Say("You attack the rat0, using the falchion as a weapon.",
                        "You hit the rat0 (10-14).",
                        "You hit the rat0 (10-14).",
                        "You hit the rat0 (10-14).",
                        "You have killed the rat0.");
            session.Reads();   // drains the writer
        }

        var fightsDb = new MuckaStore(DbPath, "test");
        new FightHistoryStore(fightsDb).Append(new FightRecord
        {
            StartedAtMs = startedAt,
            EndedAtMs = startedAt + 10_000,
            DurationMs = 10_000,
            NpcName = "rat0",
            NpcGroup = "rats",
            Outcome = nameof(FightOutcome.Kill),
            YouHits = 3,
        });
        fightsDb.Dispose();

        var reloadedDb = new MuckaStore(DbPath, "test");
        var reloaded = new SwingLedger(reloadedDb);
        try
        {
            await reloaded.WarmDamageIndexAsync();

            var estimate = reloaded.Pool.Lookup("rat0");
            Assert.Equal(PoolEvidence.Band, estimate.Evidence);
            // Alive after two blows, dead on the third: pool > 20 and pool <= 42.
            Assert.Equal(20, estimate.Interval.Above, 6);
            Assert.Equal(42, estimate.Interval.AtMost!.Value, 6);
            Assert.Equal(1, estimate.SupportingFights);
            Assert.Equal(PoolQuantity.StaminaPool, estimate.Quantity);
        }
        finally
        {
            reloadedDb.Dispose();
        }
    }

    [Theory]
    [InlineData(nameof(FightOutcome.Kill), true)]
    [InlineData(nameof(FightOutcome.NoMore), false)]
    [InlineData(nameof(FightOutcome.CFled), false)]
    [InlineData(nameof(FightOutcome.CFledFail), false)]
    [InlineData(nameof(FightOutcome.Withdraw), false)]
    [InlineData(nameof(FightOutcome.UFled), false)]
    [InlineData(nameof(FightOutcome.Died), false)]
    [InlineData(nameof(FightOutcome.EndOther), false)]
    [InlineData(nameof(FightOutcome.Unresolved), false)]
    public async Task OnlyAKillCeilingsThePool(string outcome, bool expectCeiling)
    {
        // SwingLedger maps fights.outcome to PoolFightRow.EndedInKill with an exact ordinal match on
        // "Kill", and that single comparison is what decides whether a row can bound the pool from
        // ABOVE.
        //
        // NoMore is the case that matters: the creature died of poison, so the damage that finished it
        // never crossed the wire. Reading it as a kill would hand the estimator a ceiling built from a
        // total missing its largest term - a pool far smaller than the truth, in the direction that
        // makes a fight read as easier to win.
        var startedAt = new DateTimeOffset(T0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        using (var session = new Session(DbPath))
        {
            session.Stats(new GameStatsSnapshot(Stamina: 100, MaxStamina: 100))
                   .Say("You attack the rat0, using the falchion as a weapon.",
                        "You hit the rat0 (10-14).",
                        "You hit the rat0 (10-14).",
                        "You hit the rat0 (10-14).");
            session.Reads();
        }

        var fightsDb = new MuckaStore(DbPath, "test");
        new FightHistoryStore(fightsDb).Append(new FightRecord
        {
            StartedAtMs = startedAt,
            EndedAtMs = startedAt + 10_000,
            DurationMs = 10_000,
            NpcName = "rat0",
            NpcGroup = "rats",
            Outcome = outcome,
            YouHits = 3,
        });
        fightsDb.Dispose();

        var reloadedDb = new MuckaStore(DbPath, "test");
        var reloaded = new SwingLedger(reloadedDb);
        try
        {
            await reloaded.WarmDamageIndexAsync();
            var estimate = reloaded.Pool.Lookup("rat0");

            if (expectCeiling)
            {
                Assert.Equal(PoolEvidence.Band, estimate.Evidence);
                Assert.Equal(42, estimate.Interval.AtMost!.Value, 6);
            }
            else
            {
                // A floor and nothing more. Every one of these outcomes leaves the creature's pool
                // unbounded above, because none of them says the player's own blow finished it.
                Assert.Equal(PoolEvidence.LowerBoundOnly, estimate.Evidence);
                Assert.Null(estimate.Interval.AtMost);
                Assert.Equal(30, estimate.Interval.Above, 6);
            }
        }
        finally
        {
            reloadedDb.Dispose();
        }
    }

    [Fact]
    public async Task WarmRebuildsReachMarksFoldedUpToTheSpecies()
    {
        using (var session = new Session(DbPath))
        {
            session.Stats(new GameStatsSnapshot(Stamina: 100, MaxStamina: 100))
                   .Say("You attack the rat0, using the falchion as a weapon.")
                   .Stats(new GameStatsSnapshot(Stamina: 96, MaxStamina: 100))
                   .Say("The rat0 hits you (96/100).")
                   .Stats(new GameStatsSnapshot(Stamina: 89, MaxStamina: 100))
                   .Say("The rat0 hits you (89/100).");
            session.Reads();
        }

        var reloadedDb = new MuckaStore(DbPath, "test");
        var reloaded = new SwingLedger(reloadedDb);
        try
        {
            await reloaded.WarmDamageIndexAsync();

            var mark = reloaded.Reach.Lookup("rat0");
            Assert.True(mark.HasEvidence);
            Assert.Equal(7, mark.ReachesAtLeast);    // the worse of the two blows
            Assert.Equal(2, mark.Blows);
            // Keyed on the species, so an instance never fought inherits it - and a large rat does not.
            Assert.Equal(7, reloaded.Reach.Lookup("rat9").ReachesAtLeast);
            Assert.False(reloaded.Reach.Lookup("large rat0").HasEvidence);
        }
        finally
        {
            reloadedDb.Dispose();
        }
    }

    [Fact]
    public async Task WarmingAnEmptyDatabaseLeavesTheIndexesEmptyRatherThanThrowing()
    {
        var ledgerDb = new MuckaStore(DbPath, "test");
        var ledger = new SwingLedger(ledgerDb);
        try
        {
            await ledger.WarmDamageIndexAsync();
            Assert.False(ledger.Pool.Lookup("rat0").HasEvidence);
            Assert.False(ledger.Reach.Lookup("rat0").HasEvidence);
        }
        finally
        {
            ledgerDb.Dispose();
        }
    }
}
