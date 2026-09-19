using MudSharp.Combat;
using MudSharp.Models;
using Mucka.Combat;

namespace Mucka.Util.Tests;

/// <summary>
/// CombatTracker classification and encounter/fight boundary detection, using literal lines
/// observed in the capture corpus. CombatTracker consumes StyledLine.PlainText directly, so tests
/// build lines straight from plain text - no protocol bytes/parser harness needed here.
/// </summary>
public class CombatTrackerTests
{
    private static StyledLine Line(string text) => new([new StyledSpan(text, TextStyle.Default)]);

    /// <summary>The frame prompt as the parser delivers it: a PARTIAL line. In game mode it is the
    /// only partial line there is, which is what lets CombatTracker see frame boundaries at all - see
    /// its _endedThisFrame remarks.</summary>
    private static StyledLine PromptLine() =>
        new([new StyledSpan("*", TextStyle.Default)], isPartial: true);

    /// <summary>A line the server tagged as a fight end (C1 08.10/08.11/08.12) - see
    /// LineKind.FightEnd. The tracker treats that tag as authoritative about the FACT of an end
    /// regardless of the wording, which is the only defence against a phrasing nobody has seen.</summary>
    private static StyledLine FightEndLine(string text) =>
        new([new StyledSpan(text, TextStyle.Default)], kind: LineKind.FightEnd);

    private static (CombatTracker tracker, List<bool> inCombat, List<CombatEvent> events) NewTracker()
    {
        var t = new CombatTracker();
        var inCombat = new List<bool>();
        var events = new List<CombatEvent>();
        t.InCombatChanged += inCombat.Add;
        t.EventOccurred += events.Add;
        return (t, inCombat, events);
    }

    /// <summary>
    /// The four loadout lines, each its own kind. Verbatim wordings from the capture corpus.
    ///
    /// <para>They are four rather than one because they are four different facts. A drop and a take
    /// move both the item count and the weight; a container move changes the count while leaving the
    /// weight where it is - IF the container is itself carried, which the line does not say, which is
    /// why the container is named rather than discarded. Collapsing the pair into "dropped" would
    /// assert a weight change that may not have happened, and would lose the single cleanest
    /// observation separating the dexterity burden from the strength one.</para>
    /// </summary>
    [Fact]
    public void TheFourLoadoutLines_EmitFourDistinctKinds_AndNameTheContainer()
    {
        var (t, _, events) = NewTracker();
        var now = DateTime.UtcNow;
        t.Observe(Line("You attack the rat17, using the axe0 as a weapon."), now);
        t.Observe(Line("Cache of farthings dropped."), now.AddSeconds(1));
        t.Observe(Line("Well-maintained pick2 taken."), now.AddSeconds(2));
        t.Observe(Line("Baton inserted in glass bottle6."), now.AddSeconds(3));
        t.Observe(Line("Starfish removed from lobster pot0."), now.AddSeconds(4));

        var moves = events.Skip(1).ToList();
        Assert.Equal(
            [CombatEventKind.ItemDropped, CombatEventKind.ItemTaken,
             CombatEventKind.ItemStowed, CombatEventKind.ItemRetrieved],
            moves.Select(e => e.Kind));
        Assert.Equal(
            ["Cache of farthings", "Well-maintained pick2", "Baton", "Starfish"],
            moves.Select(e => e.Weapon));
        Assert.Null(moves[0].Container);
        Assert.Null(moves[1].Container);
        Assert.Equal("glass bottle6", moves[2].Container);
        Assert.Equal("lobster pot0", moves[3].Container);
        // None of them touches the encounter: moving your pack about is not a combat state change.
        Assert.True(t.InCombat);
    }

    /// <summary>
    /// The refusal is not a drop. "The starfish is embroiled in combat and can't be dropped." is the
    /// one line in this family that means NOTHING moved. An unbounded name pattern would match it as
    /// a drop of an object called "The starfish is embroiled in combat and can't be" - a phantom
    /// event in the clog, and one that would force a pointless probe once MudSession starts keying
    /// off the same wordings.
    /// </summary>
    [Fact]
    public void ARefusedDrop_EmitsNothing()
    {
        var (t, _, events) = NewTracker();
        var now = DateTime.UtcNow;
        t.Observe(Line("You attack the rat17, using the axe0 as a weapon."), now);
        t.Observe(Line("The starfish is embroiled in combat and can't be dropped."), now.AddSeconds(1));

        Assert.DoesNotContain(events, e => e.Kind is CombatEventKind.ItemDropped or CombatEventKind.ItemTaken
                                                  or CombatEventKind.ItemStowed or CombatEventKind.ItemRetrieved);
    }

    /// <summary>A weapon breaking mid-fight must update the readout. "You cannot use the X to fight
    /// now!" is also the wield-refusal line, the sole direct evidence MUD2 emits of the hidden
    /// effective-strength gate, so it must be both acted on (the player is now bare-handed) and
    /// recorded.</summary>
    [Fact]
    public void WeaponBreakThenUnusable_EmitsBothEvents()
    {
        var (t, _, events) = NewTracker();
        var now = DateTime.UtcNow;
        t.Observe(Line("You attack the thief, using the dagger0 as a weapon."), now);
        t.Observe(Line("The dagger0 breaks to bits."), now.AddSeconds(1));
        t.Observe(Line("You cannot use the dagger0 to fight now!"), now.AddSeconds(2));

        Assert.Equal(CombatEventKind.WeaponBroke, events[1].Kind);
        Assert.Equal("dagger0", events[1].Weapon);

        Assert.Equal(CombatEventKind.WeaponUnusable, events[2].Kind);
        Assert.Equal("dagger0", events[2].Weapon);
        Assert.Equal(CombatActor.Player, events[2].Actor);
    }

    /// <summary>The refusal must not be confused with its near-twin equip line, which differs only
    /// in a few words and would otherwise clear the weapon the moment it was equipped.</summary>
    [Fact]
    public void WeaponUnusable_DoesNotMatchTheEquipLine()
    {
        var (t, _, events) = NewTracker();
        var now = DateTime.UtcNow;
        t.Observe(Line("You attack the thief."), now);
        t.Observe(Line("You are now using the staff0 to fight!"), now.AddSeconds(1));

        Assert.Equal(CombatEventKind.WeaponEquip, events[1].Kind);
        Assert.DoesNotContain(events, e => e.Kind == CombatEventKind.WeaponUnusable);
    }

