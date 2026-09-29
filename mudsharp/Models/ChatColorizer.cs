using System.Globalization;

namespace MudSharp.Models;

/// <summary>
/// The chat colour and face settings, and the ingest pass that marks each span of a C09 message
/// with whose message it is, by the <see cref="SpeechPart"/> the decoder stamped on it.
///
/// Operator rules:
/// <list type="bullet">
/// <item>Four rows, for everyone's lines alike: emotes (the 09 00 label - who did the thing -
///   and the act/emote/social text of 09 04 .. 09 10), and the words of each channel: say (09 02),
///   shout (09 01: shout, yell, yodel, scream, holler) and tell (09 03, both directions).</item>
/// <item>Each row has a colour and, for your own messages and for another Creature's, a face:
///   any of italic, bold and dim (<see cref="ChatFaces"/>). The emotes row's face styles the
///   label and the act/emote text alike. Dim draws <see cref="Dim"/> of the row's colour.</item>
/// <item>A shout or a tell is its channel's colour end to end (<see cref="ColoursWholeLine"/>);
///   its label keeps the emotes row's face.</item>
/// <item>The default faces: the words of your own say, shout and tell italic -
///   <c>Kayfez the warlock says "&lt;i&gt;I will trade you a candle for that rope&lt;/i&gt;"</c> -
///   and nothing else italic, bold or dim.</item>
/// <item>Colours and faces are looked up when a span is drawn (<see cref="CampbellPalette.ForegroundRgb"/>,
///   <see cref="Palette.FaceOf"/>), never stored on it, so a changed setting restyles every line
///   already held.</item>
/// <item>The default colours are the selected <see cref="ChatTheme"/>'s, Lamplight when none is
///   selected. Changing one colour never changes another.</item>
/// </list>
///
/// Your own messages are found by <see cref="IsSelf"/>: a line that begins with "&lt;MyName&gt; "
/// (MUD2 echoes say/shout/emote under the character's own name), with "You " (self-forms such as
/// <c>You tell your listeners "..."</c> and <c>You shout "..."</c>), or with the game's "OK, "
/// command acknowledgement (<c>OK, Ollie the superheroine waves.</c>). While invisible the game
/// parenthesises the name - <c>(Ollie the superheroine) says ...</c> - which
/// <see cref="PlayerNameParts.StartsWithPersona"/> accepts. A tell to you reads
/// <c>Lazlo the yeoman tells you "..."</c> and is someone else's.
///
/// A server-wrapped message keeps its verdict on every physical line: continuation rows are
/// identified by <see cref="StyledLine.ContinuesChat"/> - the parser's C09 colour-scope fact - and
/// take the whose-message verdict of the row that started it, never one guessed from their text.
/// </summary>
public static class ChatColorizer
{
    /// <summary>
    /// How much of a row's colour a dim span keeps, in percent; the rest is the pane background
    /// (<see cref="CampbellPalette.BackgroundSlot"/>).
    /// </summary>
    public const int DimKeepPercent = 60;

    /// <summary>
    /// The dim rendering of a packed 0xRRGGBB: each channel blended toward the pane background,
    /// keeping <see cref="DimKeepPercent"/> of the colour, rounded to nearest.
    /// </summary>
    public static int Dim(int rgb)
    {
        int bg = CampbellPalette.Rgb[CampbellPalette.BackgroundSlot];
        int Channel(int shift)
        {
            int c = (rgb >> shift) & 0xFF, b = (bg >> shift) & 0xFF;
            return (c * DimKeepPercent + b * (100 - DimKeepPercent) + 50) / 100;
        }
        return (Channel(16) << 16) | (Channel(8) << 8) | Channel(0);
    }

