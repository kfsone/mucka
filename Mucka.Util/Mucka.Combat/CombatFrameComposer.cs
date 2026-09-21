using MudSharp.Combat;

namespace Mucka.Combat;

/// <summary>
/// Everything one Combat Rail frame is composed from. A record rather than a parameter list because
/// the composer reads a dozen unrelated things the view model happens to hold, and a positional call
/// of that width is a defect waiting for its first argument swap.
/// </summary>
/// <param name="Inventory">What the player is carrying, for the Ctrl+W offer. A plain list: the view
/// model holds an <c>ObservableCollection</c>, which is BCL and not MAUI, but nothing here wants to
/// observe it.</param>
/// <param name="IsKnownWeapon">Whether a carried item has ever been used as a weapon - a delegate
/// because it reads the fight-history store, which is attached after this class's callers exist.</param>
/// <param name="Unseen">How many opponents of each anonymous word are open, and whether the player
/// can see. Read once per frame and never accumulated - see <see cref="ParticipantRoster.Build"/>.</param>
/// <param name="ArchiveSnapshot">Prior encounters' endings, frozen at each encounter close. Read only
/// by the no-encounter branch, which keeps the dead strip on screen between fights.</param>
/// <param name="DeadStripHistory">That archive plus the CURRENT encounter's endings, already folded
/// and award-filled by the caller (SidePanelViewModel.BuildDeadStripHistory, which caches against the
/// resolved-fight count and cannot be pure).</param>
/// <param name="StaminaLostLastTick">Stamina lost on the most recent combat tick, one figure for the
/// whole tick - <see cref="TickStaminaLoss.LostThisTick"/>.</param>
/// <param name="LastStaminaLossUtc">When that burst arrived. Only meaningful alongside a nonzero
/// <paramref name="StaminaLostLastTick"/>; see <see cref="TickStaminaLoss.LastLossUtc"/>.</param>
/// <param name="TickAnchor">The combat tick lattice's anchor, for both damage-emphasis cues.</param>
public sealed record CombatFrameInputs(
    CombatEncounterSnapshot Snapshot,
    CombatStatDeficits Deficits,
    CombatHistoryContext History,
    DateTime NowUtc,
    IReadOnlyList<string> Inventory,
    Func<string, bool> IsKnownWeapon,
    UnseenState Unseen,
    IReadOnlyList<CombatEnding> ArchiveSnapshot,
    IReadOnlyList<CombatEnding> DeadStripHistory,
    double StaminaLostLastTick,
    DateTime LastStaminaLossUtc,
    DateTime? TickAnchor,
    string PlayerName,
    byte? StaminaAnsiColor,
    StaminaPoolIndex? StaminaPool = null,
    SwingDamageIndex? SwingDamage = null,
    FightHistoryStore? FightHistory = null,
    ReachMarkIndex? ReachMarks = null,
    SomeKindKnowledge? SomeKinds = null);

/// <summary>
/// One composed frame: the two tiers the panel glows by, and the state the canvas draws.
/// </summary>
public sealed record CombatFrame(
    CombatTier EncumbranceTier,
    CombatTier PulseTier,
    CombatLiveView Live);

/// <summary>
/// The Combat Rail's frame composer: the encumbrance tier, the pulse tier, and the whole
/// <see cref="CombatLiveView"/> across three branches (no encounter / encounter but not in combat /
/// in combat).
///
/// <para>Pure - it returns the frame rather than assigning it, so the stores stay in the view model
/// and every rule below can be asserted from a test. <c>CombatRailResize.ComputeToggle</c> is the
/// same shape: compute here, apply the one side effect at the caller.</para>
///
/// <para>Kept separate from <c>CombatComposition</c>'s content (survivability, participants,
/// exchange, history comparison, weapon table, session totals); this is the layer built on top of
/// it.</para>
/// </summary>
public static class CombatFrameComposer
{
    /// <summary>The stamina at or below which the panel's glow runs whether or not a fight is
    /// happening. Owned here because the glow is composed here; see <see cref="Compose"/> for what
    /// the number is and is not allowed to mean.</summary>
    public const int OutOfCombatVulnerableStamina = 25;