    /// <summary>Bare-handed openings have no weapon clause: "You attack the thief." must still be
    /// matched and open an encounter.</summary>
    [Fact]
    public void PlayerAttackUnarmed_StartsCombatWithNoWeapon()
    {
        var (t, inCombat, events) = NewTracker();
        t.Observe(Line("You attack the thief."), DateTime.UtcNow);

        Assert.True(t.InCombat);
        Assert.Equal([true], inCombat);
        var e = Assert.Single(events);
        Assert.Equal(CombatEventKind.FightStart, e.Kind);
        Assert.Equal(CombatActor.Player, e.Actor);
        Assert.Equal("thief", e.NpcName);
        Assert.Null(e.Weapon);
    }

    /// <summary>Attacking bare-handed, then equipping a weapon mid-fight ("use staff"), must
    /// correctly show the weapon armed. The npc name must also not swallow the armed form's ",
    /// using the X as a weapon" clause - see the two-pattern comment on PlayerAttackStart.</summary>
    [Fact]
    public void UnarmedAttackThenWeaponEquip_EmitsBothAndKeepsTheWeapon()
    {
        var (t, _, events) = NewTracker();
        var now = DateTime.UtcNow;
        t.Observe(Line("You attack the thief."), now);
        t.Observe(Line("You are now using the staff0 to fight!"), now.AddSeconds(1));
        t.Observe(Line("You hit the thief (5-9)."), now.AddSeconds(2));

        Assert.Equal(CombatEventKind.FightStart, events[0].Kind);
        Assert.Equal("thief", events[0].NpcName);
        Assert.Null(events[0].Weapon);

        Assert.Equal(CombatEventKind.WeaponEquip, events[1].Kind);
        Assert.Equal("staff0", events[1].Weapon);

        Assert.Equal(CombatEventKind.Hit, events[2].Kind);
        Assert.Equal("thief", events[2].NpcName);
    }

    [Fact]
    public void PlayerAttack_StartsCombatAndClassifiesInitiator()
    {
        var (t, inCombat, events) = NewTracker();
        t.Observe(Line("You attack the rat0, using the dagger0 as a weapon."), DateTime.UtcNow);

        Assert.True(t.InCombat);
        Assert.Equal([true], inCombat);
        var e = Assert.Single(events);
        Assert.Equal(CombatEventKind.FightStart, e.Kind);
        Assert.Equal(CombatActor.Player, e.Actor);
        Assert.Equal("rat0", e.NpcName);
        Assert.Equal("dagger0", e.Weapon);
    }

    [Fact]
    public void NpcAggro_StartsCombatWithNpcInitiatorAndNoWeapon()
    {
        var (t, inCombat, events) = NewTracker();
        t.Observe(Line("The billy goat is glaring at you madly."), DateTime.UtcNow);

        Assert.True(t.InCombat);
        var e = Assert.Single(events);
        Assert.Equal(CombatEventKind.FightStart, e.Kind);
        Assert.Equal(CombatActor.Npc, e.Actor);
        Assert.Equal("billy goat", e.NpcName);
        Assert.Null(e.Weapon);
    }

    [Fact]
    public void HitAndMiss_BothDirectionsClassified()
    {
        var (t, _, events) = NewTracker();
        t.Observe(Line("You attack the rat0, using the dagger0 as a weapon."), DateTime.UtcNow);
        t.Observe(Line("You hit the rat0 (3-7)."), DateTime.UtcNow);
        t.Observe(Line("You miss the rat0."), DateTime.UtcNow);
        t.Observe(Line("The rat0 hits you (96/100)."), DateTime.UtcNow);
        t.Observe(Line("The rat0 misses you."), DateTime.UtcNow);

        Assert.Equal(
            [CombatEventKind.FightStart, CombatEventKind.Hit, CombatEventKind.Miss,
             CombatEventKind.HitByNpc, CombatEventKind.MissByNpc],
            events.Select(e => e.Kind));

        var hit = events.Single(e => e.Kind == CombatEventKind.Hit);
        Assert.Equal(3, hit.RangeLow);
        Assert.Equal(7, hit.RangeHigh);

        var hitByNpc = events.Single(e => e.Kind == CombatEventKind.HitByNpc);
        Assert.Equal(96, hitByNpc.RangeLow);   // current stamina, not a delta
        Assert.Equal(100, hitByNpc.RangeHigh); // max stamina
    }

    [Fact]
    public void Kill_EmitsBeforeInCombatFlipsFalse()
    {
        // A real ClogWriter listens to InCombatChanged to close its file and to EventOccurred to
        // write each line. If End() ran before Emit(), the closing Kill/Fled/Withdrawn/etc. line
        // would land after the writer already closed and be silently dropped from the clog.
        // Assert Emit fires strictly before the false transition.
        var t = new CombatTracker();
        var order = new List<string>();
        t.EventOccurred += e => order.Add($"event:{e.Kind}");
        t.InCombatChanged += v => order.Add($"incombat:{v}");
        var t0 = DateTime.UtcNow;

        t.Observe(Line("You attack the rat0, using the dagger0 as a weapon."), t0);
        t.Observe(Line("You have killed the rat0."), t0);

        Assert.Equal(
            ["incombat:True", "event:FightStart", "event:Kill", "incombat:False"],
            order);
    }

    [Fact]
    public void Kill_EndsCombatImmediately()
    {
        var (t, inCombat, events) = NewTracker();
        var t0 = DateTime.UtcNow;
        t.Observe(Line("You attack the rat0, using the dagger0 as a weapon."), t0);
        t.Observe(Line("You have killed the rat0."), t0);

        Assert.False(t.InCombat);   // closed the instant the last active NPC died - no window
        Assert.Equal([true, false], inCombat);
        Assert.Equal(CombatEventKind.Kill, events.Last().Kind);
    }

