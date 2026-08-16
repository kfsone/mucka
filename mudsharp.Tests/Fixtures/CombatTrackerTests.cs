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
        //
        // A kill only closes the encounter once the post-kill grace window (see
        // CombatTracker.KillGrace) elapses with no new participant joining — simulate that by
        // observing a later, unrelated (non-matching) line well past the grace window.
        var t = new CombatTracker();
        var order = new List<string>();
        t.EventOccurred += e => order.Add($"event:{e.Kind}");
        t.InCombatChanged += v => order.Add($"incombat:{v}");
        var t0 = DateTime.UtcNow;

        t.Observe(Line("You attack the rat0, using the dagger0 as a weapon."), t0);
        t.Observe(Line("You have killed the rat0."), t0);
        t.Observe(Line("[PROMPT]"), t0 + TimeSpan.FromSeconds(6));

        Assert.Equal(
            ["incombat:True", "event:FightStart", "event:Kill", "incombat:False"],
            order);
    }

    [Fact]
    public void Kill_EndsCombat()
    {
        var (t, inCombat, events) = NewTracker();
        var t0 = DateTime.UtcNow;
        t.Observe(Line("You attack the rat0, using the dagger0 as a weapon."), t0);
        t.Observe(Line("You have killed the rat0."), t0);

        Assert.True(t.InCombat);   // still open: within the post-kill grace window
        t.Observe(Line("[PROMPT]"), t0 + TimeSpan.FromSeconds(6));

        Assert.False(t.InCombat);
        Assert.Equal([true, false], inCombat);
        Assert.Equal(CombatEventKind.Kill, events.Last().Kind);
    }

    [Fact]
    public void Kill_WithinGraceWindow_NewParticipantContinuesSameEncounter()
    {
        // The bug this grace window fixes: a pack fight where several NPCs never got an
        // explicit aggro/start line (only e.g. "bares its razor-sharp incisors at you", which
        // CombatTracker doesn't classify as a start) still trade blows once the one NPC that DID
        // start explicitly is killed. Confirmed against the real capture (tools/combat,
        // KILL_GRACE_MS=5000): a new participant engaging within 5s of a kill-caused close must
        // continue the SAME encounter, not open a new one.
        var (t, inCombat, _) = NewTracker();
        var t0 = DateTime.UtcNow;
        t.Observe(Line("You attack the rat0, using the dagger0 as a weapon."), t0);
        t.Observe(Line("You have killed the rat0."), t0 + TimeSpan.FromSeconds(1));

        Assert.True(t.InCombat);   // pending grace, not yet closed

        t.Observe(Line("The rat6 is looking at you furiously."), t0 + TimeSpan.FromSeconds(3));

        Assert.True(t.InCombat);
        Assert.Equal([true], inCombat);   // never actually flipped false — same encounter
    }


    [Fact]
    public void Kill_SetsGracePeriodUntilTickExpiresIt_WithNoFurtherLineRequired()
    {
        // Regression: a solo kill followed by total server silence (no more lines of any kind)
        // used to leave InCombat/IsInGracePeriod stale forever, since ExpireKillGrace previously
        // only ran from inside Observe(). Confirmed live: a solo zombie kill stayed "fully in
        // combat" for ~2-3s until an unrelated weather line happened to arrive. Tick() lets a
        // UI-side ~1 Hz poll expire the grace window on its own, with no server line needed.
        var (t, inCombat, _) = NewTracker();
        var grace = new List<bool>();
        t.GracePeriodChanged += grace.Add;
        var t0 = DateTime.UtcNow;

        t.Observe(Line("You attack the zombie0, using the falchion as a weapon."), t0);
        t.Observe(Line("You have killed the zombie0."), t0 + TimeSpan.FromSeconds(1));

        Assert.True(t.InCombat);
        Assert.True(t.IsInGracePeriod);
        Assert.Equal([true], grace);

        t.Tick(t0 + TimeSpan.FromSeconds(3));   // still within the 5s grace window
        Assert.True(t.InCombat);
        Assert.True(t.IsInGracePeriod);

        t.Tick(t0 + TimeSpan.FromSeconds(7));   // grace window lapsed — no new line required
        Assert.False(t.InCombat);
        Assert.False(t.IsInGracePeriod);
        Assert.Equal([true, false], inCombat);
        Assert.Equal([true, false], grace);
    }

    [Fact]
    public void NpcKillsYou_ClosesEncounterImmediatelyWithNoGraceWindow()
    {
        // Regression: player death must close the WHOLE encounter unconditionally, unlike an
        // NPC kill (which grants a grace window in case other pack NPCs are still engaged) —
        // a dead player cannot keep fighting. Before this fix, NpcKilledYou reused the ordinary
        // kill-grace End(), so a death immediately followed by a disconnect (the common real
        // case — the player quits from the death menu) never expired the grace window and the
        // encounter lingered open until a generic ForceEnd "reset/disconnect" close instead of a
        // clean, correctly-attributed KilledByNpc close.
        var (t, inCombat, events) = NewTracker();
        t.Observe(Line("The vampire is looking at you hatefully."), DateTime.UtcNow);
        t.Observe(Line("The vampire has killed you."), DateTime.UtcNow);

        Assert.False(t.InCombat);   // closed immediately, no grace window
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
        Assert.True(t.InCombat);   // still open: within the post-kill grace window

        // Re-engaging the fled goat well past the grace window opens a genuinely new encounter —
        // this is the same behaviour a real "follow the fled goat, then re-attack" delay produces.
        var t1 = t0 + TimeSpan.FromSeconds(10);
        t.Observe(Line("You attack the billy goat, using the falchion as a weapon."), t1);
        Assert.True(t.InCombat);   // encounter 2 (re-engaging the fled goat)

        t.Observe(Line("You have killed the billy goat."), t1);
        Assert.True(t.InCombat);   // still open: within the post-kill grace window
        t.Observe(Line("[PROMPT]"), t1 + TimeSpan.FromSeconds(6));
        Assert.False(t.InCombat);
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

    [Fact]
    public void UnrelatedLine_IsIgnored()
    {
        var (t, inCombat, events) = NewTracker();
        t.Observe(Line("A rat0 scurries in from the north."), DateTime.UtcNow);

        Assert.False(t.InCombat);
        Assert.Empty(inCombat);
        Assert.Empty(events);
    }
}
