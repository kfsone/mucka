using MudSharp.Combat;
using MudSharp.Models;
using Mucka.Combat;

namespace Mucka.Util.Tests;

/// <summary>
/// The health descriptor's route from a line of game text to a drawable rung: tracker recognises it,
/// aggregator attributes it to the right NPC, roster ages it, renderer's staleness rules decide whether
/// it is still evidence.
/// </summary>
public sealed class NpcHealthTrackingTests
{
    private static readonly DateTime T0 = new(2026, 8, 10, 3, 0, 0, DateTimeKind.Utc);

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

    [Fact]
    public void Tracker_EmitsNpcHealthForAnEngagedOpponent()
    {
        var events = Observe(
            "You attack the zombie2, using the axe0 as a weapon.",
            "You hit the zombie2 (20-29).",
            "The zombie2 looks moderately damaged.");

        var health = Assert.Single(events, e => e.Kind == CombatEventKind.NpcHealth);
        Assert.Equal("zombie2", health.NpcName);
        Assert.Equal(4, health.HealthRung);
        Assert.Equal("moderately damaged", health.HealthPhrase);
    }

    /// <summary>The same sentence appears in room descriptions. A wounded creature the player has never
    /// touched must not open an encounter or appear on the panel - in a permadeath game a phantom
    /// opponent is worse than a missing one.</summary>
    [Fact]
    public void Tracker_IgnoresHealthLinesForCreaturesNotBeingFought()
    {
        var events = Observe("The rat7 looks critically injured.");

        Assert.Empty(events);
    }

    [Fact]
    public void Tracker_IgnoresHealthLinesForAnOpponentAlreadyDead()
    {
        var events = Observe(
            "You attack the rat7, using the axe0 as a weapon.",
            "You have killed the rat7.",
            "The rat7 looks critically injured.");

        Assert.DoesNotContain(events, e => e.Kind == CombatEventKind.NpcHealth);
    }

    [Fact]
    public void Aggregator_AttributesReadingsToTheRightNpcInAPackFight()
    {
        var aggregator = new CombatStatsAggregator();
        var tracker = new CombatTracker();
        tracker.EventOccurred += aggregator.Observe;

        var lines = new[]
        {
            "You attack the rat1, using the axe0 as a weapon.",
            "The rat2 attacks you.",
            "You hit the rat1 (5-9).",
            "The rat1 looks seriously injured.",
            "You hit the rat2 (1-4).",
            "The rat2 looks fit.",
        };
        for (var i = 0; i < lines.Length; i++)
            tracker.Observe(Line(lines[i]), T0.AddSeconds(i));

        var snapshot = aggregator.Snapshot(T0.AddSeconds(lines.Length));
        var rat1 = snapshot.Fights.Single(f => f.NpcName == "rat1");
        var rat2 = snapshot.Fights.Single(f => f.NpcName == "rat2");

        Assert.Equal(3, rat1.HealthRung);
        Assert.Equal("seriously injured", rat1.HealthPhrase);
        Assert.Equal(7, rat2.HealthRung);
        Assert.NotNull(rat1.HealthReadUtc);
    }

    /// <summary>Creatures regenerate: the corpus has a zombie oscillating between "strong" and
    /// "superficially damaged" four times inside one fight. The panel must report the LATEST reading -
    /// latching to the worst seen would keep promising a kill that is no longer one swing away.</summary>
    [Fact]
    public void Accumulator_ReportsTheLatestReadingNotTheWorst()
    {
        var fight = new FightAccumulator("zombie2", T0, weaponAtStart: null);

        fight.NoteHealth(6, "superficially damaged", T0);
        fight.NoteHealth(7, "strong", T0.AddSeconds(20));

        Assert.Equal(7, fight.HealthRung);
        Assert.Equal("strong", fight.HealthPhrase);
        Assert.Equal(T0.AddSeconds(20), fight.HealthReadUtc);
    }