    [Fact]
    public void Kill_ThenUnrelatedNpcEngagesMomentsLater_StartsANewEncounter()
    {
        // A solo rat dies, and a completely different rat starts attacking a fraction of a second
        // later. It is NOT the same pack fight (rat21 never engaged while rat17 was still alive):
        // once the combatant count reaches 0, the fight is over, full stop, and whatever attacks
        // next opens a genuinely new encounter. Any need to keep capturing trailing prose after
        // the close belongs to logging (ClogWriter's tail capture - see ClogWriterTests), never
        // to this class.
        var (t, inCombat, events) = NewTracker();
        var t0 = DateTime.UtcNow;

        t.Observe(Line("You hit the rat17 (15-19)."), t0);
        t.Observe(Line("You have killed the rat17."), t0 + TimeSpan.FromMilliseconds(50));

        Assert.False(t.InCombat);   // rat17's encounter is already fully closed

        // Pure trailing prose CombatTracker never classifies (score/death confirmation) - must
        // not reopen or otherwise affect the now-closed encounter.
        t.Observe(Line("(Persona saved on +22 = 101,389)."), t0 + TimeSpan.FromMilliseconds(60));
        t.Observe(Line("The rat17 has just passed on."), t0 + TimeSpan.FromMilliseconds(70));

        t.Observe(Line("The rat21 is approaching you fiercely."), t0 + TimeSpan.FromMilliseconds(80));

        Assert.True(t.InCombat);   // a genuinely new encounter, opened immediately
        Assert.Equal([true, false, true], inCombat);
        Assert.Equal(
            [CombatEventKind.Hit, CombatEventKind.Kill, CombatEventKind.FightStart],
            events.Select(e => e.Kind));
        Assert.Equal("rat21", events.Last().NpcName);
    }

    [Fact]
    public void NpcKillsYou_ClosesTheWholeEncounterEvenWithOtherActiveParticipants()
    {
        // Player death must close the WHOLE encounter unconditionally - a dead player cannot
        // keep fighting anyone else in the same room, even if other NPCs (besides the killer)
        // are still active. Using the ordinary single-NPC End() here would leave the ram
        // dangling in _active with nothing left to ever remove it.
        var (t, inCombat, events) = NewTracker();
        t.Observe(Line("The vampire is looking at you hatefully."), DateTime.UtcNow);
        t.Observe(Line("The ram is glaring at you madly."), DateTime.UtcNow);
        t.Observe(Line("The vampire has killed you."), DateTime.UtcNow);

        Assert.False(t.InCombat);   // closed immediately, ram included
        Assert.Equal([true, false], inCombat);
        Assert.Equal(CombatEventKind.KilledByNpc, events.Last().Kind);
        Assert.Equal("vampire", events.Last().NpcName);
    }

    [Fact]
    public void NarrativeDeath_NamedNpc_ClassifiesAndClosesEncounter()
    {
        // Non-fightbrief ("narrative") phrasing, confirmed live: a character that never enabled
        // fightbrief was killed by a vampire. CombatTracker.Observe never sees a single
        // fightbrief-format Hit/Miss/HitByNpc line for the whole fight - this death line may be
        // the ONLY classifiable line in the entire encounter, so it must close things cleanly.
        var (t, inCombat, events) = NewTracker();
        t.Observe(Line("The vampire is looking at you hatefully."), DateTime.UtcNow);
        t.Observe(Line("You have been killed by the vampire."), DateTime.UtcNow);

        Assert.False(t.InCombat);
        Assert.Equal([true, false], inCombat);
        Assert.Equal(CombatEventKind.KilledByNpc, events.Last().Kind);
        Assert.Equal("vampire", events.Last().NpcName);
    }

    [Fact]
    public void NarrativeDeath_AnonymizedBySleepAndBlindness_ResolvesSoleActiveNpc()
    {
        // Exact real-capture scenario: the vampire cast blindness mid-fight, then put the player
        // to sleep, then landed the killing blow while blind - MUD2 anonymizes the killer to
        // "someone" in narrative mode whenever the player is blind. With exactly one active
        // participant AND the player known to be blind, resolve "someone" back to that NPC rather
        // than losing attribution entirely. The blindness has to be reported: a line alone says
        // nothing about why the name is missing (see the sibling below).
        var (t, inCombat, events) = NewTracker();
        t.Observe(Line("The vampire is looking at you hatefully."), DateTime.UtcNow);
        t.NoteCannotSee(true);
        t.Observe(Line("You have been killed by someone."), DateTime.UtcNow);

        Assert.False(t.InCombat);
        Assert.Equal([true, false], inCombat);
        Assert.Equal(CombatEventKind.KilledByNpc, events.Last().Kind);
        Assert.Equal("vampire", events.Last().NpcName);
    }

    [Fact]
    public void NarrativeDeath_AnonymizedWhileSighted_IsSomeoneElse()
    {
        // The same two lines with no blindness reported. Nothing explains the word, so the sole
        // engaged Creature does not get it: the operator's ruling is that an unexplained anonymous
        // line is left on the word rather than risk crediting an unannounced Unseen attacker's blow
        // to the vampire and teaching vampires its kind.
        var (t, _, events) = NewTracker();
        t.Observe(Line("The vampire is looking at you hatefully."), DateTime.UtcNow);
        t.Observe(Line("You have been killed by someone."), DateTime.UtcNow);

        Assert.Equal(CombatEventKind.KilledByNpc, events.Last().Kind);
        Assert.Equal("someone", events.Last().NpcName);
        Assert.Null(t.Knowledge.Known("vampire"));
    }

    [Fact]
    public void NarrativeDeath_AnonymizedWithMultipleActiveNpcs_KeepsAnonymousName()
    {
        // With more than one active participant there's no safe way to guess which NPC actually
        // landed the blow - keep the literal "someone" rather than guessing wrong.
        var (t, _, events) = NewTracker();
        t.Observe(Line("The billy goat is glaring at you madly."), DateTime.UtcNow);
        t.Observe(Line("The ram is glaring at you madly."), DateTime.UtcNow);
        t.Observe(Line("You have been killed by someone."), DateTime.UtcNow);

        Assert.Equal(CombatEventKind.KilledByNpc, events.Last().Kind);
        Assert.Equal("someone", events.Last().NpcName);
    }

    [Fact]
    public void NpcTriedToGo_IsNotAFlee_AndLeavesTheFightOpen()
    {
        // An NPC that cannot move reports that it TRIED to go somewhere, and is still in the
        // room. Treating that as a flee would close a live fight and (once pursuit lands) walk
        // the player out of it. The NpcFled regex requires the literal "has fled by going", so
        // this does not match.
        var (t, inCombat, events) = NewTracker();
        t.Observe(Line("You attack the billy goat, using the falchion as a weapon."), DateTime.UtcNow);
        t.Observe(Line("The billy goat has tried to go north."), DateTime.UtcNow);

        Assert.True(t.InCombat);
        Assert.DoesNotContain(events, e => e.Kind == CombatEventKind.NpcFled);
    }

