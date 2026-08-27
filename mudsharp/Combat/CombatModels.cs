namespace MudSharp.Combat;

/// <summary>Who performed/received a combat action.</summary>
public enum CombatActor
{
    Player,
    Npc,
}

/// <summary>
/// Classification of a single combat-relevant line. Mirrors the event taxonomy validated
/// offline in tools/combat/reduce_combat.py against RESEARCH/mud2-multi-combat.jsonl:
/// the "08" C1 code family (FightStart/Hit/Miss/WithdrawOffer/Kill/FightEnd*) PLUS three
/// plain-prose event kinds that carry no C1 wrapper at all in observed captures
/// (WeaponEquip/WeaponBroke/DroppedGuard) — see NOTES.md "Plain-text combat-only events".
/// </summary>
public enum CombatEventKind
{
    /// <summary>An NPC joined the fight (player "You attack the X..." or NPC aggro "The X is ...").</summary>
    FightStart,
    /// <summary>
    /// "You hit the X (A-B)." - approximate damage range against an NPC.
    ///
    /// <para>Two forms, both carried here. With <c>identify</c> off MUD2 brackets the blow
    /// ("You hit the rat (5-9).") and RangeLow/RangeHigh are the bracket. With <c>identify</c> ON it
    /// reports the EXACT figure ("You hit the banshee (6).", verbatim from
    /// session-rec.mud2.co.uk.20260819-001118) and both fields carry that one number - an exact
    /// reading is simply a range of width zero, so every consumer averaging the two still lands on
    /// the right value with no special case. The exact form went unparsed until 2026-08-19, which
    /// meant turning identify on silently blinded the client to its own hits.</para>
    /// </summary>
    Hit,
    /// <summary>"You miss the X."</summary>
    Miss,
    /// <summary>
    /// "The X hits you (C/M)." - C/M is your current/max stamina, not a delta.
    ///
    /// <para>The parenthetical is not guaranteed. The KILLING blow prints bare - "The rat18 hits
    /// you." - because there is no surviving stamina to report; verbatim from
    /// session-rec.mud2.co.uk.20260819-001608's death frame. That form went unparsed until
    /// 2026-08-19, so the single most consequential hit of a session was the one the client never
    /// counted. RangeLow/RangeHigh are null for it, and the stamina relay must (and does) tolerate
    /// a null reading rather than treat it as zero.</para>
    /// </summary>
    HitByNpc,
    /// <summary>"The X misses you."</summary>
    MissByNpc,
    /// <summary>"You offer to withdraw from your fight with the X." — an offer, NOT an end.</summary>
    WithdrawOffer,
    /// <summary>"You have killed the X."</summary>
    Kill,
    /// <summary>"The X has killed you." (fightbrief), or "You have been killed by the X/someone."
    /// (narrative/non-fightbrief — confirmed live: player killed by a spellcasting vampire that
    /// put them to sleep first; "someone" appears instead of the NPC name whenever the player is
    /// blind at the moment of death — see CombatTracker's NpcKilledYouNarrative handling).</summary>
    KilledByNpc,
    /// <summary>"The X withdraws from your fight, and so do you." - mutual withdraw accepted. Ends
    /// ONLY the fight it names: the player agreed to it, but the agreement is with one creature, so
    /// anything else in a pack fight is still engaged (owner, 2026-08-19). Contrast
    /// <see cref="YouFled"/>/<see cref="YouFleeFailed"/>/<see cref="KilledByNpc"/>, which change the
    /// player's own state and therefore end everything.</summary>
    Withdrawn,
    /// <summary>"The X has fled by going &lt;dir&gt;."</summary>
    NpcFled,
    /// <summary>"You have fled by going &lt;dir&gt;." Ends EVERY currently-active fight in the encounter.</summary>
    YouFled,
    /// <summary>"You have fled by trying to go &lt;dir&gt;." - the PLAYER's flee failed. They are
    /// still in the room, but MUD2 has zeroed the fight count all the same, so this ends every
    /// currently-active fight exactly as <see cref="YouFled"/> does. Unparsed until 2026-08-19; see
    /// <see cref="FightOutcome.UFledFail"/> for the verbatim frame and for the price a failed flee
    /// still charges.</summary>
    YouFleeFailed,
    /// <summary>"You can fight it no longer." - a fight-end with no reason detail (08 12). Always a
    /// TRAILING acknowledgment of an end already stated on an earlier line of the same frame (a real
    /// flee, or - the case that fooled this client for months - a FAILED one, see
    /// <see cref="NpcFleeFailed"/>).
    ///
    /// <para><b>The object slot varies, and one of its forms names the creature.</b> Counted across
    /// every capture on disk: "it" 14, "him" 4, "her" 1, and one "the wyvern". What they trail is
    /// mixed - 11 follow a creature that really left ("has fled by going &lt;dir&gt;."), 8 follow one
    /// whose flee FAILED and never moved, and 1 follows a death. "it" appears after both flee kinds
    /// (8 failed, 6 real); the gendered forms happen to have followed only real flees, but n=5 and
    /// the likelier explanation is simply the creature's own gender. Nothing here selects the slot as
    /// far as this evidence goes - an earlier version of this comment claimed the pronoun forms
    /// always meant a creature that had left the room, which the captures flatly contradict.</para>
    ///
    /// <para>So <see cref="CombatEvent.NpcName"/> is set when the line named a creature, and null for
    /// the pronoun forms. Named, it can safely close that one fight; unnamed it stays informational,
    /// because a line that cannot say who it means must never close a fight in a pack.</para>
    /// </summary>
    FightEndOther,
    /// <summary>
    /// A weapon is now the player's active weapon. Two wordings, both landing here:
    /// "You are now using the X to fight!" (a change) and "You're using the X anyway..." (MUD2
    /// telling you it was already that weapon).
    ///
    /// <para>The second arrives when a redundant <c>k X with Y</c> / <c>use Y</c> / <c>wield Y</c> is
    /// issued mid-fight, and it matters for two reasons. It is often the ONLY line in that frame
    /// naming the weapon actually in hand - note that MUD2 answered <c>k rat with stick</c> with
    /// "You're using the unlit brand anyway..." (verbatim, session-rec.mud2.co.uk.20260819-001608),
    /// i.e. NOT the weapon asked for. And a redundant re-issue is charged as a weapon change, so it
    /// can drop the player's guard: that same capture shows four of these paired with two
    /// "Your guard drops momentarily in your confusion." lines. The guard drop arrives as its own
    /// <see cref="DroppedGuard"/> line, so nothing needs inferring here.</para>
    ///
    /// <para>MUD2 has no equipment slots (per the owner): a weapon is ordinary inventory burden, and
    /// each creature - the player included - selects one that then applies to every creature it
    /// swings at for the rest of that ENCOUNTER, until it is dropped, breaks, or is changed. That is
    /// why the weapon never carries across encounters, and why both the aggregator and the recorder
    /// re-derive it at encounter start rather than keeping the last one seen.</para>
    /// </summary>
    WeaponEquip,
    /// <summary>"The X has started to use the Y to fight!" — an NPC equips/switches to a weapon
    /// mid-fight (confirmed live: a zombie switching to a fork). The per-tick "The X hits you
    /// (N/M)." line never names a weapon, so this equip line is the only observed source of NPC
    /// weapon identity — track it, since NPC weapon choice presumably affects their damage output
    /// the same way it does the player's.</summary>
    NpcWeaponEquip,
    /// <summary>"The X breaks to bits." — the weapon in use broke mid-fight, forcing a guard drop.</summary>
    WeaponBroke,
    /// <summary>"Your guard drops..." (weapon switch or post-break confusion) — no C1 wrapper observed.</summary>
    DroppedGuard,
    /// <summary>"The X has fled by trying to go &lt;dir&gt;." - a flee that FAILED. The creature is
    /// still in the room and still hostile, but the FIGHT IS OVER (see
    /// <see cref="FightOutcome.CFledFail"/>): MUD2 breaks the sequence and the player has to attack
    /// again to re-engage. Never confuse this with <see cref="NpcFled"/> - chasing something standing
    /// in front of you is nonsense, and counting it as an escape corrupts the per-class flee rates.
    ///
    /// <para>This used NOT to end the fight, on the theory that keeping it open stopped one snake
    /// fight fragmenting into eight encounters. That was the wrong trade: eight re-engagements really
    /// are eight encounters, and refusing to close meant a fight the player simply walked away from
    /// stayed "in combat" until logout.</para></summary>
    NpcFleeFailed,
    /// <summary>"The X has a stamina lying between 90 and 99." - the stethoscope's `diagnose` read,
    /// carried in the RangeLow/RangeHigh fields. A probe, not free telemetry, but a DIRECT reading of
    /// NPC stamina - which this codebase spent four separate comments asserting the game never
    /// gives.</summary>
    NpcStaminaRead,
    /// <summary>"Axe0 dropped." - an item hit the floor, including automatically when fleeing strips
    /// the weapon from your hands. Only acted on when it names the weapon in use.</summary>
    ItemDropped,
    /// <summary>"The X looks seriously injured." - how hurt a creature is, in the game's own words
    /// and the only report of it MUD2 ever gives. Printed on the line after a landed blow, so it goes
    /// stale between hits (see NpcHealthRungs, which owns the words-to-rung mapping and the reasons
    /// the mapping is what it is).
    ///
    /// <para>Unlike every other kind here, this one must NOT start a fight: the same line appears in
    /// room descriptions, so a wounded creature standing across the room would otherwise register as
    /// an opponent.</para></summary>
    NpcHealth,
    /// <summary>"You feel your life concluding..." - the narrative death precursor, from the frame
    /// where the player was killed. Informational ONLY, and deliberately not treated as an end: it
    /// lands in the SAME frame as the "has killed you" line that follows it, so it buys no warning
    /// time and there is nothing to act on. Parsed purely so the line is accounted for rather than
    /// silently unrecognised.</summary>
    LifeConcluding,
    /// <summary>"You cannot use the X to fight now!" - the wield refusal. Fires both when the weapon
    /// has just broken and when MUD2 refuses the wield outright because effective strength (itself
    /// reduced by carried weight, and per the owner by low stamina) is below the hidden threshold
    /// for that weapon. The second case is the ONLY direct evidence of that gate the game emits,
    /// and nothing parsed this line before, so no observation of it has ever been recorded.</summary>
    WeaponUnusable,

