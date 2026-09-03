namespace MudSharp.Combat;

/// <summary>
/// When two consecutive fights against one instance name are the SAME creature being chased, and when
/// they are the player coming back later to try again.
///
/// <para><b>This whole policy is a proposal, not a measured law, and it is deliberately one class so it
/// can be reverted in one edit.</b> The owner has not blessed the discriminator; what he described is
/// the case it has to separate - fight the banshee, it withdraws, "I'm so low on stamina I leave, go
/// sleep, and come back minutes later to retry the fight with a [different] weapon and from full
/// stamina, perhaps with a dream-word under my belt". Everything below is an attempt to detect exactly
/// that, and if it is wrong the fix is here and nowhere else.</para>
///
/// <para><b>Player state is the test; time is only a backstop.</b> A chase is one continuous player
/// state - still depleted, same weapon in hand, same maximum. A deliberate re-attempt resets at least
/// one of those. Measured over the corpus (223 same-name pairs inside two minutes, 160 of them with
/// player state on both sides) the separation is not marginal: stamina recovery across the gap sits at
/// 1.1% of maximum at the 95th percentile, and the next values above it jump to 34%, 37%, 40%, 43% and
/// 77%. Every threshold between 7% and 33% classifies the identical five pairs as re-attempts, so
/// <see cref="RecoveredFraction"/> is not a knife edge. Weapon changed across 12 pairs, maximum
/// across 1.</para>
///
/// <para><b>Why not lead with the clock.</b> The gaps are far tighter than the two-minute window the
/// old exclusion used: median 4.2s, p75 6.7s, p95 35.8s across those 223 pairs. A clock alone cannot
/// tell a four-second chase from a four-second pause before a deliberate re-attack, and it wrongly
/// separates a slow one. <see cref="BackstopMs"/> exists only to stop an unbounded chain forming when
/// no state signal fires at all; 221 of the 223 pairs fall inside it.</para>
///
/// <para><b>Sleeping is covered, without a detector.</b> The operator reports that sentient creatures
/// sleep to accelerate recovery, and a chain must not span that. There is no signal to test on: the
/// sleep wordings ("The thief has just fallen asleep.") appear only as clog plain text and never on
/// the wire, so the C1 code is unknown and no honest detector can be built today. It costs nothing
/// here, because the POINT of sleeping is to recover and recovering is exactly what
/// <see cref="RecoveredFraction"/> separates on. What an explicit detector would add is the case of
/// sleeping without recovering, which is not a thing. Recorded so that when the code is identified,
/// this is where the clause belongs.</para>
/// </summary>
public static class ChaseLinkPolicy
{
    /// <summary>
    /// Stamina recovery across the gap, as a fraction of the player's maximum, above which the next
    /// fight is a deliberate re-attempt rather than a chase.
    ///
    /// <para>25% sits in the middle of a wide flat region: every value from 7% to 33% gives the same
    /// answer on the corpus, because real chases cluster at zero recovery and re-attempts at a third or
    /// more. It is not tuned to a boundary case, because there is no boundary case.</para>
    /// </summary>
    public const double RecoveredFraction = 0.25;

    /// <summary>
    /// The longest gap that may still be a chase when nothing about the player's state has changed.
    /// The owner's own suggested figure, and comfortably past the measured p95 of 35.8s - 221 of 223
    /// same-name pairs inside two minutes fall within it.
    /// </summary>
    public const long BackstopMs = 90_000;