    /// <summary>A probe is timestamped and anchored to the damage standing at the moment it landed, so
    /// the reading it produces both ages forward with the fight and knows how old it is. The two are
    /// separate answers - damage moves the number, time only dims it.</summary>
    [Fact]
    public void Accumulator_TimestampsADiagnoseProbeAndAnchorsItToTheDamageSoFar()
    {
        var fight = new FightAccumulator("water-snake5", T0, weaponAtStart: null);

        fight.AddYouHit(5, 9);
        fight.NoteStaminaRead(90, 99, T0.AddSeconds(4));
        fight.AddYouHit(10, 14);

        Assert.Equal(T0.AddSeconds(4), fight.StaminaReadUtc);

        // Only the blow AFTER the probe counts against it; the one before is already in the number.
        var read = fight.StaminaReading!.Value;
        Assert.Equal(10, read.DealtSince.Low, 6);
        Assert.Equal(14, read.DealtSince.High, 6);
        Assert.True(read.TryCurrent(out var low, out var high));
        Assert.Equal(76, low);
        Assert.Equal(89, high);
    }

    // ---- Staleness: the rules that stop an old reading being drawn as a current one ----------

    private static RosterRow Row(int? rung, double? ageSeconds)
        => new("rat1", IsLive: true, IsCurrentTarget: true, FightOutcome.Unresolved,
            rung, "seriously injured", ageSeconds);

    [Fact]
    public void RosterRow_FreshReadingIsNotStale()
    {
        var row = Row(3, 1.0);

        Assert.Equal(3, row.HealthRung);
        Assert.False(row.IsHealthStale);
    }

    /// <summary>One missed tick is ordinary - 68% of miss-streaks in the corpus are exactly one - so
    /// the ladder must not start fading at the first gap or it would flicker through every fight.</summary>
    [Fact]
    public void RosterRow_SurvivesOneMissedTickWithoutFading()
    {
        Assert.False(Row(3, 2.5).IsHealthStale);
    }

    [Fact]
    public void RosterRow_FadesAtThreeTicks_ButKeepsTheReading()
    {
        var row = Row(3, RosterRow.StaleAfterSeconds);

        Assert.Equal(3, row.HealthRung);
        Assert.True(row.IsHealthStale);
    }

    /// <summary>
    /// The health read is never discarded, however old it gets. MUD2 prints a descriptor after every
    /// non-killing landed hit, so a gap is positive evidence that nothing of the player's landed and
    /// the reading still holds. It fades and stays; it never disappears.
    /// </summary>
    [Fact]
    public void RosterRow_NeverDiscardsAReading_HoweverOldItGets()
    {
        foreach (var age in new[] { 10.0, 60.0, 600.0 })
        {
            var row = Row(3, age);

            Assert.Equal(3, row.HealthRung);
            Assert.True(row.IsHealthStale);   // faded, and still there
        }
    }

    [Fact]
    public void RosterRow_NeverReportedStaysDistinctFromMerelyOld()
    {
        // The one state that genuinely has nothing to show: a creature the player has not landed on.
        // It must not be reachable by a reading simply getting old, or the never-blank rule would have
        // erased the distinction it depends on.
        var never = new RosterRow("rat1", IsLive: true, IsCurrentTarget: true, FightOutcome.Unresolved);

        Assert.Null(never.HealthRung);
        Assert.Null(never.HealthPhrase);
        Assert.False(never.IsHealthStale);   // nothing to fade

        Assert.NotNull(Row(3, 600.0).HealthRung);
    }

    [Fact]
    public void Roster_CarriesHealthAndDamageThroughToTheRows()
    {
        var plan = ParticipantRoster.Build(
        [
            new ParticipantFact("rat1", false, FightOutcome.Unresolved, 3, "seriously injured", 1.0, 12.0),
            new ParticipantFact("rat2", false, FightOutcome.Unresolved, 7, "fit", 4.0, 30.0),
        ]);

        Assert.Equal(3, plan.Rows[0].HealthRung);
        Assert.Equal("seriously injured", plan.Rows[0].HealthPhrase);
        Assert.Equal(12.0, plan.Rows[0].DamageTakenFrom);
        Assert.Equal(30.0, plan.Rows[1].DamageTakenFrom);
    }

