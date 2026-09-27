using MudSharp.Models;

namespace Mucka.Terminal;

/// <summary>
/// The renderer-agnostic model of what is currently on screen: a capped ring of
/// completed lines plus at most one live partial line (a prompt being built before
/// its terminating '\n' arrives).
///
/// It is the single source of truth for those line semantics: the live Skia renderer, the
/// frozen history snapshot and <see cref="SessionRecorder"/> all read one buffer rather than
/// each deciding for itself where a line ends.
///
/// Lines are stored as raw <em>logical</em> lines. Wrapping is a render-time concern
/// (the renderer wraps to the negotiated column count); the buffer never wraps.
///
/// A clear-screen (form feed in the stream) does not delete anything. It commits a
/// <see cref="ClearRule"/> and moves <see cref="LiveStart"/> past it: the live screen shows only
/// what follows, while <see cref="Snapshot"/> - scrollback - keeps the lines above it with the rule
/// marking where the screen was cleared. Operator rule: clear the screen when it happens, but in
/// scrollback ignore it and display a horizontal line.
///
/// Not thread-safe. The renderer's instance is touched only on the UI thread (Append from
/// the flush tick, reads from paint); <see cref="SessionRecorder"/> keeps its own instance
/// and serialises its own access to it.
/// </summary>
public sealed class TerminalBuffer
{
    private readonly List<StyledLine> _committed = new();
    private StyledLine? _partial;
    private int _liveStart;
    private readonly int _cap;

    /// <summary>
    /// The committed line that stands where a clear-screen happened. Its content is a lone form
    /// feed rather than an identity or a flag because <see cref="LineWrapper"/> rebuilds every row
    /// from its spans; test for it with <see cref="IsClearRule"/>, never by reference.
    /// </summary>
    public static readonly StyledLine ClearRule = new([new StyledSpan("\f", TextStyle.Default)], isPartial: false);

    /// <summary>True for <see cref="ClearRule"/> and for any visual row wrapped from it. Renderers
    /// draw it as a horizontal rule, copy treats it as a blank line, the transcript writes a
    /// separator.</summary>
    public static bool IsClearRule(StyledLine line) =>
        line.Spans.Count == 1 && line.Spans[0].Text == "\f";

