using MudSharp.Combat;

namespace Mucka.ViewModels;

public sealed record CombatEncounterSnapshot(
    bool HasEncounter,
    bool InCombat,
    // StartedUtc: when this encounter began, or null when idle. Needed so the history comparison can
    // EXCLUDE this encounter's own rows: FightHistoryRecorder flushes them to the store before the
    // view model refreshes, so without this filter the readout compares the fight against itself.
    DateTime? StartedUtc,
    string? CurrentWeapon,
    IReadOnlyList<string> ActiveNpcs,
    int YouHits,
    int YouMisses,
    int TheyHits,
    int TheyMisses,
    double YouHitRate,
    double TheyHitRate,
    double ApproxDamageDone,
    double ApproxDamageTaken,
    TimeSpan Duration,
    double ApproxDps,
    // TheirApproxDps: the NPC side's damage rate. Without it the readout answered only half of
    // "am I winning this". (Plain comment, not an XML one: a positional record parameter is not a
    // valid target for /// and warns CS1587.)
    double TheirApproxDps,
    IReadOnlyList<FightSnapshot> Fights);

/// <summary>One NPC's fight within the current encounter, in first-engaged order. Includes fights
/// that have already resolved, so a multi-NPC encounter shows "goat kill / ram live" rather than
/// silently dropping the finished one.</summary>
public sealed record FightSnapshot(
    string NpcName,
    string NpcGroup,
    string? Weapon,
    int YouHits,
    int YouMisses,
    int TheyHits,
    int TheyMisses,
    double ApproxDamageDone,
    double ApproxDamageTaken,
    TimeSpan Duration,
    FightOutcome Outcome,
    bool IsResolved);

public sealed class CombatStatsAggregator
{
    private readonly HashSet<string> _activeNpcSet = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _activeNpcOrder = new();
    private readonly Dictionary<string, string> _npcWeapons = new(StringComparer.OrdinalIgnoreCase);
    // Per-NPC fights within this encounter, in first-engaged order. Retains RESOLVED fights too
    // (unlike _activeNpcOrder, which only tracks who is still up) so the display can show how each
    // one ended, and so a rejoining NPC does not silently reset its own tally.
    private readonly Dictionary<string, FightAccumulator> _fights = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FightAccumulator> _fightOrder = new();

    private DateTime? _encounterStartUtc;
    private string? _currentWeapon;
    private int _youHits;
    private int _youMisses;
    private int _theyHits;
    private int _theyMisses;
    private double _approxDamageDone;
    private double _approxDamageTaken;
    // Continuously-updated "last known player stamina", fed by EVERY external stat reading:
    // qs/heartbeat probes, natural 1-point regen ticks, the dreamword's stamina recovery, the
    // temporary-heal spell, eating a wafer, etc. — anything GameLineAnalyzer recognises. This is
    // the single running source of truth an NPC hit's damage is diffed against, so healing/regen
    // that happens on OTHER lines between hits correctly revises the baseline rather than being
    // silently absorbed into (or wrongly blamed on) the next hit's delta.
    private int? _lastKnownStamina;
    // One-shot relay from ObserveStamina to the very next ObserveDamageTaken call: the value
    // _lastKnownStamina held immediately BEFORE its most recent update. Needed because an NPC hit
    // line like "The zombie hits you (95/100)." is parsed TWICE for the SAME line — once
    // generically by GameLineAnalyzer (fires StatsUpdated -> ObserveStamina(95) FIRST, since
    // MudStreamParser raises StatsUpdated before LineReady/_combat.Observe for a line) and once by
    // CombatTracker's HitByNpc regex (RangeLow=95, reaching ObserveDamageTaken(95) SECOND). Without
    // this relay, _lastKnownStamina would already equal 95 by the time the hit's own delta is
    // computed, making every hit's delta compute to exactly 0 (confirmed live: damage taken always
    // showed 0.0, most visible on single-hit fights since NPCs miss often). ObserveDamageTaken
    // consumes (nulls) it after use so an unrelated later hit falls back to the running
    // _lastKnownStamina chain instead of a stale relay.
    private int? _pendingPreUpdateStamina;