    // -- boundary crossings ------------------------------------------------------

    [Fact]
    public void ADroppedRung_MeasuresTheDamageSinceThePreviousReading()
    {
        // With a descriptor after every landed blow, the span between two readings is normally that one
        // blow - so this reads the same as "the blow that caused it" in the ordinary case.
        var fight = new FightAccumulator("giant0", T0, weaponAtStart: null);

        fight.AddYouHit(20, 29);
        fight.NoteHealth(6, "superficially injured", T0);
        fight.AddYouHit(1, 4);
        fight.NoteHealth(5, "to have minor injuries", T0.AddSeconds(2));

        var crossing = fight.RungCrossing;
        Assert.NotNull(crossing);
        Assert.Equal(5, crossing!.Value.Rung);
        // The 1-4 since the last reading, not the 20-29 before it and not the cumulative 21-33.
        Assert.Equal(1, crossing.Value.DamageSinceReading.Low);
        Assert.Equal(4, crossing.Value.DamageSinceReading.High);
        Assert.Equal(0, crossing.Value.DealtSince.High);
    }

    [Fact]
    public void ADroppedRung_CountsEveryBlowSinceTheLastReading_NotJustTheLastOne()
    {
        // The case that separates the two forms, and the reason the span form is the one kept. A
        // descriptor follows every non-killing landed hit, so this is rare - but a killing blow prints
        // none, a narrative-mode blow carries no bracket, and a parser miss stays possible. Charging the
        // drop to the last blow alone would report 29 where 44 landed, and understating the damage
        // behind a drop makes the pool ceiling it implies too TIGHT: the creature would read as smaller,
        // and the fight as easier to win, than the evidence supports.
        var fight = new FightAccumulator("giant0", T0, weaponAtStart: null);

        fight.AddYouHit(1, 1);
        fight.NoteHealth(7, "fit", T0);
        fight.AddYouHit(10, 15);          // no descriptor observed for this one
        fight.AddYouHit(20, 29);
        fight.NoteHealth(4, "covered in wounds", T0.AddSeconds(4));

        var crossing = fight.RungCrossing;
        Assert.NotNull(crossing);
        Assert.Equal(3, crossing!.Value.RungsDropped);
        Assert.Equal(30, crossing.Value.DamageSinceReading.Low);
        Assert.Equal(44, crossing.Value.DamageSinceReading.High);

        // And the ceiling it implies is the loose, safe one rather than the tight, wrong one.
        Assert.Equal(44.0 * 7 / 2, NpcVitality.PoolCeiling(null, crossing)!.Value, 6);
    }

    [Fact]
    public void ARepeatedReadingReAnchorsTheSpan_TighteningTheNextCrossing()
    {
        // A same-rung descriptor is not a crossing, but it IS a fresh anchor: it proves the creature was
        // still in that rung at that point, so the next drop is measured from there rather than from
        // further back. Free tightening, and the reason repeats advance the anchor.
        var fight = new FightAccumulator("giant0", T0, weaponAtStart: null);

        fight.AddYouHit(1, 1);
        fight.NoteHealth(7, "fit", T0);
        fight.AddYouHit(10, 15);
        fight.NoteHealth(7, "fit", T0.AddSeconds(2));
        fight.AddYouHit(20, 29);
        fight.NoteHealth(4, "covered in wounds", T0.AddSeconds(4));

        var crossing = fight.RungCrossing;
        Assert.NotNull(crossing);
        Assert.Equal(3, crossing!.Value.RungsDropped);
        // 20-29, not the 30-44 the previous test measured over the same blows.
        Assert.Equal(29, crossing.Value.DamageSinceReading.High);
    }

    [Fact]
    public void ARepeatedReading_IsNotACrossing()
    {
        // Nothing new was learned, so nothing may be attributed to the blow before it.
        var fight = new FightAccumulator("giant0", T0, weaponAtStart: null);

        fight.AddYouHit(1, 4);
        fight.NoteHealth(6, "superficially injured", T0);
        fight.AddYouHit(1, 4);
        fight.NoteHealth(6, "superficially injured", T0.AddSeconds(2));

        Assert.Null(fight.RungCrossing);
    }

