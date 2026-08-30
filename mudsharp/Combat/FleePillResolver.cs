namespace MudSharp.Combat;

/// <summary>
/// How loudly the Combat Rail's flee pill is drawn. Four states, and the top one is not simply "more
/// of the third" - see <see cref="EscapeNow"/>.
/// </summary>
public enum FleePillStatus
{
    /// <summary>Nothing drawn. The pill's slot stays reserved (spec rule 3) - this is an empty
    /// reserved frame, not a collapsed one.</summary>
    Hidden,

    /// <summary>Drawn quietly, in the same idiom as the Ctrl+W chip: an advertisement for a key that
    /// is about to be worth pressing. No motion.</summary>
    Visible,

    /// <summary>Pulsing border. Leaving is now the live question.</summary>
    Caution,

    /// <summary>
    /// <see cref="Caution"/> plus bold text and a faint pulsing background.
    ///
    /// <para><b>This state exists because the danger here is MASKED, not because it is merely worse.</b>
    /// At this stamina MUD2 charges almost nothing to leave, and a fight that has stopped costing
    /// points reads as a fight that has stopped costing anything. It has not: the player is inside the
    /// band where a single ordinary blow kills, and death in combat is character deletion. The owner's
    /// framing, kept verbatim because it is the whole justification for a fourth state - it is the tide
    /// retreating ahead of the tsunami.</para>
    /// </summary>
    EscapeNow,
}

/// <summary>
/// Resolves the flee pill's state - the Combat Rail's one surface that speaks about leaving.
///
/// <para><b>Everything happens on the tick, and that changes what "average damage" means.</b> MUD2
/// resolves every combatant's swing on one 2.000 s boundary, so a pack's damage does not arrive as a
/// sequence the player can react between - it arrives as a lump. Two quiet ticks and then
/// <c>rat1 + rat2 + rat3</c> together is an ordinary shape, not a tail case. So the figure these
/// thresholds test against is <see cref="WorstCaseTickDamage"/>: the sum of what each live opponent
/// hits for WHEN IT HITS, not a hit-rate-discounted damage-per-tick. A discounted rate is the right
/// number for "how long will this fight take" and the wrong one for "can the next boundary kill me",
/// which is the only question the pill answers.</para>
///
/// <para><b>What the spec's section-10 ban actually forbids.</b> That entry has read as a ban on the
/// whole subject of fleeing, and it is a model's overreach: the owner's objection was to a specific
/// proposed surface - a large gauge, taking half the rail's vertical height, showing how close the
/// player was to being able to flee, which framed reaching the 1-6 stamina band as an OBJECTIVE, and
/// ranked it above winning the fight. What stays banned is that: cost figures, points at risk, flee
/// statistics, and any rendering that presents the cheap band as a goal or a safe place. The pill is
/// the opposite reading of the same band - it is loudest exactly there, and says go, not well done.
/// Nothing here computes or publishes a price.</para>
///
/// <para>Pure and primitive-typed, like <see cref="CombatTierResolver"/> and
/// <see cref="ThreatIndicator"/> beside it, so mudsharp.Tests exercises it directly.</para>
/// </summary>
public static class FleePillResolver
{
    /// <summary>
    /// Stamina at or below which the pill is drawn at all, before any damage evidence is considered:
    /// the survival threshold plus the width of the band below it.
    ///
    /// <para>The owner's own arithmetic, and its reason is cognitive rather than mechanical - the pill
    /// has to be on screen and already noticed BEFORE it starts alarming, or its first appearance is
    /// itself a new thing to read at the worst possible moment. One threshold's worth of warm-up.</para>
    /// </summary>
    public const double ReadyStaminaThreshold =
        CombatTierResolver.SurvivalStaminaThreshold + CombatTierResolver.CriticalStaminaThreshold;

