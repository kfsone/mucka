using System.Text.RegularExpressions;
using MudSharp.Models;

namespace MudSharp.Combat;

/// <summary>
/// Detects the start/end of combat encounters and classifies individual combat lines,
/// from plain rendered text only (<see cref="StyledLine.PlainText"/>) — no C1 tag inspection
/// is required at this layer because every event kind we care about is fully identifiable
/// from its prose, and three of them (WeaponEquip/WeaponBroke/DroppedGuard) carry no C1
/// wrapper at all in real captures (see RESEARCH-derived NOTES.md in tools/combat).
///
/// <para><b>What this class deliberately does NOT do:</b> it does not attempt to reconstruct
/// per-fight damage/DPS/duration aggregates — that analysis already exists offline in
/// tools/combat/reduce_combat.py against a full capture. This tracker's job is just to answer,
/// live, "are we in combat right now" and "what combat line just happened", so a ClogWriter can
/// record the raw stream faithfully for later offline analysis (same rules, same tool).</para>
///
/// <para><b>The "pass" tick:</b> MUD2 gives no textual signal at all for a combat tick where a
/// participant chose not to attack (confirmed against RESEARCH/mud2-multi-combat.jsonl — solo
/// combat can go silent for 90+ seconds with no hit/miss/pass message of any kind, until another
/// entity's presence "unsticks" the server's combat scheduler). We do not fabricate a synthetic
/// pass event; a clog's own event timestamps make the real silence visible for later statistical
/// analysis across many clogs.</para>
///
/// <para>Not internally locked: <see cref="Observe"/> and <see cref="ForceEnd"/> are called from
/// the parser Feed thread only. Consumers marshal events to their UI thread.</para>
/// </summary>
public sealed class CombatTracker
{
    // NPC-initiated aggro lines never name a weapon and use one of a handful of verb phrases
    // observed in the research capture. Best-effort: MUD2 may use aggro phrasing not yet seen.
    private static readonly Regex NpcAggroStart = new(
        @"^The (?<npc>.+?) is (?:looking at|glaring at|snarling at|moving towards|rushing at|advancing towards|approaching|staring at) you \w+\.*$",
        RegexOptions.Compiled);

    // Two forms, and the UNARMED one has no weapon clause at all:
    //   armed:   "You attack the thief, using the falchion as a weapon."
    //   unarmed: "You attack the thief."
    // Only the armed form used to be matched, so opening a fight bare-handed did not start an
    // encounter here at all - it limped along until YouHit's defensive Begin() picked it up. That
    // also swallowed any "use <weapon>" issued between the attack and the first blow, because the
    // weapon-equip line had no open encounter to attach to and the readout still said "unarmed".
    // Two patterns rather than one with an optional weapon clause: with a lazy npc group and the
    // clause optional, the engine prefers skipping the optional group and swallows ", using the X
    // as a weapon" into the npc name itself. Matched armed-first so the specific form wins.
    private static readonly Regex PlayerAttackStart = new(
        @"^You attack the (?<npc>.+?), using the (?<weapon>.+?) as a weapon\.$", RegexOptions.Compiled);
    private static readonly Regex PlayerAttackStartUnarmed = new(
        @"^You attack the (?<npc>.+?)\.$", RegexOptions.Compiled);
    private static readonly Regex YouHit = new(
        @"^You hit the (?<npc>.+?) \((?<lo>\d+)-(?<hi>\d+)\)\.$", RegexOptions.Compiled);
    private static readonly Regex YouMiss = new(@"^You miss the (?<npc>.+?)\.$", RegexOptions.Compiled);
    private static readonly Regex NpcHitsYou = new(
        @"^The (?<npc>.+?) hits you \((?<cur>\d+)/(?<max>\d+)\)\.$", RegexOptions.Compiled);
    private static readonly Regex NpcMissesYou = new(@"^The (?<npc>.+?) misses you\.$", RegexOptions.Compiled);
    private static readonly Regex WithdrawOffer = new(
        @"^You offer to withdraw from your fight with the (?<npc>.+?)\.$", RegexOptions.Compiled);
    private static readonly Regex YouKilled = new(@"^You have killed the (?<npc>.+?)\.$", RegexOptions.Compiled);
    private static readonly Regex NpcKilledYou = new(@"^The (?<npc>.+?) has killed you\.$", RegexOptions.Compiled);

