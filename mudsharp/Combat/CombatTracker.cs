using System.Text.RegularExpressions;
using MudSharp.Models;

namespace MudSharp.Combat;

/// <summary>
/// Detects the start/end of combat encounters and classifies individual combat lines, mostly from
/// plain rendered text (<see cref="StyledLine.PlainText"/>) — it has to be mostly, because several of
/// the lines that matter carry no C1 wrapper at all in real captures: WeaponEquip, WeaponBroke and
/// DroppedGuard (see RESEARCH-derived NOTES.md in tools/combat), and — verified 2026-08-26 — the
/// death lines "The X drops dead, poisoned..." and "The X has just passed on.", which arrive as bare
/// untagged text at base scope.
///
/// <para>The prose is what identifies WHICH creature, and there is no substitute for it. But the one
/// tag this class does read, <see cref="LineKind.FightEnd"/> (C08.10/11/12), is what says a fight
/// ended AT ALL, and it has been right every time the prose was not: three separate wordings have
/// slipped past the regexes below over the project's life, each leaving a fight open until logout,
/// and all three were correctly coded on the wire. Authority from the code, identity from the text.</para>
///
/// <para><b>What this class deliberately does NOT do:</b> it does not attempt to reconstruct
/// per-fight damage/DPS/duration aggregates — that analysis already exists offline in
/// tools/combat/reduce_combat.py against a full capture. This tracker's job is just to answer,
/// live, "are we in combat right now" and "what combat line just happened", so a ClogWriter can
/// record the raw stream faithfully for later offline analysis (same rules, same tool).</para>
///
/// <para><b>The ends (owner, 2026-08-19; an eighth found 2026-08-26).</b> Every end is printed inside
/// a SINGLE frame - one prompt to the next - always. That guarantee is load-bearing here: it is the
/// reason this class needs no timer, no idle window and no "lull" state to decide a fight is over.
/// The terminator line is never separated from its fight by a frame boundary, so whatever the frame
/// says is the whole answer, and a fight that has not been ended by a line in the frame really is
/// still running.
///
/// <list type="table">
/// <item><term>1. Kill</term><description>"You have killed the X." - per-creature.</description></item>
/// <item><term>2. Creature fled</term><description>"The X has fled by going &lt;dir&gt;." - per-creature; it left the room.</description></item>
/// <item><term>3. Creature flee failed</term><description>"The X has fled by trying to go &lt;dir&gt;." - per-creature; it did NOT leave, but the fight is over.</description></item>
/// <item><term>4. Player fled</term><description>"You have fled by going &lt;dir&gt;." - zeroes the fight count.</description></item>
/// <item><term>5. Player flee failed</term><description>"You have fled by trying to go &lt;dir&gt;." - zeroes the fight count anyway.</description></item>
/// <item><term>6. Withdraw</term><description>"The X withdraws from your fight, and so do you." - per-creature; an agreement with ONE creature.</description></item>
/// <item><term>7. Player died</term><description>"The X has killed you." / "You have been killed by ..." - zeroes the fight count. Permadeath.</description></item>
/// <item><term>8. You lose the creature</term><description>"The X drops dead, poisoned..." - per-creature; it died without the player landing the last blow, so no kill line is printed at all. An OPEN family (see <see cref="FightOutcome.NoMore"/>): poison is the member observed, other causes are expected to be worded differently.</description></item>
/// </list>
///
/// <para>1-3, 6 and 8 close only the creature they name. 4, 5 and 7 are the player's own state changing
/// rather than one opponent's, so they return the fight count to 0 and close every open fight at once.
/// Cases 3 and 5 were BOTH invisible to this class until 2026-08-19 - case 3 matched but deliberately
/// closed nothing, case 5 had no pattern at all - which is why an encounter the player walked away from
/// could stay "in combat" until reset or logout. Case 8 was invisible until 2026-08-26 and cost the same
/// thing: a wyvern died of poison, no line in the frame matched, and combat never closed.</para>
///
/// <para><b>Do not read the list as closed.</b> Three of the eight were found by a player noticing the
/// readout was wrong, months apart, each time in a frame that a careful reading of the existing list
/// said could not happen. <see cref="NoteRoomChanged"/> is the backstop for the next one, and it is
/// deliberately loud when it fires.</para>
///
/// <para><b>A new encounter can begin in the same frame.</b> Nothing here waits for a frame to end
/// before opening the next encounter, and it must not: MUD2 will happily kill the last creature of one
/// encounter and have the next thing turn on the player in the same output. <see cref="Begin"/> keeps
/// one encounter open for as long as any fight is active and <see cref="End"/> closes it the moment the
/// last one ends, so consecutive engagements are consecutive encounters - which is what they are,
/// each with its own attack command and its own weapon selection.</para>
///
/// <para><b>Weapons are not slots.</b> A weapon is ordinary inventory burden. Each creature - the
/// player included - selects one that applies to every creature it swings at for the rest of that
/// encounter, until it is dropped, breaks, or is changed. Selection is not free mid-fight: a redundant
/// re-issue is still charged as a change and can drop the player's guard (see
/// <see cref="WeaponAlreadyInUse"/>). Nothing about a weapon survives into the next encounter.</para>
///
/// <para><b>The "pass" tick:</b> MUD2 gives no textual signal at all for a combat tick where a
/// participant chose not to attack (confirmed against RESEARCH/mud2-multi-combat.jsonl — solo
/// combat can go silent for 90+ seconds with no hit/miss/pass message of any kind, until another
/// entity's presence "unsticks" the server's combat scheduler). We do not fabricate a synthetic
/// pass event; a clog's own event timestamps make the real silence visible for later statistical
/// analysis across many clogs.</para>
///
/// <para><b>Internally locked (2026-08-16).</b> <see cref="Observe"/> and <see cref="ForceEnd"/> are
/// called from the parser Feed thread (and, for ForceEnd, occasionally a pool thread via
/// MudSession.Dispose's async path) - a session teardown racing an in-flight Feed() could otherwise
/// run both concurrently against this class's unsynchronized fields. A single <see cref="_gate"/>
/// lock serializes the two entry points; contention is negligible (ForceEnd fires once per session
/// at most, Observe only on real combat lines). Consumers still marshal events to their own UI
/// thread themselves - this lock only protects this class's own state.</para>
/// </summary>
public sealed class CombatTracker
{
    private readonly object _gate = new();

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

