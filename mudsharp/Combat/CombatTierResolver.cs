namespace MudSharp.Combat;

/// <summary>Visual urgency tier for a STATE signal (DESIGN_FINAL.md section 4.2). Only <see cref="T3"/>
/// carries motion (a Composition glow pulse); <see cref="T1"/>/<see cref="T2"/> are static colour
/// moves. EVENT tiers (E1/E2 - one-off flashes on a state CHANGE, e.g. an NPC picking up a weapon)
/// are deliberately not modelled here: they are timestamp-driven decay windows the caller compares
/// against <c>DateTime.UtcNow</c> at paint time, not a persistent state this resolver tracks.</summary>
public enum CombatTier
{
    None,
    /// <summary>Worth noticing on your own time. Normal hue, no motion.</summary>
    T1,
    /// <summary>Worth noticing soon. Bright hue, no motion.</summary>
    T2,
    /// <summary>Act now. Bright hue, glow pulse (1.2s period).</summary>
    T3,
}

/// <summary>
/// Resolves per-signal tiers per DESIGN_FINAL.md section 4.3, plus the two rules in 4.2/4.4 that are
/// stateful or cross-cutting enough to need a home: the bracket-crossing hysteresis latches (4.2's
/// last bullet) and the flee-cost hard floor (4.4). Everything else here is a pure, stateless
/// function of primitives - testable directly, and MAUI-independent so mudsharp.Tests can exercise
/// it via the existing ProjectReference with no test-project wiring.
///
/// <para>One instance lives for the duration of an encounter; call <see cref="Reset"/> on
/// <c>BeginEncounter</c> so a fresh fight starts both latches armed (4.2: latches "re-arm only once
/// stamina rises back strictly ABOVE that same boundary" - a brand new encounter has no prior
/// crossing to remember).</para>
/// </summary>
public sealed class CombatTierResolver
{
    private bool _below20Armed = true;
    private bool _below6_5Armed = true;

    /// <summary>UTC time of the most recent downward crossing of the 20-stamina bracket, or null if
    /// none has happened (or it has since re-armed by rising back above 20). Drives the E1 "flee-cost
    /// crossing a bracket" flash (4.3's last row) - the caller compares this against
    /// <c>DateTime.UtcNow</c> and decays the flash after ~1.5s (4.2's E1 duration).</summary>
    public DateTime? Below20CrossedUtc { get; private set; }

    /// <summary>Same as <see cref="Below20CrossedUtc"/> for the 6.5 (free-flee) bracket.</summary>
    public DateTime? Below6_5CrossedUtc { get; private set; }

    public void Reset()
    {
        _below20Armed = true;
        _below6_5Armed = true;
        Below20CrossedUtc = null;
        Below6_5CrossedUtc = null;
    }

    /// <summary>
    /// Updates the two hysteresis latches for the current stamina reading. Each boundary fires its
    /// crossing timestamp AT MOST ONCE per downward crossing, and only re-arms once stamina is
    /// observed strictly above that boundary again (4.2) - without this, stamina oscillating across
    /// 6.5 from small hits and small regen ticks would re-flash on every tick that straddles the
    /// line, which is the opposite of "worth a look, once".
    /// </summary>
    public void ObserveStaminaForCrossings(double staminaCurrent, DateTime nowUtc)
    {
        // Exactly AT a boundary is deliberately neither "below" (no new crossing to fire) nor
        // "strictly above" (no re-arm either) - 4.2 says re-arming needs stamina strictly above the
        // boundary, so sitting exactly on it leaves whichever state the latch was already in alone.
        if (staminaCurrent < FleeCostLadder.CeilingThreshold)
        {
            if (_below20Armed)
            {
                Below20CrossedUtc = nowUtc;
                _below20Armed = false;
            }
        }
        else if (staminaCurrent > FleeCostLadder.CeilingThreshold)
        {
            _below20Armed = true;
        }

        if (staminaCurrent < FleeCostLadder.FreeThreshold)
        {
            if (_below6_5Armed)
            {
                Below6_5CrossedUtc = nowUtc;
                _below6_5Armed = false;
            }
        }
        else if (staminaCurrent > FleeCostLadder.FreeThreshold)
        {
            _below6_5Armed = true;
        }
    }

