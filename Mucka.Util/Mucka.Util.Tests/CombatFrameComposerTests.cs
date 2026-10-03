using Mucka.Combat;
using MudSharp.Combat;

namespace Mucka.Util.Tests;

/// <summary>
/// The Combat Rail's frame composer. It assigned three view-model fields from inside the MAUI
/// assembly until it became <see cref="CombatFrameComposer.Compose"/>, so none of the rules below
/// had a way to fail.
/// </summary>
public sealed class CombatFrameComposerTests
{
    private static readonly DateTime Now = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

    private static readonly TimeSpan TenTicks =
        TimeSpan.FromMilliseconds(10 * CombatTiming.TickMilliseconds);

    private static FightSnapshot Fight(
        string npcName = "large rat",
        bool resolved = false,
        int youHits = 2,
        int theyHits = 0,
        int theyMisses = 0,
        double damageTaken = 0,
        TimeSpan? duration = null,
        double? largestBlowTaken = null)
        => new(
            npcName, NpcGroups.Normalize(npcName), Weapon: "axe0", NpcWeapon: null,
            YouHits: youHits, YouMisses: 0, TheyHits: theyHits, TheyMisses: theyMisses,
            ApproxDamageDone: 10, ApproxDamageTaken: damageTaken,
            Duration: duration ?? TenTicks,
            Outcome: resolved ? FightOutcome.Kill : FightOutcome.Unresolved,
            IsResolved: resolved,
            EndedUtc: resolved ? Now : null,
            TheirDamage: largestBlowTaken is double blow ? new DamageProfile(1, blow, blow) : default);

    private static CombatStatDeficits Stamina(int current) => new(StaminaCurrent: current, StaminaMax: 100, ObjectsCarried: 0);

    private static CombatEncounterSnapshot Encounter(
        bool hasEncounter = true, bool inCombat = true, string? weapon = "axe0",
        FightSnapshot[]? fights = null)
        => new(
            HasEncounter: hasEncounter, InCombat: inCombat, StartedUtc: Now - TenTicks,
            CurrentWeapon: weapon, ActiveNpcs: [],
            YouHits: 0, YouMisses: 0, TheyHits: 0, TheyMisses: 0, YouHitRate: 0, TheyHitRate: 0,
            ApproxDamageDone: 0, ApproxDamageTaken: 0, Duration: TenTicks,
            ApproxDps: 0, TheirApproxDps: 0, Fights: fights ?? []);

    private static CombatFrame Compose(
        CombatEncounterSnapshot snapshot,
        CombatStatDeficits? deficits = null,
        IReadOnlyList<CombatEnding>? deadStrip = null,
        IReadOnlyList<string>? inventory = null)
        => CombatFrameComposer.Compose(new CombatFrameInputs(
            snapshot,
            deficits ?? new CombatStatDeficits(StaminaCurrent: 60, StaminaMax: 100, ObjectsCarried: 0),
            CombatHistoryContext.Empty,
            Now,
            inventory ?? [],
            _ => false,
            default,
            deadStrip ?? [],
            StaminaLostLastTick: 0,
            LastStaminaLossUtc: DateTime.MinValue,
            TickAnchor: null,
            PlayerName: "Tester",
            StaminaAnsiColor: null));

    // ---- Big hitters move the bands --------------------------------------------------------------

    /// <summary>
    /// A creature that hits for 30, against a player at 60: two of its blows from dead, so amber - not
    /// red - at a stamina the player would call healthy.
    ///
    /// <para>The average-damage "two hits left" count used to force red here; the max-blow bands
    /// replaced it, so the average alone no longer reaches the red.</para>
    /// </summary>
    [Fact]
    public void Glow_TwoOfTheLargestBlowsFromDead_IsAmber()
    {
        var frame = Compose(Encounter(fights: [Fight(theyHits: 3, damageTaken: 90, largestBlowTaken: 30)]),
            Stamina(60));

        Assert.NotEqual(CombatTier.T3, frame.PulseTier);
        Assert.InRange(frame.GlowLevel, 1, StaminaGlow.Steps);
    }

    /// <summary>The same creature with the player one blow from dead is red.</summary>
    [Fact]
    public void Glow_OneOfTheLargestBlowsFromDead_IsRed()
    {
        var frame = Compose(Encounter(fights: [Fight(theyHits: 3, damageTaken: 90, largestBlowTaken: 30)]),
            Stamina(31));

        Assert.Equal(CombatTier.T3, frame.PulseTier);
        Assert.Equal(StaminaGlow.Red, frame.GlowLevel);
    }

    /// <summary>The average-damage count no longer forces the red on its own: with no measured largest
    /// blow, a healthy 60 is not lit at all.</summary>
    [Fact]
    public void Glow_TheAverageHitsLeftCountAloneDoesNotLightIt()
        => Assert.Equal(0, Compose(Encounter(fights: [Fight(theyHits: 3, damageTaken: 90)]), Stamina(60)).GlowLevel);

