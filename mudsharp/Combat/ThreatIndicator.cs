namespace MudSharp.Combat;

/// <summary>
/// Visual urgency of the Combat Rail's headline threat indicator - the "DEATH IN &lt;n&gt;S" style
/// label the owner asked for after a 14-rat fight where the panel never said how close they were to
/// dying (stamina at 20, no signal anywhere on screen). Deliberately a SEPARATE enum from
/// <see cref="CombatTier"/>, not a rename of it: <see cref="CombatTier"/> answers "how urgent is this
/// STATE signal" for the tier table in general (strength, dexterity, unarmed, flee-cost-block all use
/// it too); <see cref="ThreatLevel"/> answers the narrower question "what should the one headline
/// label at the top of the panel say and how loud should it be", which folds the stamina tier
/// together with the win/lose outlook into a single ranked read. <see cref="Critical"/> maps 1:1 onto
/// <see cref="CombatTier.T3"/> so the panel's one shared glow layer (DESIGN_FINAL.md 4.2: "at most
/// one T3 element at a time") pulses exactly when this label reads Critical, never independently of
/// it.
/// </summary>
public enum ThreatLevel
{
    /// <summary>No encounter at all. The headline label renders nothing.</summary>
    Idle,
    /// <summary>In combat, nothing elevated: stamina is healthy and the fight is not read as losing.
    /// Calm colour, no motion.</summary>
    Safe,
    /// <summary>Worth noticing on your own time - stamina has started to drop, or the fight reads as
    /// losing despite stamina still being fine. Normal-bright colour, no motion.</summary>
    Caution,
    /// <summary>Worth noticing soon. Bright colour, no motion.</summary>
    Danger,
    /// <summary>Act now. Bright colour, glow pulse - the only level that requests the shared
    /// Composition glow layer (<see cref="CombatTier.T3"/>).</summary>
    Critical,
}

/// <summary>The resolved headline reading: what tier to render at, and the exact label text.</summary>
public readonly record struct ThreatReading(ThreatLevel Level, string Label)
{
    public static readonly ThreatReading Idle = new(ThreatLevel.Idle, string.Empty);
}

/// <summary>
/// Resolves the Combat Rail's headline threat indicator - the element replacing the permanent
/// flee-cost ladder as the panel's organising element (owner: "a threat indicator gauge of some kind
/// -- or a 'DEATH IN &lt;n&gt;S' label or something simple. Bold text. Gently glowing at first getting
/// angrier as it gets likelier.").
///
/// <para><b>Deliberately reuses <see cref="CombatTierResolver.StaminaTier"/> rather than inventing a
/// second set of numeric thresholds.</b> That table (DESIGN_FINAL.md 4.3) already answers "how close
/// is this fight to killing the player" from the same hits-left/seconds-to-die/stamina-fraction
/// inputs, is already unit-tested (CombatTierResolverTests), and already drives the shared pulse
/// layer via <see cref="CombatTierResolver.ResolvePulseTier"/> - a second independent threshold set
/// here could quietly drift out of sync with the glow it is supposed to describe. This class only
/// adds the ONE genuinely new decision the tier table does not make: what to do when stamina itself
/// is fine (tier None) but the fight still reads as losing on the outlook projection alone (see
/// <see cref="Resolve"/>'s remarks).</para>
/// </summary>
public static class ThreatIndicator
{
    /// <summary>
    /// Resolves the headline label and its urgency level.
    /// </summary>
    /// <param name="inCombat">Whether an encounter is currently live. False (idle, or the post-combat
    /// grace window) always resolves to <see cref="ThreatLevel.Idle"/> - projecting a finished fight's
    /// death clock would be a lie, and the wireframe's own review states never carry this label.</param>
    /// <param name="staminaTier">The result of <see cref="CombatTierResolver.StaminaTier"/> for the
    /// current reading - the single source of truth for "how close is this", shared with the pulse
    /// layer so the two never disagree.</param>
    /// <param name="verdict">The current <see cref="OutlookVerdict"/>.</param>
    /// <param name="secondsToDie">Projected seconds until the player dies, or null if unprojectable.</param>
    /// <param name="hitsLeft">Projected incoming hits the player can still absorb, or null if
    /// unknown.</param>
    /// <param name="staminaCurrent">Current stamina, for context only (the label prefers the sharper
    /// of secondsToDie/hitsLeft when both are available at the Critical level).</param>
    /// <param name="staminaMax">Max stamina.</param>
    public static ThreatReading Resolve(
        bool inCombat,
        CombatTier staminaTier,
        OutlookVerdict verdict,
        double? secondsToDie,
        int? hitsLeft,
        int? staminaCurrent,
        int? staminaMax)
    {
        if (!inCombat)
            return ThreatReading.Idle;

        var level = staminaTier switch
        {
            CombatTier.T3 => ThreatLevel.Critical,
            CombatTier.T2 => ThreatLevel.Danger,
            CombatTier.T1 => ThreatLevel.Caution,
            // Stamina alone says nothing is wrong, but the fight can still read as losing on the
            // outlook projection (e.g. early in a fight against a hard-hitting opponent, before
            // stamina has actually dropped far enough to trip the tier table). That is still worth a
            // calm nudge - Caution, never higher, since the tier table (the thing that can actually
            // end in death) has not said so.
            _ => verdict == OutlookVerdict.Losing ? ThreatLevel.Caution : ThreatLevel.Safe,
        };

        var label = level switch
        {
            ThreatLevel.Critical => CriticalLabel(secondsToDie, hitsLeft),
            ThreatLevel.Danger => hitsLeft is int h ? $"~{h} HITS FROM DEATH" : "STAMINA LOW",
            ThreatLevel.Caution => staminaTier == CombatTier.T1 ? "STAMINA DROPPING" : "LOSING",
            _ => verdict == OutlookVerdict.Winning ? "WINNING" : "STEADY",
        };

        return new ThreatReading(level, label);
    }

    /// <summary>Prefers the sharper, more concrete figure: a projected time is a countdown the eye
    /// can watch tick down, so it leads over a hit count when both are available. Falls back to hits,
    /// then to a plain "act now" label if - per the tier table's own trigger (4.3: hits-left &lt;= 2 OR
    /// a sub-15s die projection) - neither actually ended up populated for some future caller.</summary>
    private static string CriticalLabel(double? secondsToDie, int? hitsLeft)
    {
        if (secondsToDie is double seconds && seconds >= 0)
            return $"DEATH IN {Math.Max(1, (int)Math.Round(seconds, MidpointRounding.AwayFromZero))}S";
        if (hitsLeft is int hits)
            return hits <= 1 ? "DEATH IN ~1 HIT" : $"DEATH IN ~{hits} HITS";
        return "DEATH IMMINENT";
    }
}