    [Fact]
    public void NpcFlee_EndsThatFightOnly()
    {
        var (t, inCombat, events) = NewTracker();
        t.Observe(Line("You attack the billy goat, using the falchion as a weapon."), DateTime.UtcNow);
        t.Observe(Line("The billy goat has fled by going north."), DateTime.UtcNow);

        Assert.False(t.InCombat);
        Assert.Equal(CombatEventKind.NpcFled, events.Last().Kind);
        Assert.Equal(CombatActor.Npc, events.Last().Actor);
    }

    [Fact]
    public void MutualWithdraw_EndsFight()
    {
        var (t, inCombat, events) = NewTracker();
        t.Observe(Line("You attack the thief, using the dagger0 as a weapon."), DateTime.UtcNow);
        t.Observe(Line("You offer to withdraw from your fight with the thief."), DateTime.UtcNow);
        t.Observe(Line("The thief withdraws from your fight, and so do you."), DateTime.UtcNow);

        Assert.False(t.InCombat);
        Assert.Equal(
            [CombatEventKind.FightStart, CombatEventKind.WithdrawOffer, CombatEventKind.Withdrawn],
            events.Select(e => e.Kind));
    }

    [Fact]
    public void WithdrawOffer_AloneDoesNotEndCombat()
    {
        var (t, _, _) = NewTracker();
        t.Observe(Line("You attack the thief, using the dagger0 as a weapon."), DateTime.UtcNow);
        t.Observe(Line("You offer to withdraw from your fight with the thief."), DateTime.UtcNow);

        Assert.True(t.InCombat);   // offer alone changes nothing until the NPC accepts
    }

    /// <summary>
    /// The NPC's half of the handshake, replayed verbatim from session-rec.mud2.co.uk.20260902-232101
    /// records 994 and 998 - the only one of the five captured offers whose frame carries an incoming
    /// blow as well, and one of the three that run straight on to a kill.
    ///
    /// <para>Two things are being asserted and they are the whole point of the kind: the line is
    /// classified and attributed to the creature that spoke it, and it does NOT end the fight.</para>
    /// </summary>
    [Fact]
    public void NpcWithdrawOffer_IsAttributed_AndDoesNotEndTheFight()
    {
        var (t, _, events) = NewTracker();
        var now = DateTime.UtcNow;
        t.Observe(Line("You attack the zombie1, using the halberd as a weapon."), now);
        t.Observe(Line("You hit the zombie1 (1-4)."), now.AddSeconds(1));
        t.Observe(Line("The zombie1 hits you (69/91)."), now.AddSeconds(2));
        t.Observe(Line("The zombie1 offers to withdraw from your fight if you do likewise."), now.AddSeconds(3));

        Assert.True(t.InCombat);   // an invitation, not an end - the code on the wire is 08.07, not 08.10

        var offer = events.Last();
        Assert.Equal(CombatEventKind.NpcWithdrawOffer, offer.Kind);
        Assert.Equal(CombatActor.Npc, offer.Actor);
        Assert.Equal("zombie1", offer.NpcName);
        Assert.Equal("The zombie1 offers to withdraw from your fight if you do likewise.", offer.RawText);

        // ...and the fight really did run on: next frame, the player killed it.
        t.Observe(Line("You have killed the zombie1."), now.AddSeconds(4));
        Assert.False(t.InCombat);
        Assert.Equal(CombatEventKind.Kill, events.Last().Kind);
    }

    /// <summary>The creature's offer and the mutual acceptance are one word apart in the same
    /// vocabulary and mean opposite things, so neither pattern may match the other's line. Guarded
    /// because getting it wrong in the "offer closes the fight" direction is silent - the panel would
    /// simply stop showing an opponent that is still swinging.</summary>
    [Fact]
    public void NpcWithdrawOffer_AndMutualWithdraw_AreNotConfused()
    {
        var (t, _, events) = NewTracker();
        var now = DateTime.UtcNow;
        t.Observe(Line("You attack the zombie9, using the halberd as a weapon."), now);
        t.Observe(Line("The zombie9 offers to withdraw from your fight if you do likewise."), now.AddSeconds(1));
        Assert.True(t.InCombat);

        t.Observe(Line("The zombie9 withdraws from your fight, and so do you."), now.AddSeconds(2));
        Assert.False(t.InCombat);

        Assert.Equal(
            [CombatEventKind.FightStart, CombatEventKind.NpcWithdrawOffer, CombatEventKind.Withdrawn],
            events.Select(e => e.Kind));
    }

    [Fact]
    public void YouFlee_ClosesEveryActiveFightAtOnce()
    {
        // A single "You have fled..." line closes multiple simultaneous fights at once
        // (confirmed offline: one such line ended two simultaneous rat fights).
        var (t, inCombat, events) = NewTracker();
        t.Observe(Line("The rat0 is rushing at you madly."), DateTime.UtcNow);
        t.Observe(Line("The rat1 is rushing at you madly."), DateTime.UtcNow);
        Assert.True(t.InCombat);

        t.Observe(Line("You have fled by going south."), DateTime.UtcNow);

        Assert.False(t.InCombat);
        Assert.Equal(CombatEventKind.YouFled, events.Last().Kind);
    }

    [Fact]
    public void MultiNpcEncounter_GoatAndRam_StaysInCombatUntilBothResolved()
    {
        // Goat + ram attack together, goat flees, ram is killed, goat is followed and
        // re-engaged, then killed. CombatTracker only needs to get InCombat right across the
        // whole sequence, which this asserts end-to-end.
        var (t, inCombat, _) = NewTracker();
        var t0 = DateTime.UtcNow;
        t.Observe(Line("The billy goat is glaring at you madly."), t0);
        t.Observe(Line("The ram is glaring at you madly."), t0);
        Assert.True(t.InCombat);   // encounter 1 open (goat + ram both active)

        t.Observe(Line("The billy goat has fled by going west."), t0);
        Assert.True(t.InCombat);   // ram still active - goat fleeing must NOT end combat

        t.Observe(Line("You have killed the ram."), t0);
        Assert.False(t.InCombat);   // ram was the last one active - encounter 1 closes immediately

        // Re-engaging the fled goat - any time later, even moments after - opens a genuinely new
        // encounter, the same behaviour a real "follow the fled goat, then re-attack" produces.
        var t1 = t0 + TimeSpan.FromSeconds(10);
        t.Observe(Line("You attack the billy goat, using the falchion as a weapon."), t1);
        Assert.True(t.InCombat);   // encounter 2 (re-engaging the fled goat)

        t.Observe(Line("You have killed the billy goat."), t1);
        Assert.False(t.InCombat);   // closes immediately, same as encounter 1
        Assert.Equal([true, false, true, false], inCombat);
    }

