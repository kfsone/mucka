using System.Globalization;

namespace MudSharp.Models;

/// <summary>
/// The chat colour and face settings, and the ingest pass that marks each span of a C09 message
/// with whose message it is, by the <see cref="SpeechPart"/> the decoder stamped on it.
///
/// Operator rules:
/// <list type="bullet">
/// <item>Four rows, for everyone's lines alike: speaker (the 09 00 label - who did the thing -
///   and the act/emote/social text of 09 04 .. 09 10), and the words of each channel: say (09 02),
///   shout (09 01: shout, yell, yodel, scream, holler) and tell (09 03, both directions).</item>
/// <item>Each row has a colour and, for your own messages and for another Creature's, a face:
///   any of italic, bold and dim (<see cref="ChatFaces"/>). The speaker row's face styles the
///   label and the act/emote text alike. Dim draws <see cref="Dim"/> of the row's colour.</item>
/// <item>The default faces: the words of your own say, shout and tell italic -
///   <c>Kayfez the warlock says "&lt;i&gt;I will trade you a candle for that rope&lt;/i&gt;"</c> -
///   and nothing else italic, bold or dim.</item>
/// <item>Colours and faces are looked up when a span is drawn (<see cref="CampbellPalette.ForegroundRgb"/>,
///   <see cref="Palette.FaceOf"/>), never stored on it, so a changed setting restyles every line
///   already held.</item>
/// <item>The default colours are fixed values: speaker and say are the decoder's built-in
///   palette slots, shout is #FF8210 and tell is #91BCFF. Changing one setting never changes
///   another.</item>
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
    // The default settings. Speaker and say are the palette slots the decoder gives the 09 00
    // speaker frame (YELLOW) and the word frames (LT_YELLOW); shout and tell are fixed colours.
    public static readonly int    DefaultSpeakerRgb = CampbellPalette.Rgb[3];
    public static readonly int    DefaultSayRgb     = CampbellPalette.Rgb[11];
    public const           int    DefaultShoutRgb   = 0xFF8210;
    public const           int    DefaultTellRgb    = 0x91BCFF;
    public static readonly string DefaultSpeakerHex = ToHex(DefaultSpeakerRgb);
    public static readonly string DefaultSayHex     = ToHex(DefaultSayRgb);
    public static readonly string DefaultShoutHex   = ToHex(DefaultShoutRgb);
    public static readonly string DefaultTellHex    = ToHex(DefaultTellRgb);

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
    public readonly record struct Palette(int Speaker, int Say, int Shout, int Tell, ChatFaces Faces)
    {
        /// <summary>The colour for a span's part, or null for a span no C09 frame styled.</summary>
        public int? RgbOf(SpeechPart part) => part switch
        {
            SpeechPart.Speaker => Speaker,
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

    /// <summary>The palette of the default settings.</summary>
    public static readonly Palette DefaultPalette =
        new(DefaultSpeakerRgb, DefaultSayRgb, DefaultShoutRgb, DefaultTellRgb, ChatFaces.Default);

    /// <summary>
    /// The palette the settings hex strings and faces describe. Each colour is independent: a
    /// missing or malformed value falls back to that colour's own default.
    /// </summary>
    public static Palette ResolvePalette(string? speakerHex, string? sayHex, string? shoutHex, string? tellHex, ChatFaces faces)
        => new(
            Speaker: TryParseRgb(speakerHex) ?? DefaultSpeakerRgb,
            Say:     TryParseRgb(sayHex)     ?? DefaultSayRgb,
            Shout:   TryParseRgb(shoutHex)   ?? DefaultShoutRgb,
            Tell:    TryParseRgb(tellHex)    ?? DefaultTellRgb,
            Faces:   faces);

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
        if (line.Kind != LineKind.Chat)
        {
            carry = default;
            return line;
        }
        if (!line.ContinuesChat)
            carry = new Carry { Self = IsSelf(line, myName) };
        bool self = carry.Self;

        var rewritten = new List<StyledSpan>(line.Spans.Count);
        bool changed = false;
        foreach (var span in line.Spans)
        {
            bool own = self && span.Style.Speech != SpeechPart.None;
            if (!span.Style.Bold && !span.Style.Italic && span.Style.Own == own)
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
            rewritten.Add(new StyledSpan(span.Text, style with { Bold = false, Italic = false, Own = own }, span.ClickInsertText));
            changed = true;
        }
        return changed ? line.WithSpans(rewritten) : line;
    }
}
