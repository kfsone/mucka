using Mucka.ViewModels;
using MudSharp.Combat;
using MudSharp.Models;

namespace mudsharp.Tests.Fixtures;

/// <summary>
/// Regressions for combat lines the parser did not recognise, all found by reducing two real play
/// sessions (see tools/combat/SESSION-NOTES-20260810.md). Every string here is quoted verbatim from a
/// capture - none is invented, and none should be "tidied up" to read better.
/// </summary>
public sealed class ParserGapTests
{
    private static readonly DateTime T0 = new(2026, 8, 10, 15, 0, 0, DateTimeKind.Utc);

    private static StyledLine Line(string text) => new([new StyledSpan(text, TextStyle.Default)]);

    private static List<CombatEvent> Observe(params string[] lines)
    {
        var tracker = new CombatTracker();
        var seen = new List<CombatEvent>();
        tracker.EventOccurred += seen.Add;
        for (var i = 0; i < lines.Length; i++)
            tracker.Observe(Line(lines[i]), T0.AddSeconds(i));
        return seen;
    }

    // ---- A failed flee is not a flee, but it IS an end of fight -----------------------------

    /// <summary>
    /// The highest-value gap in the session review. A water-snake attempted this 7 times in 13 seconds
    /// and never left the room, and the game prints "You can fight it no longer." after each one.
    ///
    /// <para>Note what this test does NOT assert any more. It used to also pin "the fight stays open",
    /// on the reasoning that 7 attempts inside one 13-second fight must be one encounter rather than
    /// eight. The owner corrected that on 2026-08-19: a failed flee really does end the fight, so those
    /// are eight encounters - eight frames, eight attack commands, eight weapon selections - and the
    /// price of the old reading was a fight the player never re-opened staying "in combat" forever.
    /// See <see cref="FailedFlee_EndsTheFight_CreatureStaysInTheRoom"/>.</para>
    /// </summary>
    [Fact]
    public void FailedFlee_IsReportedAsAnAttempt_NotAnEscape()
    {
        var events = Observe(
            "You attack the water-snake5, using the falchion as a weapon.",
            "The water-snake5 has fled by trying to go over.");

        var failed = Assert.Single(events, e => e.Kind == CombatEventKind.NpcFleeFailed);
        Assert.Equal("water-snake5", failed.NpcName);
        // The distinction that matters: nothing here may read as an escape, or the chase assist would
        // send the player after a creature standing in front of them.
        Assert.DoesNotContain(events, e => e.Kind == CombatEventKind.NpcFled);
    }

    /// <summary>
    /// The fight ENDS: MUD2 breaks the sequence even though the creature never left the room, and the
    /// player has to attack again to re-engage.
    ///
    /// <para>This is the exact frame the owner reported (water-snake3, 2026-08-19). It inverts the
    /// previous expectation, which was that the fight stayed open - and that is why the bug existed at
    /// all: nothing else in this frame can close a fight, so a player who walked away instead of
    /// re-attacking left the panel claiming combat until reset or logout.</para>
    /// </summary>
    [Fact]
    public void FailedFlee_EndsTheFight_CreatureStaysInTheRoom()
    {
        var tracker = new CombatTracker();
        tracker.Observe(Line("You attack the water-snake3, using the falchion as a weapon."), T0);
        Assert.True(tracker.InCombat);

        tracker.Observe(Line("The water-snake3 looks close to death."), T0.AddSeconds(1));
        tracker.Observe(Line("The water-snake3 hits you (82/115)."), T0.AddSeconds(2));
        tracker.Observe(Line("The water-snake3 has fled by trying to go up."), T0.AddSeconds(2));

        // Closed by the flee-fail itself, BEFORE the trailing acknowledgment - which is what makes the
        // acknowledgment redundant rather than load-bearing.
        Assert.False(tracker.InCombat);

        tracker.Observe(Line("You can fight it no longer."), T0.AddSeconds(2));
        Assert.False(tracker.InCombat);
    }

