using MudSharp.Combat;
using MudSharp.Models;

namespace Mucka.Core;

/// <summary>
/// Watches the live combat stream on the session Feed thread and appends one <see cref="FightRecord"/>
/// to the history index per completed per-NPC fight.
///
/// <para>Runs its own <see cref="FightAccumulator"/> set rather than reading the view-model's:
/// SidePanelViewModel's aggregator lives on the UI thread and exists to drive display, while this
/// lives on the Feed thread and exists to produce immutable rows. Sharing one instance across both
/// threads would need locking on the typing hot path (Invariant #1). They consume the identical
/// event stream through the same FightAccumulator type, so they cannot disagree about a tally.</para>
///
/// <para>Unlike ClogWriter this is NOT gated on "$clog on". Encounter clogs are bulky raw evidence
/// worth opting into; a fight row is ~400 bytes and is the accumulating dataset the whole
/// comparison feature depends on, so it records always. Nothing here is written while the player
/// is not actually in a fight.</para>
/// </summary>
public sealed class FightHistoryRecorder
{
    private readonly FightHistoryStore _store;
    private readonly object _lock = new();
    private readonly Dictionary<string, FightAccumulator> _fights = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FightAccumulator> _fightOrder = [];

    private string? _currentWeapon;
    private int? _lastKnownStamina;
    private int? _pendingPreUpdateStamina;

    // Context captured at ENCOUNTER start and stamped onto every fight in it. A joiner inherits the
    // opening context because we never re-probe stats mid-encounter — see FightRecord's remarks.
    private GameStatsSnapshot _encounterStats = GameStatsSnapshot.Empty;
    private StatusEffectState _encounterEffects = StatusEffectState.Empty;
    private string? _encounterRoom;

    private GameStatsSnapshot _lastStats = GameStatsSnapshot.Empty;
    private StatusEffectState _lastEffects = StatusEffectState.Empty;
    private string? _lastRoom;

    public FightHistoryRecorder(FightHistoryStore store) => _store = store;

    public void OnStatsUpdated(GameStatsSnapshot stats)
    {
        lock (_lock)
        {
            _lastStats = stats;
            ObserveStaminaLocked(stats.Stamina);
        }
    }

    public void OnStatusEffectsChanged(StatusEffectState effects) => _lastEffects = effects;
    public void OnRoomShortReady(string room) => _lastRoom = room;

    public void OnInCombatChanged(bool inCombat)
    {
        lock (_lock)
        {
            if (inCombat)
            {
                // Freeze the pre-fight context now, before any combat line can move the stats.
                _encounterStats = _lastStats;
                _encounterEffects = _lastEffects;
                _encounterRoom = _lastRoom;
                _fights.Clear();
                _fightOrder.Clear();
                _currentWeapon = null;
                return;
            }

            FlushLocked();
        }
    }

    public void OnCombatEvent(CombatEvent combatEvent)
    {
        lock (_lock)
        {
            switch (combatEvent.Kind)
            {
                case CombatEventKind.FightStart:
                    if (!string.IsNullOrWhiteSpace(combatEvent.Weapon))
                        _currentWeapon = combatEvent.Weapon;
                    FightForLocked(combatEvent)?.NoteWeapon(_currentWeapon);
                    break;

                case CombatEventKind.WeaponEquip:
                    if (!string.IsNullOrWhiteSpace(combatEvent.Weapon))
                        _currentWeapon = combatEvent.Weapon;
                    foreach (var fight in _fightOrder)
                    {
                        if (!fight.IsResolved)
                            fight.NoteWeapon(_currentWeapon);
                    }
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
                    FightForLocked(combatEvent)?.AddYouHit(combatEvent.RangeLow, combatEvent.RangeHigh);
                    break;

                case CombatEventKind.Miss:
                    FightForLocked(combatEvent)?.AddYouMiss();
                    break;

                case CombatEventKind.HitByNpc:
                    FightForLocked(combatEvent)?.AddTheyHit(ResolveDamageTakenLocked(combatEvent.RangeLow));
                    break;

                case CombatEventKind.MissByNpc:
                    FightForLocked(combatEvent)?.AddTheyMiss();
                    break;

                case CombatEventKind.NpcWeaponEquip:
                    FightForLocked(combatEvent);
                    break;

                case CombatEventKind.Kill:
                    FightForLocked(combatEvent)?.Resolve(FightOutcome.Killed, combatEvent.TimestampUtc);
                    break;

                case CombatEventKind.Withdrawn:
                    FightForLocked(combatEvent)?.Resolve(FightOutcome.Withdrawn, combatEvent.TimestampUtc);
                    break;

                case CombatEventKind.NpcFled:
                    FightForLocked(combatEvent)?.Resolve(FightOutcome.NpcFled, combatEvent.TimestampUtc);
                    break;

                case CombatEventKind.KilledByNpc:
                    // Player death ends the whole encounter; CombatTracker names only the killer and
                    // then closes the rest silently, so resolve every open fight here.
                    foreach (var fight in _fightOrder)
                        fight.Resolve(FightOutcome.KilledByNpc, combatEvent.TimestampUtc);
                    break;

                case CombatEventKind.YouFled:
                    foreach (var fight in _fightOrder)
                        fight.Resolve(FightOutcome.YouFled, combatEvent.TimestampUtc);
                    break;
            }
        }
    }