    /// <param name="cap">Maximum number of committed lines retained (the live partial is extra).</param>
    public TerminalBuffer(int cap = 120)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(cap, 1);
        _cap = cap;
    }

    /// <summary>Completed lines, oldest first, including those above the last clear-screen and the
    /// <see cref="ClearRule"/> lines themselves. Does not include the live partial.</summary>
    public IReadOnlyList<StyledLine> Committed => _committed;

    /// <summary>Index into <see cref="Committed"/> of the first line on the live screen: 0 until a
    /// clear-screen, then the line after the latest <see cref="ClearRule"/>. Equal to
    /// <c>Committed.Count</c> when nothing has been committed since the clear.</summary>
    public int LiveStart => _liveStart;

    /// <summary>The live partial line (a prompt awaiting its newline), or null.</summary>
    public StyledLine? Partial => _partial;

    /// <summary>
    /// Raised as a line is committed - the moment it stops being replaceable and becomes a finished
    /// line of screen. <see cref="SessionRecorder"/> writes the transcript from this, so the file and
    /// the screen cannot disagree about where one line ends: a prompt replaced five times before its
    /// echo completes it raises this once.
    /// </summary>
    public event Action<StyledLine>? LineCommitted;

    /// <summary>Total retained lines, scrollback included = committed + (partial ? 1 : 0).</summary>
    public int Count => _committed.Count + (_partial is null ? 0 : 1);

    /// <summary>
    /// Apply one parsed line:
    /// <list type="bullet">
    /// <item>A form feed (\f) is a clear-screen. Text before it on the line completes a line of its
    ///       own (merged into a live partial, as any complete line is) and is committed, a live
    ///       partial with nothing after it is promoted, then a <see cref="ClearRule"/> is committed
    ///       and <see cref="LiveStart"/> moves past it. Text after it is applied as the rest of the
    ///       line. No rule is committed with nothing above it, or directly after another rule.</item>
    /// <item>A partial line replaces the current partial.</item>
    /// <item>A blank complete line (no spans) promotes a live partial to committed, or -
    ///       if there is no partial - appends a blank committed line.</item>
    /// <item>A non-empty complete line merges into a live partial (prompt + echo on one
    ///       line) and commits it, or - if there is no partial - appends as a new line.</item>
    /// </list>
    /// </summary>
    public void Append(StyledLine line)
    {
        if (line.PlainText.Contains('\f'))
        {
            AppendAcrossClears(line);
            return;
        }
        AppendLine(line);
    }

    // Split the line at each form feed: every piece before one is finished off and followed by a
    // clear; whatever follows the last one carries the line's own partial/kind flags.
    private void AppendAcrossClears(StyledLine line)
    {
        var segment = new List<StyledSpan>();
        foreach (var span in line.Spans)
        {
            var parts = span.Text.Split('\f');
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length > 0) segment.Add(span with { Text = parts[i] });
                if (i < parts.Length - 1)
                {
                    ClearScreenAfter(segment);
                    segment = new List<StyledSpan>();
                }
            }
        }
        if (segment.Count > 0)
            AppendLine(new StyledLine(segment, line.IsPartial, line.Kind, line.ContinuesChat));
    }

    private void ClearScreenAfter(List<StyledSpan> before)
    {
        if (before.Count > 0)
            AppendLine(new StyledLine(before, isPartial: false));   // merges into a live partial
        else if (_partial is { Spans.Count: > 0 } partial)
            Commit(Promote(partial));
        _partial = null;

        if (_committed.Count > 0 && !IsClearRule(_committed[^1]))
            Commit(ClearRule);
        _liveStart = _committed.Count;
    }

    private void AppendLine(StyledLine line)
    {
        if (line.IsPartial)
        {
            // Replace the live partial wholesale (the JS set p.innerHTML to the new content).
            _partial = line;
            return;
        }

        // Blank complete line: no spans => empty rendered output.
        if (line.Spans.Count == 0)
        {
            if (_partial is not null)
            {
                // Promote the partial; do NOT insert a blank line between consecutive prompts.
                Commit(Promote(_partial));
                _partial = null;
            }
            else
            {
                Commit(line);
            }
            return;
        }

        // Non-empty complete line.
        if (_partial is not null)
        {
            // Merge the prompt (partial) and this line's content onto one committed line.
            Commit(new StyledLine([.. _partial.Spans, .. line.Spans], isPartial: false));
            _partial = null;
        }
        else
        {
            Commit(line);
        }
    }

    /// <summary>
    /// Commit a standalone line <em>above</em> the live partial without merging into it, then
    /// restore the partial so it re-renders below the injected line. Used for client-side
    /// annotations ($f&lt;n&gt;): if you are sitting at a prompt the prompt reappears beneath the
    /// note; on a blank line the note is simply committed. With no live partial this is an
    /// ordinary append of a finished line.
    /// </summary>
    public void InjectAbovePartial(StyledLine line)
    {
        var saved = _partial;
        _partial = null;
        // No partial present, so a non-empty finished line commits standalone (never a merge).
        Commit(line.IsPartial ? Promote(line) : line);
        _partial = saved;
    }

    /// <summary>Remove all committed lines and any live partial, scrollback included. The client's
    /// own wipe (Ctrl-L, a chat-filter repaint); a clear-screen in the stream does not come here.</summary>
    public void Clear()
    {
        _committed.Clear();
        _partial = null;
        _liveStart = 0;
    }

    /// <summary>
    /// An ordered, immutable copy of everything currently visible (committed lines
    /// followed by the live partial). Taken the instant the user enters history mode so
    /// the frozen view - and selection coordinates over it - cannot shift underneath them.
    /// </summary>
    public IReadOnlyList<StyledLine> Snapshot()
    {
        if (_partial is null)
            return _committed.ToArray();
        var snap = new StyledLine[_committed.Count + 1];
        _committed.CopyTo(snap);
        snap[^1] = _partial;
        return snap;
    }

    private void Commit(StyledLine line)
    {
        _committed.Add(line);
        // Trim oldest beyond the cap. RemoveRange is O(n) once rather than repeated shifts.
        if (_committed.Count > _cap)
        {
            int removed = _committed.Count - _cap;
            _committed.RemoveRange(0, removed);
            _liveStart = Math.Max(0, _liveStart - removed);
        }
        LineCommitted?.Invoke(line);
    }

    // Promote a partial to a committed line, clearing the partial flag so downstream
    // consumers treat it as a finished line.
    private static StyledLine Promote(StyledLine partial) =>
        partial.IsPartial ? new StyledLine(partial.Spans, isPartial: false) : partial;
}