    /// <summary>The demotion the override exists to survive: a projection-driven T3 with no
    /// hits-left count behind it renders as T2. Catches an implementation that promotes on the
    /// projection alone, which is the "alarm that cries wolf at 30" this ladder was built to
    /// stop.</summary>
    [Fact]
    public void PulseTier_ProjectionOnlyT3IsDemotedToT2()
    {
        // Losing badly on the clock, but only one landed blow on file - below CombatOutlook's own
        // MinimumOwnHits, so no hits-left count exists.
        var frame = Compose(Encounter(fights: [Fight(youHits: 1, theyHits: 1, damageTaken: 55)]),
            new CombatStatDeficits(StaminaCurrent: 55, StaminaMax: 100, ObjectsCarried: 0));

        Assert.NotEqual(CombatTier.T3, frame.PulseTier);
    }

    /// <summary>Low stamina promotes to T3 on its own, in a fight the projection is not worried
    /// about. Catches the stamina ladder being dropped from the in-combat branch, where it is the one
    /// thing the whole-panel glow answers to.</summary>
    [Fact]
    public void PulseTier_SurvivalStaminaHoldsT3InCombat()
    {
        var frame = Compose(Encounter(fights: [Fight()]), Stamina(StaminaGlow.RedStamina));

        Assert.Equal(CombatTier.T3, frame.PulseTier);
        Assert.Equal(StaminaGlow.Red, frame.GlowLevel);
    }

    /// <summary>Planning-the-exit stamina is amber, not the red alarm: 25 sits mid-ramp.</summary>
    [Fact]
    public void Glow_IsAmberBetweenTwentyAndThirty()
    {
        var frame = Compose(Encounter(fights: [Fight()]), Stamina(25));

        Assert.NotEqual(CombatTier.T3, frame.PulseTier);
        Assert.InRange(frame.GlowLevel, 1, StaminaGlow.Steps);
    }

    /// <summary>Something that has hit for 39 can kill at 39: the red is due at 40.</summary>
    [Fact]
    public void Glow_RedRisesToOneAboveTheLargestLiveBlow()
    {
        var snapshot = Encounter(fights: [Fight(largestBlowTaken: 39)]);

        Assert.Equal(StaminaGlow.Red, Compose(snapshot, Stamina(40)).GlowLevel);
        Assert.Equal(CombatTier.T3, Compose(snapshot, Stamina(40)).PulseTier);
        Assert.InRange(Compose(snapshot, Stamina(41)).GlowLevel, 1, StaminaGlow.Steps);
    }

    /// <summary>A creature that has fallen no longer threatens: its blow does not raise the bands, even
    /// with the fight still going against something else.</summary>
    [Fact]
    public void Glow_AFallenCreaturesBlowDoesNotCount()
    {
        var frame = Compose(
            Encounter(fights: [Fight(resolved: true, largestBlowTaken: 39), Fight("zombie")]), Stamina(35));

        Assert.Equal(0, frame.GlowLevel);
    }

    /// <summary>Out of combat there are no hits to worry about: the plain 30/20 bands, whatever hit the
    /// player during the encounter that just ended.</summary>
    [Fact]
    public void Glow_OutOfCombatFallsBackToThirtyAndTwenty()
    {
        var frame = Compose(
            Encounter(inCombat: false, fights: [Fight(resolved: true, largestBlowTaken: 39)]), Stamina(35));

        Assert.Equal(0, frame.GlowLevel);
        Assert.Equal(StaminaGlow.Red, Compose(
            Encounter(inCombat: false, fights: [Fight(resolved: true, largestBlowTaken: 39)]), Stamina(20)).GlowLevel);
    }

    /// <summary>The gate is "in a fight", not only "the creature is down": a fight the tracker has not
    /// yet resolved still raises nothing once combat is over.</summary>
    [Fact]
    public void Glow_OutOfCombatIgnoresEvenAnUnresolvedFightsBlow()
        => Assert.Equal(0, Compose(
            Encounter(inCombat: false, fights: [Fight(largestBlowTaken: 39)]), Stamina(35)).GlowLevel);

    [Fact]
    public void Glow_IsOffAboveTheRamp()
        => Assert.Equal(0, Compose(Encounter(fights: [Fight()]), Stamina(31)).GlowLevel);

    // ---- The flee pill is fed the count, not the tier -----------------------------------------

