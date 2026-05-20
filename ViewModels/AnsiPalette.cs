using Microsoft.Maui.Graphics;

namespace Mucka.ViewModels;

// Campbell color scheme — the Windows Terminal default dark theme.
public static class AnsiPalette
{
    private static readonly Color[] _colors =
    {
        Color.FromArgb("#0C0C0C"),  //  0 Black
        Color.FromArgb("#C50F1F"),  //  1 Dark Red
        Color.FromArgb("#13A10E"),  //  2 Dark Green
        Color.FromArgb("#C19C00"),  //  3 Dark Yellow
        Color.FromArgb("#0037DA"),  //  4 Dark Blue
        Color.FromArgb("#881798"),  //  5 Dark Magenta
        Color.FromArgb("#3A96DD"),  //  6 Dark Cyan
        Color.FromArgb("#CCCCCC"),  //  7 Light Gray  (default fg)
        Color.FromArgb("#767676"),  //  8 Dark Gray   (bright black)
        Color.FromArgb("#E74856"),  //  9 Bright Red
        Color.FromArgb("#16C60C"),  // 10 Bright Green
        Color.FromArgb("#F9F1A5"),  // 11 Bright Yellow
        Color.FromArgb("#3B78FF"),  // 12 Bright Blue
        Color.FromArgb("#B4009E"),  // 13 Bright Magenta
        Color.FromArgb("#61D6D6"),  // 14 Bright Cyan
        Color.FromArgb("#F2F2F2"),  // 15 Bright White
    };

    public static readonly Color PageBg    = Color.FromArgb("#0C0C0C");
    public static readonly Color DefaultFg = _colors[7];

    public static Color GetFg(byte index) => index < 16 ? _colors[index] : DefaultFg;
    public static Color GetBg(byte index) => index < 16 ? _colors[index] : Colors.Transparent;
}
