using MudSharp.Models;

namespace Mucka.Terminal;

/// <summary>
/// The renderer-agnostic model of what is currently on screen: a capped ring of
/// completed lines plus at most one live partial line (a prompt being built before
/// its terminating '\n' arrives).
///
/// This is a faithful port of the line semantics that previously lived as JavaScript
/// inside GamePage.BuildInjectionScript — moved into testable C# so the live Skia
/// renderer and the frozen history snapshot consume one source of truth.
///
/// Lines are stored as raw <em>logical</em> lines. Wrapping is a render-time concern
/// (the renderer wraps to the negotiated column count); the buffer never wraps.
///
/// Not thread-safe: all access (Append from the flush tick, reads from paint) happens
/// on the UI thread.
/// </summary>
public sealed class TerminalBuffer
{
    private readonly List<StyledLine> _committed = new();
    private StyledLine? _partial;
    private readonly int _cap;

    /// <param name="cap">Maximum number of committed lines retained (the live partial is extra).</param>
    public TerminalBuffer(int cap = 120)
    {
        if (cap < 1) throw new ArgumentOutOfRangeException(nameof(cap));
        _cap = cap;
    }

    /// <summary>Completed lines, oldest first. Does not include the live partial.</summary>
    public IReadOnlyList<StyledLine> Committed => _committed;

    /// <summary>The live partial line (a prompt awaiting its newline), or null.</summary>
    public StyledLine? Partial => _partial;

    /// <summary>Total visible lines = committed + (partial ? 1 : 0).</summary>
    public int Count => _committed.Count + (_partial is null ? 0 : 1);

    /// <summary>Maximum committed lines retained.</summary>
    public int Capacity => _cap;

    /// <summary>
    /// Apply one parsed line. Mirrors BuildInjectionScript:
    /// <list type="bullet">
    /// <item>A line whose plain text contains form-feed (\f) clears everything.</item>
    /// <item>A partial line replaces the current partial.</item>
    /// <item>A blank complete line (no spans) promotes a live partial to committed, or —
    ///       if there is no partial — appends a blank committed line.</item>
    /// <item>A non-empty complete line merges into a live partial (prompt + echo on one
    ///       line) and commits it, or — if there is no partial — appends as a new line.</item>
    /// </list>
    /// </summary>
    public void Append(StyledLine line)
    {
        // Form-feed anywhere in the line is a clear-screen.
        if (line.PlainText.Contains('\f'))
        {
            Clear();
            return;
        }

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

    /// <summary>Remove all committed lines and any live partial (used by \f and Clear-screen).</summary>
    public void Clear()
    {
        _committed.Clear();
        _partial = null;
    }

    /// <summary>
    /// An ordered, immutable copy of everything currently visible (committed lines
    /// followed by the live partial). Taken the instant the user enters history mode so
    /// the frozen view — and selection coordinates over it — cannot shift underneath them.
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
            _committed.RemoveRange(0, _committed.Count - _cap);
    }

    // Promote a partial to a committed line, clearing the partial flag so downstream
    // consumers treat it as a finished line.
    private static StyledLine Promote(StyledLine partial) =>
        partial.IsPartial ? new StyledLine(partial.Spans, isPartial: false) : partial;
}