    /// <summary>
    /// The pill agrees with the count that already overrides the whole-panel glow, rather than
    /// deriving a second opinion from the resolved tier.
    ///
    /// <para>Asserted by construction: the frame's pill is what <see cref="FleePillResolver"/>
    /// returns for the hits-left count, and NOT what it returns for the same fight with no count.
    /// Catches the pill being handed the tier - the two then disagree about a fight two blows from
    /// over, which is precisely when the player is reading it.</para>
    /// </summary>
    [Fact]
    public void FleePill_IsResolvedFromTheHitsLeftCount()
    {
        var snapshot = Encounter(fights: [Fight(theyHits: 3, damageTaken: 90)]);
        var frame = Compose(snapshot);

        var withCount = FleePillResolver.Resolve(
            inCombat: true, 60, FleePillResolver.WorstCaseTickDamage(frame.Live.Roster), hitsLeft: 2);
        var withoutCount = FleePillResolver.Resolve(
            inCombat: true, 60, FleePillResolver.WorstCaseTickDamage(frame.Live.Roster), hitsLeft: null);

        Assert.Equal(withCount, frame.Live.FleePill);
        Assert.NotEqual(withoutCount, frame.Live.FleePill);
    }

    // ---- The post-combat branch ---------------------------------------------------------------

    /// <summary>
    /// An encounter that is open but no longer live: only the survival PROJECTION goes quiet.
    /// Projecting a finished fight's death clock would be a lie, so the pill, the reading, the blink
    /// and both tick projections are absent.
    ///
    /// <para>Catches the branch falling through to the in-combat composition, which would leave a
    /// death clock counting down on a fight that is over.</para>
    /// </summary>
    [Fact]
    public void PostCombat_TheProjectionGoesQuiet()
    {
        var frame = Compose(Encounter(inCombat: false, fights: [Fight(resolved: true)]));

        Assert.True(frame.Live.HasEncounter);
        Assert.False(frame.Live.InCombat);
        Assert.Equal(FleePillStatus.Hidden, frame.Live.FleePill);
        Assert.Equal(SurvivalReading.None, frame.Live.Survival);
        Assert.False(frame.Live.BlinkOn);
        Assert.Null(frame.Live.TicksToDeath);
        Assert.Null(frame.Live.TicksToVictory);
    }

    /// <summary>What the same branch KEEPS: the roster is still composed, so the player can still
    /// read what they were just fighting. Catches the branch being made to blank the panel.</summary>
    [Fact]
    public void PostCombat_TheRosterSurvives()
    {
        var frame = Compose(Encounter(inCombat: false, fights: [Fight("zombie", resolved: true)]));

        Assert.Single(frame.Live.Roster.Rows);
        Assert.Equal("zombie", frame.Live.Roster.Rows[0].Name);
    }

    /// <summary>
    /// Unarmed is FALSE out of combat whatever the player is holding.
    ///
    /// <para>MUD2 has no persistent wielded weapon - one is named for the current fight and stops
    /// being wielded when it ends - so "unarmed" is not a state a player can be in between fights;
    /// it is the only state. Catches the flag being wired to <c>!hasWeapon</c>, which would raise
    /// the unarmed alarm from the end of every fight until the start of the next one.</para>
    /// </summary>
    [Fact]
    public void PostCombat_IsUnarmedIsFalseWhateverIsHeld()
    {
        var frame = Compose(Encounter(inCombat: false, weapon: null, fights: [Fight(resolved: true)]));

        Assert.False(frame.Live.IsUnarmed);
        Assert.Equal(string.Empty, frame.Live.WeaponText);
    }

    /// <summary>The Ctrl+W offer exists only while a fight is live: there is nothing a wield could
    /// mean between fights, and a chip advertising the key when it would do nothing is worse than no
    /// chip. Catches the gate being dropped.</summary>
    [Fact]
    public void PostCombat_NoAlternateWeaponIsOffered()
        => Assert.Null(
            Compose(
                Encounter(inCombat: false, fights: [Fight(resolved: true)]),
                inventory: ["broadsword"]).Live.AltWeapon);

    // ---- The no-encounter branch --------------------------------------------------------------

    /// <summary>
    /// With no encounter and nothing on the session's record, the panel is idle.
    /// </summary>
    [Fact]
    public void NoEncounter_WithNoHistoryIsIdle()
    {
        var frame = Compose(Encounter(hasEncounter: false, inCombat: false));

        Assert.Equal(CombatLiveView.Idle, frame.Live);
    }

    /// <summary>
    /// The dead strip is session-scoped and survives having no encounter: the flag the canvas gates
    /// the whole strip region on is re-enabled purely so prior endings stay on screen between
    /// fights.
    ///
    /// <para>Catches the branch returning plain Idle, which blanks a session's history the moment a
    /// fight ends - the roster is empty either way, so this only ever re-enables the strip, never a
    /// live stack.</para>
    /// </summary>
    [Fact]
    public void NoEncounter_WithSessionHistoryKeepsTheDeadStripVisible()
    {
        CombatEnding[] archive = [new("large rat", FightOutcome.Kill, Now)];

        var frame = Compose(Encounter(hasEncounter: false, inCombat: false), deadStrip: archive);

        Assert.True(frame.Live.HasEncounter);
        Assert.Equal(archive, frame.Live.DeadStripHistory);
        Assert.Empty(frame.Live.Roster.Rows);
    }