    /// <summary>The packed 0xRRGGBB colour of each message part, and the faces of each row.</summary>
    public readonly record struct Palette(int Emote, int Say, int Shout, int Tell, ChatFaces Faces)
    {
        /// <summary>The colour for a span's part, or null for a span no C09 frame styled.</summary>
        public int? RgbOf(SpeechPart part) => part switch
        {
            SpeechPart.Speaker => Emote,
            SpeechPart.Say     => Say,
            SpeechPart.Shout   => Shout,
            SpeechPart.Tell    => Tell,
            _                  => null,
        };

        /// <summary>
        /// The face a span is drawn in: for a span with a <see cref="TextStyle.Speech"/> part, its
        /// row's own or other face by <see cref="TextStyle.Own"/>, whatever else it carries; for any
        /// other span, the italic and bold it carries.
        /// </summary>
        public ChatFace FaceOf(TextStyle style) => style.Speech == SpeechPart.None
            ? (style.Italic ? ChatFace.Italic : ChatFace.None) | (style.Bold ? ChatFace.Bold : ChatFace.None)
            : Faces.Of(style.Speech, style.Own);
    }

    /// <summary>
    /// Operator rule: a shout or a tell is drawn in its channel's colour from end to end - the
    /// emote spans that frame the quoted words included - so the quoted words' colour never has to
    /// sit against the Emotes colour. A say keeps the Emotes colour around its words.
    /// </summary>
    public static bool ColoursWholeLine(SpeechPart channel) => channel is SpeechPart.Shout or SpeechPart.Tell;

    /// <summary>The part whose colour a span is drawn in: its message's channel for an emote span of
    /// a message <see cref="ColoursWholeLine"/> covers, its own part otherwise. Its face is always its
    /// own part's.</summary>
    public static SpeechPart ColourPartOf(TextStyle style)
        => style.Speech == SpeechPart.Speaker && ColoursWholeLine(style.Channel) ? style.Channel : style.Speech;

    /// <summary>The palette of the default settings: the default theme's colours and the default faces.</summary>
    public static Palette DefaultPalette => ChatTheme.Default.ResetPalette();

    /// <summary>
    /// The palette the settings hex strings and faces describe. Each colour is independent: a
    /// missing or malformed value falls back to that colour in <paramref name="theme"/>.
    /// </summary>
    public static Palette ResolvePalette(ChatTheme theme, string? emoteHex, string? sayHex, string? shoutHex, string? tellHex, ChatFaces faces)
    {
        ArgumentNullException.ThrowIfNull(theme);
        return new(
            Emote: TryParseRgb(emoteHex) ?? theme.Emote,
            Say:     TryParseRgb(sayHex)     ?? theme.Say,
            Shout:   TryParseRgb(shoutHex)   ?? theme.Shout,
            Tell:    TryParseRgb(tellHex)    ?? theme.Tell,
            Faces:   faces);
    }

    /// <summary>A packed 0xRRGGBB as 6 lowercase hex digits, no '#'.</summary>
    public static string ToHex(int rgb) => (rgb & 0xFFFFFF).ToString("x6", CultureInfo.InvariantCulture);

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
    /// Per-message state threaded across the lines of a drain: whose message the current C09
    /// scope belongs to, decided on the row that started it and read by its continuation rows.
    /// </summary>
    public struct Carry
    {
        /// <summary>The current (most recently started) chat message is the local player's.</summary>
        public bool Self { get; set; }

        /// <summary>The current chat message's channel, once a row of it has shown its quoted
        /// words: <see cref="SpeechPart.None"/> until then.</summary>
        public SpeechPart Channel { get; set; }
    }

    /// <summary>The channel of the quoted words on a line: the part of its first say, shout or tell
    /// span, or <see cref="SpeechPart.None"/>.</summary>
    private static SpeechPart ChannelOf(StyledLine line)
    {
        foreach (var span in line.Spans)
            if (span.Style.Speech is SpeechPart.Say or SpeechPart.Shout or SpeechPart.Tell)
                return span.Style.Speech;
        return SpeechPart.None;
    }

