using System.Text;
using System.Text.RegularExpressions;
using MudSharp.Models;
using MudSharp.Protocol;
using Mucka.WireLog;

namespace Mucka.Combat;

/// <summary>
/// Takes the server's line wrapping out of one recorded run, so a recording made when the server
/// wrapped at the width the client sent (<c>/T</c>) reads like one made with local formatting on.
///
/// <para><b>It infers.</b> A server wrap and a real line end are the same bytes (CR NUL CR LF, or CR CR LF), so
/// nothing in a row says which it is. What is known is the rule (<c>LineWrapper</c>,
/// docs/line-wrapping.md): at width W a server wrapped a line after a row of at most W-2 characters
/// that does not end in a space, when the next word would not have fitted. Two consecutive lines
/// that meet that test, with nothing sent between them and nothing but the second line's own text
/// after the first line's terminator, are one line the server wrapped. A line the server ended on
/// purpose just before a word that happened not to fit is joined too; it is rare and it is why this
/// is a command and not something the client does on its own.</para>
///
/// <para>The width is the last <c>/T</c> the client sent before the row. A run with none, and a row
/// before the first, is left alone. Only game-mode lines that carry no C1 kind are joined: the splash
/// and the menus before the game are laid out on purpose, and chat is not wrapped by the rule.</para>
///
/// <para><b>The gate.</b> The rewritten stream is parsed again, and its lines must be exactly the
/// original lines with the joins made. A run for which they are not is refused.</para>
/// </summary>
public static class WireUnwrap
{
    /// <summary>The rows a run's unwrapping changes, with what each becomes.</summary>
    public readonly record struct Changed(long Id, long Seq, byte[] Data);

    // A tx row that is the width command and nothing else: the client-mode entry, a resize, or a bare
    // /T at the Option menu. What a player typed that happens to contain /T is not a width.
    private static readonly Regex WidthCommand = new(
        @"^(?:\x1b-\[)?(?:\x1b\x06)?(?:\x1b-T)?(?:\x1b-N)?/T(\d{1,3})(?:\x1b-\])?(?:\r?\n)?$",
        RegexOptions.CultureInvariant);

    private readonly record struct Line(int Row, int Offset, string Text, LineKind Kind, int Width, bool InGame);