    public bool InCombat { get; private set; }
    public bool HasEncounter => _encounterStartUtc is not null;

    public void BeginEncounter(DateTime startedUtc)
    {
        InCombat = true;
        _encounterStartUtc = startedUtc;
        _currentWeapon = null;
        _youHits = 0;
        _youMisses = 0;
        _theyHits = 0;
        _theyMisses = 0;
        _approxDamageDone = 0;
        _approxDamageTaken = 0;
        _activeNpcSet.Clear();
        _activeNpcOrder.Clear();
        _npcWeapons.Clear();
        _fights.Clear();
        _fightOrder.Clear();
    }

    public void EndEncounter() => InCombat = false;

    public void Reset()
    {
        InCombat = false;
        _encounterStartUtc = null;
        _currentWeapon = null;
        _youHits = 0;
        _youMisses = 0;
        _theyHits = 0;
        _theyMisses = 0;
        _approxDamageDone = 0;
        _approxDamageTaken = 0;
        _activeNpcSet.Clear();
        _activeNpcOrder.Clear();
        _fights.Clear();
        _fightOrder.Clear();
    }

    public void ObserveStamina(int? currentStamina)
    {
        if (currentStamina is null)
            return;

        _pendingPreUpdateStamina = _lastKnownStamina;
        _lastKnownStamina = currentStamina.Value;
    }

    public void Observe(CombatEvent combatEvent)
    {
        if (!InCombat)
        {
            if (combatEvent.Kind != CombatEventKind.FightStart)
                return;
            BeginEncounter(combatEvent.TimestampUtc);
        }
        else if (_encounterStartUtc is null)
        {
            BeginEncounter(combatEvent.TimestampUtc);
        }

        switch (combatEvent.Kind)
        {
            case CombatEventKind.FightStart:
                AddParticipant(combatEvent.NpcName);
                if (!string.IsNullOrWhiteSpace(combatEvent.Weapon))
                    _currentWeapon = combatEvent.Weapon;
                FightFor(combatEvent)?.NoteWeapon(_currentWeapon);
                break;

            case CombatEventKind.WeaponEquip:
                if (!string.IsNullOrWhiteSpace(combatEvent.Weapon))
                    _currentWeapon = combatEvent.Weapon;
                // A new weapon applies to every fight still in progress: MUD2 extends the weapon
                // you are wielding to all your active fights, it does not scope it to one target.
                foreach (var fight in _fightOrder)
                {
                    if (!fight.IsResolved)
                        fight.NoteWeapon(_currentWeapon);
                }
                break;

            case CombatEventKind.NpcWeaponEquip:
                AddParticipant(combatEvent.NpcName);
                if (!string.IsNullOrWhiteSpace(combatEvent.NpcName) && !string.IsNullOrWhiteSpace(combatEvent.Weapon))
                    _npcWeapons[combatEvent.NpcName] = combatEvent.Weapon;
                FightFor(combatEvent);
                break;

            case CombatEventKind.WeaponBroke:
                _currentWeapon = null;
                foreach (var fight in _fightOrder)
                {
                    if (!fight.IsResolved)
                        fight.NoteWeaponBroke();
                }
                break;

            case CombatEventKind.Hit:
                _youHits++;
                AddParticipant(combatEvent.NpcName);
                if (combatEvent.RangeLow is int low && combatEvent.RangeHigh is int high)
                    _approxDamageDone += (low + high) / 2.0;
                FightFor(combatEvent)?.AddYouHit(combatEvent.RangeLow, combatEvent.RangeHigh);
                break;

            case CombatEventKind.Miss:
                _youMisses++;
                AddParticipant(combatEvent.NpcName);
                FightFor(combatEvent)?.AddYouMiss();
                break;

            case CombatEventKind.HitByNpc:
                _theyHits++;
                AddParticipant(combatEvent.NpcName);
                // The encounter-level baseline chain owns the delta; the fight bucket receives the
                // already-resolved figure so both agree and the baseline is only advanced once.
                var damageTaken = ObserveDamageTaken(combatEvent.RangeLow);
                FightFor(combatEvent)?.AddTheyHit(damageTaken);
                break;

            case CombatEventKind.MissByNpc:
                _theyMisses++;
                AddParticipant(combatEvent.NpcName);
                FightFor(combatEvent)?.AddTheyMiss();
                break;

            case CombatEventKind.Kill:
            case CombatEventKind.Withdrawn:
            case CombatEventKind.NpcFled:
                ResolveFight(combatEvent, OutcomeFor(combatEvent.Kind));
                RemoveParticipant(combatEvent.NpcName);
                break;

            case CombatEventKind.KilledByNpc:
                // The player died, which ends the WHOLE encounter — CombatTracker emits this once
                // naming only the killer and then calls EndAll(), so no other fight gets its own
                // close event. Resolve them all: "this fight ended with me dead" is true of every
                // one of them, and leaving the others Unresolved would understate how badly a
                // pile-on went.
                foreach (var fight in _fightOrder)
                    fight.Resolve(FightOutcome.KilledByNpc, combatEvent.TimestampUtc);
                RemoveParticipant(combatEvent.NpcName);
                break;

            case CombatEventKind.YouFled:
                // Fleeing ends EVERY active fight, and none of them name themselves on this line.
                foreach (var fight in _fightOrder)
                    fight.Resolve(FightOutcome.YouFled, combatEvent.TimestampUtc);
                _activeNpcSet.Clear();
                _activeNpcOrder.Clear();
                _npcWeapons.Clear();
                break;

            // FightEndOther ("You can fight it no longer.") is deliberately NOT treated as a
            // close here, mirroring CombatTracker's own fix: it is a trailing acknowledgment
            // (typically following an already-processed NpcFled, or — confirmed against real
            // aquatic-NPC clogs — a dive/submerge re-engagement cycle) and must never clear
            // OTHER still-active participants in a multi-NPC fight. No-op by design.
            case CombatEventKind.FightEndOther:
                break;
        }
    }

