using MudSharp.Combat;
using MudSharp.Models;
using Mucka.Combat;

namespace Mucka.Util.Tests;

/// <summary>
/// Regressions for combat lines the parser did not recognise, found by reducing two real play
/// sessions. Every string here is quoted verbatim from a capture - none is invented, and none
/// should be "tidied up" to read better.
/// </summary>
public sealed class ParserGapTests
{
    private static readonly DateTime T0 = new(2026, 8, 10, 15, 0, 0, DateTimeKind.Utc);

    private static StyledLine Line(string text) => new([new StyledSpan(text, TextStyle.Default)]);

    /// <summary>The frame prompt as the parser delivers it - a PARTIAL line, and in game mode the
    /// only one. It is how CombatTracker sees a frame boundary at all.</summary>
    private static StyledLine PromptLine() =>
        new([new StyledSpan("*", TextStyle.Default)], isPartial: true);

    /// <summary>A line the server tagged C08.10/11/12 - "a fight ended", whatever the sentence
    /// says. See LineKind.FightEnd.</summary>
    private static StyledLine FightEndLine(string text) =>
        new([new StyledSpan(text, TextStyle.Default)], kind: LineKind.FightEnd);

    /// <summary>A line the server tagged C04.00.05 - "Normal creatures becoming invisible". On the
    /// wire: <c>[9F][9B][A0]</c> in front of "The man fades from view."</summary>
    private static StyledLine InvisibleLine(string text) =>
        new([new StyledSpan(text, TextStyle.Default)], kind: LineKind.CreatureInvisible);

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
    /// and never left the room, and the game prints "You can fight it no longer." after each one. A
    /// failed flee really does end the fight - see
    /// <see cref="FailedFlee_EndsTheFight_CreatureStaysInTheRoom"/>.
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
    /// player has to attack again to re-engage. Nothing else in this frame can close a fight, so
    /// without this a player who walked away instead of re-attacking would leave the panel claiming
    /// combat until reset or logout.
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