    /// <summary>Re-attacking the creature that is still standing there opens a NEW encounter - it is a
    /// new frame, a new command and a new weapon selection, and the owner's model says that is exactly
    /// what it is.</summary>
    [Fact]
    public void FailedFlee_ThenReattack_OpensAFreshEncounter()
    {
        var tracker = new CombatTracker();
        var flips = new List<bool>();
        tracker.InCombatChanged += flips.Add;

        tracker.Observe(Line("You attack the water-snake3, using the falchion as a weapon."), T0);
        tracker.Observe(Line("The water-snake3 has fled by trying to go up."), T0.AddSeconds(2));
        tracker.Observe(Line("You can fight it no longer."), T0.AddSeconds(2));
        tracker.Observe(Line("You attack the water-snake3, using the falchion as a weapon."), T0.AddSeconds(4));

        Assert.True(tracker.InCombat);
        Assert.Equal([true, false, true], flips);
    }

    /// <summary>A pack member breaking off must not take the rest of the pack with it: cases 1-3 and 6
    /// are per-creature.</summary>
    [Fact]
    public void FailedFlee_ClosesOnlyItsOwnFight()
    {
        var tracker = new CombatTracker();
        tracker.Observe(Line("You attack the water-snake3, using the falchion as a weapon."), T0);
        tracker.Observe(Line("You attack the water-snake5, using the falchion as a weapon."), T0.AddSeconds(1));
        tracker.Observe(Line("The water-snake3 has fled by trying to go up."), T0.AddSeconds(2));

        Assert.True(tracker.InCombat);   // snake5 is still engaged

        tracker.Observe(Line("The water-snake5 has fled by trying to go up."), T0.AddSeconds(3));
        Assert.False(tracker.InCombat);
    }

    /// <summary>Real escapes must keep working exactly as before - the two lines differ by one word.</summary>
    [Fact]
    public void RealFlee_StillEndsTheFight()
    {
        var events = Observe(
            "You attack the raven, using the falchion as a weapon.",
            "The raven has fled by going southeast.");

        Assert.Contains(events, e => e.Kind == CombatEventKind.NpcFled);
        Assert.DoesNotContain(events, e => e.Kind == CombatEventKind.NpcFleeFailed);
    }

    /// <summary>The attempt is counted AND the fight is resolved - as CFledFail, never as an escape.
    /// Both halves matter: the count is what tells the player this creature is tedious rather than
    /// dangerous, and the outcome is what keeps it out of the escape statistics.</summary>
    [Fact]
    public void FailedFlees_AreCountedAndResolveTheFightAsCFledFail()
    {
        var aggregator = new CombatStatsAggregator();
        var tracker = new CombatTracker();
        tracker.EventOccurred += aggregator.Observe;

        string[] lines =
        [
            "You attack the water-snake5, using the falchion as a weapon.",
            "The water-snake5 has fled by trying to go over.",
        ];
        for (var i = 0; i < lines.Length; i++)
            tracker.Observe(Line(lines[i]), T0.AddSeconds(i));

        var snapshot = aggregator.Snapshot(T0.AddSeconds(10));
        var fight = Assert.Single(snapshot.Fights);
        Assert.True(fight.IsResolved);
        Assert.Equal(FightOutcome.CFledFail, fight.Outcome);
        // FleeAttempts lives on the accumulator, not on the rendered snapshot row - the count has no
        // consumer yet (nothing reads it, and it is not persisted on FightRecord), so it is asserted
        // at its source rather than through a surface that does not carry it.
        Assert.Equal(1, Assert.Single(aggregator.Fights).FleeAttempts);
    }

    // ---- Health readings folded into a longer sentence --------------------------------------

    /// <summary>The reading is real and was being dropped purely because the sentence carried on past
    /// it. Captured verbatim from a ram.</summary>
    [Fact]
    public void HealthReading_SurvivesARunOnSentence()
    {
        Assert.True(NpcHealthRungs.TryParse(
            "The ram looks covered in wounds, and is holding the following:",
            out var npc, out var rung, out var phrase));

        Assert.Equal("ram", npc);
        Assert.Equal(4, rung);
        Assert.Equal("covered in wounds", phrase);
    }