    /// <summary>Writes out every fight of the encounter that just closed. Unresolved fights are
    /// still written, flagged <see cref="FightOutcome.Unresolved"/>: an encounter that ended by
    /// reset/disconnect is real evidence about duration and damage even though the outcome is
    /// unknown, and silently dropping those would bias the record toward decisive fights.</summary>
    private void FlushLocked()
    {
        if (_fightOrder.Count == 0)
            return;

        var endedUtc = DateTime.UtcNow;
        foreach (var fight in _fightOrder)
            _store.Append(BuildRecord(fight, endedUtc));

        _fights.Clear();
        _fightOrder.Clear();
        _currentWeapon = null;
    }

    private FightRecord BuildRecord(FightAccumulator fight, DateTime encounterEndUtc)
    {
        var endedUtc = fight.EndedUtc ?? encounterEndUtc;
        var duration = endedUtc - fight.StartedUtc;
        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;

        var swings = fight.YouHits + fight.YouMisses + fight.TheyHits + fight.TheyMisses;

        return new FightRecord
        {
            StartedAtMs = new DateTimeOffset(fight.StartedUtc, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            EndedAtMs = new DateTimeOffset(endedUtc, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            DurationMs = (long)duration.TotalMilliseconds,
            NpcName = fight.NpcName,
            NpcGroup = fight.NpcGroup,
            WeaponUsed = fight.WeaponUsed,
            Outcome = fight.Outcome.ToString(),
            YouHits = fight.YouHits,
            YouMisses = fight.YouMisses,
            TheyHits = fight.TheyHits,
            TheyMisses = fight.TheyMisses,
            ApproxDamageDone = fight.ApproxDamageDone,
            ApproxDamageTaken = fight.ApproxDamageTaken,
            // A fight that reached a real resolution yet produced not one parsed swing is the
            // signature of a character without fightbrief: narrative mode replaces every hit/miss
            // line with flavour text we do not parse. Flag it so aggregates can exclude it rather
            // than averaging in a spurious zero. An unresolved fight is NOT flagged — it may simply
            // have been cut short before anyone swung.
            NarrativeMode = swings == 0 && fight.IsResolved,
            Room = _encounterRoom,
            Weather = _encounterStats.Weather.ToString(),
            Strength = _encounterStats.Strength,
            RawStrength = _encounterStats.RawStrength,
            Dexterity = _encounterStats.Dexterity,
            RawDexterity = _encounterStats.RawDexterity,
            StaminaAtStart = _encounterStats.Stamina,
            MaxStamina = _encounterStats.MaxStamina,
            WeightCarriedGrams = _encounterStats.WeightCarriedGrams,
            ObjectsCarried = _encounterStats.ObjectsCarried,
            Level = _encounterStats.Level,
            IsBlind = _encounterStats.IsBlind,
            IsDeaf = _encounterStats.IsDeaf,
            IsCrippled = _encounterStats.IsCrippled,
            IsDumb = _encounterStats.IsDumb,
            Effects = DescribeEffects(_encounterEffects),
        };
    }

    private static string[] DescribeEffects(StatusEffectState effects)
    {
        if (!effects.AnyActive)
            return [];

        var active = new List<string>(7);
        if (effects.StrengthBuff) active.Add(nameof(effects.StrengthBuff));
        if (effects.StrengthDebuff) active.Add(nameof(effects.StrengthDebuff));
        if (effects.DexterityBuff) active.Add(nameof(effects.DexterityBuff));
        if (effects.DexterityDebuff) active.Add(nameof(effects.DexterityDebuff));
        if (effects.StaminaBuff) active.Add(nameof(effects.StaminaBuff));
        if (effects.StaminaDebuff) active.Add(nameof(effects.StaminaDebuff));
        if (effects.Glow) active.Add(nameof(effects.Glow));
        return active.ToArray();
    }

    private FightAccumulator? FightForLocked(CombatEvent combatEvent)
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

    // Same one-shot relay the view-model aggregator uses: an NPC hit line's embedded "(cur/max)" is
    // parsed twice for the same line (generic stats scan first, combat event second), so the
    // pre-line value has to be stashed before it is overwritten or every delta computes to zero.
    // See CombatStatsAggregator.ObserveDamageTaken for the full explanation.
    private void ObserveStaminaLocked(int? currentStamina)
    {
        if (currentStamina is null)
            return;

        _pendingPreUpdateStamina = _lastKnownStamina;
        _lastKnownStamina = currentStamina.Value;
    }

    private double? ResolveDamageTakenLocked(int? currentStamina)
    {
        if (currentStamina is null)
            return null;

        var baseline = _pendingPreUpdateStamina is not null && _lastKnownStamina == currentStamina
            ? _pendingPreUpdateStamina
            : _lastKnownStamina;

        double? attributed = null;
        if (baseline is not null)
        {
            var delta = baseline.Value - currentStamina.Value;
            if (delta >= 0)
                attributed = delta;
        }

        _lastKnownStamina = currentStamina.Value;
        _pendingPreUpdateStamina = null;
        return attributed;
    }
}