    /// <summary>
    /// "You hit the banshee (6)." - the same blow with `identify` ON, reporting the EXACT damage
    /// instead of a bracket. Verbatim from session-rec.mud2.co.uk.20260819-001118.
    ///
    /// <para>Nothing matched this before 2026-08-19, so turning identify on - which is meant to give
    /// the client BETTER information - silently stopped every one of the player's own hits being
    /// counted. Emitted with RangeLow == RangeHigh: an exact reading is a range of width zero, so the
    /// consumers that average the pair need no special case for it.</para>
    /// </summary>
    private static readonly Regex YouHitExact = new(
        @"^You hit the (?<npc>.+?) \((?<dmg>\d+)\)\.$", RegexOptions.Compiled);
    private static readonly Regex YouMiss = new(@"^You miss the (?<npc>.+?)\.$", RegexOptions.Compiled);
    private static readonly Regex NpcHitsYou = new(
        @"^The (?<npc>.+?) hits you \((?<cur>\d+)/(?<max>\d+)\)\.$", RegexOptions.Compiled);
    /// <summary>
    /// "The rat18 hits you." - a landed blow with NO stamina parenthetical. This is the KILLING blow:
    /// there is no surviving stamina to report, so MUD2 omits the "(cur/max)" that
    /// <see cref="NpcHitsYou"/> requires. Verbatim from session-rec.mud2.co.uk.20260819-001608's death
    /// frame: <c>The rat18 hits you. / You feel your life concluding... / The rat18 has killed you.</c>
    ///
    /// <para>Unparsed until 2026-08-19, which meant the one hit in a session that actually mattered -
    /// the fatal one - was the only hit never counted, understating both the swing count and the
    /// damage taken on exactly the fights that ended worst.</para>
    /// </summary>
    private static readonly Regex NpcHitsYouBare = new(@"^The (?<npc>.+?) hits you\.$", RegexOptions.Compiled);
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

