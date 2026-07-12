namespace MudSharp.Models;

/// <summary>
/// Semantic classification of a completed output line, derived from the MUD2 C1 code that
/// introduced it (see fecodes.txt). Used by the chat-view filter: chat mode shows only
/// <see cref="Chat"/> lines, so everything else (room text, combat, prompts, echoes) is hidden.
/// Room to grow — add Combat (codes 07/08), Wiz (code 10), etc. as filters need them.
/// </summary>
public enum LineKind
{
    /// <summary>Anything without a more specific classification: room text, combat, echoes, prompts.</summary>
    Normal = 0,

    /// <summary>A "speaker of a message" line — C1 code 09 (shout/say/tell/act/emote/social).</summary>
    Chat,
}
