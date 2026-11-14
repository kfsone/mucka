using System.Globalization;
using System.Text;
using MudSharp.Models;

namespace Mucka.Terminal;

/// <summary>How many colours a line of ANSI SGR text may use.</summary>
public enum SgrColours
{
    /// <summary>Only the 16 standard colour codes, which the terminal draws in its own theme. A chat
    /// colour is the nearest of the pane's 16 palette slots (<see cref="CampbellPalette.Rgb"/>), by
    /// plain RGB distance.</summary>
    Simple,

    /// <summary>Every colour it writes in 24-bit, exactly as the pane draws it: a palette slot in its
    /// <see cref="CampbellPalette.Rgb"/> colour, whatever the terminal's theme, and a chat colour as
    /// the player set it. The pane's default text and page colours are still the terminal's own.</summary>
    Fancy,
}

/// <summary>
/// A line of screen as text with ANSI SGR escapes, so an ANSI log shown in a terminal reads as the
/// screen did.
///
/// <para>The terminal's own default foreground and background are left alone: a span in the pane's
/// default text colour (no colour, or slot 7) gets no colour code, bold or not, and a span on the
/// pane's page colour (no colour, or slot 0) gets no background. Every other colour is written as
/// <see cref="SgrColours"/> says: in Simple, a palette slot is its standard code (30-37 and 90-97 for
/// the foreground, 40-47 and 100-107 for the background), which the terminal draws in its own theme;
/// in Fancy, every colour is the pane's own in 24-bit. A span with no chat colour keeps the pane's
/// "bold = bright" promotion of slots 0-7 (<see cref="CampbellPalette.ForegroundRgb"/>), so a bold red
/// span is bright red and bold. A chat span's colour is resolved as the pane resolves it
/// (<see cref="ChatColorizer.Palette.RgbOf"/> of <see cref="ChatColorizer.ColourPartOf"/>, drawn as
/// <see cref="ChatColorizer.Dim"/> of it in the Dim face).</para>
///
/// <para>The faces are the pane's: bold and italic from <see cref="ChatColorizer.Palette.FaceOf"/>,
/// and underline. Reverse and blink are not written, as the pane draws neither. An escape is written
/// only where the look changes from the span before it, so spans of one look share one and plain
/// default text has none.</para>
/// </summary>
public static class SgrText
{
    private const char Esc = (char)27;

    /// <summary>What ends a line that left an attribute set: every attribute back to the terminal's own.</summary>
    public static readonly string Reset = Esc + "[0m";

    /// <summary><paramref name="line"/> with its colours and faces as SGR where they change, ending in
    /// <see cref="Reset"/> only if the line left an attribute set; a line with no text is empty. Chat
    /// spans are drawn from <paramref name="chat"/>, as on screen.</summary>
    public static string Format(StyledLine line, ChatColorizer.Palette chat, SgrColours colours)
    {
        ArgumentNullException.ThrowIfNull(line);
        var text = new StringBuilder();
        var shown = default(Look);
        foreach (var span in line.Spans)
        {
            if (span.Text.Length == 0)
                continue;
            var look = LookOf(span.Style, chat, colours);
            if (look != shown)
                Change(text, shown, look);
            shown = look;
            text.Append(span.Text);
        }
        if (shown != default)
            text.Append(Reset);
        return text.ToString();
    }

    /// <summary>What a span is drawn as. A null colour is the terminal's own, so <c>default</c> is
    /// plain text.</summary>
    private readonly record struct Look(string? Foreground, string? Background, bool Bold, bool Italic, bool Underline);

    private static Look LookOf(TextStyle style, ChatColorizer.Palette chat, SgrColours colours)
    {
        var face = chat.FaceOf(style);
        return new Look(Foreground(style, chat, colours), Background(style, colours),
            (face & ChatFace.Bold) != 0, (face & ChatFace.Italic) != 0, style.Underline);
    }

