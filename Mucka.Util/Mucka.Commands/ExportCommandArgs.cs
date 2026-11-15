namespace Mucka.Commands;

/// <summary>
/// The argument of <c>$EXPORT [path]</c>: where the snapshot is written. Pure text, so it is tested
/// without the view model.
/// </summary>
public readonly record struct ExportCommandArgs(string Path)
{
    /// <param name="argument">Everything after <c>$EXPORT</c>.</param>
    /// <param name="home">The home directory, for a leading <c>~</c>.</param>
    /// <param name="defaultPath">Used when no path is given.</param>
    public static ExportCommandArgs Parse(string argument, string home, string defaultPath)
    {
        var text = argument.Trim();

        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
            text = text[1..^1].Trim();

        if (text.Length == 0)
        {
            text = defaultPath;
        }
        else if (text == "~" || text.StartsWith("~/", StringComparison.Ordinal)
                 || text.StartsWith("~\\", StringComparison.Ordinal))
        {
            text = System.IO.Path.Combine(home, text[1..].TrimStart('/', '\\'));
        }

        return new ExportCommandArgs(text);
    }
}