    /// <summary>Re-attacking the creature that is still standing there opens a NEW encounter: a new
    /// frame, a new command and a new weapon selection.</summary>
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
    }

    // ---- Health readings folded into a longer sentence --------------------------------------

    /// <summary>The reading is real; a sentence that continues past it must not cause it to be
    /// dropped. Captured verbatim from a ram.</summary>
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
    /// MUD2 does report NPC stamina: the stethoscope's `diagnose` command returns it in a bracket.
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
    /// explain it - so the readout must not go on reporting it as equipped.</summary>
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

    /// <summary>Same rule for a weapon that breaks mid-fight.</summary>
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

    // ---- The ends of a fight -----------------------------------------------------------------

    /// <summary>
    /// Cases 4 and 5, the player's own flee, successful and failed. Both zero the fight count, so both
    /// close every open fight - the failed one despite the player never leaving the room.
    ///
    /// <para>Case 5 is quoted from session-rec.mud2.co.uk.20260819-000137, where the whole exchange
    /// arrived in ONE frame: <c>flee n / You cannot go north from here. / You have changed experience
    /// level from protector to novice. / (Persona saved on -102 = 98). / You have fled by trying to go
    /// north.</c></para>
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

    /// <summary>The third member of the withdraw family: the creature's own offer. Verbatim from
    /// session-rec.mud2.co.uk.20260902-232101 records 994-998
    /// (the "critically damaged" line is quoted with it because in all five captured occurrences the
    /// offer follows one - the creature offers when it is nearly dead).
    ///
    /// <para>Asserted at the AGGREGATOR, not just the tracker: the offer must leave the fight
    /// Unresolved and the encounter open, so that the kill four seconds later is what resolves it.
    /// A kind that quietly resolved a fight would be invisible in the tracker's InCombat flag alone
    /// whenever some other creature kept the encounter up.</para></summary>
    [Fact]
    public void NpcWithdrawOffer_LeavesTheFightUnresolved()
    {
        var aggregator = new CombatStatsAggregator();
        var tracker = new CombatTracker();
        tracker.EventOccurred += aggregator.Observe;
        var seen = new List<CombatEvent>();
        tracker.EventOccurred += seen.Add;

        tracker.Observe(Line("You attack the zombie1, using the halberd as a weapon."), T0);
        tracker.Observe(Line("You hit the zombie1 (1-4)."), T0.AddSeconds(1));
        tracker.Observe(Line("The zombie1 looks critically damaged."), T0.AddSeconds(2));
        tracker.Observe(Line("The zombie1 offers to withdraw from your fight if you do likewise."), T0.AddSeconds(3));

        Assert.Contains(seen, e => e.Kind == CombatEventKind.NpcWithdrawOffer && e.NpcName == "zombie1");

        var mid = aggregator.Snapshot(T0.AddSeconds(3));
        Assert.True(mid.InCombat);
        Assert.Equal(FightOutcome.Unresolved, Assert.Single(mid.Fights).Outcome);

        tracker.Observe(Line("You have killed the zombie1."), T0.AddSeconds(4));
        var after = aggregator.Snapshot(T0.AddSeconds(5));
        Assert.Equal(FightOutcome.Kill, Assert.Single(after.Fights).Outcome);
    }

    /// <summary>Case 7, the death frame, verbatim from session-rec.mud2.co.uk.20260819-001608: the
    /// fatal blow (no stamina parenthetical), the narrative precursor, and - not asserted here, it is
    /// not a combat line - "Not updating persona."</summary>
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

    /// <summary>The death precursor is a generated pairing, not a fixed string. The client matched
    /// only "You feel your life concluding..." until two more members turned up in the wire log -
    /// one per persona lost, which is a bad rate at which to discover a vocabulary. All three carry
    /// C1 08.09; the shape is You feel your {life|vitality|very soul} {concluding|stopping|
    /// terminating}..., so three of at least nine combinations are known and the rest will arrive
    /// the same expensive way unless the pattern covers them in advance.</summary>
    [Theory]
    [InlineData("You feel your life concluding...")]      // the one it always matched
    [InlineData("You feel your vitality stopping...")]    // Awlie, 2026-09-05
    [InlineData("You feel your very soul terminating...")]// Bludgeon, 2026-09-05
    [InlineData("You feel your life snatched away...")]
    [InlineData("You feel your life seizing up...")]
    [InlineData("You feel your very soul ceasing...")]
    public void TheDeathPrecursor_IsMatchedAcrossItsWholeFamily(string line)
    {
        var tracker = new CombatTracker();
        var seen = new List<CombatEvent>();
        tracker.EventOccurred += seen.Add;

        tracker.Observe(Line("You attack the rat18, using the unlit brand as a weapon."), T0);
        tracker.Observe(Line(line), T0.AddSeconds(1));

        Assert.Contains(seen, e => e.Kind == CombatEventKind.LifeConcluding);
    }

    /// <summary>...and the widening must not swallow ordinary prose. These share the opening words
    /// and are not deaths. CONSTRUCTED, not observed - they guard the shape rather than record a
    /// sighting, which is why the subject is pinned to the three seen values and only the verb
    /// phrase is loose.</summary>
    [Theory]
    [InlineData("You feel your way along the wall...")]
    [InlineData("You feel your pockets are empty.")]
    [InlineData("You feel your strength returning.")]
    public void TheWiderPrecursorPattern_DoesNotSwallowOrdinaryProse(string line)
    {
        var tracker = new CombatTracker();
        var seen = new List<CombatEvent>();
        tracker.EventOccurred += seen.Add;

        tracker.Observe(Line("You attack the rat18, using the unlit brand as a weapon."), T0);
        tracker.Observe(Line(line), T0.AddSeconds(1));

        Assert.DoesNotContain(seen, e => e.Kind == CombatEventKind.LifeConcluding);
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

    // ---- exact damage instead of a bracket ----------------------------------------------------

    /// <summary>
    /// "You hit the banshee (6)." - verbatim from session-rec.mud2.co.uk.20260819-001118: the exact
    /// figure MUD2 sometimes prints in place of a range. Reported as a zero-width range so consumers
    /// that average the pair need no special case.
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
    /// <para>Verbatim from session-rec.mud2.co.uk.20260819-001608, where <c>k rat with stick</c> was
    /// sent and got this four times over (once per rat) paired with two guard drops. Note the weapon
    /// named is NOT the one the command asked for: taking the command at its word would have recorded
    /// the fight under "stick".</para>
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

    // ---- an opponent with no name --------------------------------------------------------------
    //
    // MUD2 writes "someone" wherever it would have written a creature's name, for as long as the
    // player cannot identify it. Two causes, both on the wire in one session: the CREATURE turned
    // invisible (C1 04.00.05, "The man fades from view.") and only it goes anonymous, or the PLAYER
    // was blinded and everything does. Nothing else about the sentence changes, and nothing about
    // its C1 code changes either.
    //
    // Every line below is verbatim - from a live fight's scrollback, or from the bytes of
    // session 12 in wire.db, which recorded the same man in the same condition on the same day.

    /// <summary>
    /// The whole fight, as it was played: a man turns invisible mid-combat and every line about him
    /// afterwards says "someone". It ends with the player accepting his withdraw offer.
    ///
    /// <para>The client that met this stayed in combat. None of the anonymous lines matched, so the
    /// swings never reached the panel and - the part that mattered - neither did the end, leaving a
    /// fight open against a creature that had agreed to stop fighting.</para>
    ///
    /// <para>The name is recovered rather than lost: the fade line says which creature went
    /// anonymous, so every later line is attributed to the man, and the encounter that closes is
    /// his.</para>
    /// </summary>
    [Fact]
    public void InvisibleOpponent_IsStillTheCreatureItWas_AndItsWithdrawStillEndsTheFight()
    {
        var tracker = new CombatTracker();
        var seen = new List<CombatEvent>();
        tracker.EventOccurred += seen.Add;
        var at = 0;
        void Observe(StyledLine line) => tracker.Observe(line, T0.AddSeconds(at++));

        Observe(Line("The man is moving towards you ferociously."));
        Observe(Line("The man misses you."));
        Observe(Line("You hit the man (1-4)."));
        Observe(PromptLine());

        Observe(Line("The man makes some magical gestures."));
        Observe(InvisibleLine("The man fades from view."));
        Observe(Line("Someone has started to use something to fight!"));
        Observe(PromptLine());

        // From here MUD2 has stopped naming him. The fight is unchanged.
        Assert.True(tracker.InCombat);
        Observe(Line("Someone hits you (103/105)."));
        Observe(Line("You miss someone."));
        Observe(Line("You hit someone (1-4)."));
        Observe(Line("Someone misses you."));
        Observe(PromptLine());

        Observe(Line("Someone offers to withdraw from your fight if you do likewise."));
        Assert.True(tracker.InCombat);   // an offer is not an end, anonymous or not
        Observe(PromptLine());

        Observe(Line("You withdraw from your fight with someone, and that person does too."));
        Assert.False(tracker.InCombat);

        // Every anonymous line lands on the man, because the fade line said which creature he was.
        Assert.All(seen.Where(e => e.NpcName is not null), e => Assert.Equal("man", e.NpcName));
        Assert.Contains(seen, e => e.Kind == CombatEventKind.NpcTurnedInvisible);
        var hurt = Assert.Single(seen, e => e.Kind == CombatEventKind.HitByNpc);
        Assert.Equal(103, hurt.RangeLow);
        Assert.Equal(105, hurt.RangeHigh);
        var ended = Assert.Single(seen, e => e.Kind == CombatEventKind.Withdrawn);
        Assert.Equal("You withdraw from your fight with someone, and that person does too.", ended.RawText);
    }

    /// <summary>
    /// The end itself, in isolation: the withdraw written from the PLAYER's side, which is what MUD2
    /// says when the player is the one who accepted. The creature-side wording ("The banshee
    /// withdraws from your fight, and so do you.") was matched from the start and this one was not,
    /// which is the whole of the bug - the same event, the same code, a different subject.
    /// </summary>
    [Fact]
    public void PlayerSideWithdraw_EndsTheFight()
    {
        var tracker = new CombatTracker();
        tracker.Observe(Line("You attack the thief, using the broadsword as a weapon."), T0);
        tracker.Observe(Line("You withdraw from your fight with the thief, and he does too."), T0.AddSeconds(1));

        Assert.False(tracker.InCombat);
    }

    /// <summary>
    /// Turning invisible is not an end, and it is not a start. The fight carries on exactly as it
    /// was - which is why the client could not simply treat the anonymous lines that follow as some
    /// other creature's.
    ///
    /// <para>Reported only for a creature already engaged. The capture that produced this line has
    /// the man fading while fighting a FOX, well before the player attacked him, and a fight the
    /// player is not in must not open one.</para>
    /// </summary>
    [Fact]
    public void FadingFromView_EndsNothing_AndOpensNothing()
    {
        var idle = new CombatTracker();
        var idleSeen = new List<CombatEvent>();
        idle.EventOccurred += idleSeen.Add;
        idle.Observe(InvisibleLine("The man fades from view."), T0);
        Assert.False(idle.InCombat);
        Assert.Empty(idleSeen);

        var tracker = new CombatTracker();
        var seen = new List<CombatEvent>();
        tracker.EventOccurred += seen.Add;
        tracker.Observe(Line("You attack the man, using the broadsword as a weapon."), T0);
        tracker.Observe(InvisibleLine("The man fades from view."), T0.AddSeconds(1));

        Assert.True(tracker.InCombat);
        var vanished = Assert.Single(seen, e => e.Kind == CombatEventKind.NpcTurnedInvisible);
        Assert.Equal("man", vanished.NpcName);
    }

    /// <summary>
    /// The prose is not what detects it. C1 04.00.05 is ("Normal creatures becoming invisible",
    /// fecodes.txt), so a wording nobody has seen still costs only the name - and with one creature
    /// engaged, not even that.
    /// </summary>
    [Fact]
    public void CodedInvisibility_WithAnUnknownWording_StillAnonymisesTheSoleOpponent()
    {
        var tracker = new CombatTracker();
        var seen = new List<CombatEvent>();
        tracker.EventOccurred += seen.Add;

        tracker.Observe(Line("You attack the man, using the broadsword as a weapon."), T0);
        tracker.Observe(InvisibleLine("The man melts into the shadows."), T0.AddSeconds(1));
        tracker.Observe(Line("Someone hits you (103/105)."), T0.AddSeconds(2));

        Assert.Equal("man", Assert.Single(seen, e => e.Kind == CombatEventKind.HitByNpc).NpcName);
    }

    /// <summary>
    /// Getting the name back. "The man has regained his visibleness!" is verbatim, and after it MUD2
    /// names him again - so he stops being what a later "someone" means, and a second unseen
    /// attacker is not silently filed under him.
    /// </summary>
    [Fact]
    public void RegainingVisibility_StopsBeingWhatSomeoneMeans()
    {
        var tracker = new CombatTracker();
        var seen = new List<CombatEvent>();
        tracker.EventOccurred += seen.Add;

        tracker.Observe(Line("You attack the man, using the broadsword as a weapon."), T0);
        tracker.Observe(Line("The rat0 is glaring at you madly."), T0.AddSeconds(1));
        tracker.Observe(InvisibleLine("The man fades from view."), T0.AddSeconds(2));

        // Two engaged, one of them invisible: "someone" can only be the invisible one.
        tracker.Observe(Line("Someone hits you (103/105)."), T0.AddSeconds(3));
        Assert.Equal("man", seen.Last(e => e.Kind == CombatEventKind.HitByNpc).NpcName);

        tracker.Observe(Line("The man has regained his visibleness!"), T0.AddSeconds(4));
        tracker.Observe(Line("Someone hits you (100/105)."), T0.AddSeconds(5));

        // Nothing is invisible now and two creatures are engaged, so the line says nothing about
        // which - and nothing is invented.
        Assert.Equal("someone", seen.Last(e => e.Kind == CombatEventKind.HitByNpc).NpcName);
    }

    /// <summary>
    /// Two invisible opponents: an anonymous line names neither, and nothing is guessed. The costs
    /// of abstaining are deliberately different at the two ends of a fight - a swing opens a roster
    /// entry called "someone", which is true and worth drawing, while an END closes a fight nobody
    /// is having and leaves the encounter to the backstops, which is the only safe direction in a
    /// pack.
    /// </summary>
    [Fact]
    public void TwoInvisibleOpponents_AreNotToldApart_AndNeitherIsClosedByAnAnonymousEnd()
    {
        var tracker = new CombatTracker();
        var seen = new List<CombatEvent>();
        tracker.EventOccurred += seen.Add;

        tracker.Observe(Line("You attack the man, using the broadsword as a weapon."), T0);
        tracker.Observe(Line("The thief is glaring at you madly."), T0.AddSeconds(1));
        tracker.Observe(InvisibleLine("The man fades from view."), T0.AddSeconds(2));
        tracker.Observe(InvisibleLine("The thief fades from view."), T0.AddSeconds(3));

        tracker.Observe(Line("Someone hits you (103/105)."), T0.AddSeconds(4));
        Assert.Equal("someone", Assert.Single(seen, e => e.Kind == CombatEventKind.HitByNpc).NpcName);

        // An end that cannot say who closes nobody: the man and the thief are both still swinging.
        tracker.Observe(Line("You withdraw from your fight with someone, and that person does too."), T0.AddSeconds(5));
        Assert.True(tracker.InCombat);
    }

    /// <summary>
    /// The anonymous opponent's WEAPON is anonymous too, and unlike the creature it cannot be
    /// recovered - no line in the frame names it. So the equip is reported with no weapon rather
    /// than with the word "something", which would file a real NPC weapon statistic against an
    /// object that does not exist.
    /// </summary>
    [Fact]
    public void AnonymousWeaponEquip_ReportsNoWeaponRatherThanSomething()
    {
        var events = Observe(
            "You attack the man, using the broadsword as a weapon.",
            "Someone has started to use something to fight!");

        var equip = Assert.Single(events, e => e.Kind == CombatEventKind.NpcWeaponEquip);
        Assert.Null(equip.Weapon);
    }

    /// <summary>
    /// "You attack someone." - verbatim from the wire, answering <c>k man</c> against a man who had
    /// already turned invisible, and carrying the ordinary fight-start code <c>[A3][9B]</c>.
    ///
    /// <para>It is the ONLY line that opens that encounter, so unmatched the entire fight happens
    /// with the client believing there is no fight at all - no panel, no clog, and nothing for any
    /// end to close. The creature faded before it was engaged, so there is no name to recover and
    /// the roster says "someone", which is the truth.</para>
    /// </summary>
    [Fact]
    public void AttackingSomethingYouCannotSee_StillOpensTheEncounter()
    {
        var events = Observe("You attack someone.");

        var start = Assert.Single(events, e => e.Kind == CombatEventKind.FightStart);
        Assert.Equal("someone", start.NpcName);
    }

    /// <summary>
    /// Combat between two OTHER creatures, overheard. Verbatim from the wire, where the man fought a
    /// fox in front of the player - including after he turned invisible, so these sentences carry
    /// "someone" too.
    ///
    /// <para>None of it is the player's fight, and none of it may open one. The defence is that
    /// every pattern in CombatTracker is anchored at both ends; this pins it, because widening that
    /// family is exactly what this whole section did.</para>
    /// </summary>
    [Fact]
    public void OverheardCombat_BetweenTwoOtherCreatures_IsNotOurFight()
    {
        var events = Observe(
            "You hear a grinding noise, as the man hits the fox.",
            "You hear a swish, as the fox misses the man.",
            "You hear a swishing sound, as someone misses the fox.",
            "You hear a parried blow, as the fox misses someone.");

        Assert.Empty(events);
    }

    /// <summary>
    /// "The thief takes back his offer to withdraw from your fight." - verbatim from the wire, and
    /// the opposite of an end. It is matched by nothing and must stay that way: the offer it
    /// retracts was never an end either, so there is no state to undo.
    ///
    /// <para>Worth pinning rather than leaving to chance - it is the sentence a careless widening of
    /// the withdraw family would swallow, and swallowing it would close a fight that has just been
    /// declared to be continuing.</para>
    /// </summary>
    [Fact]
    public void TakingBackAWithdrawOffer_EndsNothing()
    {
        var tracker = new CombatTracker();
        var seen = new List<CombatEvent>();
        tracker.EventOccurred += seen.Add;

        tracker.Observe(Line("You attack the thief, using the broadsword as a weapon."), T0);
        tracker.Observe(Line("The thief offers to withdraw from your fight if you do likewise."), T0.AddSeconds(1));
        tracker.Observe(Line("The thief takes back his offer to withdraw from your fight."), T0.AddSeconds(2));

        Assert.True(tracker.InCombat);
        Assert.DoesNotContain(seen, e => e.Kind == CombatEventKind.Withdrawn);
    }

    // ---- and if the wording is wrong again, twice more ----------------------------------------

    /// <summary>
    /// The second way out, and the one the client that met this fight did not have: the server codes
    /// its fight ends, so an end nothing recognises still closes the fight it can only be about.
    ///
    /// <para>Asserted with a sentence deliberately unlike any real one. The point is not this
    /// wording, it is that the prose is not load-bearing here.</para>
    /// </summary>
    [Fact]
    public void CodedFightEnd_ClosesAnInvisibleFight_WhateverTheSentenceSays()
    {
        var tracker = new CombatTracker();
        var seen = new List<CombatEvent>();
        tracker.EventOccurred += seen.Add;

        tracker.Observe(Line("You attack the man, using the broadsword as a weapon."), T0);
        tracker.Observe(InvisibleLine("The man fades from view."), T0.AddSeconds(1));
        tracker.Observe(Line("Someone hits you (103/105)."), T0.AddSeconds(2));
        tracker.Observe(PromptLine(), T0.AddSeconds(3));
        tracker.Observe(FightEndLine("Someone stops bothering with you."), T0.AddSeconds(4));

        Assert.False(tracker.InCombat);
        // Still his fight: the code says one ended, the fade line says whose.
        Assert.Equal("man", Assert.Single(seen, e => e.Kind == CombatEventKind.FightEndOther).NpcName);
    }

    /// <summary>
    /// The third way out, and the floor under both the others: in MUD2 you cannot walk out of a
    /// fight. Movement is refused while fighting - "You can't just leave in the middle of a fight!
    /// You have to flee!", verbatim from the same session - and leaving costs a flee, which prints
    /// its own line. So standing somewhere else is proof the fight is over, whatever any sentence
    /// did or did not say, and it holds for an end nobody has thought of, including a wiz moving the
    /// player.
    ///
    /// <para>It announces itself, because every time it fires there is an unmatched line to go and
    /// find.</para>
    /// </summary>
    [Fact]
    public void RoomChange_ClosesAnInvisibleFight_NoMatterWhatEndedIt()
    {
        var tracker = new CombatTracker();
        var seen = new List<CombatEvent>();
        tracker.EventOccurred += seen.Add;

        tracker.Observe(Line("You attack the man, using the broadsword as a weapon."), T0);
        tracker.Observe(InvisibleLine("The man fades from view."), T0.AddSeconds(1));
        tracker.Observe(Line("Someone hits you (103/105)."), T0.AddSeconds(2));
        Assert.True(tracker.InCombat);

        tracker.NoteRoomChanged(T0.AddSeconds(3));

        Assert.False(tracker.InCombat);
        var forced = Assert.Single(seen, e => e.Kind == CombatEventKind.EncounterForceEnded);
        Assert.Equal("(forced end: room changed)", forced.RawText);
    }

    // -- the two anonymous words, and what decides who they mean ---------------------------------
    //
    // Every wording below is verbatim from the wire. "someone" is a person-shaped Creature the
    // player cannot see; "something" is an animal. The word is chosen by the Creature, not by the
    // cause - the cause is what ResolveAnonymous has to work out, and the only two are a fade code
    // (04.00.05, handled elsewhere in this file) and the player's own blindness.

    [Fact]
    public void BlindPlayer_SoleAnimalOpponent_SomethingIsThatAnimal()
    {
        var tracker = new CombatTracker();
        var seen = new List<CombatEvent>();
        tracker.EventOccurred += seen.Add;

        tracker.Observe(Line("You attack the water-snake1."), T0);
        tracker.NoteCannotSee(true);
        tracker.Observe(Line("Something hits you (116/120)."), T0.AddSeconds(2));
        tracker.Observe(Line("You miss something."), T0.AddSeconds(2));

        // Both lines used to match nothing at all: the anonymous alternatives accepted only
        // "someone", so a blind fight against an animal produced no swing events.
        var hit = Assert.Single(seen, e => e.Kind == CombatEventKind.HitByNpc);
        Assert.Equal("water-snake1", hit.NpcName);
        Assert.Equal(116, hit.RangeLow);
        var miss = Assert.Single(seen, e => e.Kind == CombatEventKind.Miss);
        Assert.Equal("water-snake1", miss.NpcName);
    }

    [Fact]
    public void SightedPlayer_SoleOpponent_SomeoneIsSomeoneElse()
    {
        var tracker = new CombatTracker();
        var seen = new List<CombatEvent>();
        tracker.EventOccurred += seen.Add;

        tracker.Observe(Line("You attack the zombie5."), T0);
        tracker.Observe(Line("The zombie5 hits you (106/120)."), T0.AddSeconds(2));
        tracker.Observe(Line("Someone hits you (103/120)."), T0.AddSeconds(4));

        // Not blind, nothing faded: the only honest reading of "Someone" is a participant the game
        // has not named. Handing the blow to the zombie because it was the only thing engaged is the
        // rule that credited an invisible player's whole attack to zombie5 in run 49.
        var hits = seen.Where(e => e.Kind == CombatEventKind.HitByNpc).ToList();
        Assert.Equal(2, hits.Count);
        Assert.Equal("zombie5", hits[0].NpcName);
        Assert.Equal("someone", hits[1].NpcName);
        Assert.True(tracker.InCombat);
    }

    [Fact]
    public void BlindPlayer_SeveralOpponents_SomethingStaysSomething()
    {
        var tracker = new CombatTracker();
        var seen = new List<CombatEvent>();
        tracker.EventOccurred += seen.Add;

        tracker.Observe(Line("You attack the rat0."), T0);
        tracker.Observe(Line("The rat1 is looking at you hatefully."), T0);
        tracker.NoteCannotSee(true);
        tracker.Observe(Line("Something hits you (67/120)."), T0.AddSeconds(2));

        // Two engaged and both unnamed: the line says nothing about which, and the word is the
        // truthful name. It is "something", the word the game chose - never collapsed to "someone".
        var hit = Assert.Single(seen, e => e.Kind == CombatEventKind.HitByNpc);
        Assert.Equal("something", hit.NpcName);
    }

    [Fact]
    public void YouHitSomething_IsABlow_NotAWeaponEquip()
    {
        var tracker = new CombatTracker();
        var seen = new List<CombatEvent>();
        tracker.EventOccurred += seen.Add;

        tracker.Observe(Line("You attack the fox."), T0);
        tracker.NoteCannotSee(true);
        tracker.Observe(Line("You hit something (5-9)."), T0.AddSeconds(2));
        // The one line in the corpus where "something" is a WEAPON, not a Creature. It must still
        // yield an unknown weapon, and the blow above must not have been mistaken for it.
        tracker.Observe(Line("Something has started to use something to fight!"), T0.AddSeconds(3));

        var hit = Assert.Single(seen, e => e.Kind == CombatEventKind.Hit);
        Assert.Equal("fox", hit.NpcName);
        Assert.Equal(5, hit.RangeLow);
        Assert.Equal(9, hit.RangeHigh);
        var equip = Assert.Single(seen, e => e.Kind == CombatEventKind.NpcWeaponEquip);
        Assert.Null(equip.Weapon);
    }

    /// <summary>
    /// The player's own `invis` spell on a Creature. Verbatim: "Your spell worked!" then "The
    /// zombie9 has become invisible!" - and that second line arrives under C1 11.00 (the caster's
    /// spell-result code), NOT the 04.00.05 Creature-fade code the tracker keys on, so it reaches
    /// the tracker as plain text with no kind. The wording alone has to mark the Creature faded;
    /// before it did, the next line was "You miss someone." with nothing faded, and the kill at the
    /// end could only land on the zombie by the old sole-active accident.
    /// </summary>
    [Fact]
    public void PlayerCastInvisibility_MarksTheCreatureFaded_ByWordingAlone()
    {
        var tracker = new CombatTracker();
        var seen = new List<CombatEvent>();
        tracker.EventOccurred += seen.Add;

        tracker.Observe(Line("You attack the zombie9."), T0);
        tracker.Observe(Line("Your spell worked!"), T0.AddSeconds(20));
        tracker.Observe(Line("The zombie9 has become invisible!"), T0.AddSeconds(20));   // no LineKind
        tracker.Observe(Line("You miss someone."), T0.AddSeconds(26));
        tracker.Observe(Line("Someone misses you."), T0.AddSeconds(26));
        tracker.Observe(Line("You hit someone (5-9)."), T0.AddSeconds(40));
        tracker.Observe(Line("You have killed someone."), T0.AddSeconds(54));

        var faded = Assert.Single(seen, e => e.Kind == CombatEventKind.NpcTurnedInvisible);
        Assert.Equal("zombie9", faded.NpcName);
        // Every anonymous line after it is the zombie's - by the fade, not by any sight gate.
        Assert.All(seen.Where(e => e.RawText is { } t && t.Contains("omeone")),
            e => Assert.Equal("zombie9", e.NpcName));
        var kill = Assert.Single(seen, e => e.Kind == CombatEventKind.Kill);
        Assert.Equal("zombie9", kill.NpcName);
        Assert.False(tracker.InCombat);
        Assert.DoesNotContain(seen, e => e.NpcName == AnonymousOpponent.Person);
    }

    /// <summary>
    /// The same fade path for a Creature MUD2 calls "something". The fade itself rides the
    /// 04.00.05 code (wording-agnostic by design); the swing lines are verbatim from blind fights
    /// against animals. Pins that nothing on this path is spelled for one word only - the faded
    /// Creature owns its anonymous lines whichever word the game chose for it.
    /// </summary>
    [Fact]
    public void FadedAnimal_OwnsItsSomethingLines_SameAsAPersonOwnsSomeone()
    {
        var tracker = new CombatTracker();
        var seen = new List<CombatEvent>();
        tracker.EventOccurred += seen.Add;

        tracker.Observe(Line("You attack the water-snake1."), T0);
        tracker.Observe(InvisibleLine("The water-snake1 fades from view."), T0.AddSeconds(2));
        tracker.Observe(Line("Something hits you (116/120)."), T0.AddSeconds(4));
        tracker.Observe(Line("You miss something."), T0.AddSeconds(4));
        tracker.Observe(Line("Something misses you."), T0.AddSeconds(6));

        Assert.Equal("water-snake1", Assert.Single(seen, e => e.Kind == CombatEventKind.NpcTurnedInvisible).NpcName);
        Assert.All(seen.Where(e => e.RawText is { } t && t.Contains("omething")),
            e => Assert.Equal("water-snake1", e.NpcName));
        Assert.DoesNotContain(seen, e => AnonymousOpponent.IsAnonymous(e.NpcName));
    }
}
