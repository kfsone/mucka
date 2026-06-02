using MudSharp.Models;

namespace Mucka.Terminal;

/// <summary>
/// Naive fixed-column line wrapping for the terminal renderer. Each logical
/// <see cref="StyledLine"/> is broken into visual rows of at most <c>columns</c> characters,
/// splitting a span that straddles the boundary and preserving its <see cref="TextStyle"/>
/// across the break. A hard break (no word awareness) — this matches a dumb terminal and is
/// a no-op when the server has already wrapped (every logical line is then ≤ columns).
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
    {
        if (columns < 1) throw new ArgumentOutOfRangeException(nameof(columns));
        var rows = new List<StyledLine>();
        for (int i = 0; i < lines.Count; i++)
            Wrap(lines[i], columns, rows);
        return rows;
    }

    /// <summary>Wrap one logical line, appending its visual rows to <paramref name="rows"/>.</summary>
    public static void Wrap(StyledLine line, int columns, List<StyledLine> rows)
    {
        if (columns < 1) throw new ArgumentOutOfRangeException(nameof(columns));

        if (line.Spans.Count == 0)
        {
            rows.Add(new StyledLine(Array.Empty<StyledSpan>(), isPartial: false));
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
                current.Add(new StyledSpan(text.Substring(idx, take), span.Style));
                idx += take;
                col += take;
            }
        }
        rows.Add(new StyledLine(current, isPartial: false));
    }
}
