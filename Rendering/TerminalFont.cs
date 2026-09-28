using SkiaSharp;

namespace Mucka.Rendering;

/// <summary>
/// Loads the embedded Cascadia Mono typefaces (regular and its italic) and computes fixed-cell
/// metrics for a given pixel size. Because the font is a true monospace, every glyph shares one
/// advance width, so the renderer can place text on an exact column grid (col * CellWidth) rather
/// than measuring each run. The italic file has the regular's advance, so an italic run occupies
/// the same cells; its glyphs lean past the right edge of their cell, which the renderer allows
/// for by painting backgrounds before any glyph.
/// </summary>
public sealed class TerminalFont : IDisposable
{
    // Match the LogicalNames given to the EmbeddedResources in Mucka.csproj.
    private const string ResourceName = "Mucka.CascadiaMono.ttf";
    private const string ItalicResourceName = "Mucka.CascadiaMonoItalic.ttf";

    public SKTypeface Typeface { get; }
    public SKTypeface ItalicTypeface { get; }
    public SKFont Font { get; }
    public SKFont ItalicFont { get; }

    /// <summary>Advance width of one character cell, in pixels.</summary>
    public float CellWidth { get; }

    /// <summary>Height of one line box, in pixels (font extent x line-height factor).</summary>
    public float CellHeight { get; }

    /// <summary>Y offset from a line box's top to the text baseline, in pixels.</summary>
    public float Baseline { get; }

    /// <summary>Stroke width that fattens glyphs to a synthetic bold (stroke-and-fill leaves
    /// advances untouched, so bold runs stay on the column grid).</summary>
    public float BoldStrokeWidth { get; }

    public TerminalFont(float sizePx, float lineHeightFactor = 1.30f)
    {
        Typeface = LoadTypeface(ResourceName);
        ItalicTypeface = LoadTypeface(ItalicResourceName);
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

        // Measure the advance over a run and divide - robust against per-glyph side bearings.
        CellWidth = Font.MeasureText("0000000000") / 10f;

        var m = Font.Metrics;                 // Ascent is negative, Descent positive.
        float extent = m.Descent - m.Ascent;  // natural glyph box height
        CellHeight = extent * lineHeightFactor;
        // Centre the glyph box within the (taller) line box and place the baseline.
        Baseline = (CellHeight - extent) / 2f - m.Ascent;
    }

    private static SKTypeface LoadTypeface(string resourceName)
    {
        var asm = typeof(TerminalFont).Assembly;
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded font resource '{resourceName}' not found - check the EmbeddedResource LogicalName in Mucka.csproj.");
        return SKTypeface.FromStream(stream)
            ?? throw new InvalidOperationException($"SKTypeface.FromStream returned null for '{resourceName}'.");
    }

    public void Dispose()
    {
        Font.Dispose();
        ItalicFont.Dispose();
        Typeface.Dispose();
        ItalicTypeface.Dispose();
    }
}
