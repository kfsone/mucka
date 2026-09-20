using MudSharp.Combat;

namespace Mucka.Combat;

/// <summary>
/// Maps the client's own <see cref="FightSnapshot"/> list down to the plain facts
/// <see cref="ParticipantRoster.Build"/> needs.
///
/// <para>The roster lives in mudsharp, which does not reference this assembly, so it cannot read a
/// snapshot itself. This class sits on the side of the graph that can see both: Mucka.Combat
/// references mudsharp.</para>
/// </summary>
public static class ParticipantFacts
{
    /// <summary>
    /// One fact per fight, in the order the snapshot gives them.
    ///
    /// <para>The indexes are passed in rather than held, so the "ever" figures are attached at the
    /// one place that knows a store exists - they are a per-participant fact and belong to the
    /// participant, exactly as the NPC's own weapon does, without the roster or the renderer having
    /// to know. Every index is optional: in unit and design contexts there is no corpus, and each
    /// falls back to the answer that claims nothing.</para>
    /// </summary>
    /// <param name="nowUtc">The instant this refresh is composing for; the ages below are resolved
    /// against it so nothing downstream has to be handed a clock.</param>
    /// <param name="currentWeapon">What the player has in hand for THIS fight, for the second,
    /// weapon-narrowed novelty question.</param>
    /// <param name="tickAnchor">The combat tick lattice's anchor (<see cref="TickPhase.Anchor"/>),
    /// for the per-row damage emphasis.</param>
    public static ParticipantFact[] ToParticipantFacts(
        IReadOnlyList<FightSnapshot> fights,
        DateTime nowUtc,
        string? currentWeapon,
        StaminaPoolIndex? staminaPool,
        SwingDamageIndex? swingDamage,
        FightHistoryStore? fightHistory,
        ReachMarkIndex? reachMarks,
        SomeKindKnowledge? someKinds,
        DateTime? tickAnchor)
    {
        var facts = new ParticipantFact[fights.Count];
        for (var i = 0; i < fights.Count; i++)
        {
            var fight = fights[i];
            // Age is resolved to seconds here, at the one point that knows what "now" is, so nothing
            // downstream has to be handed a clock. Negative ages (a reading timestamped marginally
            // ahead of this refresh) clamp to zero rather than reading as fresher than fresh.
            double? healthAge = fight.HealthReadUtc is DateTime read
                ? Math.Max(0.0, (nowUtc - read).TotalSeconds)
                : null;
            // The diagnose probe's own age, resolved the same way and kept separate from the
            // descriptor's: the two fade for different reasons - see RosterRow.StaminaReadStaleAfterSeconds.
            double? staminaReadAge = fight.StaminaReadUtc is DateTime probed
                ? Math.Max(0.0, (nowUtc - probed).TotalSeconds)
                : null;
            // Null (not StaminaPoolEstimate.None) with no index attached, so the narrowing step can
            // tell "no pool index in this context" from "an index with nothing on file for this
            // creature" - the second is a real answer about a species and the first is not an answer
            // at all. Either way the rung's own seventh still stands on its own.
            var pool = staminaPool?.Lookup(fight.NpcName);
            // The seal's fill, per participant rather than for the primary target alone - the whole
            // point of a seal per slot is that a pack can be read row against row, which a figure only
            // the current target carries cannot support.
            //
            // A FRACTION, not a stamina figure. The rung is the source (it is what MUD2 actually
            // printed and what the player themselves read); the pool estimate and this fight's damage
            // brackets only narrow the position inside that seventh, and no absolute number they
            // produce reaches the screen. Both probes are dictionary lookups under a lock held for
            // exactly that probe (StaminaPoolIndex's own remarks), run for at most MaxRows opponents
            // per refresh.
            var vitality = NpcVitality.Estimate(
                fight.HealthRung, pool, pool is null ? null : RemainingFor(pool, fight),
                // The crossing is the sharpest constraint of the three on a large creature, and it needs
                // this fight's cumulative bracket because on a first encounter that is the only floor
                // under the pool there is.
                fight.RungCrossing, fight.YourDamage,
                // "full of life" / "full of energy" is cur == max exactly, not merely the top band, so
                // it is the one reading that can fill the seal. Without it the hard fill tops out at
                // the rung floor - 6/7 - and an untouched creature never draws full.
                atMax: fight.HealthPhrase is { } phrase && NpcHealthRungs.IsAtMax(phrase));
            // One probe, three answers. Narrowed by the creature's CURRENT weapon so the armed-as-now
            // profile comes back alongside the species-wide one; both are dictionary lookups under a
            // single lock (SwingDamageIndex's own remarks), and taking them together rather than in
            // two calls halves the locking on a path that runs once per opponent per refresh.
            var damage = swingDamage?.Lookup(fight.NpcName, fight.NpcWeapon) ?? OpponentDamage.Empty;
            var perBlow = DamagePrediction.PerBlow(
                fight.YourDamage, fight.YouHits, damage.Outgoing);
            // (None, None) with no store attached (unit/design contexts): no corpus means no evidence
            // either way, and "unfought" is a claim about the corpus rather than the absence of one.
            var novelty = fightHistory?.NoveltyFor(fight.NpcName, currentWeapon)
                ?? (NoveltyMark.None, NoveltyMark.None);

            facts[i] = new ParticipantFact(
                fight.NpcName, fight.IsResolved, fight.Outcome,
                fight.HealthRung, fight.HealthPhrase, healthAge, fight.ApproxDamageTaken,
                fight.NpcWeapon,
                fight.TheirDamage,
                // Empty (which draws as nothing) whenever the cache is absent or has too few blows on
                // file - never a zero, which would read as "this thing cannot hurt you". Only the
                // incoming half reaches the rail today; the outgoing brackets are cached alongside it
                // for the exchange bars and the analysis view, which want both sides.
                // BestIncoming, not Incoming: narrowed to the weapon this creature is actually holding
                // when enough blows have been seen through it. This feeds the player's own prediction
                // bands and the flee readout, which is exactly where the sharper number belongs.
                damage.BestIncoming,
                vitality,
                // rDPT: where the next two landed blows put this creature's boundary, as intervals off
                // the player's own damage brackets. This fight's bracket is preferred over history so a
                // weapon swap, a dropped load or a magic buff moves the bands on the very next blow -
                // nothing here latches. See MudSharp.Combat.DamagePrediction for what each evidence
                // state buys and why this replaced a time-based forecast.
                DamagePrediction.AfterBlows(1, vitality, pool, perBlow, fight.YourDamage, fight.RungCrossing),
                DamagePrediction.AfterBlows(2, vitality, pool, perBlow, fight.YourDamage, fight.RungCrossing),
                new SwingTempo(fight.YouHits, fight.YouMisses),
                // The reach mark is per SPECIES and includes this encounter's own blows, so it is the
                // only figure on this row that can already know about the hit that landed a second ago.
                reachMarks?.Lookup(fight.NpcName) ?? ReachMark.None,
                // Novelty: has this creature's KIND ever been fought, and ever killed - once bare, once
                // narrowed to the weapon in hand. Two dictionary probes under one lock, per row.
                //
                // The index holds only CLOSED, flushed fights (HistoryIndex's own remarks), so the
                // encounter on screen cannot enter its own answer. That is the property that makes the
                // marks hold still: a creature met for the first time stays orange for the whole of the
                // fight that is teaching you about it, and only stops being new on the NEXT one.
                novelty.Name, novelty.Weapon,
                // The diagnose probe, with the damage dealt since it so the slot can show what the
                // Creature has left NOW rather than what it had when the probe was taken. Kept for the
                // rest of the fight rather than expiring; the probe's own age rides alongside and dims
                // it - see RosterRow.StaminaReadStaleAfterSeconds for what time costs a reading that
                // damage cannot.
                fight.StaminaReading,
                staminaReadAge,
                fight.Value,
                ExchangeLines.DealtLine(fight),
                ExchangeLines.TakenLine(fight),
                fight.Exchange,
                // The name's emphasis. HealthReadUtc is the arrival of a WOUND DESCRIPTOR, and MUD2
                // prints one after every landed blow that does not kill - 3,559 descriptors against
                // 3,561 such hits across 1,197 fights - so it is the sharpest "this creature just took
                // damage" instant the client has. `diagnose` does not touch it (that is
                // FightAccumulator.NoteStaminaRead), so a probe cannot fake a blow. What it does
                // include is damage the player did not deal: NPC-versus-NPC combat is in the corpus,
                // and a creature being hurt by something else is still a creature being hurt, which is
                // what the cue claims.
                TickDamageEmphasis.IsOn(fight.HealthReadUtc, nowUtc, tickAnchor),
                // Which word this species gets when Unseen, if learned. Null for the anonymous words
                // themselves (they are not a species) and for anything never fought unseen.
                someKinds?.Known(fight.NpcName));
        }
        return facts;
    }

    /// <summary>One creature's remaining-stamina band: the species estimate, less this fight's damage
    /// brackets, narrowed by the live health rung and by any `diagnose` reading. Null when nothing at
    /// all supports one - a band with no evidence is not a band.
    ///
    /// <para>Absolute, and it never leaves this file. Its only consumer is NpcVitality.Estimate, which
    /// divides it by the pool to place the seal's fill inside the rung the game printed; no figure it
    /// produces is ever drawn.</para></summary>
    private static NpcStaminaBand? RemainingFor(StaminaPoolEstimate pool, FightSnapshot fight)
    {
        var band = NpcRemainingStamina.Compute(
            pool, fight.YourDamage, fight.RungAnchor, fight.StaminaReading);
        return band.HasEvidence ? band : null;
    }
}
