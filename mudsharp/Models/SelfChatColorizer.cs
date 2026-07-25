using System.Globalization;

namespace MudSharp.Models;

/// <summary>
/// Recolours a chat line the local player authored so their own speech stands out in every
/// chat block. A line qualifies when it is <see cref="LineKind.Chat"/> and begins either with
/// "&lt;MyName&gt; " (say/shout/emote, which MUD2 echoes under the character's own name) or with
/// "You " (self-forms such as tells, which echo as <c>You tell your listeners "..."</c>).
///
/// The unquoted "label" portion is painted with the name colour and the quoted speech with the
/// speech colour — split by walking the double-quote state, so multiple quoted segments on one
/// line are each caught. Existing styling (the tell decoration's underline/italic, bold) is
/// preserved; only the foreground RGB is stamped.
/// </summary>
public static class SelfChatColorizer
{
    // Default self-chat colours — the single source of truth. The hex strings are canonical
    // (used as the settings/persistence defaults); the RGB ints are derived from them so the
    // two representations can never drift apart.
    public static readonly string DefaultNameHex   = "e09840";
    public static readonly string DefaultSpeechHex = "ffcf60";
    public static readonly int    DefaultNameRgb   = TryParseRgb(DefaultNameHex)!.Value;
    public static readonly int    DefaultSpeechRgb = TryParseRgb(DefaultSpeechHex)!.Value;

    /// <summary>Parses "#rrggbb" / "rrggbb" into a packed 0xRRGGBB int, or null when malformed.</summary>
    public static int? TryParseRgb(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var s = hex.Trim();
        if (s.StartsWith('#')) s = s[1..];
        if (s.Length != 6) return null;
        return int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>True when the chat line reads as one the local player authored.</summary>
    public static bool IsSelf(StyledLine line, string? myName)
    {
        if (line.Kind != LineKind.Chat) return false;
        var text = line.PlainText;
        if (text.StartsWith("You ", StringComparison.Ordinal)) return true;
        return myName is { Length: > 0 }
            && text.StartsWith(myName + " ", StringComparison.Ordinal);
    }

    /// <summary>
    /// Stateless convenience overload — colours one self-authored chat line with no cross-line
    /// quote continuation. Equivalent to the <c>ref</c> overload started with a closed quote.
    /// </summary>
    public static StyledLine Apply(StyledLine line, string? myName, int nameRgb, int speechRgb)
    {
        bool carry = false;
        return Apply(line, myName, nameRgb, speechRgb, ref carry);
    }

    /// <summary>
    /// Returns <paramref name="line"/> recoloured when it is a self-authored chat line (or a
    /// server soft-wrapped continuation of one), otherwise the original unchanged.
    /// <paramref name="nameRgb"/>/<paramref name="speechRgb"/> are packed 0xRRGGBB foregrounds for
    /// the unquoted label and the quoted speech respectively.
    ///
    /// <paramref name="carryQuote"/> threads the "inside an open quote" state across lines: pass
    /// <c>false</c> for a fresh drain; it is updated to whether a quote is still open past this line
    /// so the caller can feed it back for the next. A line that does not itself start with a self
    /// prefix is only treated as a continuation while a quote is open AND it is a
    /// <see cref="LineKind.Chat"/> line — matching how MUD2 keeps wrapped speaker text in the chat
    /// colour scope. This is what keeps every wrapped row of a long tell in the speech colour, not
    /// just the first.
    /// </summary>
    public static StyledLine Apply(StyledLine line, string? myName, int nameRgb, int speechRgb, ref bool carryQuote)
    {
        bool startInQuote;
        if (IsSelf(line, myName))
            startInQuote = false;                                 // a new self line begins outside any quote
        else if (carryQuote && line.Kind == LineKind.Chat)
            startInQuote = true;                                  // wrapped continuation of the self speech
        else { carryQuote = false; return line; }

        var rewritten = new List<StyledSpan>(line.Spans.Count + 4);
        bool inQuote = startInQuote;
        foreach (var span in line.Spans)
        {
            var t = span.Text;
            if (t.Length == 0) { rewritten.Add(span); continue; }

            int runStart = 0;
            bool runSpeech = false;
            bool runOpen = false;
            for (int i = 0; i < t.Length; i++)
            {
                char c = t[i];
                bool isSpeech = inQuote || c == '"';   // the quote char itself reads as speech
                if (c == '"') inQuote = !inQuote;

                if (!runOpen) { runStart = i; runSpeech = isSpeech; runOpen = true; }
                else if (isSpeech != runSpeech)
                {
                    rewritten.Add(Recolour(span, t[runStart..i], runSpeech, nameRgb, speechRgb));
                    runStart = i;
                    runSpeech = isSpeech;
                }
            }
            if (runOpen)
                rewritten.Add(Recolour(span, t[runStart..], runSpeech, nameRgb, speechRgb));
        }

        carryQuote = inQuote;   // stays true only while a quote remains open into the next line
        return new StyledLine(rewritten, line.IsPartial, line.Kind);
    }

    private static StyledSpan Recolour(StyledSpan src, string text, bool speech, int nameRgb, int speechRgb)
        => new(text, src.Style with { ForegroundRgb = speech ? speechRgb : nameRgb }, src.ClickInsertText);
}
