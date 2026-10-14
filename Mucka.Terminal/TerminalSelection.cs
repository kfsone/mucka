using System.Text;
using MudSharp.Models;

namespace Mucka.Terminal;

/// <summary>
/// Extracts the plain text covered by a rectangular-free (stream) selection over a list of
/// visual rows. Endpoints are (row, column) pairs in either order; columns are clamped to each
/// row's length. Pure logic, shared by the renderer's copy path.
///
/// <para><b>A soft break is not a newline.</b> Rows are joined with '\n' only where a real line
/// ended; where <see cref="LineWrapper"/> split one logical line across several rows because the
/// pane is narrow, the pieces are joined with nothing. A wrap is a property of the window, not of
/// the text, and it has no business in the clipboard - a path or a URL the client printed pastes as
/// one string. Lines the server wrapped itself arrive as separate logical lines and keep their
/// breaks, which is what makes MUD output paste as the game laid it out.</para>
///
/// <para>A <see cref="TerminalBuffer.ClearRule"/> row contributes no text, so a selection across a
/// clear-screen pastes a blank line where the rule was drawn.</para>
/// </summary>
public static class TerminalSelection
{
    /// <param name="continuesPrevious">Per-row flags from <see cref="LineWrapper"/>, one per entry in
    /// <paramref name="rows"/> and built in the same pass, or null to treat every row boundary as a
    /// hard break. An index past the end reads as a hard break, which is what a null list gives -
    /// a short list degrades to the old behaviour rather than throwing.</param>
    public static string Extract(IReadOnlyList<StyledLine> rows, (int Row, int Col) a, (int Row, int Col) b,
        IReadOnlyList<bool>? continuesPrevious = null)
    {
        if (rows.Count == 0) return string.Empty;
        if (!Precedes(a, b)) (a, b) = (b, a);

        int startR = Math.Clamp(a.Row, 0, rows.Count - 1);
        int endR = Math.Clamp(b.Row, 0, rows.Count - 1);

        var sb = new StringBuilder();
        for (int r = startR; r <= endR; r++)
        {
            // A clear-screen rule is drawn, not text: it copies as a blank line.
            string text = TerminalBuffer.IsClearRule(rows[r]) ? string.Empty : rows[r].PlainText;
            int s = r == a.Row ? Math.Clamp(a.Col, 0, text.Length) : 0;
            int e = r == b.Row ? Math.Clamp(b.Col, 0, text.Length) : text.Length;
            if (e > s) sb.Append(text, s, e - s);
            // The break belongs to the row BELOW: it is hard only if that row starts a logical line.
            if (r < endR && !Continues(continuesPrevious, r + 1)) sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>The half-open column range [Start, End) of the word under <paramref name="col"/>: the
    /// run of letters, digits and underscores, or failing that the run of whitespace or of other
    /// punctuation the column sits in. Null when the column is past the end of the text.</summary>
    public static (int Start, int End)? WordBounds(string text, int col)
    {
        if (col < 0 || col >= text.Length) return null;
        int kind = CharKind(text[col]);
        int s = col, e = col + 1;
        while (s > 0 && CharKind(text[s - 1]) == kind) s--;
        while (e < text.Length && CharKind(text[e]) == kind) e++;
        return (s, e);
    }

    private static int CharKind(char c)
        => char.IsWhiteSpace(c) ? 0 : (char.IsLetterOrDigit(c) || c == '_') ? 1 : 2;

    /// <summary>The first and last visual row of the logical line that <paramref name="row"/> belongs
    /// to, following the soft-wrap flags of <see cref="LineWrapper"/> (null = every row is its own
    /// line). With <paramref name="rowCount"/> of 0 or less the result is (0, 0).</summary>
    public static (int First, int Last) LineRows(IReadOnlyList<bool>? continuesPrevious, int rowCount, int row)
    {
        int first = Math.Clamp(row, 0, Math.Max(0, rowCount - 1)), last = first;
        while (first > 0 && Continues(continuesPrevious, first)) first--;
        while (last + 1 < rowCount && Continues(continuesPrevious, last + 1)) last++;
        return (first, last);
    }

    private static bool Continues(IReadOnlyList<bool>? flags, int row)
        => flags is not null && row >= 0 && row < flags.Count && flags[row];

    private static bool Precedes((int Row, int Col) a, (int Row, int Col) b)
        => a.Row < b.Row || (a.Row == b.Row && a.Col <= b.Col);
}