    /// <summary>
    /// "The water-snake5 has fled by trying to go over." - a flee ATTEMPT that failed. One word of
    /// difference from <see cref="NpcFled"/> ("trying to") and the opposite meaning: the creature is
    /// still in the room, still hostile, and still has to be killed.
    ///
    /// <para>Observed 7 times in 13 seconds against a single water-snake, each in a different and
    /// apparently random direction. The owner's report - snakes "often try to flee but almost never
    /// succeed, it just breaks the fight sequence" - is exactly this.</para>
    ///
    /// <para>Getting this wrong in either direction is expensive. Matched as <see cref="NpcFled"/>, it
    /// would send the chase assist after a creature standing in front of the player, and would poison
    /// the per-class flee statistics with escapes that never happened (the corpus records water snakes
    /// at 0 flees from 6 fights precisely BECAUSE this line matched nothing). Left unmatched, as it was
    /// until now, the panel sees an unexplained fight end instead.</para>
    /// </summary>
    private static readonly Regex NpcFleeFailed = new(
        @"^The (?<npc>.+?) has fled by trying to go \w+\.$", RegexOptions.Compiled);
    private static readonly Regex YouFled = new(@"^You have fled by going \w+\.$", RegexOptions.Compiled);

    /// <summary>
    /// "You have fled by trying to go north." - the PLAYER's flee FAILED. Same "trying to" infix that
    /// separates <see cref="NpcFleeFailed"/> from <see cref="NpcFled"/>, and the same inversion of
    /// meaning: the player never left the room.
    ///
    /// <para>It ends every fight regardless, because MUD2 returns the fight count to 0 either way.
    /// Verbatim, one frame, from session-rec.mud2.co.uk.20260819-000137:
    /// <c>flee n / You cannot go north from here. / You have changed experience level from protector
    /// to novice. / (Persona saved on -102 = 98). / You have fled by trying to go north.</c></para>
    ///
    /// <para>That frame is also the evidence that a failed flee is charged for: 102 points and a whole
    /// experience level, for no escape. Nothing in this client parsed the line at all until
    /// 2026-08-19, so every one of those was recorded as a fight that simply never ended.</para>
    /// </summary>
    private static readonly Regex YouFleeFailed = new(
        @"^You have fled by trying to go \w+\.$", RegexOptions.Compiled);
    /// <summary>
    /// "You can fight it no longer." and its object variants - "him", "her", and the form that names
    /// the creature outright: "You can fight the wyvern no longer." (owner, 2026-08-26).
    ///
    /// <para>The pronoun forms are all over the captures (it 14, him 4, her 1) and only "it" was
    /// matched, so the gendered ones went unrecognised. They trail every kind of end indifferently -
    /// 11 a real flee, 8 a FAILED one, 1 a death - which is the point: the sentence is a generic
    /// acknowledgment and its object slot tells us nothing about what happened. The named form is the
    /// one that matters: it carries a creature name, so unlike the pronouns it CAN close a fight on
    /// its own - see the handler.</para>
    /// </summary>
    private static readonly Regex FightEndOther = new(
        @"^You can fight (?:it|him|her|them|the (?<npc>.+?)) no longer\.$", RegexOptions.Compiled);

    /// <summary>
    /// "The wyvern drops dead, poisoned..." - the creature died, and NOT from the player's blow
    /// landing last, so no "You have killed the X." is printed anywhere in the frame. Verbatim
    /// (owner, 2026-08-26); the full frame is in <see cref="CombatEventKind.NpcDied"/>.
    ///
    /// <para>Nothing in the client matched this, and nothing matched the "has just passed on." that
    /// followed it either, so the frame contained no terminator this class could see and the fight
    /// stayed open for the rest of the session - the bug that produced this pattern.</para>
    ///
    /// <para>Matched on the cause-bearing shape rather than on the word "poisoned": the sentence
    /// template is "The X drops dead, &lt;cause&gt;...", and poison is the only cause observed so
    /// far. A cause we have never seen still closes the fight, and the cause itself travels in
    /// <see cref="CombatEvent.RawText"/> for whoever comes to catalogue them. What is NOT inferred
    /// is who did it: the line does not say, so this is not reported as a kill.</para>
    ///
    /// <para>Reported only for a creature already engaged, exactly as <see cref="NpcHealthRungs"/>
    /// lines are: something else in the room dying of poison is not this fight's business, and
    /// letting it through would open a fight bucket against a creature the player never touched.</para>
    /// </summary>
    private static readonly Regex NpcDroppedDead = new(
        @"^The (?<npc>.+?) drops dead, (?:.+?)\.\.\.$", RegexOptions.Compiled);

