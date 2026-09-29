namespace Mucka.Terminal;

/// <summary>
/// How many columns the terminal has, which is the width the server is told (NAWS and <c>/T</c>) and
/// wraps at. One formula, so the width sent at login and the width the game page measures agree, and
/// the server is not told a second width once play has started.
/// </summary>
public static class TerminalColumns
{
    /// <summary>Advance width of one Cascadia Mono cell per pixel of font size: 1200/2048 em units
    /// (from the embedded TTF's hmtx/head tables - a true monospace, so every glyph shares this
    /// advance).</summary>
    public const double CharWidthPerFontPx = 1200.0 / 2048.0;

    /// <summary>The fewest columns the server is ever told.</summary>
    public const int Min = 20;

    /// <summary>The most columns the server is ever told.</summary>
    public const int Max = 160;

    /// <summary>What an auto profile is told when there is no width to measure. Operator rule: 40, a
    /// sane width for a phone, never <see cref="Min"/>.</summary>
    public const int Unmeasured = 40;

    /// <summary>The columns a font fits across a width.</summary>
    public static int Displayable(double widthDp, int fontPx)
        => widthDp > 0 && fontPx > 0 ? (int)Math.Floor(widthDp / (fontPx * CharWidthPerFontPx)) : 0;

    /// <summary>
    /// The columns to use: the profile's maximum, or all that fit when it is 0 (auto), and never more
    /// than fit. <paramref name="displayableCols"/> of 0 means the width is not known yet, and the
    /// profile's maximum - or <see cref="Unmeasured"/> for auto - stands in for it.
    /// </summary>
    public static int Effective(int maxColumns, int displayableCols)
    {
        if (displayableCols <= 0)
            return Math.Clamp(maxColumns > 0 ? maxColumns : Unmeasured, Min, Max);
        return Math.Clamp(maxColumns > 0 ? Math.Min(maxColumns, displayableCols) : displayableCols, Min, Max);
    }
}
