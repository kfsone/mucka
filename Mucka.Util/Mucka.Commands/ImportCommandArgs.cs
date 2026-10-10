using System.Text.RegularExpressions;

namespace Mucka.Commands;

/// <summary>
/// The argument of <c>$import2 [path] [unclosed id[,id...]]</c>: where the MuckaII database is, and
/// which of its runs with no recorded end the operator vouches are finished. Pure text, so it is
/// tested without the view model.
/// </summary>
public readonly record struct ImportCommandArgs(string Path, IReadOnlySet<long> Unclosed)
{
    private static readonly Regex UnclosedTail =
        new(@"(?:^|\s)unclosed\s+(?<ids>\d+(?:\s*,\s*\d+)*)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <param name="argument">Everything after <c>$import2</c>.</param>
    /// <param name="home">The home directory, for a leading <c>~</c>.</param>
    /// <param name="defaultPath">Used when no path is given; null when there is none on this platform.</param>
    public static ImportCommandArgs? Parse(string argument, string home, string? defaultPath, out string? error)
    {
        error = null;
        var text = argument.Trim();
        var unclosed = new HashSet<long>();

        var tail = UnclosedTail.Match(text);
        if (tail.Success)
        {
            foreach (var id in tail.Groups["ids"].Value.Split(','))
                unclosed.Add(long.Parse(id.Trim(), System.Globalization.CultureInfo.InvariantCulture));
            text = text[..tail.Index].Trim();
        }

        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
            text = text[1..^1].Trim();

        if (text.Length == 0)
        {
            if (defaultPath is null)
            {
                error = "no default location on this platform";
                return null;
            }
            text = defaultPath;
        }
        else if (text == "~" || text.StartsWith("~/", StringComparison.Ordinal)
                 || text.StartsWith("~\\", StringComparison.Ordinal))
        {
            text = System.IO.Path.Combine(home, text[1..].TrimStart('/', '\\'));
        }

        return new ImportCommandArgs(text, unclosed);
    }
}
