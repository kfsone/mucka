using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using MudSharp.Combat;
using MudSharp.Models;

namespace Mucka.Core;

/// <summary>
/// One swing, either direction, as persisted to ~/.mucka/clogs/swings.jsonl. Field names and order
/// are the ones pinned in tools/combat/SWING-LEDGER-SPEC.md section 3 - they are the wire format the
/// offline ingester reads, so renaming one is a schema change, not a refactor.
///
/// <para>Nulls are written rather than omitted. Half the damage fields are direction-specific by
/// construction (a bracket going out, an exact figure coming in - see <see cref="Damage"/>), and a
/// row that spells out "this side has nothing" is self-describing to anything reading the file
/// without the spec in hand. Same choice fights.jsonl already makes.</para>
/// </summary>
public sealed record SwingRow
{
    /// <summary>Schema version of THIS row. There is no in-client reader for swings.jsonl yet (the
    /// live panel reads the separate aggregate index, spec section 5; the raw stream is for offline
    /// mining), so nothing migrates on it today - it exists so the first reader that does appear can
    /// tell rows apart without guessing, the way FightRecord.FormatVersion earned its keep.</summary>
    [JsonPropertyName("v")] public int Version { get; init; } = CurrentVersion;

    [JsonIgnore] public const int CurrentVersion = 1;

    /// <summary>"out" - the player swinging.</summary>
    [JsonIgnore] public const string DirectionOut = "out";
    /// <summary>"in" - the creature swinging at the player.</summary>
    [JsonIgnore] public const string DirectionIn = "in";

    /// <summary>Unix ms, taken from <see cref="CombatEvent.TimestampUtc"/> - the instant the line
    /// completed on the Feed thread. Never re-stamped here: a consumer's own clock reading would be
    /// later than the event by however long the fan-out took, and the whole point of an ordered
    /// stream is that "what was happening around this swing" survives.</summary>
    [JsonPropertyName("ts")] public long TimestampMs { get; init; }

    [JsonPropertyName("dir")] public string Direction { get; init; } = DirectionOut;

    /// <summary>The character swinging/being swung at (MudSession.CharacterIdentified). Null only
    /// for swings landing in the window between game-mode entry and the setup <c>score</c> reply -
    /// same gap FightRecord.CharacterName documents.</summary>
    [JsonPropertyName("persona")] public string? Persona { get; init; }

    /// <summary>Always null for now, and deliberately shipped anyway. The client only ever knows a
    /// character's sex for characters IT created (GuidedLoginController.ConfirmCreateSex); nothing in
    /// the FES heartbeat or the <c>score</c> reply carries it for an existing persona. Populate it if
    /// a parse ever turns one up - do not invent a source (SWING-LEDGER-SPEC.md section 3).</summary>
    [JsonPropertyName("gender")] public string? Gender { get; init; }

    /// <summary>Player stamina from the most recent stats snapshot. For <c>dir=in</c> this is the
    /// POST-hit reading: MUD2 embeds "(cur/max)" in the hit line itself and the generic stats scan
    /// consumes it before the combat classifier sees the line, so pre-hit stamina is
    /// <c>sta + dmg</c>, not <c>sta</c>.</summary>
    [JsonPropertyName("sta")] public int? Stamina { get; init; }

    /// <summary>EFFECTIVE strength/dexterity, not raw, and that is the point: these are what the
    /// hit-chance and damage formulas consume, and they move with stamina and carried weight. Raw
    /// values would throw away the variable under test (SWING-LEDGER-SPEC.md section 3).</summary>
    [JsonPropertyName("str")] public int? Strength { get; init; }
    [JsonPropertyName("dex")] public int? Dexterity { get; init; }

    [JsonPropertyName("sta_max")] public int? MaxStamina { get; init; }
    [JsonPropertyName("blind")] public bool IsBlind { get; init; }

    /// <summary>The instance name exactly as the game gave it ("rat0"), so a single unusually tough
    /// spawn stays distinguishable from its group.</summary>
    [JsonPropertyName("npc")] public string? NpcName { get; init; }

