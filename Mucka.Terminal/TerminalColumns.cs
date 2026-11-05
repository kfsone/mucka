namespace Mucka.Terminal;

/// <summary>
/// How many columns the terminal has, which is the width <see cref="LineWrapper"/> wraps at and the
/// window size the transport advertises (NAWS). One formula for both: the connection starts at the
/// count the connect page works out, and the game page replaces it with its pane's.
/// </summary>
public static class TerminalColumns
{
    /// <summary>Advance width of one Cascadia Mono cell per pixel of font size: 1200/2048 em units
    /// (from the embedded TTF's hmtx/head tables - a true monospace, so every glyph shares this
    /// advance).</summary>
    public const double CharWidthPerFontPx = 1200.0 / 2048.0;

    /// <summary>The fewest columns the pane wraps at and the transport advertises.</summary>
    public const int Min = 20;

    /// <summary>What is used when there is no width to measure. Operator rule: 40, a sane width for a
    /// phone, never <see cref="Min"/>.</summary>
    public const int Unmeasured = 40;

    /// <summary>The columns a font fits across a width.</summary>
    public static int Displayable(double widthDp, int fontPx)
        => widthDp > 0 && fontPx > 0 ? (int)Math.Floor(widthDp / (fontPx * CharWidthPerFontPx)) : 0;

    /// <summary>The largest font, no bigger than <paramref name="maxFontPx"/> and no smaller than
    /// <paramref name="minFontPx"/>, that fits <paramref name="columns"/> across <paramref name="widthDp"/>.
    /// <paramref name="maxFontPx"/> when the width or the column count is not known.</summary>
    public static int FittingFontPx(double widthDp, int columns, int maxFontPx, int minFontPx)
    {
        if (widthDp <= 0 || columns <= 0)
            return maxFontPx;
        var fits = (int)Math.Floor(widthDp / (columns * CharWidthPerFontPx));
        return Math.Clamp(fits, minFontPx, maxFontPx);
    }

    /// <summary>
    /// The columns to use: all that fit, and never fewer than <see cref="Min"/>.
    /// <paramref name="displayableCols"/> of 0 means the width is not known yet, and
    /// <see cref="Unmeasured"/> stands in for it.
    /// </summary>
    public static int Effective(int displayableCols)
        => Math.Max(displayableCols > 0 ? displayableCols : Unmeasured, Min);
}