    [Fact]
    public void WeaponSwitchAndBreak_ClassifiedAsPlainTextEvents()
    {
        // Confirmed offline these carry NO C1 wrapper at all - plain narrative text only.
        var (t, _, events) = NewTracker();
        t.Observe(Line("You attack the rat0, using the falchion as a weapon."), DateTime.UtcNow);
        t.Observe(Line("You drop your guard as you switch from using the falchion to the dagger0."), DateTime.UtcNow);
        t.Observe(Line("You are now using the dagger0 to fight!"), DateTime.UtcNow);
        t.Observe(Line("The dagger0 breaks to bits."), DateTime.UtcNow);
        t.Observe(Line("Your guard drops momentarily in your confusion."), DateTime.UtcNow);

        Assert.Equal(
            [CombatEventKind.FightStart, CombatEventKind.DroppedGuard, CombatEventKind.WeaponEquip,
             CombatEventKind.WeaponBroke, CombatEventKind.DroppedGuard],
            events.Select(e => e.Kind));

        var switchEvent = events[1];
        Assert.Equal("falchion", switchEvent.Weapon);
        var brokeEvent = events[3];
        Assert.Equal("dagger0", brokeEvent.Weapon);
    }

    [Fact]
    public void NpcWeaponEquip_ZombieSwitchesToFork_IsClassifiedAndKeepsCombatOpen()
    {
        // Confirmed live text (previously unseen in any capture): NPCs DO announce weapon use
        // explicitly, distinct from the per-tick "The X hits/misses you." lines which never name
        // a weapon.
        var (t, _, events) = NewTracker();
        t.Observe(Line("You attack the zombie, using the falchion as a weapon."), DateTime.UtcNow);
        t.Observe(Line("You miss the zombie."), DateTime.UtcNow);
        t.Observe(Line("The zombie misses you."), DateTime.UtcNow);
        t.Observe(Line("The zombie has started to use the fork to fight!"), DateTime.UtcNow);

        Assert.True(t.InCombat);
        var equipEvent = Assert.Single(events, e => e.Kind == CombatEventKind.NpcWeaponEquip);
        Assert.Equal("zombie", equipEvent.NpcName);
        Assert.Equal("fork", equipEvent.Weapon);
        Assert.Equal(CombatActor.Npc, equipEvent.Actor);
    }

    [Fact]
    public void FightEndOther_IsInformationalOnly_DoesNotCloseCombat()
    {
        // Verified against the full research capture: this line (108/108... 27 in the smaller
        // sample) always trails "The X has fled by going <dir>." for the SAME npc - NpcFled
        // already ends that fight. It carries no NPC name, so it must NOT itself force-close
        // combat (that would wrongly end OTHER still-active fights in a multi-NPC encounter).
        var (t, inCombat, events) = NewTracker();
        t.Observe(Line("The billy goat is glaring at you madly."), DateTime.UtcNow);
        t.Observe(Line("The ram is glaring at you madly."), DateTime.UtcNow);
        t.Observe(Line("The billy goat has fled by going west."), DateTime.UtcNow);
        t.Observe(Line("You can fight it no longer."), DateTime.UtcNow);

        Assert.True(t.InCombat);   // the ram is still actively fighting
        Assert.Equal(CombatEventKind.FightEndOther, events.Last().Kind);
    }

    [Fact]
    public void ForceEnd_ClosesOpenEncounterAndIsANoOpWhenNotInCombat()
    {
        var (t, inCombat, events) = NewTracker();
        // Not in combat yet - force-end must be a silent no-op (e.g. reset outside any fight).
        t.ForceEnd(DateTime.UtcNow);
        Assert.Empty(inCombat);
        Assert.Empty(events);

        t.Observe(Line("You attack the rat0, using the dagger0 as a weapon."), DateTime.UtcNow);
        t.ForceEnd(DateTime.UtcNow);
        Assert.False(t.InCombat);
        Assert.Equal([true, false], inCombat);
    }

    /// <summary>
    /// Every force-end reason emits <see cref="CombatEventKind.EncounterForceEnded"/>, never
    /// <see cref="CombatEventKind.FightEndOther"/>, which is byte-identical to MUD2's own pronoun
    /// form ("You can fight it no longer.") and must stay a no-op at every consumer. A reset does
    /// not cancel open fights, so sharing that event kind would leave the rail drawing the pack
    /// after a reset. The reason string is carried in the raw text for the clog and nothing
    /// branches on it.
    /// </summary>
    [Theory]
    [InlineData("world reset")]       // MudSession.OnWorldResetLanded
    [InlineData("reset/disconnect")]  // the default: logout (OnGameModeExited) and app exit (Dispose)
    public void ForceEnd_EmitsItsOwnEventKindWithTheReasonInTheRawText(string reason)
    {
        var (t, _, events) = NewTracker();
        t.Observe(Line("You attack the rat0, using the dagger0 as a weapon."), DateTime.UtcNow);

        t.ForceEnd(DateTime.UtcNow, reason);

        var end = events.Last();
        Assert.Equal(CombatEventKind.EncounterForceEnded, end.Kind);
        Assert.Null(end.NpcName);
        Assert.Equal($"(forced end: {reason})", end.RawText);
    }