    /// <summary>Widening the terminator must not have opened the door to the lines that merely look
    /// like health readings.</summary>
    [Theory]
    [InlineData("The coracle looks to be in relatively good condition.")]
    [InlineData("The rat looks at you furiously.")]
    [InlineData("The rat17 looks at you madly, and is holding the following:")]
    [InlineData("The thief looks moving towards you purposefully.")]
    public void HealthReading_StillRejectsLookalikes(string line)
        => Assert.False(NpcHealthRungs.TryParse(line, out _, out _, out _), line);

    // ---- The game does report NPC stamina ---------------------------------------------------

    /// <summary>
    /// Five comments in this codebase asserted MUD2 never reports NPC stamina, and an estimator was
    /// built on that belief. The stethoscope's `diagnose` says otherwise, in a bracket.
    /// </summary>
    [Theory]
    [InlineData("The water-snake5 has a stamina lying between 90 and 99.", "water-snake5", 90, 99)]
    [InlineData("The giant snake has a stamina lying between 117 and 126.", "giant snake", 117, 126)]
    [InlineData("The viper has a stamina lying between 18 and 27.", "viper", 18, 27)]
    public void DiagnoseRead_IsCapturedWithItsBracket(string line, string npc, int low, int high)
    {
        var read = Assert.Single(Observe(line), e => e.Kind == CombatEventKind.NpcStaminaRead);

        Assert.Equal(npc, read.NpcName);
        Assert.Equal(low, read.RangeLow);
        Assert.Equal(high, read.RangeHigh);
    }

    /// <summary>Diagnosing something is not attacking it - reading a creature's stamina before
    /// deciding whether to fight it is the entire reason to carry a stethoscope.</summary>
    [Fact]
    public void DiagnoseRead_DoesNotStartAFight()
    {
        var tracker = new CombatTracker();
        tracker.Observe(Line("The viper has a stamina lying between 18 and 27."), T0);

        Assert.False(tracker.InCombat);
    }

    // ---- The weapon that flees out of your hands --------------------------------------------

    /// <summary>Fleeing drops your weapon automatically, in the same tick, with no WeaponBroke line to
    /// explain it. The panel went on reporting it as equipped.</summary>
    [Fact]
    public void DroppingTheWeaponInUse_DisarmsTheReadout()
    {
        var aggregator = new CombatStatsAggregator();
        var tracker = new CombatTracker();
        tracker.EventOccurred += aggregator.Observe;

        tracker.Observe(Line("You attack the ram, using the axe0 as a weapon."), T0);
        Assert.Equal("axe0", aggregator.Snapshot(T0).CurrentWeapon);

        tracker.Observe(Line("Axe0 dropped."), T0.AddSeconds(1));

        Assert.Null(aggregator.Snapshot(T0.AddSeconds(1)).CurrentWeapon);
    }

    /// <summary>
    /// The fight must remember what it was fought WITH even after the weapon leaves your hands.
    ///
    /// <para>MUD2 auto-drops your weapon when you flee and prints the drop in the same tick, just
    /// before the flee line - so an 83-second axe fight in the capture was recorded as having been
    /// fought bare-handed. That poisons the weapon-vs-creature history the alternate-weapon offer is
    /// ranked from: the axe gets no credit for its own fight and the unarmed bucket gains one it never
    /// had.</para>
    /// </summary>
    [Fact]
    public void DroppingTheWeapon_DoesNotErodeTheFightsOwnWeaponRecord()
    {
        var aggregator = new CombatStatsAggregator();
        var tracker = new CombatTracker();
        tracker.EventOccurred += aggregator.Observe;

        tracker.Observe(Line("You attack the ram, using the axe0 as a weapon."), T0);
        tracker.Observe(Line("You hit the ram (15-19)."), T0.AddSeconds(2));
        // The real ordering from the capture: the drop lands before the flee, while the fight is still
        // unresolved.
        tracker.Observe(Line("Axe0 dropped."), T0.AddSeconds(4));
        tracker.Observe(Line("You have fled by going west."), T0.AddSeconds(4));

        var snapshot = aggregator.Snapshot(T0.AddSeconds(5));
        var fight = Assert.Single(snapshot.Fights);

        Assert.Equal("axe0", fight.Weapon);
        Assert.Equal(FightOutcome.UFled, fight.Outcome);
        // The live hands are empty; the fight's record is not.
        Assert.Null(snapshot.CurrentWeapon);
    }

