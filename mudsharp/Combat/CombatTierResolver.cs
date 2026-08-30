namespace MudSharp.Combat;

/// <summary>Visual urgency tier for a STATE signal (DESIGN_FINAL.md section 4.2). Only <see cref="T3"/>
/// carries motion (a Composition glow pulse); <see cref="T1"/>/<see cref="T2"/> are static colour
/// moves.</summary>
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
/// Resolves per-signal tiers per DESIGN_FINAL.md section 4.3, plus the critical-stamina hard floor
/// (4.4). Everything here is a pure, stateless function of primitives - testable directly, and
/// MAUI-independent so mudsharp.Tests can exercise it via the existing ProjectReference with no
/// test-project wiring.
/// </summary>
public static class CombatTierResolver
{
    /// <summary>Stamina at/below which the player is close enough to permadeath that the panel must
    /// never read calmer than T2, whatever else it would otherwise say (D15/4.4). Not a cost figure -
    /// no flee cost, points-at-risk or flee-statistic figure is computed anywhere in this codebase
    /// (D15), and this constant is the danger reading of the band, not its price.</summary>
    public const double CriticalStaminaThreshold = 6.5;

    /// <summary>
    /// The survival threshold: <c>COMBAT-RAIL-SPEC.md</c> section 6a's third stamina number, and the
    /// only one of the three that ALARMS rather than explains.
    ///
    /// <para>Deliberately not a formula, unlike the 40 and 30 stat knees. It is where the consequences
    /// converge - most NPCs cap out at 15-20 damage so one blow can now kill, several creatures flip
    /// from peaceful to hostile against a player this wounded, a newly-arrived NPC's surprise blow lands
    /// 5-15 regardless of what the current opponent can do, and MUD2 prints its own "consider fleeing"
    /// near here. Against the owner's tally - outside rats, of 5 occasions at exactly 20 stamina, 3 cost
    /// the character - a formula that fits the instrument better is still answering the wrong
    /// question.</para>
    /// </summary>
    public const double SurvivalStaminaThreshold = 20.0;

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
    /// The critical-stamina hard floor (4.4): at or below <see cref="CriticalStaminaThreshold"/> the
    /// panel renders at no less than T2 Pulse-Danger, full stop, regardless of what the stamina tier
    /// table would otherwise say from hits-left/time-to-die alone - the player is 1-2 hits from
    /// permadeath here whatever else is true. The table can still promote it to T3; nothing is
    /// permitted to render it lower.
    /// </summary>
    public static CombatTier CriticalStaminaFloorTier(CombatTier staminaTier, double staminaCurrent)
    {
        if (staminaCurrent <= CriticalStaminaThreshold && staminaTier < CombatTier.T2)
            return CombatTier.T2;
        return staminaTier;
    }
}