    /// <summary>
    /// "The wyvern has just passed on." - the corpse line, printed for every death however caused
    /// (it trails an ordinary kill too: <c>You have killed the banshee. / (Persona saved on +143 =
    /// 343). / The banshee has just passed on.</c>).
    ///
    /// <para>Acted on ONLY for a creature this class still believes is engaged, which after a kill or
    /// any other matched terminator it is not - so in the ordinary case this stays the trailing prose
    /// it has always been. Reaching it with the fight still open means the real terminator was missed,
    /// and then this is the last thing MUD2 will ever say about that creature, so it is the backstop:
    /// dead is dead, close the fight. Requiring the leading "The " keeps players out of it - a player
    /// death is not "The Fred ...".</para>
    /// </summary>
    private static readonly Regex NpcPassedOn = new(
        @"^The (?<npc>.+?) has just passed on\.$", RegexOptions.Compiled);
    private static readonly Regex WeaponEquip = new(
        @"^You are now using the (?<weapon>.+?) to fight!$", RegexOptions.Compiled);

    /// <summary>
    /// "You're using the unlit brand anyway..." - MUD2's answer to a redundant weapon selection
    /// (`k X with Y`, `use Y`, `wield Y`) naming a weapon already in hand. Reported as
    /// <see cref="CombatEventKind.WeaponEquip"/>, because that is exactly what it states.
    ///
    /// <para>Worth parsing for a reason the wording hides: it names the weapon ACTUALLY in use, which
    /// need not be the one asked for. In session-rec.mud2.co.uk.20260819-001608 the owner sent
    /// <c>k rat with stick</c> and MUD2 answered "You're using the unlit brand anyway..." - so the
    /// only truthful statement about the weapon in that whole frame was this line, and taking the
    /// command at its word would have recorded the fight under the wrong weapon.</para>
    ///
    /// <para>A redundant re-issue is also charged as a weapon CHANGE and can therefore drop the
    /// player's guard - the same capture pairs four of these with two "Your guard drops momentarily in
    /// your confusion." lines. No inference is done here: that drop arrives as its own line and is
    /// already matched by <see cref="GuardConfusion"/>.</para>
    /// </summary>
    private static readonly Regex WeaponAlreadyInUse = new(
        @"^You're using the (?<weapon>.+?) anyway\.\.\.$", RegexOptions.Compiled);
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
    /// <summary>
    /// "The water-snake5 has a stamina lying between 90 and 99." - the stethoscope's `diagnose` read.
    ///
    /// <para><b>MUD2 does report NPC stamina after all.</b> Five separate comments in this codebase
    /// asserted it never does, and built a whole estimator around that belief (see
    /// FightHistory.EstimatedStaminaPool, which infers a creature's pool from the median damage of
    /// fights that ended in a kill). It is a probe rather than free telemetry - it needs a stethoscope
    /// and a typed command - but it is a direct, bracketed reading of the number everything else was
    /// approximating.</para>
    ///
    /// <para>Worth parsing chiefly as an instrument: it is the only way to CHECK a published creature
    /// stamina against the live game, and the owner's standing rule is that the published figures are
    /// hypotheses until our own data settles them. Observed live: giant snake 117-126, water-snake5
    /// 90-99 (published 90), viper 18-27 (published 20).</para>
    /// </summary>
    private static readonly Regex NpcStaminaRead = new(
        @"^The (?<npc>.+?) has a stamina lying between (?<lo>\d+) and (?<hi>\d+)\.$", RegexOptions.Compiled);

    /// <summary>"Axe0 dropped." - fires on a deliberate drop AND automatically when fleeing carries
    /// your weapon out of your hands. Either way the weapon is gone, and without this the panel goes
    /// on reporting a weapon the player is no longer holding.</summary>
    private static readonly Regex ItemDropped = new(
        @"^(?<item>[A-Za-z][A-Za-z0-9' -]*?) dropped\.$", RegexOptions.Compiled);

    private static readonly Regex GuardConfusion = new(
        @"^Your guard drops momentarily in your confusion\.$", RegexOptions.Compiled);

    /// <summary>"You feel your life concluding..." - the narrative precursor to the player's death.
    /// Informational only; it shares its frame with the "has killed you" line that follows, so it is
    /// no earlier a warning than the death itself. Matched purely so it stops being an unexplained
    /// line in the one frame nobody wants to be guessing about.</summary>
    private static readonly Regex LifeConcluding = new(
        @"^You feel your life concluding\.\.\.$", RegexOptions.Compiled);

    // NPC instance names currently engaged (case-insensitive) — non-empty implies InCombat.
    private readonly HashSet<string> _active = new(StringComparer.OrdinalIgnoreCase);

