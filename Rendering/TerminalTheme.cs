using MudSharp.Models;
using SkiaSharp;

namespace Mucka.Rendering;

/// <summary>
/// The single Campbell colour theme for the Skia terminal, mirroring AnsiPalette.cs and
/// the hex table in HtmlScrollback. Index 0–15 are the ANSI slots; AnsiColor.Default (-1)
/// resolves to slot 7 (light grey). The classic "bold = bright" rule promotes a
/// normal-intensity foreground (slots 0–7) to its bright variant (slots 8–15).
/// </summary>
public static class TerminalTheme
{
    public static readonly SKColor[] Palette =
    {
        SKColor.Parse("#0C0C0C"), SKColor.Parse("#C50F1F"), SKColor.Parse("#13A10E"), SKColor.Parse("#C19C00"),
        SKColor.Parse("#0037DA"), SKColor.Parse("#881798"), SKColor.Parse("#3A96DD"), SKColor.Parse("#CCCCCC"),
        SKColor.Parse("#767676"), SKColor.Parse("#E74856"), SKColor.Parse("#16C60C"), SKColor.Parse("#F9F1A5"),
        SKColor.Parse("#3B78FF"), SKColor.Parse("#B4009E"), SKColor.Parse("#61D6D6"), SKColor.Parse("#F2F2F2"),
    };

    public static readonly SKColor Background = SKColor.Parse("#0C0C0C");
    public static readonly SKColor DefaultForeground = Palette[7];

    /// <summary>Resolve a span's foreground colour, applying the bold→bright promotion.</summary>
    public static SKColor Foreground(TextStyle style)
    {
        int fg = style.Foreground == AnsiColor.Default ? 7 : (int)style.Foreground;
        if (style.Bold && fg is >= 0 and < 8) fg += 8;
        return Palette[fg is >= 0 and < 16 ? fg : 7];
    }

    /// <summary>Resolve a span's background colour, or null when it should use the page background.</summary>
    public static SKColor? SpanBackground(TextStyle style)
    {
        if (style.Background == AnsiColor.Default) return null;
        int bg = (int)style.Background;
        return bg is >= 0 and < 16 ? Palette[bg] : null;
    }
}