    /// <summary>
    /// What a live opponent is assumed to hit for when nothing is on file about it - owner's decision,
    /// 2026-08-28, replacing an earlier version that contributed nothing at all.
    /// </summary>
    /// <remarks>
    /// <para><b>It is a stand-in, not a floor.</b> A creature with samples uses its samples, however
    /// gentle they turn out to be; a rat measured at 4 a blow contributes 4, not 20. This value applies
    /// only where there is no reading to prefer.</para>
    ///
    /// <para><b>Why 20, and what that rests on.</b> It is the top of the range the OWNER gives for
    /// ordinary NPC maximum damage - "many NPCs have a maximum hit in the 15-20 range" - which is one of
    /// his stated reasons the survival threshold sits at 20 at all
    /// (<c>MUD2-PUBLISHED-MECHANICS.md</c> section 3). So an unknown creature is assumed to be about as
    /// dangerous as an ordinary creature's worst blow.</para>
    ///
    /// <para><b>That is lived experience, not a published ceiling, and the distinction is the one this
    /// project keeps getting wrong.</b> An earlier version of this comment called the figure "published",
    /// which is false twice over: the bullet it comes from is headed "per the owner", and the document
    /// holding it opens by saying its contents are hypotheses transcribed from a fan strategy guide and
    /// that nothing in it may be treated as settled. For a mechanics question the owner's own account
    /// outranks that guide - but neither is a measurement, and there IS a route to a real ceiling that
    /// nothing here uses: <c>bestiary.tsv</c> gives every creature's STR, and the damage bound
    /// <c>1..(CS/6)+1</c> turns that into a hard maximum per creature. Until that reaches runtime, 20 is
    /// a reasonable guess held by one person, and should be read as one.</para>
    ///
    /// <para>Deliberately pessimistic all the same, because the alternative it replaced was silence, and
    /// silence about an unmeasured creature reads as a claim that it is harmless.</para>
    ///
    /// <para><b>It shares a value with <see cref="CombatTierResolver.SurvivalStaminaThreshold"/> and is
    /// not the same quantity.</b> That one is a stamina at which to act; this is a damage a creature
    /// might deal. Do not merge them, do not derive one from the other, and if either is ever tuned it
    /// moves alone - this codebase has already shipped a bug of exactly that shape, where a flee-cost
    /// number was re-labelled a danger threshold because it was the only number available.</para>
    ///
    /// <para><b>Known consequence, so it is not discovered as a surprise:</b> it multiplies. Four
    /// creatures nobody has ever been hit by sum to 80, which raises the pill to Caution from 80
    /// stamina. That is transient - one landed blow per creature replaces the assumption with a
    /// measurement, and the group's history covers any species already met - but a fresh species
    /// arriving in a pack will alarm early. If that reads as crying wolf in play, this constant is the
    /// one knob.</para>
    /// </remarks>
    public const double AssumedUnknownHit = 20.0;

    /// <summary>
    /// What one tick could cost the player if every live opponent lands on it - the sum over live
    /// participants of what each hits for when it hits.
    ///
    /// <para><b>Pessimistic between the two timescales, deliberately.</b> Each creature contributes the
    /// LARGER of its this-fight average and its all-history average. The two answer one question over
    /// different samples and neither is authoritative (MUD2 creatures level up within a reset, and can
    /// be buffed, debuffed or drunk - see <see cref="SwingDamageIndex"/>), so a survival alarm takes the
    /// louder reading. Averaging them would split the difference between a measurement and a baseline,
    /// which is not a number about anything.</para>
    ///
    /// <para><b>A creature with no samples counts as <see cref="AssumedUnknownHit"/></b> rather than as
    /// nothing. Rule 5 - never render unknown as zero - and here the rule bites in the direction that
    /// matters: omitting an unmeasured creature made this total read QUIETER than reality exactly where
    /// the panel is loudest, which is a confident claim of harmlessness dressed up as caution. There is
    /// no runtime bestiary to consult, so the assumption is the honest option; see that constant for
    /// what it is worth and what it costs.</para>
    ///
    /// <para><b>Hidden live participants are extrapolated at the mean of the ones with rows.</b> The
    /// roster caps its rows, so a pack larger than the cap has live opponents with no row of their own.
    /// The mean is preferred over the blanket assumption here because reaching this case means eight
    /// rows of real participants are already in hand, and eight readings say more about the ninth
    /// creature than a global default does. It only engages past
    /// <see cref="ParticipantRoster.MaxRows"/> simultaneous opponents - against a measured maximum of 7
    /// (2026-08-27; tools/combat/concurrency.py over the live clog corpus), so this is one creature away
    /// from being reached in ordinary play rather than the remote branch an earlier version of this
    /// comment claimed when it cited a stale maximum of 4.</para>
    /// </summary>
    public static double WorstCaseTickDamage(RosterPlan plan)
    {
        var total = 0.0;
        var counted = 0;

        foreach (var row in plan.Rows)
        {
            if (!row.IsLive)
                continue;

            // The larger of the two timescales, or the assumption when NEITHER HAS SAMPLES - which is
            // tested on the sample counts, never on the resulting number.
            //
            // A measured zero is a real reading and it has to survive to the total. MUD2 lands blows that
            // take nothing off, DamageProfile counts them deliberately (dropping them would bias the mean
            // into answering "how hard does it hit when it hurts"), so Samples > 0 with Sum == 0 is a
            // reachable state describing a creature that has demonstrably failed to hurt anyone. An
            // earlier version here branched on `worst > 0` and so fed that creature the 20-point
            // assumption - identical treatment to one never seen before, which is the exact "unknown and
            // zero are not the same thing" error the assumption exists to avoid, made in the other
            // direction.
            double? fight = row.FightDamage.HasSamples ? row.FightDamage.Average : null;
            double? ever = row.EverDamage.HasSamples ? row.EverDamage.Average : null;
            var measured = (fight, ever) switch
            {
                (double f, double e) => Math.Max(f, e),
                (double f, null) => f,
                (null, double e) => e,
                _ => (double?)null,
            };

            total += measured ?? AssumedUnknownHit;
            counted++;
        }

        // A division guard, NOT a live branch - and worth saying which, because the difference is
        // invisible from here. ParticipantRoster orders live participants ahead of resolved ones and then
        // truncates, so HiddenLiveCount > 0 implies live participants alone overflowed the cap, which
        // implies every row is live and counted == MaxRows. counted == 0 alongside HiddenLiveCount > 0 is
        // therefore unreachable today. It is kept so this function cannot divide by zero if that ordering
        // is ever changed, since a silent NaN would propagate into a permadeath alarm.
        if (plan.HiddenLiveCount > 0 && counted > 0)
            total += plan.HiddenLiveCount * (total / counted);

        return total;
    }

