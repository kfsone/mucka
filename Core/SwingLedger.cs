using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using MudSharp.Combat;
using MudSharp.Models;

namespace Mucka.Core;

/// <summary>
/// Records one <see cref="SwingRow"/> per swing, both directions, into the <c>swings</c> table of the
/// client combat database (see <see cref="CombatDb"/>). This is the per-swing evidence base
/// tools/combat/SWING-LEDGER-SPEC.md specifies: the fight rollup answers "how did that fight go", and
/// could never answer "how hard does this thing hit at rung 3 while I am below the stamina knee",
/// because the individual swings were never kept.
///
/// <para><b>SQLite rather than the JSONL this used to write.</b> The spec chose a flat file on the
/// explicit condition of "a writer that only ever appends", and named its own revisit trigger:
/// querying inside the client. A combat analysis view is that, and the corpus is nowhere near the
/// scale the flat-file argument feared. Beyond queryability the swap also buys crash safety - WAL
/// rolls back a torn write, where the text file left truncated final lines that every reader had to
/// tolerate.</para>
///
/// <para>Always on: a switch means missing data precisely when something interesting happened, and
/// everything downstream is built on this stream being continuous. ClogWriter, which used to be the
/// one opt-in recorder here, now follows the same rule.</para>
///
/// <para>Threading: every On* method is called from the session Feed thread (same contract as
/// ClogWriter and FightHistoryRecorder) and does nothing but cheap in-memory bookkeeping plus an
/// enqueue. The database write happens on a single background task (<see cref="DrainAsync"/>) which
/// owns the only write connection, so the thread parsing incoming combat text never pays for it -
/// stalling that thread delays the combat text itself, which no UI-side throttle can fix
/// (DESIGN_FINAL.md section 7.5, Invariant #1).</para>
///
/// <para>Never throws on the caller: a failed write loses a row, it must not lose a fight.</para>
///
/// <para>Runs its own <see cref="FightAccumulator"/> set for the same reason FightHistoryRecorder
/// does - it needs per-NPC state (the creature's weapon, its last health reading) on the Feed thread,
/// and sharing the view-model's UI-thread instance would put a lock on the typing hot path. The
/// accumulators here are used only as per-NPC memory; the tallies they also keep are the other two
/// consumers' business.</para>
/// </summary>
public sealed class SwingLedger : IDisposable
{
    /// <summary>How many rows one transaction may cover. A batch bounds how much a crash can lose to
    /// the work of at most this many swings, while still collapsing a burst (a pack fight can produce
    /// half a dozen rows from one tick) into a single commit. Never a reason to wait: the drain
    /// commits whatever is available and loops, so a lone swing is written immediately.</summary>
    private const int MaxBatch = 256;

    private readonly object _lock = new();
    private readonly string _dbPath;
    // Injected rather than calling CrashLog directly so this type stays free of MAUI references and
    // can be exercised against a temp directory in mudsharp.Tests.
    private readonly Action<string, Exception>? _onError;

    // One dedicated writer for this ledger's whole lifetime. Unbounded: a swing arrives at most once
    // per tick per participant, so there is no realistic burst worth backpressuring, and dropping a
    // row to save a few bytes of queue would defeat the point of keeping the stream.
    private readonly Channel<SwingRow> _writeQueue =
        Channel.CreateUnbounded<SwingRow>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Task _writerTask;

    // Per-NPC memory for the CURRENT encounter, keyed by instance name. Cleared at encounter end so a
    // later fight against a respawned "rat0" cannot inherit the dead one's health reading.
    private readonly Dictionary<string, FightAccumulator> _fights = new(StringComparer.OrdinalIgnoreCase);

    private GameStatsSnapshot _lastStats = GameStatsSnapshot.Empty;
    private StatusEffectState _lastEffects = StatusEffectState.Empty;
    private string? _persona;
    private long? _encounterStartedAtMs;