    // Non-fightbrief ("narrative") death line — confirmed live against a real capture where the
    // player never enabled fightbrief for that character. "someone" replaces the NPC's name
    // whenever the player is blind at the moment of death (also confirmed live: a vampire that
    // cast a blindness spell mid-fight). See NpcKilledYouNarrative handling in Observe below for
    // how the anonymous case is resolved back to a real NPC name when possible.
    private static readonly Regex NpcKilledYouNarrative = new(
        @"^You have been killed by (?:the (?<npc>.+?)|(?<anon>someone))\.$", RegexOptions.Compiled);
    private static readonly Regex MutualWithdraw = new(
        @"^The (?<npc>.+?) withdraws from your fight, and so do you\.$", RegexOptions.Compiled);
    private static readonly Regex NpcFled = new(
        @"^The (?<npc>.+?) has fled by going \w+\.$", RegexOptions.Compiled);
    private static readonly Regex YouFled = new(@"^You have fled by going \w+\.$", RegexOptions.Compiled);
    private static readonly Regex FightEndOther = new(@"^You can fight it no longer\.$", RegexOptions.Compiled);
    private static readonly Regex WeaponEquip = new(
        @"^You are now using the (?<weapon>.+?) to fight!$", RegexOptions.Compiled);
    private static readonly Regex NpcWeaponEquip = new(
        @"^The (?<npc>.+?) has started to use the (?<weapon>.+?) to fight!$", RegexOptions.Compiled);
    private static readonly Regex WeaponSwitch = new(
        @"^You drop your guard as you switch from using the (?<from>.+?) to the (?<to>.+?)\.$", RegexOptions.Compiled);
    private static readonly Regex WeaponBroke = new(@"^The (?<weapon>.+?) breaks to bits\.$", RegexOptions.Compiled);

    /// <summary>The wield-refusal line. Two very different causes share it:
    ///   1. the weapon just broke, so it no longer exists to fight with (observed live: "The dagger0
    ///      breaks to bits." immediately followed by this), and
    ///   2. MUD2 REFUSING a wield because the player cannot handle that weapon right now - the
    ///      hidden gate on effective strength, which is itself depressed by carried weight and, per
    ///      the owner, by low stamina.
    /// Cause 2 is the only direct evidence of that gate MUD2 ever emits, and until now nothing
    /// parsed this line at all (the sole reference in the whole project was a dead regex at
    /// tools/combat/reduce_combat.py:77, defined and never called), so the research corpus contains
    /// ZERO observations of it. Recording the refusal together with the stats at that instant is
    /// what would let the threshold be bracketed. See MECHANICS_NOTES.md.
    /// The two causes are not distinguishable from this line alone; a break arriving immediately
    /// before it is the only signal, and the consumer decides what to make of that.</summary>
    private static readonly Regex WeaponUnusable = new(
        @"^You cannot use the (?<weapon>.+?) to fight now!$", RegexOptions.Compiled);
    private static readonly Regex GuardConfusion = new(
        @"^Your guard drops momentarily in your confusion\.$", RegexOptions.Compiled);

    // NPC instance names currently engaged (case-insensitive) — non-empty implies InCombat,
    // but InCombat can also stay true with an empty _active during a post-kill grace window
    // (see _pendingKillGraceUntil below).
    private readonly HashSet<string> _active = new(StringComparer.OrdinalIgnoreCase);

    // A kill that empties _active doesn't necessarily end the encounter: pack fights routinely
    // have NPCs that haven't traded a blow yet (still approaching/aggroing) when the currently-
    // tracked participant dies. The offline ground truth (tools/combat/reduce_combat.py,
    // KILL_GRACE_MS=5000) confirmed this against the full research capture: a 5-second grace
    // window after a kill-caused close, during which a new participant joining continues the
    // SAME encounter rather than opening a new one. Flee/withdraw closes get no such grace —
    // those are decisive endings and close immediately (matches reduce_combat.py's "explicit"
    // vs "kill" pending-end modes).
    private static readonly TimeSpan KillGrace = TimeSpan.FromMilliseconds(5000);
    private DateTime? _pendingKillGraceSince;
    private bool _encounterOpen;

    public bool InCombat => _encounterOpen;

    /// <summary>True while the encounter is only being kept open by the post-kill grace window
    /// (see <see cref="KillGrace"/>) — every tracked NPC is dead/gone but the window hasn't
    /// lapsed yet. Distinct from <see cref="InCombat"/> so a UI can dim its combat indicator
    /// instead of showing full "actively fighting" during this tail period.</summary>
    public bool IsInGracePeriod => _pendingKillGraceSince != null;

    /// <summary>Fires whenever <see cref="InCombat"/> flips (true = encounter started).</summary>
    public event Action<bool>? InCombatChanged;

    /// <summary>Fires whenever <see cref="IsInGracePeriod"/> flips.</summary>
    public event Action<bool>? GracePeriodChanged;