    /// <summary>Whether <paramref name="next"/> continues the chase begun by <paramref name="previous"/>.</summary>
    public static bool IsChase(PoolFightObservation previous, PoolFightObservation next)
    {
        // A kill ends the chain absolutely, and the game has a mechanism that makes this a live hazard
        // rather than a theoretical one. The operator, 2026-09-02: "there's a potion in-game that
        // summons creatures, and can draw from the full pool including dead ones. that's how we'd have
        // the occasional killed-twice-in-one-reset. it's also possible for a wizard to resummon a
        // previously dead creature, so it could happen more than 2x, but there can't be more than two
        // of the same id'd cre at the same time."
        //
        // So a name really can carry a second creature after the first dies, inside one reset - and
        // summoning is a repeatable player action, so this is not bounded by respawn timing. Linking
        // across it would add one creature's damage to another's pool. Do not read this rule as
        // paranoia and remove it.
        //
        // The second life is good evidence, not contamination, which is why it becomes its OWN
        // observation rather than being dropped: measured over the corpus, successors of a same-reset
        // kill reproduce the isolated kill distribution (rats n=10, median damage-to-kill 37.5 against
        // an isolated median of 37, IQR 33-42). See AKillFollowedByTheSameNameIsTwoObservations.
        if (previous.EndedInKill)
            return false;

        var gap = next.StartedAtMs - Math.Max(previous.EndedAtMs, previous.StartedAtMs);
        if (gap < 0 || gap > BackstopMs)
            return false;

        var before = previous.PlayerState;
        var after = next.PlayerState;

        // A dreamword. The player went and got stronger, which is the definition of a fresh attempt.
        if (before.StaminaMaxAtEnd is int maxBefore && after.StaminaMaxAtEnd is int maxAfter
            && maxBefore != maxAfter)
        {
            return false;
        }

        // A different weapon in hand. Deliberate re-arming, not a chase.
        if (!string.IsNullOrWhiteSpace(before.Weapon) && !string.IsNullOrWhiteSpace(after.Weapon)
            && !string.Equals(before.Weapon, after.Weapon, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Came back substantially recovered - slept, ate, waited it out.
        if (before.StaminaAtEnd is int staBefore && after.StaminaAtStart is int staAfter
            && before.StaminaMaxAtEnd is int ceiling && ceiling > 0
            && (staAfter - staBefore) / (double)ceiling > RecoveredFraction)
        {
            return false;
        }

        return true;
    }
}

/// <summary>
/// Joins consecutive engagements against one creature into a single observation, and throws away the
/// ones whose damage cannot be attributed to any single creature at all.
///
/// <para><b>What this replaced, and why.</b> The estimator used to DROP any fight preceded by another
/// against the same name inside two minutes, keeping the first link. That protected the corpus from
/// re-engagement contamination but paid for it with the terminal kill: the long opening fight - a
/// floor only - was kept, and the short fight that finished the creature, the only two-sided
/// constraint there is, was discarded. Those discarded fights are 12.6% of fights but just 2.7% of
/// swings, and 131 of 1,384 kills.</para>
///
/// <para><b>Linking is sound because the chain is one creature at one pool.</b> Two independent checks
/// say so. Chain totals reproduce isolated kill totals within a few points across eight species, which
/// would not happen if the links were different creatures or if the creature healed appreciably
/// between them. Direct rung continuity agrees: under 10 seconds of disengagement the rung does not
/// move (15 of 17 readings identical), while past 30 seconds it always does. Only the zombie has a
/// supportable healing rate at all - about 0.25 stamina per second of disengagement against a ~42
/// pool, so roughly 1.4 stamina over a median gap.</para>
/// </summary>
public static class ChaseLinker
{
    /// <summary>
    /// The observations to actually estimate from: chains joined, folds dropped.
    /// </summary>
    /// <returns>
    /// <c>Chains</c> in time order, one per creature-engagement-chain. <c>Linked</c> counts fights
    /// folded into a predecessor rather than standing alone. <c>DroppedInFolds</c> counts fights thrown
    /// away because several creatures shared one printed name inside a single encounter.
    /// </returns>
    public static (IReadOnlyList<PoolFightObservation> Chains, int Linked, int DroppedInFolds) Link(
        IEnumerable<PoolFightObservation> observations)
    {
        var ordered = observations.ToList();
        ordered.Sort((a, b) => a.StartedAtMs.CompareTo(b.StartedAtMs));

        var byName = new Dictionary<string, List<PoolFightObservation>>(StringComparer.OrdinalIgnoreCase);
        foreach (var observation in ordered)
        {
            var name = observation.NpcName ?? string.Empty;
            if (!byName.TryGetValue(name, out var list))
                byName[name] = list = [];
            list.Add(observation);
        }

        var chains = new List<PoolFightObservation>(ordered.Count);
        var linked = 0;
        var droppedInFolds = 0;

        foreach (var group in byName.Values)
        {
            var folds = FoldedEncounters(group);
            List<PoolFightObservation>? run = null;

            foreach (var fight in group)
            {
                // A fold: several creatures printed under one name inside a single encounter. The first
                // row absorbed every blow landed on every one of them before the first died, so it is
                // the WORST row to keep - and the old filter kept exactly it. Nothing here can
                // attribute damage between them, so the whole group goes.
                //
                // The last link of a fold is probably clean, since by then the others are dead - but
                // only "probably": the client cannot know how many creatures shared the name, so it
                // cannot know the final link faced one. Dropping the lot is the honest reading.
                if (fight.EncounterStartedAtMs is long encounter && folds.Contains(encounter))
                {
                    linked += Flush(chains, ref run);
                    droppedInFolds++;
                    continue;
                }

                if (run is { Count: > 0 } && ChaseLinkPolicy.IsChase(run[^1], fight))
                {
                    run.Add(fight);
                    continue;
                }

                linked += Flush(chains, ref run);
                run = [fight];
            }

            linked += Flush(chains, ref run);
        }

        chains.Sort((a, b) => a.StartedAtMs.CompareTo(b.StartedAtMs));
        return (chains, linked, droppedInFolds);
    }

    private static int Flush(List<PoolFightObservation> chains, ref List<PoolFightObservation>? run)
    {
        if (run is not { Count: > 0 })
            return 0;

        chains.Add(Join(run));
        var linked = run.Count - 1;
        run = null;
        return linked;
    }

    /// <summary>
    /// Encounters in which this name was fought more than once AND a fight before the last one ended in
    /// a kill.
    ///
    /// <para>That second clause is the whole discriminator between a fold and an in-encounter chase.
    /// Both produce two same-name fights inside one encounter, and they want opposite handling: a chase
    /// is the creature breaking off and being re-attacked, which links; a fold is two creatures sharing
    /// a printed name, which cannot be attributed at all. If the earlier fight ended in a KILL then the
    /// creature carrying that name is dead, so whatever answers to it afterwards in the same encounter
    /// is a different creature.</para>
    ///
    /// <para><b>Two different mechanisms produce this signature, with different bounds.</b> A NOID name
    /// - bare "rat", no instance number - is shared by an unbounded number of creatures. A summoning
    /// potion or a wizard resummon can give one ID'D name a second life, but "there can't be more than
    /// two of the same id'd cre at the same time" (operator, 2026-09-02). Both look identical from here:
    /// a kill followed by another terminator under one name.</para>
    ///
    /// <para><b>The corpus supports only the NOID cause.</b> Three encounters in 1,195 clogs show a kill
    /// followed by another terminator for the same name, and all three are bare names - "rat" with
    /// THREE kills, and "zombie" twice with two. Three lives rules out the same-id cap and confirms
    /// NOID for that one. Zero id'd names show the signature inside an encounter, and no same
    /// name+encounter pair even overlaps in time, so the cap of two is nowhere near being tested.</para>
    ///
    /// <para><b>The cap is an invariant worth knowing rather than asserting.</b> For an ID'D name, more
    /// than two concurrent lives would be a parse defect and not a game state; for a NOID name any
    /// number is expected. Nothing here counts them, because a counter that has never fired and cannot
    /// fire on the observed cause would be dead code - but if a future reader sees three concurrent
    /// fights under one numbered name, that is the parser to look at, not the game.</para>
    /// </summary>
    private static HashSet<long> FoldedEncounters(List<PoolFightObservation> group)
    {
        var folded = new HashSet<long>();
        for (var i = 0; i < group.Count - 1; i++)
        {
            if (!group[i].EndedInKill || group[i].EncounterStartedAtMs is not long encounter)
                continue;
            if (group[i + 1].EncounterStartedAtMs == encounter)
                folded.Add(encounter);
        }
        return folded;
    }

    /// <summary>
    /// Collapses a chain into one observation against one pool: every blow in order, every rung reading
    /// re-indexed onto the combined blow sequence, and the LAST link's ending - which is what makes a
    /// terminal kill usable as a ceiling over the chain's cumulative damage.
    /// </summary>
    private static PoolFightObservation Join(List<PoolFightObservation> run)
    {
        if (run.Count == 1)
            return run[0];

        var blows = new List<DamageBracket>();
        var rungs = new List<RungReading>();

        foreach (var link in run)
        {
            var offset = blows.Count;
            foreach (var rung in link.Rungs)
                rungs.Add(rung with { LandedBlowsBefore = rung.LandedBlowsBefore + offset });
            blows.AddRange(link.Blows);
        }

        var last = run[^1];
        return run[0] with
        {
            EndedAtMs = last.EndedAtMs,
            EndedInKill = last.EndedInKill,
            Blows = blows,
            Rungs = rungs,
            PlayerState = last.PlayerState,
        };
    }
}
