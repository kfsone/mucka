using System.Text;
using MudSharp.Models;

namespace Mucka.Terminal;

/// <summary>
/// Strips non-printable C0 control characters (0x00–0x1F) and DEL (0x7F) from line text
/// before it reaches the renderer. These have no glyph in a monospace font and would draw
/// as .notdef "tofu" boxes — e.g. a trailing carriage return from a CRLF line ending, or a
/// stray backspace. (The old WebView silently ignored them; Skia does not.)
///
/// Form-feed (0x0C) is deliberately preserved: <see cref="TerminalBuffer"/> consumes it as a
/// clear-screen, so stripping it here would break that. Newlines never appear in a line's
/// spans (the parser splits on them).
/// </summary>
public static class TerminalText
{
    private static bool IsStrippable(char c) => (c < 0x20 && c != '\f') || c == 0x7F;

    /// <summary>Remove strippable control characters. Returns the same instance if there are none.</summary>
    public static string StripControls(string s)
    {
        int hit = -1;
        for (int i = 0; i < s.Length; i++)
            if (IsStrippable(s[i])) { hit = i; break; }
        if (hit < 0) return s;   // common case: nothing to strip

        var sb = new StringBuilder(s.Length);
        sb.Append(s, 0, hit);
        for (int i = hit; i < s.Length; i++)
            if (!IsStrippable(s[i])) sb.Append(s[i]);
        return sb.ToString();
    }

    /// <summary>
    /// Return <paramref name="line"/> with control characters stripped from every span; spans
    /// that become empty are dropped. <see cref="StyledLine.IsPartial"/> is preserved. Returns
    /// the same instance when there is nothing to strip.
    /// </summary>
    public static StyledLine Sanitize(StyledLine line)
    {
        bool any = false;
        for (int i = 0; i < line.Spans.Count && !any; i++)
        {
            var t = line.Spans[i].Text;
            for (int j = 0; j < t.Length; j++)
                if (IsStrippable(t[j])) { any = true; break; }
        }
        if (!any) return line;

        var cleaned = new List<StyledSpan>(line.Spans.Count);
        foreach (var span in line.Spans)
        {
            var t = StripControls(span.Text);
            if (t.Length > 0) cleaned.Add(span with { Text = t });
        }
        return new StyledLine(cleaned, line.IsPartial);
    }
}