    /// <summary>Fires for every classified combat line, in order, while (or just as) InCombat.</summary>
    public event Action<CombatEvent>? EventOccurred;

    /// <summary>
    /// Classify one completed line. Cheap no-op for the overwhelming majority of lines
    /// (a plain-text prefix check would help further, but regex-per-candidate is already
    /// negligible next to network I/O — see EffectTracker for the equivalent trade-off).
    /// </summary>
    public void Observe(StyledLine line, DateTime timestampUtc)
    {
        ExpireKillGrace(timestampUtc);

        var text = line.PlainText;
        if (string.IsNullOrEmpty(text))
            return;

        Match m;
        if ((m = PlayerAttackStart.Match(text)).Success)
        {
            Begin(m.Groups["npc"].Value);
            Emit(timestampUtc, CombatEventKind.FightStart, CombatActor.Player, m.Groups["npc"].Value, m.Groups["weapon"].Value, null, null, text);
        }
        else if ((m = PlayerAttackStartUnarmed.Match(text)).Success)
        {
            // Bare-handed opening. Weapon is deliberately null: the fight really did start unarmed,
            // and a "use <weapon>" issued a moment later arrives as its own WeaponEquip event.
            Begin(m.Groups["npc"].Value);
            Emit(timestampUtc, CombatEventKind.FightStart, CombatActor.Player, m.Groups["npc"].Value, null, null, null, text);
        }
        else if ((m = NpcAggroStart.Match(text)).Success)
        {
            Begin(m.Groups["npc"].Value);
            Emit(timestampUtc, CombatEventKind.FightStart, CombatActor.Npc, m.Groups["npc"].Value, null, null, null, text);
        }
        else if ((m = YouHit.Match(text)).Success)
        {
            // A pack fight can have NPCs that never spoke an explicit aggro line (e.g. only a
            // "bares its razor-sharp incisors at you" join message we don't classify as a start)
            // yet still trade blows with the player once another named participant is killed.
            // Any hit/miss line is itself proof that NPC is an active combat participant, so it
            // must (re)join _active here — otherwise killing the one NPC that DID get an explicit
            // Begin() empties _active and spuriously closes/reopens the encounter mid-pack-fight.
            Begin(m.Groups["npc"].Value);
            Emit(timestampUtc, CombatEventKind.Hit, CombatActor.Player, m.Groups["npc"].Value, null,
                int.Parse(m.Groups["lo"].Value), int.Parse(m.Groups["hi"].Value), text);
        }
        else if ((m = YouMiss.Match(text)).Success)
        {
            Begin(m.Groups["npc"].Value);
            Emit(timestampUtc, CombatEventKind.Miss, CombatActor.Player, m.Groups["npc"].Value, null, null, null, text);
        }
        else if ((m = NpcHitsYou.Match(text)).Success)
        {
            Begin(m.Groups["npc"].Value);
            Emit(timestampUtc, CombatEventKind.HitByNpc, CombatActor.Npc, m.Groups["npc"].Value, null,
                int.Parse(m.Groups["cur"].Value), int.Parse(m.Groups["max"].Value), text);
        }
        else if ((m = NpcMissesYou.Match(text)).Success)
        {
            Begin(m.Groups["npc"].Value);
            Emit(timestampUtc, CombatEventKind.MissByNpc, CombatActor.Npc, m.Groups["npc"].Value, null, null, null, text);
        }
        else if ((m = WithdrawOffer.Match(text)).Success)
        {
            // An offer only — does not end the fight until the NPC's own line accepts it.
            Emit(timestampUtc, CombatEventKind.WithdrawOffer, CombatActor.Player, m.Groups["npc"].Value, null, null, null, text);
        }
        else if ((m = YouKilled.Match(text)).Success)
        {
            // Emit BEFORE End: End can flip InCombat to false and close out the encounter
            // (e.g. a ClogWriter listening to InCombatChanged) — the closing line itself must
            // still land inside that encounter's record, not be dropped after it's already shut.
            Emit(timestampUtc, CombatEventKind.Kill, CombatActor.Player, m.Groups["npc"].Value, null, null, null, text);
            End(m.Groups["npc"].Value, timestampUtc, isKill: true);
        }
        else if ((m = NpcKilledYou.Match(text)).Success)
        {
            Emit(timestampUtc, CombatEventKind.KilledByNpc, CombatActor.Npc, m.Groups["npc"].Value, null, null, null, text);
            // Player death ends the WHOLE encounter unconditionally, unlike an NPC kill (which
            // uses a grace window in case other pack participants are still engaged) — a dead
            // player cannot keep fighting anyone else in the same room. Using the ordinary
            // kill-grace End() here was a latent bug: death is routinely followed immediately by
            // a disconnect/quit with no further lines to expire the grace window, so the
            // encounter would linger open until ForceEnd's generic "reset/disconnect" close
            // instead of a clean, correctly-attributed KilledByNpc close.
            EndAll();
        }
        else if ((m = NpcKilledYouNarrative.Match(text)).Success)
        {
            // Non-fightbrief phrasing carries no per-line C1 hit/miss detail at all, so this may
            // be the ONLY combat line we can classify in an entire narrative-mode fight — treat
            // it as authoritative regardless. When blind, the game says "someone" instead of
            // naming the killer; best-effort resolve that back to the sole active participant
            // (this is exactly the live scenario that surfaced this gap: fighting a single
            // vampire that blinded then slept the player before landing the killing blow).
            //
            // CAUTION: "sole active participant" is a best-effort label, not a verified fact.
            // A blind player cannot see room arrivals/departures or other NPCs fleeing, so
            // _active.Count == 1 by our bookkeeping doesn't rule out an unseen second attacker
            // (another NPC, or another player) landing the actual blow. Callers/analysis should
            // treat this resolution as "most likely" whenever blindness was active, never as
            // ground truth — see MECHANICS_NOTES.md's "sole active NPC" caution.
            var npc = m.Groups["npc"].Success
                ? m.Groups["npc"].Value
                : _active.Count == 1 ? _active.First() : "someone";
            Emit(timestampUtc, CombatEventKind.KilledByNpc, CombatActor.Npc, npc, null, null, null, text);
            EndAll();
        }
        else if ((m = MutualWithdraw.Match(text)).Success)
        {
            Emit(timestampUtc, CombatEventKind.Withdrawn, CombatActor.Npc, m.Groups["npc"].Value, null, null, null, text);
            End(m.Groups["npc"].Value, timestampUtc, isKill: false);
        }
        else if ((m = NpcFled.Match(text)).Success)
        {
            Emit(timestampUtc, CombatEventKind.NpcFled, CombatActor.Npc, m.Groups["npc"].Value, null, null, null, text);
            End(m.Groups["npc"].Value, timestampUtc, isKill: false);
        }
        else if (YouFled.IsMatch(text))
        {
            // One flee command can end several simultaneous fights at once (confirmed offline:
            // a single flee line closed two concurrent rat fights) — close every active NPC.
            Emit(timestampUtc, CombatEventKind.YouFled, CombatActor.Player, null, null, null, null, text);
            EndAll();
        }
        else if (FightEndOther.IsMatch(text))
        {
            // Verified against the full research capture: every single occurrence of this line
            // (27/27) is a trailing acknowledgment that immediately follows "The X has fled by
            // going <dir>." for that SAME npc — NpcFled has already ended that fight. It carries
            // no NPC name, so treating it as its own independent terminator would risk closing
            // OTHER still-active fights in a multi-NPC encounter. Recorded as informational only
            // (no Begin/End/EndAll) — never authoritative for combat state on its own.
            Emit(timestampUtc, CombatEventKind.FightEndOther, CombatActor.Player, null, null, null, null, text);
        }
        else if ((m = WeaponSwitch.Match(text)).Success)
        {
            Emit(timestampUtc, CombatEventKind.DroppedGuard, CombatActor.Player, null, m.Groups["from"].Value, null, null, text);
        }
        else if (GuardConfusion.IsMatch(text))
        {
            Emit(timestampUtc, CombatEventKind.DroppedGuard, CombatActor.Player, null, null, null, null, text);
        }
        else if ((m = WeaponBroke.Match(text)).Success)
        {
            Emit(timestampUtc, CombatEventKind.WeaponBroke, CombatActor.Player, null, m.Groups["weapon"].Value, null, null, text);
        }
        else if ((m = WeaponUnusable.Match(text)).Success)
        {
            // Matched BEFORE WeaponEquip below purely for readability; the two patterns cannot
            // collide ("cannot use ... to fight now!" vs "are now using ... to fight!").
            Emit(timestampUtc, CombatEventKind.WeaponUnusable, CombatActor.Player, null, m.Groups["weapon"].Value, null, null, text);
        }
        else if ((m = WeaponEquip.Match(text)).Success)
        {
            // No Begin() here, deliberately: this line names no NPC, so there is nothing to open an
            // encounter AGAINST. It arrives between "You attack the thief." and the first blow, and
            // before the PlayerAttackStart fix above that attack line was not matched at all - so
            // the encounter did not exist yet and the weapon was dropped on the floor, leaving the
            // readout showing "unarmed" for the rest of the fight. With the unarmed attack form now
            // matched, the encounter is already open by the time this fires and the weapon lands on
            // it. (NpcWeaponEquip below DOES Begin(), because that line does name its NPC.)
            Emit(timestampUtc, CombatEventKind.WeaponEquip, CombatActor.Player, null, m.Groups["weapon"].Value, null, null, text);
        }
        else if ((m = NpcWeaponEquip.Match(text)).Success)
        {
            // Confirmed live: "The zombie has started to use the fork to fight!" mid-fight,
            // following ordinary miss/miss lines that named it, so it's already an active
            // participant — but Begin() defensively in case this is somehow the first line
            // naming that NPC (mirrors YouHit's own defensive Begin() for the same reason).
            Begin(m.Groups["npc"].Value);
            Emit(timestampUtc, CombatEventKind.NpcWeaponEquip, CombatActor.Npc, m.Groups["npc"].Value, m.Groups["weapon"].Value, null, null, text);
        }
    }

