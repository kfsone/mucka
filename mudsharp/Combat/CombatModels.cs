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
    /// <summary>"You hit the X (A-B)." — approximate damage range against an NPC.</summary>
    Hit,
    /// <summary>"You miss the X."</summary>
    Miss,
    /// <summary>"The X hits you (C/M)." — C/M is your current/max stamina, not a delta.</summary>
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
    /// <summary>"The X withdraws from your fight, and so do you." — mutual withdraw accepted.</summary>
    Withdrawn,
    /// <summary>"The X has fled by going &lt;dir&gt;."</summary>
    NpcFled,
    /// <summary>"You have fled by going &lt;dir&gt;." Ends EVERY currently-active fight in the encounter.</summary>
    YouFled,
    /// <summary>"You can fight it no longer." — a fight-end with no reason detail (08 12).</summary>
    FightEndOther,
    /// <summary>"You are now using the X to fight!" — new/confirmed weapon in use.</summary>
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
    /// <summary>"The X looks seriously injured." - how hurt a creature is, in the game's own words
    /// and the only report of it MUD2 ever gives. Printed on the line after a landed blow, so it goes
    /// stale between hits (see NpcHealthRungs, which owns the words-to-rung mapping and the reasons
    /// the mapping is what it is).
    ///
    /// <para>Unlike every other kind here, this one must NOT start a fight: the same line appears in
    /// room descriptions, so a wounded creature standing across the room would otherwise register as
    /// an opponent.</para></summary>
    NpcHealth,
    /// <summary>"You cannot use the X to fight now!" - the wield refusal. Fires both when the weapon
    /// has just broken and when MUD2 refuses the wield outright because effective strength (itself
    /// reduced by carried weight, and per the owner by low stamina) is below the hidden threshold
    /// for that weapon. The second case is the ONLY direct evidence of that gate the game emits,
    /// and nothing parsed this line before, so no observation of it has ever been recorded.</summary>
    WeaponUnusable,
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
