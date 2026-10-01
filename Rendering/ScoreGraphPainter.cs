using System.Globalization;
using Mucka.Combat;
using SkiaSharp;

namespace Mucka.Rendering;

/// <summary>What the pointer is on: a reading of series <see cref="Series"/>, a strip mark, a climb or
/// drop, or a callout. A callout that stands for one change also names that change, and shows its card.</summary>
public readonly record struct ScoreHover(int Series, PlotReading? Reading, PlotMark? Mark, PlotMove? Move = null, int Stair = 0,
    PlotTally? Tally = null);

/// <summary>
/// Paints a <see cref="ScoreGraphPlot"/> for <see cref="ScoreGraphView"/>: every coordinate comes from
/// the plot, so the arithmetic is tested in Mucka.Util.Tests and this class chooses ink, type and
/// where its text goes. SkiaSharp only - no MAUI - so it can also render to an image.
///
/// <para>Drawing order, back to front: a hovered mark's span wash; guides (squeezed gaps, grid, day
/// lines, axis, run and switch marks, session bands); data (bucket bars, area fill, line, drops and
/// their markers); wipe skulls; labels (the "as of" stamp, live-value tags) and callouts; then the
/// hover card. Guides are quiet so the data reads first.</para>
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The paints and fonts live as long as the view that owns the painter, and nothing disposes a MAUI view.")]
public sealed class ScoreGraphPainter
{
    /// <summary>One colour per selected character, in selection order. The panel's summary table uses
    /// the same list, so a name there is the colour of its line.</summary>
    public static readonly string[] SeriesHex = ["#58a6ff", "#3fb950", "#d2a8ff", "#39c5cf", "#f0883e", "#f778ba"];
    private static readonly SKColor[] SeriesColors = [.. SeriesHex.Select(SKColor.Parse)];

    private static readonly SKColor Panel       = SKColor.Parse("#101618");
    private static readonly SKColor Loss        = SKColor.Parse("#ff6b6b");
    private static readonly SKColor LossInk     = SKColor.Parse("#ffc1c1");
    private static readonly SKColor LossPill    = SKColor.Parse("#3a1417");
    private static readonly SKColor Run         = SKColor.Parse("#e3b341");
    private static readonly SKColor AxisInk     = SKColor.Parse("#7d8590");
    private static readonly SKColor TooltipFill = SKColor.Parse("#161b22").WithAlpha(0xf2);
    private static readonly SKColor TooltipEdge = SKColor.Parse("#3d4f5c");
    private static readonly SKColor Gain        = SKColor.Parse("#3fb950");
    private static readonly SKColor GainInk     = SKColor.Parse("#b7f0c1");
    private static readonly SKColor GainPill    = SKColor.Parse("#10261a");
    private static readonly SKColor Mixed       = SKColor.Parse("#d29922");
    private static readonly SKColor MixedInk    = SKColor.Parse("#f2d58a");
    private static readonly SKColor MixedPill   = SKColor.Parse("#2e2410");

    private static readonly SKTypeface Regular = Face(SKFontStyle.Normal);
    private static readonly SKTypeface Bold = Face(SKFontStyle.Bold);

    private readonly SKPaint _stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
    private readonly SKPaint _fill = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKFont _axisFont = Font(Regular, 10.5f);
    private readonly SKFont _pillFont = Font(Bold, 10f);
    private readonly SKFont _tipFont = Font(Regular, 11f);
    private readonly SKFont _tipBold = Font(Bold, 11f);
    // Segoe UI Symbol draws the skull as a plain glyph that takes the paint's colour; failing that,
    // whichever installed face has the character.
    private readonly SKFont _skullFont = Font(
        SKFontManager.Default.MatchFamily("Segoe UI Symbol")
            ?? SKFontManager.Default.MatchCharacter(Mucka.Core.Glyph.Skull[0])
            ?? SKTypeface.Default, 15f);
    private readonly SKPathEffect _dash = SKPathEffect.CreateDash([2f, 4f], 0);
    private readonly SKPathEffect _longDash = SKPathEffect.CreateDash([4f, 4f], 0);

    private float _scale = 1f;

    private static SKTypeface Face(SKFontStyle style)
        => SKFontManager.Default.MatchFamily("Segoe UI", style)
            ?? SKFontManager.Default.MatchFamily("sans-serif", style)
            ?? SKTypeface.Default;

    private static SKFont Font(SKTypeface face, float size)
        => new(face, size) { Edging = SKFontEdging.Antialias, Subpixel = true, Hinting = SKFontHinting.Slight };

    /// <param name="scale">Device pixels per plot unit; lines are snapped to device pixels with it.</param>
    /// <param name="selection">The stretch a drag across the plot is selecting, if one is under way.</param>
    public void Paint(SKCanvas canvas, float scale, ScoreGraphPlot plot, ScoreGraphScene scene, ScoreHover? hover,
        (double X0, double X1)? selection = null)
    {
        canvas.Save();
        _scale = scale;
        canvas.Scale(scale);

        if (selection is { } band)
        {
            _fill.Color = SKColors.White.WithAlpha(0x14);
            canvas.DrawRect((float)band.X0, (float)plot.Top, (float)(band.X1 - band.X0), (float)(plot.Bottom - plot.Top), _fill);
            _stroke.Color = SKColors.White.WithAlpha(0x60);
            _stroke.StrokeWidth = Hairline;
            canvas.DrawLine(Snap(band.X0), (float)plot.Top, Snap(band.X0), (float)plot.Bottom, _stroke);
            canvas.DrawLine(Snap(band.X1), (float)plot.Top, Snap(band.X1), (float)plot.Bottom, _stroke);
        }

        if (hover?.Mark is { } mark)
            DrawMarkSpan(canvas, plot, mark);
        DrawGuides(canvas, plot);
        DrawData(canvas, plot);
        DrawDeaths(canvas, plot);
        var taken = new List<SKRect>();
        DrawAsOf(canvas, plot, scene, taken);
        DrawLiveTags(canvas, plot, taken);
        DrawTallies(canvas, plot, taken, hover);
        if (hover?.Reading is { } reading)
            DrawHover(canvas, plot, scene, (hover.Value.Series, reading));
        else if (hover?.Mark is { } m)
            DrawMarkCard(canvas, plot, scene, m);
        else if (hover?.Move is { } move)
            DrawMoveCard(canvas, plot, scene, move, hover.Value.Stair);
        else if (hover?.Tally is { } tally)
            DrawTallyCard(canvas, plot, scene, tally);

        canvas.Restore();
    }

