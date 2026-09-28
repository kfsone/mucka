using MudSharp.Models;
using SkiaSharp;

namespace Mucka.Rendering;

/// <summary>
/// The single Campbell colour theme for the Skia terminal, built from
/// <see cref="CampbellPalette"/>, which owns the values and the foreground resolution.
/// Index 0-15 are the ANSI slots; AnsiColor.Default (-1) resolves to slot 7 (light grey).
/// A chat span is drawn in the current chat colour setting for its part.
/// </summary>
public static class TerminalTheme
{
    public static readonly SKColor[] Palette = CampbellPalette.Rgb.Select(ToSk).ToArray();

    public static readonly SKColor Background = Palette[CampbellPalette.BackgroundSlot];
    public static readonly SKColor DefaultForeground = Palette[CampbellPalette.DefaultForegroundSlot];

    /// <summary>Resolve a span's foreground colour against the current chat colours.</summary>
    public static SKColor Foreground(TextStyle style, ChatColorizer.Palette chat)
        => ToSk(CampbellPalette.ForegroundRgb(style, chat));

    /// <summary>Resolve a span's background colour, or null when it should use the page background.</summary>
    public static SKColor? SpanBackground(TextStyle style)
    {
        if (style.Background == AnsiColor.Default) return null;
        int bg = (int)style.Background;
        return bg is >= 0 and < 16 ? Palette[bg] : null;
    }

    private static SKColor ToSk(int rgb) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