    /// <summary>Same rule for a weapon that breaks mid-fight - the pre-existing half of the same
    /// bug, which had already written two fights to the research corpus as unarmed.</summary>
    [Fact]
    public void BreakingTheWeapon_DoesNotErodeTheFightsOwnWeaponRecord()
    {
        var aggregator = new CombatStatsAggregator();
        var tracker = new CombatTracker();
        tracker.EventOccurred += aggregator.Observe;

        tracker.Observe(Line("You attack the rat3, using the dagger0 as a weapon."), T0);
        tracker.Observe(Line("The dagger0 breaks to bits."), T0.AddSeconds(2));
        tracker.Observe(Line("You have killed the rat3."), T0.AddSeconds(4));

        var fight = Assert.Single(aggregator.Snapshot(T0.AddSeconds(5)).Fights);

        Assert.Equal("dagger0", fight.Weapon);
        Assert.Equal(FightOutcome.Kill, fight.Outcome);
    }

    // ---- A weapon equipped just before the client notices the fight ------------------------

    /// <summary>
    /// The reported "it showed me unarmed despite attacking with a weapon" bug.
    ///
    /// <para>When you type <c>k zombie wi axe</c> against something ALREADY engaging you, MUD2's only
    /// output is the equip line - there is no "You attack the zombie" to carry the weapon. That line
    /// names no NPC so it cannot open an encounter, and if the encounter then opens on a swing line
    /// (which carries no weapon either) the fight is weaponless for its whole duration.</para>
    /// </summary>
    /// <summary>
    /// Wires an aggregator to a tracker the way SidePanelViewModel does in production: the encounter
    /// is opened by the tracker's InCombatChanged, NOT by the aggregator seeing a FightStart. That
    /// distinction is the whole point of these tests - a fight opened by a swing line never produces a
    /// FightStart at all, which is exactly how the weapon went missing.
    /// </summary>
    private static (CombatTracker Tracker, CombatStatsAggregator Aggregator, Action<string, DateTime> Feed) Wire()
    {
        var tracker = new CombatTracker();
        var aggregator = new CombatStatsAggregator();
        var at = T0;
        tracker.InCombatChanged += inCombat =>
        {
            if (inCombat)
                aggregator.BeginEncounter(at);
        };
        tracker.EventOccurred += aggregator.Observe;
        return (tracker, aggregator, (text, when) => { at = when; tracker.Observe(Line(text), when); });
    }

    [Fact]
    public void WeaponEquippedBeforeTheEncounterOpens_IsAdoptedByTheFight()
    {
        var (_, aggregator, feed) = Wire();

        // No attack line: the equip is all the game says, and the fight opens on a swing.
        feed("You are now using the axe0 to fight!", T0);
        feed("The zombie6 misses you.", T0.AddSeconds(1));

        Assert.Equal("axe0", aggregator.Snapshot(T0.AddSeconds(2)).CurrentWeapon);
    }

    /// <summary>The window is short on purpose: MUD2's weapon is per-fight, dropped at fight end, and
    /// <c>wield</c> is refused outside a fight - so an old equip says nothing about the fight starting
    /// now, and adopting it would invent an armed fight from a stale line.</summary>
    [Fact]
    public void AStaleWeaponEquip_IsNotAdoptedByALaterFight()
    {
        var (_, aggregator, feed) = Wire();

        feed("You are now using the axe0 to fight!", T0);
        feed("The zombie6 misses you.", T0.AddMinutes(3));

        Assert.Null(aggregator.Snapshot(T0.AddMinutes(3)).CurrentWeapon);
    }

