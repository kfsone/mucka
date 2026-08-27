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
/// <para>Always records, as everything on this path now does (ClogWriter included). Nothing here is
/// written while the player is not actually in a fight.</para>
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
    // When _currentWeapon was last confirmed, so an equip seen just before the client noticed the
    // fight can be carried into it while a stale one is discarded. See the use in OnInCombatChanged.
    private DateTime _currentWeaponUtc;
    private static readonly TimeSpan PendingWeaponWindow = CombatTiming.PendingWeaponWindow;
    private readonly StaminaDeltaRelay _staminaRelay = new();
    // Last score observed via the FES heartbeat. Unlike the stamina relay this needs no
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

    /// <param name="encounterStartedAtMs">The shared encounter id, stamped once by MuckaConnection and
    /// handed to every consumer - see there. Not computed here any more: the swings table carries the
    /// same value as its join key, and two consumers each reading their own clock would produce two
    /// ids microseconds apart that no join would match.</param>
    public void OnInCombatChanged(bool inCombat, long? encounterStartedAtMs = null)
    {
        lock (_lock)
        {
            if (inCombat)
            {
                // Freeze the pre-fight context now, before any combat line can move the stats.
                _encounterStats = _lastStats;
                _encounterEffects = _lastEffects;
                _encounterRoom = _lastRoom;
                // The encounter's own natural key (see FightRecord.EncounterStartedAtMs's remarks),
                // supplied by the caller so this row and the swings table agree on it exactly. Falls
                // back to a local reading only when nobody supplied one, which is the unit-test and
                // design-time path - a row keyed to itself is still better than an unkeyed one.
                _encounterStartedAtMs = encounterStartedAtMs
                    ?? new DateTimeOffset(DateTime.UtcNow, TimeSpan.Zero).ToUnixTimeMilliseconds();
                _fights.Clear();
                _fightOrder.Clear();
                // Adopt a weapon equipped moments before the client noticed the fight. Same problem
                // Mucka.Core.CombatTiming.PendingWeaponWindow documents, reached by a different route:
                // this class does process events while out of combat, but clearing here threw the
                // weapon away again, so history recorded UNARMED for fights fought with a broadsword.
                // Display and history have to agree, and both have to be right.
                var now = DateTime.UtcNow;
                _currentWeapon = _currentWeapon is not null && now - _currentWeaponUtc <= PendingWeaponWindow
                    ? _currentWeapon
                    : null;
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
                    {
                        _currentWeapon = combatEvent.Weapon;
                        _currentWeaponUtc = combatEvent.TimestampUtc;
                    }
                    FightForLocked(combatEvent)?.NoteWeapon(_currentWeapon);
                    break;

                case CombatEventKind.WeaponEquip:
                    if (!string.IsNullOrWhiteSpace(combatEvent.Weapon))
                    {
                        _currentWeapon = combatEvent.Weapon;
                        _currentWeaponUtc = combatEvent.TimestampUtc;
                    }
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

                case CombatEventKind.ItemDropped:
                    // Fleeing auto-drops the weapon, in the same tick and just before the flee line.
                    // CombatStatsAggregator has handled this since the drop was first parsed; this
                    // class did not, so the display and the history disagreed about whether the
                    // player was still armed - and a second fight opening later in the same encounter
                    // would inherit a weapon lying on the floor. Unlike the break/refusal cases above
                    // this one names an item, so it only counts when the item IS the weapon in use.
                    if (!string.IsNullOrWhiteSpace(combatEvent.Weapon)
                        && string.Equals(combatEvent.Weapon, _currentWeapon, StringComparison.OrdinalIgnoreCase))
                    {
                        _currentWeapon = null;
                        foreach (var fight in _fightOrder)
                        {
                            if (!fight.IsResolved)
                                fight.NoteDisarmed();
                        }
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
                    FightForLocked(combatEvent)?.Resolve(FightOutcome.Kill, combatEvent.TimestampUtc);
                    break;

                case CombatEventKind.NpcDied:
                    // The creature died without our blow finishing it (poison, so far). Recorded as
                    // NoMore, never Kill: the damage that killed it never crossed the wire, so a Kill
                    // row here would feed the stamina-pool estimator a fight whose damage total is
                    // missing its largest term. See FightOutcome.NoMore.
                    FightForLocked(combatEvent)?.Resolve(FightOutcome.NoMore, combatEvent.TimestampUtc);
                    break;

                case CombatEventKind.FightEndOther:
                    // Only the forms that NAME their creature reach a fight here - FightForLocked
                    // returns null for the pronoun forms and for the synthetic force-end events, whose
                    // NpcName is null, so those still persist as Unresolved. That is the intended
                    // split: "the game closed it without a reason" (EndOther) must not be filed
                    // alongside "we never saw this fight end" (Unresolved), which is the bucket to
                    // search when hunting the next unmatched wording. See FightOutcome.EndOther.
                    FightForLocked(combatEvent)?.Resolve(FightOutcome.EndOther, combatEvent.TimestampUtc);
                    break;

                case CombatEventKind.Withdrawn:
                    FightForLocked(combatEvent)?.Resolve(FightOutcome.Withdraw, combatEvent.TimestampUtc);
                    break;

                case CombatEventKind.NpcFled:
                    FightForLocked(combatEvent)?.Resolve(FightOutcome.CFled, combatEvent.TimestampUtc);
                    break;

                case CombatEventKind.NpcFleeFailed:
                    // The creature stayed in the room but the fight ended - see FightOutcome.CFledFail.
                    // Nothing resolved this before 2026-08-19, so every failed flee was persisted as an
                    // Unresolved row: the history could not distinguish "the client lost track of this
                    // fight" from "this creature broke off", which are very different evidence.
                    FightForLocked(combatEvent)?.Resolve(FightOutcome.CFledFail, combatEvent.TimestampUtc);
                    break;

                case CombatEventKind.KilledByNpc:
                    // Player death ends the whole encounter; CombatTracker names only the killer and
                    // then closes the rest silently, so resolve every open fight here.
                    foreach (var fight in _fightOrder)
                        fight.Resolve(FightOutcome.Died, combatEvent.TimestampUtc);
                    break;

                case CombatEventKind.YouFled:
                    foreach (var fight in _fightOrder)
                        fight.Resolve(FightOutcome.UFled, combatEvent.TimestampUtc);
                    break;

                case CombatEventKind.YouFleeFailed:
                    // Same reach as YouFled - MUD2 zeroes the fight count whether the flee worked or
                    // not - but recorded under its own outcome so the corpus never counts a failed
                    // attempt as an escape.
                    foreach (var fight in _fightOrder)
                        fight.Resolve(FightOutcome.UFledFail, combatEvent.TimestampUtc);
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

        // A fight may only be BORN inside an open encounter. _encounterStartedAtMs is set when the
        // encounter opens and nulled when it flushes, so this reads "no encounter is open" - and a
        // named event arriving then is a trailing acknowledgment of something already recorded (MUD2
        // stacks several end messages, and one can land after another has closed the fight). Creating
        // a bucket for it resurrects the fight after its own flush, and the NEXT flush writes it out
        // as a second, zero-swing row against a creature already recorded properly.
        //
        // Safe against the real event ordering: CombatTracker fires InCombatChanged(true) from Begin()
        // BEFORE emitting the event that opened the fight, and Emits a closing event BEFORE End()
        // closes the encounter - so every event belonging to a fight arrives while its encounter is
        // open. Nothing legitimate is dropped here, and the weapon-tracking fields above are
        // deliberately outside this guard (a weapon equipped moments before the client noticed the
        // fight still has to be carried in - see OnInCombatChanged).
        if (_encounterStartedAtMs is null)
            return null;

        // Seed the min/end trackers from whatever we already know at the instant this NPC joins -
        // see FightAccumulator's constructor remarks for why (a one-sided fight may never trigger
        // another stats reading before it resolves).
        var fight = new FightAccumulator(
            npcName, combatEvent.TimestampUtc, _currentWeapon, _staminaRelay.LastKnown, _lastKnownScore);
        _fights[npcName] = fight;
        _fightOrder.Add(fight);
        return fight;
    }

    // An NPC hit line's embedded "(cur/max)" is parsed twice for the same line (generic stats scan
    // first, combat event second), so the pre-line value has to be stashed before it is overwritten
    // or every delta computes to zero. See MudSharp.Combat.StaminaDeltaRelay's own remarks.
    private void ObserveStaminaLocked(int? currentStamina) => _staminaRelay.Observe(currentStamina);

    private double? ResolveDamageTakenLocked(int? currentStamina)
        => _staminaRelay.ResolveDelta(currentStamina).Delta;
}
