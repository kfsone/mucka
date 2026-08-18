using Microsoft.Data.Sqlite;
using Mucka.Core;
using MudSharp.Combat;
using MudSharp.Models;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// The per-swing ledger (tools/combat/SWING-LEDGER-SPEC.md sections 2/3/6): one row per swing in the
/// combat database, both directions, written from real game lines.
///
/// <para>Driven end to end through <see cref="CombatTracker"/> rather than by hand-built
/// <see cref="CombatEvent"/>s, because half of what the ledger has to get right is a consequence of
/// the ORDER MUD2 prints things in - the health descriptor arriving on the line after the blow it
/// describes, and the incoming hit line being parsed for stats before it is classified as a hit.
/// Every quoted line here is the shape the tracker's own fixtures use; none is invented to be
/// convenient.</para>
///
/// <para>Assertions read the DATABASE back rather than an in-memory projection, so the column names
/// are under test too - they are the schema the analysis view and the offline tooling consume, not an
/// implementation detail.</para>
/// </summary>
public sealed class SwingLedgerTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 8, 12, 9, 0, 0, DateTimeKind.Utc);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mucka-swingledger-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        // Pooled connections keep the file handle open, which on Windows blocks the delete below.
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static StyledLine Line(string text) => new([new StyledSpan(text, TextStyle.Default)]);

    /// <summary>A tracker feeding a ledger, wired exactly as MuckaConnection wires them. Stats are
    /// injected by hand because the "(cur/max)" scan that fires StatsUpdated lives in
    /// GameLineAnalyzer/MudSession, above the tracker - see <see cref="Session.Stats"/>.</summary>
    private sealed class Session : IDisposable
    {
        private readonly CombatTracker _tracker = new();
        private readonly SwingLedger _ledger;
        private readonly string _path;
        private int _second;

        public Session(string directory)
        {
            _path = Path.Combine(directory, CombatDb.DefaultFileName);
            _ledger = new SwingLedger(_path);
            _tracker.EventOccurred += _ledger.OnCombatEvent;
            // Wrapped rather than assigned directly: OnInCombatChanged takes the shared encounter id
            // too (MuckaConnection supplies it in production), which the tracker's own event does not
            // carry. These tests are about swing content, so the id is left to its default.
            _tracker.InCombatChanged += inCombat => _ledger.OnInCombatChanged(inCombat);
        }

        public Session Persona(string name)
        {
            _ledger.OnCharacterIdentified(name);
            return this;
        }

        /// <summary>A stats reading, as the FES heartbeat or an inline "(cur/max)" would deliver it.
        /// Call this immediately BEFORE the line that carried it: MudStreamParser raises StatsUpdated
        /// ahead of LineReady for the same line, and the ledger's damage relay depends on that
        /// ordering.</summary>
        public Session Stats(GameStatsSnapshot stats)
        {
            _ledger.OnStatsUpdated(stats);
            return this;
        }

        public Session Effects(StatusEffectState effects)
        {
            _ledger.OnStatusEffectsChanged(effects);
            return this;
        }

        public Session Say(params string[] lines)
        {
            foreach (var text in lines)
                _tracker.Observe(Line(text), T0.AddSeconds(_second++));
            return this;
        }

        /// <summary>Closes the ledger (draining its background writer) and reads every row back, in
        /// insertion order, as column-name to value maps.</summary>
        public IReadOnlyList<Dictionary<string, object?>> Rows()
        {
            _ledger.Dispose();
            if (!File.Exists(_path))
                return [];

            var rows = new List<Dictionary<string, object?>>();
            using var connection = new SqliteConnection(CombatDb.ConnectionString(_path));
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM swings ORDER BY id;";
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

        /// <summary>The swings table's columns, in declaration order - the schema is the deliverable
        /// now that a query is what reads this, so a rename is a breaking change and belongs under
        /// test the same way the JSONL field names were.</summary>
        public IReadOnlyList<string> Columns()
        {
            _ledger.Dispose();
            using var connection = CombatDb.Open(_path);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM swings LIMIT 0;";
            using var reader = command.ExecuteReader();
            return Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
        }

        /// <summary>The ledger itself, for the tests that assert on the damage index rather than on
        /// the file. Reading it does NOT drain the writer, unlike <see cref="Rows"/>.</summary>
        public SwingLedger Ledger => _ledger;

        public void Dispose() => _ledger.Dispose();
    }

    private static int? Int(Dictionary<string, object?> row, string name)
        => row[name] is null ? null : Convert.ToInt32(row[name]);

    private static string? Str(Dictionary<string, object?> row, string name)
        => row[name] as string;

    private static bool Flag(Dictionary<string, object?> row, string name)
        => row[name] is not null && Convert.ToInt64(row[name]) != 0;

    // ---- Row shape -------------------------------------------------------------------------

    /// <summary>The column names ARE the deliverable: the analysis view and tools/combat query this
    /// table, so a rename silently breaks them. Pins the full set.</summary>
    [Fact]
    public void SwingsTable_HasExactlyTheExpectedColumns()
    {
        using var session = new Session(_directory);

        Assert.Equal(
            ["id", "ts", "dir", "encounter_started_at_ms", "persona", "sex",
             "sta", "sta_before", "sta_max",
             "str", "str_raw", "str_max", "dex", "dex_raw", "dex_max",
             "level", "score", "objects_carried", "weather",
             "blind", "deaf", "crippled", "dumb",
             "str_buff", "str_debuff", "dex_buff", "dex_debuff", "sta_buff", "sta_debuff", "glow",
             "time_to_reset", "reset_epoch_ms",
             "npc", "npc_group", "npc_weapon", "rung", "rung_phrase",
             "weapon", "hit", "dmg_low", "dmg_high", "dmg"],
            session.Columns());
    }

    [Fact]
    public void Row_CarriesTheIdentifyingContext()
    {
        using var session = new Session(_directory).Persona("Ollie");
        session.Stats(new GameStatsSnapshot(
                    Stamina: 81, MaxStamina: 105, Strength: 94, Dexterity: 99, Sex: "male"))
               .Say("You attack the rat0, using the axe0 as a weapon.",
                    "You hit the rat0 (15-19).");

        var row = Assert.Single(session.Rows());
        Assert.Equal("Ollie", Str(row, "persona"));
        Assert.Equal("rat0", Str(row, "npc"));
        Assert.Equal("rats", Str(row, "npc_group"));
        // SWING-LEDGER-SPEC.md section 3 says sex is unobtainable for an existing character and to
        // ship the field null. That note is stale: GameStatsSnapshot.Sex parses it off the score
        // sheet, so it is recorded rather than left blank.
        Assert.Equal("male", Str(row, "sex"));
    }

    /// <summary>The dimensions that make a baseline sliceable: which reset this happened in, and what
    /// was buffing or debuffing the player at the time. MUD2's creatures level up within a reset and
    /// the player's own effects move both damage and hit chance, so a corpus that cannot separate
    /// those is a corpus of blended averages.</summary>
    [Fact]
    public void Row_CarriesTheResetAndEffectContext()
    {
        using var session = new Session(_directory);
        session.Effects(new StatusEffectState(StrengthDebuff: true, DexterityBuff: true))
               .Stats(new GameStatsSnapshot(Stamina: 81, Level: 4, Score: 1200, TimeToReset: 600))
               .Say("You attack the rat0, using the axe0 as a weapon.",
                    "You hit the rat0 (15-19).");

        var row = Assert.Single(session.Rows());
        Assert.Equal(600, Int(row, "time_to_reset"));
        // The reset's END instant: constant across every swing of one reset, which is what makes it
        // the key to group by. ts is T0+1s here, and the countdown is in seconds.
        Assert.Equal(
            new DateTimeOffset(T0.AddSeconds(1), TimeSpan.Zero).ToUnixTimeMilliseconds() + 600_000,
            Convert.ToInt64(row["reset_epoch_ms"]));
        Assert.Equal(4, Int(row, "level"));
        Assert.Equal(1200, Int(row, "score"));
        Assert.True(Flag(row, "str_debuff"));
        Assert.True(Flag(row, "dex_buff"));
        Assert.False(Flag(row, "str_buff"));
    }

    [Fact]
    public void Row_IsStampedWithTheEventsOwnTimestamp()
    {
        using var session = new Session(_directory);
        session.Say("You attack the rat0, using the axe0 as a weapon.",   // T0
                    "You hit the rat0 (15-19).");                          // T0 + 1s

        var row = Assert.Single(session.Rows());
        Assert.Equal(
            new DateTimeOffset(T0.AddSeconds(1), TimeSpan.Zero).ToUnixTimeMilliseconds(),
            Convert.ToInt64(row["ts"]));
    }

    // ---- Outgoing: a bracket, never an exact figure ------------------------------------------

    [Fact]
    public void OutgoingHit_RecordsTheGamesBracketAndNoExactDamage()
    {
        using var session = new Session(_directory);
        session.Stats(new GameStatsSnapshot(Stamina: 81, MaxStamina: 105, Strength: 94, Dexterity: 99))
               .Say("You attack the rat0, using the axe0 as a weapon.",
                    "You hit the rat0 (15-19).");

        var row = Assert.Single(session.Rows());
        Assert.Equal("out", Str(row, "dir"));
        Assert.True(Flag(row, "hit"));
        Assert.Equal(15, Int(row, "dmg_low"));
        Assert.Equal(19, Int(row, "dmg_high"));
        // MUD2 never gives the player an exact figure for their own blow; a midpoint here would be
        // a fabricated reading indistinguishable from a measured one.
        Assert.Null(Int(row, "dmg"));
        Assert.Equal("axe0", Str(row, "weapon"));
        // Effective, not raw - these are what the hit-chance and damage formulas consume.
        Assert.Equal(94, Int(row, "str"));
        Assert.Equal(99, Int(row, "dex"));
        Assert.Equal(81, Int(row, "sta"));
        Assert.Equal(105, Int(row, "sta_max"));
    }

    // ---- Incoming: an exact figure, never a bracket ------------------------------------------

    /// <summary>The incoming line carries POST-hit stamina, not a delta, and the same line is parsed
    /// twice (stats scan, then combat classifier). Without the one-shot relay the baseline has already
    /// advanced by the time the delta is computed and every blow records 0 - which is what shipped
    /// once, most visibly on single-hit fights. Two consecutive hits here so a stale relay cannot pass
    /// by luck.</summary>
    [Fact]
    public void IncomingHit_RecordsExactDamageAndNoBracket()
    {
        using var session = new Session(_directory);
        session.Stats(new GameStatsSnapshot(Stamina: 100, MaxStamina: 100))
               .Say("The zombie2 attacks you.")
               .Stats(new GameStatsSnapshot(Stamina: 93, MaxStamina: 100))
               .Say("The zombie2 hits you (93/100).")
               .Stats(new GameStatsSnapshot(Stamina: 89, MaxStamina: 100))
               .Say("The zombie2 hits you (89/100).");

        var rows = session.Rows();
        Assert.Equal(2, rows.Count);

        Assert.Equal("in", Str(rows[0], "dir"));
        Assert.Equal(7, Int(rows[0], "dmg"));
        Assert.Equal(4, Int(rows[1], "dmg"));
        // "(93/100)" is the player's current/max stamina, not a damage bracket - crossing the two
        // over would fill the outgoing damage columns with stamina readings.
        Assert.Null(Int(rows[0], "dmg_low"));
        Assert.Null(Int(rows[0], "dmg_high"));
        Assert.Equal("zombie2", Str(rows[0], "npc"));
        Assert.Equal("zombies", Str(rows[0], "npc_group"));
    }

    /// <summary>Pre-hit stamina is stored, not left to be reconstructed as <c>sta + dmg</c>. The two
    /// agree whenever both are present, which is exactly why the field has to be tested rather than
    /// assumed: the case it exists for is the one where <c>dmg</c> is null (see the null test below),
    /// and a consumer doing the arithmetic would silently produce nothing there.</summary>
    [Fact]
    public void IncomingHit_RecordsStaminaAsItWasBeforeTheBlow()
    {
        using var session = new Session(_directory);
        session.Stats(new GameStatsSnapshot(Stamina: 100, MaxStamina: 100))
               .Say("The zombie2 attacks you.")
               .Stats(new GameStatsSnapshot(Stamina: 93, MaxStamina: 100))
               .Say("The zombie2 hits you (93/100).");

        var row = session.Rows().Single(r => Flag(r, "hit"));
        Assert.Equal(100, Int(row, "sta_before"));
        // "sta" stays the POST-hit reading it has always been - the new field adds a fact, it does
        // not redefine an existing one, or every row already on disk would change meaning.
        Assert.Equal(93, Int(row, "sta"));
        Assert.Equal(7, Int(row, "dmg"));
    }

    /// <summary>No baseline means no damage AND no pre-hit figure. Writing the baseline beside a null
    /// dmg would invite exactly the subtraction the field exists to replace.</summary>
    [Fact]
    public void IncomingHit_WithNoBaseline_RecordsNeitherDamageNorPreHitStamina()
    {
        using var session = new Session(_directory);
        // No Stats() at all: nothing has ever reported the player's stamina, so there is nothing to
        // diff the "(93/100)" against.
        session.Say("The zombie2 attacks you.",
                    "The zombie2 hits you (93/100).");

        var row = session.Rows().Single(r => Flag(r, "hit"));
        Assert.Null(Int(row, "dmg"));
        Assert.Null(Int(row, "sta_before"));
    }

    /// <summary>Outgoing swings have no pre-hit stamina to report - the player's own blow does not
    /// move their stamina, and the field must not quietly become "stamina, again".</summary>
    [Fact]
    public void OutgoingSwing_HasNoPreHitStamina()
    {
        using var session = new Session(_directory);
        session.Stats(new GameStatsSnapshot(Stamina: 81, MaxStamina: 105))
               .Say("You attack the rat0, using the axe0 as a weapon.",
                    "You hit the rat0 (15-19).");

        Assert.Null(Int(Assert.Single(session.Rows()), "sta_before"));
    }

    // ---- The damage index behind the rail's "ever" figures ------------------------------------

    /// <summary>The whole point of buffering the encounter: while a fight is live, its own blows must
    /// not be showing back as that creature's history. Otherwise the rail's "now" and "ever" rows are
    /// two renderings of the same swings, at n=1, presented as a comparison.
    ///
    /// <para>This is the swing-level twin of the guarantee CombatHistoryCache establishes for the
    /// fight-level index, and it is established the same way - by not folding the data in at all until
    /// the encounter closes, rather than by filtering it out afterwards.</para></summary>
    [Fact]
    public void DamageIndex_DoesNotSeeTheEncounterItIsStillIn()
    {
        using var session = new Session(_directory);
        session.Stats(new GameStatsSnapshot(Stamina: 100, MaxStamina: 100))
               .Say("The zombie2 attacks you.")
               .Stats(new GameStatsSnapshot(Stamina: 93, MaxStamina: 100))
               .Say("The zombie2 hits you (93/100).")
               .Stats(new GameStatsSnapshot(Stamina: 89, MaxStamina: 100))
               .Say("The zombie2 hits you (89/100).")
               .Stats(new GameStatsSnapshot(Stamina: 80, MaxStamina: 100))
               .Say("The zombie2 hits you (80/100).");

        Assert.False(session.Ledger.Damage.Lookup("zombie2").Incoming.HasSamples);

        // ...and lands the moment the encounter closes — CombatTracker closes it immediately,
        // right on this same Say() call, once the kill line empties its combatant count.
        session.Say("You have killed the zombie2.");

        var profile = session.Ledger.Damage.Lookup("zombie2").Incoming;
        Assert.Equal(3, profile.Samples);
        Assert.Equal(9, profile.Max);                    // the 89 -> 80 blow
        Assert.Equal((7 + 4 + 9) / 3.0, profile.Average);
    }

    /// <summary>The player's own output is profiled too, as RANGES. MUD2 reports a blow as "15-19"
    /// and never as a number, so the average comes out as a range as well - averaging the midpoints
    /// would throw away error bars at the one moment they could still be narrowed, which is the plan
    /// (a diagnose reading gives a known hitpoint band; kill-total arithmetic constrains a fight's
    /// blows). Both work on stored ranges and neither can recover a midpoint.</summary>
    [Fact]
    public void DamageIndex_ProfilesThePlayersOwnOutputAsRanges()
    {
        using var session = new Session(_directory);
        session.Say("You attack the zombie2, using the axe0 as a weapon.",
                    "You hit the zombie2 (15-19).",
                    "You hit the zombie2 (5-9).",
                    "You hit the zombie2 (10-14).",
                    "You have killed the zombie2.");

        var outgoing = session.Ledger.Damage.Lookup("zombie2").Outgoing;
        Assert.Equal(3, outgoing.Samples);
        Assert.Equal(10.0, outgoing.AverageLow);    // (15 + 5 + 10) / 3
        Assert.Equal(14.0, outgoing.AverageHigh);   // (19 + 9 + 14) / 3
        // The highest UPPER bound - "the most this could have been", which is the honest reading of a
        // range and the one that survives the brackets later being narrowed from above.
        Assert.Equal(19.0, outgoing.Max);
    }

    /// <summary>Both directions accumulate independently, and a live encounter is excluded from both.
    /// The player swings at things that never land a blow and vice versa, so forcing one direction's
    /// sample scope onto the other would discard the better evidence for no reason.</summary>
    [Fact]
    public void DamageIndex_KeepsTheTwoDirectionsSeparate()
    {
        using var session = new Session(_directory);
        session.Stats(new GameStatsSnapshot(Stamina: 100, MaxStamina: 100))
               .Say("You attack the zombie2, using the axe0 as a weapon.",
                    "You hit the zombie2 (15-19).")
               .Stats(new GameStatsSnapshot(Stamina: 93, MaxStamina: 100))
               .Say("The zombie2 hits you (93/100).")
               .Say("You have killed the zombie2.");

        var damage = session.Ledger.Damage.Lookup("zombie2");
        // One blow each way. Below MinimumSamples, so Lookup reports nothing rather than dressing a
        // single observation up as a distribution - on BOTH sides, independently.
        Assert.False(damage.Incoming.HasSamples);
        Assert.False(damage.Outgoing.HasSamples);
    }

    /// <summary>A rebuild reads the same evidence the live path folded in, and must agree with it -
    /// the index is a projection of the swings table, so a restart cannot change what history says.</summary>
    [Fact]
    public async Task DamageIndex_RebuiltFromTheLedger_MatchesTheLivePath()
    {
        using var session = new Session(_directory);
        session.Stats(new GameStatsSnapshot(Stamina: 100, MaxStamina: 100))
               .Say("The zombie2 attacks you.")
               .Stats(new GameStatsSnapshot(Stamina: 93, MaxStamina: 100))
               .Say("The zombie2 hits you (93/100).")
               .Stats(new GameStatsSnapshot(Stamina: 89, MaxStamina: 100))
               .Say("The zombie2 hits you (89/100).")
               .Stats(new GameStatsSnapshot(Stamina: 80, MaxStamina: 100))
               .Say("The zombie2 hits you (80/100).")
               .Say("You have killed the zombie2.");

        var live = session.Ledger.Damage.Lookup("zombie2").Incoming;
        // Asserted up front so the comparison below cannot pass by both sides being empty - three
        // blows is exactly SwingDamageIndex.MinimumSamples, and two would make this test vacuous.
        Assert.Equal(3, live.Samples);
        session.Rows();   // drains the writer, so every row is actually on disk

        var reloaded = new SwingLedger(Path.Combine(_directory, CombatDb.DefaultFileName));
        try
        {
            await reloaded.WarmDamageIndexAsync();
            var rebuilt = reloaded.Damage.Lookup("zombie2").Incoming;
            Assert.Equal(live.Samples, rebuilt.Samples);
            Assert.Equal(live.Max, rebuilt.Max);
            Assert.Equal(live.Average, rebuilt.Average);
        }
        finally
        {
            reloaded.Dispose();
        }
    }

    /// <summary>Regen between blows revises the baseline; the delta must be measured against what
    /// stamina actually was, not against the last hit's figure.</summary>
    [Fact]
    public void IncomingHit_MeasuresAgainstStaminaAsItWasImmediatelyBefore_IncludingRegen()
    {
        using var session = new Session(_directory);
        session.Stats(new GameStatsSnapshot(Stamina: 60, MaxStamina: 100))
               .Say("The zombie2 attacks you.")
               .Stats(new GameStatsSnapshot(Stamina: 70, MaxStamina: 100))   // heal/regen on its own line
               .Say("The zombie2 misses you.")
               .Stats(new GameStatsSnapshot(Stamina: 64, MaxStamina: 100))
               .Say("The zombie2 hits you (64/100).");

        var hit = session.Rows().Single(r => Flag(r, "hit"));
        Assert.Equal(6, Int(hit, "dmg"));
    }

    /// <summary>The NPC arms itself independently of the player, and it changes what it does to you -
    /// so the two weapons get two COLUMNS rather than sharing one that means different things by
    /// direction.
    ///
    /// <para>The old single field forced a choice on every row and made the player's weapon
    /// unrecoverable from an incoming swing. What you were holding when something hit you is exactly
    /// as much a condition of that blow as what you were holding when you landed one, so both sides
    /// carry both facts now.</para></summary>
    [Fact]
    public void EachSwing_CarriesBothTheCreaturesWeaponAndThePlayers()
    {
        using var session = new Session(_directory);
        session.Say("You attack the zombie2, using the axe0 as a weapon.",
                    "The zombie2 has started to use the fork to fight!",
                    "The zombie2 misses you.",
                    "You miss the zombie2.");

        var rows = session.Rows();
        var incoming = rows.Single(r => Str(r, "dir") == "in");
        var outgoing = rows.Single(r => Str(r, "dir") == "out");

        Assert.Equal("fork", Str(incoming, "npc_weapon"));
        Assert.Equal("axe0", Str(incoming, "weapon"));
        Assert.Equal("fork", Str(outgoing, "npc_weapon"));
        Assert.Equal("axe0", Str(outgoing, "weapon"));
    }

    // ---- Misses are swings too ----------------------------------------------------------------

    /// <summary>A miss is the denominator of every hit-rate question the ledger exists to answer, so
    /// it is a row like any other - just one with hit:false and no damage on either side.</summary>
    [Fact]
    public void Misses_AreRecordedInBothDirectionsWithNoDamage()
    {
        using var session = new Session(_directory);
        session.Say("You attack the rat0, using the axe0 as a weapon.",
                    "You miss the rat0.",
                    "The rat0 misses you.");

        var rows = session.Rows();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.False(Flag(r, "hit"));
            Assert.Null(Int(r, "dmg_low"));
            Assert.Null(Int(r, "dmg_high"));
            Assert.Null(Int(r, "dmg"));
        });
        Assert.Equal("out", Str(rows[0], "dir"));
        Assert.Equal("in", Str(rows[1], "dir"));
    }

    // ---- The rung is the state the swing was aimed at -----------------------------------------

    /// <summary>MUD2 prints the health descriptor on the line AFTER the blow that caused it. Recording
    /// the reading that arrives next would attribute the wound to the swing that had not landed yet,
    /// and "does a wounded creature hit softer" would be answered against the wrong row.</summary>
    [Fact]
    public void HealthRung_IsTheReadingFromBeforeTheSwing_NotTheOneItCaused()
    {
        using var session = new Session(_directory);
        session.Say("You attack the zombie2, using the axe0 as a weapon.",
                    "You hit the zombie2 (20-29).",              // nothing known yet
                    "The zombie2 looks moderately damaged.",     // rung 4
                    "You hit the zombie2 (20-29).",              // aimed at a rung-4 creature
                    "The zombie2 looks critically injured.",     // rung 2
                    "You hit the zombie2 (20-29).");             // aimed at a rung-2 creature

        var rows = session.Rows();
        Assert.Equal(3, rows.Count);
        Assert.Null(Int(rows[0], "rung"));
        Assert.Null(Str(rows[0], "rung_phrase"));
        Assert.Equal(4, Int(rows[1], "rung"));
        Assert.Equal("moderately damaged", Str(rows[1], "rung_phrase"));
        Assert.Equal(2, Int(rows[2], "rung"));
        Assert.Equal("critically injured", Str(rows[2], "rung_phrase"));
    }

    /// <summary>Rungs are per-creature: a pack fight must not smear one creature's wounds across its
    /// packmates.</summary>
    [Fact]
    public void HealthRung_IsAttributedToTheCreatureItWasReportedFor()
    {
        using var session = new Session(_directory);
        session.Say("You attack the rat1, using the axe0 as a weapon.",
                    "The rat2 attacks you.",
                    "You hit the rat1 (5-9).",
                    "The rat1 looks seriously injured.",
                    "You hit the rat2 (1-4).",
                    "You miss the rat1.",
                    "You miss the rat2.");

        var rows = session.Rows();
        var lastRat1 = rows.Last(r => Str(r, "npc") == "rat1");
        var lastRat2 = rows.Last(r => Str(r, "npc") == "rat2");

        Assert.Equal(3, Int(lastRat1, "rung"));
        Assert.Null(Int(lastRat2, "rung"));
    }

    // ---- Missing context is recorded, never a reason to drop a swing --------------------------

    /// <summary>A swing landing before the first heartbeat is still evidence about the swing rhythm,
    /// the opponent and the outcome. Dropping it would bias the corpus toward the well-instrumented
    /// middle of a session and quietly understate early-fight activity.</summary>
    [Fact]
    public void SwingWithNoStatsYet_IsStillRecorded_WithNullsRatherThanZeros()
    {
        using var session = new Session(_directory);
        session.Say("You attack the rat0, using the axe0 as a weapon.",
                    "You hit the rat0 (15-19).");

        var row = Assert.Single(session.Rows());
        Assert.Null(Int(row, "sta"));
        Assert.Null(Int(row, "str"));
        Assert.Null(Int(row, "dex"));
        Assert.Null(Int(row, "sta_max"));
        Assert.Null(Str(row, "persona"));
        // The swing itself is intact - that is the whole point of keeping the row.
        Assert.Equal(15, Int(row, "dmg_low"));
        Assert.Equal("rat0", Str(row, "npc"));
    }

    /// <summary>No baseline means no honest damage figure. A 0 would read as "armour soaked it",
    /// which is a different fact, and it would drag every damage average toward zero.</summary>
    [Fact]
    public void IncomingHit_WithNoStaminaBaseline_RecordsNullDamageNotZero()
    {
        using var session = new Session(_directory);
        session.Say("The zombie2 attacks you.",
                    "The zombie2 hits you (93/100).");

        var row = Assert.Single(session.Rows());
        Assert.True(Flag(row, "hit"));
        Assert.Null(Int(row, "dmg"));
    }

    // ---- Weapon tracking ----------------------------------------------------------------------

    /// <summary>"You are now using the X to fight!" names no NPC, so it cannot open an encounter -
    /// and against something already engaging you it is the only line printed. Without the latch the
    /// whole fight's rows read as bare-handed, which is how a broadsword fight got recorded as
    /// unarmed in the fight history.</summary>
    [Fact]
    public void OutgoingSwing_AdoptsAWeaponEquippedJustBeforeTheFightWasNoticed()
    {
        using var session = new Session(_directory);
        session.Say("You are now using the broadsword to fight!",
                    "The zombie2 hits you (93/100).",
                    "You hit the zombie2 (20-29).");

        var outgoing = session.Rows().Single(r => Str(r, "dir") == "out");
        Assert.Equal("broadsword", Str(outgoing, "weapon"));
    }

    /// <summary>Once the weapon leaves the player's hands every subsequent swing is bare-handed, and
    /// the rows must say so - the owner lost a weapon mid-fight and the readout went on showing it.
    /// </summary>
    [Fact]
    public void OutgoingSwing_AfterTheWeaponBreaks_IsRecordedUnarmed()
    {
        using var session = new Session(_directory);
        session.Say("You attack the rat0, using the axe0 as a weapon.",
                    "You hit the rat0 (15-19).",
                    "The axe0 breaks to bits.",
                    "You miss the rat0.");

        var rows = session.Rows();
        Assert.Equal(2, rows.Count);
        Assert.Equal("axe0", Str(rows[0], "weapon"));
        Assert.Null(Str(rows[1], "weapon"));
    }

    // ---- Non-swing events produce nothing ------------------------------------------------------

    /// <summary>Only the four swing kinds produce rows. Everything else the tracker classifies is
    /// context the ledger consumes without emitting, or fights.jsonl's business.</summary>
    [Fact]
    public void NonSwingEvents_ProduceNoRows()
    {
        using var session = new Session(_directory);
        session.Say("You attack the rat0, using the axe0 as a weapon.",
                    "You are now using the axe0 to fight!",
                    "The rat0 looks moderately damaged.",
                    "You have killed the rat0.");

        Assert.Empty(session.Rows());
    }
}
