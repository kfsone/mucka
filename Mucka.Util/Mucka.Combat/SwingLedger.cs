using Microsoft.Data.Sqlite;
using MudSharp.Combat;
using MudSharp.Models;
using Mucka.Store;

namespace Mucka.Combat;

/// <summary>
/// Records one <see cref="SwingRow"/> per swing, both directions, into the <c>swings</c> table (see
/// <see cref="MuckaDb"/>). This is the per-swing evidence base: the fight rollup answers "how did that
/// fight go", but could never answer "how hard does this thing hit at rung 3 while I am below the
/// stamina knee", because the individual swings were never kept.
///
/// <para>Always on: a switch means missing data precisely when something interesting happened, and
/// everything downstream is built on this stream being continuous.</para>
///
/// <para>Threading: every On* method is called from the session Feed thread (same contract as
/// ClogWriter and FightHistoryRecorder) and does nothing but cheap in-memory bookkeeping plus an
/// enqueue onto <see cref="MuckaStore"/>, whose single background task owns the only write
/// connection. The thread parsing incoming combat text never pays for the write - stalling it delays
/// the combat text itself, which no UI-side throttle can fix (Invariant #1).</para>
///
/// <para>Never throws on the caller: a failed write loses a row, it must not lose a fight.</para>
///
/// <para>Runs its own <see cref="FightAccumulator"/> set for the same reason FightHistoryRecorder
/// does - it needs per-NPC state (the creature's weapon, its last health reading) on the Feed thread,
/// and sharing the view-model's UI-thread instance would put a lock on the typing hot path. The
/// accumulators here are used only as per-NPC memory; the tallies they also keep are the other two
/// consumers' business.</para>
/// </summary>
public sealed class SwingLedger
{
    private readonly object _lock = new();
    private readonly MuckaStore _store;
    // Injected rather than calling CrashLog directly so this type stays free of MAUI references and
    // can be exercised against a temp directory from Mucka.Util.Tests.
    private readonly Action<string, Exception>? _onError;

    // Per-NPC memory for the CURRENT encounter, keyed by instance name. Cleared at encounter end so a
    // later fight against a respawned "rat0" cannot inherit the dead one's health reading.
    private readonly Dictionary<string, FightAccumulator> _fights = new(StringComparer.OrdinalIgnoreCase);

    private GameStatsSnapshot _lastStats = GameStatsSnapshot.Empty;
    private StatusEffectState _lastEffects = StatusEffectState.Empty;
    // The login every row written from here belongs to - persona_sessions.id. Null before the first
    // one opens, which is the honest reading rather than a guess.
    private long? _personaSessionId;
    private long? _encounterStartedAtMs;
    // The last encounter key this ledger saw, kept after the encounter closes. Only score_events uses
    // it, and only because a kill's award is printed after the line that closed the encounter - see
    // OnScoreSave.
    private long? _lastEncounterStartedAtMs;
    // Whether a fight is in progress. Tracked separately from _encounterStartedAtMs rather than read
    // off it, because the KEY is supplied by the caller and is allowed to be null (MuckaConnection
    // always passes one; nothing makes it mandatory, and the test harness does not). Deriving "are we
    // fighting" from "did someone give us an id" conflates two questions that are only usually the
    // same answer.
    private bool _encounterOpen;

    // Set by a "You have completed a Task." line and cleared by the very next score announcement,
    // which is the one it pays for. Not a count and not time-bounded: the payout is printed on the
    // line immediately after the task line in every recording of it, so the mark has exactly one row
    // to land on. See OnTaskCompleted.
    private bool _taskLineJustSeen;

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
    private readonly List<(string NpcName, string? NpcWeapon, double Damage)> _encounterTaken = [];
    private readonly List<(string NpcName, double Low, double High)> _encounterDealt = [];

    private readonly SwingDamageIndex _damage = new();
    private readonly ReachMarkIndex _reach = new();
    private readonly StaminaPoolIndex _pool = new();

    /// <summary>The accumulated "how hard does this thing hit, and how hard do I hit it" cache, for the
    /// rail's per-opponent damage column. Warmed by <see cref="WarmDamageIndexAsync"/> at startup and
    /// thereafter updated incrementally, one encounter at a time.</summary>
    public SwingDamageIndex Damage => _damage;