    /// <summary>Periodic time-only check for the post-kill grace window expiring. Observe() only
    /// runs ExpireKillGrace when a new line actually arrives, so a quiet final kill (no further
    /// server output for a while) leaves InCombat/IsInGracePeriod stale — sitting "fully in
    /// combat" until whatever line happens to show up next (confirmed live: a solo zombie kill
    /// stayed lit until an unrelated weather line arrived ~2-3s later). Callers should invoke
    /// this from a UI-side ~1 Hz tick so the grace window (and its dimmed-icon UI state) expires
    /// on its own even when the server goes quiet.</summary>
    public void Tick(DateTime nowUtc) => ExpireKillGrace(nowUtc);

    /// <summary>Force-close any open encounter without a matching end line (e.g. an auto-reset
    /// wiping the game state mid-fight, or logout/relog).</summary>
    public void ForceEnd(DateTime timestampUtc)
    {
        if (!InCombat)
            return;
        Emit(timestampUtc, CombatEventKind.FightEndOther, null, null, null, null, null, "(forced end: reset/disconnect)");
        _active.Clear();
        CloseEncounter();
    }

    /// <summary>Closes the encounter if a post-kill grace window (see <see cref="KillGrace"/>)
    /// has elapsed with no new participant joining. Called on every observed line, mirroring
    /// reduce_combat.py's per-record <c>_expire_pending_session</c> check.</summary>
    private void ExpireKillGrace(DateTime timestampUtc)
    {
        if (_pendingKillGraceSince is { } since && _active.Count == 0 && timestampUtc - since > KillGrace)
            CloseEncounter();
    }