    /// <summary>
    /// This wyvern frame is a reported fight at 5,201 points, with a pitchfork that broke; no
    /// capture holds it. The separate captured occurrence, which used a dagger, is replayed from
    /// its real bytes in <c>WyvernPoisonDeathReplayTests</c>. Two different fights, with the same
    /// three closing lines.
    ///
    /// <para>Either way MUD2 printed no "You have killed the X." at all, and the client matched none
    /// of the three lines that announce the end, so it claimed combat for the rest of the session.</para>
    /// </summary>
    [Fact]
    public void PoisonDeath_ClosesTheFight_EvenWithNoKillLine()
    {
        var (t, inCombat, events) = NewTracker();
        var t0 = DateTime.UtcNow;

        t.Observe(Line("The wyvern hits you (41/99)."), t0);
        t.Observe(Line("You hit the wyvern (10-14)."), t0.AddMilliseconds(10));
        t.Observe(Line("The pitchfork breaks to bits."), t0.AddMilliseconds(20));
        t.Observe(Line("You cannot use the pitchfork to fight now!"), t0.AddMilliseconds(30));
        t.Observe(Line("The wyvern looks covered in wounds."), t0.AddMilliseconds(40));
        Assert.True(t.InCombat);

        t.Observe(Line("The wyvern drops dead, poisoned..."), t0.AddMilliseconds(50));
        Assert.False(t.InCombat);   // the bug: this used to leave the encounter open forever

        var died = Assert.Single(events, e => e.Kind == CombatEventKind.NpcDied);
        Assert.Equal("wyvern", died.NpcName);
        Assert.Equal(CombatActor.Npc, died.Actor);   // whatever finished it, it was not our swing
        Assert.DoesNotContain(events, e => e.Kind == CombatEventKind.Kill);

        // The rest of the frame must not reopen anything, and must not re-report the death: the
        // fight is already closed, so "has just passed on." is back to being trailing prose.
        t.Observe(Line("The wyvern has just passed on."), t0.AddMilliseconds(60));
        t.Observe(Line("You can fight the wyvern no longer."), t0.AddMilliseconds(70));
        t.Observe(Line("(Persona saved on +26 = 5,201)."), t0.AddMilliseconds(80));

        Assert.False(t.InCombat);
        Assert.Equal([true, false], inCombat);
        Assert.Single(events, e => e.Kind == CombatEventKind.NpcDied);
    }

    /// <summary>A poison death names its creature, so in a pack it closes that fight only.</summary>
    [Fact]
    public void PoisonDeath_ClosesOnlyTheCreatureItNames()
    {
        var (t, _, events) = NewTracker();
        var t0 = DateTime.UtcNow;

        t.Observe(Line("The wyvern is snarling at you angrily."), t0);
        t.Observe(Line("The ram is glaring at you madly."), t0.AddMilliseconds(10));
        t.Observe(Line("The wyvern drops dead, poisoned..."), t0.AddMilliseconds(20));

        Assert.True(t.InCombat);   // the ram is still swinging
        Assert.Equal("wyvern", Assert.Single(events, e => e.Kind == CombatEventKind.NpcDied).NpcName);
    }

    /// <summary>
    /// Something else in the room dying of poison is not this fight's business. Mirrors the
    /// NpcHealth rule: a line about a creature the player never engaged must not open a fight
    /// against it - a phantom opponent on the panel is worse than a missing one in a permadeath game.
    /// </summary>
    [Fact]
    public void PoisonDeath_OfAnUnengagedCreature_IsIgnored()
    {
        var (t, inCombat, events) = NewTracker();
        t.Observe(Line("The rat3 drops dead, poisoned..."), DateTime.UtcNow);

        Assert.False(t.InCombat);
        Assert.Empty(inCombat);
        Assert.Empty(events);
    }

    /// <summary>
    /// "The X has just passed on." is MUD2's last word on any death however caused. It stays
    /// trailing prose after a matched end (asserted in
    /// <see cref="Kill_ThenUnrelatedNpcEngagesMomentsLater_StartsANewEncounter"/>), but if the fight
    /// is somehow still open when it arrives, we missed the real terminator and this closes it.
    /// </summary>
    [Fact]
    public void PassedOn_ClosesAFightThatIsSomehowStillOpen()
    {
        var (t, inCombat, events) = NewTracker();
        var t0 = DateTime.UtcNow;

        t.Observe(Line("You attack the wyvern, using the pitchfork as a weapon."), t0);
        t.Observe(Line("The wyvern has just passed on."), t0.AddMilliseconds(10));

        Assert.False(t.InCombat);
        Assert.Equal([true, false], inCombat);
        Assert.Equal("wyvern", Assert.Single(events, e => e.Kind == CombatEventKind.NpcDied).NpcName);
    }

    /// <summary>
    /// The named form of the case-3 trailing line carries a creature name, so it can close that one
    /// fight - and only that one.
    /// </summary>
    [Fact]
    public void FightEndOther_NamedForm_ClosesTheFightItNames()
    {
        var (t, inCombat, events) = NewTracker();
        var t0 = DateTime.UtcNow;

        t.Observe(Line("You attack the wyvern, using the pitchfork as a weapon."), t0);
        t.Observe(Line("The ram is glaring at you madly."), t0.AddMilliseconds(10));
        t.Observe(Line("You can fight the wyvern no longer."), t0.AddMilliseconds(20));

        Assert.True(t.InCombat);   // the ram is untouched by a line that named the wyvern
        var ended = Assert.Single(events, e => e.Kind == CombatEventKind.FightEndOther);
        Assert.Equal("wyvern", ended.NpcName);

        t.Observe(Line("You can fight the ram no longer."), t0.AddMilliseconds(30));
        Assert.False(t.InCombat);
        Assert.Equal([true, false], inCombat);
    }

    /// <summary>
    /// A named fight-end for a creature we are not fighting reports no name: we ignore it if we
    /// are not fighting that npc.
    ///
    /// <para>The case is a frame stacking several end messages where this one lands after another has
    /// already closed the fight - the captured wyvern frame exactly, where the poison death closes it
    /// two lines earlier. Downstream it matters because both consumers get-or-CREATE a fight bucket
    /// from a name on this event, and FightHistoryRecorder has no in-combat guard, so an unverified
    /// name becomes a persisted zero-swing row: a second fight against that creature that never
    /// happened. See
    /// FightHistoryRecorderTests.TrailingFightEndAfterTheEncounterClosed_WritesNoSecondRow.</para>
    ///
    /// <para>The line itself still reaches consumers with its full text, so the observation survives
    /// even when the name it could not vouch for is dropped.</para>
    /// </summary>
    [Fact]
    public void FightEndOther_NamingACreatureWeAreNotFighting_ReportsNoName()
    {
        var (t, _, events) = NewTracker();
        var t0 = DateTime.UtcNow;

        t.Observe(Line("You attack the wyvern, using the pitchfork as a weapon."), t0);
        t.Observe(Line("You can fight the goat no longer."), t0.AddSeconds(1));

        Assert.True(t.InCombat);   // the wyvern's fight is untouched
        var ended = Assert.Single(events, e => e.Kind == CombatEventKind.FightEndOther);
        Assert.Null(ended.NpcName);
        Assert.Equal("You can fight the goat no longer.", ended.RawText);   // the observation survives
    }