    public static CombatFrame Compose(CombatFrameInputs inputs)
    {
        var snapshot = inputs.Snapshot;
        var deficits = inputs.Deficits;
        var history = inputs.History;
        var nowUtc = inputs.NowUtc;

        // Unconditional (not gated on InCombat): carrying too much is worth flagging through the
        // post-fight grace window too, and costs nothing to recompute - pure arithmetic on values
        // already on hand.
        var encumbranceTier = CombatTierResolver.StrengthTier(
            deficits.StrengthEffective, deficits.StrengthMax);

        // The panel's glow keeps running at low stamina whether or not a fight is happening, because
        // the danger does not stop when the fight does. At this stamina a wandering NPC that would
        // ignore a healthy player will attack, one blow from most creatures can kill, and fleeing
        // still costs real points. Walking away from a fight at 22 stamina and forgetting about it is
        // a way to lose a character between fights.
        //
        // 25 rather than 20: chosen as a margin close enough to the survival threshold to matter with
        // a little room before it.
        var vulnerable = deficits.StaminaCurrent is int sta && sta <= OutOfCombatVulnerableStamina
            ? CombatTier.T3
            : CombatTier.None;

        if (!snapshot.HasEncounter)
        {
            // The dead strip is session-scoped and must survive dismissing the encounter summary -
            // ClearCombatSummaryCommand leaves the session totals, and the strip is part of that, not
            // part of the per-encounter readout being wiped. CombatLiveView.Idle alone blanks it
            // (DeadStripHistory defaults to empty), so HasEncounter is set true here whenever there IS
            // session history to show. CombatRailView.DrawOpponents gates the WHOLE
            // opponent-slot/dead-strip region on that one flag, and with an empty Roster
            // (RosterPlan.Empty, from CombatLiveView.Idle) the live-slot loop draws nothing regardless
            // of it - so this only ever re-enables the dead strip, never the live stack.
            return new CombatFrame(
                encumbranceTier, vulnerable,
                inputs.ArchiveSnapshot.Count == 0
                    ? CombatLiveView.Idle
                    : CombatLiveView.Idle with
                    {
                        HasEncounter = true,
                        DeadStripHistory = inputs.ArchiveSnapshot,
                    });
        }

        // IN COMBAT ONLY. A weapon is a property of the ENCOUNTER, not of the player: MUD2 has no
        // equipment slots and no persistent wield - one is named for the current fight, or as part of
        // starting it ("kill x with y"), and when that fight ends nothing is held. So between fights
        // there is no weapon to report, not an old one worth remembering.
        var liveWeapon = snapshot.InCombat ? snapshot.CurrentWeapon : null;
        var hasWeapon = !string.IsNullOrWhiteSpace(liveWeapon);
        // Empty rather than "UNARMED" for the bare-handed case: the tile draws that word off
        // CombatLiveView.IsUnarmed, which knows whether a fight is running, and this string is only
        // ever the NAME of something held.
        var weaponText = hasWeapon ? CombatComposition.DisplayName(liveWeapon) : string.Empty;
        // Roster/weapon/duration context is worth showing whenever an encounter exists at all, live
        // or just-finished: an encounter's roster and weapon are worth reading while the tile still
        // shows the fight that has just ended.
        //
        // Built AFTER the weapon is known, because each participant's novelty is asked twice - once
        // bare, once against what is actually in hand - and the second question has no answer until
        // the line above has run.
        var facts = ParticipantFacts.ToParticipantFacts(
            snapshot.Fights, nowUtc, liveWeapon,
            inputs.StaminaPool, inputs.SwingDamage, inputs.FightHistory,
            inputs.ReachMarks, inputs.SomeKinds, inputs.TickAnchor);
        // The Unseen state rides in beside the facts: the badges are derived from what is open NOW,
        // on every refresh, never accumulated - see ParticipantRoster.Build.
        var roster = ParticipantRoster.Build(facts, inputs.Unseen);
        // The weapon's own mark: the worst thing the corpus says about this weapon against anything
        // still engaged. Over the FACTS, not the roster rows, so a pack past the row cap still counts.
        var weaponNovelty = CombatNovelty.WeaponRollup(facts);
        // The Ctrl+W offer, in combat only. MUD2 has no equipment slots and no default weapon: a
        // weapon is chosen while fighting, or as part of starting a fight ("kill x with y"). There is
        // nothing a wield could mean between fights, so the offer - and with it the chip advertising
        // the key - exists only while a fight is live.
        //
        // Recomputed on every refresh rather than latched, because the pack changes mid-fight (things
        // get picked up, weapons break) and a stale offer would send a wield for something no longer
        // carried - which costs a dropped guard and a free enemy swing. Cheap: one dictionary probe
        // per carried item over an inventory of a handful.
        var altWeapon = snapshot.InCombat
            ? CombatComposition.ChooseAltWeapon(
                inputs.Inventory, snapshot.CurrentWeapon, history.ByWeapon, inputs.IsKnownWeapon)
            : null;
        var deadStripHistory = inputs.DeadStripHistory;

        if (!snapshot.InCombat)
        {
            // Post-combat / grace window: only the survival PROJECTION (threat/flee) goes quiet -
            // projecting a finished fight's death clock would be a lie. The roster and weapon/duration
            // context stay, exactly as the old formatter's headline/participant rows did.
            return new CombatFrame(
                encumbranceTier, vulnerable,
                new CombatLiveView(
                    InCombat: false, HasEncounter: true, WeaponText: weaponText,
                    // FALSE out of combat, whatever the player is holding. MUD2 has no persistent notion
                    // of being armed - a weapon is named for the CURRENT fight and stops being wielded
                    // when that fight ends. So "unarmed" is not a state the player can be in between
                    // fights; it is the only state, and an alarm about it would fire from the end of every
                    // fight until the start of the next one.
                    //
                    // This flag therefore means "unarmed IN A FIGHT", which is the only thing it can
                    // usefully mean; !hasWeapon would fire falsely for the common case where the
                    // post-combat branch resolves a weapon from the fight that just ended.
                    IsUnarmed: false,
                    Roster: roster,
                    StaminaCurrent: deficits.StaminaCurrent, StaminaMax: deficits.StaminaMax,
                    ObjectsCarried: deficits.ObjectsCarried, Score: deficits.Score,
                    DeadStripHistory: deadStripHistory,
                    MagicCurrent: deficits.MagicCurrent, MagicMax: deficits.MagicMax,
                    AltWeapon: altWeapon,
                    // Rolls up to None on its own here - WeaponRollup skips resolved fights, and outside
                    // combat every fight in the encounter is resolved. Passed rather than omitted so the
                    // two construction sites stay readable as the same record.
                    WeaponNovelty: weaponNovelty));
        }

        var primary = CombatComposition.PrimaryFight(snapshot);
        var outlook = CombatComposition.ComputeOutlook(snapshot, deficits, history, primary);

        // Incoming per-hit rate this fight - thin-sample gated (MinimumOwnHits) the same way the old
        // ladder's own risk pairing gated it, reused here for the tier table's "hits-left" trigger
        // too, so the threat indicator and the tier table never quietly disagree about "how
        // close is this fight".
        double? incomingPerHit = primary is { TheyHits: > 0 } f ? f.ApproxDamageTaken / f.TheyHits : null;
        int? hitsLeft = incomingPerHit is double rate && rate > 0
            && deficits.StaminaCurrent is int sta1 && primary!.TheyHits >= CombatOutlook.MinimumOwnHits
            ? (int)Math.Ceiling(sta1 / rate)
            : null;

        // The encounter table's one coloured cell, off the SAME outlook the tier resolver below reads.
        // Two consumers of one projection, which is what ComputeOutlook was extracted for; what must
        // never happen again is a second ladder derived from the raw seconds beside it.
        var survival = Survival.Read(outlook, CombatTiming.TickMilliseconds);

        var staminaTier = CombatTierResolver.StaminaTier(
            deficits.StaminaCurrent, deficits.StaminaMax, hitsLeft, outlook.SecondsToDie, outlook.SecondsToKill);
        var fightTier = CombatTierResolver.ResolvePulseTier(staminaTier, CombatTier.None);

        // The whole-panel glow is the loudest thing this client owns, so it answers to ONE stamina
        // threshold - the same 25 that governs it out of combat - rather than to the survival
        // projection on its own.
        //
        // The projection promotes to T3 at "under 15 seconds to die", which against an ordinary
        // zombie is arithmetically true from about 30 stamina. That is a correct reading and still
        // too eager for a full-panel flash: it fires while the player is comfortably above the
        // threshold they actually act on, and an alarm that cries wolf at 30 is an alarm that gets
        // ignored at 20. The projection still drives everything quieter.
        //
        // One override survives, because it is not a projection but a count: two hits left or fewer.
        // That is imminent whatever the absolute stamina says - it is how a dragon kills someone at
        // full health.
        var imminent = fightTier == CombatTier.T3 && hitsLeft is int left && left <= 2;
        var pulseTier = imminent || vulnerable == CombatTier.T3
            ? CombatTier.T3
            : fightTier == CombatTier.T3 ? CombatTier.T2 : fightTier;

        // The flee pill computes no flee-cost figure and publishes no price; its loudest state is an
        // alarm about the cheap band, not a report of a cost. One accidental flee from a zombie at
        // 90/100 stamina cost 1300 of 13,000 points and a level, so the player already knows fleeing
        // is expensive. What the panel owes them is the zone signal (staminaTier, above) and a valid
        // direction to run, not a price tag to read while deciding.

        var incomingPerBlow = IncomingPerBlowOf(roster);

        return new CombatFrame(
            encumbranceTier, pulseTier,
            new CombatLiveView(
                InCombat: true, HasEncounter: true, WeaponText: weaponText, IsUnarmed: !hasWeapon,
                Roster: roster,
                StaminaCurrent: deficits.StaminaCurrent, StaminaMax: deficits.StaminaMax,
                ObjectsCarried: deficits.ObjectsCarried, Score: deficits.Score,
                DeadStripHistory: deadStripHistory,
                MagicCurrent: deficits.MagicCurrent, MagicMax: deficits.MagicMax,
                AltWeapon: altWeapon,
                // The flee pill. Fed hitsLeft rather than the resolved tier, so it agrees with the count
                // that already overrides the whole-panel glow instead of deriving a second opinion from the
                // same inputs.
                //
                // Not gated on the grace window here: the grace flag changes without the frame state being
                // rebuilt, so folding it in would leave it stale exactly when it matters. The renderer and
                // the pulse layer both apply that gate themselves, as the tick meter already does.
                FleePill: FleePillResolver.Resolve(
                    inCombat: true, deficits.StaminaCurrent,
                    FleePillResolver.WorstCaseTickDamage(roster), hitsLeft),
                // The incoming half of the border language, pooled over everything still swinging at the
                // player. See CombatLiveView.IncomingTempo for why it is a ratio of sums.
                IncomingTempo: IncomingTempoOf(snapshot),
                WeaponNovelty: weaponNovelty,
                // The player's own prediction bands, off the ONE creature ReachAggregate.GreatestThreat
                // names (see IncomingPerBlowOf) - never a pack summed together. See
                // DamagePrediction.IncomingPerBlow.
                YourNextBlow: DamagePrediction.PlayerAfterBlows(
                    1, deficits.StaminaCurrent, deficits.StaminaMax, incomingPerBlow),
                YourBlowAfter: DamagePrediction.PlayerAfterBlows(
                    2, deficits.StaminaCurrent, deficits.StaminaMax, incomingPerBlow),
                StaminaLostLastTick: inputs.StaminaLostLastTick,
                // Only meaningful alongside a nonzero LostThisTick - see CombatLiveView.StaminaLossUtc.
                StaminaLossUtc: inputs.StaminaLostLastTick > 0 ? inputs.LastStaminaLossUtc : null,
                // The encounter table. Par counts what is still up; Op counts everything this encounter
                // has produced, which is why a fight that has killed four of five reads "1" and "5".
                PlayerName: inputs.PlayerName,
                StaminaAnsiColor: inputs.StaminaAnsiColor,
                YourDealt: ExchangeLines.EncounterLine(snapshot, outgoing: true),
                YourTaken: ExchangeLines.EncounterLine(snapshot, outgoing: false),
                YourExchange: snapshot.Exchange,
                // One reading, resolved here off the same outlook the survivability line uses. The phase
                // rides with it rather than being sampled at paint time, so the 1 Hz flush that already
                // runs through a fight is what makes a blink visible - and it only alternates while the
                // reading is Dire, so nothing republishes once a second for a blink nobody is drawing.
                Survival: survival,
                BlinkOn: survival == SurvivalReading.Dire && Blink.PhaseOn(nowUtc),
                // Unarmed AND below maximum - an unarmed opening is normal and must not raise an alarm.
                BlinkOffPhase: !hasWeapon
                    && deficits.StaminaCurrent is int sta2 && deficits.StaminaMax is int max2 && sta2 < max2
                    && Blink.PhaseOn(nowUtc, inverted: true),
                LiveOpponents: roster.LiveCount,
                OpponentsFaced: roster.TotalCount,
                // Duration comes off the encounter, not the primary fight: a fight that started when the
                // third creature joined has been going a fraction of the time the player has been in
                // trouble, and the table is about the encounter.
                EncounterTicks: EncounterTicksOf(snapshot),
                // Both null until the projection will commit. Same instrument the survivability line uses
                // (CombatComposition.ComputeOutlook), converted to ticks in this one place.
                TicksToVictory: TicksFromSeconds(outlook.SecondsToKill),
                TicksToDeath: TicksFromSeconds(outlook.SecondsToDie),
                // The player's name emphasis, off the same instant the ring's just-lost slice fades from -
                // TickStaminaLoss already groups a tick's blows into one burst with one arrival time, which
                // is exactly the granularity this cue wants. Gated on there being a loss at all, for the
                // same reason StaminaLossUtc is: LastLossUtc is DateTime.MinValue until something lands,
                // and a default that reads as "damage in 1 AD" is not a timestamp.
                PlayerTookDamageThisTick: inputs.StaminaLostLastTick > 0
                    && TickDamageEmphasis.IsOn(inputs.LastStaminaLossUtc, nowUtc, inputs.TickAnchor)));
    }