    /// <summary>The span a hovered mark covers, washed faintly behind everything, with the mark itself
    /// ringed.</summary>
    private void DrawMarkSpan(SKCanvas canvas, ScoreGraphPlot plot, PlotMark mark)
    {
        var ink = mark.Kind == PlotMarkKind.Run ? Run : SKColors.White;
        _fill.Color = ink.WithAlpha(0x10);
        canvas.DrawRect((float)mark.X, (float)plot.Top, Math.Max(2f, (float)(mark.EndX - mark.X)), (float)(plot.Bottom - plot.Top), _fill);
        _stroke.Color = ink.WithAlpha(0xd0);
        _stroke.StrokeWidth = 1.25f;
        canvas.DrawCircle(Snap(mark.X), (float)plot.Top - 7, 5f, _stroke);
    }

    /// <summary>What a strip mark stands for, when it began and ended, and what the score did across
    /// it: per character for a Mucka run, for the character switched to for a switch.</summary>
    private void DrawMarkCard(SKCanvas canvas, ScoreGraphPlot plot, ScoreGraphScene scene, PlotMark mark)
    {
        var rows = new List<CardRow>();
        string title;
        SKColor titleInk;
        if (mark.Kind == PlotMarkKind.Run)
        {
            title = "Mucka session";
            titleInk = Run;
            for (var s = 0; s < scene.Series.Count; s++)
                if (plot.NetChange(s, mark.Span) is { } change)
                    rows.Add(ChangeRow(scene.Series[s].Label, SeriesColors[s % SeriesColors.Length], change));
        }
        else
        {
            title = "Switched to " + scene.Series[mark.Series].Label + " from " + mark.From;
            titleInk = SeriesColors[mark.Series % SeriesColors.Length];
            if (plot.NetChange(mark.Series, mark.Span) is { } change)
                rows.Add(ChangeRow("this session", AxisInk, change));
        }
        if (rows.Count == 0)
            rows.Add(new CardRow("no score recorded", AxisInk, string.Empty, string.Empty, AxisInk));

        DrawCard(canvas, plot, Snap(mark.X), title, titleInk, boldTitle: true, Span(mark.Span), rows);
    }