    [Fact]
    public void AReadingThatIMPROVES_IsNotACrossing()
    {
        // Creatures regenerate - the corpus has a zombie oscillating four times in one fight - and a
        // rung going back UP is not a boundary the last blow drove it across.
        var fight = new FightAccumulator("zombie2", T0, weaponAtStart: null);

        fight.AddYouHit(1, 4);
        fight.NoteHealth(5, "to have minor damage", T0);
        fight.AddYouHit(1, 4);
        fight.NoteHealth(6, "superficially damaged", T0.AddSeconds(2));

        Assert.Null(fight.RungCrossing);
    }

    [Fact]
    public void TheFirstReadingOfAFight_IsNotACrossing()
    {
        // There is no previous rung to have dropped from, and the creature may have walked in hurt.
        var fight = new FightAccumulator("giant0", T0, weaponAtStart: null);

        fight.AddYouHit(1, 4);
        fight.NoteHealth(3, "seriously injured", T0);

        Assert.Null(fight.RungCrossing);
    }

    [Fact]
    public void ABlowWithNoParsedRange_CannotCarryACrossing()
    {
        // Narrative mode: the blow landed but the game printed no bracket, so there is no width to pin
        // the boundary with. Stale-attributing the previous blow's bracket would claim a precision that
        // blow did not produce.
        var fight = new FightAccumulator("giant0", T0, weaponAtStart: null);

        fight.AddYouHit(1, 4);
        fight.NoteHealth(6, "superficially injured", T0);
        fight.AddYouHit(null, null);
        fight.NoteHealth(5, "to have minor injuries", T0.AddSeconds(2));

        Assert.Null(fight.RungCrossing);
    }

    [Fact]
    public void ABracketlessBlowPoisonsTheWholeSpan_NotJustItself()
    {
        // The narrative-mode blow's damage never reaches DamageDealt, so the span since the last reading
        // understates what the creature absorbed even though a later blow in the same span is fully
        // parsed. Suppressed until the next clean reading rather than recorded from a short measurement.
        var fight = new FightAccumulator("giant0", T0, weaponAtStart: null);

        fight.AddYouHit(1, 1);
        fight.NoteHealth(7, "fit", T0);
        fight.AddYouHit(null, null);
        fight.AddYouHit(20, 29);
        fight.NoteHealth(4, "covered in wounds", T0.AddSeconds(4));

        Assert.Null(fight.RungCrossing);

        // The next clean span records normally - the flag clears at the reading.
        fight.AddYouHit(5, 8);
        fight.NoteHealth(2, "critically injured", T0.AddSeconds(6));

        Assert.Equal(8, fight.RungCrossing!.Value.DamageSinceReading.High);
    }

    [Fact]
    public void ACrossingAgesWithBlowsThatFollowIt()
    {
        var fight = new FightAccumulator("giant0", T0, weaponAtStart: null);

        fight.AddYouHit(1, 4);
        fight.NoteHealth(6, "superficially injured", T0);
        fight.AddYouHit(1, 4);
        fight.NoteHealth(5, "to have minor injuries", T0.AddSeconds(2));
        fight.AddYouHit(10, 14);

        var crossing = fight.RungCrossing;
        Assert.NotNull(crossing);
        Assert.Equal(10, crossing!.Value.DealtSince.Low);
        Assert.Equal(14, crossing.Value.DealtSince.High);
    }

