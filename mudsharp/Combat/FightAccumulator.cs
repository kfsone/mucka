namespace MudSharp.Combat;

/// <summary>How a single per-NPC fight ended. Mirrors combat_fights.outcome in
/// tools/combat/schema.sql so live and offline rows are directly comparable.</summary>
public enum FightOutcome
{
    /// <summary>Still going (or the encounter ended without ever resolving this one).</summary>
    Unresolved,
    Killed,
    KilledByNpc,
    NpcFled,
    YouFled,
    Withdrawn,
}

/// <summary>
/// Accumulates one NPC's fight within an encounter: the counters, the weapon actually used, and
/// how it ended.
///
/// <para>Exists because <see cref="CombatEvent"/> names its NPC on every kind that has one, but
/// nothing was bucketing by it — encounter-wide totals cannot answer "how did this rat fight
/// compare to previous rat fights" when a goat was also in the room. The offline pipeline already
/// models this split (combat_sessions holding N combat_fights); this is the live half.</para>
///
/// <para>Pure and thread-agnostic: one instance is driven from the UI thread for display, another
/// from the session Feed thread for history persistence. They never share state — see
/// CombatStatsAggregator and FightHistoryRecorder respectively.</para>
/// </summary>
public sealed class FightAccumulator
{
    public FightAccumulator(string npcName, DateTime startedUtc, string? weaponAtStart)
    {
        NpcName = npcName;
        NpcGroup = NpcGroups.Normalize(npcName);
        StartedUtc = startedUtc;
        WeaponUsed = weaponAtStart;
    }

    public string NpcName { get; }
    public string NpcGroup { get; }
    public DateTime StartedUtc { get; }
    public DateTime? EndedUtc { get; private set; }
    public FightOutcome Outcome { get; private set; } = FightOutcome.Unresolved;

    /// <summary>The weapon in use for THIS fight. Seeded from the encounter's current weapon at
    /// fight start rather than left null, because MUD2 does not re-arm you for a second
    /// attacker: a weapon equipped for fight A silently extends to fight B when B joins
    /// mid-encounter, and there is no equip line for B. reduce_combat.py does the same.</summary>
    public string? WeaponUsed { get; private set; }

    public int YouHits { get; private set; }
    public int YouMisses { get; private set; }
    public int TheyHits { get; private set; }
    public int TheyMisses { get; private set; }
    public double ApproxDamageDone { get; private set; }
    public double ApproxDamageTaken { get; private set; }

    public bool IsResolved => Outcome != FightOutcome.Unresolved;

    public TimeSpan DurationAt(DateTime nowUtc)
    {
        var end = EndedUtc ?? nowUtc;
        var duration = end - StartedUtc;
        return duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
    }

    public void NoteWeapon(string? weapon)
    {
        if (!string.IsNullOrWhiteSpace(weapon))
            WeaponUsed = weapon;
    }

    public void NoteWeaponBroke() => WeaponUsed = null;

    public void AddYouHit(int? rangeLow, int? rangeHigh)
    {
        YouHits++;
        if (rangeLow is int low && rangeHigh is int high)
            ApproxDamageDone += (low + high) / 2.0;
    }

    public void AddYouMiss() => YouMisses++;

    /// <summary>Records an incoming hit. <paramref name="damage"/> is the already-resolved stamina
    /// delta for this blow (the caller owns baseline tracking — see
    /// CombatStatsAggregator.ObserveDamageTaken for why the baseline cannot simply be read off the
    /// hit line itself), or null when it could not be determined.</summary>
    public void AddTheyHit(double? damage)
    {
        TheyHits++;
        if (damage is double value && value > 0)
            ApproxDamageTaken += value;
    }

    public void AddTheyMiss() => TheyMisses++;

    /// <summary>First resolution wins: a Kill followed by a trailing FightEndOther, or a player
    /// death that also force-closes the encounter, must not overwrite the real outcome.</summary>
    public void Resolve(FightOutcome outcome, DateTime endedUtc)
    {
        if (IsResolved || outcome == FightOutcome.Unresolved)
            return;
        Outcome = outcome;
        EndedUtc = endedUtc;
    }
}
