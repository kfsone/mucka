using System.Globalization;
using System.Text.RegularExpressions;
using MudSharp.Models;

namespace MudSharp.Combat;

/// <summary>
/// Detects the start/end of combat encounters and classifies individual combat lines, mostly from
/// plain rendered text (<see cref="StyledLine.PlainText"/>): several lines that matter carry no C1
/// wrapper at all in real captures (WeaponEquip, WeaponBroke, DroppedGuard), and the death lines
/// "The X drops dead, poisoned..." and "The X has just passed on." arrive as bare untagged text at
/// base scope.
///
/// <para>The prose is what identifies WHICH creature, and there is no substitute for it. But the one
/// tag this class does read, <see cref="LineKind.FightEnd"/> (C08.10/11/12), is what says a fight
/// ended AT ALL, and it has been right every time the prose was not. Authority from the code,
/// identity from the text.</para>
///
/// <para><b>What this class deliberately does NOT do:</b> it does not attempt to reconstruct
/// per-fight damage/DPS/duration aggregates. This tracker's job is just to answer, live, "are we
/// in combat right now" and "what combat line just happened", so a ClogWriter can record the raw
/// stream faithfully for later offline analysis.</para>
///
/// <para><b>Every end is printed inside a SINGLE frame</b> - one prompt to the next - always. That
/// guarantee is load-bearing here: it is the
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
/// <item><term>6. Withdraw</term><description>"The X withdraws from your fight, and so do you." / "You withdraw from your fight with someone, and that person does too." - per-creature; an agreement with ONE creature, worded from whichever side accepted.</description></item>
/// <item><term>7. Player died</term><description>"The X has killed you." / "You have been killed by ..." - zeroes the fight count. Permadeath.</description></item>
/// <item><term>8. You lose the creature</term><description>"The X drops dead, poisoned..." - per-creature; it died without the player landing the last blow, so no kill line is printed at all. An OPEN family (see <see cref="FightOutcome.NoMore"/>): poison is the member observed, other causes are expected to be worded differently.</description></item>
/// </list></para>
///
/// <para>1-3, 6 and 8 close only the creature they name. 4, 5 and 7 are the player's own state changing
/// rather than one opponent's, so they return the fight count to 0 and close every open fight at once.
/// </para>
///
/// <para><see cref="NoteRoomChanged"/> is the backstop for an end whose wording this class does not
/// yet recognise, and it is deliberately loud when it fires.</para>
///
/// <para><b>An opponent need not have a name.</b> MUD2 writes "someone" in place of the creature in
/// every sentence it would otherwise have named - because the creature turned invisible, or because
/// the player was blinded - and it does so without changing anything else about the sentence or its
/// C1 code. The whole family is matched in both forms; <see cref="NpcObject"/> carries the evidence.
/// An anonymous line never names anyone: it goes to the encounter's one unseen group
/// (<see cref="AnonymousOpponent.Group"/>), kept by an <see cref="UnseenBook"/> per encounter, which
/// owns the rules of docs/unseen-presentation.md. An unnamed fight is a fight, and the ends of one
/// still have to close it.</para>
///
/// <para><b>A new encounter can begin in the same frame.</b> Nothing here waits for a frame to end
/// before opening the next encounter, and it must not: MUD2 will happily kill the last creature of one
/// encounter and have the next thing turn on the player in the same output. <see cref="Begin"/> keeps
/// one encounter open for as long as any fight is active and <see cref="Ending"/> closes it the moment the
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
/// participant chose not to attack - solo combat can go silent for 90+ seconds with no hit/miss/pass
/// message of any kind. We do not fabricate a synthetic pass event; a clog's own event timestamps
/// make the real silence visible for later statistical analysis across many clogs.</para>
///
/// <para><b>Internally locked.</b> <see cref="Observe"/> and <see cref="ForceEnd"/> are
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

    /// <summary>
    /// The creature as the OBJECT of a sentence ("... the man." / "... someone.") and as its
    /// SUBJECT ("The man ..." / "Someone ..."). Every combat line that names a creature is built
    /// from one of these two, so they are written once here rather than eleven times below.
    ///
    /// <para><b>"someone" and "something" are MUD2's names for a Creature the player cannot
    /// identify</b>, and neither is an occasional curiosity - the word replaces the name in EVERY
    /// sentence for as long as the condition lasts. Two causes are confirmed, and they are different
    /// things: the CREATURE turned invisible (C1 04.00.05, "The man fades from view."), in which case
    /// only that one goes anonymous and anything else in the room is still named; or the PLAYER was
    /// blinded, in which case everything is. Both are on the wire in the same session.</para>
    ///
    /// <para><b>The word is chosen by the Creature, not by the cause.</b> The man, the thief and an
    /// invisible player are all "someone"; a water-snake, a fox, an eagle and a rat pack fought blind
    /// are all "something" - across every coded C08 line in the corpus, with no counter-example. It
    /// coincides with the pronoun the game already uses for each ("You can fight him no longer." /
    /// "You can fight it no longer."). Both alternatives are accepted below and the matched word is
    /// handed to the book with the line, so a blind fight against an animal ("Something hits you
    /// (116/120).") produces its swing events.</para>
    ///
    /// <para>What is NOT different is the protocol. An anonymous line carries the same C08 sub-code
    /// as its named counterpart, byte for byte: <c>[A3][9B]</c> "You attack someone.",
    /// <c>[A3][9C]</c> "You hit someone (5-9).", <c>[A3][9D]</c> "You miss someone.",
    /// <c>[A3][9E]</c> "Someone hits you (115/120).", <c>[A3][9F]</c> "Someone misses you." So the
    /// substitution is a naming rule applied to sentences the server was going to send anyway, and
    /// the whole family is widened as a family rather than one wording at a time - which is the
    /// failure this file keeps having.</para>
    ///
    /// <para>The <c>anon</c> group carries the word, which the book uses only to match an ending to a
    /// slot and to leave a Creature of known class unparked (<see cref="ActorOf"/>).</para>
    /// </summary>
    private const string NpcObject  =
        @"(?:the (?<npc>.+?)|(?<anon>" + AnonymousOpponent.Person + "|" + AnonymousOpponent.Thing + ")|" + PlayerOpponent.Persona + ")";
    private const string NpcSubject =
        @"(?:The (?<npc>.+?)|(?<anon>" + AnonymousOpponent.PersonAsSubject + "|" + AnonymousOpponent.ThingAsSubject + ")|" + PlayerOpponent.Persona + ")";

    /// <summary>A subject that is always named: a creature, or a player by persona. For the wordings
    /// that have no anonymous form.</summary>
    private const string NamedSubject = @"(?:The (?<npc>.+?)|" + PlayerOpponent.Persona + ")";

    // NPC-initiated aggro lines never name a weapon and use one of a handful of verb phrases
    // observed in the research capture. Best-effort: MUD2 may use aggro phrasing not yet seen.
    // Deliberately NOT given an anonymous form: every wording here describes watching a creature
    // move, and an invisible one has never been seen announcing itself this way.
    private static readonly Regex NpcAggroStart = new(
        $@"^{NamedSubject} is (?:looking at|glaring at|snarling at|moving towards|rushing at|advancing towards|approaching|staring at) you \w+\.*$",
        RegexOptions.Compiled);

    /// <summary>
    /// "The rat22 is about to attack you." / "Someone is about to attack you." / "Something is about
    /// to attack you." - all verbatim, 28 occurrences, 26 named and 2 anonymous, none matched by
    /// <see cref="NpcAggroStart"/> (no adverb, and that pattern has no anonymous subject). Under
    /// 08.00 like every other start. The anonymous form is the one that matters: it is how an
    /// opponent the player cannot see announces itself, and it opens a participant of its own -
    /// never resolved onto a Creature already engaged, because a Creature already engaged does not
    /// announce that it is about to attack.
    ///
    /// <para>One announcement per Creature, observed: a dark Cellar fight carried two "Something is
    /// about to attack you." lines 2 s apart and closed with two named kill lines, "You have killed
    /// the rat18." and "You have killed the rat19." Two announcements, two Creatures.</para>
    /// </summary>
    private static readonly Regex NpcAboutToAttack = new(
        $@"^{NpcSubject} is about to attack you\.$", RegexOptions.Compiled);

    /// <summary>The subject of an 08.00 line no wording above recognised - see the
    /// <see cref="LineKind.FightStart"/> branch at the end of the chain. "You attack X" is the
    /// player moving first; any other sentence starting with a Creature is the Creature moving
    /// first. Loose on purpose: it reads the name out of a sentence nobody has seen yet.</summary>
    private static readonly Regex CodedStartSubject = new(
        @"^(?:You attack (?<obj>.+?)|(?<subj>The .+?|Someone|Something|" + PlayerOpponent.Persona + @") .*)\.$", RegexOptions.Compiled);

    // Two forms, and the UNARMED one has no weapon clause at all:
    //   armed:   "You attack the thief, using the falchion as a weapon."
    //   unarmed: "You attack the thief."
    // Both forms must open the encounter: a bare-handed attack that is not matched leaves nothing
    // for a "use <weapon>" issued before the first blow to attach to, and the readout stays "unarmed".
    // Two patterns rather than one with an optional weapon clause: with a lazy npc group and the
    // clause optional, the engine prefers skipping the optional group and swallows ", using the X
    // as a weapon" into the npc name itself. Matched armed-first so the specific form wins.
    private static readonly Regex PlayerAttackStart = new(
        $@"^You attack {NpcObject}, using the (?<weapon>.+?) as a weapon\.$", RegexOptions.Compiled);
    private static readonly Regex PlayerAttackStartUnarmed = new(
        $@"^You attack {NpcObject}\.$", RegexOptions.Compiled);
    private static readonly Regex YouHit = new(
        $@"^You hit {NpcObject} \((?<lo>\d+)-(?<hi>\d+)\)\.$", RegexOptions.Compiled);

    /// <summary>
    /// "You hit the banshee (6)." - the EXACT damage of a blow, instead of the bracket
    /// <see cref="YouHit"/> matches. Verbatim from session-rec.mud2.co.uk.20260819-001118.
    ///
    /// <para>What makes MUD2 print this instead of a bracket is not known; it is not the `identify`
    /// setting. The corpus is 6 exact lines against roughly 820 bracketed ones.</para>
    ///
    /// <para>Emitted with RangeLow == RangeHigh: an exact reading is a range of width zero, so the
    /// consumers that average the pair need no special case for it.</para>
    /// </summary>
    private static readonly Regex YouHitExact = new(
        $@"^You hit {NpcObject} \((?<dmg>\d+)\)\.$", RegexOptions.Compiled);
    private static readonly Regex YouMiss = new($@"^You miss {NpcObject}\.$", RegexOptions.Compiled);
    private static readonly Regex NpcHitsYou = new(
        $@"^{NpcSubject} hits you \((?<cur>\d+)/(?<max>\d+)\)\.$", RegexOptions.Compiled);
    /// <summary>
    /// "The rat18 hits you." - a landed blow with NO stamina parenthetical. This is the KILLING blow:
    /// there is no surviving stamina to report, so MUD2 omits the "(cur/max)" that
    /// <see cref="NpcHitsYou"/> requires. Verbatim from session-rec.mud2.co.uk.20260819-001608's death
    /// frame: <c>The rat18 hits you. / You feel your life concluding... / The rat18 has killed you.</c>
    /// </summary>
    private static readonly Regex NpcHitsYouBare = new($@"^{NpcSubject} hits you\.$", RegexOptions.Compiled);
    private static readonly Regex NpcMissesYou = new($@"^{NpcSubject} misses you\.$", RegexOptions.Compiled);
    private static readonly Regex WithdrawOffer = new(
        $@"^You offer to withdraw from your fight with {NpcObject}\.$", RegexOptions.Compiled);

    /// <summary>
    /// "The zombie1 offers to withdraw from your fight if you do likewise." - the creature's own half
    /// of the handshake, and NOT an end; see <see cref="CombatEventKind.NpcWithdrawOffer"/> for the
    /// code-level and prose-level evidence, and for all 5 occurrences on disk. One sentence, exactly
    /// as written here apart from the subject - nothing plural, titled or pronominal has ever been
    /// observed - plus the anonymous subject, seen verbatim from an invisible opponent: "Someone
    /// offers to withdraw from your fight if you do likewise."
    ///
    /// <para>Cannot collide with <see cref="MutualWithdraw"/> ("... withdraws from your fight, and so
    /// do you.") in either direction: the verbs differ and both patterns are anchored at both ends.</para>
    ///
    /// <para>Nor with "The thief takes back his offer to withdraw from your fight." (verbatim, on the
    /// wire), which is the OPPOSITE of an end and is deliberately matched by nothing: it changes no
    /// state this class holds, since the offer it retracts was never treated as one either. It is
    /// named here because it is the line a careless widening of this family would swallow.</para>
    /// </summary>
    private static readonly Regex NpcWithdrawOffer = new(
        $@"^{NpcSubject} offers to withdraw from your fight if you do likewise\.$", RegexOptions.Compiled);
    private static readonly Regex YouKilled = new($@"^You have killed {NpcObject}\.$", RegexOptions.Compiled);
    private static readonly Regex NpcKilledYou = new($@"^{NpcSubject} has killed you\.$", RegexOptions.Compiled);

    // Non-fightbrief ("narrative") death line - confirmed live against a real capture where the
    // player never enabled fightbrief for that character. "someone" replaces the NPC's name
    // whenever the player is blind at the moment of death (also confirmed live: a vampire that
    // cast a blindness spell mid-fight).
    private static readonly Regex NpcKilledYouNarrative = new(
        $@"^You have been killed by {NpcObject}\.$", RegexOptions.Compiled);
    private static readonly Regex MutualWithdraw = new(
        $@"^{NpcSubject} withdraws from your fight, and so do you\.$", RegexOptions.Compiled);

    /// <summary>
    /// The same mutual withdraw written from the other side: "You withdraw from your fight with
    /// someone, and that person does too." Verbatim from the scrollback of the fight that produced
    /// this whole anonymous family - the player typed `withdraw` to accept the creature's standing
    /// offer, and MUD2 answered with the player as the subject rather than the creature.
    ///
    /// <para>It ends the fight exactly as <see cref="MutualWithdraw"/> does, and it is the line that
    /// went unmatched while the client sat in combat with an opponent it could not see leave.</para>
    ///
    /// <para>The trailing clause is left loose. "that person" is what MUD2 says about someone the
    /// player cannot identify; what it says about a creature it CAN name has never been captured,
    /// and it plainly varies with the target, so pinning a pronoun here would lose the named form
    /// the same way the named form of this sentence was lost in the first place.</para>
    /// </summary>
    private static readonly Regex PlayerMutualWithdraw = new(
        $@"^You withdraw from your fight with {NpcObject}, and .+? does too\.$", RegexOptions.Compiled);

    private static readonly Regex NpcFled = new(
        $@"^{NpcSubject} has fled by going \w+\.$", RegexOptions.Compiled);

    /// <summary>
    /// "The water-snake5 has fled by trying to go over." - a flee ATTEMPT that failed. One word of
    /// difference from <see cref="NpcFled"/> ("trying to") and the opposite meaning: the creature is
    /// still in the room and still has to be killed. It is NOT still fighting, though - a flee attempt
    /// ends combat whether or not it succeeds - so killing it means attacking it again first.
    ///
    /// <para>Observed 7 times in 13 seconds against a single water-snake, each in a different and
    /// apparently random direction: it often tries to flee but almost never succeeds, and the attempt
    /// breaks the fight sequence regardless.</para>
    ///
    /// <para>Getting this wrong in either direction is expensive. Matched as <see cref="NpcFled"/>, it
    /// would send the chase assist after a creature standing in front of the player, and would poison
    /// the per-class flee statistics with escapes that never happened (the corpus records water snakes
    /// at 0 flees from 6 fights precisely BECAUSE this line matched nothing). Left unmatched, the panel
    /// sees an unexplained fight end instead.</para>
    /// </summary>
    private static readonly Regex NpcFleeFailed = new(
        $@"^{NpcSubject} has fled by trying to go \w+\.$", RegexOptions.Compiled);
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
    /// experience level, for no escape.</para>
    /// </summary>
    private static readonly Regex YouFleeFailed = new(
        @"^You have fled by trying to go \w+\.$", RegexOptions.Compiled);
    /// <summary>
    /// "You can fight it no longer." and its object variants - "him", "her", and the form that names
    /// the creature outright: "You can fight the wyvern no longer."
    ///
    /// <para>The pronoun forms are all over the captures (it 14, him 4, her 1). They trail every
    /// kind of end indifferently - 11 a real flee, 8 a FAILED one, 1 a death - which is the point: the sentence is a generic
    /// acknowledgment and its object slot tells us nothing about what happened. The named form is the
    /// one that matters: it carries a creature name, so unlike the pronouns it CAN close a fight on
    /// its own - see the handler.</para>
    /// </summary>
    private static readonly Regex FightEndOther = new(
        @"^You can fight (?:it|him|her|them|the (?<npc>.+?)) no longer\.$", RegexOptions.Compiled);

    /// <summary>
    /// "The wyvern drops dead, poisoned..." - the creature died, and NOT from the player's blow
    /// landing last, so no "You have killed the X." is printed anywhere in the frame. Verbatim;
    /// the full frame is in <see cref="CombatEventKind.NpcDied"/>.
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
    /// need not be the one asked for. In session-rec.mud2.co.uk.20260819-001608 the player sent
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
    /// <summary>
    /// "The zombie has started to use the fork to fight!", and from an opponent the player cannot
    /// see, "Someone has started to use something to fight!" (verbatim).
    ///
    /// <para>The anonymous form loses the weapon as well as the creature, and unlike the creature
    /// the weapon cannot be recovered - nothing else in the frame names it. So it is reported with a
    /// null weapon rather than with the word "something", which would file a real NPC weapon
    /// statistic under an object that does not exist.</para>
    /// </summary>
    private static readonly Regex NpcWeaponEquip = new(
        $@"^{NpcSubject} has started to use (?:the (?<weapon>.+?)|something) to fight!$", RegexOptions.Compiled);

    /// <summary>
    /// The Creature turned invisible, in either of the two wordings on the wire - and they arrive
    /// under DIFFERENT codes, which is why this pattern is load-bearing for one of them.
    ///
    /// <para>"The man fades from view." - the Creature went invisible by itself. Under C1 04.00.05,
    /// which is what detects it (<see cref="LineKind.CreatureInvisible"/>); the pattern only reads the
    /// name out, and the code carries the line when the wording is one we do not know.</para>
    ///
    /// <para>"The zombie9 has become invisible!" - the PLAYER made it invisible (`invis z9` ->
    /// "Your spell worked!" -> this line). Under C1 11.00, the caster's spell-result code, NOT
    /// 04.00.05: two occurrences in the corpus, both the player's casts, neither carrying the
    /// Creature code. Nothing tags that line, so the wording alone is what detects it here. Before it
    /// was accepted, the Creature simply became "someone" on the next line with nothing faded, and
    /// its kill ("You have killed someone.") could only be attributed by luck.</para>
    /// </summary>
    private static readonly Regex NpcFadedFromView = new(
        $@"^{NamedSubject} (?:fades from view\.|has become invisible!)$", RegexOptions.Compiled);

    /// <summary>
    /// "The man has regained his visibleness!" - verbatim, and the end of the anonymity. Observed
    /// under C1 11.01 ("disabling spell ends"), which is shared with blind/deaf/dumb/cripple/glow
    /// and so cannot identify this on its own: here the prose is the discriminator and the code is
    /// not read, which is the reverse of <see cref="NpcFadedFromView"/> and of every other pairing
    /// in this file.
    ///
    /// <para>Matched so that it reaches no later pattern, and acted on by nothing: a visibility line
    /// promotes no parked row, only a swing carrying the name does (docs/unseen-presentation.md).</para>
    ///
    /// <para>The possessive varies with the creature, so it is left loose. Nothing else in the
    /// sentence is.</para>
    /// </summary>
    private static readonly Regex NpcRegainedVisibility = new(
        $@"^{NamedSubject} (?:has regained \w+ visibleness|has suddenly become visible)!$", RegexOptions.Compiled);

    /// <summary>"You have just been woken up!" and "The man has woken up!" - verbatim. Starts are
    /// missing beside these (docs/evidence/unseen-corpus.md, missed starts), so the book stops
    /// counting for the rest of the encounter (<see cref="UnseenBook.WokenUp"/>).</summary>
    private static readonly Regex WokenUp = new(
        $@"^(?:You have just been woken up|{NpcSubject} has woken up)!$", RegexOptions.Compiled);
    private static readonly Regex WeaponSwitch = new(
        @"^You drop your guard as you switch from using the (?<from>.+?) to the (?<to>.+?)\.$", RegexOptions.Compiled);
    private static readonly Regex WeaponBroke = new(@"^The (?<weapon>.+?) breaks to bits\.$", RegexOptions.Compiled);

    /// <summary>The wield-refusal line. Two very different causes share it:
    ///   1. the weapon just broke, so it no longer exists to fight with (observed live: "The dagger0
    ///      breaks to bits." immediately followed by this), and
    ///   2. MUD2 REFUSING a wield because the player cannot handle that weapon right now - the
    ///      hidden gate on effective strength, which is itself depressed by carried weight and by
    ///      low stamina.
    /// Cause 2 is the only direct evidence of that gate MUD2 ever emits. Recording the refusal
    /// together with the stats at that instant is what would let the threshold be bracketed.
    /// The two causes are not distinguishable from this line alone; a break arriving immediately
    /// before it is the only signal, and the consumer decides what to make of that.</summary>
    private static readonly Regex WeaponUnusable = new(
        @"^You cannot use the (?<weapon>.+?) to fight now!$", RegexOptions.Compiled);
    /// <summary>
    /// "The water-snake5 has a stamina lying between 90 and 99." - the stethoscope's `diagnose` read.
    ///
    /// <para><b>MUD2 does report NPC stamina after all.</b> It is a probe rather than free
    /// telemetry - it needs a stethoscope and a typed command - but it is a direct, bracketed
    /// reading of the number everything else was approximating, and it is now recorded
    /// (the store's <c>npc_stamina_reads</c> table) and consumed as the strongest constraint the
    /// remaining-stamina model has (see MudSharp.Combat.NpcRemainingStamina).</para>
    ///
    /// <para>Worth parsing chiefly as an instrument: it is the only way to CHECK a published creature
    /// stamina against the live game, and published figures are hypotheses until our own data
    /// settles them. Observed live: giant snake 117-126, water-snake5
    /// 90-99 (published 90), viper 18-27 (published 20).</para>
    /// </summary>
    private static readonly Regex NpcStaminaRead = new(
        @"^The (?<npc>.+?) has a stamina lying between (?<lo>\d+) and (?<hi>\d+)\.$", RegexOptions.Compiled);


    private static readonly Regex GuardConfusion = new(
        @"^Your guard drops momentarily in your confusion\.$", RegexOptions.Compiled);

    /// <summary>The narrative precursor to the player's death. Informational only; it shares its
    /// frame with the "has killed you" line that follows, so it is no earlier a warning than the
    /// death itself. Matched purely so it stops being an unexplained line in the one frame nobody
    /// wants to be guessing about.
    ///
    /// <para><b>It is a GENERATED PAIRING, not a fixed string.</b> The shape is
    /// <c>You feel your {life|vitality|very soul} {concluding|stopping|terminating}...</c>: three
    /// observed subjects (life, vitality, very soul) across six distinct verb phrases, including
    /// two-word ones - "snatched away", "seizing up" - out of at least nine possible combinations.
    /// Enumerating full strings would keep losing one death at a time.</para>
    ///
    /// <para>The SUBJECT is pinned to the three observed values and the verb phrase left loose,
    /// rather than the other way round: that is where the evidence is. A loose subject would also
    /// start swallowing ordinary prose, since "You feel your ..." opens plenty of lines that are not
    /// deaths.</para>
    ///
    /// <para>Residual risk, stated because it is real: a NEW subject is still missed. The actual fix
    /// is the C1 code - every observed member carries <c>08.09</c>, with the container left UNCLOSED
    /// at end of line - and a matcher keying on that would not care about the prose at all. This
    /// pattern is the belt to those braces.</para></summary>
    private static readonly Regex LifeConcluding = new(
        @"^You feel your (?:life|vitality|very soul) [a-z][a-z ]{2,18}\.\.\.$",
        RegexOptions.Compiled);

    // Named participants currently engaged (case-insensitive). The unseen group is not here: its
    // open participants are the book's slots - see OpenParticipants.
    private readonly HashSet<string> _active = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The unseen group of the current encounter, or of the last one until the next opens.
    /// Created when an encounter opens, closed when it closes.</summary>
    private UnseenBook? _book;

    /// <summary>The award key of each ending the book was handed: the participant name the ending's
    /// event carried and its timestamp. Per book.</summary>
    private readonly Dictionary<int, UnseenAward> _stamps = [];

    /// <summary>Rows whose book-assigned outcome has been reported as an
    /// <see cref="CombatEventKind.UnseenRowEnded"/> event. Per book.</summary>
    private readonly HashSet<UnseenRow> _reported = [];

    /// <summary>What the last <see cref="CombatEventKind.UnseenChanged"/> carried. Per book.</summary>
    private UnseenSnapshot _published = UnseenSnapshot.Empty;

    /// <summary>The <see cref="UnseenBook.Version"/> the last snapshot was built from, or -1 when none
    /// has been built from this book. Per book.</summary>
    private int _publishedVersion = -1;

    /// <summary>Snapshots built since this tracker was made: a test seam for the rule that a line
    /// which changes nothing in the book builds nothing.</summary>
    internal int SnapshotsBuilt { get; private set; }

    /// <summary>When the current encounter opened: where the history places a group badge before any
    /// ending has, so the snapshot stays equal from line to line until something real changes.</summary>
    private DateTime _openedUtc;

    /// <summary>The latest timestamp given this encounter to an ending event naming the unseen group. Several of
    /// them can land in one read under one clock value, and the award pairing keys an ending by name
    /// and time, so each is placed a tick after the one before.</summary>
    private DateTime _lastGroupEnding;

    /// <summary>A wake-up line arrived in the frame being read, before any encounter was open to
    /// hand it to.</summary>
    private bool _wokenThisFrame;

    /// <summary>Whether the PLAYER cannot see - blind, or in a dark room - as last reported by
    /// <see cref="NoteCannotSee"/>. Player state, not encounter state: it is deliberately NOT cleared
    /// when an encounter closes, because the condition outlives the fight, and an encounter that
    /// opens while it holds starts with every row parked.</summary>
    private bool _cannotSee;

    /// <summary>Exposed for the wiring tests only: the flag as the two sources last left it.</summary>
    internal bool CannotSee => _cannotSee;

    /// <summary>The participants still open, from the book (<see cref="UnseenBook.OpenParticipants"/>;
    /// every named Creature in <see cref="_active"/> has an open row there). The encounter closes when
    /// this reaches zero.</summary>
    private int OpenParticipants => _book is { Closed: false } book ? book.OpenParticipants : 0;

    // The encounter closes the instant OpenParticipants reaches zero, whether that was a kill, a
    // flee, or a withdrawal - see Ending(). Begin() keeps the SAME encounter open for as long as any
    // participant is open, so a genuine pack fight (new participants joining while others are still engaged)
    // stays one encounter; a NEW mob attacking after the encounter had already fully ended is a new
    // encounter. Any residual need to keep capturing trailing prose (score, "has just passed on",
    // dropped items) after the close is a LOGGING concern, not a combat-state one - see
    // ClogWriter's own tail-capture, which runs until the next prompt regardless of whether a new
    // encounter starts in the meantime.
    private bool _encounterOpen;

    /// <summary>
    /// Has a fight already ended in the frame currently arriving? Set by every ending and EndAll, cleared at
    /// the frame boundary and when an encounter opens. Read only by
    /// <see cref="SoleOnFightEnd"/> - see there for the pack fight it exists to stop wrecking.
    ///
    /// <para><b>The frame boundary is the prompt</b>, which reaches this class as a partial line.
    /// Three things produce one: ClosePromptContext (the prompt itself, gated on PromptAllowed),
    /// MuckaConnection's EmitPartial (gated on <c>!InGameMode</c> - pre-game text like "Account ID:"
    /// with no newline, so never during a fight), and ShowPrompt, called unconditionally on every C98.
    /// That third one is the reason to check this rather than assume it.</para>
    ///
    /// <para>Checked, over 33 captures: of the 20 frames where a fight end is followed by its trailing
    /// 08.12 echo, NONE has a C98 between the two - so nothing lapses the suppression in the gap it
    /// has to cover. C98 itself lands either at a line start (50 of 70) or after nothing but a
    /// sentence's closing "." (20 of 70), which is the line tail being flushed immediately ahead of
    /// that frame's prompt. Both are frame boundaries, which is what the code assumes and what the
    /// project's own C98 note ("always precedes the C01 prompt preamble") already said.</para>
    ///
    /// <para>A frame whose prompt is DISCARDED - the FES heartbeat's, which produces no visible output
    /// - leaves the flag set for longer than one frame. That errs toward not rescuing, which is the
    /// safe direction: the cost is a fight left open for the room-change backstop to close, against
    /// the alternative of closing a creature that is still swinging.</para>
    /// </summary>
    private bool _endedThisFrame;

    public bool InCombat => _encounterOpen;

    /// <summary>Fires whenever <see cref="InCombat"/> flips (true = encounter started).</summary>
    public event Action<bool>? InCombatChanged;

    /// <summary>Fires for every classified combat line, in order, while (or just as) InCombat.</summary>
    public event Action<CombatEvent>? EventOccurred;

    /// <summary>
    /// Fires exactly when a name becomes newly active - i.e. <see cref="Begin"/> adds it to a
    /// roster it was not already part of. NOT the same as an explicit fight-start line: several
    /// kinds call <see cref="Begin"/> defensively for a participant that spoke no aggro line of its
    /// own (a pack member first seen only via a landed blow, or an NPC's own weapon-equip line -
    /// see YouHit/NpcHitsYou/NpcWeaponEquip's own remarks), and this is what catches those too. A
    /// re-engagement after a genuine drop from the roster (e.g. a failed flee that later resumes as
    /// a fresh <see cref="CombatEventKind.FightStart"/>) also fires again, because the name really
    /// did leave and come back. Exists so a consumer that has to learn something about "every
    /// creature that has ever actually fought" (e.g. the value probe) does not have to duplicate
    /// this class's own idea of what counts as new.
    /// </summary>
    public event Action<string>? ParticipantJoined;

    /// <summary>
    /// Classify one completed line. Cheap no-op for the overwhelming majority of lines
    /// (a plain-text prefix check would help further, but regex-per-candidate is already
    /// negligible next to network I/O - see EffectTracker for the equivalent trade-off).
    /// </summary>
    public void Observe(StyledLine line, DateTime timestampUtc)
    {
        lock (_gate)
            ObserveLocked(line, timestampUtc);
    }

    private void ObserveLocked(StyledLine line, DateTime timestampUtc)
    {
        // The prompt closes a frame, and in game mode it is the only partial line there is (see
        // _endedThisFrame). Whatever ended in the frame just gone cannot be echoed by a line in the
        // next one, so the suppression lapses here rather than lasting the whole encounter.
        if (line.IsPartial)
        {
            _endedThisFrame = false;
            _wokenThisFrame = false;
        }

        var text = line.PlainText;
        if (string.IsNullOrEmpty(text))
            return;

        Match m;
        if ((m = PlayerAttackStart.Match(text)).Success)
        {
            var who = ActorOf(m);
            StartFight(who, timestampUtc);
            Emit(timestampUtc, CombatEventKind.FightStart, CombatActor.Player, NameOf(who), m.Groups["weapon"].Value, null, null, text);
            PublishUnseen(timestampUtc);
        }
        else if ((m = PlayerAttackStartUnarmed.Match(text)).Success)
        {
            // Bare-handed opening. Weapon is deliberately null: the fight really did start unarmed,
            // and a "use <weapon>" issued a moment later arrives as its own WeaponEquip event.
            //
            // The anonymous form is the one that hurts if it is missed: "You attack someone." is
            // verbatim from the wire, answering `k man` against a man who had turned invisible, and
            // it is the ONLY line opening that encounter. Unmatched, the whole fight happens with
            // the client believing there is no fight.
            var who = ActorOf(m);
            StartFight(who, timestampUtc);
            Emit(timestampUtc, CombatEventKind.FightStart, CombatActor.Player, NameOf(who), null, null, null, text);
            PublishUnseen(timestampUtc);
        }
        else if ((m = NpcAggroStart.Match(text)).Success)
        {
            StartFight(UnseenActor.Named(m.Groups["npc"].Value), timestampUtc);
            Emit(timestampUtc, CombatEventKind.FightStart, CombatActor.Npc, m.Groups["npc"].Value, null, null, null, text);
            PublishUnseen(timestampUtc);
        }
        else if ((m = NpcAboutToAttack.Match(text)).Success)
        {
            // Named: the Creature it names. Anonymous: a NEW participant carrying the word. An
            // engaged Creature does not announce that it is about to attack; this line is how an
            // opponent the player cannot see joins, and it is the count of such opponents (see
            // LineKind.FightStart).
            var who = ActorOf(m);
            StartFight(who, timestampUtc);
            Emit(timestampUtc, CombatEventKind.FightStart, CombatActor.Npc, NameOf(who), null, null, null, text);
            PublishUnseen(timestampUtc);
        }
        else if ((m = YouHit.Match(text)).Success)
        {
            // A pack fight can have NPCs that never spoke an explicit aggro line (e.g. only a
            // "bares its razor-sharp incisors at you" join message we don't classify as a start)
            // yet still trade blows with the player once another named participant is killed.
            // Any hit/miss line is itself proof that NPC is an active combat participant, so it
            // must (re)join _active here - otherwise killing the one NPC that DID get an explicit
            // Begin() empties _active and spuriously closes/reopens the encounter mid-pack-fight.
            //
            // Every number parsed here is ASCII digits off the wire. MUD2 has no notion of a
            // locale, so the reader's culture must never reach these - invariant throughout.
            var lo = int.Parse(m.Groups["lo"].Value, CultureInfo.InvariantCulture);
            var hi = int.Parse(m.Groups["hi"].Value, CultureInfo.InvariantCulture);
            var who = ActorOf(m);
            Swing(who, timestampUtc);
            Emit(timestampUtc, CombatEventKind.Hit, CombatActor.Player, NameOf(who), null, lo, hi, text);
            PublishUnseen(timestampUtc);
        }
        else if ((m = YouHitExact.Match(text)).Success)
        {
            // One exact figure instead of a bracket - cause unknown, see YouHitExact. Reported as a
            // zero-width range so
            // that every consumer averaging RangeLow/RangeHigh lands on the exact value unchanged -
            // see YouHitExact. Matched AFTER YouHit only for readability; the two cannot collide,
            // since "(5-9)" cannot satisfy a pattern demanding digits-then-close-paren.
            var exact = int.Parse(m.Groups["dmg"].Value, CultureInfo.InvariantCulture);
            var who = ActorOf(m);
            Swing(who, timestampUtc);
            Emit(timestampUtc, CombatEventKind.Hit, CombatActor.Player, NameOf(who), null,
                exact, exact, text);
            PublishUnseen(timestampUtc);
        }
        else if ((m = YouMiss.Match(text)).Success)
        {
            var who = ActorOf(m);
            Swing(who, timestampUtc);
            Emit(timestampUtc, CombatEventKind.Miss, CombatActor.Player, NameOf(who), null, null, null, text);
            PublishUnseen(timestampUtc);
        }
        else if ((m = NpcHitsYou.Match(text)).Success)
        {
            var who = ActorOf(m);
            Swing(who, timestampUtc);
            Emit(timestampUtc, CombatEventKind.HitByNpc, CombatActor.Npc, NameOf(who), null,
                int.Parse(m.Groups["cur"].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups["max"].Value, CultureInfo.InvariantCulture), text);
            PublishUnseen(timestampUtc);
        }
        else if ((m = NpcHitsYouBare.Match(text)).Success)
        {
            // The fatal blow, which carries no "(cur/max)" because no stamina survives it. Null
            // ranges: the consumers treat a missing stamina reading as "no reading", which is the
            // truth here - inventing 0 would look like a measurement and would poison the running
            // stamina baseline the damage-taken deltas are derived from.
            var who = ActorOf(m);
            Swing(who, timestampUtc);
            Emit(timestampUtc, CombatEventKind.HitByNpc, CombatActor.Npc, NameOf(who), null,
                null, null, text);
            PublishUnseen(timestampUtc);
        }
        else if ((m = NpcMissesYou.Match(text)).Success)
        {
            var who = ActorOf(m);
            Swing(who, timestampUtc);
            Emit(timestampUtc, CombatEventKind.MissByNpc, CombatActor.Npc, NameOf(who), null, null, null, text);
            PublishUnseen(timestampUtc);
        }
        else if ((m = WithdrawOffer.Match(text)).Success)
        {
            // An offer only - does not end the fight until the NPC's own line accepts it.
            Emit(timestampUtc, CombatEventKind.WithdrawOffer, CombatActor.Player, NameOf(ActorOf(m)), null, null, null, text);
        }
        else if ((m = NpcWithdrawOffer.Match(text)).Success)
        {
            // The mirror image, and equally not an end: the creature is asking, and the fight runs on
            // until the player answers (3 of the 5 captured offers are followed by the player killing
            // that same creature in the next frame). So no ending, and deliberately no Begin() either -
            // exactly as the player's own offer above does neither. Begin() would let a line that is
            // not a swing open an encounter, and every creature that has ever printed this was already
            // trading blows on the lines immediately above it, so there is nothing for it to rescue.
            // Actor is the NPC: it is the creature making the offer.
            Emit(timestampUtc, CombatEventKind.NpcWithdrawOffer, CombatActor.Npc, NameOf(ActorOf(m)), null, null, null, text);
        }
        else if ((m = YouKilled.Match(text)).Success)
        {
            // Written from the player's side, so always an ending of the player's own fight, named or
            // not. Emitted BEFORE the encounter can close: closing flips InCombat to false (e.g. a
            // ClogWriter listening to InCombatChanged) - the closing line itself must still land
            // inside that encounter's record, not be dropped after it's already shut.
            Ending(ActorOf(m), UnseenOutcome.Killed, ownFight: true,
                timestampUtc, CombatEventKind.Kill, CombatActor.Player, text);
        }
        else if ((m = NpcDroppedDead.Match(text)).Success)
        {
            // The eighth end - you lost the creature (FightOutcome.NoMore). It died without the
            // player landing the last blow, so no "You have killed the X." arrives to close it. Actor is the NPC, not the player -
            // whatever finished it, it was not our swing, and the line does not say whose poison it
            // was. Uncoded, so only for a creature engaged; a named ending of an engaged creature is
            // its row's whatever the code says.
            var deadNpc = m.Groups["npc"].Value;
            if (_active.Contains(deadNpc))
                Ending(UnseenActor.Named(deadNpc), UnseenOutcome.Died, ownFight: true,
                    timestampUtc, CombatEventKind.NpcDied, CombatActor.Npc, text);
        }
        else if ((m = NpcPassedOn.Match(text)).Success)
        {
            // Backstop, not a terminator: after any matched end this creature is already gone from
            // _active and this line is the trailing prose it has always been. Still here means we
            // missed the real end, and this is MUD2's last word on the creature - so close it, and
            // let the event stand in the clog as the evidence that a line went unmatched.
            var goneNpc = m.Groups["npc"].Value;
            if (_active.Contains(goneNpc))
                Ending(UnseenActor.Named(goneNpc), UnseenOutcome.Died, ownFight: true,
                    timestampUtc, CombatEventKind.NpcDied, CombatActor.Npc, text);
        }
        else if ((m = NpcKilledYou.Match(text)).Success)
        {
            PlayerKilled(ActorOf(m), timestampUtc, text);
            // Player death ends the WHOLE encounter unconditionally - a dead player cannot keep
            // fighting anyone else in the same room, regardless of how many other NPCs are still
            // engaged. Using the ordinary single-NPC Ending() here would leave the rest of _active
            // dangling open (a latent bug: death is routinely followed immediately by a
            // disconnect/quit with no further lines to ever empty it), so the encounter would
            // linger open until ForceEnd's generic "reset/disconnect" close instead of a clean,
            // correctly-attributed KilledByNpc close.
            EndAll(timestampUtc);
        }
        else if ((m = NpcKilledYouNarrative.Match(text)).Success)
        {
            // Non-fightbrief phrasing carries no per-line C1 hit/miss detail at all, so this may
            // be the ONLY combat line we can classify in an entire narrative-mode fight - treat
            // it as authoritative regardless. When blind, the game says "someone" instead of naming
            // the killer - the live scenario that surfaced this gap was a single vampire that
            // blinded then slept the player before landing the killing blow - and the line then
            // names the unseen group, never a Creature.
            PlayerKilled(ActorOf(m), timestampUtc, text);
            EndAll(timestampUtc);
        }
        else if ((m = MutualWithdraw.Match(text)).Success || (m = PlayerMutualWithdraw.Match(text)).Success)
        {
            // Per-creature: a withdraw zeroes only the fight it names. It reads
            // like a player-side terminator - the player agreed to it - but it is an agreement with
            // ONE creature, and anything else in a pack goes on swinging.
            //
            // Two sentences for one event, differing in which side is the subject: the creature
            // accepted ("The banshee withdraws from your fight, and so do you.") or the player did
            // ("You withdraw from your fight with someone, and that person does too."). They mean
            // the same thing and are reported as one kind. Actor stays the NPC in both, because
            // Withdrawn is an agreement rather than an act by either side. Both say "your fight", so
            // both are endings of the player's own fight.
            Ending(ActorOf(m), UnseenOutcome.Withdrew, ownFight: true,
                timestampUtc, CombatEventKind.Withdrawn, CombatActor.Npc, text);
        }
        else if ((m = NpcFleeFailed.Match(text)).Success)
        {
            // Matched BEFORE NpcFled, because "has fled by trying to go" also contains "has fled by"
            // and the two must never be confused - see NpcFleeFailed's own remarks.
            //
            // This DOES end the fight, and it ends it for real rather than merely interrupting it:
            // fleeing ends combat with all creatures attacking the player, even when the flee fails.
            // The creature is still in the room but it is NOT still fighting - "You can fight it
            // no longer." trails this in the same frame saying so - and the player must attack again
            // to re-engage. Across 128 NpcFleeFailed events in the clog corpus, not one is followed
            // by a swing from that creature before a fresh FightStart.
            //
            // Each re-engagement is its own encounter (its own frame, its own attack command, and its
            // own weapon selection) - a fight the player walked away from stays "in combat", with no
            // line left that could ever close it, unless this closes it here.
            //
            // Per-creature, not EndAll: only this creature's fight ended, and anything else in a pack
            // is still swinging.
            CreatureFlight(line, m, UnseenOutcome.FleeFailed, CombatEventKind.NpcFleeFailed, timestampUtc, text);
        }
        else if ((m = NpcFled.Match(text)).Success)
        {
            CreatureFlight(line, m, UnseenOutcome.Fled, CombatEventKind.NpcFled, timestampUtc, text);
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
            EndAll(timestampUtc);
        }
        else if (YouFled.IsMatch(text))
        {
            // One flee command can end several simultaneous fights at once (confirmed offline:
            // a single flee line closed two concurrent rat fights) - close every active NPC.
            Emit(timestampUtc, CombatEventKind.YouFled, CombatActor.Player, null, null, null, null, text);
            EndAll(timestampUtc);
        }
        else if ((m = FightEndOther.Match(text)).Success)
        {
            // Always a trailing acknowledgment of an end already stated on an earlier line of the
            // SAME frame - so the fight it refers to is normally closed before this line is reached,
            // by NpcFleeFailed or NpcFled (or a kill, or the poison death above). Verified 27/27
            // against the research capture for the "has fled by going <dir>." case.
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
            // Named or not, it is ignored for a creature we are not fighting. MUD2 stacks several end
            // messages in a frame and this one can land AFTER the fight was already closed by another
            // of them - which is exactly the captured wyvern frame, where the poison death closes the
            // fight two lines earlier. So a name that is not on the roster is a trailing
            // acknowledgment of something already dealt with, and the right response is nothing at all.
            //
            // Not merely tidy: FightHistoryRecorder has no in-combat guard, so a named event is enough
            // to get-or-CREATE a bucket, and one created after the encounter's flush is written out by
            // the next flush as a zero-swing row - a second fight against the same creature that never
            // happened. The recorder also refuses to create a fight outside an open encounter,
            // so the two layers guard it independently; this side is pinned by
            // CombatTrackerTests.FightEndOther_NamingACreatureWeAreNotFighting_ReportsNoName.
            // A named line stays named: an engaged Creature's own ending, or - coded, with a slot
            // available - that slot's conclusion with the name kept. A named line for anyone else
            // is a bystander's and moves nothing; it never becomes an anonymous ending.
            UnseenActor? ended;
            if (m.Groups["npc"].Success)
            {
                var named = m.Groups["npc"].Value;
                ended = _active.Contains(named)
                        || (line.Kind == LineKind.FightEnd && !_endedThisFrame
                            && _book is { Closed: false, AvailableSlots: > 0 } book && book.Row(named) is null)
                    ? UnseenActor.Named(named)
                    : null;
            }
            else
                ended = SoleOnFightEnd(line, PronounWord(text));
            if (ended is { } who)
                Ending(who, UnseenOutcome.Ended, ownFight: true,
                    timestampUtc, CombatEventKind.FightEndOther, CombatActor.Player, text);
            else
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
            // encounter AGAINST. It arrives between "You attack the thief." and the first blow. With
            // the unarmed attack form matched (see PlayerAttackStartUnarmed), the encounter is already
            // open by the time this fires and the weapon lands on it. (NpcWeaponEquip below DOES
            // Begin(), because that line does name its NPC.)
            Emit(timestampUtc, CombatEventKind.WeaponEquip, CombatActor.Player, null, m.Groups["weapon"].Value, null, null, text);
        }
        else if ((m = NpcWeaponEquip.Match(text)).Success)
        {
            // Confirmed live: "The zombie has started to use the fork to fight!" mid-fight,
            // following ordinary miss/miss lines that named it, so it's already an active
            // participant - but Begin() defensively in case this is somehow the first line
            // naming that NPC (mirrors YouHit's own defensive Begin() for the same reason).
            //
            // An invisible opponent's weapon is anonymous too ("Someone has started to use something
            // to fight!"), and unlike the creature it cannot be recovered - so the weapon is null
            // rather than the word "something". See this pattern's own remarks. The anonymous form
            // is the group's, and only inside an encounter: it is not a start and opens nothing.
            var who = ActorOf(m);
            if (who.Name is { } armed)
            {
                Begin(armed, timestampUtc);
                // A Creature first named by its weapon line is engaged: it gets its row, so the
                // encounter cannot close around it.
                if (_book!.Row(armed) is not { Open: true })
                    _book.Start(who);
            }
            if (who.Name is not null || _encounterOpen)
                Emit(timestampUtc, CombatEventKind.NpcWeaponEquip, CombatActor.Npc, NameOf(who),
                    m.Groups["weapon"].Success ? m.Groups["weapon"].Value : null, null, null, text);
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
        else if (line.Kind == LineKind.CreatureInvisible || NpcFadedFromView.IsMatch(text))
        {
            // The creature turned invisible. Not a state change to the fight - it runs on exactly as
            // it was - but from here MUD2 stops naming this one and writes "someone" instead, so its
            // row is parked in the unseen group until a line names it again.
            //
            // The code (04.00.05) is what detects it and the prose only reads the name out, which is
            // the arrangement LineKind.FightEnd already uses. With a wording we do not know and one
            // creature engaged, that one is parked; with several, none is. A wrong park costs a row
            // drawn inside the group for a moment, never a credit.
            //
            // Engaged creatures only, for the reason the health-rung lines give: something across
            // the room turning invisible is not this fight's business. It costs the case on the wire
            // where the man faded while fighting a FOX and the player attacked him afterwards: that
            // fight opens against the unseen group.
            var named = NpcFadedFromView.Match(text);
            var gone = named.Success ? named.Groups["npc"].Value
                     : _active.Count == 1 ? _active.First()
                     : null;
            if (gone is not null && _active.Contains(gone))
            {
                if (_book is { Closed: false } book)
                    book.Faded(gone);
                Emit(timestampUtc, CombatEventKind.NpcTurnedInvisible, CombatActor.Npc, gone, null, null, null, text);
                PublishUnseen(timestampUtc);
            }
        }
        else if (NpcRegainedVisibility.IsMatch(text))
        {
            // "The man has regained his visibleness!" - silent, and it promotes nothing: only a swing
            // line carrying the name brings a parked row back.
        }
        else if (WokenUp.IsMatch(text))
        {
            if (_book is { Closed: false } book)
                book.WokenUp();
            else
                _wokenThisFrame = true;
        }
        else if (InventoryChangeLines.TryParse(text, out var moveKind, out var movedItem, out var movedInto))
        {
            // The four loadout lines, from the one place that owns their wordings (see
            // InventoryChangeLines - MudSession fires the in-combat FES,FEI probe off the same
            // patterns, and they must not drift apart).
            //
            // A drop is only interesting to the PANEL when what hit the floor is what we were
            // fighting with: fleeing drops your weapon automatically, so it arrives in the same tick
            // as a flee with no WeaponBroke to explain it, and the panel would otherwise keep the
            // weapon on screen. All four are interesting to the CLOG, where each one paired with the
            // stat readings either side of it is one object's dexterity and strength cost.
            EventOccurred?.Invoke(new CombatEvent(
                timestampUtc, KindOf(moveKind), CombatActor.Player, null, movedItem, null, null, text,
                Container: movedInto));
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
        else if (line.Kind == LineKind.FightStart && (m = CodedStartSubject.Match(text)).Success)
        {
            // Reached only when NOTHING above matched the wording, and the server nonetheless tagged
            // this line 08.00 - a fight start. Same construction as the FightEnd branch below and
            // for the same reason: every start wording this file has ever missed arrived correctly
            // coded. The prose here only says WHICH Creature and who moved first; an unknown
            // wording costs the weapon, never the fight. Deliberately last in the chain, so the
            // wordings above keep supplying the actor and weapon they always have.
            // An anonymous subject or object opening a fight is a new slot, exactly as in
            // NpcAboutToAttack.
            CombatActor actor;
            string named;
            if (m.Groups["obj"].Success)
            {
                actor = CombatActor.Player;
                var obj = m.Groups["obj"].Value;
                named = obj.StartsWith("the ", StringComparison.Ordinal) ? obj[4..] : obj;
            }
            else
            {
                actor = CombatActor.Npc;
                var subj = m.Groups["subj"].Value;
                named = m.Groups["npc"].Success ? m.Groups["npc"].Value
                    : subj.StartsWith("The ", StringComparison.Ordinal) ? subj[4..]
                    : subj;
            }
            var who = AnonymousOpponent.Canonical(named) is { } word
                ? UnseenActor.Anonymous(SomeKinds.FromWord(word))
                : UnseenActor.Named(named);
            StartFight(who, timestampUtc);
            Emit(timestampUtc, CombatEventKind.FightStart, actor, NameOf(who), null, null, null, text);
            PublishUnseen(timestampUtc);
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
            if (SoleOnFightEnd(line, WordIn(text)) is { } who)
                Ending(who, UnseenOutcome.Ended, ownFight: true,
                    timestampUtc, CombatEventKind.FightEndOther, CombatActor.Player, text);
            else
                Emit(timestampUtc, CombatEventKind.FightEndOther, CombatActor.Player, null, null, null, null, text);
        }
    }

    /// <summary>
    /// The one creature that a nameless but server-confirmed fight end can only be about, or null
    /// when that is a guess.
    ///
    /// <para>Requires the C1 code (<see cref="LineKind.FightEnd"/>): the prose alone is never
    /// trusted to close a fight it does not name.</para>
    ///
    /// <para><b>And requires that nothing has ended in this frame yet.</b> In a pack, MUD2 kills one
    /// creature and then prints the trailing "You can fight it no longer." for THAT fight, in the
    /// same frame:
    /// <code>
    /// The goat0 is glaring at you madly.
    /// The ram1 is glaring at you madly.
    /// You have killed the goat0.        &lt;- closes goat0, leaving ram1 the only one active
    /// You can fight it no longer.      &lt;- refers to goat0, but "exactly one active" now means ram1
    /// </code>
    /// so without this guard the survivor's fight would be closed while it was still swinging.</para>
    ///
    /// <para>So once anything has ended in the frame being read, an unnamed end is assumed to be
    /// acknowledging that one and closes nothing. Frame scope is what the evidence supports and no
    /// more: the class's load-bearing guarantee is that every end prints inside a single frame, so an
    /// echo cannot be separated from its own end by a prompt - not the whole encounter, since a
    /// pack's last survivor can have a genuinely unmatched end frames after an unrelated creature
    /// died earlier in the same encounter.</para>
    ///
    /// <para>Where it does abstain, the fight stays open, with <see cref="NoteRoomChanged"/> as the
    /// floor.</para>
    ///
    /// <para>Best-effort by nature. Same caution as NpcKilledYouNarrative's "sole active participant":
    /// our roster is what we managed to observe, not necessarily what is in the room.</para>
    ///
    /// <para><b>Who it can be about.</b> One named Creature and no anonymous slot: that Creature, unless
    /// it is parked in the unseen group - then the line is an anonymous ending, and the book's counting
    /// decides whose it was. Any anonymous slot open: an anonymous ending (with one slot and no row,
    /// of that slot's word), so a fight against an opponent the player never saw named - an ogre dying
    /// of poison in the dark - can still close. Otherwise nothing. <paramref name="word"/> is the word
    /// the line's pronoun points at.</para>
    /// </summary>
    private UnseenActor? SoleOnFightEnd(StyledLine line, SomeKind word)
    {
        if (line.Kind != LineKind.FightEnd || _endedThisFrame || _book is not { Closed: false } book)
            return null;
        if (_active.Count == 1 && book.AvailableSlots == 0)
        {
            var only = _active.First();
            return book.Row(only) is { Parked: true } ? UnseenActor.Anonymous(word) : UnseenActor.Named(only);
        }
        if (book.AvailableSlots == 1 && book.OpenRowCount == 0)
            return UnseenActor.Anonymous(book.AvailableSlotsOf(SomeKind.Someone) == 1 ? SomeKind.Someone : SomeKind.Something);
        if (book.AvailableSlots > 0)
            return UnseenActor.Anonymous(word);
        return null;
    }

    /// <summary>The word a "You can fight it/him/her no longer." pronoun points at: "it" an animal,
    /// "him" and "her" a person - the same split MUD2 makes between "something" and
    /// "someone".</summary>
    private static SomeKind PronounWord(string text)
        => text.Contains(" him ", StringComparison.Ordinal) || text.Contains(" her ", StringComparison.Ordinal)
            ? SomeKind.Someone
            : SomeKinds.Default;

    /// <summary>The anonymous word an unrecognised line uses anywhere in it, verbatim: "You can fight
    /// someone no longer." (observed coded 08.12) names no Creature and still says which word.</summary>
    private static SomeKind WordIn(string text)
        => text.Contains(AnonymousOpponent.Person, StringComparison.OrdinalIgnoreCase) ? SomeKind.Someone
         : text.Contains(AnonymousOpponent.Thing, StringComparison.OrdinalIgnoreCase) ? SomeKind.Something
         : SomeKinds.Default;

    /// <summary>The subject or object of a matched line: the Creature it names, or the anonymous word
    /// it used.</summary>
    private static UnseenActor ActorOf(Match m)
        => m.Groups["npc"].Success
            ? UnseenActor.Named(m.Groups["npc"].Value)
            : UnseenActor.Anonymous(SomeKinds.FromWord(m.Groups["anon"].Value));

    /// <summary>The participant name an event carries: the Creature, or the unseen group for an
    /// anonymous line. An anonymous line never names a Creature.</summary>
    private static string NameOf(UnseenActor who) => who.Name ?? AnonymousOpponent.Group;

    /// <summary>
    /// Whether the player can currently see, from whichever source reports it. Two conditions
    /// produce the same anonymity on the wire and this class does not distinguish them:
    ///
    /// <list type="bullet">
    /// <item><b>Blind</b> - the coded <c>&lt;11.00&gt;You have suddenly and magically gone blind!</c>
    /// and <c>&lt;11.01&gt;You have suddenly and magically regained your sight!</c> lines the frame
    /// they land, and the FES heartbeat's flag on every genuine reply (authoritative, up to one
    /// heartbeat late, and the only signal on a relog into an already-blind persona), and an
    /// items reply whose room section is <c>--</c>.</item>
    /// <item><b>Dark</b> - no code and no FES column. An items reply whose room section is
    /// <c>oo</c> starts it and any other complete items reply ends it; between replies the game's
    /// own prose carries it: "It's too dark to see now." and "You move in the darkness..." start
    /// it, "It's light enough to see now!" and any coded room entry end it.</item>
    /// </list>
    ///
    /// <para>A level, not an edge - a repeat of the current value is a no-op - so the FES path
    /// clears a blind the coded line set even when no heartbeat ever saw the blind state itself.
    /// <paramref name="reason"/> names the signal that changed it and is recorded verbatim in the
    /// synthesised event's raw text, exactly as <see cref="ForceEnd"/>'s is, so a clog says which
    /// source spoke.</para>
    /// </summary>
    public void NoteCannotSee(bool cannotSee, DateTime timestampUtc, string reason)
    {
        lock (_gate)
        {
            if (cannotSee == _cannotSee)
                return;
            _cannotSee = cannotSee;
            // Sight lost parks every open row; sight regained promotes none (only a named swing
            // does). An encounter that opens later while the player cannot see starts its book
            // that way - see Begin.
            if (_encounterOpen && _book is { Closed: false } book)
            {
                if (cannotSee)
                    book.SightLost();
                else
                    book.SightRegained();
            }
            // Emitted whether or not a fight is open: the loss of sight routinely precedes the fight
            // it explains (a dark room, then something attacks), and the consumers that only care
            // inside an encounter already drop events arriving outside one.
            Emit(timestampUtc,
                cannotSee ? CombatEventKind.SightLost : CombatEventKind.SightRegained,
                null, null, null, null, null,
                $"({(cannotSee ? "sight lost" : "sight regained")}: {reason})");
            PublishUnseen(timestampUtc);
        }
    }

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
    /// <para><b>You cannot walk out of a fight in MUD2.</b> Movement is refused while fighting, and
    /// leaving costs a flee - which prints its own line and is already handled. So a room change is
    /// proof the fight is over, whatever we think, and it is the one such proof that does not depend
    /// on having matched any particular sentence. That makes it the right backstop for an end
    /// phrased in a way nothing here matches, which would otherwise leave combat stuck until
    /// logout.</para>
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

    /// <summary>
    /// Emits <see cref="CombatEventKind.EncounterForceEnded"/>, NOT <see cref="CombatEventKind.FightEndOther"/>:
    /// this event means "every open fight is over", where an unnamed FightEndOther means "some fight
    /// already stated as ended is being acknowledged, without saying which". The aggregator rightly
    /// refuses to close a pack's other participants on an unnamed line, so a reset reported the second
    /// way leaves every fight live on the rail. Emitted before the state teardown below so a consumer
    /// that acts on InCombatChanged(false) sees the fights already resolved.
    /// </summary>
    private void ForceEndLocked(DateTime timestampUtc, string reason)
    {
        if (!InCombat)
            return;
        Emit(timestampUtc, CombatEventKind.EncounterForceEnded, null, null, null, null, null, $"(forced end: {reason})");
        _active.Clear();
        CloseEncounter(timestampUtc);
    }

    /// <summary>A named Creature joins <see cref="_active"/>, and the encounter opens if it is not open
    /// yet - with a fresh <see cref="UnseenBook"/>. Null for an anonymous line, which only opens the
    /// encounter: the unseen group's participants are the book's slots.</summary>
    private void Begin(string? npc, DateTime timestampUtc)
    {
        if (npc is not null && _active.Add(npc))
            ParticipantJoined?.Invoke(npc);
        if (_encounterOpen)
            return;
        _encounterOpen = true;
        _openedUtc = timestampUtc;
        _lastGroupEnding = DateTime.MinValue;
        _book = new UnseenBook(SomeKindKnowledge.Known);
        _stamps.Clear();
        _reported.Clear();
        _published = UnseenSnapshot.Empty;
        _publishedVersion = -1;
        if (_cannotSee)
            _book.SightLost();
        if (_wokenThisFrame)
            _book.WokenUp();
        // Load-bearing, not redundant with the frame boundary: MUD2 will close one encounter and
        // open the next in the SAME frame (see this class's remarks), and with no prompt between
        // them this is the only thing that clears a true left by the previous encounter's last
        // kill. Without it the new encounter's first unnamed end could not be rescued.
        _endedThisFrame = false;
        InCombatChanged?.Invoke(true);
    }

    /// <summary>A fight OPENING on a coded start: a named Creature's row, or one more anonymous slot -
    /// the only place the slot count rises, because every attacker announces itself.</summary>
    private void StartFight(UnseenActor who, DateTime timestampUtc)
    {
        Begin(who.Name, timestampUtc);
        _book!.Start(who);
    }

    /// <summary>A swing, either direction. A named one is its Creature's; an anonymous one is the
    /// group's. An anonymous blow that nothing open could have thrown is an opponent whose start was
    /// missed, and opens a slot for it.</summary>
    private void Swing(UnseenActor who, DateTime timestampUtc)
    {
        Begin(who.Name, timestampUtc);
        var book = _book!;
        // A missed start: nothing open could have thrown it - no slot of its word, no open row whose
        // class could produce the word - so it opens a slot. Otherwise the swing parks the rows that
        // could have thrown it, and no slot opens.
        if (who.Name is null && !book.HasOpenProducer(who.Word))
            book.Start(who);
        book.Swing(who);
    }

    /// <summary>
    /// An ending line: the book is handed it, the event goes out under the name the book's answer
    /// gives (the Creature, or the unseen group for an anonymous ending and for a named ending that
    /// concluded an anonymous slot), then the encounter closes if nothing is left open. With no
    /// encounter open the event still goes out, and nothing else happens.
    /// </summary>
    private void Ending(UnseenActor who, UnseenOutcome outcome, bool ownFight, DateTime timestampUtc,
        CombatEventKind kind, CombatActor actor, string text)
    {
        if (_book is not { Closed: false } book)
        {
            Emit(timestampUtc, kind, actor, NameOf(who), null, null, null, text);
            return;
        }
        var id = book.End(who, outcome, ownFight);
        if (id < 0)
        {
            // A plain room-wide line, or a named ending the book has neither a row nor an available
            // slot for: it may park rows, and it concludes nothing. A named one still goes out under
            // its name, as with no encounter open.
            if (ownFight && who.Name is not null)
            {
                Emit(timestampUtc, kind, actor, who.Name, null, null, null, text);
                _endedThisFrame = true;
            }
            PublishUnseen(timestampUtc);
            return;
        }
        var concludedSlot = who.Name is null || book.ConclusionOf(id) is not null;
        var name = concludedSlot ? AnonymousOpponent.Group : who.Name!;
        var at = timestampUtc;
        if (concludedSlot)
        {
            if (at <= _lastGroupEnding)
                at = _lastGroupEnding.AddTicks(1);
            _lastGroupEnding = at;
        }
        _stamps[id] = new UnseenAward(name, at);
        Emit(at, kind, actor, name, null, null, null, text, concludedName: concludedSlot ? who.Name : null);
        if (!concludedSlot)
            _active.Remove(name);
        _endedThisFrame = true;
        ReportBookOutcomes(timestampUtc);
        PublishUnseen(timestampUtc);
        if (OpenParticipants <= 0)
            CloseEncounter(timestampUtc);
    }

    /// <summary>"The X has fled by going/trying to go ..." - written from the creature's side, so
    /// anything leaving the room can print it, including a creature disengaging from somebody else.
    /// The code says whose fight it was: C08.11 marks the player's own. A named line ends an engaged
    /// Creature's row whatever its code. An uncoded line about anyone else concludes nothing; a coded
    /// one about a Creature never engaged is the conclusion of an anonymous slot while one is
    /// available (<see cref="UnseenBook.AvailableSlots"/>), and is passed by otherwise.</summary>
    private void CreatureFlight(StyledLine line, Match m, UnseenOutcome outcome, CombatEventKind kind,
        DateTime timestampUtc, string text)
    {
        if (!_encounterOpen)
            return;
        var who = ActorOf(m);
        var coded = line.Kind == LineKind.FightEnd;
        if (who.Name is { } name && !_active.Contains(name)
            && !(coded && _book is { Closed: false, AvailableSlots: > 0 }))
            return;
        Ending(who, outcome, ownFight: coded || who.Name is not null, timestampUtc, kind, CombatActor.Npc, text);
    }


    /// <summary>The player was killed. A named killer's line is the event, as ever. An unseen
    /// killer's death is also one of the group's conclusions, so the history says "killed you" and
    /// the session counts the death: the book is handed it before the encounter closes.</summary>
    private void PlayerKilled(UnseenActor who, DateTime timestampUtc, string text)
    {
        if (who.Name is null && _book is { Closed: false })
            Ending(who, UnseenOutcome.KilledYou, ownFight: true,
                timestampUtc, CombatEventKind.KilledByNpc, CombatActor.Npc, text);
        else
            Emit(timestampUtc, CombatEventKind.KilledByNpc, CombatActor.Npc, NameOf(who), null, null, null, text);
    }

    /// <summary>The player's own flight or death: every fight ends at once. The book does not model
    /// a flight; it is closed with the encounter.</summary>
    private void EndAll(DateTime timestampUtc)
    {
        _active.Clear();
        _endedThisFrame = true;
        CloseEncounter(timestampUtc);
    }

    private void CloseEncounter(DateTime timestampUtc)
    {
        if (!_encounterOpen)
            return;
        _encounterOpen = false;
        if (_book is { Closed: false } book)
        {
            book.Close();
            ReportBookOutcomes(timestampUtc);
            PublishUnseen(timestampUtc);
        }
        InCombatChanged?.Invoke(false);
    }

    /// <summary>Emits one <see cref="CombatEventKind.UnseenRowEnded"/> per row the book concluded
    /// without a line naming it - by counting, or as fate unseen at the close - and takes it off
    /// <see cref="_active"/>.</summary>
    private void ReportBookOutcomes(DateTime timestampUtc)
    {
        // After every group ending so far, so the history keeps the order the book concluded them in.
        if (timestampUtc <= _lastGroupEnding)
            timestampUtc = _lastGroupEnding.AddTicks(1);
        foreach (var row in _book!.Rows)
        {
            if (row.Open || !(row.ByCounting || row.Outcome == MudSharp.Combat.UnseenOutcome.FateUnseen)
                || !_reported.Add(row))
                continue;
            _active.Remove(row.Name);
            var outcome = UnseenSnapshot.OutcomeOf(row.Outcome!.Value);
            Emit(timestampUtc, CombatEventKind.UnseenRowEnded, CombatActor.Npc, row.Name, null, null, null,
                row.ByCounting ? $"({row.Name}: {outcome} by counting)" : $"({row.Name}: fate unseen)",
                outcome);
        }
    }

    /// <summary>Emits <see cref="CombatEventKind.UnseenChanged"/> when the book's snapshot differs from
    /// the one last published, after the line's own event. Nothing is built while
    /// <see cref="UnseenBook.Version"/> is unchanged: a blow to an open row moves nothing the snapshot
    /// carries.</summary>
    private void PublishUnseen(DateTime timestampUtc)
    {
        if (_book is null || _book.Version == _publishedVersion)
            return;
        _publishedVersion = _book.Version;
        SnapshotsBuilt++;
        var snapshot = UnseenSnapshot.From(_book,
            id => _stamps.TryGetValue(id, out var stamp) ? stamp : new UnseenAward(AnonymousOpponent.Group, _openedUtc),
            _openedUtc);
        if (snapshot.SameAs(_published))
            return;
        _published = snapshot;
        Emit(timestampUtc, CombatEventKind.UnseenChanged, null, null, null, null, null,
            $"(unseen: {snapshot.Count}{(snapshot.Parked.Count > 0 ? ", parked " + string.Join(", ", snapshot.Parked) : "")})",
            unseen: snapshot);
    }

    private void Emit(DateTime ts, CombatEventKind kind, CombatActor? actor, string? npc, string? weapon,
        int? lo, int? hi, string raw, FightOutcome? outcome = null, UnseenSnapshot? unseen = null,
        string? concludedName = null)
        => EventOccurred?.Invoke(new CombatEvent(ts, kind, actor, npc, weapon, lo, hi, raw,
            Outcome: outcome, Unseen: unseen, ConcludedName: concludedName));

    /// <summary>Which event kind reports one parsed loadout line. Four kinds rather than one because
    /// the four are different facts - see CombatEventKind.ItemStowed for why a container move must not
    /// be collapsed into a drop.</summary>
    private static CombatEventKind KindOf(InventoryChangeKind kind) => kind switch
    {
        InventoryChangeKind.Dropped   => CombatEventKind.ItemDropped,
        InventoryChangeKind.Taken     => CombatEventKind.ItemTaken,
        InventoryChangeKind.Stowed    => CombatEventKind.ItemStowed,
        _                             => CombatEventKind.ItemRetrieved,
    };
}
