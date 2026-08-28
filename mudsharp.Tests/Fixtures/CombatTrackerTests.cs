using MudSharp.Combat;
using MudSharp.Models;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// CombatTracker classification and encounter/fight boundary detection, using literal lines
/// observed in RESEARCH/mud2-multi-combat.jsonl (see tools/combat/NOTES.md for the full
/// catalogue). CombatTracker consumes StyledLine.PlainText directly, so tests build lines
/// straight from plain text — no protocol bytes/parser harness needed here.
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

    /// <summary>Reported live: a weapon broke mid-fight and the readout kept showing it equipped.
    /// "You cannot use the X to fight now!" was parsed nowhere - the only reference in the project
    /// was a dead regex in tools/combat/reduce_combat.py. It is also the wield-refusal line, the
    /// sole direct evidence MUD2 emits of the hidden effective-strength gate, so it must be both
    /// acted on (the player is now bare-handed) and recorded.</summary>
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

    /// <summary>Bare-handed openings have no weapon clause: "You attack the thief." Only the armed
    /// form used to be matched, so an unarmed attack opened no encounter at all.</summary>
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

    /// <summary>Regression, reported live: attack bare-handed, then "use staff" mid-fight, and the
    /// readout still said UNARMED. The unarmed attack line was unmatched, so no encounter existed
    /// when the weapon-equip line arrived and the weapon was discarded. The npc name must also not
    /// swallow the armed form's ", using the X as a weapon" clause - see the two-pattern comment on
    /// PlayerAttackStart.</summary>
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
        // Regression: a real ClogWriter listens to InCombatChanged to close its file and to
        // EventOccurred to write each line. If End() ran before Emit(), the closing Kill/Fled/
        // Withdrawn/etc. line would land after the writer already closed and be silently
        // dropped from the clog — exactly what was observed in a live clog missing its final
        // "You have killed the X." line. Assert Emit fires strictly before the false transition.
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

        Assert.False(t.InCombat);   // closed the instant the last active NPC died — no window
        Assert.Equal([true, false], inCombat);
        Assert.Equal(CombatEventKind.Kill, events.Last().Kind);
    }

    [Fact]
    public void Kill_ThenUnrelatedNpcEngagesMomentsLater_StartsANewEncounter()
    {
        // The owner's exact worked scenario: a solo rat dies, and a completely different rat
        // starts attacking a fraction of a second later — well within what used to be a
        // 5-second "pack straggler" grace window. It is NOT the same pack fight (rat21 never
        // engaged while rat17 was still alive), and per the owner, once the combatant count
        // reaches 0 the fight is over, full stop: whatever attacks next opens a genuinely new
        // encounter. Any need to keep capturing trailing prose after the close belongs to
        // logging (ClogWriter's tail capture — see ClogWriterTests), never to this class.
        var (t, inCombat, events) = NewTracker();
        var t0 = DateTime.UtcNow;

        t.Observe(Line("You hit the rat17 (15-19)."), t0);
        t.Observe(Line("You have killed the rat17."), t0 + TimeSpan.FromMilliseconds(50));

        Assert.False(t.InCombat);   // rat17's encounter is already fully closed

        // Pure trailing prose CombatTracker never classifies (score/death confirmation) — must
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
        // Regression: player death must close the WHOLE encounter unconditionally — a dead
        // player cannot keep fighting anyone else in the same room, even if other NPCs (besides
        // the killer) are still active. Using the ordinary single-NPC End() here would leave the
        // ram dangling in _active with nothing left to ever remove it.
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
        // fightbrief-format Hit/Miss/HitByNpc line for the whole fight — this death line may be
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
        // to sleep, then landed the killing blow while blind — MUD2 anonymizes the killer to
        // "someone" in narrative mode whenever the player is blind. With exactly one active
        // participant, best-effort resolve "someone" back to that NPC rather than losing
        // attribution entirely.
        var (t, inCombat, events) = NewTracker();
        t.Observe(Line("The vampire is looking at you hatefully."), DateTime.UtcNow);
        t.Observe(Line("You have been killed by someone."), DateTime.UtcNow);

        Assert.False(t.InCombat);
        Assert.Equal([true, false], inCombat);
        Assert.Equal(CombatEventKind.KilledByNpc, events.Last().Kind);
        Assert.Equal("vampire", events.Last().NpcName);
    }

    [Fact]
    public void NarrativeDeath_AnonymizedWithMultipleActiveNpcs_KeepsAnonymousName()
    {
        // With more than one active participant there's no safe way to guess which NPC actually
        // landed the blow — keep the literal "someone" rather than guessing wrong.
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
        // Per the user: an NPC that cannot move reports that it TRIED to go somewhere, and is still
        // in the room. Treating that as a flee would close a live fight and (once pursuit lands) walk
        // the player out of it. The NpcFled regex requires the literal "has fled by going", so this
        // already does not match — pinned here because that was correct by luck rather than by design,
        // and pursuit work is about to depend on the distinction.
        // See tools/combat/MECHANICS_NOTES.md "Fleeing NPCs and pursuit".
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

    [Fact]
    public void YouFlee_ClosesEveryActiveFightAtOnce()
    {
        // User's worked scenario has a single-flee-closes-two-concurrent-fights precedent
        // (confirmed offline: one "You have fled..." line ended two simultaneous rat fights).
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
        // User's exact worked example: goat + ram attack together, goat flees, ram is killed,
        // goat is followed and re-engaged, then killed — 3 encounters/4 fights in the FULL
        // analytical model (tools/combat), but CombatTracker only needs to get InCombat right
        // across the whole sequence, which this asserts end-to-end.
        var (t, inCombat, _) = NewTracker();
        var t0 = DateTime.UtcNow;
        t.Observe(Line("The billy goat is glaring at you madly."), t0);
        t.Observe(Line("The ram is glaring at you madly."), t0);
        Assert.True(t.InCombat);   // encounter 1 open (goat + ram both active)

        t.Observe(Line("The billy goat has fled by going west."), t0);
        Assert.True(t.InCombat);   // ram still active — goat fleeing must NOT end combat

        t.Observe(Line("You have killed the ram."), t0);
        Assert.False(t.InCombat);   // ram was the last one active — encounter 1 closes immediately

        // Re-engaging the fled goat — any time later, even moments after — opens a genuinely new
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
        // Confirmed offline these carry NO C1 wrapper at all — plain narrative text only.
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
        // sample) always trails "The X has fled by going <dir>." for the SAME npc — NpcFled
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
        // Not in combat yet — force-end must be a silent no-op (e.g. reset outside any fight).
        t.ForceEnd(DateTime.UtcNow);
        Assert.Empty(inCombat);
        Assert.Empty(events);

        t.Observe(Line("You attack the rat0, using the dagger0 as a weapon."), DateTime.UtcNow);
        t.ForceEnd(DateTime.UtcNow);
        Assert.False(t.InCombat);
        Assert.Equal([true, false], inCombat);
    }

    /// <summary>
    /// The wyvern frame as the OWNER PASTED IT (2026-08-26) — his own report of an earlier fight at
    /// 5,201 points, with a pitchfork that broke; no capture holds it. The separate captured
    /// occurrence, which used a dagger, is replayed from its real bytes in
    /// <c>WyvernPoisonDeathReplayTests</c>. Two different fights, same three closing lines, and worth
    /// keeping straight: the first write-up of this fix merged them into one "verbatim" transcript.
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
    /// against it — a phantom opponent on the panel is worse than a missing one in a permadeath game.
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
    /// fight — and only that one.
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
    /// A named fight-end for a creature we are not fighting reports no name. Per the owner: we ignore
    /// it if we are not fighting that npc - simple.
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
    /// The gendered pronoun forms appear in the captures (him 4, her 1) and only "it" was matched,
    /// so they were unrecognised lines. They name nobody, so they stay informational exactly as "it"
    /// does — but they must at least be classified.
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
    /// of a fight (owner), so a room change proves the fight is over. It announces itself in the
    /// event's raw text, because each time it fires there is an unmatched line to go and find.
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
    /// Regression, reported in play: the combat metronome fell silent for most of a fight, because a
    /// pack fight's encounter was being closed and reopened on every kill.
    ///
    /// <para>MUD2 prints the trailing "You can fight it no longer." for the fight that just ended, in
    /// the same frame. SoleActiveOnFightEnd read "exactly one creature still active" as "so this line
    /// must mean that one" - but the other had been removed moments earlier by the kill, so the line
    /// was about the corpse and the survivor got closed while it was still swinging. The encounter
    /// then reopened on its next blow, and since the tick phase is only known from an encounter's
    /// first swing, the metronome restarted each time and clicked a handful of times in two minutes.</para>
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
    /// The suppression lasts one FRAME, not the whole encounter. A review traced the over-broad
    /// version: a pack where one creature died early meant the last survivor's genuinely unmatched
    /// end - frames later, with nothing else nearby - went unrescued for the rest of the encounter,
    /// which is exactly the case SoleActiveOnFightEnd exists for.
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
    /// FIGHT-ENDS.md's water-snake case, with a second creature present.</summary>
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

}