    /// <summary>
    /// <see cref="Apply(StyledLine, string?, ref Carry)"/> over a drain, in place. A message the
    /// server wrapped can open on rows that end before its quoted words start, so each message's
    /// channel is read across all of its rows in <paramref name="lines"/> before any is restyled.
    /// A message split across two drains colours its earlier rows by what the earlier drain held.
    /// </summary>
    public static void ApplyAll(IList<StyledLine> lines, string? myName, ref Carry carry)
    {
        ArgumentNullException.ThrowIfNull(lines);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Kind == LineKind.Chat)
            {
                var channel = line.ContinuesChat ? carry.Channel : SpeechPart.None;
                for (var j = i; channel == SpeechPart.None && j < lines.Count; j++)
                {
                    if (j > i && (lines[j].Kind != LineKind.Chat || !lines[j].ContinuesChat))
                        break;
                    channel = ChannelOf(lines[j]);
                }
                if (!line.ContinuesChat)
                    carry = new Carry { Self = IsSelf(line, myName) };
                carry.Channel = channel;
            }
            lines[i] = Restyle(line, ref carry);
        }
    }

    /// <summary>
    /// True for the game's "OK, " acknowledgement of the player's own act/social command
    /// ("OK, you wave.", "OK, Ollie the superheroine waves."). The OK prefix is MUD2's
    /// command-accepted convention, so these lines are always self-authored regardless of the
    /// subject that follows. Matches <c>^OK,\s</c> - the whitespace is required so a word that
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
        return PlayerNameParts.StartsWithPersona(text, myName);
    }

    /// <summary>
    /// Stateless convenience overload - restyles one chat line with no cross-line message state.
    /// Equivalent to the <c>ref</c> overload started with a fresh <see cref="Carry"/>.
    /// </summary>
    public static StyledLine Apply(StyledLine line, string? myName)
    {
        var carry = default(Carry);
        return Apply(line, myName, ref carry);
    }

    /// <summary>
    /// Returns a <see cref="LineKind.Chat"/> line with no span carrying bold or italic, and every
    /// span with a <see cref="SpeechPart"/> marked <see cref="TextStyle.Own"/> when the message is
    /// the local player's; any other line unchanged. Colour and face are not touched: they are
    /// resolved when the span is drawn. <paramref name="carry"/> carries the whose-message verdict
    /// from a message's first row to its <see cref="StyledLine.ContinuesChat"/> rows; a non-chat
    /// line resets it.
    /// </summary>
    public static StyledLine Apply(StyledLine line, string? myName, ref Carry carry)
    {
        if (line.Kind == LineKind.Chat)
        {
            if (!line.ContinuesChat)
                carry = new Carry { Self = IsSelf(line, myName) };
            if (carry.Channel == SpeechPart.None)
                carry.Channel = ChannelOf(line);
        }
        return Restyle(line, ref carry);
    }

    // The restyle both overloads share, on the whose-message and channel verdicts already in
    // `carry`: every speech span marked Own and with the message's channel.
    private static StyledLine Restyle(StyledLine line, ref Carry carry)
    {
        if (line.Kind != LineKind.Chat)
        {
            carry = default;
            return line;
        }
        bool self = carry.Self;
        var channel = carry.Channel;

        var rewritten = new List<StyledSpan>(line.Spans.Count);
        bool changed = false;
        foreach (var span in line.Spans)
        {
            bool speech = span.Style.Speech != SpeechPart.None;
            bool own = self && speech;
            var spanChannel = speech ? channel : SpeechPart.None;
            if (!span.Style.Bold && !span.Style.Italic && span.Style.Own == own && span.Style.Channel == spanChannel)
            {
                rewritten.Add(span);
                continue;
            }
            // A span no C09 frame styled keeps its drawn colour when its bold goes: bold promoted
            // a dim slot to its bright one, so the bright slot is written instead.
            var style = span.Style;
            if (style.Bold && style.Speech == SpeechPart.None)
            {
                int slot = style.Foreground == AnsiColor.Default
                    ? CampbellPalette.DefaultForegroundSlot : (int)style.Foreground;
                if (slot is >= 0 and < 8) style = style with { Foreground = (AnsiColor)(slot + 8) };
            }
            rewritten.Add(new StyledSpan(span.Text, style with { Bold = false, Italic = false, Own = own, Channel = spanChannel }, span.ClickInsertText));
            changed = true;
        }
        return changed ? line.WithSpans(rewritten) : line;
    }
}
