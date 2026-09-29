namespace MudSharp.Models;

/// <summary>
/// The Campbell colour scheme - the Windows Terminal default dark theme - as packed 0xRRGGBB, one
/// entry per ANSI slot 0-15, and the one place a span's foreground is resolved. The Skia terminal
/// (Rendering.TerminalTheme) draws with these values.
/// </summary>
public static class CampbellPalette
{
    public static readonly IReadOnlyList<int> Rgb =
    [
        0x0C0C0C, 0xC50F1F, 0x13A10E, 0xC19C00, 0x0037DA, 0x881798, 0x3A96DD, 0xCCCCCC,
        0x767676, 0xE74856, 0x16C60C, 0xF9F1A5, 0x3B78FF, 0xB4009E, 0x61D6D6, 0xF2F2F2,
    ];

    /// <summary>Slot 0 (near black) - the pane's background.</summary>
    public const int BackgroundSlot = 0;

    /// <summary>Slot 7 (light grey) - what <see cref="AnsiColor.Default"/> resolves to.</summary>
    public const int DefaultForegroundSlot = 7;

    /// <summary>
    /// A span's packed foreground at the moment it is drawn. A span with a
    /// <see cref="TextStyle.Speech"/> part is the colour in <paramref name="chat"/> of the part
    /// <see cref="ChatColorizer.ColourPartOf"/> names - its own, or the channel's for a shout's or
    /// tell's emote spans - exactly, unless its own face is <see cref="ChatFace.Dim"/>, which draws
    /// <see cref="ChatColorizer.Dim"/> of it; nothing else touches it. Any other span is its
    /// palette slot with the classic "bold = bright" promotion of slots 0-7 to 8-15.
    /// </summary>
    public static int ForegroundRgb(TextStyle style, ChatColorizer.Palette chat)
    {
        if (chat.RgbOf(ChatColorizer.ColourPartOf(style)) is int rgb)
            return (chat.FaceOf(style) & ChatFace.Dim) != 0 ? ChatColorizer.Dim(rgb) : rgb & 0xFFFFFF;
        int fg = style.Foreground == AnsiColor.Default ? DefaultForegroundSlot : (int)style.Foreground;
        if (style.Bold && fg is >= 0 and < 8) fg += 8;
        return Rgb[fg is >= 0 and < 16 ? fg : DefaultForegroundSlot];
    }
}
