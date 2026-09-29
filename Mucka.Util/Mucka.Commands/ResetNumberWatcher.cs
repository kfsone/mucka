namespace Mucka.Commands;

/// <summary>
/// Reads the world's reset number off the login banner as its lines go past, wherever the server
/// wrapped "This reset is number 127276." (see <see cref="WrappedTail"/>; at /T20 it arrived as
/// "This reset is" / "number 127276.").
///
/// <para>On the line-ready path, so it is cheap: the sentence ends in digits and a full stop, and
/// no line that does not is joined or matched.</para>
///
/// <para>Not thread-safe; one caller drives it from one thread.</para>
/// </summary>
public sealed class ResetNumberWatcher
{
    // "This reset is number" is 20 columns on its own, so at the narrowest width it can take a row
    // to itself and leave the number on the next: three rows at most.
    private const int SentenceRows = 3;
    private readonly WrappedTail _tail = new(SentenceRows);

    /// <summary>Notes one line of server output. True, with the number, on the line that completes
    /// the sentence; the lines are then forgotten so the next line cannot read the same one
    /// again.</summary>
    public bool TryNote(string text, out long resetNumber)
    {
        resetNumber = 0;
        if (string.IsNullOrEmpty(text))
            return false;

        _tail.Add(text);
        if (!EndsInNumberAndStop(text))
            return false;

        foreach (var candidate in _tail.Candidates())
        {
            if (candidate.Contains("This reset is number", StringComparison.Ordinal)
                && ShellText.TryParseResetNumber(candidate, out resetNumber))
            {
                _tail.Clear();
                return true;
            }
        }
        return false;
    }

    private static bool EndsInNumberAndStop(string text)
    {
        var s = text.AsSpan().TrimEnd(" \r\n\t\0");
        return s.Length >= 2 && s[^1] == '.' && char.IsAsciiDigit(s[^2]);
    }
}