    /// <summary><see cref="NpcGroups.Normalize"/>d, which is the same normalisation
    /// reduce_combat.py applies - live and offline rows must bucket identically or the two halves of
    /// the pipeline silently disagree about history.</summary>
    [JsonPropertyName("group")] public string NpcGroup { get; init; } = string.Empty;

    /// <summary><c>dir=out</c>: what the player had in hand at this instant. <c>dir=in</c>: the
    /// creature's own weapon, which it arms independently and which materially changes its output -
    /// see FightAccumulator.NpcWeapon. Null on either side means unarmed/never announced.</summary>
    [JsonPropertyName("weapon")] public string? Weapon { get; init; }

    [JsonPropertyName("hit")] public bool Hit { get; init; }

    /// <summary>The game's own damage bracket, <c>dir=out</c> hits only - MUD2 never gives the player
    /// an exact figure for their own blows. Stored as both ends rather than a midpoint: averaging in
    /// the ledger would destroy information the consumer might want (SWING-LEDGER-SPEC.md section 3).
    /// </summary>
    [JsonPropertyName("dmg_low")] public int? DamageLow { get; init; }
    [JsonPropertyName("dmg_high")] public int? DamageHigh { get; init; }

    /// <summary>Exact damage, <c>dir=in</c> hits only, from the stamina delta. Null when no baseline
    /// was available to diff against - see <see cref="SwingLedger"/>'s stamina relay, which exists
    /// because the naive delta computes to zero every time.</summary>
    [JsonPropertyName("dmg")] public int? Damage { get; init; }

    /// <summary>The creature's health rung BEFORE this swing, 1-7 on NpcHealthRungs' scale, with the
    /// game's own wording. Null until the creature has been described at all. "Before" is not a
    /// nicety: MUD2 prints the descriptor on the line AFTER a landed blow, so the reading in hand
    /// when a swing is classified is the state that swing was aimed at - which is exactly what
    /// "does a wounded creature hit softer" needs (SWING-LEDGER-SPEC.md section 4).</summary>
    [JsonPropertyName("rung")] public int? HealthRung { get; init; }
    [JsonPropertyName("rung_phrase")] public string? HealthPhrase { get; init; }
}

