namespace MudSharp.Models;

/// <summary>
/// Semantic classification of a completed output line, derived from the MUD2 C1 code that
/// introduced it (see fecodes.txt). Used by the chat-view filter: chat mode shows only
/// <see cref="Chat"/> lines, so everything else (room text, combat, prompts, echoes) is hidden.
/// Room to grow — add Combat (codes 07/08), Wiz (code 10), etc. as filters need them.
///
/// <para><see cref="Chat"/> wins over any other kind when both apply, because it is the one that
/// drives a filter the player can see; the rest are evidence for consumers.</para>
/// </summary>
public enum LineKind
{
    /// <summary>Anything without a more specific classification: room text, combat, echoes, prompts.</summary>
    Normal = 0,

    /// <summary>A "speaker of a message" line — C1 code 09 (shout/say/tell/act/emote/social).</summary>
    Chat,

    /// <summary>
    /// A fight-end line — C1 codes 08.10 (withdraw), 08.11 (flee) and 08.12 (other). The server
    /// itself is stating that a fight ended, which is worth far more than the sentence it says it
    /// with: the prose has turned out three times now to have wordings nothing here matched
    /// (a creature's failed flee, the player's failed flee, and "You can fight the wyvern no
    /// longer." with a name where every capture had a pronoun), each costing a fight that never
    /// closed. The code was correct in all three frames.
    ///
    /// <para>The prose is still parsed, because the code says only THAT a fight ended and never
    /// WHICH creature — see CombatTracker's FightEndOther handling, which takes the name from the
    /// text and the authority from this.</para>
    ///
    /// <para><b>What the code says is WHY a fight ended, never what happened.</b> Bartle's own list
    /// (Bartle.MUD2-C1-Codes.txt) names them "Fight ends - withdraw / flee / other", and measurement
    /// bears the distinction out: a creature's successful flee and its FAILED flee carry the SAME
    /// 08.11 across every capture on disk (11 and 8 occurrences, no exceptions), so the one-word
    /// prose difference between "has fled by going" and "has fled by trying to go" is the only thing
    /// that separates a creature that escaped from one standing in front of you. The protocol will
    /// not tell you.</para>
    ///
    /// <para>And a creature dying by anything but the player's blow has NO code at all - not merely
    /// unobserved: the 08 family is exhaustive in the spec (08.08 you killed them, 08.09 they killed
    /// you, plus the three fight-end reasons) and there is no death or corpse code anywhere in the
    /// document. Confirmed on the wire, where "The X has just passed on." arrives untagged in 87 of
    /// 87 occurrences. MUD2 is a museum piece, so that absence is permanent. Prose matching is not a
    /// workaround for these lines; it is the only thing there will ever be.</para>
    /// </summary>
    FightEnd,
}