    /// <summary>The changed rows of one run, or null when the gate refused the plan. Empty when there
    /// is nothing to join.</summary>
    public static List<Changed>? Plan(IReadOnlyList<WireTrim.Row> rows)
    {
        // The rx stream, the row each byte belongs to, and the width in force at each rx row.
        var all = new List<byte>();
        var rowStart = new List<int>();
        var rowId = new List<long>();
        var rowSeq = new List<long>();
        var rowWidth = new List<int>();
        var nonRxBefore = new List<int>();   // how many non-rx rows precede each rx row
        var width = 0;
        var nonRx = 0;
        foreach (var row in rows)
        {
            if (row.Direction == (int)WireDirection.Rx)
            {
                rowStart.Add(all.Count);
                rowId.Add(row.Id);
                rowSeq.Add(row.Seq);
                rowWidth.Add(width);
                nonRxBefore.Add(nonRx);
                all.AddRange(row.Data);
            }
            else
            {
                nonRx++;
                if (row.Direction == (int)WireDirection.Tx
                    && WidthCommand.Match(Encoding.Latin1.GetString(row.Data)) is { Success: true } sent
                    && int.TryParse(sent.Groups[1].Value, out var w))
                    width = w;
            }
        }
        if (!rowWidth.Any(w => w > 0)) return [];   // no width was ever sent: nothing to parse for
        var stream = all.ToArray();

        var lines = ParseLines(stream, rowStart, rowWidth);
        if (lines.Count < 2) return [];

        // Which consecutive lines are one line the server wrapped.
        var joinWithNext = new bool[lines.Count];
        var any = false;
        for (var i = 0; i + 1 < lines.Count; i++)
        {
            var a = lines[i];
            var b = lines[i + 1];
            if (!a.InGame || !b.InGame) continue;   // the splash and the menus are laid out on purpose
            if (a.Width <= 0 || a.Width != b.Width) continue;
            if (a.Kind != LineKind.Normal || b.Kind != LineKind.Normal) continue;
            if (a.Text.Length == 0 || b.Text.Length == 0 || a.Text.EndsWith(' ')) continue;
            if (nonRxBefore[a.Row] != nonRxBefore[b.Row]) continue;   // something was sent between
            if (a.Text.Length > a.Width - 2 || b.Text[0] == ' ') continue;
            if (!JoinsDirectly(stream, RxOffset(a, rowStart), RxOffset(b, rowStart))) continue;
            var firstWord = b.Text.Split(' ', 2)[0];
            if (a.Text.Length + 1 + firstWord.Length <= a.Width - 2) continue;
            joinWithNext[i] = true;
            any = true;
        }
        if (!any) return [];

        // Each join replaces the run of CR/NUL/LF that ended the first line with one space.
        var action = new byte?[stream.Length];   // null keep, 0 delete, otherwise the byte that stands in
        for (var i = 0; i < lines.Count; i++)
        {
            if (!joinWithNext[i]) continue;
            var end = RxOffset(lines[i], rowStart);
            var start = end;
            while (start > 0 && stream[start - 1] is 0x0A or 0x0D or 0x00)
                start--;
            for (var j = start; j <= end; j++)
                action[j] = 0;
            action[start] = (byte)' ';
        }

        var rewritten = new List<byte>(stream.Length);
        for (var j = 0; j < stream.Length; j++)
        {
            if (action[j] is null) rewritten.Add(stream[j]);
            else if (action[j] != 0) rewritten.Add(action[j]!.Value);
        }

        // The gate: the lines of the rewritten stream are the original lines with the joins made.
        var expected = new List<(string Text, LineKind Kind)>();
        for (var i = 0; i < lines.Count; i++)
        {
            var text = lines[i].Text;
            var kind = lines[i].Kind;
            while (joinWithNext[i])
            {
                i++;
                text = text + " " + lines[i].Text;
            }
            expected.Add((text, kind));
        }
        var actual = ParseLines(rewritten.ToArray(), [0], [0]).Select(l => (l.Text, l.Kind)).ToList();
        if (!expected.SequenceEqual(actual)) return null;

        // Back to the rows: a changed row keeps the bytes the edits left in its own span.
        var changed = new List<Changed>();
        for (var r = 0; r < rowStart.Count; r++)
        {
            var from = rowStart[r];
            var to = r + 1 < rowStart.Count ? rowStart[r + 1] : stream.Length;
            var touched = false;
            var data = new List<byte>(to - from);
            for (var j = from; j < to; j++)
            {
                if (action[j] is null) data.Add(stream[j]);
                else
                {
                    touched = true;
                    if (action[j] != 0) data.Add(action[j]!.Value);
                }
            }
            if (touched) changed.Add(new Changed(rowId[r], rowSeq[r], data.ToArray()));
        }
        return changed;
    }

    // Whether the second line follows the first with nothing between its terminator and its text: the
    // byte after the first line's line feed is the text's own first character, and the second line's
    // feed is the next one. A code, a pop, a prompt or a probe block in between means the first line
    // closed something - a paragraph, a turn - and the bytes would end up in the middle of a line.
    private static bool JoinsDirectly(byte[] stream, int firstLineFeed, int secondLineFeed)
    {
        var next = firstLineFeed + 1;
        if (next >= secondLineFeed || stream[next] is < 0x21 or > 0x7E) return false;
        for (var j = next; j < secondLineFeed; j++)
            if (stream[j] == 0x0A) return false;
        return true;
    }

    private static int RxOffset(Line line, List<int> rowStart) => rowStart[line.Row] + line.Offset;

    /// <summary>Every finished line of the stream: where its line feed is (as a row and an offset in
    /// it), its text and kind, and the width in force at its row.</summary>
    private static List<Line> ParseLines(byte[] stream, List<int> rowStart, List<int> rowWidth)
    {
        var lines = new List<Line>();
        var parser = new MudStreamParser();
        var emitted = new List<(string Text, LineKind Kind)>();
        parser.LineReady += line => { if (!line.IsPartial) emitted.Add((line.PlainText, line.Kind)); };
        var inGame = false;
        parser.GameModeEntered += () => inGame = true;
        parser.GameModeExited += () => inGame = false;

        var row = 0;
        for (var j = 0; j < stream.Length; j++)
        {
            while (row + 1 < rowStart.Count && rowStart[row + 1] <= j) row++;
            var before = emitted.Count;
            parser.Feed(new ReadOnlySpan<byte>(stream, j, 1));
            for (var e = before; e < emitted.Count; e++)
                lines.Add(new Line(row, j - rowStart[row], emitted[e].Text, emitted[e].Kind, rowWidth[row], inGame));
        }
        return lines;
    }
}
