using MudSharp.Models;

namespace Mucka.Terminal;

/// <summary>Where a search found its text: the logical line, and the span of it, in characters of
/// the line's plain text.</summary>
public readonly record struct TextMatch(int Line, int Start, int Length);

/// <summary>
/// The Session viewer's search over a log's logical lines (<see cref="TerminalBuffer.ScreenOf"/>),
/// ignoring case. A match is found in one line's plain text; a clear-screen rule holds none.
///
/// <para>A search walks from where it is: forward from one character past the current match, so
/// each search finds the next match, and backward from just before it. With no current match it
/// starts at a line the caller names (the viewer gives the first line it shows, or for a backward
/// search the last). Past either end it carries on from the other, as a browser's find does.</para>
/// </summary>
public static class LogSearch
{
    private const StringComparison Comparison = StringComparison.OrdinalIgnoreCase;

    /// <summary>The next match of <paramref name="text"/>, or with <paramref name="backward"/> the one
    /// before; null when no line holds it.</summary>
    /// <param name="from">The current match, which the search moves on from; or null.</param>
    /// <param name="startLine">With no current match, the line the search starts on, included.</param>
    public static TextMatch? Find(IReadOnlyList<StyledLine> lines, string text, TextMatch? from, int startLine,
        bool backward)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (string.IsNullOrEmpty(text) || lines.Count == 0)
            return null;
        var line = Math.Clamp(from?.Line ?? startLine, 0, lines.Count - 1);
        // Forward: matches starting at or after this column of the first line. Backward: before it.
        var column = from is { } current ? (backward ? current.Start : current.Start + 1)
            : backward ? int.MaxValue : 0;
        // One more line than there are, so the first line is searched again from its other side.
        for (var step = 0; step <= lines.Count; step++)
        {
            var index = backward ? Wrap(line - step, lines.Count) : Wrap(line + step, lines.Count);
            var whole = lines[index].PlainText;
            if (!TerminalBuffer.IsClearRule(lines[index]))
            {
                var at = step == 0
                    ? (backward ? LastBefore(whole, text, column) : FirstFrom(whole, text, column))
                    : (backward ? LastBefore(whole, text, int.MaxValue) : FirstFrom(whole, text, 0));
                if (at >= 0)
                    return new TextMatch(index, at, text.Length);
            }
        }
        return null;
    }

    /// <summary>How many matches of <paramref name="text"/> there are, and which of them
    /// <paramref name="match"/> is, counting from 1; 0 when it is not one of them.</summary>
    public static (int Index, int Count) Place(IReadOnlyList<StyledLine> lines, string text, TextMatch? match)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (string.IsNullOrEmpty(text))
            return (0, 0);
        int count = 0, index = 0;
        for (var line = 0; line < lines.Count; line++)
        {
            if (TerminalBuffer.IsClearRule(lines[line]))
                continue;
            var whole = lines[line].PlainText;
            for (var at = whole.IndexOf(text, Comparison); at >= 0; at = FirstFrom(whole, text, at + 1))
            {
                count++;
                if (match is { } m && m.Line == line && m.Start == at)
                    index = count;
            }
        }
        return (index, count);
    }

    private static int Wrap(int line, int count) => ((line % count) + count) % count;

    private static int FirstFrom(string whole, string text, int column)
        => column > whole.Length ? -1 : whole.IndexOf(text, column, Comparison);

    // The last match starting before column: every match is found forward, so the rule for overlapping
    // matches is the same in both directions.
    private static int LastBefore(string whole, string text, int column)
    {
        var last = -1;
        for (var at = whole.IndexOf(text, Comparison); at >= 0 && at < column; at = FirstFrom(whole, text, at + 1))
            last = at;
        return last;
    }
}