    /// <summary>Stamina / hits-left tier (4.3's first three rows). All inputs optional/nullable -
    /// an unknown value simply cannot promote the tier past what the known ones justify.</summary>
    public static CombatTier StaminaTier(
        double? staminaCurrent, double? staminaMax, int? hitsLeft, double? secondsToDie, double? secondsToKill)
    {
        if ((hitsLeft is int h && h <= 2)
            || (secondsToDie is double die && die < 15 && secondsToKill is double kill && die < kill))
            return CombatTier.T3;

        if ((hitsLeft is int h2 && h2 <= 4)
            || (staminaCurrent is double cur1 && staminaMax is double max1 && max1 > 0 && cur1 < max1 * 0.25))
            return CombatTier.T2;

        if (staminaCurrent is double cur2 && staminaMax is double max2 && max2 > 0 && cur2 < max2 * 0.5)
            return CombatTier.T1;

        return CombatTier.None;
    }

    /// <summary>Strength delta-chip tier (4.3): T2 below 50% of max effective strength, intensifying
    /// from T1 at 75% - the brief's own stated threshold.</summary>
    public static CombatTier StrengthTier(int? effectiveStrength, int? maxStrength)
    {
        if (effectiveStrength is not int eff || maxStrength is not int max || max <= 0)
            return CombatTier.None;

        var fraction = eff / (double)max;
        if (fraction < 0.50) return CombatTier.T2;
        if (fraction < 0.75) return CombatTier.T1;
        return CombatTier.None;
    }

    /// <summary>Dexterity delta-chip tier (4.3): T1 for any nonzero penalty while in combat - this
    /// signal never escalates past T1 per the design's own table.</summary>
    public static CombatTier DexterityTier(int? dexterityDelta, bool inCombat)
        => inCombat && dexterityDelta is int delta && delta != 0 ? CombatTier.T1 : CombatTier.None;

    /// <summary>Unarmed alert tier (4.3): always T2 while a fight is live and the current weapon is
    /// null. Never escalates to T3 - fighting bare-handed is a contributing cause, not the clock
    /// itself (see the stamina tie-break rationale below).</summary>
    public static CombatTier UnarmedTier(bool isUnarmed, bool fightLive)
        => isUnarmed && fightLive ? CombatTier.T2 : CombatTier.None;

    /// <summary>
    /// The single shared pulse layer can only animate one thing at a time (4.2: "at most one T3
    /// element at a time, enforced in code"). When two candidates are both T3, the tie-break is
    /// stamina - it is the only T3-eligible signal that can directly end the encounter in death;
    /// everything else is a contributing cause, not the clock itself.
    /// </summary>
    public static CombatTier ResolvePulseTier(CombatTier staminaTier, CombatTier otherTier)
    {
        if (staminaTier == CombatTier.T3) return CombatTier.T3;
        if (otherTier == CombatTier.T3) return CombatTier.T3;
        return staminaTier > otherTier ? staminaTier : otherTier;
    }

    /// <summary>
    /// The FLEE COST block's hard floor (4.4): at or below 6.5 stamina it renders at no less than T2
    /// Pulse-Danger, full stop, regardless of what the stamina tier table would otherwise say from
    /// hits-left/time-to-die alone. The table can still promote it to T3; nothing is permitted to
    /// render it below T2 while stamina sits at/under the free-flee threshold.
    /// </summary>
    public static CombatTier FleeCostBlockTier(CombatTier staminaTier, double staminaCurrent)
    {
        if (staminaCurrent <= FleeCostLadder.FreeThreshold && staminaTier < CombatTier.T2)
            return CombatTier.T2;
        return staminaTier;
    }
}
