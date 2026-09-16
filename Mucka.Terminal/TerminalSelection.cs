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
            string text = rows[r].PlainText;
            int s = r == a.Row ? Math.Clamp(a.Col, 0, text.Length) : 0;
            int e = r == b.Row ? Math.Clamp(b.Col, 0, text.Length) : text.Length;
            if (e > s) sb.Append(text, s, e - s);
            // The break belongs to the row BELOW: it is hard only if that row starts a logical line.
            if (r < endR && !Continues(continuesPrevious, r + 1)) sb.Append('\n');
        }
        return sb.ToString();
    }

    private static bool Continues(IReadOnlyList<bool>? flags, int row)
        => flags is not null && row >= 0 && row < flags.Count && flags[row];

    private static bool Precedes((int Row, int Col) a, (int Row, int Col) b)
        => a.Row < b.Row || (a.Row == b.Row && a.Col <= b.Col);
}