    private string? _currentWeapon;
    // When _currentWeapon was last confirmed, so an equip seen just before the client noticed the
    // fight can be carried into it while a stale one is discarded - see _encounterJustOpened.
    private DateTime _currentWeaponUtc;
    private static readonly TimeSpan PendingWeaponWindow = CombatTiming.PendingWeaponWindow;
    // Set when an encounter opens, consumed by the first combat event in it. The latch is resolved
    // there rather than in OnInCombatChanged because that event carries no timestamp, and the sibling
    // recorders' DateTime.UtcNow fallback compares a wall-clock reading against a stamp taken by the
    // tracker - two different clocks, agreeing only to within however long the fan-out took. Every
    // event that could open an encounter arrives immediately after the flag is set (CombatTracker
    // raises InCombatChanged from Begin(), before Emit() for the same line), so this costs nothing and
    // keeps the whole class on the tracker's own clock.
    private bool _encounterJustOpened;

    private readonly StaminaDeltaRelay _staminaRelay = new();

    // Blows exchanged during the CURRENT encounter, held back from _damage until it closes. This is
    // what makes a live fight's "ever" figures genuinely mean "before this fight" - see
    // SwingDamageIndex's class remarks. Cleared by the same merge that consumes them, so an encounter
    // can never be folded in twice.
    private readonly List<(string NpcName, double Damage)> _encounterTaken = [];
    private readonly List<(string NpcName, double Low, double High)> _encounterDealt = [];

    private readonly SwingDamageIndex _damage = new();

    /// <summary>The accumulated "how hard does this thing hit, and how hard do I hit it" cache, for the
    /// rail's per-opponent damage column. Warmed by <see cref="WarmDamageIndexAsync"/> at startup and
    /// thereafter updated incrementally, one encounter at a time.</summary>
    public SwingDamageIndex Damage => _damage;

    public SwingLedger(string dbPath, Action<string, Exception>? onError = null)
    {
        _dbPath = dbPath;
        _onError = onError;
        _writerTask = Task.Run(DrainAsync);
    }

    public string DatabasePath => _dbPath;

    /// <summary>
    /// Fills <see cref="Damage"/> from the database's own GROUP BY views. Call once at startup, OFF the
    /// UI thread; safe before anything has been recorded, and safe to call again (it replaces rather
    /// than accumulates, so a second warm cannot double-count).
    ///
    /// <para>The aggregation happens in SQL, not here, which is the point: the warm-up cost is
    /// proportional to the number of distinct creatures, not to the number of swings, so the corpus can
    /// grow indefinitely without startup growing with it. That is the property the spec's proposed
    /// index file was trying to buy, obtained without a second file that can disagree with the first.</para>
    /// </summary>
    public Task WarmDamageIndexAsync(CancellationToken cancellationToken = default)
        // Task.Run for the same reason FightHistoryStore.LoadAsync uses it: every step below is
        // synchronous SQLite work, and an async method whose awaits complete synchronously never
        // leaves the thread that called it - which here is the UI thread (Invariant #1).
        => Task.Run(WarmCore, cancellationToken);

    private void WarmCore()
    {
        try
        {
            // CombatDb.Open creates the directory and applies the shared PRAGMAs - see
            // FightHistoryStore.LoadCore for what skipping it costs.
            using var connection = CombatDb.Open(_dbPath);

            var incomingByNpc = ReadDamage(connection, "v_incoming_by_npc");
            var incomingByGroup = ReadDamage(connection, "v_incoming_by_group");
            var outgoingByNpc = ReadBracket(connection, "v_outgoing_by_npc");
            var outgoingByGroup = ReadBracket(connection, "v_outgoing_by_group");

            _damage.LoadProfiles(incomingByNpc, incomingByGroup, outgoingByNpc, outgoingByGroup);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            // Best-effort: an unreadable database costs the rail its history column and must not cost
            // anything else. The live per-fight figures do not come from here.
            _onError?.Invoke("SwingLedger.WarmDamageIndex", ex);
        }
    }