    /// <summary>The low-stamina glow outlives the fight - the danger does not stop when the fight
    /// does. Catches the no-encounter branch reporting None, which would drop the one alarm that
    /// covers walking away from a fight at 22 stamina and forgetting about it.</summary>
    [Fact]
    public void NoEncounter_VulnerableStaminaStillGlows()
    {
        var frame = Compose(
            Encounter(hasEncounter: false, inCombat: false),
            new CombatStatDeficits(StaminaCurrent: 20, StaminaMax: 100, ObjectsCarried: 0));

        Assert.Equal(CombatTier.T3, frame.PulseTier);
        Assert.Equal(StaminaGlow.Red, frame.GlowLevel);
    }

    /// <summary>The amber ramp runs between fights too: the danger does not stop when the fight
    /// does.</summary>
    [Fact]
    public void NoEncounter_TheAmberRampStillGlows()
        => Assert.InRange(Compose(Encounter(hasEncounter: false, inCombat: false), Stamina(28)).GlowLevel,
            1, StaminaGlow.Steps);

    // ---- Encumbrance is computed on every branch ------------------------------------------------

    /// <summary>Carrying too much is flagged with no encounter at all: it is worth knowing through
    /// the grace window and between fights, and it costs nothing to recompute. Catches the
    /// computation being moved inside the in-combat branch.</summary>
    [Fact]
    public void EncumbranceTier_IsComputedWithNoEncounter()
    {
        var frame = Compose(
            Encounter(hasEncounter: false, inCombat: false),
            new CombatStatDeficits(
                StaminaCurrent: 60, StaminaMax: 100, ObjectsCarried: 9,
                StrengthEffective: 20, StrengthMax: 100));

        Assert.Equal(CombatTier.T2, frame.EncumbranceTier);
    }

    // ---- The encounter table --------------------------------------------------------------------

    /// <summary>Duration comes off the ENCOUNTER, not the primary fight: a fight that started when
    /// the third creature joined has been going a fraction of the time the player has been in
    /// trouble. Catches the table being fed the fight's own clock, which freezes at EndedUtc.</summary>
    [Fact]
    public void EncounterTicks_ComeOffTheEncounterNotThePrimaryFight()
    {
        var frame = Compose(Encounter(
            fights: [Fight(duration: TimeSpan.FromMilliseconds(CombatTiming.TickMilliseconds))]));

        Assert.Equal(10.0, frame.Live.EncounterTicks!.Value, 6);
    }

    /// <summary>Par counts what is still up; Op counts everything the encounter has produced. A
    /// fight that has killed one of two reads "1" and "2".</summary>
    [Fact]
    public void OpponentCounts_SeparateTheLiveFromTheFaced()
    {
        var frame = Compose(Encounter(fights: [Fight("large rat"), Fight("zombie", resolved: true)]));

        Assert.Equal(1, frame.Live.LiveOpponents);
        Assert.Equal(2, frame.Live.OpponentsFaced);
    }

    // ---- IncomingTempoOf ------------------------------------------------------------------------

    /// <summary>The pooled tempo is a ratio of sums over LIVE participants only: a creature that is
    /// already dead is not part of what is coming at the player. Catches resolved fights being left
    /// in, which keeps the border reading busy after a pack is cleared.</summary>
    [Fact]
    public void IncomingTempo_PoolsLiveParticipantsOnly()
    {
        var tempo = CombatFrameComposer.IncomingTempoOf(Encounter(fights:
        [
            Fight("large rat", theyHits: 3, theyMisses: 1),
            Fight("zombie", resolved: true, theyHits: 40, theyMisses: 40),
        ]));

        Assert.Equal(3, tempo.Hits);
        Assert.Equal(1, tempo.Misses);
    }

    // ---- TicksFromSeconds -----------------------------------------------------------------------

    /// <summary>Null straight through. CombatOutlook returns null for "not enough evidence to
    /// project", and a zero here would put a confident "0t to death" on the table at the exact
    /// moment the projection was refusing to make one. Permadeath game; catches a `?? 0`.</summary>
    [Fact]
    public void TicksFromSeconds_PassesNullThrough()
        => Assert.Null(CombatFrameComposer.TicksFromSeconds(null));

    /// <summary>And converts a real projection against the one tick length.</summary>
    [Fact]
    public void TicksFromSeconds_ConvertsAgainstTheTickLength()
        => Assert.Equal(5.0, CombatFrameComposer.TicksFromSeconds(10.0)!.Value, 6);
}