    /// <summary>An ordinary armed opening must be unaffected - the fight's own attack line still wins,
    /// and a bare-handed opening still reads as bare-handed.</summary>
    [Fact]
    public void AnOrdinaryOpening_StillReportsItsOwnWeapon()
    {
        var (_, armed, feedArmed) = Wire();
        feedArmed("You attack the zombie6, using the falchion as a weapon.", T0);
        Assert.Equal("falchion", armed.Snapshot(T0).CurrentWeapon);

        var (_, bare, feedBare) = Wire();
        feedBare("You attack the raven.", T0);
        Assert.Null(bare.Snapshot(T0).CurrentWeapon);
    }

    /// <summary>Dropping anything else is inventory management, not disarmament.</summary>
    [Fact]
    public void DroppingSomethingElse_LeavesTheWeaponAlone()
    {
        var aggregator = new CombatStatsAggregator();
        var tracker = new CombatTracker();
        tracker.EventOccurred += aggregator.Observe;

        tracker.Observe(Line("You attack the ram, using the axe0 as a weapon."), T0);
        tracker.Observe(Line("Stethoscope dropped."), T0.AddSeconds(1));

        Assert.Equal("axe0", aggregator.Snapshot(T0.AddSeconds(1)).CurrentWeapon);
    }

    // ---- The seven ends (owner, 2026-08-19) --------------------------------------------------

    /// <summary>
    /// Cases 4 and 5, the player's own flee, successful and failed. Both zero the fight count, so both
    /// close every open fight - the failed one despite the player never leaving the room.
    ///
    /// <para>Case 5 is quoted from session-rec.mud2.co.uk.20260819-000137, where the whole exchange
    /// arrived in ONE frame: <c>flee n / You cannot go north from here. / You have changed experience
    /// level from protector to novice. / (Persona saved on -102 = 98). / You have fled by trying to go
    /// north.</c> Nothing in this client matched that last line until 2026-08-19.</para>
    /// </summary>
    [Theory]
    [InlineData("You have fled by going out.", CombatEventKind.YouFled)]
    [InlineData("You have fled by trying to go north.", CombatEventKind.YouFleeFailed)]
    public void PlayerFlee_SucceededOrNot_EndsEveryFight(string fleeLine, CombatEventKind expected)
    {
        var tracker = new CombatTracker();
        var seen = new List<CombatEvent>();
        tracker.EventOccurred += seen.Add;

        tracker.Observe(Line("You attack the rat21, using the falchion as a weapon."), T0);
        tracker.Observe(Line("You attack the rat17, using the falchion as a weapon."), T0.AddSeconds(1));
        Assert.True(tracker.InCombat);

        tracker.Observe(Line(fleeLine), T0.AddSeconds(2));

        Assert.False(tracker.InCombat);
        Assert.Contains(seen, e => e.Kind == expected);
        // The two readings of the sentence must never be confused - one word apart, opposite meanings.
        var wrong = expected == CombatEventKind.YouFled
            ? CombatEventKind.YouFleeFailed
            : CombatEventKind.YouFled;
        Assert.DoesNotContain(seen, e => e.Kind == wrong);
    }

    /// <summary>A failed player flee reaches every fight in the record, each labelled UFledFail rather
    /// than UFled: the fights ended, but the player did not get away, and the escape statistics must not
    /// claim otherwise.</summary>
    [Fact]
    public void PlayerFleeFailed_ResolvesEveryFightAsUFledFail()
    {
        var aggregator = new CombatStatsAggregator();
        var tracker = new CombatTracker();
        tracker.EventOccurred += aggregator.Observe;

        tracker.Observe(Line("You attack the rat21, using the falchion as a weapon."), T0);
        tracker.Observe(Line("You attack the rat17, using the falchion as a weapon."), T0.AddSeconds(1));
        tracker.Observe(Line("You have fled by trying to go north."), T0.AddSeconds(2));

        var snapshot = aggregator.Snapshot(T0.AddSeconds(5));
        Assert.Equal(2, snapshot.Fights.Count);
        Assert.All(snapshot.Fights, f => Assert.Equal(FightOutcome.UFledFail, f.Outcome));
    }

