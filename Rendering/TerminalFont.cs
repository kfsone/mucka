using SkiaSharp;

namespace Mucka.Rendering;

/// <summary>
/// Loads the embedded Cascadia Mono typeface and computes fixed-cell metrics for a given
/// pixel size. Because the font is a true monospace, every glyph shares one advance width,
/// so the renderer can place text on an exact column grid (col * CellWidth) rather than
/// measuring each run.
/// </summary>
public sealed class TerminalFont : IDisposable
{
    // Matches the LogicalName given to the EmbeddedResource in Mucka.csproj.
    private const string ResourceName = "Mucka.CascadiaMono.ttf";

    public SKTypeface Typeface { get; }
    public SKTypeface ItalicTypeface { get; }
    public SKFont Font { get; }
    public SKFont ItalicFont { get; }

    /// <summary>Advance width of one character cell, in pixels.</summary>
    public float CellWidth { get; }

    /// <summary>Height of one line box, in pixels (font extent × line-height factor).</summary>
    public float CellHeight { get; }

    /// <summary>Y offset from a line box's top to the text baseline, in pixels.</summary>
    public float Baseline { get; }

    /// <summary>Stroke width that fattens glyphs to a synthetic bold (stroke-and-fill leaves
    /// advances untouched, so bold runs stay on the column grid).</summary>
    public float BoldStrokeWidth { get; }

    public TerminalFont(float sizePx, float lineHeightFactor = 1.30f)
    {
        Typeface = LoadTypeface();
        ItalicTypeface = SKTypeface.FromFamilyName(Typeface.FamilyName, SKFontStyle.Italic)
            ?? Typeface;
        Font = new SKFont(Typeface, sizePx)
        {
            // Match Windows Terminal's ClearType: LCD subpixel AA + subpixel positioning.
            // Skia's default greyscale AA bleeds glyph edges into the background, which reads
            // as a washed-out "filtered" version of the palette. LCD striping only suits
            // landscape desktop LCDs, so keep greyscale AA elsewhere.
            Edging = OperatingSystem.IsWindows()
                ? SKFontEdging.SubpixelAntialias
                : SKFontEdging.Antialias,
            Subpixel = true,
        };
        ItalicFont = new SKFont(ItalicTypeface, sizePx)
        {
            Edging = Font.Edging,
            Subpixel = Font.Subpixel,
        };
        BoldStrokeWidth = sizePx / 24f;

        // Measure the advance over a run and divide — robust against per-glyph side bearings.
        CellWidth = Font.MeasureText("0000000000") / 10f;

        var m = Font.Metrics;                 // Ascent is negative, Descent positive.
        float extent = m.Descent - m.Ascent;  // natural glyph box height
        CellHeight = extent * lineHeightFactor;
        // Centre the glyph box within the (taller) line box and place the baseline.
        Baseline = (CellHeight - extent) / 2f - m.Ascent;
    }

    private static SKTypeface LoadTypeface()
    {
        var asm = typeof(TerminalFont).Assembly;
        using var stream = asm.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded font resource '{ResourceName}' not found — check the EmbeddedResource LogicalName in Mucka.csproj.");
        return SKTypeface.FromStream(stream)
            ?? throw new InvalidOperationException($"SKTypeface.FromStream returned null for '{ResourceName}'.");
    }

    public void Dispose()
    {
        Font.Dispose();
        ItalicFont.Dispose();
        Typeface.Dispose();
        if (!ReferenceEquals(ItalicTypeface, Typeface))
            ItalicTypeface.Dispose();
    }
}
