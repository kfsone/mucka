using System.Globalization;

namespace MudSharp.Models;

/// <summary>
/// Recolours a chat line the local player authored so their own speech stands out in every
/// chat block. A line qualifies when it is <see cref="LineKind.Chat"/> and begins with
/// "&lt;MyName&gt; " (say/shout/emote, which MUD2 echoes under the character's own name), with
/// "You " (self-forms such as tells, which echo as <c>You tell your listeners "..."</c>), or
/// with the game's "OK, " command acknowledgement (act/social echoes: <c>OK, you wave.</c>,
/// <c>OK, Ollie the superheroine waves.</c> — always your own command, whatever the subject).
/// While invisible the game parenthesises the name — <c>(Ollie the superheroine) says ...</c> —
/// so one leading '(' before the name is accepted too.
///
/// The unquoted "label" portion is painted with the name colour and the quoted speech with the
/// speech colour — split by walking the double-quote state, so multiple quoted segments on one
/// line are each caught. Existing styling (the tell decoration's underline/italic, bold) is
/// preserved; only the foreground RGB is stamped.
///
/// A server-wrapped message keeps its colours on every physical line: continuation rows are
/// identified by <see cref="StyledLine.ContinuesChat"/> — the parser's C09 colour-scope fact —
/// never guessed from the text, so wraps outside a quote (emotes, text after the closing quote)
/// colour correctly and an unbalanced quote can never bleed onto someone else's message.
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

    /// <summary>
    /// Per-message state threaded across the lines of a drain. <see cref="SelfActive"/> is the
    /// eligibility gate: the last chat message to start was self-authored, so its
    /// <see cref="StyledLine.ContinuesChat"/> rows recolour too. <see cref="InQuote"/> only
    /// carries the name/speech split point across the wrap — it never gates.
    /// </summary>
    public struct Carry
    {
        /// <summary>The current (most recently started) chat message is self-authored.</summary>
        public bool SelfActive;
        /// <summary>A double quote was still open when the previous line of that message ended.</summary>
        public bool InQuote;
    }

    /// <summary>Parses "#rrggbb" / "rrggbb" into a packed 0xRRGGBB int, or null when malformed.</summary>
    public static int? TryParseRgb(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var s = hex.Trim();
        if (s.StartsWith('#')) s = s[1..];
        if (s.Length != 6) return null;
        return int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>
    /// True for the game's "OK, " acknowledgement of the player's own act/social command
    /// ("OK, you wave.", "OK, Ollie the superheroine waves."). The OK prefix is MUD2's
    /// command-accepted convention, so these lines are always self-authored regardless of the
    /// subject that follows. Matches <c>^OK,\s</c> — the whitespace is required so a word that
    /// merely begins "OK," mid-sentence style ("OK,then") never qualifies.
    /// </summary>
    public static bool IsOkActEcho(string text)
        => text.Length > 3
           && text.StartsWith("OK,", StringComparison.Ordinal)
           && char.IsWhiteSpace(text[3]);

    /// <summary>True when the chat line reads as one the local player authored.</summary>
    public static bool IsSelf(StyledLine line, string? myName)
    {
        if (line.Kind != LineKind.Chat) return false;
        var text = line.PlainText;
        if (IsOkActEcho(text)) return true;
        if (text.StartsWith("You ", StringComparison.Ordinal)) return true;
        // Invisible self: the game parenthesises the whole name-with-description, so the line
        // reads "(Ollie the superheroine) waves." — see PlayerNameParts.StartsWithPersona, which
        // owns that rule for every "is this line about my persona?" check.
        return PlayerNameParts.StartsWithPersona(text, myName);
    }

    /// <summary>
    /// Stateless convenience overload — colours one self-authored chat line with no cross-line
    /// message state. Equivalent to the <c>ref</c> overload started with a fresh <see cref="Carry"/>.
    /// </summary>
    public static StyledLine Apply(StyledLine line, string? myName, int nameRgb, int speechRgb)
    {
        var carry = default(Carry);
        return Apply(line, myName, nameRgb, speechRgb, ref carry);
    }

    /// <summary>
    /// Returns <paramref name="line"/> recoloured when it is a self-authored chat line (or a
    /// server soft-wrapped continuation of one), otherwise the original unchanged.
    /// <paramref name="nameRgb"/>/<paramref name="speechRgb"/> are packed 0xRRGGBB foregrounds for
    /// the unquoted label and the quoted speech respectively.
    ///
    /// <paramref name="carry"/> threads the per-message state across lines: a line qualifies as a
    /// continuation when the parser marked it <see cref="StyledLine.ContinuesChat"/> (its C09
    /// scope opened on an earlier line) AND the message that opened that scope was self-authored
    /// (<see cref="Carry.SelfActive"/>). Any other line — a new message, or anything non-chat —
    /// resets the carry, so state can never leak past the message it belongs to.
    /// </summary>
    public static StyledLine Apply(StyledLine line, string? myName, int nameRgb, int speechRgb, ref Carry carry)
    {
        bool startInQuote;
        if (IsSelf(line, myName))
        {
            carry.SelfActive = true;
            startInQuote = false;                                 // a new self line begins outside any quote
        }
        else if (line.Kind == LineKind.Chat && line.ContinuesChat && carry.SelfActive)
        {
            startInQuote = carry.InQuote;                         // wrapped continuation of the self message
        }
        else
        {
            carry = default;
            return line;
        }

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

        carry.InQuote = inQuote;   // where the name/speech split resumes if the message wraps on
        return new StyledLine(rewritten, line.IsPartial, line.Kind, line.ContinuesChat);
    }

    private static StyledSpan Recolour(StyledSpan src, string text, bool speech, int nameRgb, int speechRgb)
        => new(text, src.Style with { ForegroundRgb = speech ? speechRgb : nameRgb }, src.ClickInsertText);
}
