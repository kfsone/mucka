namespace MudSharp.Combat;

/// <summary>
/// What the fight corpus says about a creature the player is facing RIGHT NOW, reduced to the one
/// question the rail asks: is this a thing you have never fought, a thing you have fought and never
/// finished, or a thing you have killed before?
///
/// <para><b>The order is the severity order and the numbers matter</b> - <see cref="CombatNovelty.WeaponRollup"/> takes
/// the maximum, so <see cref="Undefeated"/> must sort above <see cref="Unfought"/>.</para>
///
/// <para><b>This hierarchy is deliberately not a danger ramp.</b> Orange means "no evidence"; red
/// means "evidence, and it says you could not finish this". Red is worse than unknown here, which is
/// the opposite of the convention where an unknown is the scary one, and it is the owner's own
/// reading: a creature you have already failed to kill is a known problem, where one you have never
/// met is merely an open question. Do not "correct" this to orange-then-red-by-difficulty.</para>
/// </summary>
public enum NoveltyMark
{
    /// <summary>Fought before AND killed at least once. Nothing is drawn.</summary>
    None = 0,

    /// <summary>Never fought - no closed fight on file for this creature's pool key. Orange.</summary>
    Unfought = 1,

    /// <summary>Fought at least once and never killed: fled from, killed by, withdrawn from, the
    /// creature broke off, or the fight was never resolved. Red.</summary>
    Undefeated = 2,
}

/// <summary>
/// Turns "have I fought this / have I killed this" into a <see cref="NoveltyMark"/>, and rolls a
/// pack of them into the single mark the player's own weapon carries.
///
/// <para>Kept as a pure static beside <see cref="CombatTierResolver"/> and
/// <see cref="FleePillResolver"/> - same reason as those: the decision is testable without a
/// canvas, a view model or a database, and the renderer is left with nothing to decide.</para>
/// </summary>
public static class CombatNovelty
{
    /// <summary>
    /// One creature's mark. <paramref name="defeated"/> implies <paramref name="fought"/> and the
    /// two are read in that order, so a caller that can only answer "killed" cannot produce the
    /// nonsense state "killed but never fought".
    /// </summary>
    public static NoveltyMark Classify(bool fought, bool defeated)
        => defeated ? NoveltyMark.None
         : fought ? NoveltyMark.Undefeated
         : NoveltyMark.Unfought;

    /// <summary>
    /// Whether a stored fight outcome means the creature ENDED UP DEAD, which is the only question
    /// the novelty marks ask.
    ///
    /// <para><b>Deliberately not <see cref="FightRecord.IsKill"/>, and the difference is the whole
    /// point of this method existing.</b> That property means "the player's own blow landed last",
    /// which is the right test for the stamina-pool estimator and the wrong one here.
    /// <see cref="FightOutcome.NoMore"/> - the outcome for "The X drops dead, poisoned..." - is a
    /// dead creature, and the red mark says "you have fought this and could not finish it". Marking a
    /// creature you poisoned to death as unfinished is simply false.</para>
    ///
    /// <para><b>The operator's own framing, 2026-09-02</b>, which is also why the family is open
    /// rather than poison-specific: "NoMore usually indicates a non-combat defeat of the npc - such
    /// as poisoning them, or if you were in combat with the dragon when it dies from eating coal 10
    /// minutes earlier, etc. So it's a kill but an outlier to be ignored if it has no swings." The
    /// coal is the case that shows why no damage figure here can be trusted: the thing that killed it
    /// happened in another room, ten minutes earlier, and nothing about it crossed the wire during
    /// this fight.</para>
    ///
    /// <para><b>The second half of that sentence is a different rule with a different owner.</b>
    /// "An outlier to be ignored if it has no swings" governs POOL ESTIMATION, not this. A NoMore row
    /// bounds a pool from below at best and must never contribute a ceiling, because the damage that
    /// finished the creature was never observed - see <see cref="StaminaPoolEstimator"/>, which owns
    /// that decision. Both rules read the same outcome and reach opposite conclusions, correctly:
    /// "did it die" and "did we watch what killed it" are not the same question.</para>
    /// </summary>
    public static bool CountsAsDefeated(string? outcome)
        => outcome == nameof(FightOutcome.Kill) || outcome == nameof(FightOutcome.NoMore);

    /// <summary>
    /// The mark the weapon in hand carries, across everything still ENGAGED: the worst mark any one
    /// of them contributes.
    ///
    /// <para>The owner's wording is "if there is ANY engaged npc you have ... " for both colours, so
    /// this is a maximum and not a vote. Red therefore wins over orange whenever both are present -
    /// one creature this weapon has never met matters less than one it has met and failed to
    /// finish.</para>
    ///
    /// <para>Nothing engaged is <see cref="NoveltyMark.None"/>: there is no claim to make about the
    /// weapon.</para>
    ///
    /// <para>Resolved fights are skipped. The claim the weapon's colour makes is about what is
    /// currently swinging back - a rat you killed two minutes ago is neither a reason to warn about
    /// the weapon nor, having been killed, a creature this weapon failed on.</para>
    ///
    /// <para>Runs over every fight in the encounter, not just the rows the rail has room to draw: a
    /// pack larger than the row cap must not silently drop the very opponent the warning is about.
    /// Allocation-free, so it is safe on the refresh path (Invariant #1).</para>
    /// </summary>
    public static NoveltyMark WeaponRollup(IReadOnlyList<ParticipantFact> facts)
    {
        var worst = NoveltyMark.None;
        for (var i = 0; i < facts.Count; i++)
        {
            if (facts[i].IsResolved)
                continue;
            if (facts[i].WeaponNovelty > worst)
                worst = facts[i].WeaponNovelty;
            if (worst == NoveltyMark.Undefeated)
                break;
        }
        return worst;
    }
}