    /// <summary>
    /// The gendered pronoun forms appear in the captures (him 4, her 1). They name nobody, so they
    /// stay informational exactly as "it" does - but they must at least be classified.
    /// </summary>
    [Theory]
    [InlineData("You can fight it no longer.")]
    [InlineData("You can fight him no longer.")]
    [InlineData("You can fight her no longer.")]
    public void FightEndOther_PronounForms_AreClassifiedAndCloseNothing(string line)
    {
        var (t, _, events) = NewTracker();
        var t0 = DateTime.UtcNow;

        t.Observe(Line("You attack the wyvern, using the pitchfork as a weapon."), t0);
        t.Observe(Line(line), t0.AddMilliseconds(10));

        Assert.True(t.InCombat);
        var ended = Assert.Single(events, e => e.Kind == CombatEventKind.FightEndOther);
        Assert.Null(ended.NpcName);
    }

    /// <summary>
    /// The backstop for every fight-end phrasing nobody has found yet: in MUD2 you cannot walk out
    /// of a fight, so a room change proves the fight is over. It announces itself in the event's
    /// raw text, because each time it fires there is an unmatched line to go and find.
    /// </summary>
    [Fact]
    public void NoteRoomChanged_ClosesAnOpenEncounter_AndSaysThatIsWhatHappened()
    {
        var (t, inCombat, events) = NewTracker();
        var t0 = DateTime.UtcNow;

        // Not in combat: a silent no-op, since the player walks between rooms all day.
        t.NoteRoomChanged(t0);
        Assert.Empty(inCombat);
        Assert.Empty(events);

        t.Observe(Line("You attack the wyvern, using the pitchfork as a weapon."), t0.AddSeconds(1));
        t.NoteRoomChanged(t0.AddSeconds(2));

        Assert.False(t.InCombat);
        Assert.Equal([true, false], inCombat);
        // Distinct from the reset/disconnect wording, so a clog says which backstop fired.
        Assert.Equal("(forced end: room changed)", events.Last().RawText);
        // The fourth force-end path, and the one that fires in ordinary play rather than at a session
        // boundary - so it is the most likely to expose a consumer still expecting a FightEndOther
        // here. See ForceEnd_EmitsItsOwnEventKindWithTheReasonInTheRawText.
        Assert.Equal(CombatEventKind.EncounterForceEnded, events.Last().Kind);
    }

    /// <summary>
    /// The wording we have never seen. Verified on the wire (session-rec...20260826-134435) that the
    /// server codes its fight ends 08.12 even when the sentence is one no regex here knows, so with
    /// one creature engaged there is no ambiguity about which fight just ended.
    /// </summary>
    [Fact]
    public void CodedFightEnd_WithAnUnknownWording_ClosesTheSoleActiveFight()
    {
        var (t, inCombat, events) = NewTracker();
        var t0 = DateTime.UtcNow;

        t.Observe(Line("You attack the wyvern, using the pitchfork as a weapon."), t0);
        t.Observe(FightEndLine("The wyvern shrugs you off and stalks away, unimpressed."), t0.AddSeconds(1));

        Assert.False(t.InCombat);
        Assert.Equal([true, false], inCombat);
        var ended = Assert.Single(events, e => e.Kind == CombatEventKind.FightEndOther);
        Assert.Equal("wyvern", ended.NpcName);
        // The unrecognised sentence reaches the clog verbatim - which is how the next one gets found.
        Assert.Equal("The wyvern shrugs you off and stalks away, unimpressed.", ended.RawText);
    }

    /// <summary>
    /// Two creatures engaged and a fight-end line that names neither: the code says A fight ended,
    /// not WHICH, and guessing would file a pack fight's ending under the wrong creature. A wrong row
    /// is evidence; an open fight is only a bug.
    /// </summary>
    [Fact]
    public void CodedFightEnd_WithTwoActiveFights_ClosesNothing()
    {
        var (t, _, events) = NewTracker();
        var t0 = DateTime.UtcNow;

        t.Observe(Line("The billy goat is glaring at you madly."), t0);
        t.Observe(Line("The ram is glaring at you madly."), t0.AddSeconds(1));
        t.Observe(FightEndLine("You can fight it no longer."), t0.AddSeconds(2));

        Assert.True(t.InCombat);
        Assert.Null(Assert.Single(events, e => e.Kind == CombatEventKind.FightEndOther).NpcName);
    }

    /// <summary>
    /// The pronoun forms carry the same 08.12 code as the named one, so in a solo fight they close it
    /// after all - the code supplies the authority the sentence withholds. Plain text alone still must
    /// not: <see cref="FightEndOther_PronounForms_AreClassifiedAndCloseNothing"/> is the same line
    /// without the tag, and it closes nothing.
    /// </summary>
    [Fact]
    public void CodedPronounFightEnd_ClosesASoloFight()
    {
        var (t, inCombat, _) = NewTracker();
        var t0 = DateTime.UtcNow;

        t.Observe(Line("You attack the wyvern, using the pitchfork as a weapon."), t0);
        t.Observe(FightEndLine("You can fight him no longer."), t0.AddSeconds(1));

        Assert.False(t.InCombat);
        Assert.Equal([true, false], inCombat);
    }

    [Fact]
    public void UnrelatedLine_IsIgnored()
    {
        var (t, inCombat, events) = NewTracker();
        t.Observe(Line("A rat0 scurries in from the north."), DateTime.UtcNow);

        Assert.False(t.InCombat);
        Assert.Empty(inCombat);
        Assert.Empty(events);
    }

    /// <summary>
    /// MUD2 prints the trailing "You can fight it no longer." for the fight that just ended, in the
    /// same frame. Once a kill has already resolved one participant, "exactly one creature still
    /// active" no longer identifies which fight a trailing pronoun line is about, so it must not be
    /// read as closing the survivor.
    /// </summary>
    [Fact]
    public void PackKill_ThenTrailingPronounEnd_LeavesTheSurvivorFighting()
    {
        var (t, inCombat, _) = NewTracker();
        var t0 = DateTime.UtcNow;

        t.Observe(Line("The goat0 is glaring at you madly."), t0);
        t.Observe(Line("The ram1 is glaring at you madly."), t0.AddSeconds(1));
        t.Observe(Line("You have killed the goat0."), t0.AddSeconds(2));
        t.Observe(FightEndLine("You can fight it no longer."), t0.AddSeconds(2));

        Assert.True(t.InCombat);
        Assert.Equal([true], inCombat);   // one encounter, never closed and reopened

        // And the survivor's own end still closes it.
        t.Observe(Line("You have killed the ram1."), t0.AddSeconds(3));
        Assert.False(t.InCombat);
        Assert.Equal([true, false], inCombat);
    }