    /// <summary>Case 6. A withdraw is an agreement with ONE creature, so it closes that fight only -
    /// the rest of a pack is still swinging. Verbatim from session-rec.mud2.co.uk.20260819-001118,
    /// including the offer that precedes it by two minutes and must not itself end anything.</summary>
    [Fact]
    public void Withdraw_ClosesOnlyTheNamedFight()
    {
        var tracker = new CombatTracker();
        var seen = new List<CombatEvent>();
        tracker.EventOccurred += seen.Add;

        tracker.Observe(Line("You attack the banshee, using the unlit brand as a weapon."), T0);
        tracker.Observe(Line("You attack the rat21, using the unlit brand as a weapon."), T0.AddSeconds(1));

        tracker.Observe(Line("You offer to withdraw from your fight with the banshee."), T0.AddSeconds(2));
        Assert.True(tracker.InCombat);   // an offer, not an end
        Assert.Contains(seen, e => e.Kind == CombatEventKind.WithdrawOffer);

        tracker.Observe(Line("The banshee withdraws from your fight, and so do you."), T0.AddSeconds(3));
        Assert.True(tracker.InCombat);   // rat21 never agreed to anything

        tracker.Observe(Line("You have killed the rat21."), T0.AddSeconds(4));
        Assert.False(tracker.InCombat);
    }

    /// <summary>Case 7, the death frame, verbatim from session-rec.mud2.co.uk.20260819-001608. Three of
    /// its five lines were unparsed before 2026-08-19: the fatal blow (no stamina parenthetical), the
    /// narrative precursor, and - not asserted here, it is not a combat line - "Not updating
    /// persona."</summary>
    [Fact]
    public void DeathFrame_CountsTheFatalBlow_AndEndsEverything()
    {
        var tracker = new CombatTracker();
        var seen = new List<CombatEvent>();
        tracker.EventOccurred += seen.Add;

        tracker.Observe(Line("You attack the rat18, using the unlit brand as a weapon."), T0);
        tracker.Observe(Line("The rat18 hits you."), T0.AddSeconds(1));
        tracker.Observe(Line("You feel your life concluding..."), T0.AddSeconds(1));
        tracker.Observe(Line("The rat18 has killed you."), T0.AddSeconds(1));

        Assert.False(tracker.InCombat);

        // The fatal blow is a real landed hit and must be counted as one, with NO stamina reading
        // invented for it - there is no surviving stamina to report, and a fabricated 0 would look
        // like a measurement.
        var fatal = Assert.Single(seen, e => e.Kind == CombatEventKind.HitByNpc);
        Assert.Equal("rat18", fatal.NpcName);
        Assert.Null(fatal.RangeLow);
        Assert.Null(fatal.RangeHigh);

        Assert.Contains(seen, e => e.Kind == CombatEventKind.LifeConcluding);
        Assert.Contains(seen, e => e.Kind == CombatEventKind.KilledByNpc);
    }

    /// <summary>The ordinary hit-by-NPC line still carries its stamina reading - widening the pattern to
    /// accept the bare form must not have cost the parenthetical one its numbers.</summary>
    [Fact]
    public void NpcHit_WithStamina_StillReportsIt()
    {
        var events = Observe(
            "You attack the water-snake3, using the falchion as a weapon.",
            "The water-snake3 hits you (82/115).");

        var hit = Assert.Single(events, e => e.Kind == CombatEventKind.HitByNpc);
        Assert.Equal(82, hit.RangeLow);
        Assert.Equal(115, hit.RangeHigh);
    }

