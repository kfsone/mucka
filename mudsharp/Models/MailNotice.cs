using System.Text.RegularExpressions;

namespace MudSharp.Models;

/// <summary>
/// The server's announcement that mail has arrived: "+- You have new mail from Drizzle -+", on a
/// line of its own with no C1 code, at any time - in game mode, at the shell, and inside the shell's
/// MAIL program - so the text is the only signal there is.
///
/// <para>Whole-line only. A player can say anything, and the notice repeated through <c>say</c>
/// arrives wrapped by its speaker ('Kayfez the pioneer says "+- You have new mail from Drizzle -+".',
/// captured), which must not read as mail.</para>
/// </summary>
public static class MailNotice
{
    private static readonly Regex Line = new(
        @"^\+- You have new mail from (?<from>.+?) -\+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>True when <paramref name="plainLine"/>, one complete line, is the notice;
    /// <paramref name="sender"/> is who it names.</summary>
    public static bool TryParse(string plainLine, out string sender)
    {
        sender = string.Empty;
        // Runs for every server line, on the Feed thread: one ordinal scan, and the regex only for the
        // rare line that passes it.
        if (!plainLine.Contains("new mail", StringComparison.Ordinal))
            return false;
        var m = Line.Match(ServerText.Collapse(plainLine));
        if (!m.Success)
            return false;
        sender = m.Groups["from"].Value;
        return true;
    }
}