    /// <summary>
    /// Every live opponent's swings at the player this encounter, pooled into one tempo.
    ///
    /// <para>Hits and misses are ADDED, so the resulting rate is a ratio of sums. An average of the
    /// per-creature rates would weight a rat that has swung twice the same as an ogre that has swung
    /// forty times, which is the shape of aggregate error this panel has been bitten by before.</para>
    ///
    /// <para>Live participants only: a creature that is already dead is not part of what is coming at
    /// the player, and leaving its swings in would keep the border reading busy after a pack was
    /// cleared.</para>
    /// </summary>
    public static SwingTempo IncomingTempoOf(CombatEncounterSnapshot snapshot)
    {
        var tempo = SwingTempo.None;
        foreach (var fight in snapshot.Fights)
        {
            if (!fight.IsResolved)
                tempo = tempo.Plus(new SwingTempo(fight.TheyHits, fight.TheyMisses));
        }
        return tempo;
    }

    /// <summary>
    /// What one incoming blow takes, from the creature <see cref="ReachAggregate.GreatestThreat"/>
    /// names - the greatest measured single-blow reach among the live rows, tie-broken on damage
    /// actually taken.
    ///
    /// <para>One creature, never the pack summed. Falls back to the first live row when nothing has a
    /// reach mark yet, so the bands appear as soon as anything has swung rather than waiting for the
    /// threat ranking to have evidence.</para>
    /// </summary>
    public static DamageBracket? IncomingPerBlowOf(RosterPlan roster)
    {
        var index = ReachAggregate.GreatestThreat(roster);
        if (index < 0)
        {
            for (var i = 0; i < roster.Rows.Count; i++)
            {
                if (roster.Rows[i].IsLive)
                {
                    index = i;
                    break;
                }
            }
        }

        if (index < 0)
            return null;

        var row = roster.Rows[index];
        return DamagePrediction.IncomingPerBlow(row.FightDamage, row.EverDamage);
    }

