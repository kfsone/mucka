using System.Globalization;
using Mucka.Combat;
using SkiaSharp;

namespace Mucka.Rendering;

/// <summary>What the pointer is on: a reading of series <see cref="Series"/>, a strip mark, or a climb
/// or drop.</summary>
public readonly record struct ScoreHover(int Series, PlotReading? Reading, PlotMark? Mark, PlotMove? Move = null, int Stair = 0);

/// <summary>
/// Paints a <see cref="ScoreGraphPlot"/> for <see cref="ScoreGraphView"/>: every coordinate comes from
/// the plot, so the arithmetic is tested in Mucka.Util.Tests and this class chooses ink, type and
/// label placement. SkiaSharp only - no MAUI - so it can also render to an image.
///
/// <para>Drawing order, back to front: a hovered mark's span wash; guides (squeezed gaps, grid, day
/// lines, axis, run and switch marks, session bands); data (bucket bars, area fill, line, drops and
/// their markers); wipe skulls; labels (the "as of" stamp, live-value tags, drop pills); then the
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
    public void Paint(SKCanvas canvas, float scale, ScoreGraphPlot plot, ScoreGraphScene scene, ScoreHover? hover)
    {
        canvas.Save();
        _scale = scale;
        canvas.Scale(scale);

        if (hover?.Mark is { } mark)
            DrawMarkSpan(canvas, plot, mark);
        DrawGuides(canvas, plot);
        DrawData(canvas, plot);
        DrawDeaths(canvas, plot);
        var taken = new List<SKRect>();
        DrawAsOf(canvas, plot, scene, taken);
        DrawLiveTags(canvas, plot, taken);
        DrawLossPills(canvas, plot, taken);
        if (hover?.Reading is { } reading)
            DrawHover(canvas, plot, scene, (hover.Value.Series, reading));
        else if (hover?.Mark is { } m)
            DrawMarkCard(canvas, plot, scene, m);
        else if (hover?.Move is { } move)
            DrawMoveCard(canvas, plot, scene, move, hover.Value.Stair);

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
        DrawCard(canvas, plot, bx, title, ink, boldTitle: true, subtitle, rows);
    }

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
            var mid = (float)(bar.X0 + bar.X1) / 2;
            var w = Math.Clamp((float)(bar.X1 - bar.X0) * 0.6f, 2f, 14f);
            var h = Math.Max(1.5f, (float)(bar.YLow - bar.YHigh));
            var rect = new SKRect(mid - w / 2, (float)bar.YHigh, mid + w / 2, (float)bar.YHigh + h);
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
            float x = (float)loss.X, y = (float)loss.YAfter + 1.5f, r = (float)loss.Radius;
            using var tri = new SKPath();
            tri.MoveTo(x - r, y);
            tri.LineTo(x + r, y);
            tri.LineTo(x, y + r * 1.3f);
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

    /// <summary>The largest drops' sizes in pills, largest placed first, each at the first spot near
    /// its marker that stays inside the plot and clear of every pill already placed.</summary>
    private void DrawLossPills(SKCanvas canvas, ScoreGraphPlot plot, List<SKRect> taken)
    {
        var bounds = new SKRect((float)plot.Left, (float)plot.Top, (float)plot.Right, (float)plot.Bottom);
        const float Height = 15f;
        foreach (var loss in plot.Losses.Where(l => l.Labelled).OrderByDescending(l => l.Drop))
        {
            var text = "-" + loss.Drop.ToString("N0", CultureInfo.CurrentCulture);
            var w = _pillFont.MeasureText(text) + 10;
            float x = (float)loss.X, foot = (float)loss.YAfter + 1.5f + (float)loss.Radius * 1.3f, head = (float)loss.YBefore;
            SKRect[] candidates =
            [
                Rect(x - w / 2, foot + 3),
                Rect(x + (float)loss.Radius + 4, (float)loss.YAfter - Height / 2),
                Rect(x - (float)loss.Radius - 4 - w, (float)loss.YAfter - Height / 2),
                Rect(x - w / 2, head - Height - 4),
                Rect(x + 6, head - Height / 2),
                Rect(x - 6 - w, head - Height / 2),
            ];
            SKRect Rect(float l, float t) => new(l, t, l + w, t + Height);

            foreach (var rect in candidates)
            {
                if (!bounds.Contains(rect) || taken.Any(t => t.IntersectsWith(rect)))
                    continue;
                taken.Add(rect);
                _fill.Color = LossPill;
                canvas.DrawRoundRect(rect, 4, 4, _fill);
                _stroke.Color = Loss.WithAlpha(0xa0);
                _stroke.StrokeWidth = Hairline;
                canvas.DrawRoundRect(rect, 4, 4, _stroke);
                _fill.Color = LossInk;
                canvas.DrawText(text, rect.MidX, rect.MidY + 3.5f, SKTextAlign.Center, _pillFont, _fill);
                break;
            }
        }
    }

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
        bool boldTitle, string? subtitle, List<CardRow> rows)
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
        var boxLeft = x + 12 + width <= plot.Right + ScoreGraphPlot.RightMargin ? x + 12 : x - 12 - width;
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
