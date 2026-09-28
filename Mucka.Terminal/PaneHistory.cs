using MudSharp.Models;

namespace Mucka.Terminal;

/// <summary>
/// What the game pane holds while it is not showing it: the chat filter paints only chat, and
/// turning the filter off repaints the pane from here.
///
/// The main history is a <see cref="TerminalBuffer"/> fed exactly what the pane is fed (the same
/// sanitise and tab expansion, then <see cref="TerminalBuffer.Append"/>), so it applies the pane's
/// own line semantics: a prompt is a partial line, replaced wholesale by the next prompt and merged
/// onto the line that commits it, and the prompt still standing is the live partial. Replaying
/// <see cref="MainSnapshot"/> into an empty buffer reproduces the pane - the same committed lines,
/// the same live prompt, and each clear-screen as a <see cref="TerminalBuffer.ClearRule"/> with the
/// live screen starting after it.
///
/// The chat ring keeps finished <see cref="LineKind.Chat"/> lines, deeper than the main history, so
/// shouts and tells survive in chat mode after they have scrolled out of it. It is classified on the
/// incoming line: a merged or sanitised line does not carry the kind.
///
/// Not thread-safe; the owner touches it from one thread.
/// </summary>
public sealed class PaneHistory
{
    private readonly TerminalBuffer _main;
    private readonly List<StyledLine> _chat = new();
    private readonly int _chatCap;

    public PaneHistory(int mainCap, int chatCap)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chatCap, 1);
        _main = new TerminalBuffer(mainCap);
        _chatCap = chatCap;
    }

    /// <summary>Committed main-history lines, oldest first, including <see cref="TerminalBuffer.ClearRule"/>
    /// lines and prompts merged with the input that committed them. Excludes the live prompt.</summary>
    public IReadOnlyList<StyledLine> Committed => _main.Committed;

    /// <summary>Record one line as the pane receives it.</summary>
    public void Append(StyledLine line)
    {
        _main.Append(Normalize(line));
        if (!line.IsPartial && line.Kind == LineKind.Chat)
        {
            _chat.Add(line);
            if (_chat.Count > _chatCap) _chat.RemoveAt(0);
        }
    }

    /// <summary>Record a client annotation the way the pane shows it: above the live prompt, with
    /// the prompt restored below (see <see cref="TerminalBuffer.InjectAbovePartial"/>).</summary>
    public void InjectAbovePartial(StyledLine line) => _main.InjectAbovePartial(Normalize(line));

    /// <summary>The main history as the pane would hold it: committed lines, then the live prompt.
    /// Replay through the pane's append to restore it.</summary>
    public IReadOnlyList<StyledLine> MainSnapshot() => _main.Snapshot();

    /// <summary>Chat-only history, oldest first.</summary>
    public IReadOnlyList<StyledLine> ChatSnapshot() => _chat.ToArray();

    // The pane's own per-line transform, applied before its buffer merges anything. Tab expansion
    // is column-aware, so expanding a prompt and its input separately differs from expanding the
    // merged line. Both steps return the instance unchanged when there is nothing to do, so the
    // pane's second pass on replay is a no-op.
    private static StyledLine Normalize(StyledLine line) =>
        TerminalText.ExpandTabs(TerminalText.Sanitize(line));
}