    /// <summary>
    /// The suppression lasts one FRAME, not the whole encounter: scoping it to the whole encounter
    /// would leave a later, genuinely unmatched end for the last survivor - frames later, with
    /// nothing else nearby - unrescued for the rest of the encounter, which is exactly the case
    /// SoleActiveOnFightEnd exists for.
    ///
    /// <para>The prompt is the boundary that lapses it, and MUD2's guarantee that every end prints
    /// inside a single frame is what makes that the right scope: an echo cannot be separated from the
    /// end it echoes by a prompt.</para>
    /// </summary>
    [Fact]
    public void CodedFightEnd_InALaterFrame_StillRescuesTheSurvivor()
    {
        var (t, inCombat, _) = NewTracker();
        var t0 = DateTime.UtcNow;

        t.Observe(Line("The goat0 is glaring at you madly."), t0);
        t.Observe(Line("The ram1 is glaring at you madly."), t0.AddSeconds(1));
        t.Observe(Line("You have killed the goat0."), t0.AddSeconds(2));
        t.Observe(FightEndLine("You can fight it no longer."), t0.AddSeconds(2));
        Assert.True(t.InCombat);   // the echo of goat0's end closes nothing

        // A later frame. The ram goes on fighting across it...
        t.Observe(PromptLine(), t0.AddSeconds(3));
        t.Observe(Line("You hit the ram1 (5-9)."), t0.AddSeconds(4));
        t.Observe(PromptLine(), t0.AddSeconds(5));

        // ...and then its fight ends in a wording nothing here matches, which only the C1 code
        // reveals. Nothing has ended in THIS frame, so the rescue is available again.
        t.Observe(FightEndLine("The ram1 loses interest and wanders off."), t0.AddSeconds(6));

        Assert.False(t.InCombat);
        Assert.Equal([true, false], inCombat);
    }

    /// <summary>The same trailing line for a creature that fled rather than died - the frame from
    /// docs/MUD2-fight-ends.md's water-snake case, with a second creature present.</summary>
    [Fact]
    public void PackFleeFailed_ThenTrailingPronounEnd_LeavesTheSurvivorFighting()
    {
        var (t, _, _) = NewTracker();
        var t0 = DateTime.UtcNow;

        t.Observe(Line("The water-snake5 is snarling at you angrily."), t0);
        t.Observe(Line("The water-snake1 is snarling at you angrily."), t0.AddSeconds(1));
        t.Observe(Line("The water-snake5 has fled by trying to go up."), t0.AddSeconds(2));
        t.Observe(FightEndLine("You can fight it no longer."), t0.AddSeconds(2));

        Assert.True(t.InCombat);
    }

    /// <summary>
    /// ParticipantJoined exists for consumers that need "every name that has ever actually fought"
    /// (the creature-value probe) without duplicating this class's own idea of what counts as new -
    /// see the event's own remarks. It must fire exactly once for a genuinely new name and NOT again
    /// for every defensive re-Begin() the same still-active name draws from later lines (YouHit,
    /// NpcHitsYou, NpcWeaponEquip all call Begin() unconditionally for a participant that may already
    /// be engaged).
    /// </summary>
    [Fact]
    public void ParticipantJoined_FiresOnceForAGenuinelyNewName_NotOnEveryDefensiveReBegin()
    {
        var t = new CombatTracker();
        var joined = new List<string>();
        t.ParticipantJoined += joined.Add;
        var now = DateTime.UtcNow;

        // Opens the fight - genuinely new.
        t.Observe(Line("You attack the rat17, using the axe0 as a weapon."), now);
        // Defensive Begin()s for the SAME name, from three different lines that all call it
        // unconditionally regardless of whether the name is already active.
        t.Observe(Line("You hit the rat17 (5-9)."), now.AddSeconds(1));
        t.Observe(Line("The rat17 hits you (84/90)."), now.AddSeconds(2));
        t.Observe(Line("The rat17 has started to use the fork to fight!"), now.AddSeconds(3));

        Assert.Equal(["rat17"], joined);
    }

    /// <summary>A pack member that never speaks an explicit aggro line still counts as newly
    /// joined the moment ANY line proves it is an active participant - the same gap YouHit's own
    /// remarks describe (a creature that "bares its razor-sharp incisors" is never matched as a
    /// fight-start, yet still trades blows once another named participant is killed).</summary>
    [Fact]
    public void ParticipantJoined_FiresForAPackMemberFirstSeenOnlyViaAHitLine()
    {
        var t = new CombatTracker();
        var joined = new List<string>();
        t.ParticipantJoined += joined.Add;
        var now = DateTime.UtcNow;

        t.Observe(Line("You attack the rat17, using the axe0 as a weapon."), now);
        // rat18 never announced itself - the first line naming it at all is a landed blow.
        t.Observe(Line("You hit the rat18 (5-9)."), now.AddSeconds(1));

        Assert.Equal(["rat17", "rat18"], joined);
    }

    /// <summary>A name that genuinely left (its fight ended) and later re-engages is a fresh
    /// participation, not a continuation - see EngagedFightFor's own remarks on why a re-engagement
    /// after e.g. a failed flee is a second, separate fight against the same name. ParticipantJoined
    /// fires again for it, so a consumer keyed off "has this session already learned a value for
    /// this literal name" (rather than off this event alone) is what prevents a redundant probe -
    /// this event's own job is only to say when the name is newly active.</summary>
    [Fact]
    public void ParticipantJoined_FiresAgainAfterAGenuineLeaveAndRejoin()
    {
        var t = new CombatTracker();
        var joined = new List<string>();
        t.ParticipantJoined += joined.Add;
        var now = DateTime.UtcNow;

        t.Observe(Line("The water-snake5 is snarling at you angrily."), now);
        t.Observe(Line("The water-snake5 has fled by trying to go up."), now.AddSeconds(1));
        t.Observe(FightEndLine("You can fight it no longer."), now.AddSeconds(1));
        Assert.False(t.InCombat);

        // Re-engages after the failed flee - a fresh participation of the same name.
        t.Observe(Line("You attack the water-snake5, using the axe0 as a weapon."), now.AddSeconds(2));

        Assert.Equal(["water-snake5", "water-snake5"], joined);
    }
}
