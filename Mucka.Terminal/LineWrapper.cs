using MudSharp.Models;

namespace Mucka.Terminal;

/// <summary>
/// Word wrapping for the terminal renderer, by the rules MUD2 applied when it wrapped for us. Each
/// logical <see cref="StyledLine"/> is broken into visual rows, splitting a span that straddles a
/// break and preserving its <see cref="TextStyle"/> and any <see cref="StyledSpan.ClickInsertText"/>
/// across it (so a clickable name that wraps stays clickable in both pieces).
///
/// <para>The rule is MUD2's, measured over its wrapped output at three terminal widths, fitted to a
/// pane that has no cell to spare on the right: a row shows at most <c>columns</c> characters, and
/// the space that closes it may hang one cell past them, where it is clipped. A longer line breaks
/// after the last space that fits that way. The space stays at the end of the row it closes, so the
/// rows of one logical line concatenate back to its exact text. A word longer than a row has no
/// space to break at and is cut at <c>columns</c>. See <c>docs/line-wrapping.md</c>.</para>
///
/// A visual row is itself a (non-partial) <see cref="StyledLine"/>; a blank logical line
/// produces exactly one empty row.
/// </summary>
public static class LineWrapper
{
    /// <summary>The most characters one row shows in a pane <paramref name="columns"/> wide. The
    /// space that closes a wrapped row is not counted: it hangs past the last column.</summary>
    public static int RowCapacity(int columns) => Math.Max(1, columns);

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
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
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
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        int firstRow = rows.Count;
        int capacity = RowCapacity(columns);

        if (line.Spans.Count == 0)
        {
            rows.Add(new StyledLine(Array.Empty<StyledSpan>(), isPartial: false));
            MarkFrom(continuesPrevious, firstRow, rows.Count);
            return;
        }

        var text = line.PlainText;
        int start = 0;
        int spanIndex = 0, spanOffset = 0;
        while (true)
        {
            int end = text.Length - start <= capacity ? text.Length : BreakEnd(text, start, capacity);
            rows.Add(new StyledLine(TakeSpans(line.Spans, ref spanIndex, ref spanOffset, end - start),
                isPartial: false));
            if (end == text.Length) break;
            start = end;
        }
        MarkFrom(continuesPrevious, firstRow, rows.Count);
    }

    // Where the row starting at `start` ends (exclusive), for a remainder longer than one row. The
    // last space at or before the capacity-th character past `start` (the one that hangs), with
    // something other than indent before it, closes the row, space included; with none, the row is
    // cut at the capacity.
    private static int BreakEnd(string text, int start, int capacity)
    {
        int content = start;
        while (content < text.Length && text[content] == ' ')
            content++;
        for (int i = start + capacity; i > content; i--)
        {
            if (text[i] == ' ')
                return i + 1;
        }
        return start + capacity;
    }

    // The next `count` characters of the spans, as spans: a span cut by the row boundary is split,
    // and the cursor (spanIndex, spanOffset) is left at the first character not taken.
    private static List<StyledSpan> TakeSpans(IReadOnlyList<StyledSpan> spans,
        ref int spanIndex, ref int spanOffset, int count)
    {
        var taken = new List<StyledSpan>();
        while (count > 0)
        {
            var span = spans[spanIndex];
            int take = Math.Min(count, span.Text.Length - spanOffset);
            if (take > 0)
            {
                // Preserve ClickInsertText across the wrap so a clickable name split over a
                // row boundary stays clickable in every piece (TryActivateSpanInsert reads it).
                taken.Add(new StyledSpan(span.Text.Substring(spanOffset, take), span.Style, span.ClickInsertText));
                spanOffset += take;
                count -= take;
            }
            if (spanOffset >= span.Text.Length)
            {
                spanIndex++;
                spanOffset = 0;
            }
        }
        return taken;
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