    // The encounter closes the instant _active empties, whether that was a kill, a flee, or a
    // withdrawal — see End(). There used to be a 5-second "grace" window here that kept the
    // encounter open after a kill in case a pack straggler joined, on the theory that a pack
    // fight otherwise fragments into several encounters. It doesn't need one: Begin() already
    // keeps the SAME encounter open for as long as _active is non-empty, so a genuine pack fight
    // (new participants joining while others are still engaged) was never affected by this at
    // all — only a NEW mob attacking after the encounter had already fully ended was, and per
    // the owner that IS a new encounter. Any residual need to keep capturing trailing prose
    // (score, "has just passed on", dropped items) after the close is a LOGGING concern, not a
    // combat-state one — see ClogWriter's own tail-capture, which runs until the next prompt
    // regardless of whether a new encounter starts in the meantime.
    private bool _encounterOpen;

    public bool InCombat => _encounterOpen;

    /// <summary>Fires whenever <see cref="InCombat"/> flips (true = encounter started).</summary>
    public event Action<bool>? InCombatChanged;

    /// <summary>Fires for every classified combat line, in order, while (or just as) InCombat.</summary>
    public event Action<CombatEvent>? EventOccurred;

    /// <summary>
    /// Classify one completed line. Cheap no-op for the overwhelming majority of lines
    /// (a plain-text prefix check would help further, but regex-per-candidate is already
    /// negligible next to network I/O — see EffectTracker for the equivalent trade-off).
    /// </summary>
    public void Observe(StyledLine line, DateTime timestampUtc)
    {
        lock (_gate)
            ObserveLocked(line, timestampUtc);
    }