    /// <summary>When the graph was read, top-right inside the plot, so a line held flat to the right
    /// edge says how far "now" is.</summary>
    private void DrawAsOf(SKCanvas canvas, ScoreGraphPlot plot, ScoreGraphScene scene, List<SKRect> taken)
    {
        var when = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(scene.EndMs), TimeZoneInfo.Local);
        var text = "as of " + when.ToString("ddd d MMM  HH:mm", CultureInfo.InvariantCulture);
        var w = _axisFont.MeasureText(text);
        float right = (float)plot.Right - 4, baseline = (float)plot.Top + 12;
        _fill.Color = AxisInk.WithAlpha(0xb0);
        canvas.DrawText(text, right, baseline, SKTextAlign.Right, _axisFont, _fill);
        taken.Add(new SKRect(right - w, baseline - 10, right, baseline + 3));
    }

    /// <summary>A wiped persona: a skull and crossbones above the line where it stood when the wipe
    /// was recorded, haloed in the panel colour so it reads over gridlines.</summary>
    private void DrawDeaths(SKCanvas canvas, ScoreGraphPlot plot)
    {
        foreach (var d in plot.Deaths)
        {
            var x = (float)d.X;
            var baseline = Math.Clamp((float)d.Y - 6, (float)plot.Top + 14, (float)plot.Bottom - 2);
            _stroke.Color = Panel;
            _stroke.StrokeWidth = 3f;
            canvas.DrawText(Mucka.Core.Glyph.Skull, x, baseline, SKTextAlign.Center, _skullFont, _stroke);
            _fill.Color = SKColors.White;
            canvas.DrawText(Mucka.Core.Glyph.Skull, x, baseline, SKTextAlign.Center, _skullFont, _fill);
        }
    }

    /// <summary>A climb or drop: its extent bracketed beside it, each step dotted, and a card with its
    /// total, its step count and span, and the score it ran from and to.</summary>
    private void DrawMoveCard(SKCanvas canvas, ScoreGraphPlot plot, ScoreGraphScene scene, PlotMove move, int stair)
    {
        var ink = move.Change >= 0 ? Gain : Loss;
        float x0 = (float)move.X0, x1 = (float)move.X1, yFrom = (float)move.YFrom, yTo = (float)move.YTo;

        // Bracket to the right of the move: a spine and two feet at its start and end heights.
        var bx = x1 + 7;
        _stroke.Color = ink.WithAlpha(0xd0);
        _stroke.StrokeWidth = 1.5f;
        canvas.DrawLine(bx, yFrom, bx, yTo, _stroke);
        canvas.DrawLine(bx - 4, yFrom, bx, yFrom, _stroke);
        canvas.DrawLine(bx - 4, yTo, bx, yTo, _stroke);
        _stroke.Color = SKColors.White.WithAlpha(0x50);
        _stroke.StrokeWidth = Hairline;
        _stroke.PathEffect = _dash;
        canvas.DrawLine(x0, yFrom, bx - 4, yFrom, _stroke);
        _stroke.PathEffect = null;

        foreach (var dot in move.Stairs)
        {
            _fill.Color = Panel;
            canvas.DrawCircle((float)dot.X, (float)dot.Y, 3.5f, _fill);
            _fill.Color = ink;
            canvas.DrawCircle((float)dot.X, (float)dot.Y, 2.25f, _fill);
        }
        // The step under the pointer, ringed; the card itemises it.
        var at = move.Stairs[Math.Clamp(stair, 0, move.Stairs.Count - 1)];
        _stroke.Color = SKColors.White;
        _stroke.StrokeWidth = 1.25f;
        canvas.DrawCircle((float)at.X, (float)at.Y, 5f, _stroke);

        // Every step's size beside the climb, so the whole staircase reads at once; the card goes on
        // the other side where there is room for it there. A single step is its own total, already
        // in the card's title.
        var labelsRight = move.Stairs.Count > 1 && DrawStairLabels(canvas, plot, move, ink, bx + 5);

        var inv = CultureInfo.InvariantCulture;
        var start = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(move.StartMs), TimeZoneInfo.Local);
        var end = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(move.EndMs), TimeZoneInfo.Local);
        var when = move.Steps == 1
            ? start.ToString("ddd d MMM  HH:mm:ss", inv)
            : start.ToString("ddd d MMM  HH:mm:ss", inv) + " - " + end.ToString("HH:mm:ss", inv);
        var title = (move.Change >= 0 ? "Climb +" : "Drop -") + Math.Abs(move.Change).ToString("N0", CultureInfo.CurrentCulture);
        var subtitle = (move.Steps == 1 ? "1 step" : move.Steps.ToString(inv) + " steps") + "   " + when;
        var rows = new List<CardRow>
        {
            new("from", AxisInk, move.From.ToString("N0", CultureInfo.CurrentCulture), string.Empty, AxisInk),
            new("to", AxisInk, move.To.ToString("N0", CultureInfo.CurrentCulture), string.Empty, AxisInk),
        };
        if (scene.Series.Count > 1)
            rows.Insert(0, new CardRow(scene.Series[move.Series].Label, SeriesColors[move.Series % SeriesColors.Length], string.Empty, string.Empty, AxisInk));

        // The ringed step: when, its size, and - when the game printed it as several lines in one
        // frame - each line.
        if (move.Steps > 1 || at.Parts is not null)
        {
            var stepWhen = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(at.Ms), TimeZoneInfo.Local);
            rows.Add(ChangeRow("step " + stepWhen.ToString("HH:mm:ss", inv), AxisInk, at.Change));
            if (at.Parts is { } parts)
                rows.Add(new CardRow(parts.Count.ToString(inv) + " lines", AxisInk, string.Empty, PartsText(parts), AxisInk));
        }
        DrawCard(canvas, plot, labelsRight ? x0 - 4 : bx, title, ink, boldTitle: true, subtitle, rows, preferLeft: labelsRight);
    }

    /// <summary>
    /// Each step's size in a small pill level with its node, in a column beside the move. Pills that
    /// would overlap are nudged up or down to the nearest free spot, with a leader line back to the
    /// node; the largest steps are placed first, so where there is not room for all of them, the
    /// small ones are the ones left out. Returns true when the column went right of the move.
    /// </summary>
    private bool DrawStairLabels(SKCanvas canvas, ScoreGraphPlot plot, PlotMove move, SKColor ink, float rightColumn)
    {
        const float Height = 14f, Gap = 2f;
        // Only the largest steps are considered: a column the plot's height holds far fewer pills
        // than this, and the rest would be measured and tried on every hover repaint for nothing.
        var chosen = Enumerable.Range(0, move.Stairs.Count)
            .OrderByDescending(i => Math.Abs(move.Stairs[i].Change))
            .Take(MaxStairLabels)
            .ToList();
        var texts = chosen.ToDictionary(i => i, i =>
            (move.Stairs[i].Change >= 0 ? "+" : "-") + Math.Abs(move.Stairs[i].Change).ToString("N0", CultureInfo.CurrentCulture));
        var w = texts.Count == 0 ? 0 : texts.Values.Max(t => _pillFont.MeasureText(t)) + 10;
        var canvasRight = (float)(plot.Right + ScoreGraphPlot.RightMargin);
        var right = rightColumn + w <= canvasRight || (float)move.X0 - 12 - w < 0;
        var left = right ? Math.Min(rightColumn, canvasRight - w) : (float)move.X0 - 12 - w;

        var placed = new List<(float Top, float Bottom)>();
        float lo = (float)plot.Top, hi = (float)plot.Bottom;
        foreach (var i in chosen)
        {
            var want = (float)move.Stairs[i].Y - Height / 2;
            float? top = null;
            // Nearest free slot, alternating below and above, at most a few rows away from the node.
            for (var k = 0; k <= 8 && top is null; k++)
            {
                foreach (var t in k == 0 ? [want] : new[] { want + k * (Height + Gap), want - k * (Height + Gap) })
                {
                    if (t < lo || t + Height > hi) continue;
                    if (placed.Any(p => t < p.Bottom + Gap && t + Height > p.Top - Gap)) continue;
                    top = t;
                    break;
                }
            }
            if (top is not { } y) continue;
            placed.Add((y, y + Height));

            var dot = move.Stairs[i];
            var rect = new SKRect(left, y, left + w, y + Height);
            if (Math.Abs(rect.MidY - (float)dot.Y) > 1.5f)
            {
                _stroke.Color = ink.WithAlpha(0x60);
                _stroke.StrokeWidth = Hairline;
                canvas.DrawLine((float)dot.X, (float)dot.Y, right ? rect.Left : rect.Right, rect.MidY, _stroke);
            }
            _fill.Color = TooltipFill;
            canvas.DrawRoundRect(rect, 3, 3, _fill);
            _stroke.Color = ink.WithAlpha(0x70);
            _stroke.StrokeWidth = Hairline;
            canvas.DrawRoundRect(rect, 3, 3, _stroke);
            _fill.Color = ink;
            canvas.DrawText(texts[i], rect.MidX, rect.MidY + 3.5f, SKTextAlign.Center, _pillFont, _fill);
        }
        return right;
    }

    private const int MaxStairLabels = 40;

    /// <summary>A frame's lines as signed figures, cut short past <see cref="MaxParts"/>.</summary>
    private static string PartsText(IReadOnlyList<long> parts)
    {
        var shown = parts.Take(MaxParts).Select(p => (p > 0 ? "+" : "-") + Math.Abs(p).ToString("N0", CultureInfo.CurrentCulture));
        var text = string.Join(" ", shown);
        return parts.Count > MaxParts
            ? text + " +" + (parts.Count - MaxParts).ToString(CultureInfo.InvariantCulture) + " more"
            : text;
    }

    private const int MaxParts = 8;

    private static CardRow ChangeRow(string label, SKColor ink, long change)
        => new(label, ink, string.Empty,
            (change > 0 ? "+" : change < 0 ? "-" : "") + Math.Abs(change).ToString("N0", CultureInfo.CurrentCulture),
            change > 0 ? Gain : change < 0 ? Loss : AxisInk);

    private static string Span(TimeSpanMs span)
    {
        var start = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(span.StartMs), TimeZoneInfo.Local);
        var end = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(span.EndMs), TimeZoneInfo.Local);
        var length = end - start;
        var inv = CultureInfo.InvariantCulture;
        var duration = length.TotalHours >= 1
            ? ((int)length.TotalHours).ToString(inv) + "h " + length.Minutes.ToString("00", inv) + "m"
            : Math.Max(0, (int)length.TotalMinutes).ToString(inv) + "m";
        var endText = end.Date == start.Date
            ? end.ToString("HH:mm", CultureInfo.InvariantCulture)
            : end.ToString("ddd d HH:mm", CultureInfo.InvariantCulture);
        return start.ToString("ddd d MMM  HH:mm", CultureInfo.InvariantCulture) + " - " + endText + "  (" + duration + ")";
    }

    /// <summary>A coordinate on the centre of a device pixel, so a one-pixel line is one pixel wide.</summary>
    private float Snap(double v) => (MathF.Round((float)v * _scale - 0.5f) + 0.5f) / _scale;

    private float Hairline => 1f / _scale;

    private void DrawGuides(SKCanvas canvas, ScoreGraphPlot plot)
    {
        float left = (float)plot.Left, top = (float)plot.Top, right = (float)plot.Right, bottom = (float)plot.Bottom;

        // Squeezed gaps: a faint darker seam; the axis baseline below breaks across each one.
        _fill.Color = SKColors.Black.WithAlpha(0x38);
        foreach (var (x0, x1) in plot.Gaps)
            canvas.DrawRect((float)x0, top, (float)(x1 - x0), bottom - top, _fill);

        // Horizontal grid, dotted and faint, with its labels.
        _stroke.StrokeWidth = Hairline;
        _stroke.Color = SKColors.White.WithAlpha(0x1a);
        _stroke.PathEffect = _dash;
        _fill.Color = AxisInk;
        foreach (var (y, value) in plot.YTicks)
        {
            var sy = Snap(y);
            canvas.DrawLine(left, sy, right, sy, _stroke);
            canvas.DrawText(value.ToString("N0", CultureInfo.CurrentCulture), left - 8, sy + 3.5f,
                SKTextAlign.Right, _axisFont, _fill);
        }

        // Day lines at each labelled tick, fainter still.
        _stroke.Color = SKColors.White.WithAlpha(0x10);
        foreach (var (x, label) in plot.XTicks)
        {
            var sx = Snap(x);
            canvas.DrawLine(sx, top, sx, bottom, _stroke);
            canvas.DrawText(label, sx, bottom + 16, SKTextAlign.Center, _axisFont, _fill);
        }
        _stroke.PathEffect = null;

        // Axis baseline, broken across squeezed gaps so the cut in time shows on the axis itself.
        _stroke.Color = SKColors.White.WithAlpha(0x30);
        var from = left;
        foreach (var (x0, x1) in plot.Gaps)
        {
            if (x0 > from) canvas.DrawLine(from, Snap(bottom), (float)x0 - 1, Snap(bottom), _stroke);
            from = (float)x1 + 1;
        }
        if (right > from) canvas.DrawLine(from, Snap(bottom), right, Snap(bottom), _stroke);

        // The mark strip above the plot: Mucka runs as small notches with a barely-there line, so
        // twenty of them in a day read as marks rather than a fence; character switches as dots with
        // a faint dashed line.
        var strip = top - 7;
        foreach (var x in plot.Marks.Where(m => m.Kind == PlotMarkKind.Run).Select(m => m.X))
        {
            var sx = Snap(x);
            _stroke.Color = Run.WithAlpha(0x16);
            canvas.DrawLine(sx, top, sx, bottom, _stroke);
            _fill.Color = Run.WithAlpha(0xc0);
            using var notch = new SKPath();
            notch.MoveTo(sx - 3, strip - 3);
            notch.LineTo(sx + 3, strip - 3);
            notch.LineTo(sx, strip + 2);
            notch.Close();
            canvas.DrawPath(notch, _fill);
        }
        _stroke.Color = SKColors.White.WithAlpha(0x40);
        _stroke.PathEffect = _longDash;
        _fill.Color = SKColors.White.WithAlpha(0xd8);
        foreach (var x in plot.Marks.Where(m => m.Kind == PlotMarkKind.Switch).Select(m => m.X))
        {
            var sx = Snap(x);
            canvas.DrawLine(sx, top, sx, bottom, _stroke);
            canvas.DrawCircle(sx, strip, 2.25f, _fill);
        }
        _stroke.PathEffect = null;

        // Session extents, as a thin rounded band under the axis.
        _fill.Color = SKColors.White.WithAlpha(0x40);
        foreach (var (x0, x1) in plot.SessionBands)
            canvas.DrawRoundRect(new SKRect((float)x0, bottom + 2, Math.Max((float)x0 + 2, (float)x1), bottom + 4.5f), 1.25f, 1.25f, _fill);
    }

    private void DrawData(SKCanvas canvas, ScoreGraphPlot plot)
    {
        float top = (float)plot.Top, bottom = (float)plot.Bottom, right = (float)plot.Right;
        var single = plot.Lines.Count == 1;

        // Bucket ranges as slim candles centred in their bucket: a filled body and a crisp edge.
        foreach (var bar in plot.Bars)
        {
            var colour = SeriesColors[bar.Series % SeriesColors.Length];
            var rect = Sk(ScoreGraphPlot.CandleBody(bar));
            _fill.Color = colour.WithAlpha(0x40);
            canvas.DrawRoundRect(rect, 1.5f, 1.5f, _fill);
            _stroke.Color = colour.WithAlpha(0x90);
            _stroke.StrokeWidth = Hairline;
            canvas.DrawRoundRect(rect, 1.5f, 1.5f, _stroke);
        }

        for (var s = 0; s < plot.Lines.Count; s++)
        {
            var line = plot.Lines[s];
            if (line.Count == 0) continue;
            var colour = SeriesColors[s % SeriesColors.Length];

            using var path = new SKPath();
            path.MoveTo((float)line[0].X, (float)line[0].Y);
            for (var i = 1; i < line.Count; i++)
                path.LineTo((float)line[i].X, (float)line[i].Y);

            // Area under the line, fading to nothing at the axis. Fainter when lines share the plot.
            using (var area = new SKPath(path))
            {
                area.LineTo(Math.Max(right, (float)line[^1].X), bottom);
                area.LineTo((float)line[0].X, bottom);
                area.Close();
                using var shader = SKShader.CreateLinearGradient(new SKPoint(0, top), new SKPoint(0, bottom),
                    [colour.WithAlpha(single ? (byte)0x5c : (byte)0x20), colour.WithAlpha(0)], SKShaderTileMode.Clamp);
                _fill.Shader = shader;
                canvas.DrawPath(area, _fill);
                _fill.Shader = null;
            }

            // A soft wide stroke under a sharp narrow one.
            _stroke.Color = colour.WithAlpha(0x30);
            _stroke.StrokeWidth = 4.5f;
            canvas.DrawPath(path, _stroke);
            _stroke.Color = colour;
            _stroke.StrokeWidth = 1.75f;
            canvas.DrawPath(path, _stroke);
        }

        // Drops over the lines, then their markers: a downward chevron at the foot, ringed in the
        // panel colour so it separates from the line it sits on.
        _stroke.Color = Loss;
        _stroke.StrokeWidth = 2f;
        foreach (var loss in plot.Losses)
            if (loss.YAfter > loss.YBefore)
                canvas.DrawLine((float)loss.X, (float)loss.YBefore, (float)loss.X, (float)loss.YAfter, _stroke);
        foreach (var loss in plot.Losses)
        {
            if (loss.Radius <= 0) continue;
            var box = Sk(ScoreGraphPlot.ChevronRect(loss));
            using var tri = new SKPath();
            tri.MoveTo(box.Left, box.Top);
            tri.LineTo(box.Right, box.Top);
            tri.LineTo(box.MidX, box.Bottom);
            tri.Close();
            _fill.Color = Loss;
            canvas.DrawPath(tri, _fill);
            _stroke.Color = Panel;
            _stroke.StrokeWidth = 1.25f;
            canvas.DrawPath(tri, _stroke);
        }
    }

    /// <summary>Each line's last score in a pill right of the plot, in the line's colour, with a dot
    /// where the line last moved. Pills are pushed apart vertically when lines end close together.</summary>
    private void DrawLiveTags(SKCanvas canvas, ScoreGraphPlot plot, List<SKRect> taken)
    {
        var tags = new List<(int Series, PlotReading Last)>();
        for (var s = 0; s < plot.Readings.Count; s++)
            if (plot.Readings[s].Count > 0)
                tags.Add((s, plot.Readings[s][^1]));

        const float Height = 16f;
        var placed = new List<(int Series, PlotReading Last, float Y)>();
        foreach (var (s, last) in tags.OrderBy(t => t.Last.Y))
        {
            var y = Math.Clamp((float)last.Y, (float)plot.Top + Height / 2, (float)plot.Bottom - Height / 2);
            if (placed.Count > 0 && y < placed[^1].Y + Height + 2)
                y = placed[^1].Y + Height + 2;
            placed.Add((s, last, y));
        }

        foreach (var (s, last, y) in placed)
        {
            var colour = SeriesColors[s % SeriesColors.Length];
            _fill.Color = Panel;
            canvas.DrawCircle((float)last.X, (float)last.Y, 4.5f, _fill);
            _fill.Color = colour;
            canvas.DrawCircle((float)last.X, (float)last.Y, 3f, _fill);

            var text = last.Total.ToString("N0", CultureInfo.CurrentCulture);
            var w = _pillFont.MeasureText(text) + 10;
            var rect = new SKRect((float)plot.Right + 6, y - Height / 2, (float)plot.Right + 6 + w, y + Height / 2);
            _fill.Color = colour;
            canvas.DrawRoundRect(rect, 4, 4, _fill);
            _fill.Color = Panel;
            canvas.DrawText(text, rect.MidX, rect.MidY + 3.5f, SKTextAlign.Center, _pillFont, _fill);
            taken.Add(rect);
        }
    }

    /// <summary>The callouts, laid out by <see cref="ScoreGraphPlot.PlaceTallies"/> once per plot and kept
    /// for every repaint and for <see cref="TallyAt"/>: splines first, then bulbs and pills over them.
    /// Coloured by what went into them: red only losses, green only gains, amber both. The one the
    /// hover is on has its splines solid white and its edge ringed.</summary>
    private void DrawTallies(SKCanvas canvas, ScoreGraphPlot plot, List<SKRect> taken, ScoreHover? hover)
    {
        if (!ReferenceEquals(_tallyPlot, plot))
        {
            var bounds = new PlotRect(plot.Left, plot.Top, plot.Right, plot.Bottom);
            var reserved = taken.Select(r => new PlotRect(r.Left, r.Top, r.Right, r.Bottom));
            _tallies = ScoreGraphPlot.PlaceTallies(plot.Tallies, bounds, plot.TallyObstacles,
                text => _pillFont.MeasureText(text) + 10, reserved, plot.LineRects);
            _tallyPlot = plot;
        }

        bool Lit(PlotTally tally) => hover is { } h && (ReferenceEquals(h.Tally, tally)
            || h.Move is { } m && tally.Moves.Contains(m)
            || h.Reading is { } r && tally.Buckets.Contains(r));

        // Splines under every callout, so a line never runs over a figure; the lit one last, on top.
        foreach (var lit in new[] { false, true })
            foreach (var (tally, _, _, splines) in _tallies.Where(c => Lit(c.Tally) == lit))
            {
                _stroke.Color = lit ? SKColors.White : SplineInk;
                _stroke.StrokeWidth = lit ? 2f : 1f;
                _stroke.PathEffect = lit ? null : _dash;
                var toward = tally.Kind == TallyKind.Gain ? -1f : 1f;
                foreach (var (from, to) in splines)
                {
                    using var path = Spline(from, to, toward);
                    canvas.DrawPath(path, _stroke);
                }
            }
        _stroke.PathEffect = null;

        foreach (var (tally, box, expanded, _) in _tallies)
        {
            var rect = Sk(box);
            var (fill, edge, ink) = Ink(tally.Kind);
            var lit = Lit(tally);
            // A gathered callout is a size larger and edged firmer, so it reads as standing for several.
            _stroke.Color = lit ? SKColors.White : edge.WithAlpha(tally.Merged ? (byte)0xe0 : (byte)0xa0);
            _stroke.StrokeWidth = lit || tally.Merged ? 1.25f : Hairline;
            _fill.Color = fill;
            if (expanded)
            {
                canvas.DrawRoundRect(rect, 4, 4, _fill);
                canvas.DrawRoundRect(rect, 4, 4, _stroke);
                _fill.Color = ink;
                canvas.DrawText(tally.Text, rect.MidX, rect.MidY + 3.5f, SKTextAlign.Center, _pillFont, _fill);
                continue;
            }
            // A bulb: a circle with a chevron pointing the way the score went, flat when it came
            // out even.
            var r = rect.Width / 2;
            canvas.DrawCircle(rect.MidX, rect.MidY, r, _fill);
            canvas.DrawCircle(rect.MidX, rect.MidY, r, _stroke);
            var down = Math.Sign(tally.Net) * -1f;
            var c = r * 0.45f;
            using var chevron = new SKPath();
            chevron.MoveTo(rect.MidX - c, rect.MidY - c * 0.5f * down);
            chevron.LineTo(rect.MidX, rect.MidY + c * 0.5f * down);
            chevron.LineTo(rect.MidX + c, rect.MidY - c * 0.5f * down);
            _stroke.Color = ink;
            _stroke.StrokeWidth = 1.5f;
            canvas.DrawPath(chevron, _stroke);
        }
    }

    /// <summary>A callout's spline: leaving its anchor the way the score went (down for a drop, up
    /// for a climb) and arriving at the callout, so it reads as hanging off the change.</summary>
    private static SKPath Spline(PlotPoint from, PlotPoint to, float toward)
    {
        float ax = (float)from.X, ay = (float)from.Y, bx = (float)to.X, by = (float)to.Y;
        var length = MathF.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
        var path = new SKPath();
        path.MoveTo(ax, ay);
        path.CubicTo(ax, ay + toward * 0.4f * length, bx + (ax - bx) * 0.4f, by + (ay - by) * 0.4f, bx, by);
        return path;
    }

    /// <summary>A resting spline: a light grey no data on the graph is drawn in, so it reads as a link.</summary>
    private static readonly SKColor SplineInk = SKColor.Parse("#c9d1d9").WithAlpha(0xcc);

    private static (SKColor Fill, SKColor Edge, SKColor Ink) Ink(TallyKind kind) => kind switch
    {
        TallyKind.Loss => (LossPill, Loss, LossInk),
        TallyKind.Gain => (GainPill, Gain, GainInk),
        _ => (MixedPill, Mixed, MixedInk),
    };

    private ScoreGraphPlot? _tallyPlot;
    private List<PlacedTally> _tallies = [];

    /// <summary>The callout under the point in the last paint, if any; a bulb is small, so it is
    /// reached a little way off.</summary>
    public PlotTally? TallyAt(double x, double y)
    {
        foreach (var (tally, box, expanded, _) in _tallies)
        {
            var reach = expanded ? 0 : BulbReach;
            if (x >= box.Left - reach && x <= box.Right + reach && y >= box.Top - reach && y <= box.Bottom + reach)
                return tally;
        }
        return null;
    }

    private const double BulbReach = 4;

    private static SKRect Sk(PlotRect r) => new((float)r.Left, (float)r.Top, (float)r.Right, (float)r.Bottom);

    /// <summary>A callout that stands for several changes: each of them with its time, then the
    /// totals each way when both went into it.</summary>
    private void DrawTallyCard(SKCanvas canvas, ScoreGraphPlot plot, ScoreGraphScene scene, PlotTally tally)
    {
        var inv = CultureInfo.InvariantCulture;
        var members = tally.Moves.Select(m => (m.StartMs, m.EndMs, m.Change, Lost: Math.Max(0, -m.Change), Gained: Math.Max(0, m.Change)))
            .Concat(tally.Buckets.Select(b => (StartMs: b.Ms, b.EndMs, Change: b.Gained - b.Lost, b.Lost, b.Gained)))
            .OrderBy(x => x.StartMs)
            .ToList();
        var oneDay = members.Count > 0 && members.All(x => Local(x.StartMs).Date == Local(members[0].StartMs).Date);

        var rows = new List<CardRow>();
        if (scene.Series.Count > 1)
            rows.Add(new CardRow(scene.Series[tally.Series].Label, SeriesColors[tally.Series % SeriesColors.Length], string.Empty, string.Empty, AxisInk));
        foreach (var x in members.Take(MaxTallyRows))
        {
            var when = tally.Buckets.Count > 0 && x.EndMs > x.StartMs
                ? When(new PlotReading(0, 0, x.StartMs, x.EndMs, 0, 0, 0, 0, 0))
                : Local(x.StartMs).ToString(oneDay ? "HH:mm:ss" : "ddd d  HH:mm:ss", inv);
            rows.Add(ChangeRow(when, AxisInk, x.Change));
        }
        if (members.Count > MaxTallyRows)
            rows.Add(new CardRow((members.Count - MaxTallyRows).ToString(inv) + " more", AxisInk, string.Empty, string.Empty, AxisInk));
        if (tally.Kind == TallyKind.Mixed)
        {
            rows.Add(new CardRow("gained", AxisInk, string.Empty, "+" + tally.Gained.ToString("N0", CultureInfo.CurrentCulture), Gain));
            rows.Add(new CardRow("lost", AxisInk, string.Empty, "-" + tally.Lost.ToString("N0", CultureInfo.CurrentCulture), Loss));
        }

        var (title, ink) = tally.Kind switch
        {
            TallyKind.Loss => ("Drops ", Loss),
            TallyKind.Gain => ("Climbs ", Gain),
            _ => ("Net ", Mixed),
        };
        var subtitle = members.Count.ToString(inv) + " changes" + (oneDay && members.Count > 0
            ? "   " + Local(members[0].StartMs).ToString("ddd d MMM", inv)
            : string.Empty);
        DrawCard(canvas, plot, (float)tally.X, title + tally.Text, ink, boldTitle: true, subtitle, rows);
    }

    private const int MaxTallyRows = 10;

    private static DateTimeOffset Local(long ms)
        => TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(ms), TimeZoneInfo.Local);

    /// <summary>A crosshair on the snapped reading, a dot on every line at that instant, and a card:
    /// when, then each character's score and its change, plus a bucket's range and losses.</summary>
    private void DrawHover(SKCanvas canvas, ScoreGraphPlot plot, ScoreGraphScene scene, (int Series, PlotReading Reading) hover)
    {
        var (hs, hr) = hover;
        var x = Snap(hr.X);
        _stroke.Color = SKColors.White.WithAlpha(0x70);
        _stroke.StrokeWidth = Hairline;
        canvas.DrawLine(x, (float)plot.Top, x, (float)plot.Bottom, _stroke);

        var rows = new List<CardRow>();
        for (var s = 0; s < plot.Readings.Count; s++)
        {
            var held = s == hs ? hr : plot.HeldAt(s, hr.Ms);
            if (held is not { } r) continue;
            var colour = SeriesColors[s % SeriesColors.Length];
            // On the crosshair: a held score is the line's height there, wherever the reading was.
            _fill.Color = Panel;
            canvas.DrawCircle(x, (float)r.Y, 5f, _fill);
            _fill.Color = colour;
            canvas.DrawCircle(x, (float)r.Y, 3.5f, _fill);
            var change = s == hs && r.Change != 0
                ? (r.Change > 0 ? "+" : "-") + Math.Abs(r.Change).ToString("N0", CultureInfo.CurrentCulture)
                : string.Empty;
            rows.Add(new CardRow(scene.Series[s].Label, colour, r.Total.ToString("N0", CultureInfo.CurrentCulture), change, r.Change >= 0 ? Gain : Loss));
        }
        if (hr.Parts is { } parts)
            rows.Add(new CardRow(parts.Count.ToString(CultureInfo.InvariantCulture) + " lines", AxisInk, string.Empty, PartsText(parts), AxisInk));
        if (hr.EndMs > hr.Ms)
        {
            rows.Add(new CardRow("range", AxisInk, $"{hr.Low.ToString("N0", CultureInfo.CurrentCulture)} - {hr.High.ToString("N0", CultureInfo.CurrentCulture)}", string.Empty, AxisInk));
            // Each way on its own row: won 2,000 then lost 5,000 is not the same bucket as lost 3,000.
            if (hr.Gained > 0)
                rows.Add(new CardRow("gained", AxisInk, string.Empty, "+" + hr.Gained.ToString("N0", CultureInfo.CurrentCulture), Gain));
            if (hr.Lost > 0)
                rows.Add(new CardRow("lost", AxisInk, string.Empty, "-" + hr.Lost.ToString("N0", CultureInfo.CurrentCulture), Loss));
        }

        DrawCard(canvas, plot, x, When(hr), AxisInk, boldTitle: false, subtitle: null, rows);
    }

    private readonly record struct CardRow(string Left, SKColor LeftInk, string Right, string Change, SKColor ChangeInk);

    /// <summary>A floating card beside <paramref name="anchorX"/>: a title, an optional grey subtitle,
    /// then rows of a coloured label, a bold figure and a coloured change.</summary>
    private void DrawCard(SKCanvas canvas, ScoreGraphPlot plot, float anchorX, string title, SKColor titleInk,
        bool boldTitle, string? subtitle, List<CardRow> rows, bool preferLeft = false)
    {
        var x = anchorX;
        var titleFont = boldTitle ? _tipBold : _tipFont;
        const float Pad = 8, Line = 16, Gap = 14;
        var leftW = rows.Count == 0 ? 0 : rows.Max(r => _tipFont.MeasureText(r.Left));
        var rightW = rows.Count == 0 ? 0 : rows.Max(r => _tipBold.MeasureText(r.Right));
        var changeW = rows.Count == 0 ? 0 : rows.Max(r => r.Change.Length == 0 ? 0 : _tipFont.MeasureText(r.Change) + 8);
        var lines = rows.Count + 1 + (subtitle is null ? 0 : 1);
        var width = Math.Max(Math.Max(titleFont.MeasureText(title), subtitle is null ? 0 : _tipFont.MeasureText(subtitle)),
            leftW + Gap + rightW + changeW) + Pad * 2;
        var height = Pad * 2 + Line * lines - 2;

        // Right of the crosshair unless that runs off the plot; vertically, clear of the top.
        var canvasRight = (float)(plot.Right + ScoreGraphPlot.RightMargin);
        var fitsRight = x + 12 + width <= canvasRight;
        var fitsLeft = x - 12 - width >= 0;
        var boxLeft = (preferLeft ? fitsLeft || !fitsRight : !fitsRight) ? x - 12 - width : x + 12;
        // Neither side has room: keep the card on the canvas, over whatever is there.
        boxLeft = Math.Clamp(boxLeft, 0, Math.Max(0, canvasRight - width));
        var box = new SKRect(boxLeft, (float)plot.Top + 4, boxLeft + width, (float)plot.Top + 4 + height);

        using (var shadow = SKImageFilter.CreateDropShadow(0, 2, 6, 6, SKColors.Black.WithAlpha(0x90)))
        {
            _fill.Color = TooltipFill;
            _fill.ImageFilter = shadow;
            canvas.DrawRoundRect(box, 6, 6, _fill);
            _fill.ImageFilter = null;
        }
        _stroke.Color = TooltipEdge;
        _stroke.StrokeWidth = Hairline;
        canvas.DrawRoundRect(box, 6, 6, _stroke);

        var baseline = box.Top + Pad + 10;
        _fill.Color = titleInk;
        canvas.DrawText(title, box.Left + Pad, baseline, SKTextAlign.Left, titleFont, _fill);
        if (subtitle is not null)
        {
            baseline += Line;
            _fill.Color = AxisInk;
            canvas.DrawText(subtitle, box.Left + Pad, baseline, SKTextAlign.Left, _tipFont, _fill);
        }
        foreach (var row in rows)
        {
            baseline += Line;
            _fill.Color = row.LeftInk;
            canvas.DrawText(row.Left, box.Left + Pad, baseline, SKTextAlign.Left, _tipFont, _fill);
            _fill.Color = SKColors.White.WithAlpha(0xee);
            canvas.DrawText(row.Right, box.Left + Pad + leftW + Gap + rightW, baseline, SKTextAlign.Right, _tipBold, _fill);
            if (row.Change.Length > 0)
            {
                _fill.Color = row.ChangeInk;
                canvas.DrawText(row.Change, box.Right - Pad, baseline, SKTextAlign.Right, _tipFont, _fill);
            }
        }
    }

    private static string When(PlotReading r)
    {
        var start = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(r.Ms), TimeZoneInfo.Local);
        if (r.EndMs <= r.Ms)
            return start.ToString("ddd d MMM  HH:mm:ss", CultureInfo.InvariantCulture);
        var end = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(r.EndMs), TimeZoneInfo.Local);
        var span = r.EndMs - r.Ms;
        return span >= (long)TimeSpan.FromDays(1).TotalMilliseconds
            ? start.ToString("ddd d MMM", CultureInfo.InvariantCulture)
            : start.ToString("ddd d MMM  HH:mm", CultureInfo.InvariantCulture) + " - " + end.ToString("HH:mm", CultureInfo.InvariantCulture);
    }
}