    /// <summary>How far each species has been SEEN to reach with one blow - a floor under its true
    /// maximum, never the maximum. See <see cref="ReachMarkIndex"/>.</summary>
    public ReachMarkIndex Reach => _reach;

    /// <summary>
    /// Per-species stamina pool bands, from the censored-interval estimator (see
    /// <c>MudSharp.Combat.StaminaPoolEstimator</c>).
    ///
    /// <para><b>Filled at warm-up only, and therefore a session behind.</b> Unlike
    /// <see cref="Damage"/> and <see cref="Reach"/>, which fold an encounter's own blows in as it
    /// closes, an estimate is a function of a species' WHOLE observation set - so refreshing it means
    /// re-reading the swings joined to the fight rollup, and the rollup for the encounter just closed
    /// is written by a different background task on its own schedule. Folding at encounter close would
    /// race that write and produce an estimate that silently omitted the fight that triggered it. The
    /// consequence to know about: a species first met during this session shows no band until the next
    /// start-up, and a band already on file does not tighten mid-session.</para>
    /// </summary>
    public StaminaPoolIndex Pool => _pool;

    public SwingLedger(MuckaStore store, Action<string, Exception>? onError = null)
    {
        _store = store;
        _onError = onError;
    }

    public string DatabasePath => _store.Path;

    /// <summary>
    /// Fills <see cref="Damage"/> from the database's own GROUP BY views. Call once at startup, OFF the
    /// UI thread; safe before anything has been recorded, and safe to call again (it replaces rather
    /// than accumulates, so a second warm cannot double-count).
    ///
    /// <para>The aggregation happens in SQL, not here, which is the point: the warm-up cost is
    /// proportional to the number of distinct creatures, not to the number of swings, so the corpus can
    /// grow indefinitely without startup growing with it, and there is no second index file that can
    /// disagree with the database.</para>
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
            // Its own short-lived connection, never the writer's: a warm-up is a read, and WAL lets it
            // run alongside whatever the writer is doing. MuckaDb.Open creates the directory and
            // applies the shared PRAGMAs - see FightHistoryStore.LoadCore for what skipping it costs.
            using var connection = MuckaDb.Open(_store.Path);

            var incomingByNpc = ReadDamage(connection, "v_incoming_by_npc");
            var incomingByGroup = ReadDamage(connection, "v_incoming_by_group");
            var outgoingByNpc = ReadBracket(connection, "v_outgoing_by_npc");
            var outgoingByGroup = ReadBracket(connection, "v_outgoing_by_group");
            var incomingByGroupWeapon = ReadDamage(connection, "v_incoming_by_group_weapon");

            _damage.LoadProfiles(
                incomingByNpc, incomingByGroup, outgoingByNpc, outgoingByGroup, incomingByGroupWeapon);

            // Reach marks come off the same per-instance rows the damage index already read, folded
            // up to the pool key: "large rat0" and "large rat7" are one creature as far as how hard it
            // can hit goes, and neither is a plain rat.
            _reach.Load(incomingByNpc.Select(row => (row.Name, row.Profile.Max, row.Profile.Samples)));

            _pool.Load(PoolObservationBuilder.BuildAll(ReadPoolFights(connection), ReadPoolSwings(connection)));
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

