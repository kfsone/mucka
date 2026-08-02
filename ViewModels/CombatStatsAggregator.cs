using MudSharp.Combat;

namespace Mucka.ViewModels;

public sealed record CombatEncounterSnapshot(
    bool HasEncounter,
    bool InCombat,
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
    double ApproxDps);

public sealed class CombatStatsAggregator
{
    private readonly HashSet<string> _activeNpcSet = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _activeNpcOrder = new();
    private readonly Dictionary<string, string> _npcWeapons = new(StringComparer.OrdinalIgnoreCase);

    private DateTime? _encounterStartUtc;
    private string? _currentWeapon;
    private int _youHits;
    private int _youMisses;
    private int _theyHits;
    private int _theyMisses;
    private double _approxDamageDone;
    private double _approxDamageTaken;
    // Fed continuously by every external stat reading (qs/heartbeat/etc.) — used ONLY to seed
    // _combatBaselineStamina at the start of the NEXT encounter (see BeginEncounter). Must never
    // be read directly inside ObserveDamageTaken: MudStreamParser fires StatsUpdated for a line
    // BEFORE LineReady/_combat.Observe for that SAME line, and GameLineAnalyzer's own
    // CombatStaminaRegex extracts the identical "(cur/max)" pair embedded in an NPC hit line
    // (e.g. "The zombie hits you (95/100)."), so by the time CombatTracker's HitByNpc reaches
    // here _lastKnownStamina has ALREADY been overwritten with that same hit's own post-hit
    // value — diffing against it would always yield delta 0 (confirmed live: damage taken always
    // showed 0.0). _combatBaselineStamina below is the delta-tracking chain instead, decoupled
    // from this field once an encounter is underway.
    private int? _lastKnownStamina;
    // Dedicated pre-hit baseline for HitByNpc delta tracking, seeded once from _lastKnownStamina
    // at BeginEncounter (a FightStart line never itself embeds a stamina pair, so that snapshot
    // predates any in-fight hit) and then updated ONLY by ObserveDamageTaken itself thereafter —
    // never re-seeded from ObserveStamina mid-encounter, which is what avoids the race above.
    private int? _combatBaselineStamina;

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
        _combatBaselineStamina = _lastKnownStamina;
        _activeNpcSet.Clear();
        _activeNpcOrder.Clear();
        _npcWeapons.Clear();
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
        _combatBaselineStamina = null;
        _activeNpcSet.Clear();
        _activeNpcOrder.Clear();
    }

    public void ObserveStamina(int? currentStamina)
    {
        if (currentStamina is not null)
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
                break;

            case CombatEventKind.WeaponEquip:
                if (!string.IsNullOrWhiteSpace(combatEvent.Weapon))
                    _currentWeapon = combatEvent.Weapon;
                break;

            case CombatEventKind.NpcWeaponEquip:
                AddParticipant(combatEvent.NpcName);
                if (!string.IsNullOrWhiteSpace(combatEvent.NpcName) && !string.IsNullOrWhiteSpace(combatEvent.Weapon))
                    _npcWeapons[combatEvent.NpcName] = combatEvent.Weapon;
                break;

            case CombatEventKind.WeaponBroke:
                _currentWeapon = null;
                break;

            case CombatEventKind.Hit:
                _youHits++;
                AddParticipant(combatEvent.NpcName);
                if (combatEvent.RangeLow is int low && combatEvent.RangeHigh is int high)
                    _approxDamageDone += (low + high) / 2.0;
                break;

            case CombatEventKind.Miss:
                _youMisses++;
                AddParticipant(combatEvent.NpcName);
                break;

            case CombatEventKind.HitByNpc:
                _theyHits++;
                AddParticipant(combatEvent.NpcName);
                ObserveDamageTaken(combatEvent.RangeLow);
                break;

            case CombatEventKind.MissByNpc:
                _theyMisses++;
                AddParticipant(combatEvent.NpcName);
                break;

            case CombatEventKind.Kill:
            case CombatEventKind.KilledByNpc:
            case CombatEventKind.Withdrawn:
            case CombatEventKind.NpcFled:
                RemoveParticipant(combatEvent.NpcName);
                break;

            case CombatEventKind.YouFled:
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

        return new CombatEncounterSnapshot(
            HasEncounter,
            InCombat,
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
            dps);
    }

    private void ObserveDamageTaken(int? currentStamina)
    {
        if (currentStamina is null)
            return;

        // Deliberately diffs against _combatBaselineStamina, NOT _lastKnownStamina — see the
        // field comment above _lastKnownStamina for why using the latter here always produced a
        // delta of exactly 0 in production.
        if (_combatBaselineStamina is not null)
        {
            var delta = _combatBaselineStamina.Value - currentStamina.Value;
            if (delta >= 0)
                _approxDamageTaken += delta;
        }

        _combatBaselineStamina = currentStamina.Value;
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