    private static string? Foreground(TextStyle style, ChatColorizer.Palette chat, SgrColours colours)
    {
        if (chat.RgbOf(ChatColorizer.ColourPartOf(style)) is not null)
        {
            var rgb = CampbellPalette.ForegroundRgb(style, chat);
            return colours == SgrColours.Fancy ? TrueColour("38", rgb) : ForegroundCode(Nearest(rgb));
        }
        var slot = (int)style.Foreground;
        // The pane's default text colour is the terminal's own default, however MUD2 named it: game
        // text arrives as an explicit white (37) far more often than as no colour at all.
        if (slot is < 0 or >= 16 || slot == CampbellPalette.DefaultForegroundSlot)
            return null;
        // A bold slot 0-7 is drawn as its bright slot, as the pane draws it.
        var drawn = style.Bold && slot < 8 ? slot + 8 : slot;
        return colours == SgrColours.Fancy ? TrueColour("38", CampbellPalette.Rgb[drawn]) : ForegroundCode(drawn);
    }

    private static string? Background(TextStyle style, SgrColours colours)
    {
        var slot = (int)style.Background;
        // The pane's page colour is the terminal's own background, however MUD2 named it: game text
        // arrives on an explicit black (40), which is the page colour on screen.
        if (slot is < 0 or >= 16 || slot == CampbellPalette.BackgroundSlot)
            return null;
        return colours == SgrColours.Fancy ? TrueColour("48", CampbellPalette.Rgb[slot])
            : (slot < 8 ? 40 + slot : 100 + slot - 8).ToString(CultureInfo.InvariantCulture);
    }

    private static string TrueColour(string plane, int rgb) => string.Create(CultureInfo.InvariantCulture,
        $"{plane};2;{(rgb >> 16) & 0xFF};{(rgb >> 8) & 0xFF};{rgb & 0xFF}");

    private static string ForegroundCode(int slot)
        => (slot < 8 ? 30 + slot : 90 + slot - 8).ToString(CultureInfo.InvariantCulture);

    /// <summary>The palette slot nearest to <paramref name="rgb"/> by squared RGB distance; the lowest
    /// slot of two equally near.</summary>
    private static int Nearest(int rgb)
    {
        var best = 0;
        var bestDistance = int.MaxValue;
        for (var slot = 0; slot < CampbellPalette.Rgb.Count; slot++)
        {
            var distance = Square(Channel(rgb, 16) - Channel(CampbellPalette.Rgb[slot], 16))
                         + Square(Channel(rgb, 8) - Channel(CampbellPalette.Rgb[slot], 8))
                         + Square(Channel(rgb, 0) - Channel(CampbellPalette.Rgb[slot], 0));
            if (distance < bestDistance)
            {
                best = slot;
                bestDistance = distance;
            }
        }
        return best;
    }

    private static int Channel(int rgb, int shift) => (rgb >> shift) & 0xFF;

    private static int Square(int n) => n * n;

    /// <summary>Writes the escape that takes the terminal from <paramref name="from"/> to
    /// <paramref name="to"/>: only what differs, unless something has to be switched off, which is a
    /// reset followed by everything <paramref name="to"/> sets.</summary>
    private static void Change(StringBuilder text, Look from, Look to)
    {
        var reset = (from.Foreground is not null && to.Foreground is null)
                    || (from.Background is not null && to.Background is null)
                    || (from.Bold && !to.Bold)
                    || (from.Italic && !to.Italic)
                    || (from.Underline && !to.Underline);
        var shown = reset ? default : from;
        var codes = new List<string>(5);
        if (reset)
            codes.Add("0");
        if (to.Foreground is not null && to.Foreground != shown.Foreground)
            codes.Add(to.Foreground);
        if (to.Background is not null && to.Background != shown.Background)
            codes.Add(to.Background);
        if (to.Bold && !shown.Bold)
            codes.Add("1");
        if (to.Italic && !shown.Italic)
            codes.Add("3");
        if (to.Underline && !shown.Underline)
            codes.Add("4");
        text.Append(Esc).Append('[').AppendJoin(';', codes).Append('m');
    }
}
