using System.Text;
using MudSharp.Models;

namespace Mucka.Terminal;

/// <summary>
/// Extracts the plain text covered by a rectangular-free (stream) selection over a list of
/// visual rows. Endpoints are (row, column) pairs in either order; columns are clamped to each
/// row's length, and rows are joined with '\n'. Pure logic, shared by the renderer's copy path.
/// </summary>
public static class TerminalSelection
{
    public static string Extract(IReadOnlyList<StyledLine> rows, (int Row, int Col) a, (int Row, int Col) b)
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
            if (r < endR) sb.Append('\n');
        }
        return sb.ToString();
    }

    private static bool Precedes((int Row, int Col) a, (int Row, int Col) b)
        => a.Row < b.Row || (a.Row == b.Row && a.Col <= b.Col);
}