    /// <summary>Every closed fight, reduced to what the pool estimator needs. Only outcomes the
    /// PLAYER'S blow produced count as a kill: a creature that dropped dead of poison bounds nothing,
    /// because the damage that finished it was never on the wire.</summary>
    private static List<PoolFightRow> ReadPoolFights(SqliteConnection connection)
    {
        var result = new List<PoolFightRow>();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT npc_name, started_at_ms, ended_at_ms, outcome, encounter_started_at_ms "
            + "FROM fights WHERE npc_name IS NOT NULL;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new PoolFightRow(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                // Exact ordinal match on "Kill" and nothing else. A NoMore row - poison, or anything
                // that finished the creature without the player's blow - must NOT read as a kill: the
                // damage that ended it never crossed the wire, so the row can floor the pool and must
                // never ceiling it. Pinned by SwingLedgerOutcomeTests.
                string.Equals(reader.GetString(3), nameof(FightOutcome.Kill), StringComparison.Ordinal),
                reader.IsDBNull(4) ? null : reader.GetInt64(4)));
        }
        return result;
    }

    /// <summary>Every swing that could constrain a pool: the player's brackets and the creature's rung
    /// readings. Both directions are read, because the rung is stamped on incoming rows too and a
    /// reading that arrived between two of the player's blows is still a reading.</summary>
    private static List<PoolSwingRow> ReadPoolSwings(SqliteConnection connection)
    {
        var result = new List<PoolSwingRow>();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT npc, ts, dir, hit, dmg_low, dmg_high, rung, sta, sta_max, weapon "
            + "FROM swings WHERE npc IS NOT NULL ORDER BY ts, id;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new PoolSwingRow(
                reader.GetString(0),
                reader.GetInt64(1),
                string.Equals(reader.GetString(2), SwingRow.DirectionOut, StringComparison.Ordinal),
                reader.GetInt64(3) != 0,
                reader.IsDBNull(4) ? null : reader.GetDouble(4),
                reader.IsDBNull(5) ? null : reader.GetDouble(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6),
                // Player state, for the chase-linkage test - see MudSharp.Combat.ChaseLinkPolicy.
                reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetInt32(8),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
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

    /// <summary>A persona session opened or closed - one login, from game mode entered/exited. Every
    /// row written after this carries <paramref name="personaSessionId"/>; null when no login is
    /// current, so rows recorded at the shell are unattributed rather than attributed to the last
    /// character to have played.</summary>
    public void OnPersonaSessionChanged(long? personaSessionId)
    {
        lock (_lock)
            _personaSessionId = personaSessionId;
    }

    /// <summary>
    /// MUD2 announced a score change - <c>(Persona saved on +38 = 19,214).</c> Written as its own row,
    /// unattributed, exactly as printed. See the <c>score_events</c> table comment in
    /// <see cref="MuckaDb"/> and <see cref="MudSharp.Models.ScoreSave"/>.
    ///
    /// <para><b>Recorded whether or not a fight is in progress.</b> Treasure deposits, the reset save
    /// and the shell-exit save all come through here, and a table that held only the combat ones could
    /// not be used to check that the deltas account for the whole of a session's score movement -
    /// which is the one property that makes the deltas trustworthy.</para>
    ///
    /// <para><b>The encounter key survives the close on purpose.</b> The award for a kill is printed
    /// on the line AFTER the kill, and the kill line is what closes the encounter, so by the time this
    /// runs <see cref="_encounterStartedAtMs"/> has already been nulled for exactly the rows that most
    /// need it. <c>_lastEncounterStartedAtMs</c> keeps the key and <c>encounter_open</c> records which
    /// of the two situations produced it - no time bound is applied here, deliberately, because "is
    /// this award close enough to that encounter to belong to it" is an analysis question and the
    /// timestamps are already in the table to answer it.</para>
    /// </summary>
    public void OnScoreSave(ScoreSave save)
    {
        lock (_lock)
        {
            AppendLocked(new ScoreEventRow
            {
                TimestampMs = new DateTimeOffset(DateTime.UtcNow, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                EncounterStartedAtMs = _encounterStartedAtMs ?? _lastEncounterStartedAtMs,
                EncounterOpen = _encounterOpen,
                PersonaSessionId = _personaSessionId,
                Delta = save.Delta,
                Total = save.Total,
                AfterTaskLine = _taskLineJustSeen,
                RawText = save.RawText,
            });
            _taskLineJustSeen = false;
        }
    }

    /// <summary>
    /// MUD2 announced a discharged task - <c>You have completed a Task. This makes a total of 1.</c>
    /// Not a row of its own: it is a mark on the NEXT <c>score_events</c> row, which is the payout it
    /// explains.
    ///
    /// <para><b>What the column is and is not.</b> <c>after_task_line</c> records an OBSERVATION - the
    /// game printed a task line and then this score line, in that order, with nothing between them.
    /// It does not assert that the delta is the task's rather than a kill's; a query can decide that,
    /// with the swings in the same encounter in front of it. Recording it at all is what stops an
    /// analysis pass from having to re-derive it from a raw_text scan of a line that is not in this
    /// table.</para>
    ///
    /// <para>Recorded whether or not a fight is in progress, like every other score row - a task can
    /// be discharged by carrying something to a place, with no combat anywhere near it.</para>
    /// </summary>
    public void OnTaskCompleted(TaskCompletion completed)
    {
        lock (_lock)
            _taskLineJustSeen = true;
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
                _lastEncounterStartedAtMs = encounterStartedAtMs ?? _lastEncounterStartedAtMs;
                _encounterOpen = true;
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
            _encounterOpen = false;

            // The encounter is over, so its blows can now become history. Done BEFORE the _fights
            // guard below, and unconditionally: the guard is about whether a WEAPON latch should
            // survive, which is a different question from whether damage was exchanged. Folding here
            // rather than per swing is the whole self-comparison guarantee (see SwingDamageIndex) - the
            // same moment, and the same reasoning, as FightHistoryRecorder.FlushLocked handing its rows
            // to the fight-level index.
            if (_encounterTaken.Count > 0 || _encounterDealt.Count > 0)
            {
                _damage.FoldAll(_encounterTaken, _encounterDealt);
                // Reach marks fold at the same instant and for the same reason. Unlike the damage
                // index this one only ever rises, so folding it late costs nothing but a session's
                // worth of latency on a number that changes a handful of times a month.
                foreach (var (npcName, _, damage) in _encounterTaken)
                    _reach.Observe(npcName, damage);
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

                case CombatEventKind.NpcStaminaRead:
                    // The only direct measurement of NPC stamina MUD2 gives - four observations exist
                    // in the whole corpus. Recorded whether or not the creature is engaged, exactly
                    // as the tracker reports it: diagnosing something BEFORE picking a fight with it is
                    // the point of carrying a stethoscope, and an encounter id of null says so.
                    if (combatEvent.RangeLow is int staLow && combatEvent.RangeHigh is int staHigh
                        && !string.IsNullOrWhiteSpace(combatEvent.NpcName))
                    {
                        AppendLocked(new NpcStaminaReadRow
                        {
                            TimestampMs = new DateTimeOffset(combatEvent.TimestampUtc, TimeSpan.Zero)
                                .ToUnixTimeMilliseconds(),
                            EncounterStartedAtMs = _encounterStartedAtMs,
                            PersonaSessionId = _personaSessionId,
                            NpcName = combatEvent.NpcName,
                            NpcGroup = NpcGroups.Normalize(combatEvent.NpcName),
                            PoolKey = NpcPoolKey.For(combatEvent.NpcName),
                            PrintedLow = staLow,
                            PrintedHigh = staHigh,
                            RawText = combatEvent.RawText,
                        });
                    }
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
                    {
                        // The creature's weapon as of THIS blow, from the same per-NPC state the row
                        // itself is built from - not the weapon it ends the fight holding. A creature
                        // that swaps mid-fight landed its earlier blows with the earlier weapon, and
                        // attributing them all to the last one would quietly corrupt the per-weapon
                        // history this is being collected for.
                        _encounterTaken.Add(
                            (combatEvent.NpcName, FightForLocked(combatEvent)?.NpcWeapon, taken));
                    }
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
            PersonaSessionId = _personaSessionId,
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

            // The countdown as the game gave it, raw and unconverted - a reading, not a key.
            TimeToReset = _lastStats.TimeToReset,
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
    // every hit records exactly 0 damage.
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

    /// <summary>Hands one row to the store. Cheap, never blocks, never throws; the write happens on
    /// the store's single background task. Three shapes go down that one queue - the per-swing
    /// stream, the rare `diagnose` reading and the score announcements - and one ordered channel is
    /// what keeps the order between them, which is the whole basis on which an award is later
    /// attributed to a kill.</summary>
    private void AppendLocked(IStoreRow row) => _store.Enqueue(row);
}
