using System.Text.Json;
using Mucka.Core;
using MudSharp.Combat;
using MudSharp.Models;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// The per-swing ledger (tools/combat/SWING-LEDGER-SPEC.md sections 2/3/6): one JSONL row per swing,
/// both directions, written from real game lines.
///
/// <para>Driven end to end through <see cref="CombatTracker"/> rather than by hand-built
/// <see cref="CombatEvent"/>s, because half of what the ledger has to get right is a consequence of
/// the ORDER MUD2 prints things in - the health descriptor arriving on the line after the blow it
/// describes, and the incoming hit line being parsed for stats before it is classified as a hit.
/// Every quoted line here is the shape the tracker's own fixtures use; none is invented to be
/// convenient.</para>
///
/// <para>Assertions read the file back rather than an in-memory projection, so the field NAMES are
/// under test too - they are the wire format the offline ingester consumes, not an implementation
/// detail.</para>
/// </summary>
public sealed class SwingLedgerTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 8, 12, 9, 0, 0, DateTimeKind.Utc);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mucka-swingledger-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
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
            _path = Path.Combine(directory, SwingLedger.DefaultFileName);
            _ledger = new SwingLedger(_path);
            _tracker.EventOccurred += _ledger.OnCombatEvent;
            _tracker.InCombatChanged += _ledger.OnInCombatChanged;
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

        public Session Say(params string[] lines)
        {
            foreach (var text in lines)
                _tracker.Observe(Line(text), T0.AddSeconds(_second++));
            return this;
        }

        /// <summary>Closes the ledger (draining its background writer) and reads every row back.</summary>
        public IReadOnlyList<JsonElement> Rows()
        {
            _ledger.Dispose();
            if (!File.Exists(_path))
                return [];
            return File.ReadAllLines(_path)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => JsonDocument.Parse(line).RootElement)
                .ToList();
        }

        public void Dispose() => _ledger.Dispose();
    }

    private static int? Int(JsonElement row, string name)
        => row.GetProperty(name).ValueKind == JsonValueKind.Null ? null : row.GetProperty(name).GetInt32();

    private static string? Str(JsonElement row, string name)
        => row.GetProperty(name).ValueKind == JsonValueKind.Null ? null : row.GetProperty(name).GetString();

    // ---- Row shape -------------------------------------------------------------------------

    /// <summary>The field names ARE the deliverable: swings.jsonl is read by tools/combat, so a
    /// rename here silently breaks the offline half of the pipeline. Pins the full set from
    /// SWING-LEDGER-SPEC.md section 3.</summary>
    [Fact]
    public void Row_CarriesExactlyTheSpecifiedFields()
    {
        using var session = new Session(_directory).Persona("Ollie");
        session.Stats(new GameStatsSnapshot(Stamina: 81, MaxStamina: 105, Strength: 94, Dexterity: 99))
               .Say("You attack the rat0, using the axe0 as a weapon.",
                    "You hit the rat0 (15-19).");

        var row = Assert.Single(session.Rows());
        var names = row.EnumerateObject().Select(p => p.Name).ToArray();

        Assert.Equal(
            ["v", "ts", "dir", "persona", "gender", "sta", "str", "dex", "sta_max", "blind",
             "npc", "group", "weapon", "hit", "dmg_low", "dmg_high", "dmg", "rung", "rung_phrase"],
            names);
        Assert.Equal(SwingRow.CurrentVersion, row.GetProperty("v").GetInt32());
        Assert.Equal("Ollie", Str(row, "persona"));
        Assert.Equal("rats", Str(row, "group"));
        // Not obtainable for an existing character today, shipped nullable rather than invented.
        Assert.Equal(JsonValueKind.Null, row.GetProperty("gender").ValueKind);
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
            row.GetProperty("ts").GetInt64());
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
        Assert.True(row.GetProperty("hit").GetBoolean());
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
        Assert.Equal("zombies", Str(rows[0], "group"));
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

        var hit = session.Rows().Single(r => r.GetProperty("hit").GetBoolean());
        Assert.Equal(6, Int(hit, "dmg"));
    }

    /// <summary>The NPC arms itself independently of the player, and it changes what it does to you -
    /// so an incoming row carries the CREATURE'S weapon, not whatever is in the player's hands.</summary>
    [Fact]
    public void IncomingSwing_CarriesTheCreaturesOwnWeapon()
    {
        using var session = new Session(_directory);
        session.Say("You attack the zombie2, using the axe0 as a weapon.",
                    "The zombie2 has started to use the fork to fight!",
                    "The zombie2 misses you.",
                    "You miss the zombie2.");

        var rows = session.Rows();
        var incoming = rows.Single(r => Str(r, "dir") == "in");
        var outgoing = rows.Single(r => Str(r, "dir") == "out");

        Assert.Equal("fork", Str(incoming, "weapon"));
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
            Assert.False(r.GetProperty("hit").GetBoolean());
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
        Assert.True(row.GetProperty("hit").GetBoolean());
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