    public CombatEncounterSnapshot Snapshot(DateTime nowUtc)
    {
        var duration = _encounterStartUtc is null ? TimeSpan.Zero : nowUtc - _encounterStartUtc.Value;
        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;

        var yourAttempts = _youHits + _youMisses;
        var theirAttempts = _theyHits + _theyMisses;
        var yourHitRate = yourAttempts == 0 ? 0 : _youHits / (double)yourAttempts;
        var theirHitRate = theirAttempts == 0 ? 0 : _theyHits / (double)theirAttempts;
        var durationSeconds = duration.TotalSeconds;
        var dps = durationSeconds <= 0 ? 0 : _approxDamageDone / durationSeconds;
        var theirDps = durationSeconds <= 0 ? 0 : _approxDamageTaken / durationSeconds;

        return new CombatEncounterSnapshot(
            HasEncounter,
            InCombat,
            _encounterStartUtc,
            _currentWeapon,
            FormatActiveNpcs(),
            _youHits,
            _youMisses,
            _theyHits,
            _theyMisses,
            yourHitRate,
            theirHitRate,
            _approxDamageDone,
            _approxDamageTaken,
            duration,
            dps,
            theirDps,
            BuildFightSnapshots(nowUtc));
    }

    /// <summary>The per-NPC fights of this encounter, in first-engaged order.</summary>
    public IReadOnlyList<FightAccumulator> Fights => _fightOrder;

    private IReadOnlyList<FightSnapshot> BuildFightSnapshots(DateTime nowUtc)
    {
        if (_fightOrder.Count == 0)
            return Array.Empty<FightSnapshot>();

        var result = new List<FightSnapshot>(_fightOrder.Count);
        foreach (var fight in _fightOrder)
        {
            result.Add(new FightSnapshot(
                fight.NpcName,
                fight.NpcGroup,
                fight.WeaponUsed,
                fight.YouHits,
                fight.YouMisses,
                fight.TheyHits,
                fight.TheyMisses,
                fight.ApproxDamageDone,
                fight.ApproxDamageTaken,
                fight.DurationAt(nowUtc),
                fight.Outcome,
                fight.IsResolved));
        }

        return result;
    }