    private void Begin(string npc)
    {
        // A new participant engaging cancels any pending post-kill grace close — the encounter
        // continues even though a moment ago _active briefly held no one.
        ClearGrace();
        _active.Add(npc);
        if (!_encounterOpen)
        {
            _encounterOpen = true;
            InCombatChanged?.Invoke(true);
        }
    }

    private void End(string npc, DateTime timestampUtc, bool isKill)
    {
        _active.Remove(npc);
        if (_active.Count != 0)
            return;

        if (isKill)
        {
            // Grace period: pack fights routinely have unengaged NPCs still aggroing when the
            // currently-tracked participant dies. Don't close the encounter yet — a new Begin()
            // within KillGrace continues it; ExpireKillGrace closes it once the window lapses.
            EnterGrace(timestampUtc);
        }
        else
        {
            // Flee/withdraw are decisive endings — no grace period.
            CloseEncounter();
        }
    }

    private void EndAll()
    {
        _active.Clear();
        CloseEncounter();
    }

    private void CloseEncounter()
    {
        ClearGrace();
        if (!_encounterOpen)
            return;
        _encounterOpen = false;
        InCombatChanged?.Invoke(false);
    }

    private void EnterGrace(DateTime timestampUtc)
    {
        var wasGrace = _pendingKillGraceSince != null;
        _pendingKillGraceSince = timestampUtc;
        if (!wasGrace)
            GracePeriodChanged?.Invoke(true);
    }

    private void ClearGrace()
    {
        var wasGrace = _pendingKillGraceSince != null;
        _pendingKillGraceSince = null;
        if (wasGrace)
            GracePeriodChanged?.Invoke(false);
    }

    private void Emit(DateTime ts, CombatEventKind kind, CombatActor? actor, string? npc, string? weapon,
        int? lo, int? hi, string raw)
        => EventOccurred?.Invoke(new CombatEvent(ts, kind, actor, npc, weapon, lo, hi, raw));
}
