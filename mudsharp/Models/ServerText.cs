namespace MudSharp.Models;

/// <summary>
/// Server text as the player reads it, with the server's wrapping taken out.
///
/// <para>MUD2 wraps everything it sends at the negotiated terminal width and ends each wrapped row with
/// "\r\0\r\n", so one sentence arrives as however many lines that width makes of it. Observed on a
/// phone at /T20: "You're already" / "getting object" / "identification" / "numbers where" /
/// "applicable." A match against one physical line is a match against a width, not against the
/// text.</para>
/// </summary>
public static class ServerText
{
    /// <summary>Collapses every run of whitespace, and the NUL of the wrap padding, to one space and
    /// trims the ends. Lines joined with any whitespace and passed through this read the same at any
    /// width.</summary>
    public static string Collapse(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var sb = new System.Text.StringBuilder(text.Length);
        var lastWasSpace = false;
        foreach (var c in text)
        {
            if (c is ' ' or '\r' or '\n' or '\t' or '\0')
            {
                if (!lastWasSpace && sb.Length > 0)
                    sb.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                sb.Append(c);
                lastWasSpace = false;
            }
        }

        if (sb.Length > 0 && sb[^1] == ' ')
            sb.Length--;
        return sb.ToString();
    }
}