    /// <summary>
    /// How long the whole ENCOUNTER has been running, in ticks.
    ///
    /// <para>Straight off <c>snapshot.Duration</c>, which the aggregator keeps as
    /// <c>nowUtc - _encounterStartUtc</c>. Walking the fights and taking the longest instead would be
    /// wrong twice over: <see cref="FightAccumulator.DurationAt"/> FREEZES at <c>EndedUtc</c> once a
    /// fight resolves, so killing the last creature would stop Dur advancing while the encounter is
    /// still open through the grace window - and the <c>dmg/tick</c> cells on the same tile divide by
    /// this same encounter duration, so the two readouts would visibly drift apart every second.</para>
    /// </summary>
    public static double? EncounterTicksOf(CombatEncounterSnapshot snapshot)
        => snapshot.Duration > TimeSpan.Zero ? CombatTiming.TicksElapsed(snapshot.Duration) : null;

    /// <summary>Seconds into ticks, or null straight through. Null is the whole point: CombatOutlook
    /// returns null for "not enough evidence to project", and turning that into a zero here would put
    /// a confident "0t to death" on the table at the exact moment the projection was refusing to make
    /// one. Permadeath game; that particular zero is the worst lie on the panel.</summary>
    public static double? TicksFromSeconds(double? seconds)
        => seconds is double value ? value * 1000.0 / CombatTiming.TickMilliseconds : null;
}
