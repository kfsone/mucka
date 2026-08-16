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

    // ---- A failed flee is not a flee -------------------------------------------------------

    /// <summary>
    /// The highest-value gap in the session review. A water-snake attempted this 7 times in 13 seconds
    /// and never left the room, yet the game prints "You can fight it no longer." after each one - so
    /// one continuous fight was being recorded as eight separate encounters.
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

    /// <summary>The creature is still in the room, so it must still be an opponent - and the trailing
    /// "You can fight it no longer." must not quietly close it either.</summary>
    [Fact]
    public void FailedFlee_LeavesTheFightOpen()
    {
        var tracker = new CombatTracker();
        tracker.Observe(Line("You attack the water-snake5, using the falchion as a weapon."), T0);
        tracker.Observe(Line("The water-snake5 has fled by trying to go swampward."), T0.AddSeconds(2));
        tracker.Observe(Line("You can fight it no longer."), T0.AddSeconds(2));

        Assert.True(tracker.InCombat);
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

    [Fact]
    public void FailedFlees_AreCountedOnTheFightWithoutResolvingIt()
    {
        var aggregator = new CombatStatsAggregator();
        var tracker = new CombatTracker();
        tracker.EventOccurred += aggregator.Observe;

        string[] lines =
        [
            "You attack the water-snake5, using the falchion as a weapon.",
            "The water-snake5 has fled by trying to go over.",
            "The water-snake5 has fled by trying to go up.",
            "The water-snake5 has fled by trying to go south.",
        ];
        for (var i = 0; i < lines.Length; i++)
            tracker.Observe(Line(lines[i]), T0.AddSeconds(i));

        var snapshot = aggregator.Snapshot(T0.AddSeconds(10));
        var fight = Assert.Single(snapshot.Fights);
        Assert.False(fight.IsResolved);
        Assert.Equal(FightOutcome.Unresolved, fight.Outcome);
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
        Assert.Equal(FightOutcome.YouFled, fight.Outcome);
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
        Assert.Equal(FightOutcome.Killed, fight.Outcome);
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
}