    /// <summary>
    /// The named creature DIED, by something other than the player's own blow landing last.
    /// Observed wording (2026-08-26): "The wyvern drops dead, poisoned..." - and then, as with any
    /// death, "The wyvern has just passed on." Both forms report here.
    ///
    /// <para>This is a fight end MUD2 has, and neither <see cref="Kill"/> nor anything else covered
    /// it: there is no "You have killed the X." line anywhere in the frame, because the player did
    /// not deliver the killing damage - the poison did. Nothing in the client matched either line, so
    /// the encounter had no terminator at all and the panel went on claiming combat until logout.</para>
    ///
    /// <para><b>Two occurrences, deliberately labelled apart</b> (they were conflated into one
    /// "verbatim" frame in this comment's first draft, and a review caught it). The one that is
    /// BYTES is session-rec.mud2.co.uk.20260826-134435.jsonl, records 2905-3034, extracted to
    /// mudsharp.Tests/Fixtures/Data/wyvern-poison-death.jsonl - dagger0 in hand, no weapon break,
    /// stamina 57/99, score a flat 6,209 with no persona save in the death frame:</para>
    /// <code>
    /// The wyvern drops dead, poisoned...        &lt;- no C1 code at all
    /// The wyvern has just passed on.            &lt;- no C1 code at all
    /// {c08.12}You can fight the wyvern no longer.{/c08.12}
    /// </code>
    ///
    /// <para>The other is the owner's own paste from his screen, an earlier fight at 5,201 points
    /// that no capture holds - a pitchfork that broke mid-fight, "(Persona saved on +26 = 5,201)"
    /// trailing the death. A recollection, not evidence, and treated as one; see
    /// tools/combat/FIGHT-ENDS.md case 8, which sets both out side by side.</para>
    ///
    /// <para><b>Attribution is deliberately not asserted.</b> The line says the creature is dead and
    /// says what killed it; it does not say who applied the poison, and the client has no way to
    /// know from one line. So this is NOT reported as a kill (<see cref="FightOutcome.NoMore"/>, not
    /// <see cref="FightOutcome.Kill"/>) even though that frame's persona save credited points. That
    /// separation is also what keeps FightHistory's stamina-pool estimator honest - it infers a
    /// creature's pool from the damage dealt in fights that ended in a kill, and poison damage is
    /// damage the client never saw, so counting these would understate every pool.</para>
    ///
    /// <para><b>"has just passed on." is reported only for a creature still believed engaged.</b>
    /// It trails every ordinary kill too (see <see cref="FightOutcome.Kill"/>'s verbatim frame),
    /// where the fight is already resolved and re-reporting it would be noise. Reaching it with the
    /// fight still open means our terminator was missed - which is exactly what happened here - so
    /// in that case it is the rescue, and its presence in a clog is the signal to go and find the
    /// line we failed to match.</para>
    /// </summary>
    NpcDied,
}

/// <summary>
/// One classified combat line, timestamped at observation (wall-clock at the point the line
/// completed on the Feed thread — see CombatTracker.Observe). NpcName/Weapon are null when the
/// event kind does not name one (e.g. DroppedGuard from confusion).
/// </summary>
public sealed record CombatEvent(
    DateTime TimestampUtc,
    CombatEventKind Kind,
    CombatActor? Actor,
    string? NpcName,
    string? Weapon,
    int? RangeLow,
    int? RangeHigh,
    string RawText,
    // Only set on NpcHealth: the creature's rung on NpcHealthRungs' scale, 1 (about to die) to 7
    // (unhurt), plus the descriptor as the game worded it so the panel can echo the player's own
    // scroll back at them rather than paraphrasing it.
    int? HealthRung = null,
    string? HealthPhrase = null);