    /// <summary>Gets (or lazily creates) the fight bucket for an event's NPC. Creation seeds the
    /// weapon from the encounter's current one, because a fight that JOINS mid-encounter never gets
    /// its own equip line — MUD2 silently extends your wielded weapon to the new attacker.</summary>
    private FightAccumulator? FightFor(CombatEvent combatEvent)
    {
        var npcName = combatEvent.NpcName;
        if (string.IsNullOrWhiteSpace(npcName))
            return null;

        if (_fights.TryGetValue(npcName, out var existing))
            return existing;

        var fight = new FightAccumulator(npcName, combatEvent.TimestampUtc, _currentWeapon);
        _fights[npcName] = fight;
        _fightOrder.Add(fight);
        return fight;
    }

    private void ResolveFight(CombatEvent combatEvent, FightOutcome outcome)
        => FightFor(combatEvent)?.Resolve(outcome, combatEvent.TimestampUtc);

    private static FightOutcome OutcomeFor(CombatEventKind kind) => kind switch
    {
        CombatEventKind.Kill => FightOutcome.Killed,
        CombatEventKind.KilledByNpc => FightOutcome.KilledByNpc,
        CombatEventKind.Withdrawn => FightOutcome.Withdrawn,
        CombatEventKind.NpcFled => FightOutcome.NpcFled,
        CombatEventKind.YouFled => FightOutcome.YouFled,
        _ => FightOutcome.Unresolved,
    };

    /// <summary>Returns the stamina delta attributed to this blow, so the caller can credit it to
    /// the right per-NPC fight, or null when no baseline was available.</summary>
    private double? ObserveDamageTaken(int? currentStamina)
    {
        if (currentStamina is null)
            return null;

        // Trust the one-shot relay ONLY when the most recent ObserveStamina call was for this
        // exact value — i.e. it really did fire for this SAME line (see _pendingPreUpdateStamina's
        // field comment). Without this equality guard, a stale relay left over from an earlier,
        // unrelated update (never consumed because no combat event followed it) could wrongly
        // outrank a more recent, already-correct _lastKnownStamina. When they don't match — e.g. a
        // hit that drops the player to exactly 0 stamina, where GameLineAnalyzer's compact-stamina
        // scan requires sta > 0 and so never fired for this line at all — _lastKnownStamina simply
        // wasn't touched by this line and already holds the correct pre-hit baseline directly.
        var baseline = _pendingPreUpdateStamina is not null && _lastKnownStamina == currentStamina
            ? _pendingPreUpdateStamina
            : _lastKnownStamina;

        double? attributed = null;
        if (baseline is not null)
        {
            var delta = baseline.Value - currentStamina.Value;
            if (delta >= 0)
            {
                _approxDamageTaken += delta;
                attributed = delta;
            }
        }

        _lastKnownStamina = currentStamina.Value;
        _pendingPreUpdateStamina = null;
        return attributed;
    }

    private void AddParticipant(string? npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName) || !_activeNpcSet.Add(npcName))
            return;

        _activeNpcOrder.Add(npcName);
    }

    private void RemoveParticipant(string? npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName) || !_activeNpcSet.Remove(npcName))
            return;

        _activeNpcOrder.RemoveAll(name => string.Equals(name, npcName, StringComparison.OrdinalIgnoreCase));
        _npcWeapons.Remove(npcName);
    }

    /// <summary>Formats each active NPC name with its last-observed weapon, e.g. "zombie (fork)",
    /// when one has been confirmed via a "The X has started to use the Y to fight!" line. Most
    /// NPCs never announce a weapon (fists/claws/bite presumably), so the common case is just the
    /// bare name.</summary>
    private IReadOnlyList<string> FormatActiveNpcs()
    {
        if (_activeNpcOrder.Count == 0)
            return Array.Empty<string>();

        var result = new List<string>(_activeNpcOrder.Count);
        foreach (var name in _activeNpcOrder)
        {
            result.Add(_npcWeapons.TryGetValue(name, out var weapon)
                ? $"{name} ({weapon})"
                : name);
        }

        return result;
    }
}