    /// <summary>
    /// Resolves the pill's state.
    /// </summary>
    /// <param name="inCombat">Whether a fight is actually live. The pill is a fight instrument:
    /// <c>flee</c> is a combat command, and out of combat the player simply walks. The post-kill grace
    /// window counts as not fighting and is gated by the caller, the same way the tick meter's is.</param>
    /// <param name="staminaCurrent">Current stamina, or null if no FES reading has arrived yet.</param>
    /// <param name="worstCaseTickDamage">From <see cref="WorstCaseTickDamage"/>. Zero means "no
    /// evidence", never "harmless" - hence the explicit guards below.</param>
    /// <param name="hitsLeft">Projected incoming blows the player can still absorb, or null. Included
    /// because it is the one signal that is a COUNT rather than a forecast, and the only thing on the
    /// panel that can speak for a fight where stamina is untouched - two hits from death at full health
    /// is how a dragon kills someone. It is already the sole override that promotes the whole-panel glow
    /// (spec section 8), so the pill agreeing with it keeps two readouts from disagreeing about one
    /// state.</param>
    public static FleePillStatus Resolve(
        bool inCombat,
        int? staminaCurrent,
        double worstCaseTickDamage,
        int? hitsLeft)
    {
        if (!inCombat)
            return FleePillStatus.Hidden;

        var twoHitsLeft = hitsLeft is int hits && hits <= 2;

        if (staminaCurrent is not int stamina)
        {
            // No stamina reading at all. The count is the only evidence left, and it is enough to raise
            // the pill but never enough to claim which band the player is in - EscapeNow is a statement
            // about an absolute stamina and cannot be inferred from a projection.
            return twoHitsLeft ? FleePillStatus.Caution : FleePillStatus.Hidden;
        }

        if (stamina <= CombatTierResolver.CriticalStaminaThreshold)
            return FleePillStatus.EscapeNow;

        // A single boundary can now kill. Note this subsumes the per-creature test ("any one of them
        // averages more than my whole stamina") exactly: a sum is never less than its largest term, so
        // the pack case fires wherever the single-creature case would and earlier besides. One rule
        // rather than two that could only ever agree.
        if (stamina <= CombatTierResolver.SurvivalStaminaThreshold
            || (worstCaseTickDamage > 0 && worstCaseTickDamage >= stamina)
            || twoHitsLeft)
            return FleePillStatus.Caution;

        // Warm-up: one average bad tick would land the player at or below the survival threshold.
        if (stamina <= ReadyStaminaThreshold
            || (worstCaseTickDamage > 0
                && stamina <= worstCaseTickDamage + CombatTierResolver.SurvivalStaminaThreshold))
            return FleePillStatus.Visible;

        return FleePillStatus.Hidden;
    }
}
