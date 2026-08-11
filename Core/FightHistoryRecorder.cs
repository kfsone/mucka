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
///
/// <para><see cref="Dispose"/> is a belt-and-braces flush for whatever is still open at shutdown -
/// the primary path is MudSession.Dispose calling CombatTracker.ForceEnd, whose InCombatChanged
/// cascade already reaches <see cref="OnInCombatChanged"/> and flushes normally. This exists in
/// case that wiring is ever bypassed or reordered; FlushLocked is idempotent (a no-op with nothing
/// open), so calling it twice is harmless.</para>
/// </summary>
public sealed class FightHistoryRecorder : IDisposable
{
    private readonly FightHistoryStore _store;
    private readonly object _lock = new();
    private readonly Dictionary<string, FightAccumulator> _fights = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FightAccumulator> _fightOrder = [];

    private string? _currentWeapon;
    private int? _lastKnownStamina;
    private int? _pendingPreUpdateStamina;
    // Last score observed via the FES heartbeat. Unlike _lastKnownStamina this needs no
    // pending-relay dance: score has no "double-parsed on the same line" hazard the way an inline
    // "(cur/max)" stamina delta does, so a plain carried-forward value is honest as-is.
    private int? _lastKnownScore;

    // The character occupying this session, from MudSession.CharacterIdentified (the post-login
    // "score" reply). Threaded through so every alt's fights stop pooling into one undifferentiated
    // history (see FightRecord.CharacterName's remarks). Session-scoped, not encounter-scoped: it
    // does not reset on BeginEncounter/FlushLocked, only on a fresh CharacterIdentified.
    private string? _characterName;

    // Unix-ms of the instant THIS encounter began (CombatTracker.InCombatChanged -> true), shared by
    // every fight opened within it so per-fight rows can be regrouped back into their encounter. Set
    // in OnInCombatChanged(true); cleared to null once flushed so a stray late-arriving fight after
    // FlushLocked (should not happen, but see BuildRecord's own defensiveness elsewhere) cannot be
    // mis-stamped with the PREVIOUS encounter's id.
    private long? _encounterStartedAtMs;

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
            if (stats.Score is int score)
                _lastKnownScore = score;

            // Broadcast to every fight still open, not just the primary target: stamina and score
            // are player-scoped, not per-NPC, so a pack fight's concurrent rows all share the same
            // readings (mirrors the existing WeaponEquip broadcast below). A resolved fight is
            // skipped on purpose - that is what freezes its StaminaAtEnd/ScoreAtEnd/MinStamina at
            // "last known while still open" instead of drifting into post-fight regen or a later
            // fight's own score changes.
            foreach (var fight in _fightOrder)
            {
                if (fight.IsResolved)
                    continue;
                fight.NoteStamina(stats.Stamina);
                fight.NoteScore(stats.Score);
            }
        }
    }

    public void OnStatusEffectsChanged(StatusEffectState effects) => _lastEffects = effects;
    public void OnRoomShortReady(string room) => _lastRoom = room;

    /// <summary>The character occupying this session was identified (MudSession.CharacterIdentified,
    /// fired once per game-mode entry from the post-login "score" reply). Stamped onto every fight
    /// row from here on - see FightRecord.CharacterName's remarks for why this matters.</summary>
    public void OnCharacterIdentified(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;
        lock (_lock)
            _characterName = name;
    }

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
                // The encounter's own natural key (see FightRecord.EncounterStartedAtMs's remarks).
                // DateTime.UtcNow here, not the triggering combat event's own timestamp: this fires
                // synchronously from the same Feed-thread call that flips CombatTracker.InCombat,
                // ahead of the FightStart event for the SAME line (Begin() raises InCombatChanged
                // before Observe() calls Emit()), so the two are effectively the same instant anyway.
                _encounterStartedAtMs = new DateTimeOffset(DateTime.UtcNow, TimeSpan.Zero).ToUnixTimeMilliseconds();
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

                // WeaponUnusable shares this - see the matching case in CombatStatsAggregator for
                // why. The two aggregators consume the same event stream and must not disagree.
                case CombatEventKind.WeaponBroke:
                case CombatEventKind.WeaponUnusable:
                    _currentWeapon = null;
                    foreach (var fight in _fightOrder)
                    {
                        if (!fight.IsResolved)
                            fight.NoteDisarmed();
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

    /// <summary>Flushes any encounter still open (see the class remarks) - safe to call even when
    /// nothing is open, and safe to call more than once. Does NOT dispose <see cref="_store"/>:
    /// MuckaConnection owns that lifetime separately, since the store outlives any one recorder in
    /// principle and is what actually needs to drain its own background writer on shutdown.</summary>
    public void Dispose()
    {
        lock (_lock)
            FlushLocked();
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
        // Clear the encounter key too: FightForLocked must never stamp a stray late fight (should
        // not happen post-clear, but leaving a stale value around risks a silent mis-attribution to
        // the encounter that just closed if it ever did) with the PREVIOUS encounter's id.
        _encounterStartedAtMs = null;
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
            CharacterName = _characterName,
            EncounterStartedAtMs = _encounterStartedAtMs,
            StartedAtMs = new DateTimeOffset(fight.StartedUtc, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            EndedAtMs = new DateTimeOffset(endedUtc, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            DurationMs = (long)duration.TotalMilliseconds,
            MinStamina = fight.MinStamina,
            StaminaAtEnd = fight.StaminaAtEnd,
            ScoreAtStart = fight.ScoreAtStart,
            ScoreAtEnd = fight.ScoreAtEnd,
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

        // Seed the min/end trackers from whatever we already know at the instant this NPC joins -
        // see FightAccumulator's constructor remarks for why (a one-sided fight may never trigger
        // another stats reading before it resolves).
        var fight = new FightAccumulator(
            npcName, combatEvent.TimestampUtc, _currentWeapon, _lastKnownStamina, _lastKnownScore);
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
