using MudSharp.Models;

namespace Mucka.Commands;

/// <summary>
/// The last few lines of server output, so a sentence the server wrapped across rows can be read
/// whole. MUD2 wraps at the negotiated width (see <see cref="ServerText"/>): at /T20 the banner's
/// "This reset is number 127276." arrived as "This reset is" / "number 127276.".
///
/// <para>A sentence that ends on the newest line starts on one of the lines before it, so the
/// candidates are the newest line alone, then it joined to the one before, and so on back to
/// <see cref="Depth"/> lines, each collapsed by <see cref="ServerText.Collapse"/>.</para>
///
/// <para>Not thread-safe; one caller drives it from one thread.</para>
/// </summary>
public sealed class WrappedTail
{
    private readonly string[] _lines;
    private int _count;

    /// <param name="depth">The most rows one sentence is expected to span.</param>
    public WrappedTail(int depth)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(depth, 1);
        _lines = new string[depth];
    }

    /// <summary>The most lines a candidate joins.</summary>
    public int Depth => _lines.Length;

    /// <summary>Records the newest line, forgetting the oldest once <see cref="Depth"/> are held.</summary>
    public void Add(string text)
    {
        if (_count == _lines.Length)
        {
            Array.Copy(_lines, 1, _lines, 0, _lines.Length - 1);
            _count--;
        }
        _lines[_count++] = text ?? string.Empty;
    }

    /// <summary>Forgets every line, so a sentence already read is not read again as part of the
    /// next one.</summary>
    public void Clear()
    {
        Array.Clear(_lines);
        _count = 0;
    }

    /// <summary>The candidates ending on the newest line, shortest first. A row that ends a sentence
    /// is not the front of a wrapped one, so no candidate reaches back past it: two separate sentences
    /// are never read as one.</summary>
    public IEnumerable<string> Candidates()
    {
        for (var k = 1; k <= _count; k++)
        {
            if (k > 1 && EndsSentence(_lines[_count - k]))
                yield break;
            yield return ServerText.Collapse(string.Join(' ', _lines, _count - k, k));
        }
    }

    private static bool EndsSentence(string line)
    {
        var s = line.AsSpan().TrimEnd(" \r\n\t\0");
        return s.Length > 0 && s[^1] is '.' or '!' or '?';
    }
}