    // ---- identify on: exact damage instead of a bracket ---------------------------------------

    /// <summary>
    /// "You hit the banshee (6)." - verbatim from session-rec.mud2.co.uk.20260819-001118, the exact
    /// figure `identify` reports in place of a range. Reported as a zero-width range so consumers that
    /// average the pair need no special case.
    ///
    /// <para>This mattered more than its size suggests: turning identify ON - which exists to give the
    /// client better information - used to stop every one of the player's own hits being counted.</para>
    /// </summary>
    [Fact]
    public void ExactDamage_IsReportedAsAZeroWidthRange()
    {
        var events = Observe(
            "You attack the banshee, using the unlit brand as a weapon.",
            "You hit the banshee (6).");

        var hit = Assert.Single(events, e => e.Kind == CombatEventKind.Hit);
        Assert.Equal("banshee", hit.NpcName);
        Assert.Equal(6, hit.RangeLow);
        Assert.Equal(6, hit.RangeHigh);
    }

    /// <summary>The bracketed form is unaffected.</summary>
    [Fact]
    public void RangedDamage_StillParsesAsARange()
    {
        var events = Observe(
            "You attack the rat, using the unlit brand as a weapon.",
            "You hit the rat (5-9).");

        var hit = Assert.Single(events, e => e.Kind == CombatEventKind.Hit);
        Assert.Equal(5, hit.RangeLow);
        Assert.Equal(9, hit.RangeHigh);
    }

    // ---- weapons are not slots ---------------------------------------------------------------

    /// <summary>
    /// "You're using the unlit brand anyway..." - MUD2's reply to a redundant weapon selection, and in
    /// that frame the ONLY line naming the weapon actually in hand.
    ///
    /// <para>Verbatim from session-rec.mud2.co.uk.20260819-001608, where the owner sent
    /// <c>k rat with stick</c> and got this four times over (once per rat) paired with two guard drops.
    /// Note the weapon named is NOT the one the command asked for: taking the command at its word would
    /// have recorded the fight under "stick".</para>
    /// </summary>
    [Fact]
    public void RedundantWeaponSelection_ReportsTheWeaponActuallyInUse()
    {
        var events = Observe(
            "You attack the rat, using the unlit brand as a weapon.",
            "You're using the unlit brand anyway...",
            "Your guard drops momentarily in your confusion.");

        var equip = Assert.Single(events, e => e.Kind == CombatEventKind.WeaponEquip);
        Assert.Equal("unlit brand", equip.Weapon);
        // The guard drop is its own line and is already classified - nothing is inferred from the
        // weapon line about it.
        Assert.Contains(events, e => e.Kind == CombatEventKind.DroppedGuard);
    }

    /// <summary>A weapon does not survive into the next encounter. MUD2 has no equipment slots: the
    /// selection applies for the duration of ONE encounter, and a fresh encounter starts with the weapon
    /// unknown until a line says otherwise.</summary>
    [Fact]
    public void Weapon_DoesNotCarryIntoTheNextEncounter()
    {
        var aggregator = new CombatStatsAggregator();
        var tracker = new CombatTracker();
        tracker.InCombatChanged += inCombat =>
        {
            if (inCombat)
                aggregator.BeginEncounter(T0);
            else
                aggregator.EndEncounter();
        };
        tracker.EventOccurred += aggregator.Observe;

        tracker.Observe(Line("You attack the rat21, using the falchion as a weapon."), T0);
        tracker.Observe(Line("You have killed the rat21."), T0.AddSeconds(2));
        Assert.False(tracker.InCombat);

        // A brand-new encounter, opened by the creature rather than the player, so no line names a
        // weapon at all. It must not inherit the falchion.
        tracker.Observe(Line("The rat17 is staring at you aggressively."), T0.AddSeconds(30));
        var fight = Assert.Single(aggregator.Fights);
        Assert.Equal("rat17", fight.NpcName);
        Assert.Null(fight.WeaponUsed);
    }

}
