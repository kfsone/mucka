namespace MudSharp.Models;

/// <summary>
/// Semantic classification of a completed output line, derived from the MUD2 C1 code that
/// introduced it (see mud2_FE4.txt). Used by the chat-view filter: chat mode shows only
/// <see cref="Chat"/> lines, so everything else (room text, combat, prompts, echoes) is hidden.
/// Room to grow - add Combat (codes 07/08), Wiz (code 10), etc. as filters need them.
///
/// <para><see cref="Chat"/> wins over any other kind when both apply, because it is the one that
/// drives a filter the player can see; the rest are evidence for consumers.</para>
/// </summary>
public enum LineKind
{
    /// <summary>Anything without a more specific classification: room text, combat, echoes, prompts.</summary>
    Normal = 0,

    /// <summary>A "speaker of a message" line - C1 code 09 (shout/say/tell/act/emote/social).</summary>
    Chat,

    /// <summary>
    /// A fight-end line - C1 codes 08.10 (withdraw), 08.11 (flee) and 08.12 (other). The server
    /// itself is stating that a fight ended, which is worth far more than the sentence it says it
    /// with: the prose has turned out three times now to have wordings nothing here matched
    /// (a creature's failed flee, the player's failed flee, and "You can fight the wyvern no
    /// longer." with a name where every capture had a pronoun), each costing a fight that never
    /// closed. The code was correct in all three frames.
    ///
    /// <para>The prose is still parsed, because the code says only THAT a fight ended and never
    /// WHICH creature - see CombatTracker's FightEndOther handling, which takes the name from the
    /// text and the authority from this.</para>
    ///
    /// <para><b>What the code says is WHY a fight ended, never what happened.</b> Bartle's own list
    /// (mud2_FE4.txt) names them "08 10 Fight ends - withdraw.", "08 11 Fight ends - flee." and
    /// "08 12 Fight ends - other.", and measurement
    /// bears the distinction out: a creature's successful flee and its FAILED flee carry the SAME
    /// 08.11 across every capture on disk (11 and 8 occurrences, no exceptions), so the one-word
    /// prose difference between "has fled by going" and "has fled by trying to go" is the only thing
    /// that separates a creature that escaped from one standing in front of you. The protocol will
    /// not tell you.</para>
    ///
    /// <para>And a creature dying by anything but the player's blow carries no code on the wire:
    /// "The X has just passed on." arrives untagged in 87 of 87 occurrences. The documents agree -
    /// the 08 family lists 08.08 you killed them, 08.09 they killed you, plus the three fight-end
    /// reasons, and there is no death or corpse code anywhere in either - though neither claims to
    /// be complete (mud2_FE4.txt: "I will be adding more codes"; "Look in muddle/codes.mud for the
    /// latest versions."). Prose matching is not a workaround for these lines; it is the only
    /// thing there is.</para>
    /// </summary>
    FightEnd,

    /// <summary>
    /// A fight started - C1 code 08, "Fight starts." in mud2_FE4.txt. A missing trailing parameter
    /// reads as 00, so the bare <c>08</c> the list gives and <c>08 00</c> are the same code; the
    /// wire carries <c>08 00</c> (<c>[A3][9B]</c>) in every one of 1,068+ occurrences and the bare
    /// form in none. Both are tagged.
    ///
    /// <para>Tagged for the same reason as <see cref="FightEnd"/>: the code states the fact, the
    /// prose only says which Creature. Every opening carries it - "You attack the X", every aggro
    /// sentence, and the anonymous forms "You attack someone." and "Someone/Something is about to
    /// attack you." - which is what makes it the primary count of Unseen opponents: an opponent the
    /// player cannot see still announces itself under this code. A wording no regex knows still
    /// opens the fight; it costs the actor and the weapon, never the fight.</para>
    /// </summary>
    FightStart,

    /// <summary>
    /// A creature turned invisible - C1 code 04.00.05, which mud2_FE4.txt names "Normal creatures
    /// becoming invisible." Confirmed on the wire: <c>[9F][9B][A0]</c> introduces "The man fades
    /// from view."
    ///
    /// <para>Worth a kind of its own because of what happens AFTER it. MUD2 does not stop reporting
    /// an invisible creature - it stops NAMING it, substituting "someone" into every sentence that
    /// would have carried the name, and "something" for the weapon it picks up. So one unremarkable
    /// line silently changes the wording of every combat line for the rest of the fight, and the
    /// only thing that can put the name back on those lines is knowing this one arrived. See
    /// CombatTracker's anonymous-participant handling.</para>
    ///
    /// <para>The code is tagged rather than the sentence for the reason <see cref="FightEnd"/>
    /// gives: the code states the fact, the prose only says which creature. Here the prose matters
    /// more than usual, since the whole point is to learn a name - but a wording we do not know
    /// still tells us the fight has gone anonymous, and with one creature engaged that is enough.
    /// </para>
    ///
    /// <para>Creatures only. The mortal equivalent (05.00.05, "Mortals becoming invisible") is the
    /// obvious sibling and is deliberately not tagged: a player has never been observed doing this
    /// mid-fight, and the substitution it would produce is unverified.</para>
    /// </summary>
    CreatureInvisible,
}
