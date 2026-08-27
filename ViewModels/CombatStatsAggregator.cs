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
    // Weapon: the PLAYER'S weapon for THIS fight (see FightAccumulator.WeaponUsed). NpcWeapon is
    // the separate, independently-armed NPC side - conflating the two would have silently
    // overwritten one side's weapon with the other's the first time they differed.
    string? Weapon,
    string? NpcWeapon,
    int YouHits,
    int YouMisses,
    int TheyHits,
    int TheyMisses,
    double ApproxDamageDone,
    double ApproxDamageTaken,
    TimeSpan Duration,
    FightOutcome Outcome,
    bool IsResolved,
    // Recent-hits strip data (clog window, primary fight only - see CombatHistoryFormatter). Every
    // fight carries its own bounded ring regardless, since the cost is a handful of structs and
    // BuildFightSnapshots already allocates one FightSnapshot per active NPC on every refresh.
    IReadOnlyList<SwingOutcome> RecentYourSwings,
    IReadOnlyList<SwingOutcome> RecentTheirSwings,
    // How hurt this creature last looked, on NpcHealthRungs' 1-7 scale, with the game's own wording
    // and when it was read. MUD2 reports this only on a landed blow, so the timestamp is not
    // bookkeeping - it is what separates a current reading from a stale one, and the panel must never
    // draw the two alike. Null throughout until the first descriptor lands.
    int? HealthRung = null,
    string? HealthPhrase = null,
    DateTime? HealthReadUtc = null,
    // How hard this creature has hit the player SO FAR THIS FIGHT - sample count, worst blow, running
    // total, in the same shape the historical index reports so the rail's two damage rows cannot
    // disagree about what a mean is. ApproxDamageTaken above is the same total; this carries the
    // measured-hit count and the worst single blow that a total alone cannot reconstruct.
    DamageProfile TheirDamage = default);

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
    // silently absorbed into (or wrongly blamed on) the next hit's delta. See
    // MudSharp.Combat.StaminaDeltaRelay's own remarks for why a one-shot relay is needed at all
    // (an NPC hit line is parsed twice for the same line, once generically for stats and once by
    // the combat tracker's own hit regex).
    private readonly StaminaDeltaRelay _staminaRelay = new();

    public bool InCombat { get; private set; }
    public bool HasEncounter => _encounterStartUtc is not null;

    public void BeginEncounter(DateTime startedUtc)
    {
        InCombat = true;
        _encounterStartUtc = startedUtc;
        // Adopt a weapon equipped just before the client noticed the fight - see PendingWeaponWindow.
        // Still null in the ordinary case, which is correct: MUD2 starts every engagement empty-handed
        // until a line says otherwise.
        _currentWeapon = _pendingWeapon is not null
            && startedUtc - _pendingWeaponUtc <= PendingWeaponWindow
            && startedUtc >= _pendingWeaponUtc
                ? _pendingWeapon
                : null;
        _pendingWeapon = null;
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

    public void ObserveStamina(int? currentStamina) => _staminaRelay.Observe(currentStamina);

    /// <summary>
    /// A weapon equip seen while the client believed no fight was open, held briefly in case one
    /// opens moments later.
    ///
    /// <para>Needed because of an ordering MUD2 forces on us. "You are now using the axe0 to fight!"
    /// names no NPC, so it cannot open an encounter - and when you type <c>k zombie wi axe</c> against
    /// something that is ALREADY engaging you, that line is the ONLY output: there is no "You attack
    /// the zombie" to carry the weapon. If the encounter then opens on a swing line, which carries no
    /// weapon either, the fight has no weapon for its entire duration and the panel reads UNARMED.
    /// Confirmed in the clog corpus: 4 equips stranded outside an encounter, and 14 encounters opened
    /// by a swing line with no weapon anywhere.</para>
    ///
    /// <para>The window itself (why SHORT, and why shared with the other two combat recorders) is
    /// documented on <see cref="Mucka.Core.CombatTiming.PendingWeaponWindow"/>.</para>
    /// </summary>
    private static readonly TimeSpan PendingWeaponWindow = Mucka.Core.CombatTiming.PendingWeaponWindow;
    private string? _pendingWeapon;
    private DateTime _pendingWeaponUtc;

    public void Observe(CombatEvent combatEvent)
    {
        if (!InCombat)
        {
            if (combatEvent.Kind == CombatEventKind.WeaponEquip
                && !string.IsNullOrWhiteSpace(combatEvent.Weapon))
            {
                _pendingWeapon = combatEvent.Weapon;
                _pendingWeaponUtc = combatEvent.TimestampUtc;
                return;
            }
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
                FightFor(combatEvent)?.NoteNpcWeapon(combatEvent.Weapon);
                break;

            // WeaponUnusable shares this: "You cannot use the X to fight now!" means the weapon is
            // not in play whatever the cause (it just broke, or MUD2 refused the wield). Either way
            // the player is fighting bare-handed from here, and the readout must say so - the owner
            // lost a weapon mid-fight and the panel went on showing it as equipped.
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
                // Fleeing drops your weapon automatically, with no WeaponBroke line to explain it, so
                // without this the panel keeps showing a weapon that is lying on the floor. Only the
                // weapon in use matters here; everything else the player drops is inventory.
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

            case CombatEventKind.NpcFleeFailed:
                // The creature is still standing in the room, but the FIGHT is over: MUD2 broke the
                // sequence and the player has to attack again. So this resolves the fight (CFledFail)
                // and drops the creature from the live roster - it is no longer an opponent until
                // re-engaged, and leaving it listed is what kept the panel claiming "in combat" after
                // a fight the player simply walked away from.
                ResolveFight(combatEvent, FightOutcome.CFledFail);
                RemoveParticipant(combatEvent.NpcName);
                break;

            case CombatEventKind.NpcHealth:
                // No AddParticipant: CombatTracker only emits this for an NPC already engaged (the
                // same line appears in room descriptions), so anything reaching here is a participant
                // already. A fight that has resolved is left alone - a corpse has no health.
                if (combatEvent.HealthRung is int rung && FightFor(combatEvent) is { IsResolved: false } hurt)
                    hurt.NoteHealth(rung, combatEvent.HealthPhrase, combatEvent.TimestampUtc);
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
            // NpcDied rides here too: the creature is dead and off the roster exactly as a kill
            // leaves it. It resolves as NoMore rather than Kill because nothing in that line says the
            // player did it - see FightOutcome.NoMore. CombatTracker only emits it for a creature
            // already engaged, so it cannot open a bucket against something the player never fought.
            case CombatEventKind.NpcDied:
                ResolveFight(combatEvent, OutcomeFor(combatEvent.Kind));
                RemoveParticipant(combatEvent.NpcName);
                break;

            case CombatEventKind.KilledByNpc:
                // The player died, which ends the WHOLE encounter - CombatTracker emits this once
                // naming only the killer and then calls EndAll(), so no other fight gets its own
                // close event. Resolve them all: "this fight ended with me dead" is true of every
                // one of them, and leaving the others Unresolved would understate how badly a
                // pile-on went.
                foreach (var fight in _fightOrder)
                    fight.Resolve(FightOutcome.Died, combatEvent.TimestampUtc);
                RemoveParticipant(combatEvent.NpcName);
                break;

            case CombatEventKind.YouFled:
                // Fleeing ends EVERY active fight, and none of them name themselves on this line.
                foreach (var fight in _fightOrder)
                    fight.Resolve(FightOutcome.UFled, combatEvent.TimestampUtc);
                _activeNpcSet.Clear();
                _activeNpcOrder.Clear();
                _npcWeapons.Clear();
                break;

            case CombatEventKind.YouFleeFailed:
                // A FAILED flee ends every fight just the same - the player never left the room, but
                // MUD2 returned their fight count to 0 regardless, so every opponent has to be
                // re-engaged from scratch. Treated identically to YouFled here apart from the outcome
                // label, which must stay distinct: the fights ended, the player did not get away, and
                // conflating the two would make the escape statistics claim escapes that never
                // happened. (The attempt was not free either - see FightOutcome.UFledFail.)
                foreach (var fight in _fightOrder)
                    fight.Resolve(FightOutcome.UFledFail, combatEvent.TimestampUtc);
                _activeNpcSet.Clear();
                _activeNpcOrder.Clear();
                _npcWeapons.Clear();
                break;

            // FightEndOther ("You can fight it no longer.") does not RESOLVE a fight here, mirroring
            // CombatTracker: it is a trailing acknowledgment of an end already stated earlier in the
            // same frame - a kill, a poison death, a real flee, or (the case that used to be
            // mishandled) a FAILED flee, all of which have resolved their own fight before this line
            // is reached.
            //
            // The named variant ("You can fight the wyvern no longer.") drops that creature from the
            // live roster, because CombatTracker has just closed its fight and the panel must not go
            // on listing it as an opponent. Its fight keeps whatever outcome it already had, and if it
            // had none it stays Unresolved - which is the honest reading, since this line states that
            // a fight ended and declines to say why. The pronoun forms name nobody and stay a no-op:
            // acting on them would clear OTHER still-active participants in a pack.
            case CombatEventKind.FightEndOther:
                if (combatEvent.NpcName is not null)
                {
                    // Resolve as EndOther, not left Unresolved: the game positively closed this fight,
                    // and Unresolved has to keep meaning "we lost track of it" or the rows nobody can
                    // explain stop being findable. First resolution still wins (FightAccumulator.Resolve
                    // returns early once IsResolved), so the usual case - this line trailing a kill or a
                    // flee that already resolved the fight - is untouched; pinned for THIS event kind by
                    // Fights_NamedFightEndOtherCannotOverwriteAnEarlierKill.
                    //
                    // FightFor get-or-CREATES, so this relies on CombatTracker naming only creatures
                    // it believes are engaged: a trailing end for an already-closed fight arrives
                    // unnamed and lands in the no-op above, rather than minting a zero-swing bucket.
                    ResolveFight(combatEvent, FightOutcome.EndOther);
                    RemoveParticipant(combatEvent.NpcName);
                }
                break;

            // Informational only, and in the same frame as the death that follows it, so there is
            // nothing to record and nothing to warn about - see CombatEventKind.LifeConcluding.
            case CombatEventKind.LifeConcluding:
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
                fight.NpcWeapon,
                fight.YouHits,
                fight.YouMisses,
                fight.TheyHits,
                fight.TheyMisses,
                fight.ApproxDamageDone,
                fight.ApproxDamageTaken,
                fight.DurationAt(nowUtc),
                fight.Outcome,
                fight.IsResolved,
                fight.RecentYourSwings,
                fight.RecentTheirSwings,
                fight.HealthRung,
                fight.HealthPhrase,
                fight.HealthReadUtc,
                DamageProfile.ForFight(
                    fight.TheyHitsMeasured, fight.MaxDamageTaken, fight.ApproxDamageTaken)));
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
        CombatEventKind.Kill => FightOutcome.Kill,
        CombatEventKind.KilledByNpc => FightOutcome.Died,
        CombatEventKind.Withdrawn => FightOutcome.Withdraw,
        CombatEventKind.NpcFled => FightOutcome.CFled,
        CombatEventKind.NpcFleeFailed => FightOutcome.CFledFail,
        CombatEventKind.NpcDied => FightOutcome.NoMore,
        CombatEventKind.YouFled => FightOutcome.UFled,
        CombatEventKind.YouFleeFailed => FightOutcome.UFledFail,
        _ => FightOutcome.Unresolved,
    };

    /// <summary>Returns the stamina delta attributed to this blow, so the caller can credit it to
    /// the right per-NPC fight, or null when no baseline was available.</summary>
    private double? ObserveDamageTaken(int? currentStamina)
    {
        // The equality/relay guard against a stale one-shot value lives in StaminaDeltaRelay now -
        // see its own remarks for why a hit that never touched the running baseline (e.g. a hit to
        // exactly 0 stamina, which the compact-stamina scan does not fire for) must fall back to it
        // directly rather than trust a stale relay.
        var (delta, _) = _staminaRelay.ResolveDelta(currentStamina);
        if (delta is int d)
            _approxDamageTaken += d;
        return delta;
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
