using MudSharp.Models;

namespace Mucka.Terminal;

/// <summary>
/// Naive fixed-column line wrapping for the terminal renderer. Each logical
/// <see cref="StyledLine"/> is broken into visual rows of at most <c>columns</c> characters,
/// splitting a span that straddles the boundary and preserving its <see cref="TextStyle"/>
/// and any <see cref="StyledSpan.ClickInsertText"/> across the break (so a clickable name that
/// wraps stays clickable in both pieces). A hard break (no word awareness) - this matches a dumb terminal and is
/// a no-op when the server has already wrapped (every logical line is then <= columns).
///
/// A visual row is itself a (non-partial) <see cref="StyledLine"/>; a blank logical line
/// produces exactly one empty row.
/// </summary>
public static class LineWrapper
{
    /// <summary>Wrap one logical line into visual rows.</summary>
    public static List<StyledLine> Wrap(StyledLine line, int columns)
    {
        var rows = new List<StyledLine>();
        Wrap(line, columns, rows);
        return rows;
    }

    /// <summary>Wrap a sequence of logical lines into one flat list of visual rows.</summary>
    public static List<StyledLine> WrapAll(IReadOnlyList<StyledLine> lines, int columns)
        => WrapAll(lines, columns, null);

    /// <summary>
    /// Wrap a sequence of logical lines, and record for each visual row whether it CONTINUES the
    /// row above it rather than starting a logical line of its own.
    ///
    /// <para>That distinction is the difference between a hard break and a soft one, and only this
    /// class knows it. The copy path needs it: a soft break is an artifact of the pane's width and
    /// must not survive into the clipboard - see <see cref="TerminalSelection.Extract"/>.</para>
    /// </summary>
    /// <param name="continuesPrevious">Filled in step with the returned rows, or null if not wanted.</param>
    public static List<StyledLine> WrapAll(IReadOnlyList<StyledLine> lines, int columns,
        List<bool>? continuesPrevious)
    {
        if (columns < 1) throw new ArgumentOutOfRangeException(nameof(columns));
        var rows = new List<StyledLine>();
        for (int i = 0; i < lines.Count; i++)
            Wrap(lines[i], columns, rows, continuesPrevious);
        return rows;
    }

    /// <summary>Wrap one logical line, appending its visual rows to <paramref name="rows"/>.</summary>
    public static void Wrap(StyledLine line, int columns, List<StyledLine> rows)
        => Wrap(line, columns, rows, null);

    /// <summary>Wrap one logical line, appending its visual rows and their continuation flags. The
    /// line's FIRST row is never a continuation - it begins a logical line - and every row the wrap
    /// produces after it is.</summary>
    public static void Wrap(StyledLine line, int columns, List<StyledLine> rows,
        List<bool>? continuesPrevious)
    {
        if (columns < 1) throw new ArgumentOutOfRangeException(nameof(columns));
        int firstRow = rows.Count;

        if (line.Spans.Count == 0)
        {
            rows.Add(new StyledLine(Array.Empty<StyledSpan>(), isPartial: false));
            MarkFrom(continuesPrevious, firstRow, rows.Count);
            return;
        }

        var current = new List<StyledSpan>();
        int col = 0;
        foreach (var span in line.Spans)
        {
            string text = span.Text;
            int idx = 0;
            while (idx < text.Length)
            {
                if (col >= columns)
                {
                    rows.Add(new StyledLine(current, isPartial: false));
                    current = new List<StyledSpan>();
                    col = 0;
                }
                int take = Math.Min(columns - col, text.Length - idx);
                // Preserve ClickInsertText across the wrap so a clickable name split over a
                // row boundary stays clickable in every piece (TryActivateSpanInsert reads it).
                current.Add(new StyledSpan(text.Substring(idx, take), span.Style, span.ClickInsertText));
                idx += take;
                col += take;
            }
        }
        rows.Add(new StyledLine(current, isPartial: false));
        MarkFrom(continuesPrevious, firstRow, rows.Count);
    }

    // One logical line occupies rows [firstRow, endExclusive): the first starts it, the rest
    // continue it. Written here rather than at each Add so the two lists cannot drift apart.
    private static void MarkFrom(List<bool>? continuesPrevious, int firstRow, int endExclusive)
    {
        if (continuesPrevious is null) return;
        for (int r = firstRow; r < endExclusive; r++)
            continuesPrevious.Add(r > firstRow);
    }
}