    private void ObserveLocked(StyledLine line, DateTime timestampUtc)
    {
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
        else if ((m = YouHitExact.Match(text)).Success)
        {
            // `identify` on: one exact figure instead of a bracket. Reported as a zero-width range so
            // that every consumer averaging RangeLow/RangeHigh lands on the exact value unchanged -
            // see YouHitExact. Matched AFTER YouHit only for readability; the two cannot collide,
            // since "(5-9)" cannot satisfy a pattern demanding digits-then-close-paren.
            Begin(m.Groups["npc"].Value);
            var exact = int.Parse(m.Groups["dmg"].Value);
            Emit(timestampUtc, CombatEventKind.Hit, CombatActor.Player, m.Groups["npc"].Value, null,
                exact, exact, text);
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
        else if ((m = NpcHitsYouBare.Match(text)).Success)
        {
            // The fatal blow, which carries no "(cur/max)" because no stamina survives it. Null
            // ranges: the consumers treat a missing stamina reading as "no reading", which is the
            // truth here - inventing 0 would look like a measurement and would poison the running
            // stamina baseline the damage-taken deltas are derived from.
            Begin(m.Groups["npc"].Value);
            Emit(timestampUtc, CombatEventKind.HitByNpc, CombatActor.Npc, m.Groups["npc"].Value, null,
                null, null, text);
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
            End(m.Groups["npc"].Value);
        }
        else if ((m = NpcDroppedDead.Match(text)).Success)
        {
            // The eighth end - you lost the creature (FightOutcome.NoMore). It died without the
            // player landing the last blow, so no "You have killed the X." arrives to close it. Actor is the NPC, not the player -
            // whatever finished it, it was not our swing, and the line does not say whose poison it
            // was. Emitted before End for the same reason the kill above is: End can flip InCombat
            // false, and a listener that closes its record on that must still receive this line.
            var deadNpc = m.Groups["npc"].Value;
            if (_active.Contains(deadNpc))
            {
                Emit(timestampUtc, CombatEventKind.NpcDied, CombatActor.Npc, deadNpc, null, null, null, text);
                End(deadNpc);
            }
        }
        else if ((m = NpcPassedOn.Match(text)).Success)
        {
            // Backstop, not a terminator: after any matched end this creature is already gone from
            // _active and this line is the trailing prose it has always been. Still here means we
            // missed the real end, and this is MUD2's last word on the creature - so close it, and
            // let the event stand in the clog as the evidence that a line went unmatched.
            var goneNpc = m.Groups["npc"].Value;
            if (_active.Contains(goneNpc))
            {
                Emit(timestampUtc, CombatEventKind.NpcDied, CombatActor.Npc, goneNpc, null, null, null, text);
                End(goneNpc);
            }
        }
        else if ((m = NpcKilledYou.Match(text)).Success)
        {
            Emit(timestampUtc, CombatEventKind.KilledByNpc, CombatActor.Npc, m.Groups["npc"].Value, null, null, null, text);
            // Player death ends the WHOLE encounter unconditionally — a dead player cannot keep
            // fighting anyone else in the same room, regardless of how many other NPCs are still
            // engaged. Using the ordinary single-NPC End() here would leave the rest of _active
            // dangling open (a latent bug: death is routinely followed immediately by a
            // disconnect/quit with no further lines to ever empty it), so the encounter would
            // linger open until ForceEnd's generic "reset/disconnect" close instead of a clean,
            // correctly-attributed KilledByNpc close.
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
            // Per-creature: a withdraw zeroes only the fight it names (owner, 2026-08-19). It reads
            // like a player-side terminator - the player agreed to it - but it is an agreement with
            // ONE creature, and anything else in a pack goes on swinging.
            Emit(timestampUtc, CombatEventKind.Withdrawn, CombatActor.Npc, m.Groups["npc"].Value, null, null, null, text);
            End(m.Groups["npc"].Value);
        }
        else if ((m = NpcFleeFailed.Match(text)).Success)
        {
            // Matched BEFORE NpcFled, because "has fled by trying to go" also contains "has fled by"
            // and the two must never be confused - see NpcFleeFailed's own remarks.
            //
            // This DOES end the fight (owner, 2026-08-19). The creature is still in the room and still
            // hostile, but MUD2 has broken the fight sequence - "You can fight it no longer." trails
            // it in the same frame saying so - and the player has to attack again to re-engage.
            //
            // It used to deliberately NOT end here, to stop one 15-second snake fight being recorded
            // as eight encounters. That reasoning was inverted: eight re-engagements ARE eight
            // encounters (each is its own frame, its own attack command, and its own weapon
            // selection), and the price of pretending otherwise was a fight the player simply walked
            // away from - exactly the water-snake3 frame the owner reported - staying "in combat" with
            // no line left that could ever close it, until reset or logout forced it.
            //
            // Per-creature, not EndAll: only this creature's fight ended, and anything else in a pack
            // is still swinging.
            Emit(timestampUtc, CombatEventKind.NpcFleeFailed, CombatActor.Npc, m.Groups["npc"].Value, null, null, null, text);
            End(m.Groups["npc"].Value);
        }
        else if ((m = NpcFled.Match(text)).Success)
        {
            Emit(timestampUtc, CombatEventKind.NpcFled, CombatActor.Npc, m.Groups["npc"].Value, null, null, null, text);
            End(m.Groups["npc"].Value);
        }
        else if (YouFleeFailed.IsMatch(text))
        {
            // Matched BEFORE YouFled for the same reason NpcFleeFailed precedes NpcFled: keep the
            // failed form's own reading of the sentence, never the successful one's. (The two patterns
            // cannot actually collide - "by trying to go" does not satisfy "by going" - but the
            // ordering states the intent rather than relying on that.)
            //
            // EndAll despite the player never leaving the room: the flee FAILED, yet MUD2 still zeroed
            // the fight count. Every open fight is over and the player must re-attack from scratch.
            // Unlike a withdraw (which is an agreement with one creature), this is the player's own
            // combat state being reset, so it cannot be scoped to a single opponent.
            Emit(timestampUtc, CombatEventKind.YouFleeFailed, CombatActor.Player, null, null, null, null, text);
            EndAll();
        }
        else if (YouFled.IsMatch(text))
        {
            // One flee command can end several simultaneous fights at once (confirmed offline:
            // a single flee line closed two concurrent rat fights) — close every active NPC.
            Emit(timestampUtc, CombatEventKind.YouFled, CombatActor.Player, null, null, null, null, text);
            EndAll();
        }
        else if ((m = FightEndOther.Match(text)).Success)
        {
            // Always a trailing acknowledgment of an end already stated on an earlier line of the
            // SAME frame - so the fight it refers to is normally closed before this line is reached,
            // by NpcFleeFailed or NpcFled (or a kill, or the poison death above). Verified 27/27
            // against the research capture for the "has fled by going <dir>." case; the failed-flee
            // case that also trails it was for a long time the one this reasoning got wrong, because
            // NpcFleeFailed did not close anything, leaving this line as the only end-of-fight
            // evidence in the frame and deliberately ignoring it.
            //
            // The pronoun forms ("it", "him", "her") stay informational, and for the original reason:
            // they name no creature, so promoting one to an independent terminator would close OTHER
            // still-active fights in a pack.
            //
            // The NAMED form ("You can fight the wyvern no longer.") is different - it says who, so
            // it closes that one fight and nothing else. In every frame observed it arrives after the
            // end it acknowledges and closes nothing; the point is the frames we have not observed,
            // where it is the only line that both states an end and identifies its creature.
            //
            // Named or not, it is ignored for a creature we are not fighting (owner, 2026-08-26).
            // MUD2 stacks several end messages in a frame and this one can land AFTER the fight was
            // already closed by another of them - which is exactly the captured wyvern frame, where
            // the poison death closes the fight two lines earlier. So a name that is not on the roster
            // is a trailing acknowledgment of something already dealt with, and the right response is
            // nothing at all.
            //
            // Not merely tidy, since FightEndOther began resolving fights: FightHistoryRecorder has no
            // in-combat guard, so a named event is enough to get-or-CREATE a bucket, and one created
            // after the encounter's flush is written out by the next flush as a zero-swing row - a
            // second fight against the same creature that never happened. The recorder now refuses to
            // create a fight outside an open encounter as well, so the two layers guard it
            // independently; this side is pinned by
            // CombatTrackerTests.FightEndOther_NamingACreatureWeAreNotFighting_ReportsNoName.
            var endedNpc = m.Groups["npc"].Success && _active.Contains(m.Groups["npc"].Value)
                ? m.Groups["npc"].Value
                : SoleActiveOnFightEnd(line);
            Emit(timestampUtc, CombatEventKind.FightEndOther, CombatActor.Player, endedNpc, null, null, null, text);
            if (endedNpc is not null)
                End(endedNpc);
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
        else if ((m = WeaponAlreadyInUse.Match(text)).Success)
        {
            // Reported as WeaponEquip: the line states which weapon is in use, which is precisely what
            // that kind means. No Begin(), for WeaponEquip's own reason below - the line names no
            // creature, so there is nothing to open an encounter against.
            Emit(timestampUtc, CombatEventKind.WeaponEquip, CombatActor.Player, null, m.Groups["weapon"].Value, null, null, text);
        }
        else if (LifeConcluding.IsMatch(text))
        {
            // Informational only - no Begin/End. The death line it precedes is in the same frame and
            // does all the actual work; see CombatEventKind.LifeConcluding.
            Emit(timestampUtc, CombatEventKind.LifeConcluding, CombatActor.Player, null, null, null, null, text);
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
        else if ((m = NpcStaminaRead.Match(text)).Success)
        {
            // A measurement, not a combat state change: no Begin(), and it is reported whether or not
            // the creature is engaged, since diagnosing something BEFORE picking a fight with it is
            // the whole point of carrying a stethoscope. The bracket travels in the range fields the
            // damage lines already use.
            Emit(timestampUtc, CombatEventKind.NpcStaminaRead, CombatActor.Npc, m.Groups["npc"].Value, null,
                int.Parse(m.Groups["lo"].Value, System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(m.Groups["hi"].Value, System.Globalization.CultureInfo.InvariantCulture), text);
        }
        else if ((m = ItemDropped.Match(text)).Success)
        {
            // Only interesting when what hit the floor is what we were fighting with. Fleeing drops
            // your weapon automatically, so this arrives in the same tick as a flee with no
            // WeaponBroke to explain it, and the panel would otherwise keep the weapon on screen.
            Emit(timestampUtc, CombatEventKind.ItemDropped, CombatActor.Player, null, m.Groups["item"].Value, null, null, text);
        }
        else if (NpcHealthRungs.TryParse(text, out var hurtNpc, out var rung, out var phrase))
        {
            // Matched last, and deliberately: "The X looks ..." is the most permissive shape in this
            // whole chain, so every pattern that could share it gets first refusal.
            //
            // No Begin(), and reported only for an NPC ALREADY engaged. The identical line appears in
            // room descriptions, so a wounded creature standing across the room would otherwise open
            // an encounter against something the player has never touched - and in a permadeath game
            // a phantom opponent on the panel is worse than a missing one.
            if (_active.Contains(hurtNpc))
            {
                EventOccurred?.Invoke(new CombatEvent(
                    timestampUtc, CombatEventKind.NpcHealth, CombatActor.Npc, hurtNpc, null, null, null,
                    text, rung, phrase));
            }
        }
        else if (line.Kind == LineKind.FightEnd)
        {
            // Reached only when NOTHING above matched the wording, and the server nonetheless tagged
            // this line C08.10/11/12 - a fight end. The code is the evidence; the prose was only ever
            // how we identify WHICH creature, so an unknown phrasing costs us the name and no more.
            //
            // This branch is the whole reason for LineKind.FightEnd. Every wording bug this file has
            // had was a correctly-coded line that no regex here recognised, and each one cost a fight
            // that never closed - so the untranslated line is now caught rather than ignored, and lands
            // in the clog verbatim, which is how the next unknown wording gets found.
            var endedNpc = SoleActiveOnFightEnd(line);
            Emit(timestampUtc, CombatEventKind.FightEndOther, CombatActor.Player, endedNpc, null, null, null, text);
            if (endedNpc is not null)
                End(endedNpc);
        }
    }

    /// <summary>
    /// The one creature that a nameless but server-confirmed fight end can only be about, or null
    /// when that is a guess.
    ///
    /// <para>Requires the C1 code (<see cref="LineKind.FightEnd"/>): the prose alone has never been
    /// trusted to close a fight it does not name, and should not start being. With the code present
    /// and exactly ONE creature engaged there is no other fight the line could mean. With two or
    /// more there is, and picking one would file a pack fight's ending under the wrong creature -
    /// worse than leaving it open, because a wrong row is evidence and an open fight is only a bug.</para>
    ///
    /// <para>Best-effort by nature, and only ever acts when we failed to parse the real terminator:
    /// when we did parse it, the fight is already closed and <see cref="_active"/> no longer holds
    /// it. Same caution as NpcKilledYouNarrative's "sole active participant" - our roster is what we
    /// managed to observe, not necessarily what is in the room.</para>
    /// </summary>
    private string? SoleActiveOnFightEnd(StyledLine line)
        => line.Kind == LineKind.FightEnd && _active.Count == 1 ? _active.First() : null;

    /// <summary>Force-close any open encounter without a matching end line (e.g. an auto-reset
    /// wiping the game state mid-fight, or logout/relog). <paramref name="reason"/> is recorded
    /// verbatim as the synthetic event's raw text, so a clog says which backstop fired.</summary>
    public void ForceEnd(DateTime timestampUtc, string reason = "reset/disconnect")
    {
        lock (_gate)
            ForceEndLocked(timestampUtc, reason);
    }

    /// <summary>
    /// The player is in a different room than they were. Closes any open encounter.
    ///
    /// <para><b>You cannot walk out of a fight in MUD2</b> (owner): movement is refused while
    /// fighting, and leaving costs a flee - which prints its own line and is already handled. So a
    /// room change is proof the fight is over, whatever we think, and it is the one such proof that
    /// does not depend on having matched any particular sentence. That makes it the right backstop
    /// for the whole class of bug this file keeps hitting: an end phrased in a way nothing here
    /// matches, leaving combat stuck until logout (a poisoned wyvern, 2026-08-26; a water-snake's
    /// failed flee before that; and, before that, a bare-handed fight that never opened).</para>
    ///
    /// <para>Deliberately NOT silent: it force-ends with its own reason string, so an encounter
    /// closed this way is visibly closed by the backstop rather than by evidence, and the clog says
    /// so. Every time this fires there is an unmatched line to go and find. It cannot fix a fight
    /// the player is still standing in - only leaving does that - so it is a floor on how long a
    /// phantom fight can persist, not a substitute for parsing the end.</para>
    /// </summary>
    public void NoteRoomChanged(DateTime timestampUtc)
    {
        lock (_gate)
            ForceEndLocked(timestampUtc, "room changed");
    }

    private void ForceEndLocked(DateTime timestampUtc, string reason)
    {
        if (!InCombat)
            return;
        Emit(timestampUtc, CombatEventKind.FightEndOther, null, null, null, null, null, $"(forced end: {reason})");
        _active.Clear();
        CloseEncounter();
    }

    private void Begin(string npc)
    {
        _active.Add(npc);
        if (!_encounterOpen)
        {
            _encounterOpen = true;
            InCombatChanged?.Invoke(true);
        }
    }

    private void End(string npc)
    {
        _active.Remove(npc);
        if (_active.Count == 0)
            CloseEncounter();
    }

    private void EndAll()
    {
        _active.Clear();
        CloseEncounter();
    }

    private void CloseEncounter()
    {
        if (!_encounterOpen)
            return;
        _encounterOpen = false;
        InCombatChanged?.Invoke(false);
    }

    private void Emit(DateTime ts, CombatEventKind kind, CombatActor? actor, string? npc, string? weapon,
        int? lo, int? hi, string raw)
        => EventOccurred?.Invoke(new CombatEvent(ts, kind, actor, npc, weapon, lo, hi, raw));
}