/// <summary>
/// Appends one <see cref="SwingRow"/> per swing, both directions, to ~/.mucka/clogs/swings.jsonl.
/// This is the per-swing evidence base tools/combat/SWING-LEDGER-SPEC.md specifies: the fights.jsonl
/// rollup answers "how did that fight go", and could never answer "how hard does this thing hit at
/// rung 3 while I am below the stamina knee", because the individual swings were never kept.
///
/// <para>JSONL rather than the offline SQLite: taking Microsoft.Data.Sqlite plus its native library
/// into a MAUI app on two platforms, for a writer that only ever appends, buys nothing the offline
/// ingester does not already provide. FightHistoryStore records the same reasoning for fights.jsonl;
/// section 1 of the spec settles it for this file.</para>
///
/// <para>Always on, unlike ClogWriter's "$clog on" gate and exactly like fights.jsonl: a switch means
/// missing data precisely when something interesting happened, and everything downstream is built on
/// this file being continuous.</para>
///
/// <para>Threading: every On* method is called from the session Feed thread (same contract as
/// ClogWriter and FightHistoryRecorder) and does nothing but cheap in-memory bookkeeping plus a
/// serialize-and-enqueue. The disk write happens on a single background task
/// (<see cref="DrainAsync"/>), so the thread parsing incoming combat text never pays for it -
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
    /// <summary>Standard file name, alongside fights.jsonl and the per-encounter clogs. The directory
    /// is supplied by the caller (see MuckaConnection) so this type needs no platform/MAUI path lookup
    /// of its own and can be linked into mudsharp.Tests as-is.</summary>
    public const string DefaultFileName = "swings.jsonl";

    private readonly object _lock = new();
    private readonly string _filePath;
    // Injected rather than calling CrashLog directly so this type stays free of MAUI references and
    // can be exercised against a temp directory in mudsharp.Tests.
    private readonly Action<string, Exception>? _onError;

    // One dedicated writer for this ledger's whole lifetime. Unbounded: a swing arrives at most once
    // per tick per participant, so there is no realistic burst worth backpressuring, and dropping a
    // row to save a few bytes of queue would defeat the point of the file.
    private readonly Channel<string> _writeQueue =
        Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Task _writerTask;

    // Per-NPC memory for the CURRENT encounter, keyed by instance name. Cleared at encounter end so a
    // later fight against a respawned "rat0" cannot inherit the dead one's health reading.
    private readonly Dictionary<string, FightAccumulator> _fights = new(StringComparer.OrdinalIgnoreCase);

    private GameStatsSnapshot _lastStats = GameStatsSnapshot.Empty;
    private string? _persona;

    private string? _currentWeapon;
    // When _currentWeapon was last confirmed, so an equip seen just before the client noticed the
    // fight can be carried into it while a stale one is discarded - see _encounterJustOpened.
    private DateTime _currentWeaponUtc;
    private static readonly TimeSpan PendingWeaponWindow = TimeSpan.FromSeconds(5);
    // Set when an encounter opens, consumed by the first combat event in it. The latch is resolved
    // there rather than in OnInCombatChanged because that event carries no timestamp, and the sibling
    // recorders' DateTime.UtcNow fallback compares a wall-clock reading against a stamp taken by the
    // tracker - two different clocks, agreeing only to within however long the fan-out took. Every
    // event that could open an encounter arrives immediately after the flag is set (CombatTracker
    // raises InCombatChanged from Begin(), before Emit() for the same line), so this costs nothing and
    // keeps the whole class on the tracker's own clock.
    private bool _encounterJustOpened;

    private int? _lastKnownStamina;
    private int? _pendingPreUpdateStamina;

    public SwingLedger(string filePath, Action<string, Exception>? onError = null)
    {
        _filePath = filePath;
        _onError = onError;
        _writerTask = Task.Run(DrainAsync);
    }

    public string FilePath => _filePath;

    public void OnStatsUpdated(GameStatsSnapshot stats)
    {
        lock (_lock)
        {
            _lastStats = stats;
            ObserveStaminaLocked(stats.Stamina);
        }
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

    public void OnInCombatChanged(bool inCombat)
    {
        lock (_lock)
        {
            if (inCombat)
            {
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
                    FightForLocked(combatEvent)?.NoteNpcWeapon(combatEvent.Weapon, combatEvent.TimestampUtc);
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
                    AppendLocked(BuildRowLocked(combatEvent, SwingRow.DirectionOut, hit: true, damage: null));
                    break;

                case CombatEventKind.Miss:
                    AppendLocked(BuildRowLocked(combatEvent, SwingRow.DirectionOut, hit: false, damage: null));
                    break;

                case CombatEventKind.HitByNpc:
                    // The delta must be resolved BEFORE the row is built, and exactly once: it advances
                    // the stamina baseline (see ResolveDamageTakenLocked).
                    var damage = ResolveDamageTakenLocked(combatEvent.RangeLow);
                    AppendLocked(BuildRowLocked(combatEvent, SwingRow.DirectionIn, hit: true, damage));
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
    private SwingRow BuildRowLocked(CombatEvent combatEvent, string direction, bool hit, int? damage)
    {
        var outgoing = direction == SwingRow.DirectionOut;
        var fight = FightForLocked(combatEvent);

        return new SwingRow
        {
            TimestampMs = new DateTimeOffset(combatEvent.TimestampUtc, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            Direction = direction,
            Persona = _persona,
            Gender = null,
            Stamina = _lastStats.Stamina,
            Strength = _lastStats.Strength,
            Dexterity = _lastStats.Dexterity,
            MaxStamina = _lastStats.MaxStamina,
            IsBlind = _lastStats.IsBlind,
            NpcName = combatEvent.NpcName,
            NpcGroup = NpcGroups.Normalize(combatEvent.NpcName),
            // Outgoing: what is in the player's hands right now, not the fight's rollup weapon - a
            // per-swing row wants the per-swing answer, and MUD2 extends one wielded weapon across
            // every fight in the encounter anyway. Incoming: this creature's own, which it arms
            // independently of the player.
            Weapon = outgoing ? _currentWeapon : fight?.NpcWeapon,
            Hit = hit,
            // RangeLow/RangeHigh mean different things per direction and must never be crossed over:
            // outgoing they are the damage bracket, incoming they are the player's post-hit
            // (current/max) stamina, which is where `dmg` comes from instead.
            DamageLow = outgoing && hit ? combatEvent.RangeLow : null,
            DamageHigh = outgoing && hit ? combatEvent.RangeHigh : null,
            Damage = outgoing ? null : damage,
            HealthRung = fight?.HealthRung,
            HealthPhrase = fight?.HealthPhrase,
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

    // Same one-shot relay both other consumers use: an NPC hit line like "The zombie hits you
    // (95/100)." is parsed TWICE for the SAME line - generically by GameLineAnalyzer (StatsUpdated ->
    // ObserveStaminaLocked(95) first) and then by CombatTracker's HitByNpc regex (RangeLow=95, reaching
    // ResolveDamageTakenLocked second). Without stashing the pre-line value, _lastKnownStamina already
    // equals 95 by the time the delta is computed and EVERY hit records exactly 0 damage - confirmed
    // live before the fix. See CombatStatsAggregator.ObserveDamageTaken for the full account.
    private void ObserveStaminaLocked(int? currentStamina)
    {
        if (currentStamina is null)
            return;

        _pendingPreUpdateStamina = _lastKnownStamina;
        _lastKnownStamina = currentStamina.Value;
    }

    private int? ResolveDamageTakenLocked(int? currentStamina)
    {
        if (currentStamina is null)
            return null;

        // Trust the relay ONLY when the last stats update was for this exact value, i.e. it really did
        // fire for this same line. A stale relay left over from an unrelated earlier update must not
        // outrank an already-correct _lastKnownStamina; and when they differ (e.g. a blow to exactly 0
        // stamina, which the compact-stamina scan does not fire for at all) _lastKnownStamina was
        // never touched by this line and already holds the pre-hit baseline.
        var baseline = _pendingPreUpdateStamina is not null && _lastKnownStamina == currentStamina
            ? _pendingPreUpdateStamina
            : _lastKnownStamina;

        int? attributed = null;
        if (baseline is not null)
        {
            var delta = baseline.Value - currentStamina.Value;
            // A negative delta means stamina went UP across the blow (regen or a heal landing in the
            // same tick outran it); there is no honest damage figure to record for that, and a
            // clamped 0 would read as "armour soaked it" - which is a different fact.
            if (delta >= 0)
                attributed = delta;
        }

        _lastKnownStamina = currentStamina.Value;
        _pendingPreUpdateStamina = null;
        return attributed;
    }

    /// <summary>Serializes (cheap, in-memory, stays on the Feed thread) and enqueues. TryWrite on an
    /// unbounded channel never blocks and only fails after Complete(), which only <see cref="Dispose"/>
    /// calls.</summary>
    private void AppendLocked(SwingRow row)
    {
        string line;
        try
        {
            line = JsonSerializer.Serialize(row);
        }
        catch (Exception ex)
        {
            _onError?.Invoke("SwingLedger.Serialize", ex);
            return;
        }

        _writeQueue.Writer.TryWrite(line);
    }

    /// <summary>The single background writer for this ledger's whole lifetime. Drains lines in the
    /// order they were enqueued and appends each to disk. <c>ReadAllAsync</c> completes normally only
    /// once the queue is both closed and fully drained, which is the "nothing queued is lost on
    /// shutdown" property <see cref="Dispose"/> relies on.</summary>
    private async Task DrainAsync()
    {
        await foreach (var line in _writeQueue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                var directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                // No BOM, one object per line - the offline ingester reads these with a plain
                // json.loads per line, which chokes on a BOM prefixed to the first line.
                File.AppendAllText(_filePath, line + Environment.NewLine, new System.Text.UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort by design: a failed write loses a row, never a fight.
                _onError?.Invoke("SwingLedger.Append", ex);
            }
        }
    }

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