    private static List<(string Name, DamageProfile Profile)> ReadDamage(SqliteConnection connection, string view)
    {
        var result = new List<(string, DamageProfile)>();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name, samples, max_dmg, sum_dmg FROM {view};";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0))
                continue;
            result.Add((reader.GetString(0),
                new DamageProfile(reader.GetInt32(1), reader.GetDouble(2), reader.GetDouble(3))));
        }
        return result;
    }

    private static List<(string Name, BracketProfile Profile)> ReadBracket(SqliteConnection connection, string view)
    {
        var result = new List<(string, BracketProfile)>();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name, samples, sum_low, sum_high, max_high FROM {view};";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0))
                continue;
            result.Add((reader.GetString(0),
                new BracketProfile(reader.GetInt32(1), reader.GetDouble(2), reader.GetDouble(3), reader.GetDouble(4))));
        }
        return result;
    }

    public void OnStatsUpdated(GameStatsSnapshot stats)
    {
        lock (_lock)
        {
            _lastStats = stats;
            ObserveStaminaLocked(stats.Stamina);
        }
    }

    /// <summary>The player's active buffs/debuffs changed. Recorded per swing because MUD2's own
    /// damage and hit-chance depend on them, so a baseline that cannot separate "the usual" from "the
    /// usual, while weakened" is not a baseline at all.</summary>
    public void OnStatusEffectsChanged(StatusEffectState effects)
    {
        lock (_lock)
            _lastEffects = effects;
    }

    /// <summary>The character occupying this session was identified (MudSession.CharacterIdentified,
    /// from the post-login <c>score</c> reply). Session-scoped, not encounter-scoped.</summary>
    public void OnCharacterIdentified(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;
        lock (_lock)
            _persona = name;
    }

    /// <param name="encounterStartedAtMs">The shared encounter id, stamped ONCE by MuckaConnection and
    /// handed to every consumer. Not computed here: this row's join partner in the fights table
    /// carries the same value, and two consumers each reading their own clock would produce two ids
    /// microseconds apart that no join would ever match.</param>
    public void OnInCombatChanged(bool inCombat, long? encounterStartedAtMs = null)
    {
        lock (_lock)
        {
            if (inCombat)
            {
                _encounterStartedAtMs = encounterStartedAtMs;
                // Arm the pending-weapon latch; the first event of the encounter resolves it (see
                // _encounterJustOpened). "You are now using the axe0 to fight!" names no NPC so it
                // cannot open an encounter, and against something already engaging you it is the ONLY
                // line printed - dropping it unconditionally here is what recorded broadsword fights
                // as bare-handed in the fight history. Same window as FightHistoryRecorder and
                // CombatStatsAggregator: all three read the same stream and must not disagree about
                // what was in the player's hands.
                _encounterJustOpened = true;
                return;
            }

            _encounterJustOpened = false;

            // The encounter is over, so its blows can now become history. Done BEFORE the _fights
            // guard below, and unconditionally: the guard is about whether a WEAPON latch should
            // survive, which is a different question from whether damage was exchanged. Folding here
            // rather than per swing is the whole self-comparison guarantee (see SwingDamageIndex) - the
            // same moment, and the same reasoning, as FightHistoryRecorder.FlushLocked handing its rows
            // to the fight-level index.
            if (_encounterTaken.Count > 0 || _encounterDealt.Count > 0)
            {
                _damage.FoldAll(_encounterTaken, _encounterDealt);
                _encounterTaken.Clear();
                _encounterDealt.Clear();
            }
            _encounterStartedAtMs = null;

            // Mirrors FightHistoryRecorder.FlushLocked, down to the "only when something was actually
            // tracked" guard: an encounter that closed without naming a single NPC cannot have been a
            // fight, and clearing the weapon on it would throw away a latch the sibling recorder kept.
            if (_fights.Count == 0)
                return;
            _fights.Clear();
            _currentWeapon = null;
        }
    }

    public void OnCombatEvent(CombatEvent combatEvent)
    {
        lock (_lock)
        {
            if (_encounterJustOpened)
            {
                // Resolve the pending-weapon latch against the tracker's own stamp for this line. An
                // equip more than a few seconds old says nothing about the fight starting now: MUD2's
                // wielded weapon is per-fight, dropped at fight end, and `wield` is refused outside
                // one - carrying a stale line forward would invent an armed fight.
                _encounterJustOpened = false;
                if (_currentWeapon is not null && combatEvent.TimestampUtc - _currentWeaponUtc > PendingWeaponWindow)
                    _currentWeapon = null;
            }

            switch (combatEvent.Kind)
            {
                case CombatEventKind.FightStart:
                    if (!string.IsNullOrWhiteSpace(combatEvent.Weapon))
                    {
                        _currentWeapon = combatEvent.Weapon;
                        _currentWeaponUtc = combatEvent.TimestampUtc;
                    }
                    FightForLocked(combatEvent);
                    break;

                case CombatEventKind.WeaponEquip:
                    if (!string.IsNullOrWhiteSpace(combatEvent.Weapon))
                    {
                        _currentWeapon = combatEvent.Weapon;
                        _currentWeaponUtc = combatEvent.TimestampUtc;
                    }
                    break;

                // WeaponUnusable shares this: "You cannot use the X to fight now!" means the weapon is
                // not in play whatever the cause (it just broke, or MUD2 refused the wield outright).
                // Either way every swing from here is bare-handed until a fresh equip line says
                // otherwise - see the matching case in CombatStatsAggregator.
                case CombatEventKind.WeaponBroke:
                case CombatEventKind.WeaponUnusable:
                    _currentWeapon = null;
                    break;

                case CombatEventKind.NpcWeaponEquip:
                    FightForLocked(combatEvent)?.NoteNpcWeapon(combatEvent.Weapon);
                    break;

                case CombatEventKind.NpcHealth:
                    // Recorded here and read on the NEXT swing, which is what makes SwingRow.HealthRung
                    // the state BEFORE its swing: MUD2 prints the descriptor on the line following a
                    // landed blow, so by the time this arrives the swing it describes is already
                    // written.
                    if (combatEvent.HealthRung is int rung)
                        FightForLocked(combatEvent)?.NoteHealth(rung, combatEvent.HealthPhrase, combatEvent.TimestampUtc);
                    break;

                // The four swing kinds - the only ones that produce a row.
                case CombatEventKind.Hit:
                    if (combatEvent.RangeLow is int low && combatEvent.RangeHigh is int high
                        && !string.IsNullOrWhiteSpace(combatEvent.NpcName))
                    {
                        _encounterDealt.Add((combatEvent.NpcName, low, high));
                    }
                    AppendLocked(BuildRowLocked(combatEvent, SwingRow.DirectionOut, hit: true, damage: null));
                    break;

                case CombatEventKind.Miss:
                    AppendLocked(BuildRowLocked(combatEvent, SwingRow.DirectionOut, hit: false, damage: null));
                    break;

                case CombatEventKind.HitByNpc:
                    // The delta must be resolved BEFORE the row is built, and exactly once: it advances
                    // the stamina baseline (see ResolveDamageTakenLocked).
                    var (damage, staminaBefore) = ResolveDamageTakenLocked(combatEvent.RangeLow);
                    if (damage is int taken && !string.IsNullOrWhiteSpace(combatEvent.NpcName))
                        _encounterTaken.Add((combatEvent.NpcName, taken));
                    AppendLocked(BuildRowLocked(
                        combatEvent, SwingRow.DirectionIn, hit: true, damage, staminaBefore));
                    break;

                case CombatEventKind.MissByNpc:
                    AppendLocked(BuildRowLocked(combatEvent, SwingRow.DirectionIn, hit: false, damage: null));
                    break;
            }
        }
    }

    /// <summary>Builds the row for one swing from whatever is known at this instant. Never returns
    /// null and never drops a swing for missing context: a row with null stats still carries the
    /// timestamp, the direction, the opponent and the outcome, and "we were swinging at a rat before
    /// the first heartbeat landed" is evidence. Dropping it would bias the corpus toward the
    /// well-instrumented middle of a session.</summary>
    private SwingRow BuildRowLocked(
        CombatEvent combatEvent, string direction, bool hit, int? damage, int? staminaBefore = null)
    {
        var outgoing = direction == SwingRow.DirectionOut;
        var fight = FightForLocked(combatEvent);
        var timestampMs = new DateTimeOffset(combatEvent.TimestampUtc, TimeSpan.Zero).ToUnixTimeMilliseconds();

        return new SwingRow
        {
            TimestampMs = timestampMs,
            Direction = direction,
            EncounterStartedAtMs = _encounterStartedAtMs,
            Persona = _persona,
            Sex = _lastStats.Sex,

            Stamina = _lastStats.Stamina,
            // Incoming hits only; every other swing kind passes null and means it (see StaminaBefore).
            StaminaBefore = outgoing || !hit ? null : staminaBefore,
            MaxStamina = _lastStats.MaxStamina,
            Strength = _lastStats.Strength,
            RawStrength = _lastStats.RawStrength,
            MaxStrength = _lastStats.MaxStrength,
            Dexterity = _lastStats.Dexterity,
            RawDexterity = _lastStats.RawDexterity,
            MaxDexterity = _lastStats.MaxDexterity,
            Level = _lastStats.Level,
            Score = _lastStats.Score,
            ObjectsCarried = _lastStats.ObjectsCarried,
            // Space is the parser's "nothing reported"; stored as null rather than as a blank string
            // so a query can tell "no reading" from a real weather code without knowing that.
            Weather = _lastStats.Weather == ' ' ? null : _lastStats.Weather.ToString(),

            IsBlind = _lastStats.IsBlind,
            IsDeaf = _lastStats.IsDeaf,
            IsCrippled = _lastStats.IsCrippled,
            IsDumb = _lastStats.IsDumb,
            StrengthBuff = _lastEffects.StrengthBuff,
            StrengthDebuff = _lastEffects.StrengthDebuff,
            DexterityBuff = _lastEffects.DexterityBuff,
            DexterityDebuff = _lastEffects.DexterityDebuff,
            StaminaBuff = _lastEffects.StaminaBuff,
            StaminaDebuff = _lastEffects.StaminaDebuff,
            Glow = _lastEffects.Glow,

            TimeToReset = _lastStats.TimeToReset,
            // The reset's END instant, constant across every swing of one reset - see
            // SwingRow.ResetEpochMs. TimeToReset is in seconds as the game reports it.
            ResetEpochMs = _lastStats.TimeToReset is int ttr ? timestampMs + (ttr * 1000L) : null,

            NpcName = combatEvent.NpcName,
            NpcGroup = NpcGroups.Normalize(combatEvent.NpcName),
            NpcWeapon = fight?.NpcWeapon,
            HealthRung = fight?.HealthRung,
            HealthPhrase = fight?.HealthPhrase,

            // What is in the player's hands right now, not the fight's rollup weapon - a per-swing row
            // wants the per-swing answer, and MUD2 extends one wielded weapon across every fight in the
            // encounter anyway. Recorded on incoming rows too: what you were holding when something hit
            // you is exactly as much a condition of that blow as what you were holding when you landed
            // one (a shield-less arm, a two-handed weapon), and the creature's own weapon has its own
            // column now rather than sharing this one.
            Weapon = _currentWeapon,
            Hit = hit,
            // RangeLow/RangeHigh mean different things per direction and must never be crossed over:
            // outgoing they are the damage bracket, incoming they are the player's post-hit
            // (current/max) stamina, which is where Damage comes from instead.
            DamageLow = outgoing && hit ? combatEvent.RangeLow : null,
            DamageHigh = outgoing && hit ? combatEvent.RangeHigh : null,
            Damage = outgoing ? null : damage,
        };
    }

    /// <summary>Per-NPC memory for this encounter, created on first sight. The weapon seeded here is
    /// the player's, which this class never reads back off the accumulator - it is passed only so the
    /// type is constructed honestly rather than lied to.</summary>
    private FightAccumulator? FightForLocked(CombatEvent combatEvent)
    {
        var npcName = combatEvent.NpcName;
        if (string.IsNullOrWhiteSpace(npcName))
            return null;

        if (_fights.TryGetValue(npcName, out var existing))
            return existing;

        var fight = new FightAccumulator(npcName, combatEvent.TimestampUtc, _currentWeapon);
        _fights[npcName] = fight;
        return fight;
    }

    // An NPC hit line like "The zombie hits you (95/100)." is parsed TWICE for the SAME line -
    // generically by GameLineAnalyzer (StatsUpdated -> ObserveStaminaLocked(95) first) and then by
    // CombatTracker's HitByNpc regex (RangeLow=95, reaching ResolveDamageTakenLocked second). See
    // MudSharp.Combat.StaminaDeltaRelay's own remarks for why this needs a relay at all - without one,
    // every hit records exactly 0 damage (confirmed live before the fix).
    private void ObserveStaminaLocked(int? currentStamina) => _staminaRelay.Observe(currentStamina);

    /// <summary>Resolves one incoming blow into a damage figure AND the pre-hit stamina it was
    /// measured against, via the shared <see cref="StaminaDeltaRelay"/>. Both come out together
    /// because they are the two halves of one attribution - returning only the delta and letting the
    /// caller reconstruct the baseline would reintroduce exactly the arithmetic SwingRow.StaminaBefore
    /// exists to avoid.</summary>
    private (int? Damage, int? Before) ResolveDamageTakenLocked(int? currentStamina)
    {
        var (delta, baseline) = _staminaRelay.ResolveDelta(currentStamina);
        // The baseline is reported only when it actually produced a figure. A baseline that yielded a
        // negative delta (regen outran the blow) is not the pre-hit stamina of anything we are willing
        // to call damage, and writing it beside a null dmg would invite the subtraction back.
        return (delta, delta is null ? null : baseline);
    }

    /// <summary>Enqueues. TryWrite on an unbounded channel never blocks and only fails after
    /// Complete(), which only <see cref="Dispose"/> calls.</summary>
    private void AppendLocked(SwingRow row) => _writeQueue.Writer.TryWrite(row);

    private const string InsertSql = """
        INSERT INTO swings (
            ts, dir, encounter_started_at_ms, persona, sex,
            sta, sta_before, sta_max,
            str, str_raw, str_max, dex, dex_raw, dex_max,
            level, score, objects_carried, weather,
            blind, deaf, crippled, dumb,
            str_buff, str_debuff, dex_buff, dex_debuff, sta_buff, sta_debuff, glow,
            time_to_reset, reset_epoch_ms,
            npc, npc_group, npc_weapon, rung, rung_phrase,
            weapon, hit, dmg_low, dmg_high, dmg
        ) VALUES (
            $ts, $dir, $encounter, $persona, $sex,
            $sta, $sta_before, $sta_max,
            $str, $str_raw, $str_max, $dex, $dex_raw, $dex_max,
            $level, $score, $objects, $weather,
            $blind, $deaf, $crippled, $dumb,
            $str_buff, $str_debuff, $dex_buff, $dex_debuff, $sta_buff, $sta_debuff, $glow,
            $ttr, $reset_epoch,
            $npc, $npc_group, $npc_weapon, $rung, $rung_phrase,
            $weapon, $hit, $dmg_low, $dmg_high, $dmg
        );
        """;

    /// <summary>The single background writer for this ledger's whole lifetime. Owns the only write
    /// connection and drains in the order rows were enqueued, batching whatever is already waiting into
    /// one transaction. <c>ReadAllAsync</c> completes normally only once the queue is both closed and
    /// fully drained, which is the "nothing queued is lost on shutdown" property
    /// <see cref="Dispose"/> relies on.</summary>
    private async Task DrainAsync()
    {
        SqliteConnection? connection = null;
        var reader = _writeQueue.Reader;

        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                // Opened lazily, on the first row that actually arrives, so a session with no combat
                // in it never creates a database file at all.
                connection ??= CombatDb.Open(_dbPath);
                WriteBatch(connection, reader);
            }
        }
        catch (Exception ex)
        {
            // Best-effort by design: a failed write loses rows, it must not take the app with it. The
            // loop is abandoned rather than retried - if the database cannot be opened or written at
            // all, retrying per swing would turn one failure into one per tick, forever.
            _onError?.Invoke("SwingLedger.Drain", ex);
        }
        finally
        {
            connection?.Dispose();
        }
    }

    private void WriteBatch(SqliteConnection connection, ChannelReader<SwingRow> reader)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = InsertSql;

        var written = 0;
        while (written < MaxBatch && reader.TryRead(out var row))
        {
            try
            {
                Bind(command, row);
                command.ExecuteNonQuery();
                written++;
            }
            catch (SqliteException ex)
            {
                // One malformed row must not abort the batch behind it. Counted as handled and skipped,
                // the same discipline the JSONL reader used for a truncated line.
                _onError?.Invoke("SwingLedger.Insert", ex);
            }
        }

        if (written > 0)
            transaction.Commit();
    }

    private static void Bind(SqliteCommand command, SwingRow row)
    {
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$ts", row.TimestampMs);
        command.Parameters.AddWithValue("$dir", row.Direction);
        command.Parameters.AddWithValue("$encounter", Value(row.EncounterStartedAtMs));
        command.Parameters.AddWithValue("$persona", Value(row.Persona));
        command.Parameters.AddWithValue("$sex", Value(row.Sex));
        command.Parameters.AddWithValue("$sta", Value(row.Stamina));
        command.Parameters.AddWithValue("$sta_before", Value(row.StaminaBefore));
        command.Parameters.AddWithValue("$sta_max", Value(row.MaxStamina));
        command.Parameters.AddWithValue("$str", Value(row.Strength));
        command.Parameters.AddWithValue("$str_raw", Value(row.RawStrength));
        command.Parameters.AddWithValue("$str_max", Value(row.MaxStrength));
        command.Parameters.AddWithValue("$dex", Value(row.Dexterity));
        command.Parameters.AddWithValue("$dex_raw", Value(row.RawDexterity));
        command.Parameters.AddWithValue("$dex_max", Value(row.MaxDexterity));
        command.Parameters.AddWithValue("$level", Value(row.Level));
        command.Parameters.AddWithValue("$score", Value(row.Score));
        command.Parameters.AddWithValue("$objects", Value(row.ObjectsCarried));
        command.Parameters.AddWithValue("$weather", Value(row.Weather));
        command.Parameters.AddWithValue("$blind", row.IsBlind ? 1 : 0);
        command.Parameters.AddWithValue("$deaf", row.IsDeaf ? 1 : 0);
        command.Parameters.AddWithValue("$crippled", row.IsCrippled ? 1 : 0);
        command.Parameters.AddWithValue("$dumb", row.IsDumb ? 1 : 0);
        command.Parameters.AddWithValue("$str_buff", row.StrengthBuff ? 1 : 0);
        command.Parameters.AddWithValue("$str_debuff", row.StrengthDebuff ? 1 : 0);
        command.Parameters.AddWithValue("$dex_buff", row.DexterityBuff ? 1 : 0);
        command.Parameters.AddWithValue("$dex_debuff", row.DexterityDebuff ? 1 : 0);
        command.Parameters.AddWithValue("$sta_buff", row.StaminaBuff ? 1 : 0);
        command.Parameters.AddWithValue("$sta_debuff", row.StaminaDebuff ? 1 : 0);
        command.Parameters.AddWithValue("$glow", row.Glow ? 1 : 0);
        command.Parameters.AddWithValue("$ttr", Value(row.TimeToReset));
        command.Parameters.AddWithValue("$reset_epoch", Value(row.ResetEpochMs));
        command.Parameters.AddWithValue("$npc", Value(row.NpcName));
        command.Parameters.AddWithValue("$npc_group", row.NpcGroup);
        command.Parameters.AddWithValue("$npc_weapon", Value(row.NpcWeapon));
        command.Parameters.AddWithValue("$rung", Value(row.HealthRung));
        command.Parameters.AddWithValue("$rung_phrase", Value(row.HealthPhrase));
        command.Parameters.AddWithValue("$weapon", Value(row.Weapon));
        command.Parameters.AddWithValue("$hit", row.Hit ? 1 : 0);
        command.Parameters.AddWithValue("$dmg_low", Value(row.DamageLow));
        command.Parameters.AddWithValue("$dmg_high", Value(row.DamageHigh));
        command.Parameters.AddWithValue("$dmg", Value(row.Damage));
    }

    /// <summary>Null becomes SQL NULL, not a default. Every stat here can genuinely be unknown, and a
    /// zero standing in for "never reported" is a fabricated measurement that outlives the session that
    /// invented it.</summary>
    private static object Value(object? value) => value ?? DBNull.Value;

    /// <summary>Blocks briefly - just draining whatever is already queued in memory - so an app exit
    /// mid-fight cannot lose the swings enqueued moments before it. Same shutdown contract as
    /// FightHistoryStore.Dispose.</summary>
    public void Dispose()
    {
        _writeQueue.Writer.TryComplete();
        try
        {
            _writerTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Best-effort: Dispose must never throw during shutdown. Whatever did not get written in
            // time is lost, same as any other best-effort I/O failure in this class.
        }
    }
}