    [Fact]
    public void ACrossingRecordsHowFarItFell_NotJustThatItFell()
    {
        // A blow that carries the creature down three rungs at once bounds its pool from ABOVE, which a
        // single-rung drop cannot do. The magnitude is the whole difference, so it has to be recorded.
        var fight = new FightAccumulator("giant0", T0, weaponAtStart: null);

        fight.AddYouHit(1, 1);
        fight.NoteHealth(7, "fit", T0);
        fight.AddYouHit(20, 29);
        fight.NoteHealth(4, "covered in wounds", T0.AddSeconds(2));

        var crossing = fight.RungCrossing;
        Assert.NotNull(crossing);
        Assert.Equal(4, crossing!.Value.Rung);
        Assert.Equal(3, crossing.Value.RungsDropped);
        Assert.Equal(29, crossing.Value.DamageSinceReading.High);
    }

    [Fact]
    public void AnOrdinarySingleRungDrop_RecordsADropOfOne()
    {
        var fight = new FightAccumulator("giant0", T0, weaponAtStart: null);

        fight.AddYouHit(1, 4);
        fight.NoteHealth(6, "superficially injured", T0);
        fight.AddYouHit(1, 4);
        fight.NoteHealth(5, "to have minor injuries", T0.AddSeconds(2));

        Assert.Equal(1, fight.RungCrossing!.Value.RungsDropped);
    }

    [Fact]
    public void ADiagnoseReadingReachesTheRow_AndIsStoredExactlyAsPrinted()
    {
        // The one absolute stamina figure allowed on an NPC row, because MUD2 printed it to the player
        // in so many words - "has a stamina lying between 90 and 99". STORED as printed and nothing
        // rounds or re-derives it; what the row DRAWS is NpcStaminaReading.TryCurrent, that number less
        // the damage landed since. It is kept for the fight under the same never-blank rule as the
        // wound phrase.
        var plan = ParticipantRoster.Build(
        [
            new ParticipantFact("water-snake5", false, FightOutcome.Unresolved, 5, "to have minor injuries", 30.0)
            {
                StaminaRead = new NpcStaminaReading(90, 99, DamageBracket.Zero),
            },
        ]);

        var read = plan.Rows[0].StaminaRead;
        Assert.NotNull(read);
        Assert.Equal(90, read!.Value.PrintedLow);
        Assert.Equal(99, read.Value.PrintedHigh);

        // Nothing rounds or re-derives it, and age does not remove it.
        Assert.True(plan.Rows[0].IsHealthStale);
        Assert.NotNull(plan.Rows[0].StaminaRead);
    }

    [Fact]
    public void ADiagnoseReadingCarriesItsOwnAge_AndFadesOnThatAndNotOnTheWoundPhrases()
    {
        // The two readings go stale for different reasons: a wound descriptor is corroborated by
        // silence (no descriptor means no blow landed), and a probe's number is not, because a creature
        // regenerates unannounced. So a fresh probe under an old descriptor must draw bright, and an
        // old probe under a fresh descriptor must draw faded.
        var freshProbe = ParticipantRoster.Build(
        [
            new ParticipantFact("water-snake5", false, FightOutcome.Unresolved, 5, "to have minor injuries", 30.0)
            {
                StaminaRead = new NpcStaminaReading(90, 99, DamageBracket.Zero),
                StaminaReadAgeSeconds = 1.0,
            },
        ]).Rows[0];

        Assert.True(freshProbe.IsHealthStale);
        Assert.False(freshProbe.IsStaminaReadStale);

        var oldProbe = ParticipantRoster.Build(
        [
            new ParticipantFact("water-snake5", false, FightOutcome.Unresolved, 5, "to have minor injuries", 1.0)
            {
                StaminaRead = new NpcStaminaReading(90, 99, DamageBracket.Zero),
                StaminaReadAgeSeconds = RosterRow.StaminaReadStaleAfterSeconds,
            },
        ]).Rows[0];

        Assert.False(oldProbe.IsHealthStale);
        Assert.True(oldProbe.IsStaminaReadStale);
    }

    [Fact]
    public void NoDiagnoseTaken_LeavesTheRowWithout()
    {
        var plan = ParticipantRoster.Build(
            [new ParticipantFact("rat1", false, FightOutcome.Unresolved, 5, "to have minor injuries", 1.0)]);

        Assert.Null(plan.Rows[0].StaminaRead);
    }
}
